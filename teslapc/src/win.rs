//! Windows capture, input, display, firewall, and hotspot. The service lives in `win_service.rs`.

use std::process::Command;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Mutex, OnceLock};
use std::time::Duration;

use windows::Win32::Foundation::{POINT, RECT};
use windows::Win32::Graphics::Gdi::{
    BitBlt, ChangeDisplaySettingsW, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, EnumDisplaySettingsW, GetDC, GetDIBits, ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, CDS_UPDATEREGISTRY, DEVMODEW, DIB_RGB_COLORS, DISP_CHANGE_SUCCESSFUL, DM_BITSPERPEL, DM_PELSHEIGHT, DM_PELSWIDTH, ENUM_CURRENT_SETTINGS, SRCCOPY,
};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, INPUT_MOUSE, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_WHEEL, MOUSEINPUT, MOUSE_EVENT_FLAGS, VIRTUAL_KEY, VK_BACK, VK_DELETE, VK_DOWN, VK_END, VK_ESCAPE, VK_F1, VK_F10, VK_F11, VK_F12, VK_F2, VK_F3, VK_F4, VK_F5, VK_F6, VK_F7, VK_F8, VK_F9, VK_HOME, VK_INSERT, VK_LEFT, VK_NEXT, VK_PRIOR, VK_RETURN, VK_RIGHT, VK_TAB, VK_UP,
};
use windows::Win32::UI::Input::Pointer::{InitializeTouchInjection, InjectTouchInput, POINTER_INFO, POINTER_TOUCH_INFO, POINTER_FLAG_DOWN, POINTER_FLAG_INCONTACT, POINTER_FLAG_INRANGE, POINTER_FLAG_UP, POINTER_FLAG_UPDATE, TOUCH_FEEDBACK_DEFAULT};
use windows::Win32::UI::HiDpi::{SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2};
use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SetCursorPos, SetProcessDPIAware, PT_TOUCH, SM_CXSCREEN, SM_CYSCREEN};

use crate::decisions::bypass::{windows_portproxy_args, BYPASS_IP, HOTSPOT_WATCHDOG_MS};
use crate::decisions::capture::CaptureBackend;
use crate::decisions::input::{pointer_path_on_desktop, touch_repeat_applies, PointerPath};
use crate::platform::{DesktopCheck, Grab, GrabOutcome};
use crate::state::App;

pub fn grab(backend: CaptureBackend) -> Result<GrabOutcome, String> {
    match backend {
        CaptureBackend::Wgc => grab_wgc(),
        CaptureBackend::Dxgi => grab_dxgi(),
        CaptureBackend::Gdi => grab_gdi().map(GrabOutcome::Frame),
    }
}

fn grab_wgc() -> Result<GrabOutcome, String> {
    if std::env::var("TESLAPC_NO_WGC").ok().as_deref() == Some("1") {
        return Err("TESLAPC_NO_WGC=1".into());
    }
    crate::win_capture::grab_wgc()
}

fn grab_dxgi() -> Result<GrabOutcome, String> {
    crate::win_capture::grab_dxgi()
}

