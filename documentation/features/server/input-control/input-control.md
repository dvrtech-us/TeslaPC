# Input Control

Pointer, wheel, and keyboard JSON on the input WebSocket is turned into host input. The mapping lives in `teslapc/src/decisions/input.rs`. Windows injection is `win.rs`. Linux injection is `linux.rs` (`/dev/uinput`).

## User flow

1. The page opens a WebSocket to `/ws/input` (or any path other than `/ws/audio`).
2. Touch is the default. `TESLAPC_INPUT_MODE=mouse` or `POST /config` `inputMode=mouse` uses the cursor.
3. Closing the socket ends the touch contact.

## Messages

```json
{ "Type": "move|down|up|rightclick", "X": 820, "Y": 45, "DisplaySize": { "width": 1280, "height": 720 } }
{ "Type": "wheel", "Delta": -120, "X": 500, "Y": 300, "DisplaySize": { "width": 1280, "height": 720 } }
{ "Type": "key", "Key": "A", "KeyCode": "KeyA" }
```

- `scale_pointer` maps `X`/`Y` from `DisplaySize` onto the primary screen. A missing `DisplaySize` passes the coordinates through. `Delta` is not scaled.
- `wheel_delta` negates `Delta` before the wheel event.
- Touch keeps one contact, ignores hover (`touch_event_applies`), and `routes::touch_repeat` resends an update every `TOUCH_REPEAT_MS` (`100`).
- An explicit `rightclick` is a mouse right button even in touch mode.
- `map_key` is the SendKeys mapping. Modifiers and lock keys are dropped. Unknown names are dropped. Metacharacters `+ ^ % ~ ( ) { } [ ]` are wrapped. `expand_paste` splits pasted text, collapses CR/LF to Enter, and tabs to Tab.
- Which path a pointer event takes is `pointer_path(kind, touch_mode, touch_available)`: touch only when the mode is touch, `app.inner.touch_enabled` is true, and the event is not `rightclick`. Everything else is the mouse path. Wheel and keys do not use it.
- Windows startup calls `platform::init_dpi_awareness` first thing in `serve`: `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`, falling back to `SetProcessDPIAware()`. `primary_size()` and `InjectTouchInput` therefore both use physical pixels at any display scaling. The call is a no-op on Linux.
- Windows touch uses `InjectTouchInput` after `InitializeTouchInjection` (`win::probe_touch`, run from `serve` through `platform::probe_touch`). Mouse and keyboard use `SendInput`/`SetCursorPos`, which need no probe, so `platform::probe_input` is `true` on Windows. If `probe_touch` fails, `touch_enabled` is false, `serve` logs `[Input] Touch injection is unavailable. Pointer events use the mouse instead.` when the mode is touch, and pointer events fall back to the mouse. Mouse, wheel, and keys keep working.
- `win::end_contact` and `win::repeat_touch` copy `TOUCH_XY` and release the lock before calling `inject_touch` (which locks it again), so a held contact no longer deadlocks. `end_contact` always leaves the contact marked up.
- Linux touch, mouse, wheel, and keys are the records from `decisions/linux_input.rs` (`linux_pointer_events`, `linux_repeat_events`, `linux_wheel_events`, `linux_key_events`). `linux.rs` writes those `input_event` values to `/dev/uinput`. Touch uses `ABS_MT` slot 0. Mouse uses `ABS_X`/`ABS_Y` plus `BTN_LEFT`/`BTN_RIGHT`. `linux_abs_axes` sets those position maxima to the primary screen size, the same size `scale_pointer` uses, so a coordinate equal to that size is the far edge. Wheel uses `REL_WHEEL`. `linux_wheel_events` divides the inverted Windows delta by `LINUX_WHEEL_NOTCH` (`120`) and keeps the remainder, so one notch is ±1. `{ENTER}` is `KEY_ENTER`. Other text is `EV_KEY`, and a character without a key code uses Ctrl+Shift+U.

## Failure

`platform::probe_input` is the mouse/keyboard fact. On Linux it opens `/dev/uinput`; a failure logs the error, sets `input_enabled` false, and the socket still accepts messages without injecting them. On Windows it is always `true`. `platform::probe_touch` is a separate fact stored in `touch_enabled`: Windows `InitializeTouchInjection`, and on Linux always `true` (touch shares the uinput device). A touch failure never disables mouse or keyboard.
