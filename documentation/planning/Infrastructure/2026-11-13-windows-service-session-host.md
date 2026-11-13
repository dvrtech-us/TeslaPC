# Plan: Windows service that serves the login screen, lock screen, and UAC

**Date:** 2026-11-13
**Status:** Revised after two adversarial reviews (Claude Opus, Codex). Awaiting owner sign-off. No code written.
Where the "Revisions after review" section at the end differs from the design above, the revisions win.

## Goal

After a reboot with nobody signed in, the car browser can reach TeslaPC, see the Windows
login screen, sign in, and keep using the desktop through lock screens and UAC prompts.
`teslapc service install` becomes the supported way to run on Windows.

Out of scope: authentication (owner decision 2026-11-13: car-only use, no PIN), Linux,
Ctrl+Alt+Del generation (`SendSAS`), multiple simultaneous user sessions, RDP sessions.

## What was observed on the lab laptop (2026-11-13, build from `29fe82d`)

1. `teslapc service install` (`win.rs` `service_install`) passes `binPath= "<exe>" --service`
   to `sc.exe` as one argument. The stored `ImagePath` gets a leading space and the service
   fails to start with event 7000 "The parameter is incorrect".
2. With `ImagePath` corrected by hand, the service starts in session 0. Ports 8080/8443 listen,
   `/stream` returns headers and zero frames. Nothing hands off to the console session.
3. `sc stop TeslaPC` leaves the process running. `service_main` registers a handler that
   returns `NoError` for every control and never stops the body.
4. Run from a logon task as the signed-in user (session 1), capture, audio, and input work.
   That process cannot see the login screen, lock screen, or UAC, and does not exist before
   sign-in.
5. At startup the capture loop went WGC -> DXGI -> GDI and stayed on GDI. The loop in
   `capture_loop.rs` only moves forward through backends until `capture_epoch` changes.

## Design

One binary, two roles. No IPC.

### Role 1: supervisor (`teslapc --service`, session 0, LocalSystem)

Started by the Service Control Manager. It does not bind ports, capture, or play audio. It:

1. Finds the active console session with `WTSGetActiveConsoleSessionId`.
2. Starts one **host** process in that session as SYSTEM:
   `OpenProcessToken(self)` -> `DuplicateTokenEx(TokenPrimary)` ->
   `SetTokenInformation(TokenSessionId, <console id>)` ->
   `CreateProcessAsUserW("<exe>" --session-host, lpDesktop = "winsta0\default",
   CREATE_NO_WINDOW)`, with stdout and stderr redirected to
   `%ProgramData%\TeslaPC\teslapc.log`.
3. Watches the host. If it exits, restart it after a backoff (1 s, 2 s, 4 s ... capped 30 s,
   reset after 60 s of uptime).
4. Accepts `SERVICE_CONTROL_SESSIONCHANGE`. On any session event, and on a 2 s poll as a
   backstop, compares the console session id with the one the host was started in. If it
   changed (logoff, user switch), it terminates the host and starts a new one in the new
   console session. `0xFFFFFFFF` (no console session) means wait and poll.
5. On `SERVICE_CONTROL_STOP` / `SHUTDOWN`: reports `StopPending`, terminates the host, waits
   up to 5 s, reports `Stopped`.

The host is placed in a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so it cannot
outlive the supervisor.

### Role 2: host (`teslapc --session-host`, console session, SYSTEM)

This is today's whole server (`serve.rs` `serve`): HTTP, HTTPS, capture, audio, input,
hotspot, bypass, ACME. Two additions:

1. **Follow the input desktop.** A helper `attach_input_desktop()` calls
   `OpenInputDesktop` and, when the desktop name differs from the one the calling thread is
   on, `SetThreadDesktop`. It is called:
   - at the top of each capture-loop iteration (`ScreenCapture` thread);
   - before each injected event on the input path (`inject_pointer`, `inject_wheel`,
     `inject_key`, `repeat_touch`). Input currently runs on tokio workers; it moves to one
     dedicated `Input` thread fed by a channel so the desktop attachment is stable and no
     runtime thread is re-parented.
   When the capture thread switches desktop it drops the WGC and DXGI sessions and restarts
   backend selection (same effect as a `capture_epoch` bump), because DXGI duplication
   returns `DXGI_ERROR_ACCESS_LOST` across a desktop switch.
   Without SYSTEM (normal `teslapc.exe` run), `OpenInputDesktop` on the secure desktop fails
   and the thread stays where it is. Behavior for a non-service run is unchanged.
