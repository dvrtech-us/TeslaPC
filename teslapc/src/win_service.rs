//! Windows service. `--service` is the supervisor: it runs in session 0 as LocalSystem, never
//! binds a port, and keeps one `--session-host` process alive in the active console session.
//! `service install` / `service uninstall` register and remove it through the SCM.
//! Decisions (step, backoff, rotation, command lines) are in `decisions::service`.

use std::ffi::OsString;
use std::io::Write;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::IntoRawHandle;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::core::{HRESULT, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, ERROR_INVALID_PARAMETER, ERROR_SERVICE_DOES_NOT_EXIST, ERROR_SERVICE_EXISTS, ERROR_SERVICE_MARKED_FOR_DELETE, ERROR_SERVICE_NOT_ACTIVE, HANDLE, WAIT_FAILED, WAIT_OBJECT_0};
use windows::Win32::Security::{DuplicateTokenEx, SecurityImpersonation, SetTokenInformation, TokenPrimary, TokenSessionId, TOKEN_ADJUST_DEFAULT, TOKEN_ADJUST_SESSIONID, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE, TOKEN_IMPERSONATE, TOKEN_QUERY};
use windows::Win32::System::Console::{SetStdHandle, STD_ERROR_HANDLE, STD_OUTPUT_HANDLE};
use windows::Win32::System::Environment::{CreateEnvironmentBlock, DestroyEnvironmentBlock};
use windows::Win32::System::JobObjects::{AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE};
use windows::Win32::System::RemoteDesktop::WTSGetActiveConsoleSessionId;
use windows::Win32::System::Threading::{
    CreateEventW, CreateProcessAsUserW, GetCurrentProcess, GetCurrentProcessId, GetExitCodeProcess, OpenProcess, OpenProcessToken, ResumeThread, SetEvent, TerminateProcess, WaitForMultipleObjects, WaitForSingleObject, CREATE_NO_WINDOW, CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, INFINITE, PROCESS_INFORMATION, PROCESS_SYNCHRONIZE, STARTUPINFOW,
};
use windows_service::service::{
    ServiceAccess, ServiceAction, ServiceActionType, ServiceControl, ServiceControlAccept, ServiceErrorControl, ServiceExitCode, ServiceFailureActions, ServiceFailureResetPeriod, ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType, SessionChangeReason,
};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult, ServiceStatusHandle};
use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

use crate::decisions::service::{
    host_command_line, log_should_rotate, next_failure_count, restart_delay_ms, service_launch_arguments, supervisor_step, tasks_running_teslapc, SupervisorInput, SupervisorStep, EXIT_LAUNCH_FAILED, EXIT_PORT_IN_USE, HOST_TERMINATE_WAIT_MS, NO_CONSOLE_SESSION, SERVICE_DISPLAY_NAME, SERVICE_NAME, SERVICE_RESTART_DELAY_MS,
    SUPERVISOR_POLL_MS, UNINSTALL_STOP_WAIT_MS,
};
use crate::paths;

// ---------------------------------------------------------------------------------------------
// Handles and logs

/// Owns a Win32 handle and closes it on drop.
struct OwnedHandle(HANDLE);

// Process, thread, job, and event handles may be used and closed from any thread.
unsafe impl Send for OwnedHandle {}
unsafe impl Sync for OwnedHandle {}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

/// Open `path` for appending. A file over `LOG_ROTATE_BYTES` is renamed to `<path>.1` first,
/// replacing an older `.1`.
fn open_log(path: &Path) -> std::io::Result<std::fs::File> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    if let Ok(meta) = std::fs::metadata(path) {
        if log_should_rotate(meta.len()) {
            let mut old = path.as_os_str().to_owned();
            old.push(".1");
            let _ = std::fs::remove_file(&old);
            let _ = std::fs::rename(path, &old);
        }
    }
    std::fs::OpenOptions::new().create(true).append(true).open(path)
}

/// One timestamped line in `teslapc-service.log`. The supervisor has no console.
fn slog(line: &str) {
    let stamped = format!("{} {line}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"));
    println!("{stamped}");
    if let Ok(mut file) = open_log(&paths::service_log_file()) {
        let _ = writeln!(file, "{stamped}");
    }
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn wide_os(s: &std::ffi::OsStr) -> Vec<u16> {
    s.encode_wide().chain(std::iter::once(0)).collect()
}