fn grab_gdi() -> Result<Grab, String> {
    unsafe {
        let _ = SetProcessDPIAware();
        let width = GetSystemMetrics(SM_CXSCREEN);
        let height = GetSystemMetrics(SM_CYSCREEN);
        if width <= 0 || height <= 0 {
            return Err("primary screen size is zero".into());
        }
        // A fresh screen DC on every grab: `GetDC(None)` is the screen of the desktop this
        // thread is attached to, so it follows `SetThreadDesktop`. Nothing is cached.
        let screen = GetDC(None);
        if screen.is_invalid() {
            return Err("GetDC failed".into());
        }
        let memory = CreateCompatibleDC(Some(screen));
        if memory.is_invalid() {
            let _ = ReleaseDC(None, screen);
            return Err("CreateCompatibleDC failed".into());
        }
        let bitmap = CreateCompatibleBitmap(screen, width, height);
        if bitmap.is_invalid() {
            let _ = DeleteDC(memory);
            let _ = ReleaseDC(None, screen);
            return Err("CreateCompatibleBitmap failed".into());
        }
        let old = SelectObject(memory, bitmap.into());
        if BitBlt(memory, 0, 0, width, height, Some(screen), 0, 0, SRCCOPY).is_err() {
            SelectObject(memory, old);
            let _ = DeleteObject(bitmap.into());
            let _ = DeleteDC(memory);
            let _ = ReleaseDC(None, screen);
            return Err("BitBlt failed".into());
        }
        let mut info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                biSizeImage: 0,
                biXPelsPerMeter: 0,
                biYPelsPerMeter: 0,
                biClrUsed: 0,
                biClrImportant: 0,
            },
            bmiColors: [windows::Win32::Graphics::Gdi::RGBQUAD::default()],
        };
        let mut pixels = vec![0u8; (width as usize) * (height as usize) * 4];
        let rows = GetDIBits(memory, bitmap, 0, height as u32, Some(pixels.as_mut_ptr() as *mut _), &mut info, DIB_RGB_COLORS);
        SelectObject(memory, old);
        let _ = DeleteObject(bitmap.into());
        let _ = DeleteDC(memory);
        let _ = ReleaseDC(None, screen);
        if rows == 0 {
            return Err("GetDIBits failed".into());
        }
        Ok(Grab { bgra: pixels, width, height })
    }
}

pub fn primary_size() -> (i32, i32) {
    unsafe { (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN)) }
}

static TOUCH_READY: Mutex<bool> = Mutex::new(false);
static TOUCH_XY: Mutex<(i32, i32, bool)> = Mutex::new((0, 0, false));

/// Per-monitor v2 awareness, so `GetSystemMetrics` and `InjectTouchInput` both use physical
/// pixels. Falls back to system awareness when the context call is refused.
pub fn init_dpi_awareness() {
    unsafe {
        if SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2).is_err() {
            let _ = SetProcessDPIAware();
        }
    }
}

/// One injection queued to the session-host `Input` thread. Queue order is event order.
enum InputOp {
    Pointer { kind: String, x: i32, y: i32, touch: bool },
    Wheel(i32),
    Key(String),
    EndContact,
    RepeatTouch,
    ProbeTouch(mpsc::Sender<bool>),
}

/// Set once by `start_input_thread` (`--session-host` only). Unset: every call injects
/// directly from the caller's thread, as before.
static INPUT_TX: OnceLock<mpsc::Sender<InputOp>> = OnceLock::new();

/// `--session-host`: start the one `Input` thread. From then on every input operation
/// (pointer, wheel, key, end of contact, touch repeat, `InitializeTouchInjection`,
/// `SetCursorPos`) runs there, and that thread follows the input desktop.
pub fn start_input_thread(app: App) {
    let (tx, rx) = mpsc::channel::<InputOp>();
    match std::thread::Builder::new().name("Input".into()).spawn(move || input_thread(app, rx)) {
        Ok(_) => {
            // A second call drops its sender here, so its thread sees a closed channel and ends.
            if INPUT_TX.set(tx).is_ok() {
                println!("[Input] Session host: input runs on the Input thread and follows the input desktop.");
            }
        }
        Err(err) => println!("[Input] Could not start the Input thread ({err}); input is injected from the caller's thread."),
    }
}

/// Queue to the `Input` thread when it runs; otherwise (normal run, or the thread is gone)
/// inject directly on this thread.
fn dispatch(op: InputOp) {
    let op = match INPUT_TX.get() {
        Some(tx) => match tx.send(op) {
            Ok(()) => return,
            Err(mpsc::SendError(op)) => op,
        },
        None => op,
    };
    run_direct(op);
}

