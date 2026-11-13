# Windows service becomes a supervisor; two log files

- Date: 2026-11-13
- Feature: control-panel
- Related code: `teslapc/src/serve.rs`, `teslapc/src/win_service.rs`

## Decision

`--service` on Windows no longer runs `serve` in session 0. It supervises a `--session-host`
process in the console session (see `infrastructure/windows-service`). Because neither process
has a visible console, the host redirects stdout/stderr to `%ProgramData%\TeslaPC\teslapc.log`
and the supervisor writes `%ProgramData%\TeslaPC\teslapc-service.log`. `service uninstall` was
added and `service <other>` now prints usage instead of installing.