fn ws_err(err: &windows_service::Error) -> String {
    match err {
        windows_service::Error::Winapi(io) => io.to_string(),
        other => other.to_string(),
    }
}

fn ws_code(err: &windows_service::Error) -> Option<u32> {
    match err {
        windows_service::Error::Winapi(io) => io.raw_os_error().map(|c| c as u32),
        _ => None,
    }
}

// ---------------------------------------------------------------------------------------------
// Session host side

/// Point this process's stdout and stderr at `%ProgramData%\TeslaPC\teslapc.log`.
/// Rust's `Stdout`/`Stderr` on Windows call `GetStdHandle` on every write, so `println!` picks
/// up the new handles immediately. The file handles are intentionally never closed.
pub fn redirect_host_log() {
    let path = paths::host_log_file();
    let file = match open_log(&path) {
        Ok(file) => file,
        Err(err) => {
            slog(&format!("[Host] Could not open {}: {err}. Host output is lost.", path.display()));
            return;
        }
    };
    let err_file = match file.try_clone() {
        Ok(f) => Some(f),
        Err(err) => {
            slog(&format!("[Host] Could not duplicate the log handle for stderr: {err}"));
            None
        }
    };
    unsafe {
        let out = HANDLE(file.into_raw_handle());
        if let Err(err) = SetStdHandle(STD_OUTPUT_HANDLE, out) {
            slog(&format!("[Host] SetStdHandle(stdout) failed: {err}"));
            let _ = CloseHandle(out);
        }
        if let Some(f) = err_file {
            let errh = HANDLE(f.into_raw_handle());
            if let Err(err) = SetStdHandle(STD_ERROR_HANDLE, errh) {
                slog(&format!("[Host] SetStdHandle(stderr) failed: {err}"));
                let _ = CloseHandle(errh);
            }
        }
    }
    println!();
    println!("==== {} TeslaPC {} session host, pid {} ====", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"), crate::crate_version(), std::process::id());
}

/// Exit this process when the supervisor process `pid` exits. Backstop for the job object.
pub fn exit_with_supervisor(pid: u32) {
    let handle = match unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, pid) } {
        Ok(h) => OwnedHandle(h),
        Err(err) if err.code() == HRESULT::from_win32(ERROR_INVALID_PARAMETER.0) => {
            println!("[Service] Supervisor pid {pid} is not running. Exiting.");
            let _ = std::io::stdout().flush();
            std::process::exit(0);
        }
        Err(err) => {
            println!("[Service] Cannot watch supervisor pid {pid}: {err}. Relying on the job object.");
            return;
        }
    };
    let spawned = std::thread::Builder::new().name("SupervisorWatch".into()).spawn(move || {
        // Move the whole OwnedHandle (not just its field) into the thread.
        let handle = handle;
        let result = unsafe { WaitForSingleObject(handle.0, INFINITE) };
        if result == WAIT_OBJECT_0 {
            println!("[Service] Supervisor pid {pid} exited. Session host exiting.");
            let _ = std::io::stdout().flush();
            std::process::exit(0);
        }
        println!("[Service] Waiting on supervisor pid {pid} failed ({:?}): {}", result, windows::core::Error::from_win32());
    });
    if let Err(err) = spawned {
        println!("[Service] Could not start the supervisor watch thread: {err}");
    }
}

// ---------------------------------------------------------------------------------------------
// Supervisor

enum Signal {
    Stop,
    Logoff(u32),
    SessionEvent,
}

/// Control-handler → supervisor-loop mailbox. The handler pushes and sets the event; the loop
/// drains it. Nothing else is shared.
struct Signals {
    queue: Mutex<Vec<Signal>>,
    event: OwnedHandle,
}

impl Signals {
    fn new() -> Result<Self, String> {
        let event = unsafe { CreateEventW(None, false, false, PCWSTR::null()) }.map_err(|e| format!("CreateEventW: {e}"))?;
        Ok(Signals { queue: Mutex::new(Vec::new()), event: OwnedHandle(event) })
    }

    fn push(&self, signal: Signal) {
        if let Ok(mut q) = self.queue.lock() {
            q.push(signal);
        }
        if let Err(err) = unsafe { SetEvent(self.event.0) } {
            slog(&format!("[Service] SetEvent failed: {err}"));
        }
    }

    fn drain(&self) -> Vec<Signal> {
        self.queue.lock().map(|mut q| std::mem::take(&mut *q)).unwrap_or_default()
    }
}