/// Today's direct behavior: results are not acted on.
fn run_direct(op: InputOp) {
    match op {
        InputOp::Pointer { kind, x, y, touch } => {
            let _ = pointer_now(&kind, x, y, touch);
        }
        InputOp::Wheel(delta) => {
            let _ = wheel_now(delta);
        }
        InputOp::Key(token) => {
            let _ = key_now(&token);
        }
        InputOp::EndContact => {
            let _ = end_contact_now();
        }
        InputOp::RepeatTouch => {
            let _ = repeat_touch_now();
        }
        InputOp::ProbeTouch(reply) => {
            let _ = reply.send(probe_touch_now());
        }
    }
}

/// The session-host `Input` thread. Before each event it checks the input desktop
/// (rate-limited, immediately after a failed injection) and re-attaches when it changed.
fn input_thread(app: App, rx: mpsc::Receiver<InputOp>) {
    let mut desktop = crate::win_desktop::DesktopAttachment::new("Input");
    let mut force_check = true;
    // Left button pressed through the mouse path and not released yet.
    let mut left_down = false;
    // `InitializeTouchInjection` failed while attached to a secure desktop (for example at
    // boot on the sign-in screen): try again once the thread is on `Default`.
    let mut retry_touch_on_default = false;
    while let Ok(op) = rx.recv() {
        let check = desktop.check(force_check, || {
            // A touch contact belongs to the desktop it started on: end it before leaving
            // (best effort; injection may already be refused), then forget it.
            if let Err(err) = end_contact_now() {
                app.log("debug", &format!("[Input] Ending the touch contact before a desktop switch: {err}"));
            }
            if let Ok(mut xy) = TOUCH_XY.lock() {
                xy.2 = false;
            }
        });
        force_check = false;
        if let DesktopCheck::Switched(_) = check {
            // Moves and the up of a contact that started on the old desktop are then ignored
            // by the route's touch filter, and the repeat tick stops.
            if let Ok(mut down) = app.inner.touch_down.lock() {
                *down = false;
            }
            if left_down {
                left_down = false;
                if let Err(err) = unsafe { mouse_button(true, false) } {
                    app.log("debug", &format!("[Input] Releasing the left button after a desktop switch: {err}"));
                }
            }
            if retry_touch_on_default && desktop.on_default() {
                retry_touch_on_default = false;
                if probe_touch_now() {
                    app.inner.touch_enabled.store(true, Ordering::Relaxed);
                    println!("[Input] Touch injection initialized on the Default desktop.");
                }
            }
        }
        let on_default = desktop.on_default();
        let result = match op {
            InputOp::Pointer { kind, x, y, touch } => match pointer_path_on_desktop(&kind, touch, touch_ready(), on_default) {
                PointerPath::Touch => inject_touch(&kind, x, y),
                PointerPath::Mouse => {
                    match kind.as_str() {
                        "down" => left_down = true,
                        "up" => left_down = false,
                        _ => {}
                    }
                    mouse_pointer(&kind, x, y)
                }
            },
            InputOp::Wheel(delta) => wheel_now(delta),
            InputOp::Key(token) => key_now(&token),
            InputOp::EndContact => {
                let touch = end_contact_now();
                let mouse = if left_down {
                    left_down = false;
                    unsafe { mouse_button(true, false) }
                } else {
                    Ok(())
                };
                touch.and(mouse)
            }
            InputOp::RepeatTouch => {
                let contact_down = TOUCH_XY.lock().map(|xy| xy.2).unwrap_or(false);
                if touch_repeat_applies(contact_down, on_default) {
                    repeat_touch_now()
                } else {
                    Ok(())
                }
            }
            InputOp::ProbeTouch(reply) => {
                let ready = probe_touch_now();
                retry_touch_on_default = !ready && !on_default;
                let _ = reply.send(ready);
                Ok(())
            }
        };
        if let Err(err) = result {
            // A refused injection is what a desktop switch looks like: re-check at once.
            app.log("debug", &format!("[Input] {err} (desktop {})", desktop.name().unwrap_or("unknown")));
            force_check = true;
        }
    }
}

fn touch_ready() -> bool {
    TOUCH_READY.lock().map(|r| *r).unwrap_or(false)
}

pub fn probe_touch() -> bool {
    let (tx, rx) = mpsc::channel();
    dispatch(InputOp::ProbeTouch(tx));
    rx.recv_timeout(Duration::from_secs(5)).unwrap_or(false)
}

