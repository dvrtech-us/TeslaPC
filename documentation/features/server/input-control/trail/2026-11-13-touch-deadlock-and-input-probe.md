# Touch deadlock, input probe split, and DPI awareness

- Date: 2026-11-13
- Feature: input-control
- Related code: `teslapc/src/win.rs`, `teslapc/src/platform.rs`, `teslapc/src/serve.rs`, `teslapc/src/routes.rs`, `teslapc/src/state.rs`, `teslapc/src/decisions/input.rs`

## Problem

- `end_contact` and `repeat_touch` held the `TOUCH_XY` mutex while calling `inject_touch`, which locks it again. `std::sync::Mutex` is not re-entrant, so any active touch contact deadlocked or panicked. Touch is the default mode.
- `serve` stored `probe_input()` in `input_enabled`. On Windows that only reported `InitializeTouchInjection`, and `routes.rs` drops every pointer, wheel, and key event when `input_enabled` is false, so a touch failure also disabled mouse and keyboard.
- `SetProcessDPIAware` ran only inside `grab_gdi`. Until GDI capture ran, `primary_size()` returned logical pixels while `InjectTouchInput` takes physical pixels, so touches landed in the wrong place above 100% display scaling.

## Decision

- Copy `(x, y, down)` out of `TOUCH_XY` and drop the guard before `inject_touch`. `end_contact` then sets `down` false regardless of the injection result.
- Two facts. `platform::probe_input` (mouse and keyboard) is `true` on Windows because `SendInput` needs no probe, and is the uinput open on Linux (unchanged: a failure still means no input). `platform::probe_touch` is Windows `InitializeTouchInjection` and `true` on Linux. The result is `app.inner.touch_enabled`.
- `decisions::input::pointer_path(kind, touch_mode, touch_available)` returns `PointerPath::Touch` only for touch mode, touch available, and a kind other than `rightclick`. Otherwise `Mouse`. `routes::handle_input_text` uses it. One log line at startup says touch is unavailable and the mouse is used.
- `platform::init_dpi_awareness` runs first in `serve`: `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`, falling back to `SetProcessDPIAware()`. The call inside `grab_gdi` is left as it was.

## Constants

- `TOUCH_REPEAT_MS` stays `100`.
- DPI context: `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`.

## Files

- `teslapc/src/win.rs`: `end_contact`, `repeat_touch`, `probe_touch`, `init_dpi_awareness`.
- `teslapc/src/platform.rs`: `probe_input`, `probe_touch`, `init_dpi_awareness`.
- `teslapc/src/serve.rs`, `teslapc/src/routes.rs`, `teslapc/src/state.rs`: `touch_enabled`, startup log, path choice.
- `teslapc/src/decisions/input.rs`, `teslapc/tests/protocol.rs`: `pointer_path` and its test.