struct Status {
    handle: ServiceStatusHandle,
    checkpoint: u32,
}

impl Status {
    fn set(&mut self, state: ServiceState, wait_ms: u64, exit_code: u32) {
        let pending = matches!(state, ServiceState::StartPending | ServiceState::StopPending);
        self.checkpoint = if pending { self.checkpoint + 1 } else { 0 };
        let controls_accepted = if state == ServiceState::Running {
            ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN | ServiceControlAccept::SESSION_CHANGE
        } else {
            ServiceControlAccept::empty()
        };
        let status = ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: state,
            controls_accepted,
            exit_code: ServiceExitCode::Win32(exit_code),
            checkpoint: self.checkpoint,
            wait_hint: Duration::from_millis(if pending { wait_ms } else { 0 }),
            process_id: None,
        };
        if let Err(err) = self.handle.set_service_status(status) {
            slog(&format!("[Service] SetServiceStatus({state:?}) failed: {}", ws_err(&err)));
        }
    }
}

windows_service::define_windows_service!(ffi_service_main, service_main);

/// Hand this process to the SCM. Returns `false` (and logs) when the SCM did not start it.
pub fn run_as_service() -> bool {
    match windows_service::service_dispatcher::start(SERVICE_NAME, ffi_service_main) {
        Ok(()) => true,
        Err(err) => {
            println!("[Service] Not started by the Service Control Manager ({}). Running the server in this console.", ws_err(&err));
            false
        }
    }
}

fn service_main(_arguments: Vec<OsString>) {
    slog(&format!("[Service] Supervisor {} starting, pid {}.", crate::crate_version(), std::process::id()));
    let signals = match Signals::new() {
        Ok(s) => Arc::new(s),
        Err(err) => {
            slog(&format!("[Service] {err}. Service cannot run."));
            if let Ok(handle) = service_control_handler::register(SERVICE_NAME, |_| ServiceControlHandlerResult::NoError) {
                Status { handle, checkpoint: 0 }.set(ServiceState::Stopped, 0, 1);
            }
            return;
        }
    };
    let handler_signals = signals.clone();
    let handler = move |control: ServiceControl| match control {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            handler_signals.push(Signal::Stop);
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::SessionChange(param) => {
            if param.reason == SessionChangeReason::SessionLogoff {
                handler_signals.push(Signal::Logoff(param.notification.session_id));
            } else {
                handler_signals.push(Signal::SessionEvent);
            }
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    };
    let handle = match service_control_handler::register(SERVICE_NAME, handler) {
        Ok(h) => h,
        Err(err) => {
            slog(&format!("[Service] RegisterServiceCtrlHandlerExW failed: {}", ws_err(&err)));
            return;
        }
    };
    let mut status = Status { handle, checkpoint: 0 };
    status.set(ServiceState::StartPending, 3_000, 0);
    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(err) => {
            slog(&format!("[Service] Could not locate the TeslaPC executable: {err}"));
            status.set(ServiceState::Stopped, 0, 1);
            return;
        }
    };
    status.set(ServiceState::Running, 0, 0);
    supervise(&signals, &mut status, &exe);
    slog("[Service] Supervisor stopped.");
    status.set(ServiceState::Stopped, 0, 0);
}

struct Host {
    process: OwnedHandle,
    // The job holds JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: dropping it kills the host and its children.
    _job: Option<OwnedHandle>,
    pid: u32,
    session: u32,
    started: Instant,
}

impl Host {
    /// `Some(exit code)` once the process has exited.
    fn exited(&self) -> Option<u32> {
        let r = unsafe { WaitForSingleObject(self.process.0, 0) };
        if r != WAIT_OBJECT_0 {
            return None;
        }
        let mut code = 0u32;
        if let Err(err) = unsafe { GetExitCodeProcess(self.process.0, &mut code) } {
            slog(&format!("[Service] GetExitCodeProcess(pid {}) failed: {err}", self.pid));
            return Some(EXIT_LAUNCH_FAILED);
        }
        Some(code)
    }
}

/// Record of the most recent host that ended.
struct LastExit {
    code: u32,
    uptime_ms: u64,
    at: Instant,
}

