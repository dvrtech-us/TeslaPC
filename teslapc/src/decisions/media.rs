//! ffmpeg argument lines and the Windows VLC fallback. Linux does not launch another player.

pub const FFMPEG_FPS: u32 = 30;
pub const FFMPEG_QV: u32 = 6;
pub const VLC_WAIT_MS: u64 = 2000;
pub const VLC_EXE: &str = r"C:\Program Files\VideoLAN\VLC\vlc.exe";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VlcPlan {
    pub image_name: &'static str,
    pub wait_ms: u64,
    pub executable: &'static str,
    pub arguments: String,
}

pub fn windows_vlc_plan(file: &str) -> VlcPlan {
    VlcPlan {
        image_name: "vlc.exe",
        wait_ms: VLC_WAIT_MS,
        executable: VLC_EXE,
        arguments: format!(" -vvv \"{file}\" --fullscreen"),
    }
}

pub fn linux_missing_ffmpeg_error() -> &'static str {
    "ffmpeg is not installed. TeslaPC on Linux does not launch another player."
}

pub fn ffmpeg_video_arg_list(file: &str, start_seconds: f64, max_h: i32) -> Vec<String> {
    let max_h = if max_h > 0 { max_h } else { 1080 };
    vec![
        "-hide_banner".into(),
        "-loglevel".into(),
        "error".into(),
        "-re".into(),
        "-ss".into(),
        format_seconds(start_seconds),
        "-i".into(),
        file.into(),
        "-an".into(),
        "-vf".into(),
        format!("scale=-2:'min({max_h},ih)',fps={FFMPEG_FPS}"),
        "-c:v".into(),
        "mjpeg".into(),
        "-q:v".into(),
        FFMPEG_QV.to_string(),
        "-f".into(),
        "image2pipe".into(),
        "pipe:1".into(),
    ]
}

pub fn ffmpeg_video_args(file: &str, start_seconds: f64, max_h: i32) -> String {
    let mut parts = ffmpeg_video_arg_list(file, start_seconds, max_h);
    // Quote the input path the way the C# argument string does.
    if let Some(path) = parts.get_mut(7) {
        *path = format!("\"{path}\"");
    }
    parts.join(" ")
}

pub fn ffmpeg_audio_arg_list(file: &str, start_seconds: f64, sample_format: &str, rate: u32, channels: u16) -> Vec<String> {
    let fmt = match sample_format {
        "pcm16" => "s16le",
        "pcm24" => "s24le",
        "pcm32" => "s32le",
        _ => "f32le",
    };
    let rate = if rate == 0 { 48000 } else { rate };
    let channels = if channels == 0 { 2 } else { channels };
    vec![
        "-hide_banner".into(),
        "-loglevel".into(),
        "error".into(),
        "-re".into(),
        "-ss".into(),
        format_seconds(start_seconds),
        "-i".into(),
        file.into(),
        "-vn".into(),
        "-ar".into(),
        rate.to_string(),
        "-ac".into(),
        channels.to_string(),
        "-f".into(),
        fmt.into(),
        "pipe:1".into(),
    ]
}

pub fn ffmpeg_audio_args(file: &str, start_seconds: f64, sample_format: &str, rate: u32, channels: u16) -> String {
    ffmpeg_audio_arg_list(file, start_seconds, sample_format, rate, channels).join(" ")
}

fn format_seconds(seconds: f64) -> String {
    let seconds = if seconds.is_finite() && seconds > 0.0 { seconds } else { 0.0 };
    let text = format!("{seconds:.3}");
    text.trim_end_matches('0').trim_end_matches('.').to_string()
}

/// Pull complete JPEG frames (SOI..EOI) out of an ffmpeg image2pipe buffer.
/// Returns the frames and the number of leading bytes consumed.
pub fn take_jpegs(buf: &[u8]) -> (Vec<Vec<u8>>, usize) {
    let mut frames = Vec::new();
    let mut consumed = 0usize;
    let mut i = 0usize;
    while i + 1 < buf.len() {
        if buf[i] == 0xFF && buf[i + 1] == 0xD8 {
            if let Some(end) = find_eoi(&buf[i..]) {
                let stop = i + end;
                frames.push(buf[i..stop].to_vec());
                consumed = stop;
                i = stop;
                continue;
            }
            break;
        }
        i += 1;
    }
    if frames.is_empty() {
        // Drop leading junk only up to a possible SOI so a partial frame stays buffered.
        if let Some(soi) = find_soi(buf) {
            (frames, soi)
        } else if buf.len() > 1 && buf[buf.len() - 1] == 0xFF {
            (frames, buf.len() - 1)
        } else {
            (frames, buf.len())
        }
    } else {
        (frames, consumed)
    }
}

fn find_soi(buf: &[u8]) -> Option<usize> {
    buf.windows(2).position(|w| w == [0xFF, 0xD8])
}

fn find_eoi(buf: &[u8]) -> Option<usize> {
    let mut i = 2usize;
    while i + 1 < buf.len() {
        if buf[i] == 0xFF && buf[i + 1] == 0xD9 {
            return Some(i + 2);
        }
        i += 1;
    }
    None
}
