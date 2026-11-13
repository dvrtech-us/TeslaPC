# Supervisor service and console-session host

- Date: 2026-11-13
- Feature: windows-service
- Plan: `documentation/planning/Infrastructure/2026-11-13-windows-service-session-host.md` (step 2)
- Related code: `teslapc/src/win_service.rs`, `teslapc/src/decisions/service.rs`, `teslapc/src/serve.rs`, `teslapc/src/capture_loop.rs`, `teslapc/src/paths.rs`, `teslapc/src/platform.rs`

## Context

Lab observations on `29fe82d`: `service install` passed `binPath= "<exe>" --service` to
`sc.exe` as one argument, so `ImagePath` had a leading space and the service failed with event
7000. With the path fixed by hand the service ran the whole server in session 0, where capture
returns no frames. `sc stop` did nothing because the control handler returned `NoError` and
never stopped `serve`.

## Decisions

- **Two processes, one binary.** `--service` is a supervisor only. It launches
  `--session-host` as SYSTEM in the console session with a duplicated own token whose
  `TokenSessionId` is set, on `winsta0\default`. No IPC.
- **One loop owns state.** The control handler only queues a signal and sets an event. The loop
  waits on that event, the host process handle, and a 2 s poll, then calls the pure
  `supervisor_step`. A STOP sets a stopping flag that wins over every other input, so a backoff
  wait never delays a stop by more than one wake.
- **Session rules.** `0xFFFFFFFF` keeps the host. A logoff for the host's session restarts it
  even when the console id does not change. After a launch the loop re-evaluates immediately.
- **Job per host.** Created suspended, assigned to a fresh kill-on-close job, then resumed. Job
  failure is logged, not fatal: the host also watches `--supervisor-pid` and exits with it.
  `CREATE_BREAKAWAY_FROM_JOB` is not used; the supervisor is not in a job and nested jobs are
  supported.
- **Logging without inherited handles.** Handles cannot cross sessions, so
  `bInheritHandles = FALSE` and the host opens its own log and points `STD_OUTPUT_HANDLE` /
  `STD_ERROR_HANDLE` at it with `SetStdHandle`. Rust 1.98's Windows stdio calls `GetStdHandle`
  on every write, so existing `println!` calls need no change. The supervisor appends to a
  separate `teslapc-service.log`. Both rotate once at 5 MiB.
- **Port in use is visible.** The host exits `3` when 8080 cannot be bound; the supervisor logs
  "Port in use" and waits 30 s instead of the short backoff. `service install` warns about
  scheduled tasks that run `teslapc.exe` (the old logon-task setup) and does not change them.
- **Install through the SCM API.** `windows-service` `ServiceManager::create_service`, or
  `change_config` when the service exists. Failure actions (restart after 5 s) are set through
  `update_failure_actions`, which windows-service 0.8.1 supports. `paths::windows_service_create_args`
  and the `sc.exe` call are deleted. `service uninstall` added.
- **Session host skips WGC** and starts at DXGI, as with `TESLAPC_NO_WGC=1`.
- **Module.** The service code moved out of `win.rs` into `win_service.rs` to keep `win.rs`
  about capture, input, and network.

## Not in this step

Desktop following (`OpenInputDesktop` / `SetThreadDesktop`) and the dedicated input thread
(plan step 4). Until then the host only sees and drives the signed-in user's desktop.

## Consequences

- Positive: `sc start` / `sc stop` work; the host restarts on crash, logoff, or user switch.
- Negative: the server, ffmpeg, VLC, and PowerShell children run as SYSTEM in the user's
  session; mapped drives of the user are not visible to it. Nothing here has run on Windows
  yet; it compiles for `x86_64-pc-windows-msvc` and the decisions are covered by `cargo test`.
