//! Following the input desktop in `--session-host` mode: when a thread re-attaches, how often
//! it checks, which errors are worth a log line, and how a busy capture thread is restarted.

/// The name of the signed-in user's desktop on `winsta0`. Every other name (`Winlogon` for the
/// lock screen, sign-in screen and UAC prompts, `Screen-saver`, ...) is a secure desktop.
pub const DEFAULT_DESKTOP_NAME: &str = "Default";

/// A thread checks the input desktop at most this often, unless an operation just failed.
pub const DESKTOP_CHECK_INTERVAL_MS: u64 = 200;

/// After `SetThreadDesktop` failed, wait this long before trying again (a forced check does
/// not shorten it), so a thread that cannot move does not retry on every frame or event.
pub const DESKTOP_RETRY_AFTER_FAILURE_MS: u64 = 2000;

/// First wait before a restarted capture thread runs, after `SetThreadDesktop` returned
/// `ERROR_BUSY`. Doubles with each consecutive busy restart up to [`CAPTURE_RESTART_MAX_MS`].
pub const CAPTURE_RESTART_START_MS: u64 = 250;
pub const CAPTURE_RESTART_MAX_MS: u64 = 5000;

/// Re-attach when the input desktop's name differs from the attached one. Names are compared
/// without case, as Windows does for desktop names. An unknown attached name (the query
/// failed) always re-attaches. An empty input name is not a desktop: stay.
pub fn desktop_switch_needed(attached: Option<&str>, input: &str) -> bool {
    if input.is_empty() {
        return false;
    }
    match attached {
        Some(name) => !name.eq_ignore_ascii_case(input),
        None => true,
    }
}

/// Whether a thread should check the input desktop now.
/// `ms_since_last_check`: `None` when it has never checked (a fresh thread checks at once).
/// `force`: an injection or capture just failed in a way a desktop switch would explain.
/// `last_switch_failed`: the previous `SetThreadDesktop` failed; back off even when forced.
pub fn desktop_check_due(ms_since_last_check: Option<u64>, force: bool, last_switch_failed: bool) -> bool {
    let Some(elapsed) = ms_since_last_check else {
        return true;
    };
    if last_switch_failed {
        return elapsed >= DESKTOP_RETRY_AFTER_FAILURE_MS;
    }
    force || elapsed >= DESKTOP_CHECK_INTERVAL_MS
}

/// `true` for the user's desktop. An unknown name counts as the user's desktop, so input keeps
/// today's touch behavior when the name could not be read.
pub fn is_default_desktop(name: Option<&str>) -> bool {
    match name {
        Some(name) => name.eq_ignore_ascii_case(DEFAULT_DESKTOP_NAME),
        None => true,
    }
}

/// Log a desktop error only when it differs from the last one logged. The caller clears the
/// last error after a success, so the same failure is logged again if it comes back.
pub fn desktop_error_is_new(last_logged: Option<&str>, error: &str) -> bool {
    last_logged != Some(error)
}

/// Wait before a restarted capture thread starts, after `busy_restarts` consecutive
/// `ERROR_BUSY` restarts (`0` = not a restart).
pub fn capture_restart_delay_ms(busy_restarts: u32) -> u64 {
    if busy_restarts == 0 {
        return 0;
    }
    let shift = (busy_restarts - 1).min(16);
    CAPTURE_RESTART_START_MS.saturating_mul(1u64 << shift).min(CAPTURE_RESTART_MAX_MS)
}
