//! Windows service decisions: the supervisor step, the host restart backoff, log rotation,
//! argument parsing, and the command lines the supervisor and installer build.
//! `win_service.rs` performs the OS calls and calls these.

/// Service name and display name registered with the Service Control Manager.
pub const SERVICE_NAME: &str = "TeslaPC";
pub const SERVICE_DISPLAY_NAME: &str = "TeslaPC";

/// `WTSGetActiveConsoleSessionId` result when no session is attached to the console.
pub const NO_CONSOLE_SESSION: u32 = 0xFFFF_FFFF;

/// Exit code of a `--session-host` process that could not bind port 8080.
pub const EXIT_PORT_IN_USE: i32 = 3;

/// Exit code the supervisor records for a host it could not launch. Never returned by a process
/// the supervisor started; used only inside the supervisor's state.
pub const EXIT_LAUNCH_FAILED: u32 = 0xFFFF_FFFF;

/// The supervisor wakes at least this often to re-read the console session.
pub const SUPERVISOR_POLL_MS: u64 = 2_000;
/// First restart delay after a host exits.
pub const HOST_BACKOFF_START_MS: u64 = 1_000;
/// Largest restart delay.
pub const HOST_BACKOFF_MAX_MS: u64 = 30_000;
/// A host that ran at least this long resets the backoff.
pub const HOST_HEALTHY_UPTIME_MS: u64 = 60_000;
/// Restart delay after a host exited with [`EXIT_PORT_IN_USE`].
pub const PORT_IN_USE_WAIT_MS: u64 = 30_000;
/// How long the supervisor waits for a terminated host to exit (session change and STOP).
pub const HOST_TERMINATE_WAIT_MS: u64 = 5_000;
/// `teslapc service uninstall` waits this long for the service to stop before deleting it.
pub const UNINSTALL_STOP_WAIT_MS: u64 = 10_000;
/// SCM failure action: restart the service this long after it crashes.
pub const SERVICE_RESTART_DELAY_MS: u64 = 5_000;

/// A log file larger than this at open is renamed to `<name>.1` and a new file is started.
pub const LOG_ROTATE_BYTES: u64 = 5 * 1024 * 1024;

/// Everything the supervisor knows when it decides what to do next.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SupervisorInput {
    /// STOP or SHUTDOWN was received. No further launches.
    pub stopping: bool,
    /// A host process exists and has not exited.
    pub host_alive: bool,
    /// Console session the live host was started in. `None` when there is no live host.
    pub host_session: Option<u32>,
    /// `WTSGetActiveConsoleSessionId` now.
    pub console_session: u32,
    /// A `WTS_SESSION_LOGOFF` notification arrived for the live host's session since the last step.
    pub logoff_for_host_session: bool,
    /// Exit code of the most recent host that ended. `None` before the first launch.
    pub last_exit_code: Option<u32>,
    /// How long that host ran, in ms.
    pub uptime_ms: u64,
    /// Time since that host ended, in ms.
    pub since_exit_ms: u64,
    /// Consecutive host exits, counted with [`next_failure_count`].
    pub failures: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SupervisorStep {
    /// The live host is in the right session (or there is no console session). Wait and poll.
    Keep,
    /// No live host: launch one in this session.
    Start { session: u32 },
    /// Terminate the live host, wait for it to exit, then launch in this session.
    Restart { session: u32 },
    /// No live host and none should be launched yet. Wait this long (interruptible).
    Wait { ms: u64 },
    /// Terminate the host and end the service.
    Stop,
}

pub fn supervisor_step(input: &SupervisorInput) -> SupervisorStep {
    if input.stopping {
        return SupervisorStep::Stop;
    }
    let console = input.console_session;
    if input.host_alive {
        if console == NO_CONSOLE_SESSION {
            return SupervisorStep::Keep;
        }
        if input.logoff_for_host_session || input.host_session != Some(console) {
            return SupervisorStep::Restart { session: console };
        }
        return SupervisorStep::Keep;
    }
    if console == NO_CONSOLE_SESSION {
        return SupervisorStep::Wait { ms: SUPERVISOR_POLL_MS };
    }
    let Some(code) = input.last_exit_code else {
        return SupervisorStep::Start { session: console };
    };
    let delay = restart_delay_ms(code, input.failures);
    if input.since_exit_ms >= delay {
        SupervisorStep::Start { session: console }
    } else {
        SupervisorStep::Wait { ms: delay - input.since_exit_ms }
    }
}

