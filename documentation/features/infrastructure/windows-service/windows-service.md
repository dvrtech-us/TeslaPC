# Windows Service (supervisor and session host)

On Windows the supported unattended setup is the `TeslaPC` service. One binary runs in three
roles:

| Command | Role | Session / account |
|---------|------|-------------------|
| `teslapc.exe` (no role flag) | The whole server, as before (`serve.rs` `serve`) | Whoever started it |
| `teslapc.exe --service` | **Supervisor only.** Started by the Service Control Manager. Binds no port, captures nothing, plays no audio | Session 0, LocalSystem |
| `teslapc.exe --session-host --supervisor-pid <pid>` | The whole server, started by the supervisor | Active console session, SYSTEM |

Code: `teslapc/src/win_service.rs` (OS calls), `teslapc/src/decisions/service.rs` (pure
decisions, tested in `teslapc/tests/protocol.rs`), `teslapc/src/serve.rs` (argument handling),
`teslapc/src/paths.rs` (`host_log_file`, `service_log_file`).

The session host follows the input desktop: its capture thread and its single `Input` thread
re-attach to whichever desktop receives input, so the stream shows and controls the sign-in
screen, the lock screen, and UAC prompts (the `Winlogon` secure desktop) and returns to the
user's desktop (`Default`) afterwards. See "Desktop following" below. This was written and
compiled on macOS for `x86_64-pc-windows-msvc`; it has not yet run on Windows.

## Install and uninstall

`teslapc service install` (elevated prompt):

- Uses `windows_service::service_manager::ServiceManager` (no `sc.exe`). Name and display name
  `TeslaPC` (`SERVICE_NAME`, `SERVICE_DISPLAY_NAME`), own process, `AutoStart`,
  `ServiceErrorControl::Normal`, LocalSystem (`account_name: None`).
- Executable is `std::env::current_exe()`; launch arguments are `service_launch_arguments()`
  (`["--service"]`). The `windows-service` crate quotes the path only when it contains a space,
  tab, newline, or quote, and joins the arguments with one space, so `ImagePath` has no leading
  space.
- If `CreateServiceW` fails with `ERROR_SERVICE_EXISTS`, the service is opened and
  `change_config` rewrites the same settings ("Updated existing service").
  `ERROR_SERVICE_MARKED_FOR_DELETE` prints a close-the-Services-console/reboot message.
- Sets the description, then failure actions: three `Restart` actions with
  `SERVICE_RESTART_DELAY_MS` (`5000`) delay, reset period 24 h. A failure here is printed and
  the install still succeeds.
- Does not start the service. It prints `Start it with sc start TeslaPC or reboot.`
- Then runs `schtasks /query /fo csv /v` and passes stdout to `tasks_running_teslapc`, which
  returns the TaskName (second column) of every row that contains `teslapc.exe`
  (case-insensitive), skipping repeated header rows. If any are found it prints one warning
  line naming them: they must be disabled because they hold ports 8080/8443. Tasks are never
  changed. If `schtasks` cannot run or exits non-zero, nothing is printed.
- Any SCM failure (most often "Access is denied" without elevation) prints the Win32 error
  text and returns `1`.

`teslapc service uninstall`:

- Opens the service with `QUERY_STATUS | STOP | DELETE`. Not installed
  (`ERROR_SERVICE_DOES_NOT_EXIST`) prints "Nothing to remove" and returns `0`.
- If it is not stopped, sends STOP (unless already `StopPending`) and polls every 250 ms for up
  to `UNINSTALL_STOP_WAIT_MS` (`10000`). If it does not stop in time it deletes anyway (the SCM
  removes it once it stops).
- `DeleteService`. `ERROR_SERVICE_MARKED_FOR_DELETE` is reported and returns `0`; other errors
  return `1`.

`teslapc service` with any other word, or extra words, prints `SERVICE_USAGE` and returns `2`
(`parse_service_command`). On Linux `service install` still writes the systemd unit;
`service uninstall` prints the `systemctl disable --now` command and returns `1`.

## Supervisor (`--service`)

`serve::run` calls `platform::run_as_service()` → `win_service::run_as_service()`, which calls
`service_dispatcher::start("TeslaPC", ...)`. If the process was not started by the SCM, it
prints `[Service] Not started by the Service Control Manager (...)` and `run` falls through
to a normal server run (unchanged behavior). When the dispatcher returns, the process exits `0`.

`service_main`:

1. Creates the `Signals` mailbox (a `Mutex<Vec<Signal>>` plus an auto-reset event from
   `CreateEventW`). Registers the control handler. The handler only pushes a signal and sets
   the event: STOP and SHUTDOWN → `Stop`; SESSION_CHANGE with `WTS_SESSION_LOGOFF` →
   `Logoff(session id)`; any other SESSION_CHANGE → `SessionEvent`; INTERROGATE → `NoError`;
   everything else `NotImplemented`.
2. Reports `StartPending` (wait hint 3 s), then `Running`, accepting
   `STOP | SHUTDOWN | SESSION_CHANGE`.
