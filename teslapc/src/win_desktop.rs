//! Desktop attachment for one thread (`--session-host` only). The lock screen, sign-in screen
//! and UAC prompts live on the secure desktop (`Winlogon`); a thread attached to `Default`
//! cannot capture or inject there. `DesktopAttachment` moves its thread to whichever desktop
//! receives input. Each thread owns its own attachment and its own handles.

use std::time::Instant;

use windows::core::HRESULT;
use windows::Win32::Foundation::{ERROR_BUSY, HANDLE};
use windows::Win32::System::StationsAndDesktops::{CloseDesktop, GetThreadDesktop, GetUserObjectInformationW, OpenInputDesktop, SetThreadDesktop, DESKTOP_ACCESS_FLAGS, DESKTOP_CONTROL_FLAGS, HDESK, UOI_NAME};
use windows::Win32::System::Threading::GetCurrentThreadId;

use crate::platform::DesktopCheck;
use crate::decisions::desktop::{desktop_check_due, desktop_error_is_new, desktop_switch_needed, is_default_desktop};

/// `MAXIMUM_ALLOWED`: every right the caller's token grants. SYSTEM gets enough on `Winlogon`
/// for `SetThreadDesktop`, DXGI duplication, GDI reads and `SendInput`.
const DESKTOP_MAXIMUM_ALLOWED: DESKTOP_ACCESS_FLAGS = DESKTOP_ACCESS_FLAGS(0x0200_0000);

pub struct DesktopAttachment {
    role: &'static str,
    /// From `GetThreadDesktop`. Owned by the system; never closed.
    original: Option<HDESK>,
    /// Opened by this attachment with `OpenInputDesktop` and attached; `None` = `original`.
    attached: Option<HDESK>,
    /// Name of the desktop the thread is attached to; `None` if it could not be read.
    name: Option<String>,
    last_check: Option<Instant>,
    last_switch_failed: bool,
    last_error: Option<String>,
    /// Handles to close once their old thread has let go of them.
    pending_close: Vec<HDESK>,
}

impl DesktopAttachment {
    /// Must be created on the thread it manages.
    pub fn new(role: &'static str) -> Self {
        let original = unsafe { GetThreadDesktop(GetCurrentThreadId()) };
        let (original, name) = match original {
            Ok(desk) => {
                let name = desktop_name(desk).map_err(|err| println!("[Desktop] {role}: could not read the starting desktop name: {err}")).ok();
                (Some(desk), name)
            }
            Err(err) => {
                println!("[Desktop] {role}: GetThreadDesktop failed: {err}");
                (None, None)
            }
        };
        if let Some(name) = &name {
            println!("[Desktop] {role} starts on {name}");
        }
        DesktopAttachment { role, original, attached: None, name, last_check: None, last_switch_failed: false, last_error: None, pending_close: Vec::new() }
    }

    /// Name of the attached desktop, if known.
    pub fn name(&self) -> Option<&str> {
        self.name.as_deref()
    }

    /// The thread is on the user's desktop (or its desktop name is unknown).
    pub fn on_default(&self) -> bool {
        is_default_desktop(self.name())
    }

    /// Take over desktop handles a previous thread could not close (raw handle values from
    /// [`DesktopAttachment::release_for_restart`]).
    pub fn adopt(&mut self, leftovers: Vec<usize>) {
        for raw in leftovers.into_iter().filter(|raw| *raw != 0) {
            self.pending_close.push(HDESK(raw as *mut core::ffi::c_void));
        }
    }

    /// Give up the attached handle (and any still pending) without closing them, as raw values
    /// a restarted thread closes after this thread has exited. A handle cannot be closed while
    /// its thread is attached to it, and this thread stays attached until it exits.
    pub fn release_for_restart(mut self) -> Vec<usize> {
        let mut out: Vec<usize> = self.pending_close.drain(..).map(|h| h.0 as usize).collect();
        if let Some(desk) = self.attached.take() {
            out.push(desk.0 as usize);
        }
        out
    }

