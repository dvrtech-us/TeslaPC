//! Process-wide server state. Routes and OS threads share this.

use std::collections::VecDeque;
use std::path::PathBuf;
use std::process::Child;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU32, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use bytes::Bytes;
use tokio::sync::{mpsc, Notify, watch};

use crate::decisions::audio::{cover_silence, format_message, is_silent, keepalive_noise, sample_format_name};
use crate::decisions::config::{input_mode_from, parse_log_level, KEY_INPUT_MODE, KEY_LOG_LEVEL, KEY_STREAM_HEIGHT, KEY_VIDEO_ROOT};
use crate::decisions::stream::{clamp_stream_height, DEFAULT_STREAM_HEIGHT};
use crate::paths;
use crate::store::Library;

#[derive(Clone, Debug)]
pub struct SharedFrame {
    pub seq: u64,
    pub jpeg: Vec<u8>,
}

#[derive(Clone, Debug)]
pub struct AudioFormat {
    pub sample_rate: u32,
    pub bits_per_sample: u16,
    pub channels: u16,
    pub sample_format: String,
}

impl Default for AudioFormat {
    fn default() -> Self {
        Self {
            sample_rate: 48000,
            bits_per_sample: 32,
            channels: 2,
            sample_format: "float".into(),
        }
    }
}

impl AudioFormat {
    pub fn announce(&self) -> String {
        format_message(self.sample_rate, self.bits_per_sample, self.channels, &self.sample_format)
    }

    pub fn from_wave(rate: u32, bits: u16, channels: u16, is_float: bool) -> Self {
        Self {
            sample_rate: rate,
            bits_per_sample: bits,
            channels,
            sample_format: sample_format_name(bits, is_float),
        }
    }
}

pub struct PcmHub {
    pub queue: Mutex<VecDeque<Bytes>>,
    pub notify: Notify,
    pub clients: Mutex<Vec<mpsc::UnboundedSender<Bytes>>>,
    pub client_count: AtomicUsize,
    pub media_mode: AtomicBool,
    pub capturing: AtomicBool,
    pub typical_bytes: AtomicUsize,
    /// Last captured packet of any content.
    pub last_real: Mutex<Instant>,
    /// Last captured packet that held sound.
    pub last_sound: Mutex<Instant>,
    /// Sample width and float flag of the capture format, for keepalive noise.
    pub sample_kind: Mutex<(u16, bool)>,
    pub noise_seed: AtomicU32,
}

impl PcmHub {
    pub fn new() -> Self {
        Self {
            queue: Mutex::new(VecDeque::new()),
            notify: Notify::new(),
            clients: Mutex::new(Vec::new()),
            client_count: AtomicUsize::new(0),
            media_mode: AtomicBool::new(false),
            capturing: AtomicBool::new(false),
            typical_bytes: AtomicUsize::new(0),
            last_real: Mutex::new(Instant::now()),
            last_sound: Mutex::new(Instant::now()),
            sample_kind: Mutex::new((32, true)),
            noise_seed: AtomicU32::new(0),
        }
    }

    pub fn queue_empty(&self) -> bool {
        self.queue.lock().map(|q| q.is_empty()).unwrap_or(true)
    }

    pub fn set_sample_kind(&self, bits_per_sample: u16, is_float: bool) {
        if let Ok(mut kind) = self.sample_kind.lock() {
            *kind = (bits_per_sample, is_float);
        }
    }

    /// `nbytes` of keepalive noise in the capture sample format.
    pub fn noise(&self, nbytes: usize) -> Bytes {
        let (bits, is_float) = self.sample_kind.lock().map(|k| *k).unwrap_or((32, true));
        let mut seed = self.noise_seed.load(Ordering::Relaxed);
        let out = keepalive_noise(nbytes, bits, is_float, &mut seed);
        self.noise_seed.store(seed, Ordering::Relaxed);
        Bytes::from(out)
    }

    /// `real` is a captured packet. A silent captured packet is replaced with keepalive noise
    /// until the silence has lasted `SILENCE_MAX_MS`, then it is sent as captured.
    pub fn push(&self, bytes: Bytes, real: bool) {
        if bytes.is_empty() {
            return;
        }
        let mut bytes = bytes;
        if real {
            let now = Instant::now();
            if let Ok(mut last) = self.last_real.lock() {
                *last = now;
            }
            self.typical_bytes.store(bytes.len(), Ordering::Relaxed);
            if !is_silent(&bytes) {
                if let Ok(mut last) = self.last_sound.lock() {
                    *last = now;
                }
            } else if cover_silence(self.silent_ms()) {
                bytes = self.noise(bytes.len());
            }
        }
        if self.client_count.load(Ordering::Relaxed) == 0 {
            return;
        }
        if let Ok(mut q) = self.queue.lock() {
            q.push_back(bytes);
            while q.len() > 32 {
                q.pop_front();
            }
        }
        self.notify.notify_one();
    }

    pub fn silent_ms(&self) -> u64 {
        self.last_sound
            .lock()
            .map(|t| t.elapsed().as_millis() as u64)
            .unwrap_or(0)
    }

    pub fn idle_ms(&self) -> u64 {
        self.last_real
            .lock()
            .map(|t| t.elapsed().as_millis() as u64)
            .unwrap_or(0)
    }
}

