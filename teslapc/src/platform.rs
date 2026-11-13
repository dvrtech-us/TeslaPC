//! OS calls. Protocol decisions stay in `decisions`; this module only performs them.

use crate::decisions::capture::CaptureBackend;
use crate::state::App;

#[derive(Debug)]
pub struct Grab {
    pub bgra: Vec<u8>,
    pub width: i32,
    pub height: i32,
}

/// What one capture attempt produced. `Err` is a failed backend; `NoFrame` is a healthy session
/// with nothing new to show (a still screen under WGC or DXGI).
#[derive(Debug)]
pub enum GrabOutcome {
    Frame(Grab),
    NoFrame,
}

pub fn grab(backend: CaptureBackend) -> Result<GrabOutcome, String> {
    #[cfg(windows)]
    {
        return crate::win::grab(backend);
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::grab(backend);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = backend;
        Err("capture is not implemented for this OS".into())
    }
}

pub fn primary_size() -> (i32, i32) {
    #[cfg(windows)]
    {
        return crate::win::primary_size();
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::primary_size();
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        (1920, 1080)
    }
}

pub fn probe_input() -> bool {
    #[cfg(windows)]
    {
        return crate::win::probe_input();
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::probe_input();
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        false
    }
}

pub fn inject_pointer(kind: &str, x: i32, y: i32, touch: bool) {
    #[cfg(windows)]
    {
        crate::win::inject_pointer(kind, x, y, touch);
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::inject_pointer(kind, x, y, touch);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = (kind, x, y, touch);
    }
}

pub fn inject_wheel(delta: i32) {
    #[cfg(windows)]
    {
        crate::win::inject_wheel(delta);
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::inject_wheel(delta);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = delta;
    }
}

pub fn inject_key(token: &str) {
    #[cfg(windows)]
    {
        crate::win::inject_key(token);
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::inject_key(token);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = token;
    }
}

pub fn end_contact() {
    #[cfg(windows)]
    {
        crate::win::end_contact();
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::end_contact();
    }
}

pub fn repeat_touch() {
    #[cfg(windows)]
    {
        crate::win::repeat_touch();
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::repeat_touch();
    }
}

pub fn start_audio(app: App) {
    #[cfg(windows)]
    {
        crate::win::start_audio(app);
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::start_audio(app);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = app;
        println!("[Audio] no loopback on this OS");
    }
}

pub fn display_snapshot() -> (bool, i32, i32, Vec<(i32, i32)>) {
    #[cfg(windows)]
    {
        return crate::win::display_snapshot();
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::display_snapshot();
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        (false, 0, 0, Vec::new())
    }
}

pub fn try_set_mode(w: i32, h: i32) -> bool {
    #[cfg(windows)]
    {
        return crate::win::try_set_mode(w, h);
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::try_set_mode(w, h);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = (w, h);
        false
    }
}

pub fn open_firewall(http_port: u16, https_port: u16) {
    #[cfg(windows)]
    {
        crate::win::open_firewall(http_port, https_port);
    }
    #[cfg(not(windows))]
    {
        let _ = (http_port, https_port);
    }
}

pub fn service_install() -> i32 {
    #[cfg(windows)]
    {
        return crate::win::service_install();
    }
    #[cfg(target_os = "linux")]
    {
        return crate::linux::service_install();
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        println!("[Service] no service installer for this OS");
        1
    }
}

pub fn run_as_service(server: impl FnOnce() + Send + 'static) -> bool {
    #[cfg(windows)]
    {
        return crate::win::run_as_service(server);
    }
    #[cfg(not(windows))]
    {
        let _ = server;
        false
    }
}

pub fn hotspot_set(on: bool, app: &App) -> Result<String, String> {
    #[cfg(windows)]
    {
        return crate::win::hotspot_set(on, app);
    }
    #[cfg(not(windows))]
    {
        let _ = (on, app);
        Err("Mobile Hotspot is a Windows feature. On Linux set TESLAPC_BYPASS_IFACE; the access point itself is left to the operator.".into())
    }
}

pub fn start_bypass(app: App) {
    #[cfg(windows)]
    {
        crate::win::start_bypass(app);
    }
    #[cfg(target_os = "linux")]
    {
        crate::linux::start_bypass(app);
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = app;
    }
}