fn probe_touch_now() -> bool {
    unsafe {
        match InitializeTouchInjection(1, TOUCH_FEEDBACK_DEFAULT) {
            Ok(()) => {
                if let Ok(mut ready) = TOUCH_READY.lock() {
                    *ready = true;
                }
                true
            }
            Err(err) => {
                println!("[Input] InjectTouchInput is unavailable ({err}).");
                false
            }
        }
    }
}

pub fn inject_pointer(kind: &str, x: i32, y: i32, touch: bool) {
    dispatch(InputOp::Pointer { kind: kind.to_string(), x, y, touch });
}

pub fn inject_wheel(delta: i32) {
    dispatch(InputOp::Wheel(delta));
}

pub fn inject_key(token: &str) {
    dispatch(InputOp::Key(token.to_string()));
}

pub fn end_contact() {
    dispatch(InputOp::EndContact);
}

pub fn repeat_touch() {
    dispatch(InputOp::RepeatTouch);
}

fn pointer_now(kind: &str, x: i32, y: i32, touch: bool) -> Result<(), String> {
    if touch && kind != "rightclick" {
        return inject_touch(kind, x, y);
    }
    mouse_pointer(kind, x, y)
}

/// `SetCursorPos`, then the button for `kind`. Buttons are sent even if the move failed.
fn mouse_pointer(kind: &str, x: i32, y: i32) -> Result<(), String> {
    unsafe {
        let moved = SetCursorPos(x, y).map_err(|e| format!("SetCursorPos({x}, {y}): {e}"));
        let buttons = match kind {
            "down" => mouse_button(true, true),
            "up" => mouse_button(true, false),
            "rightclick" => {
                let down = mouse_button(false, true);
                let up = mouse_button(false, false);
                down.and(up)
            }
            _ => Ok(()),
        };
        moved.and(buttons)
    }
}

fn wheel_now(delta: i32) -> Result<(), String> {
    unsafe { send_mouse(MOUSEEVENTF_WHEEL, delta) }
}

fn end_contact_now() -> Result<(), String> {
    // Copy the state out and drop the guard: `inject_touch` locks `TOUCH_XY` itself.
    let (x, y, down) = TOUCH_XY.lock().map(|xy| *xy).unwrap_or((0, 0, false));
    if !down {
        return Ok(());
    }
    // `inject_touch("up")` stores the contact as up. Mark it up even if injection fails.
    let result = inject_touch("up", x, y);
    if let Ok(mut xy) = TOUCH_XY.lock() {
        xy.2 = false;
    }
    result
}

fn repeat_touch_now() -> Result<(), String> {
    let (x, y, down) = TOUCH_XY.lock().map(|xy| *xy).unwrap_or((0, 0, false));
    if down {
        return inject_touch("move", x, y);
    }
    Ok(())
}

fn inject_touch(kind: &str, x: i32, y: i32) -> Result<(), String> {
    if !touch_ready() {
        return Err("touch injection is not initialized".into());
    }
    let flags = match kind {
        "down" => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN,
        "up" => POINTER_FLAG_INRANGE | POINTER_FLAG_UP,
        _ => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE,
    };
    if let Ok(mut xy) = TOUCH_XY.lock() {
        xy.0 = x;
        xy.1 = y;
        xy.2 = kind != "up";
    }
    unsafe {
        let mut pointer_info = POINTER_INFO::default();
        pointer_info.pointerType = PT_TOUCH;
        pointer_info.pointerId = 1;
        pointer_info.pointerFlags = flags;
        pointer_info.ptPixelLocation = POINT { x, y };
        pointer_info.ptPixelLocationRaw = POINT { x, y };
        let contact = POINTER_TOUCH_INFO {
            pointerInfo: pointer_info,
            rcContact: RECT { left: x - 2, top: y - 2, right: x + 2, bottom: y + 2 },
            rcContactRaw: RECT { left: x - 2, top: y - 2, right: x + 2, bottom: y + 2 },
            pressure: 512,
            ..Default::default()
        };
        InjectTouchInput(&[contact]).map_err(|e| format!("InjectTouchInput({kind} at {x}, {y}): {e}"))
    }
}