fn supervise(signals: &Signals, status: &mut Status, exe: &Path) {
    let own_pid = unsafe { GetCurrentProcessId() };
    let mut host: Option<Host> = None;
    let mut stopping = false;
    let mut last_exit: Option<LastExit> = None;
    let mut failures = 0u32;
    let mut last_console: Option<u32> = None;
    loop {
        let mut logoff_for_host = false;
        for signal in signals.drain() {
            match signal {
                Signal::Stop => {
                    if !stopping {
                        slog("[Service] Stop requested.");
                    }
                    stopping = true;
                }
                Signal::Logoff(id) => {
                    slog(&format!("[Service] Session {id} logged off."));
                    if host.as_ref().map(|h| h.session) == Some(id) {
                        logoff_for_host = true;
                    }
                }
                Signal::SessionEvent => {}
            }
        }

        if let Some(code) = host.as_ref().and_then(Host::exited) {
            let h = host.take().expect("host present");
            let uptime_ms = h.started.elapsed().as_millis() as u64;
            failures = next_failure_count(failures, uptime_ms);
            slog(&format!("[Service] Host pid {} (session {}) exited with code {code} after {uptime_ms} ms.", h.pid, h.session));
            if !stopping {
                let delay = restart_delay_ms(code, failures);
                if code == EXIT_PORT_IN_USE as u32 {
                    slog(&format!("[Service] Port in use: the host could not bind 8080. Another TeslaPC (for example a logon scheduled task) may hold it. Retrying in {} s.", delay / 1000));
                } else {
                    slog(&format!("[Service] Restarting the host in {delay} ms (consecutive exits: {failures})."));
                }
            }
            last_exit = Some(LastExit { code, uptime_ms, at: Instant::now() });
            drop(h);
        }

        let console = unsafe { WTSGetActiveConsoleSessionId() };
        if last_console != Some(console) {
            if console == NO_CONSOLE_SESSION {
                slog("[Service] No session is attached to the console.");
            } else {
                slog(&format!("[Service] Console session is {console}."));
            }
            last_console = Some(console);
        }

        let input = SupervisorInput {
            stopping,
            host_alive: host.is_some(),
            host_session: host.as_ref().map(|h| h.session),
            console_session: console,
            logoff_for_host_session: logoff_for_host,
            last_exit_code: last_exit.as_ref().map(|e| e.code),
            uptime_ms: last_exit.as_ref().map(|e| e.uptime_ms).unwrap_or(0),
            since_exit_ms: last_exit.as_ref().map(|e| e.at.elapsed().as_millis() as u64).unwrap_or(0),
            failures,
        };
        match supervisor_step(&input) {
            SupervisorStep::Stop => {
                status.set(ServiceState::StopPending, HOST_TERMINATE_WAIT_MS + 2_000, 0);
                if let Some(h) = host.take() {
                    terminate_host(h, "service stopping");
                }
                return;
            }
            SupervisorStep::Keep => wait(signals, host.as_ref(), SUPERVISOR_POLL_MS),
            SupervisorStep::Wait { ms } => wait(signals, None, ms.min(SUPERVISOR_POLL_MS)),
            SupervisorStep::Restart { session } => {
                if let Some(h) = host.take() {
                    let reason = if logoff_for_host {
                        format!("session {} logged off", h.session)
                    } else {
                        format!("console session changed from {} to {session}", h.session)
                    };
                    terminate_host(h, &reason);
                }
                host = start_host(session, exe, own_pid, &mut failures, &mut last_exit);
            }
            SupervisorStep::Start { session } => {
                host = start_host(session, exe, own_pid, &mut failures, &mut last_exit);
            }
        }
    }
}

fn start_host(session: u32, exe: &Path, own_pid: u32, failures: &mut u32, last_exit: &mut Option<LastExit>) -> Option<Host> {
    match launch_host(session, exe, own_pid) {
        Ok(h) => {
            slog(&format!("[Service] Started host pid {} in session {session}.", h.pid));
            Some(h)
        }
        Err(err) => {
            *failures = next_failure_count(*failures, 0);
            let delay = restart_delay_ms(EXIT_LAUNCH_FAILED, *failures);
            slog(&format!("[Service] Could not start the host in session {session}: {err}. Retrying in {delay} ms."));
            *last_exit = Some(LastExit { code: EXIT_LAUNCH_FAILED, uptime_ms: 0, at: Instant::now() });
            None
        }
    }
}