    /// Check the input desktop (rate-limited by `desktop_check_due`) and attach to it when its
    /// name differs. `before_switch` runs only when a switch will be attempted, before
    /// `SetThreadDesktop`, to tear down anything bound to the old desktop.
    pub fn check(&mut self, force: bool, before_switch: impl FnOnce()) -> DesktopCheck {
        let since = self.last_check.map(|t| t.elapsed().as_millis() as u64);
        if !desktop_check_due(since, force, self.last_switch_failed) {
            return DesktopCheck::Stay;
        }
        self.last_check = Some(Instant::now());
        self.close_pending();
        let input = match unsafe { OpenInputDesktop(DESKTOP_CONTROL_FLAGS(0), false, DESKTOP_MAXIMUM_ALLOWED) } {
            Ok(desk) => desk,
            Err(err) => {
                // Fails briefly while Windows switches desktops: stay where we are.
                self.log_error(&format!("OpenInputDesktop failed: {err}"));
                return DesktopCheck::Stay;
            }
        };
        let input_name = match desktop_name(input) {
            Ok(name) => name,
            Err(err) => {
                close_desktop(input, self.role);
                self.log_error(&format!("could not read the input desktop name: {err}"));
                return DesktopCheck::Stay;
            }
        };
        if !desktop_switch_needed(self.name(), &input_name) {
            close_desktop(input, self.role);
            self.last_error = None;
            return DesktopCheck::Stay;
        }
        before_switch();
        match unsafe { SetThreadDesktop(input) } {
            Ok(()) => {
                if let Some(previous) = self.attached.replace(input) {
                    // The thread has left `previous`, so it can be closed now.
                    close_desktop(previous, self.role);
                }
                self.name = Some(input_name.clone());
                self.last_switch_failed = false;
                self.last_error = None;
                println!("[Desktop] {} attached to {input_name}", self.role);
                let _ = std::io::Write::flush(&mut std::io::stdout());
                DesktopCheck::Switched(input_name)
            }
            Err(err) => {
                close_desktop(input, self.role);
                self.last_switch_failed = true;
                self.log_error(&format!("SetThreadDesktop({input_name}) failed: {err}"));
                if err.code() == HRESULT::from_win32(ERROR_BUSY.0) {
                    DesktopCheck::Busy
                } else {
                    DesktopCheck::Failed
                }
            }
        }
    }

    fn close_pending(&mut self) {
        let role = self.role;
        self.pending_close.retain(|desk| match unsafe { CloseDesktop(*desk) } {
            Ok(()) => false,
            Err(err) => {
                println!("[Desktop] {role}: a previous thread's desktop handle is not closed yet ({err}); will retry.");
                true
            }
        });
    }

    fn log_error(&mut self, error: &str) {
        if desktop_error_is_new(self.last_error.as_deref(), error) {
            println!("[Desktop] {}: {error}", self.role);
            let _ = std::io::Write::flush(&mut std::io::stdout());
            self.last_error = Some(error.to_string());
        }
    }
}

impl Drop for DesktopAttachment {
    fn drop(&mut self) {
        if let Some(desk) = self.attached.take() {
            // A handle in use by this thread cannot be closed: go back to the original first.
            if let Some(original) = self.original {
                if let Err(err) = unsafe { SetThreadDesktop(original) } {
                    println!("[Desktop] {}: could not return to the starting desktop: {err}", self.role);
                }
            }
            close_desktop(desk, self.role);
        }
        for desk in self.pending_close.drain(..) {
            close_desktop(desk, self.role);
        }
    }
}

fn close_desktop(desk: HDESK, role: &str) {
    if let Err(err) = unsafe { CloseDesktop(desk) } {
        println!("[Desktop] {role}: CloseDesktop failed: {err}");
    }
}

/// `UOI_NAME` of a desktop, for example `Default` or `Winlogon`.
fn desktop_name(desk: HDESK) -> Result<String, String> {
    let mut buf = [0u16; 256];
    let mut needed = 0u32;
    unsafe {
        GetUserObjectInformationW(
            HANDLE(desk.0),
            UOI_NAME,
            Some(buf.as_mut_ptr() as *mut core::ffi::c_void),
            (buf.len() * std::mem::size_of::<u16>()) as u32,
            Some(&mut needed),
        )
        .map_err(|e| format!("GetUserObjectInformationW(UOI_NAME): {e}"))?;
    }
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    Ok(String::from_utf16_lossy(&buf[..len]))
}