unsafe fn mouse_button(left: bool, down: bool) -> Result<(), String> {
    let flags = match (left, down) {
        (true, true) => MOUSEEVENTF_LEFTDOWN,
        (true, false) => MOUSEEVENTF_LEFTUP,
        (false, true) => MOUSEEVENTF_RIGHTDOWN,
        (false, false) => MOUSEEVENTF_RIGHTUP,
    };
    send_mouse(flags, 0)
}

unsafe fn send_mouse(flags: MOUSE_EVENT_FLAGS, data: i32) -> Result<(), String> {
    let input = INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx: 0,
                dy: 0,
                mouseData: data as u32,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    send_one(input, "mouse")
}

/// One `SendInput` call with one event. `SendInput` returns how many events it inserted; zero
/// means the input was blocked (for example by another desktop or UIPI).
unsafe fn send_one(input: INPUT, what: &str) -> Result<(), String> {
    let sent = SendInput(&[input], std::mem::size_of::<INPUT>() as i32);
    if sent == 1 {
        Ok(())
    } else {
        Err(format!("SendInput({what}) inserted {sent} of 1 events: {}", windows::core::Error::from_win32()))
    }
}

fn key_now(token: &str) -> Result<(), String> {
    if let Some(ch) = brace_char(token) {
        return send_unicode(ch);
    }
    if let Some(vk) = named_vk(token) {
        return send_vk(vk);
    }
    if token.chars().count() == 1 {
        return send_unicode(token.chars().next().unwrap());
    }
    Ok(())
}

fn brace_char(token: &str) -> Option<char> {
    let inner = token.strip_prefix('{')?.strip_suffix('}')?;
    if inner.chars().count() == 1 {
        return inner.chars().next();
    }
    None
}

fn named_vk(token: &str) -> Option<VIRTUAL_KEY> {
    Some(match token {
        "{BACKSPACE}" => VK_BACK,
        "{ENTER}" => VK_RETURN,
        "{TAB}" => VK_TAB,
        "{ESC}" => VK_ESCAPE,
        "{DELETE}" => VK_DELETE,
        "{INSERT}" => VK_INSERT,
        "{HOME}" => VK_HOME,
        "{END}" => VK_END,
        "{PGUP}" => VK_PRIOR,
        "{PGDN}" => VK_NEXT,
        "{LEFT}" => VK_LEFT,
        "{RIGHT}" => VK_RIGHT,
        "{UP}" => VK_UP,
        "{DOWN}" => VK_DOWN,
        "{F1}" => VK_F1,
        "{F2}" => VK_F2,
        "{F3}" => VK_F3,
        "{F4}" => VK_F4,
        "{F5}" => VK_F5,
        "{F6}" => VK_F6,
        "{F7}" => VK_F7,
        "{F8}" => VK_F8,
        "{F9}" => VK_F9,
        "{F10}" => VK_F10,
        "{F11}" => VK_F11,
        "{F12}" => VK_F12,
        _ => return None,
    })
}

fn send_unicode(ch: char) -> Result<(), String> {
    let mut buf = [0u16; 2];
    let encoded = ch.encode_utf16(&mut buf);
    let mut result = Ok(());
    for unit in encoded {
        let down = unsafe { key_scan(*unit, true) };
        let up = unsafe { key_scan(*unit, false) };
        if result.is_ok() {
            result = down.and(up);
        }
    }
    result
}

fn send_vk(vk: VIRTUAL_KEY) -> Result<(), String> {
    unsafe {
        let down = INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT { wVk: vk, wScan: 0, dwFlags: Default::default(), time: 0, dwExtraInfo: 0 },
            },
        };
        let up = INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT { wVk: vk, wScan: 0, dwFlags: KEYEVENTF_KEYUP, time: 0, dwExtraInfo: 0 },
            },
        };
        let pressed = send_one(down, "key down");
        let released = send_one(up, "key up");
        pressed.and(released)
    }
}