2. **Backend choice.** WGC is not expected to work for SYSTEM or on the `Winlogon` desktop.
   The existing order WGC -> DXGI -> GDI already falls through. Add: on the `Winlogon`
   desktop start at DXGI, and do not treat a black frame as a reason to leave DXGI there
   (a dark lock screen is legitimately dark).

`teslapc.exe` with no role flag keeps working exactly as today for development.

### `teslapc service install` / `uninstall`

- Create the service through the `windows-service` crate (`ServiceManager::create_service`)
  instead of `sc.exe`, with `ImagePath` `"<exe>" --service`, auto start, LocalSystem, and
  failure actions "restart after 5 s".
- Add `teslapc service uninstall` (stop, delete).
- Install does not touch scheduled tasks. Removing an old logon task is a documented manual
  step.

## Pure decisions and tests (`teslapc/src/decisions/service.rs`, `tests/protocol.rs`)

- `supervisor_step(host_alive, host_session, console_session, uptime_ms, failures) ->
  Keep | Start{session} | Restart{session} | Wait{ms}` including the backoff table.
- `service_image_path(exe) -> String` (quoted path, one space, `--service`, no leading space).
- `first_capture_backend` gains the desktop name input; `backend_after_frame` gains the
  "stay on DXGI on Winlogon" rule.
- `desktop_changed(current, input) -> bool`.

OS calls stay in `win.rs`. Nothing in this plan can be executed by `cargo test` on a
non-Windows host beyond these decisions.

## Verification on the lab laptop (must be run, in this order)

1. `teslapc service install` then `sc start TeslaPC`: service `RUNNING`, host process present
   in the console session, `/stream` delivers frames.
2. `sc stop TeslaPC`: both processes gone within 5 s.
3. Lock the laptop (Win+L) while streaming: stream shows the lock screen; a click and typed
   password unlock it.
4. Trigger a UAC prompt: stream shows it; Yes can be clicked.
5. Reboot, do not sign in: stream shows the login screen; sign in from the browser.
6. Sign out: the host restarts in the new console session and the stream resumes on reload.
7. Audio after sign-in (`/ws/audio` format `float`, sound present).
8. Settings page hotspot on/off, and the bypass address `100.64.0.1`.
9. Media playback from the file browser.
10. Kill the host process: supervisor restarts it.

## Risks and unknowns

| # | Risk | Handling |
|---|------|----------|
| 1 | WASAPI loopback as SYSTEM in the user session may not see the user's default endpoint | Verify step 7. If it fails, audio capture moves to a second helper started with the user's token (`WTSQueryUserToken`) |
| 2 | Hotspot control (`NetworkOperatorTetheringManager` through PowerShell) may fail as SYSTEM | Verify step 8. Fallback as in risk 1 |
| 3 | `InjectTouchInput` may not work on the `Winlogon` desktop | On `Winlogon`, use `SendInput` mouse events regardless of `TESLAPC_INPUT_MODE` |
| 4 | SYSTEM cannot see the user's mapped network drives; a video root on a mapped drive breaks | Document. UNC paths with machine access still work |
| 5 | ffmpeg and the VLC fallback run as SYSTEM | Accepted with the no-auth decision. Documented |
| 6 | Host restart on logoff drops the stream for a few seconds | Accepted. Browser reload reconnects |
| 7 | The lock screen needs a key or click to show the password box; Ctrl+Alt+Del policies are not handled | Out of scope. Documented |
| 8 | Any reachable client can now drive the login screen and elevation prompts | Owner accepted (car-only use) |
| 9 | Smart App Control blocks unsigned builds; it is now off on the lab laptop | Documented as a requirement for unsigned builds |

## Documentation to update with the code

- `documentation/features/app/control-panel/` (service behavior, log file path).
- New feature `documentation/features/infrastructure/windows-service/` with `baseline.md`
  and a first trail entry.
- `documentation/features/streaming/screen-capture/` and `server/input-control/` (desktop
  following, backend rule on `Winlogon`, input thread).
- `AGENTS.md`, `CLAUDE.md`, `documentation/README.md` Feature Map, "Current Known Gaps".

## Work split

1. Decisions + tests (`decisions/service.rs`, capture rule changes).
2. Supervisor and service install/uninstall in `win.rs` / `serve.rs`.
3. Desktop following for capture and the input thread.
4. Lab verification steps 1-10, then docs.

## Revisions after review (2026-11-13)

Both reviewers confirmed the two-process design, the token recipe, and observations 1, 3,
and 5. The changes below replace the matching parts of the design.

### Bugs in today's code that the reviews found (fix first, independent of the service)