3. Runs `supervise` (one loop owns all state), then reports `Stopped`.

Each loop iteration:

1. Drains signals. `Stop` sets `stopping`. `Logoff(id)` sets `logoff_for_host_session` for this
   iteration when `id` is the host's session.
2. If the host exited (`WaitForSingleObject(process, 0)`), reads `GetExitCodeProcess`, updates
   `failures` with `next_failure_count`, logs the exit and the restart delay
   (`restart_delay_ms`), records the exit time, and drops the host (closing its job).
3. Reads `WTSGetActiveConsoleSessionId` and logs when it changes.
4. Calls `supervisor_step` and performs the result:
   - `Stop`: report `StopPending` (wait hint `HOST_TERMINATE_WAIT_MS + 2000` = 7 s), terminate
     the host, return.
   - `Keep`: `WaitForMultipleObjects([event, host process], SUPERVISOR_POLL_MS)`.
   - `Wait { ms }`: wait on the event for `min(ms, SUPERVISOR_POLL_MS)`. The step is
     recomputed on every wake, so backoff is interruptible by STOP and by session events.
   - `Restart { session }`: terminate the host (reason: logoff or session change), then launch.
   - `Start { session }`: launch.

`supervisor_step` (`decisions/service.rs`):

| Condition (checked in order) | Result |
|------------------------------|--------|
| `stopping` | `Stop` |
| host alive, console `0xFFFFFFFF` (`NO_CONSOLE_SESSION`) | `Keep` |
| host alive, logoff for its session, or its session ≠ console | `Restart { console }` |
| host alive otherwise | `Keep` |
| no host, console `0xFFFFFFFF` | `Wait { 2000 }` |
| no host, nothing has exited yet | `Start { console }` |
| no host, `since_exit_ms ≥ restart_delay_ms(code, failures)` | `Start { console }` |
| no host otherwise | `Wait { delay − since_exit_ms }` |

Backoff: `next_failure_count` returns `1` when the host ran at least
`HOST_HEALTHY_UPTIME_MS` (`60000`), otherwise previous + 1. `host_backoff_ms(n)` is
1 s, 2 s, 4 s, 8 s, 16 s, then `HOST_BACKOFF_MAX_MS` (`30000`). Exit code
`EXIT_PORT_IN_USE` (`3`) always waits `PORT_IN_USE_WAIT_MS` (`30000`) and logs
`Port in use: the host could not bind 8080 ...`. A failed launch is recorded as exit code
`EXIT_LAUNCH_FAILED` (`0xFFFFFFFF`, supervisor-internal) with uptime 0 and backs off the same way.

Terminating a host: `TerminateProcess(exit 1)`, `WaitForSingleObject` up to
`HOST_TERMINATE_WAIT_MS` (`5000`), log if it did not exit, then close the process and job
handles. Closing the job (kill-on-close) ends any child the host left (ffmpeg, VLC,
PowerShell).

### Host launch (`launch_host`)

1. `OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID | TOKEN_IMPERSONATE)`.
2. `DuplicateTokenEx(..., SecurityImpersonation, TokenPrimary)` with the same rights.
3. `SetTokenInformation(TokenSessionId, <console session>)`.
4. `CreateEnvironmentBlock(token, bInherit = FALSE)`. On failure it is logged and the host
   inherits the service environment (no `CREATE_UNICODE_ENVIRONMENT`). The block is destroyed
   after `CreateProcessAsUserW` returns.
5. `CreateProcessAsUserW`: application name = the exe path, command line
   `host_command_line(exe, own pid)` = `"<exe>" --session-host --supervisor-pid <pid>`,
   `lpDesktop = "winsta0\default"`, `bInheritHandles = FALSE`, flags
   `CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT`, current directory = the
   exe's folder. `CREATE_BREAKAWAY_FROM_JOB` is not used (the supervisor is not in a job, and
   nested jobs are allowed).
6. A fresh job per host: `CreateJobObjectW`, `SetInformationJobObject` with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `AssignProcessToJobObject`. A failure at any of these
   is logged and the host runs without a job (the `--supervisor-pid` watch is the fallback).
7. `ResumeThread`. On failure the process is terminated and the launch counts as failed.

All handles are owned by `OwnedHandle` (closes on drop) and closed on every path.

## Session host (`--session-host`)

In `serve::run`, before `.env` is loaded or anything prints:

1. `platform::redirect_host_log()` opens `%ProgramData%\TeslaPC\teslapc.log` (append; over
   `LOG_ROTATE_BYTES` = 5 MiB at open it is renamed to `teslapc.log.1`, replacing an older
   `.1`) and calls `SetStdHandle` for `STD_OUTPUT_HANDLE` and `STD_ERROR_HANDLE` (a second,
   cloned file handle). Rust's Windows `Stdout`/`Stderr` call `GetStdHandle` on every write
   (`library/std/src/sys/stdio/windows.rs`, Rust 1.98), so every `println!` after this lands in
   the file. A header line with the time, version, and pid is written. If the file cannot be
   opened, a line goes to the service log and output is lost.