unsafe fn key_scan(scan: u16, down: bool) -> Result<(), String> {
    let flags = if down { KEYEVENTF_UNICODE } else { KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
    let input = INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT { wVk: VIRTUAL_KEY(0), wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: 0 },
        },
    };
    send_one(input, "unicode key")
}

pub fn display_snapshot() -> (bool, i32, i32, Vec<(i32, i32)>) {
    let current = current_mode();
    (false, current.0, current.1, list_modes())
}

fn current_mode() -> (i32, i32) {
    unsafe {
        let mut dm = blank_devmode();
        if EnumDisplaySettingsW(None, ENUM_CURRENT_SETTINGS, &mut dm) == true {
            return (dm.dmPelsWidth as i32, dm.dmPelsHeight as i32);
        }
    }
    primary_size()
}

fn list_modes() -> Vec<(i32, i32)> {
    let mut modes = Vec::new();
    unsafe {
        let mut i = 0u32;
        loop {
            let mut dm = blank_devmode();
            if EnumDisplaySettingsW(None, win_mode(i), &mut dm) == false {
                break;
            }
            if dm.dmBitsPerPel >= 32 {
                let pair = (dm.dmPelsWidth as i32, dm.dmPelsHeight as i32);
                if !modes.contains(&pair) {
                    modes.push(pair);
                }
            }
            i += 1;
        }
    }
    modes.sort_by(|a, b| (b.0 as i64 * b.1 as i64).cmp(&(a.0 as i64 * a.1 as i64)));
    modes
}

fn win_mode(i: u32) -> windows::Win32::Graphics::Gdi::ENUM_DISPLAY_SETTINGS_MODE {
    windows::Win32::Graphics::Gdi::ENUM_DISPLAY_SETTINGS_MODE(i)
}

pub fn try_set_mode(width: i32, height: i32) -> bool {
    unsafe {
        let mut best: Option<DEVMODEW> = None;
        let mut i = 0u32;
        loop {
            let mut dm = blank_devmode();
            if EnumDisplaySettingsW(None, win_mode(i), &mut dm) == false {
                break;
            }
            if dm.dmPelsWidth == width as u32 && dm.dmPelsHeight == height as u32 && dm.dmBitsPerPel >= 32 {
                let replace = best.map(|b| dm.dmDisplayFrequency > b.dmDisplayFrequency).unwrap_or(true);
                if replace {
                    best = Some(dm);
                }
            }
            i += 1;
        }
        let mut target = if let Some(mode) = best { mode } else {
            let mut current = blank_devmode();
            if EnumDisplaySettingsW(None, ENUM_CURRENT_SETTINGS, &mut current) == false {
                return false;
            }
            current.dmPelsWidth = width as u32;
            current.dmPelsHeight = height as u32;
            current
        };
        target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
        let result = ChangeDisplaySettingsW(Some(&target as *const DEVMODEW), CDS_UPDATEREGISTRY);
        if result != DISP_CHANGE_SUCCESSFUL {
            println!("[Display] ChangeDisplaySettings to {width}x{height} returned {result:?}.");
            return false;
        }
        true
    }
}

fn blank_devmode() -> DEVMODEW {
    let mut dm = DEVMODEW::default();
    dm.dmSize = std::mem::size_of::<DEVMODEW>() as u16;
    dm
}

pub fn open_firewall(http_port: u16, https_port: u16) {
    let _ = Command::new("netsh").args(["advfirewall", "firewall", "delete", "rule", "name=TeslaPC Server"]).output();
    let result = Command::new("netsh")
        .args([
            "advfirewall",
            "firewall",
            "add",
            "rule",
            "name=TeslaPC Server",
            "dir=in",
            "action=allow",
            "protocol=TCP",
            &format!("localport={http_port},{https_port}"),
            "enable=yes",
            "profile=any",
        ])
        .output();
    match result {
        Ok(out) if out.status.success() => println!("[Firewall] Opened inbound TCP ports {http_port} and {https_port}."),
        Ok(out) => println!("[Firewall] Opening ports requires administrator privileges. {}", String::from_utf8_lossy(&out.stdout)),
        Err(err) => println!("[Firewall] {err}"),
    }
}

