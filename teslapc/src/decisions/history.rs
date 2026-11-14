//! Watched flag. Sticky once playback reaches 95% of duration.

pub const WATCHED_FRACTION: f64 = 0.95;

/// `already` stays set. A new watch is raised when `duration > 0` and `position / duration >= 0.95`.
pub fn watched_now(position: f64, duration: f64, already: bool) -> bool {
    if already {
        return true;
    }
    duration > 0.0 && position / duration >= WATCHED_FRACTION
}

pub fn percent_watched(position: f64, duration: f64) -> i32 {
    if duration <= 0.0 {
        return 0;
    }
    let ratio = (position / duration).clamp(0.0, 1.0);
    (ratio * 100.0).round() as i32
}