2. `capture_loop::session_host_mode()`: capture starts at DXGI (`[Capture] Session host: WGC
   skipped, starting at DXGI.`) and the capture thread follows the input desktop. DXGI → GDI
   is unchanged.
3. With `--supervisor-pid <pid>`, `exit_with_supervisor` opens the supervisor with
   `PROCESS_SYNCHRONIZE`; a `SupervisorWatch` thread waits on it and calls
   `std::process::exit(0)` when it exits. If `OpenProcess` reports `ERROR_INVALID_PARAMETER`
   (no such process) the host exits immediately; any other error is logged and the host keeps
   running under the job.
4. Then the normal `serve`. If binding `8080` fails, the host prints the error and exits with
   `EXIT_PORT_IN_USE` (`3`). A normal run (no role flag) still exits `1` on that failure.

In `serve`, before the input probes, `platform::start_input_thread` starts the `Input` thread
(below). Firewall, hotspot, bypass, ACME, and audio run exactly as in a normal run, as SYSTEM in
the console session.

## Desktop following (session host only)

A normal `teslapc.exe` run never calls any of this: `capture_loop` only creates a follower
when `session_host_mode()` was called, and `win::start_input_thread` is only called for
`--session-host`.

### Attachment helper (`win_desktop.rs` `DesktopAttachment`)

One per thread, created on that thread (`platform::DesktopFollower` wraps it; off Windows every
check is `Stay`).

- At creation: `GetThreadDesktop(GetCurrentThreadId())` is stored as the original handle (never
  closed) and its `UOI_NAME` is read (`[Desktop] <role> starts on <name>`).
- `check(force, before_switch)` runs only when `desktop_check_due(ms_since_last, force,
  last_switch_failed)` (`decisions/desktop.rs`) says so: always on the first call; otherwise
  every `DESKTOP_CHECK_INTERVAL_MS` (`200`) ms, or at once when `force` is set (a failed grab
  or injection); after a failed `SetThreadDesktop` not before `DESKTOP_RETRY_AFTER_FAILURE_MS`
  (`2000`) ms, even when forced.
- `OpenInputDesktop(0, FALSE, MAXIMUM_ALLOWED)` (`0x02000000`, passed as
  `DESKTOP_ACCESS_FLAGS`). A failure means stay (it fails briefly during a switch).
- The name comes from `GetUserObjectInformationW(UOI_NAME)`. `desktop_switch_needed(attached,
  input)` compares names without case; an unknown attached name always switches, an empty input
  name never does. Same name: the new handle is closed, nothing changes.
- Different name: `before_switch` runs (the caller tears down what is bound to the old
  desktop), then `SetThreadDesktop(new)`. Success: the new handle becomes the attached one, the
  previously attached handle is closed if this helper opened it, and
  `[Desktop] <role> attached to <name>` is printed. Failure: the new handle is closed, the
  thread stays, and the result is `Busy` for `ERROR_BUSY` (the thread owns windows or hooks),
  otherwise `Failed`.
- Errors print `[Desktop] <role>: <error>` once per distinct text (`desktop_error_is_new`);
  the remembered text is cleared after a successful check, so a recurring failure is printed
  again.
- On drop the thread returns to the original desktop and the attached handle is closed.

### Capture thread

See `documentation/features/streaming/screen-capture/`. In short: the check runs on each
streaming tick before the grab; a switch drops the thread's DXGI/WGC sessions before
`SetThreadDesktop`, restarts at DXGI, and clears the stored JPEG; DXGI errors right after a
switch are retried up to 5 times, 200 ms apart; `Busy` replaces the capture thread with a new
one.

### Input thread

See `documentation/features/server/input-control/`. In short: every injection, including
`InitializeTouchInjection` and `SetCursorPos`, is queued to one `Input` thread that checks the
desktop before each event; off `Default` every pointer event is a mouse event.

### Still to confirm on Windows

Nothing in this section has run on Windows yet. In order of how likely it is to be wrong:
whether DXGI duplication opens on `Winlogon` as SYSTEM (otherwise GDI is used there), whether
the capture thread can `SetThreadDesktop` at all (a hidden window created by D3D/COM would make
it `Busy` and trigger restarts), whether the restarted thread's handle hand-off closes cleanly,
whether `SendInput` clicks and typing reach the sign-in and UAC screens, whether touch
injection still works on `Default` after a trip to `Winlogon`, and whether
`InitializeTouchInjection` succeeds when the host starts on the sign-in screen (it is retried
when the `Input` thread first reaches `Default`).

## Logs

| File | Writer | Content |
|------|--------|---------|
| `%ProgramData%\TeslaPC\teslapc.log` (+ `.1`) | session host | everything the server prints (stdout and stderr) |
| `%ProgramData%\TeslaPC\teslapc-service.log` (+ `.1`) | supervisor (`slog`), and the host if it cannot open its own log | timestamped `[Service]` lines: start, console session changes, every launch (session, pid), every exit (code, uptime), restart reason and delay, every Win32 failure with its error text, stop |

Both rotate at open when larger than 5 MiB (`log_should_rotate`). The supervisor opens the
file per line.