/// Wait for a control signal, the host's exit, or `ms`.
fn wait(signals: &Signals, host: Option<&Host>, ms: u64) {
    let mut handles = vec![signals.event.0];
    if let Some(h) = host {
        handles.push(h.process.0);
    }
    let result = unsafe { WaitForMultipleObjects(&handles, false, ms.min(u32::MAX as u64 - 1) as u32) };
    if result == WAIT_FAILED {
        slog(&format!("[Service] WaitForMultipleObjects failed: {}", windows::core::Error::from_win32()));
        std::thread::sleep(Duration::from_millis(ms.min(SUPERVISOR_POLL_MS)));
    }
}

/// Terminate the host, wait (bounded) for it to exit, then drop it, which closes the job and
/// kills any child it left behind.
fn terminate_host(h: Host, reason: &str) {
    slog(&format!("[Service] Stopping host pid {} (session {}): {reason}.", h.pid, h.session));
    if h.exited().is_none() {
        if let Err(err) = unsafe { TerminateProcess(h.process.0, 1) } {
            slog(&format!("[Service] TerminateProcess(pid {}) failed: {err}", h.pid));
        }
        let r = unsafe { WaitForSingleObject(h.process.0, HOST_TERMINATE_WAIT_MS as u32) };
        if r != WAIT_OBJECT_0 {
            slog(&format!("[Service] Host pid {} did not exit within {HOST_TERMINATE_WAIT_MS} ms ({r:?}).", h.pid));
        }
    }
    drop(h);
}

/// Environment block from `CreateEnvironmentBlock`, destroyed on drop.
struct EnvBlock(*mut core::ffi::c_void);

impl Drop for EnvBlock {
    fn drop(&mut self) {
        if !self.0.is_null() {
            if let Err(err) = unsafe { DestroyEnvironmentBlock(self.0) } {
                slog(&format!("[Service] DestroyEnvironmentBlock failed: {err}"));
            }
        }
    }
}

fn create_kill_job() -> Result<OwnedHandle, String> {
    let job = OwnedHandle(unsafe { CreateJobObjectW(None, PCWSTR::null()) }.map_err(|e| format!("CreateJobObjectW: {e}"))?);
    let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    unsafe {
        SetInformationJobObject(job.0, JobObjectExtendedLimitInformation, &info as *const _ as *const core::ffi::c_void, std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32)
    }
    .map_err(|e| format!("SetInformationJobObject: {e}"))?;
    Ok(job)
}

/// Start `"<exe>" --session-host --supervisor-pid <own pid>` as SYSTEM in `session`, on
/// `winsta0\default`, suspended; put it in a fresh kill-on-close job; resume it.
fn launch_host(session: u32, exe: &Path, own_pid: u32) -> Result<Host, String> {
    let access = TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID | TOKEN_IMPERSONATE;
    let mut raw = HANDLE::default();
    unsafe { OpenProcessToken(GetCurrentProcess(), access, &mut raw) }.map_err(|e| format!("OpenProcessToken: {e}"))?;
    let own_token = OwnedHandle(raw);

    let mut raw = HANDLE::default();
    unsafe { DuplicateTokenEx(own_token.0, access, None, SecurityImpersonation, TokenPrimary, &mut raw) }.map_err(|e| format!("DuplicateTokenEx: {e}"))?;
    let token = OwnedHandle(raw);
    drop(own_token);

    let session_id = session;
    unsafe { SetTokenInformation(token.0, TokenSessionId, &session_id as *const u32 as *const core::ffi::c_void, std::mem::size_of::<u32>() as u32) }
        .map_err(|e| format!("SetTokenInformation(TokenSessionId={session}): {e}"))?;

    let mut env_ptr: *mut core::ffi::c_void = std::ptr::null_mut();
    let env = match unsafe { CreateEnvironmentBlock(&mut env_ptr, Some(token.0), false) } {
        Ok(()) => EnvBlock(env_ptr),
        Err(err) => {
            slog(&format!("[Service] CreateEnvironmentBlock failed: {err}. The host inherits the service environment."));
            EnvBlock(std::ptr::null_mut())
        }
    };

    let exe_str = exe.display().to_string();
    let app = wide_os(exe.as_os_str());
    let mut cmd = wide(&host_command_line(&exe_str, own_pid));
    let mut desktop = wide("winsta0\\default");
    let cwd = exe.parent().map(|d| wide_os(d.as_os_str()));

    let mut si = STARTUPINFOW { cb: std::mem::size_of::<STARTUPINFOW>() as u32, ..Default::default() };
    si.lpDesktop = PWSTR(desktop.as_mut_ptr());
    let mut pi = PROCESS_INFORMATION::default();
    let mut flags = CREATE_NO_WINDOW | CREATE_SUSPENDED;
    let env_arg = if env.0.is_null() {
        None
    } else {
        flags |= CREATE_UNICODE_ENVIRONMENT;
        Some(env.0 as *const core::ffi::c_void)
    };
    let cwd_arg = cwd.as_ref().map(|c| PCWSTR(c.as_ptr())).unwrap_or(PCWSTR::null());
    unsafe { CreateProcessAsUserW(Some(token.0), PCWSTR(app.as_ptr()), Some(PWSTR(cmd.as_mut_ptr())), None, None, false, flags, env_arg, cwd_arg, &si, &mut pi) }
        .map_err(|e| format!("CreateProcessAsUserW: {e}"))?;
    let process = OwnedHandle(pi.hProcess);
    let thread = OwnedHandle(pi.hThread);
    drop(env);
    drop(token);

    let job = match create_kill_job() {
        Ok(job) => match unsafe { AssignProcessToJobObject(job.0, process.0) } {
            Ok(()) => Some(job),
            Err(err) => {
                slog(&format!("[Service] AssignProcessToJobObject(pid {}) failed: {err}. Continuing without a job; the host watches the supervisor pid.", pi.dwProcessId));
                None
            }
        },
        Err(err) => {
            slog(&format!("[Service] {err}. Continuing without a job; the host watches the supervisor pid."));
            None
        }
    };

    if unsafe { ResumeThread(thread.0) } == u32::MAX {
        let err = windows::core::Error::from_win32();
        if let Err(term) = unsafe { TerminateProcess(process.0, 1) } {
            slog(&format!("[Service] TerminateProcess after a failed resume failed: {term}"));
        }
        return Err(format!("ResumeThread: {err}"));
    }
    drop(thread);
    Ok(Host { process, _job: job, pid: pi.dwProcessId, session, started: Instant::now() })
}

