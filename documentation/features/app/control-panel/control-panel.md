# Process Control

There is no WinForms dashboard. `teslapc` logs to stdout. `teslapc service install` registers it.

## Windows service

Full detail: [windows-service](../../infrastructure/windows-service/windows-service.md).

- `teslapc service install` creates (or updates) service `TeslaPC` through the SCM API with `"<exe>" --service`, auto start, LocalSystem, and restart-after-5-s failure actions. It warns when a scheduled task runs `teslapc.exe`. `teslapc service uninstall` stops and deletes it. Any other `service` word prints usage and exits `2`.
- `--service` started by the SCM is a supervisor only: no ports, capture, or audio. It starts `teslapc.exe --session-host --supervisor-pid <pid>` as SYSTEM in the active console session and restarts it on exit, logoff, or console session change. If the process was not started by the SCM, it logs that and runs the normal server in the console.
- `--session-host` is the normal server with its stdout and stderr redirected to a file.

## Log files (Windows service)

| File | Content |
|------|---------|
| `%ProgramData%\TeslaPC\teslapc.log` | session host output (what a console run prints) |
| `%ProgramData%\TeslaPC\teslapc-service.log` | supervisor: launches, exits, restart reasons, Win32 errors |

Each is renamed to `<name>.1` (replacing an older `.1`) when it is over 5 MiB at open. A console run (no role flag) still logs to stdout only.

## Linux service

`linux_systemd_unit` is a `Type=simple` unit. `service install` writes `/etc/systemd/system/teslapc.service` and runs `systemctl daemon-reload`. A permission error is printed. The unit is not required for `teslapc` itself to serve HTTP.

## Hotspot control

On and off live on the Settings page (`POST /hotspot`), not in a desktop window.
