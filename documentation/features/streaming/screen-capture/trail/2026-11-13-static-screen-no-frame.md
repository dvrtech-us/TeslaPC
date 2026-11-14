# Static screen is "no new frame", not a capture failure

- Date: 2026-11-13
- Feature: screen-capture

## Problem

WGC and DXGI Desktop Duplication only deliver a frame when the screen changes. `grab_dxgi` gave up after eight 100 ms `DXGI_ERROR_WAIT_TIMEOUT` results and `grab_wgc` after 20 `TryGetNextFrame` calls 20 ms apart (field log: `WGC TryGetNextFrame: The operation completed successfully. (0x00000000)`). Both dropped the session and returned `Err`. `capture_loop` treats any `Err` as a failure and `backend_after_frame` only moves WGC -> DXGI -> GDI, so a still screen ended on GDI within about a second and never came back. `DXGI_ERROR_ACCESS_LOST` (desktop switch, mode change, sleep/resume) also returned `Err`.

## Decision

- `platform::grab` returns `Result<GrabOutcome, String>`; `GrabOutcome` is `Frame(Grab)` or `NoFrame`. Linux always returns `Frame`.
- DXGI: one `AcquireNextFrame(100)` per grab. A wait timeout is `NoFrame` and keeps the session. `ACCESS_LOST`, `INVALID_CALL`, and `SESSION_DISCONNECTED` reopen the duplication once in the same call; a failed reopen is `Err`. `ReleaseFrame` always follows a successful acquire.
- WGC: one `TryGetNextFrame` per grab. A null frame surfaces from the `windows` crate (0.61) as `Error::empty()` with an `S_OK` code, so `err.code().is_ok()` is matched (not the error text) and returns `NoFrame`. Real errors drop the session and return `Err`.
- Capture loop: `decisions::capture::no_frame_action` returns `Wait`, `Republish`, or `Fail`. On `NoFrame` the loop stays on the backend and republishes the stored JPEG (no re-encode) once per second. A backend with no real frame since selection is failed after 2 s so the chain still advances.
- The black-frame handoff is unchanged for real frames.
- The per-frame `[Capture] WGC frame` and `[Capture] DXGI frame` lines moved from `win_capture.rs` (no access to `App`) into `capture_loop` and use `App::log("debug", ...)`, the gate that applies `TESLAPC_LOG_LEVEL`.

## Constants

- `DXGI_ACQUIRE_TIMEOUT_MS` = `100` (`win_capture.rs`)
- `REPUBLISH_INTERVAL_MS` = `1000`
- `NO_FRAME_FAIL_MS` = `2000`
- Loop pace after `NoFrame` is `FRAME_INTERVAL_MS` (`33`)

## Files

- `teslapc/src/platform.rs`, `teslapc/src/win.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/linux.rs`
- `teslapc/src/capture_loop.rs`, `teslapc/src/decisions/capture.rs`, `teslapc/tests/protocol.rs`
