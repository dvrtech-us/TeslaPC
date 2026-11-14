//! Audio format announcement, silence detection, and the keepalive-noise decision.

use serde_json::json;

/// Inject keepalive noise only after the loopback has delivered no packet for this long.
pub const SILENCE_START_MS: u64 = 250;
/// Silence shorter than this is covered with keepalive noise so the remote player keeps the
/// audio source. After this much continuous silence the noise stops.
pub const SILENCE_MAX_MS: u64 = 10_000;
/// Peak of the keepalive noise as a fraction of full scale (about -72 dBFS).
pub const KEEPALIVE_NOISE_AMPLITUDE: f32 = 1.0 / 4096.0;
/// How often the keepalive task wakes.
pub const KEEPALIVE_POLL_MS: u64 = 20;

#[derive(Debug, Clone, Copy)]
pub struct KeepaliveInput {
    /// Time since the last captured packet of any content.
    pub idle_ms: u64,
    /// Time since the last captured packet that held sound.
    pub silent_ms: u64,
    /// No captured packet arrived since the previous keepalive tick.
    pub no_packet_since_tick: bool,
    pub capturing: bool,
    pub media_mode: bool,
    pub has_clients: bool,
    pub typical_buffer_bytes: usize,
}

pub fn should_inject_silence(input: KeepaliveInput) -> bool {
    input.capturing
        && !input.media_mode
        && input.has_clients
        && input.typical_buffer_bytes > 0
        && input.no_packet_since_tick
        && input.idle_ms >= SILENCE_START_MS
        && cover_silence(input.silent_ms)
}

/// Longest stretch one keepalive buffer may cover, so a stalled task cannot queue a burst.
pub const KEEPALIVE_MAX_CHUNK_MS: u64 = 100;

/// Bytes of keepalive audio that cover `elapsed_ms` of real time, in whole frames.
pub fn keepalive_bytes(elapsed_ms: u64, sample_rate: u32, bits_per_sample: u16, channels: u16) -> usize {
    let frame = (bits_per_sample as usize / 8) * channels as usize;
    let frames = sample_rate as u64 * elapsed_ms.min(KEEPALIVE_MAX_CHUNK_MS) / 1000;
    frames as usize * frame
}

/// A packet is silent when every byte is zero. That holds for float and integer PCM.
pub fn is_silent(pcm: &[u8]) -> bool {
    pcm.iter().all(|b| *b == 0)
}

/// Silence is covered with keepalive noise until it has lasted `SILENCE_MAX_MS`.
pub fn cover_silence(silent_ms: u64) -> bool {
    silent_ms < SILENCE_MAX_MS
}

/// `nbytes` of low-level white noise in the capture sample format. `seed` carries the
/// generator state between calls. An unknown sample width yields zeros.
pub fn keepalive_noise(nbytes: usize, bits_per_sample: u16, is_float: bool, seed: &mut u32) -> Vec<u8> {
    let mut out = vec![0u8; nbytes];
    let width = match (bits_per_sample, is_float) {
        (32, true) | (32, false) => 4,
        (24, false) => 3,
        (16, false) => 2,
        _ => return out,
    };
    if *seed == 0 {
        *seed = 0x9E37_79B9;
    }
    for sample in out.chunks_exact_mut(width) {
        let mut x = *seed;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        *seed = x;
        let unit = ((x >> 8) as f32 / 8_388_608.0) - 1.0;
        let value = unit * KEEPALIVE_NOISE_AMPLITUDE;
        if is_float {
            sample.copy_from_slice(&value.to_le_bytes());
        } else {
            let scale = match width {
                2 => 32_768.0f64,
                3 => 8_388_608.0,
                _ => 2_147_483_648.0,
            };
            let int = (value as f64 * scale) as i32;
            sample.copy_from_slice(&int.to_le_bytes()[..width]);
        }
    }
    out
}

/// First wait after a failed loopback attempt.
pub const LOOPBACK_BACKOFF_START_MS: u64 = 1_000;
/// The wait doubles per consecutive failure up to this cap.
pub const LOOPBACK_BACKOFF_MAX_MS: u64 = 10_000;
/// A run longer than this counts as healthy and resets the backoff.
pub const LOOPBACK_HEALTHY_RUN_MS: u64 = 30_000;

/// Wait before the next loopback attempt. `previous_ms` is the last wait (0 before the
/// first failure) and `ran_ms` is how long the attempt that just ended ran.
/// The schedule is 1 s, 2 s, 4 s, 8 s, then 10 s; a run over 30 s starts it again at 1 s.
pub fn loopback_backoff_ms(previous_ms: u64, ran_ms: u64) -> u64 {
    if previous_ms == 0 || ran_ms > LOOPBACK_HEALTHY_RUN_MS {
        return LOOPBACK_BACKOFF_START_MS;
    }
    previous_ms.saturating_mul(2).min(LOOPBACK_BACKOFF_MAX_MS)
}

pub fn sample_format_name(bits_per_sample: u16, is_float: bool) -> String {
    if is_float {
        return "float".to_string();
    }
    match bits_per_sample {
        16 => "pcm16".to_string(),
        24 => "pcm24".to_string(),
        32 => "pcm32".to_string(),
        other => format!("pcm{other}"),
    }
}

/// First text frame on `/ws/audio`.
pub fn format_message(sample_rate: u32, bits_per_sample: u16, channels: u16, sample_format: &str) -> String {
    json!({
        "type": "format",
        "sampleRate": sample_rate,
        "bitsPerSample": bits_per_sample,
        "channels": channels,
        "sampleFormat": sample_format,
    })
    .to_string()
}

/// Length of one audio chunk pushed to clients.
pub const AUDIO_CHUNK_MS: u32 = 10;

/// Bytes in one `AUDIO_CHUNK_MS` chunk at `sample_rate`, in whole frames and never zero.
/// `frame_bytes` is bytes per sample times channels.
pub fn audio_chunk_bytes(sample_rate: u32, frame_bytes: usize) -> usize {
    let frame_bytes = frame_bytes.max(1);
    let frames = (sample_rate / (1000 / AUDIO_CHUNK_MS)) as usize;
    frames.max(1) * frame_bytes
}

/// Largest stats text frame the server reads from a client.
pub const CLIENT_STATS_MAX_BYTES: usize = 512;

/// Log line for a client `{"type":"stats",...}` text frame, or `None` for anything else
/// (wrong type, not JSON, or longer than `CLIENT_STATS_MAX_BYTES`). Missing or non-numeric
/// fields print as `?`.
pub fn client_stats_line(text: &str) -> Option<String> {
    if text.len() > CLIENT_STATS_MAX_BYTES {
        return None;
    }
    let value: serde_json::Value = serde_json::from_str(text).ok()?;
    if value.get("type")?.as_str()? != "stats" {
        return None;
    }
    let field = |name: &str| match value.get(name) {
        Some(v) if v.is_number() => v.to_string(),
        _ => "?".to_string(),
    };
    Some(format!(
        "[Audio] client buffered={}ms min={}ms underruns={} trims={} rate={}",
        field("bufferedMs"),
        field("minMs"),
        field("underruns"),
        field("trims"),
        field("contextRate"),
    ))
}
