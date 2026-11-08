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
- Windows touch uses `InjectTouchInput` after `InitializeTouchInjection`. If that fails, input is disabled for the launch and HTTP still starts.
- Linux touch, mouse, wheel, and keys are the records from `decisions/linux_input.rs` (`linux_pointer_events`, `linux_repeat_events`, `linux_wheel_events`, `linux_key_events`). `linux.rs` writes those `input_event` values to `/dev/uinput`. Touch uses `ABS_MT` slot 0. Mouse uses `ABS_X`/`ABS_Y` plus `BTN_LEFT`/`BTN_RIGHT`. `linux_abs_axes` sets those position maxima to the primary screen size, the same size `scale_pointer` uses, so a coordinate equal to that size is the far edge. Wheel uses `REL_WHEEL`. `linux_wheel_events` divides the inverted Windows delta by `LINUX_WHEEL_NOTCH` (`120`) and keeps the remainder, so one notch is ±1. `{ENTER}` is `KEY_ENTER`. Other text is `EV_KEY`, and a character without a key code uses Ctrl+Shift+U.

## Failure

`probe_input` logs the privilege error, `input_enabled` is set false, and the socket still accepts messages without injecting them.
