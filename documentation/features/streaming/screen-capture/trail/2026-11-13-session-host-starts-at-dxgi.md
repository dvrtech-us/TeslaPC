# Session host starts at DXGI

- Date: 2026-11-13
- Feature: screen-capture
- Related code: `teslapc/src/capture_loop.rs`, `teslapc/src/serve.rs`

## Decision

WGC is not expected to work for a SYSTEM process started by the service supervisor, so
`--session-host` calls `capture_loop::skip_wgc()` before capture starts. `loop_forever` then
behaves as with `TESLAPC_NO_WGC=1` (start at DXGI, then GDI) and logs which reason applied.
The environment variable is not set, so child processes do not inherit it.