// ---------------------------------------------------------------------------------------------
// Install / uninstall

pub fn service_install() -> i32 {
    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(err) => {
            println!("[Service] Could not locate the TeslaPC executable: {err}");
            return 1;
        }
    };
    let manager = match ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE) {
        Ok(m) => m,
        Err(err) => {
            println!("[Service] Could not open the Service Control Manager: {}. Run this from an elevated (Administrator) prompt.", ws_err(&err));
            return 1;
        }
    };
    let info = ServiceInfo {
        name: OsString::from(SERVICE_NAME),
        display_name: OsString::from(SERVICE_DISPLAY_NAME),
        service_type: ServiceType::OWN_PROCESS,
        start_type: ServiceStartType::AutoStart,
        error_control: ServiceErrorControl::Normal,
        executable_path: exe.clone(),
        launch_arguments: service_launch_arguments().into_iter().map(OsString::from).collect(),
        dependencies: vec![],
        account_name: None,
        account_password: None,
    };
    let access = ServiceAccess::QUERY_STATUS | ServiceAccess::CHANGE_CONFIG | ServiceAccess::START;
    let service = match manager.create_service(&info, access) {
        Ok(s) => {
            println!("[Service] Created service {SERVICE_NAME}: \"{}\" --service (auto start, LocalSystem).", exe.display());
            s
        }
        Err(err) if ws_code(&err) == Some(ERROR_SERVICE_EXISTS.0) => {
            let s = match manager.open_service(SERVICE_NAME, access) {
                Ok(s) => s,
                Err(err) => {
                    println!("[Service] Service {SERVICE_NAME} exists but could not be opened: {}", ws_err(&err));
                    return 1;
                }
            };
            if let Err(err) = s.change_config(&info) {
                println!("[Service] Could not update service {SERVICE_NAME}: {}", ws_err(&err));
                return 1;
            }
            println!("[Service] Updated existing service {SERVICE_NAME}: \"{}\" --service (auto start, LocalSystem).", exe.display());
            s
        }
        Err(err) if ws_code(&err) == Some(ERROR_SERVICE_MARKED_FOR_DELETE.0) => {
            println!("[Service] Service {SERVICE_NAME} is marked for deletion. Close the Services console and any handle to it, or reboot, then install again.");
            return 1;
        }
        Err(err) => {
            println!("[Service] Could not create service {SERVICE_NAME}: {}. Run this from an elevated (Administrator) prompt.", ws_err(&err));
            return 1;
        }
    };
    if let Err(err) = service.set_description("TeslaPC remote desktop. Supervises the TeslaPC host in the console session.") {
        println!("[Service] Could not set the description: {}", ws_err(&err));
    }
    let restart = ServiceAction { action_type: ServiceActionType::Restart, delay: Duration::from_millis(SERVICE_RESTART_DELAY_MS) };
    let actions = ServiceFailureActions {
        reset_period: ServiceFailureResetPeriod::After(Duration::from_secs(24 * 60 * 60)),
        reboot_msg: None,
        command: None,
        actions: Some(vec![restart.clone(), restart.clone(), restart]),
    };
    match service.update_failure_actions(actions) {
        Ok(()) => println!("[Service] Failure actions: restart after {} s.", SERVICE_RESTART_DELAY_MS / 1000),
        Err(err) => println!("[Service] Could not set failure actions: {}", ws_err(&err)),
    }
    println!("[Service] Installed. Start it with `sc start {SERVICE_NAME}` or reboot.");
    warn_scheduled_tasks();
    0
}

