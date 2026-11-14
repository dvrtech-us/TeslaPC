# Process Control — Behavior Baseline

- The process has no window and no WinForms log tab.
- Logs are stdout lines. Under the Windows service the host writes them to `%ProgramData%\TeslaPC\teslapc.log` and the supervisor to `%ProgramData%\TeslaPC\teslapc-service.log`.
- `teslapc service install` is the non-interactive install entry on Windows and Linux. `teslapc service uninstall` removes the Windows service.
- A denied install is logged and exits non-zero. HTTP does not depend on the service being registered.
- On Windows `--service` (started by the SCM) is a supervisor that runs the server as `--session-host` in the console session. On Linux, and when not started by the SCM, `--service` runs the same server.
