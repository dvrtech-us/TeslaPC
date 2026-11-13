//! Capture-backend order, the black-frame handoff, and what to do when a backend has no new frame.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureBackend {
    Wgc,
    Dxgi,
    Gdi,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackendStep {
    /// Stay on this backend.
    Keep(CaptureBackend),
    /// A black or failed frame: try the next backend.
    Switch(CaptureBackend),
}

/// `TESLAPC_NO_WGC=1` skips Windows Graphics Capture and starts at DXGI.
pub fn first_capture_backend(no_wgc: bool) -> CaptureBackend {
    if no_wgc {
        CaptureBackend::Dxgi
    } else {
        CaptureBackend::Wgc
    }
}

pub fn next_capture_backend(current: CaptureBackend) -> Option<CaptureBackend> {
    match current {
        CaptureBackend::Wgc => Some(CaptureBackend::Dxgi),
        CaptureBackend::Dxgi => Some(CaptureBackend::Gdi),
        CaptureBackend::Gdi => None,
    }
}

/// A frame is black when at least 98% of sampled pixels have B, G, and R below 8.
/// An empty buffer is treated as black so the caller tries the next backend.
pub fn frame_is_black(bgra: &[u8]) -> bool {
    let pixels = bgra.len() / 4;
    if pixels == 0 {
        return true;
    }
    let samples = pixels.min(4096);
    let step = (pixels / samples).max(1);
    let mut black = 0usize;
    let mut seen = 0usize;
    let mut i = 0usize;
    while i < pixels && seen < samples {
        let o = i * 4;
        let b = bgra[o];
        let g = bgra[o + 1];
        let r = bgra[o + 2];
        if r < 8 && g < 8 && b < 8 {
            black += 1;
        }
        seen += 1;
        i += step;
    }
    seen > 0 && black * 100 >= seen * 98
}

/// Failed capture or a black frame advances WGC → DXGI → GDI.
/// After GDI has also failed or come back black, stay there (every backend was tried).
pub fn backend_after_frame(current: CaptureBackend, capture_ok: bool, bgra: &[u8]) -> BackendStep {
    let bad = !capture_ok || frame_is_black(bgra);
    if !bad {
        return BackendStep::Keep(current);
    }
    match next_capture_backend(current) {
        Some(next) => BackendStep::Switch(next),
        None => BackendStep::Keep(current),
    }
}

/// Right after the capture thread moves to another desktop, DXGI duplication often cannot be
/// created for a moment. A DXGI error in that window is retried this many times, this far
/// apart, before the normal step down to GDI.
pub const DXGI_SWITCH_RETRIES: u32 = 5;
pub const DXGI_SWITCH_RETRY_MS: u64 = 200;

/// Retry DXGI (stay on it) after an error, instead of stepping down. Only for DXGI, only while
/// it has produced no frame since a desktop switch (`after_switch`), and only
/// [`DXGI_SWITCH_RETRIES`] times. Every other error steps down as [`backend_after_frame`] says.
pub fn retry_after_switch(backend: CaptureBackend, after_switch: bool, retries_used: u32) -> bool {
    backend == CaptureBackend::Dxgi && after_switch && retries_used < DXGI_SWITCH_RETRIES
}

pub fn session_should_restart(epoch_at_start: u64, epoch_now: u64) -> bool {
    epoch_at_start != epoch_now
}

/// A backend that has never produced a frame since it was (re)selected and keeps reporting
/// "no new frame" for this long is treated as failed so the loop moves on.
pub const NO_FRAME_FAIL_MS: u64 = 2000;

/// While the screen is still, the last JPEG is published again at most this often so MJPEG
/// clients keep receiving parts.
pub const REPUBLISH_INTERVAL_MS: u64 = 1000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoFrameAction {
    /// Stay on this backend and retry on the next tick.
    Wait,
    /// Publish the stored last JPEG again (no re-encode).
    Republish,
    /// The backend never produced a frame: treat it as failed.
    Fail,
}

/// What the capture loop does when the backend reports "no new frame".
/// `produced_since_selected`: this backend has delivered a real frame since it was selected.
/// `ms_since_selected`: time since the backend was (re)selected.
/// `ms_since_publish`: time since the last JPEG (new or repeated) was published.
pub fn no_frame_action(produced_since_selected: bool, have_last_jpeg: bool, ms_since_selected: u64, ms_since_publish: u64) -> NoFrameAction {
    if produced_since_selected {
        if have_last_jpeg && ms_since_publish >= REPUBLISH_INTERVAL_MS {
            NoFrameAction::Republish
        } else {
            NoFrameAction::Wait
        }
    } else if ms_since_selected >= NO_FRAME_FAIL_MS {
        NoFrameAction::Fail
    } else {
        NoFrameAction::Wait
    }
}