/// Best effort: name any scheduled task that runs a `teslapc.exe`. Silent when `schtasks` fails.
fn warn_scheduled_tasks() {
    let Ok(out) = std::process::Command::new("schtasks").args(["/query", "/fo", "csv", "/v"]).output() else { return };
    if !out.status.success() {
        return;
    }
    let tasks = tasks_running_teslapc(&String::from_utf8_lossy(&out.stdout));
    if !tasks.is_empty() {
        println!("[Service] Warning: scheduled task(s) {} run teslapc.exe. Disable them: they will hold ports 8080/8443 and the service host will fail with \"port in use\".", tasks.join(", "));
    }
}

pub fn service_uninstall() -> i32 {
    let manager = match ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT) {
        Ok(m) => m,
        Err(err) => {
            println!("[Service] Could not open the Service Control Manager: {}. Run this from an elevated (Administrator) prompt.", ws_err(&err));
            return 1;
        }
    };
    let service = match manager.open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS | ServiceAccess::STOP | ServiceAccess::DELETE) {
        Ok(s) => s,
        Err(err) if ws_code(&err) == Some(ERROR_SERVICE_DOES_NOT_EXIST.0) => {
            println!("[Service] Service {SERVICE_NAME} is not installed. Nothing to remove.");
            return 0;
        }
        Err(err) => {
            println!("[Service] Could not open service {SERVICE_NAME}: {}. Run this from an elevated (Administrator) prompt.", ws_err(&err));
            return 1;
        }
    };
    match service.query_status() {
        Ok(s) if s.current_state != ServiceState::Stopped => {
            if s.current_state != ServiceState::StopPending {
                match service.stop() {
                    Ok(_) => println!("[Service] Stopping {SERVICE_NAME}..."),
                    Err(err) if ws_code(&err) == Some(ERROR_SERVICE_NOT_ACTIVE.0) => {}
                    Err(err) => println!("[Service] Stop failed: {}", ws_err(&err)),
                }
            }
            let deadline = Instant::now() + Duration::from_millis(UNINSTALL_STOP_WAIT_MS);
            let mut stopped = false;
            while Instant::now() < deadline {
                if matches!(service.query_status(), Ok(s) if s.current_state == ServiceState::Stopped) {
                    stopped = true;
                    break;
                }
                std::thread::sleep(Duration::from_millis(250));
            }
            if stopped {
                println!("[Service] Stopped.");
            } else {
                println!("[Service] {SERVICE_NAME} did not stop within {} s. Deleting anyway; it is removed when it stops.", UNINSTALL_STOP_WAIT_MS / 1000);
            }
        }
        Ok(_) => {}
        Err(err) => println!("[Service] Could not query status: {}", ws_err(&err)),
    }
    match service.delete() {
        Ok(()) => {
            println!("[Service] Removed service {SERVICE_NAME}.");
            0
        }
        Err(err) if ws_code(&err) == Some(ERROR_SERVICE_MARKED_FOR_DELETE.0) => {
            println!("[Service] Service {SERVICE_NAME} is already marked for deletion. It is removed when every handle to it is closed (close the Services console) or after a reboot.");
            0
        }
        Err(err) => {
            println!("[Service] Could not delete service {SERVICE_NAME}: {}", ws_err(&err));
            1
        }
    }
}
