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

/// Mouse and keyboard injection is available. Windows `SendInput` needs no probe.
/// On Linux this opens uinput, and a failure means no input at all.
pub fn probe_input() -> bool {
    #[cfg(windows)]
    {
        return true;
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

/// Touch injection is available. Only called after `probe_input` succeeded. Windows runs
/// `InitializeTouchInjection`. Linux touch uses the same uinput device, so it is available
/// whenever input is.
pub fn probe_touch() -> bool {
    #[cfg(windows)]
    {
        return crate::win::probe_touch();
    }
    #[cfg(not(windows))]
    {
        true
    }
}

/// Make the process DPI aware before anything reads screen metrics. No-op on Linux.
pub fn init_dpi_awareness() {
    #[cfg(windows)]
    {
        crate::win::init_dpi_awareness();
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
        return crate::win_service::service_install();
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

pub fn service_uninstall() -> i32 {
    #[cfg(windows)]
    {
        return crate::win_service::service_uninstall();
    }
    #[cfg(not(windows))]
    {
        println!("[Service] service uninstall is a Windows command. On Linux run: systemctl disable --now teslapc.service && rm /etc/systemd/system/teslapc.service");
        1
    }
}

/// Windows: hand the process to the Service Control Manager as the supervisor. Returns `true`
/// when the service ran and has stopped; `false` when the SCM did not start this process.
/// Other OSes: always `false`.
pub fn run_as_service() -> bool {
    #[cfg(windows)]
    {
        return crate::win_service::run_as_service();
    }
    #[cfg(not(windows))]
    {
        false
    }
}

/// `--session-host`: send stdout and stderr to the host log file. No-op off Windows.
pub fn redirect_host_log() {
    #[cfg(windows)]
    {
        crate::win_service::redirect_host_log();
    }
}

/// `--session-host --supervisor-pid <pid>`: exit this process when the supervisor exits.
/// No-op off Windows.
pub fn exit_with_supervisor(pid: u32) {
    #[cfg(windows)]
    {
        crate::win_service::exit_with_supervisor(pid);
    }
    #[cfg(not(windows))]
    {
        let _ = pid;
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