- **Touch deadlock.** `end_contact()` and `repeat_touch()` in `win.rs` hold `TOUCH_XY` and
  call `inject_touch()`, which locks `TOUCH_XY` again. With `TESLAPC_INPUT_MODE=touch` (the
  default) and a held contact this blocks or panics. Release the guard before injecting.
- **Touch probe disables all input.** `serve.rs` sets `input_enabled` from `probe_input()`,
  which only tests `InitializeTouchInjection`. A touch failure turns off mouse and keyboard.
  Split into "touch available" and "input available".
- **DXGI leaves on a static screen.** `grab_dxgi` returns `Err` after eight 100 ms
  `DXGI_ERROR_WAIT_TIMEOUT` results and on `DXGI_ERROR_ACCESS_LOST`; the loop then moves to
  GDI for good. Give `grab` three outcomes: frame, no new frame (re-send the last JPEG, stay),
  and recreate (reopen DXGI in place, stay). Only a failed reopen steps down to GDI.
- **Audio never retries.** `run_loopback` ends its thread on any error, including a missing
  endpoint at boot and `AUDCLNT_E_DEVICE_INVALIDATED`. Wrap it in a retry loop with backoff
  that re-reads the default endpoint.
- **DPI.** `SetProcessDPIAware` is only called inside `grab_gdi`. Call
  `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` once at startup so touch coordinates
  match on every backend.

### Supervisor

- **Logging:** handles cannot be inherited across sessions. `bInheritHandles = FALSE`; the
  host opens `%ProgramData%\TeslaPC\teslapc.log` itself before anything else, and rotates it
  at 5 MB (one `.1` file). Per-frame capture lines move to `debug` level.
- **Job object:** a fresh job per host. Create the host `CREATE_SUSPENDED`, assign, check the
  result, resume. The supervisor stays outside the job. Also pass the supervisor PID to the
  host; the host exits when that process dies.
- **One loop:** control callbacks (stop, shutdown, session change), the 2 s poll, and host
  exit all signal one supervisor loop. A stopping state blocks further launches and makes
  the backoff wait interruptible. After `TerminateProcess`, wait for exit before launching.
- **Session rules:** `0xFFFFFFFF` means keep the current host and poll. Re-check the console
  session after each launch. A logoff event for the host's session restarts the host even
  when the id does not change.
- **Readiness:** the host is ready when 8080 is bound. A bind failure is a distinct exit code
  and is logged by the supervisor as "port in use", with a 30 s retry.
- **Old logon task:** `service install` warns when a scheduled task runs a `teslapc.exe`.
  It does not change tasks.
- **Cleanup:** delete `paths::windows_service_create_args`; parse `service install|uninstall`.

### Host

- **Capture, phase 1:** in `--session-host` mode skip WGC. Order is DXGI -> GDI, with the
  DXGI outcomes above. Drop the "stay on DXGI when black on Winlogon" rule.
- **Desktop following:** tear down the capture session before `SetThreadDesktop`. Open with
  `MAXIMUM_ALLOWED`. Close a handle that was opened and not switched to; keep the attached
  handle; close the previous one after switching; never close the handle from
  `GetThreadDesktop`. A failed `OpenInputDesktop` means stay. `ERROR_BUSY` restarts the
  capture thread.
- **Input thread:** every input call goes through it, including `end_contact`, the touch
  repeat, `InitializeTouchInjection`, and `SetCursorPos`. Active contacts are cancelled on a
  desktop switch. `SendInput` return counts are checked and logged at `debug`.

### Order of work (changed)

1. Fix the five existing bugs above. Deploy to the lab laptop from the logon task and retest.
2. Supervisor, service install/uninstall, host launch with **no** desktop following.
3. **Gate:** on the lab laptop, reboot without signing in and check from the car over the
   hotspot: is the PC reachable, does the hotspot come up as SYSTEM before login, does audio
   work after sign-in without a logoff. If the hotspot cannot start before login, stop and
   decide with the owner; a user-token helper cannot exist before login.
4. Desktop following for capture and input.
5. Full verification, then docs.

### Verification additions

Host runs as SYSTEM in the console session (`tasklist /v`); capture backend named in the log
at each step; lock screen left idle for more than 10 s still streams; unlock back to the
desktop; rapid lock/UAC cycles; resolution change through `/display`; sleep and resume;
kill the supervisor and confirm the host and its ffmpeg/VLC/PowerShell children exit; stop
during backoff; old logon task still present; touch with display scaling above 100%; held
touch and disconnect during a touch; default audio device change. Each check asserts changing
decoded frames and working input, not process presence.
