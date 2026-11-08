//! Audio format announcement and silence-keepalive decision.

use serde_json::json;

/// Inject silence only after the loopback has been idle this long.
pub const SILENCE_START_MS: u64 = 250;
/// Stop injecting after this much continuous idle time.
pub const SILENCE_MAX_MS: u64 = 30_000;
/// How often the keepalive task wakes.
pub const KEEPALIVE_POLL_MS: u64 = 20;

#[derive(Debug, Clone, Copy)]
pub struct KeepaliveInput {
    pub idle_ms: u64,
    pub queue_empty: bool,
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
        && input.queue_empty
        && input.idle_ms >= SILENCE_START_MS
        && input.idle_ms < SILENCE_MAX_MS
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