pub struct Playback {
    pub ffmpeg: Option<PathBuf>,
    pub ffprobe: Option<PathBuf>,
    pub playing: bool,
    pub paused: bool,
    pub file: Option<String>,
    pub position: f64,
    pub duration: f64,
    pub started: Option<Instant>,
    pub generation: u64,
    pub video: Option<Child>,
    pub audio: Option<Child>,
}

impl Playback {
    pub fn new(ffmpeg: Option<PathBuf>, ffprobe: Option<PathBuf>) -> Self {
        Self {
            ffmpeg,
            ffprobe,
            playing: false,
            paused: false,
            file: None,
            position: 0.0,
            duration: 0.0,
            started: None,
            generation: 0,
            video: None,
            audio: None,
        }
    }
}

pub struct Inner {
    pub localhost_only: bool,
    pub no_bypass: bool,
    pub https_host: Mutex<String>,
    pub acme_email: Mutex<String>,
    pub video_root: Mutex<String>,
    pub log_level: Mutex<String>,
    pub input_mode: Mutex<String>,
    pub stream_height: AtomicI32,
    pub input_enabled: AtomicBool,
    pub manual_hotspot_off: AtomicBool,
    pub stream_clients: AtomicUsize,
    pub capture_epoch: AtomicU64,
    pub media_mode: AtomicBool,
    pub frames: watch::Sender<SharedFrame>,
    pub pcm: PcmHub,
    pub audio_format: Mutex<AudioFormat>,
    pub library: Mutex<Option<Library>>,
    pub playback: Mutex<Playback>,
    pub touch_down: Mutex<bool>,
}

#[derive(Clone)]
pub struct App {
    pub inner: Arc<Inner>,
}

impl App {
    pub fn new(localhost_only: bool, no_bypass: bool, playback: Playback) -> Self {
        let (frames, _) = watch::channel(SharedFrame { seq: 0, jpeg: Vec::new() });
        let height = std::env::var(KEY_STREAM_HEIGHT)
            .ok()
            .and_then(|v| v.parse().ok())
            .map(clamp_stream_height)
            .unwrap_or(DEFAULT_STREAM_HEIGHT);
        let video = std::env::var(KEY_VIDEO_ROOT)
            .ok()
            .filter(|v| !v.trim().is_empty())
            .unwrap_or_else(paths::default_video_root);
        let level = std::env::var(KEY_LOG_LEVEL)
            .ok()
            .and_then(|v| parse_log_level(&v).map(|s| s.to_string()))
            .unwrap_or_else(|| "info".into());
        let mode = input_mode_from(std::env::var(KEY_INPUT_MODE).ok().as_deref()).to_string();
        let library = match Library::open() {
            Ok(lib) => Some(lib),
            Err(err) => {
                println!("[Library] SQLite unavailable: {err}");
                None
            }
        };
        Self {
            inner: Arc::new(Inner {
                localhost_only,
                no_bypass,
                https_host: Mutex::new(std::env::var("TESLAPC_HTTPS_HOST").unwrap_or_default()),
                acme_email: Mutex::new(std::env::var("TESLAPC_ACME_EMAIL").unwrap_or_default()),
                video_root: Mutex::new(video),
                log_level: Mutex::new(level),
                input_mode: Mutex::new(mode),
                stream_height: AtomicI32::new(height),
                input_enabled: AtomicBool::new(true),
                manual_hotspot_off: AtomicBool::new(false),
                stream_clients: AtomicUsize::new(0),
                capture_epoch: AtomicU64::new(0),
                media_mode: AtomicBool::new(false),
                frames,
                pcm: PcmHub::new(),
                audio_format: Mutex::new(AudioFormat::default()),
                library: Mutex::new(library),
                playback: Mutex::new(playback),
                touch_down: Mutex::new(false),
            }),
        }
    }

    pub fn log(&self, level: &str, message: &str) {
        let current = self.inner.log_level.lock().map(|s| s.clone()).unwrap_or_else(|_| "info".into());
        if rank(level) <= rank(&current) {
            let prefix = match level {
                "error" => "[ERROR] ",
                "warn" => "[WARN] ",
                "debug" => "[DEBUG] ",
                _ => "",
            };
            println!("{prefix}{message}");
        }
    }

    pub fn publish_jpeg(&self, jpeg: Vec<u8>) {
        if jpeg.is_empty() {
            return;
        }
        let seq = self.inner.frames.borrow().seq.wrapping_add(1).max(1);
        let _ = self.inner.frames.send(SharedFrame { seq, jpeg });
    }

    pub fn bump_capture(&self) {
        self.inner.capture_epoch.fetch_add(1, Ordering::Relaxed);
    }

    pub fn video_root(&self) -> String {
        self.inner.video_root.lock().map(|s| s.clone()).unwrap_or_else(|_| paths::default_video_root())
    }

    pub fn cf_token_set(&self) -> bool {
        std::env::var("TESLAPC_CF_TOKEN").ok().is_some_and(|v| !v.trim().is_empty())
    }
}

fn rank(level: &str) -> i32 {
    match level {
        "error" => 0,
        "warn" => 1,
        "info" => 2,
        "debug" => 3,
        _ => 2,
    }
}
