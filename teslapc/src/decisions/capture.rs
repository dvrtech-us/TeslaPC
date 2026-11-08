//! Capture-backend order and the black-frame handoff.

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

pub fn session_should_restart(epoch_at_start: u64, epoch_now: u64) -> bool {
    epoch_at_start != epoch_now
}