pub fn hotspot_set(on: bool, app: &App) -> Result<String, String> {
    if on {
        app.inner.manual_hotspot_off.store(false, Ordering::Relaxed);
        let result = run_hotspot(true);
        let report = crate::decisions::bypass::parse_hotspot_report(result.as_deref().unwrap_or(""));
        let step = crate::decisions::bypass::hotspot_step(
            crate::decisions::bypass::HotspotSource::SettingsOn,
            false,
            report,
            crate::decisions::bypass::bypass_address_present(&address_dump()),
        );
        if step.wait_ms > 0 {
            std::thread::sleep(Duration::from_millis(step.wait_ms));
        }
        if step.setup_bypass {
            setup_bypass();
        }
        result
    } else {
        app.inner.manual_hotspot_off.store(true, Ordering::Relaxed);
        run_hotspot(false)
    }
}

pub fn start_bypass(app: App) {
    std::thread::spawn(move || {
        apply_hotspot(&app, false);
        loop {
            std::thread::sleep(Duration::from_millis(HOTSPOT_WATCHDOG_MS));
            apply_hotspot(&app, true);
        }
    });
}

fn apply_hotspot(app: &App, watchdog: bool) {
    let manually_off = app.inner.manual_hotspot_off.load(Ordering::Relaxed);
    if watchdog && manually_off {
        return;
    }
    let result = run_hotspot(true);
    let report = crate::decisions::bypass::parse_hotspot_report(result.as_deref().unwrap_or(""));
    let step = crate::decisions::bypass::hotspot_step(
        crate::decisions::bypass::HotspotSource::Watchdog,
        manually_off,
        report,
        crate::decisions::bypass::bypass_address_present(&address_dump()),
    );
    if step.wait_ms > 0 {
        std::thread::sleep(Duration::from_millis(step.wait_ms));
    }
    if step.setup_bypass {
        setup_bypass();
        if watchdog {
            println!("[Hotspot] Watchdog re-attached the Tesla bypass.");
        }
    }
}

fn address_dump() -> String {
    Command::new("netsh")
        .args(["interface", "ipv4", "show", "addresses"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).into_owned())
        .unwrap_or_default()
}

fn setup_bypass() {
    let adapter = hotspot_adapter();
    let Some(adapter) = adapter else {
        println!("[TeslaBrowserBypass] Could not find the Mobile Hotspot adapter.");
        return;
    };
    let add = Command::new("netsh")
        .args(["interface", "ipv4", "add", "address", &format!("name={adapter}"), &format!("address={BYPASS_IP}"), "mask=255.255.255.0"])
        .output();
    match add {
        Ok(out) if out.status.success() || String::from_utf8_lossy(&out.stdout).to_ascii_lowercase().contains("already") => {
            println!("[TeslaBrowserBypass] {BYPASS_IP} on {adapter}");
        }
        Ok(out) => println!("[TeslaBrowserBypass] address failed: {}", String::from_utf8_lossy(&out.stdout)),
        Err(err) => println!("[TeslaBrowserBypass] {err}"),
    }
    for port in [8080u16, 8443] {
        let args = windows_portproxy_args(port);
        let mut parts = args.split_whitespace();
        let result = Command::new("netsh").args(parts.by_ref()).output();
        match result {
            Ok(out) if out.status.success() => println!("[TeslaBrowserBypass] Port proxy {BYPASS_IP}:{port} -> 127.0.0.1:{port}"),
            Ok(out) => println!("[TeslaBrowserBypass] portproxy failed: {}", String::from_utf8_lossy(&out.stdout)),
            Err(err) => println!("[TeslaBrowserBypass] {err}"),
        }
    }
}