/// Consecutive-exit counter after a host ran `uptime_ms`. A healthy run starts the count over.
pub fn next_failure_count(previous: u32, uptime_ms: u64) -> u32 {
    if uptime_ms >= HOST_HEALTHY_UPTIME_MS {
        1
    } else {
        previous.saturating_add(1)
    }
}

/// Backoff for the nth consecutive exit: 1 s, 2 s, 4 s ... capped at 30 s. `0` means none.
pub fn host_backoff_ms(failures: u32) -> u64 {
    if failures == 0 {
        return 0;
    }
    let shift = (failures - 1).min(16);
    HOST_BACKOFF_START_MS.saturating_mul(1u64 << shift).min(HOST_BACKOFF_MAX_MS)
}

/// Delay before the next launch after a host ended with `exit_code`.
pub fn restart_delay_ms(exit_code: u32, failures: u32) -> u64 {
    if exit_code == EXIT_PORT_IN_USE as u32 {
        PORT_IN_USE_WAIT_MS
    } else {
        host_backoff_ms(failures)
    }
}

/// Rotate a log file of `size_bytes` before appending to it.
pub fn log_should_rotate(size_bytes: u64) -> bool {
    size_bytes > LOG_ROTATE_BYTES
}

/// Command line the supervisor passes to `CreateProcessAsUserW`.
pub fn host_command_line(exe: &str, supervisor_pid: u32) -> String {
    format!("\"{exe}\" --session-host --supervisor-pid {supervisor_pid}")
}

/// Arguments the Service Control Manager appends to the executable path. The `windows-service`
/// crate quotes the path and joins these with single spaces.
pub fn service_launch_arguments() -> Vec<String> {
    vec!["--service".to_string()]
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServiceCommand {
    Install,
    Uninstall,
    Usage,
}

/// `teslapc service <word>`. `args` excludes the program name and starts at `service`.
pub fn parse_service_command(args: &[String]) -> ServiceCommand {
    match args.get(1).map(String::as_str) {
        Some("install") if args.len() == 2 => ServiceCommand::Install,
        Some("uninstall") if args.len() == 2 => ServiceCommand::Uninstall,
        _ => ServiceCommand::Usage,
    }
}

pub const SERVICE_USAGE: &str = "Usage: teslapc service install | teslapc service uninstall";

/// `--supervisor-pid <pid>`. Missing, empty, zero, or non-numeric values give `None`.
pub fn parse_supervisor_pid(args: &[String]) -> Option<u32> {
    let i = args.iter().position(|a| a == "--supervisor-pid")?;
    args.get(i + 1)?.trim().parse::<u32>().ok().filter(|pid| *pid != 0)
}

/// Task names from `schtasks /query /fo csv /v` whose row mentions `teslapc.exe`
/// (case-insensitive). The task name is the second column. Header rows are skipped and
/// names are listed once, in first-seen order.
pub fn tasks_running_teslapc(csv: &str) -> Vec<String> {
    let mut names: Vec<String> = Vec::new();
    for line in csv.lines() {
        let fields = split_csv_line(line);
        if fields.len() < 2 || fields[1] == "TaskName" {
            continue;
        }
        if !fields.iter().any(|f| f.to_ascii_lowercase().contains("teslapc.exe")) {
            continue;
        }
        let name = fields[1].trim().to_string();
        if !name.is_empty() && !names.contains(&name) {
            names.push(name);
        }
    }
    names
}

fn split_csv_line(line: &str) -> Vec<String> {
    let mut fields = Vec::new();
    let mut field = String::new();
    let mut quoted = false;
    let mut chars = line.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '"' if quoted && chars.peek() == Some(&'"') => {
                field.push('"');
                chars.next();
            }
            '"' => quoted = !quoted,
            ',' if !quoted => fields.push(std::mem::take(&mut field)),
            _ => field.push(c),
        }
    }
    fields.push(field);
    fields
}
