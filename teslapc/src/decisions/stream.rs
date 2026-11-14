//! Stream cap, uniform scale, and MJPEG framing.

pub const JPEG_QUALITY: u8 = 60;
pub const STREAM_FPS: u32 = 30;
/// `1000 / 30`.
pub const FRAME_INTERVAL_MS: u64 = 33;
pub const DEFAULT_STREAM_HEIGHT: i32 = 1080;
pub const MIN_STREAM_HEIGHT: i32 = 240;
pub const MAX_STREAM_HEIGHT: i32 = 2160;
/// Slow clients wait this long for a newer frame, then take the latest one.
pub const SLOW_CLIENT_WAIT_MS: u64 = 1000;
pub const MJPEG_BOUNDARY: &str = "boundary";

pub fn clamp_stream_height(height: i32) -> i32 {
    if (MIN_STREAM_HEIGHT..=MAX_STREAM_HEIGHT).contains(&height) {
        height
    } else {
        DEFAULT_STREAM_HEIGHT
    }
}

pub fn width_cap(height: i32) -> i32 {
    height.saturating_mul(4)
}

/// Uniform scale into `height * 4` by `height`. Never upscales. Rounds to at least 1px.
pub fn fit_frame(screen_w: i32, screen_h: i32, max_height: i32) -> (i32, i32) {
    if screen_w <= 0 || screen_h <= 0 {
        return (1, 1);
    }
    let max_h = max_height.max(1);
    let max_w = width_cap(max_h).max(1);
    let scale = 1.0_f64
        .min((max_w as f64) / (screen_w as f64))
        .min((max_h as f64) / (screen_h as f64));
    let out_w = ((screen_w as f64) * scale).round().max(1.0) as i32;
    let out_h = ((screen_h as f64) * scale).round().max(1.0) as i32;
    (out_w, out_h)
}

pub fn mjpeg_content_type() -> &'static str {
    "multipart/x-mixed-replace; boundary=boundary"
}

/// One MJPEG body part: boundary, `Content-Type: image/jpeg`, length, JPEG bytes, CRLF.
pub fn mjpeg_part(jpeg: &[u8]) -> Vec<u8> {
    let header = format!(
        "\r\n--{MJPEG_BOUNDARY}\r\nContent-Type: image/jpeg\r\nContent-Length: {}\r\n\r\n",
        jpeg.len()
    );
    let mut out = header.into_bytes();
    out.extend_from_slice(jpeg);
    out.extend_from_slice(b"\r\n");
    out
}