fn hotspot_adapter() -> Option<String> {
    let output = Command::new("netsh").args(["interface", "show", "interface"]).output().ok()?;
    crate::decisions::bypass::hotspot_adapter_name(&String::from_utf8_lossy(&output.stdout))
}

fn run_hotspot(on: bool) -> Result<String, String> {
    let script = if on { HOTSPOT_ON } else { HOTSPOT_OFF };
    let encoded = encode_utf16_b64(script);
    let output = Command::new("powershell.exe")
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", &encoded])
        .output()
        .map_err(|e| e.to_string())?;
    let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
    let err = String::from_utf8_lossy(&output.stderr).trim().to_string();
    if !err.is_empty() {
        println!("[Hotspot] {err}");
    }
    if text.is_empty() {
        return Err("hotspot command produced no result (administrator privileges may be required)".into());
    }
    println!("[Hotspot] {text}");
    Ok(text)
}

fn encode_utf16_b64(script: &str) -> String {
    let bytes: Vec<u8> = script.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
    base64_encode(&bytes)
}

fn base64_encode(bytes: &[u8]) -> String {
    const T: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(T[((n >> 18) & 63) as usize] as char);
        out.push(T[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 { T[((n >> 6) & 63) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { T[(n & 63) as usize] as char } else { '=' });
    }
    out
}

const HOTSPOT_ON: &str = r#"
$ErrorActionPreference = 'Stop'
[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
[Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime] | Out-Null
$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if ($null -eq $profile) { Write-Output 'NOPROFILE'; exit 3 }
$mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
if ($mgr.TetheringOperationalState.ToString() -eq 'On') { Write-Output 'ALREADYON'; exit 0 }
$null = $mgr.StartTetheringAsync()
for ($i = 0; $i -lt 30; $i++) {
    if ($mgr.TetheringOperationalState.ToString() -eq 'On') { Write-Output 'TURNEDON'; exit 0 }
    Start-Sleep -Milliseconds 500
}
Write-Output ('STATE:' + $mgr.TetheringOperationalState)
exit 2
"#;

const HOTSPOT_OFF: &str = r#"
$ErrorActionPreference = 'Stop'
[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
[Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime] | Out-Null
$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if ($null -eq $profile) { Write-Output 'NOPROFILE'; exit 3 }
$mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
if ($mgr.TetheringOperationalState.ToString() -ne 'On') { Write-Output 'ALREADYOFF'; exit 0 }
$null = $mgr.StopTetheringAsync()
for ($i = 0; $i -lt 30; $i++) {
    if ($mgr.TetheringOperationalState.ToString() -ne 'On') { Write-Output 'TURNEDOFF'; exit 0 }
    Start-Sleep -Milliseconds 500
}
exit 2
"#;

pub fn start_audio(app: App) {
    std::thread::Builder::new().name("WasapiLoopback".into()).spawn(move || {
        // Each attempt starts from GetDefaultAudioEndpoint, so a new default device is picked up.
        let mut last_error = String::new();
        let mut wait_ms = 0u64;
        loop {
            let started = std::time::Instant::now();
            let result = run_loopback(app.clone());
            let ran_ms = started.elapsed().as_millis() as u64;
            app.inner.pcm.capturing.store(false, Ordering::Relaxed);
            let err = match result {
                Ok(()) => "WASAPI loopback ended".to_string(),
                Err(err) => err,
            };
            if ran_ms > crate::decisions::audio::LOOPBACK_HEALTHY_RUN_MS {
                // A healthy run ended, so this failure is news even if the text repeats.
                last_error.clear();
            }
            if err != last_error {
                println!("[Audio] WASAPI loopback unavailable: {err}. Retrying.");
                let _ = std::io::Write::flush(&mut std::io::stdout());
                last_error = err;
            }
            wait_ms = crate::decisions::audio::loopback_backoff_ms(wait_ms, ran_ms);
            std::thread::sleep(Duration::from_millis(wait_ms));
        }
    }).ok();
}

fn run_loopback(app: App) -> Result<(), String> {
    crate::win_capture::run_loopback(app)
}


