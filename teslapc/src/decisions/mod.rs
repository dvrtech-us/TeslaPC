//! Pure protocol decisions. OS calls sit behind these so tests do not need
//! admin, a display, or a car.

pub mod audio;
pub mod bypass;
pub mod capture;
pub mod config;
pub mod display;
pub mod history;
pub mod input;
pub mod linux_input;
pub mod portal;
pub mod listing;
pub mod media;
pub mod renewal;
pub mod socket;
pub mod stream;

pub use audio::{cover_silence, format_message, is_silent, keepalive_bytes, keepalive_noise, should_inject_silence, KeepaliveInput, KEEPALIVE_NOISE_AMPLITUDE, KEEPALIVE_POLL_MS, SILENCE_MAX_MS, SILENCE_START_MS};
pub use bypass::linux_bypass_plan;
pub use capture::{backend_after_frame, first_capture_backend, frame_is_black, BackendStep, CaptureBackend};
pub use config::apply_config_body;
pub use display::best_for_aspect;
pub use history::{watched_now, WATCHED_FRACTION};
pub use input::{expand_paste, map_key, scale_pointer, wheel_delta, TOUCH_REPEAT_MS};
pub use media::{ffmpeg_video_args, linux_missing_ffmpeg_error, windows_vlc_plan};
pub use renewal::{acme_directory_url, needs_renewal, RENEWAL_CHECK_HOURS, RENEW_WHEN_DAYS_LEFT};
pub use socket::{classify_socket, SocketRole};
pub use stream::{clamp_stream_height, fit_frame, mjpeg_content_type, mjpeg_part, JPEG_QUALITY};
