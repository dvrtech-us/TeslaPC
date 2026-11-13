# Windows Service — Behavior Baseline

- `teslapc service install` creates or updates service `TeslaPC` through the SCM API: own process, auto start, LocalSystem, `ImagePath` = quoted-if-needed exe + ` --service` with no leading space. Failure actions restart after 5 s. Not elevated → error text, exit `1`.
- `install` never starts the service and never changes scheduled tasks; it warns once, naming every task whose row contains `teslapc.exe`.
- `teslapc service uninstall` stops (up to 10 s), then deletes. Not installed → exit `0`.
- `teslapc service <anything else>` prints usage and exits `2`.
- `--service` started by the SCM binds no port and runs no capture, audio, or input. Not started by the SCM → logs and runs the normal server.
- The supervisor keeps exactly one host, started as SYSTEM in `WTSGetActiveConsoleSessionId()` on `winsta0\default` with `"<exe>" --session-host --supervisor-pid <pid>`.
- Console id `0xFFFFFFFF` keeps the current host. A different console id, or a logoff for the host's session (even with the same id), terminates the host, waits up to 5 s, and starts one in the current console session.
- Host restart delay: 1, 2, 4, 8, 16, 30, 30 ... s; a run of 60 s or more resets it to 1 s. Exit code `3` (port in use) waits 30 s and is logged as "Port in use".
- STOP/SHUTDOWN: no further launch, `StopPending`, host terminated (5 s bound), `Stopped`. STOP during a backoff wait is handled within one wake (at most 2 s).
- Each host is in its own kill-on-close job; closing it ends the host and its children. If the job cannot be set up, the host exits when the supervisor pid exits.
- `--session-host` writes stdout and stderr to `%ProgramData%\TeslaPC\teslapc.log`, starts capture at DXGI, and exits `3` when 8080 cannot be bound.
- Supervisor lines go to `%ProgramData%\TeslaPC\teslapc-service.log`. Both logs rotate to `.1` at open when over 5 MiB.
- `--session-host` only: the capture thread and the one `Input` thread follow the input desktop (`OpenInputDesktop(MAXIMUM_ALLOWED)` + `SetThreadDesktop` when the `UOI_NAME` differs, case-insensitive). A check runs at most every 200 ms per thread, at once after a failed grab or injection, and not within 2 s of a failed `SetThreadDesktop`. A failed `OpenInputDesktop` keeps the current desktop. Each switch prints `[Desktop] <role> attached to <name>` once.
- The handle from `GetThreadDesktop` is never closed. Every handle a thread opened is closed when it is not switched to, after the thread leaves it, or (capture restart) by the replacement thread.
- A normal run (no role flag) never opens or switches desktops.
