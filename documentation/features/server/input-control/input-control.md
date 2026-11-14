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
- Windows touch uses a synthetic pointer device: `win::probe_touch` (run from `serve` through `platform::probe_touch`) calls `CreateSyntheticPointerDevice(PT_TOUCH, 1, POINTER_FEEDBACK_DEFAULT)` once and `inject_touch` sends each frame with `InjectSyntheticPointerInput`. If the device cannot be created it falls back to `InitializeTouchInjection` and `InjectTouchInput`. Frames are down `INRANGE|INCONTACT|DOWN`, move `INRANGE|INCONTACT|UPDATE`, up `UP`, with a 4x4 px contact, pressure `512`, and `touchMask` `TOUCH_MASK_CONTACTAREA|TOUCH_MASK_PRESSURE`. Mouse and keyboard use `SendInput`/`SetCursorPos`, which need no probe, so `platform::probe_input` is `true` on Windows. If `probe_touch` fails, `touch_enabled` is false, `serve` logs `[Input] Touch injection is unavailable. Pointer events use the mouse instead.` when the mode is touch, and pointer events fall back to the mouse. Mouse, wheel, and keys keep working.
- `win::end_contact` and `win::repeat_touch` copy `TOUCH_XY` and release the lock before calling `inject_touch` (which locks it again), so a held contact no longer deadlocks. `end_contact` always leaves the contact marked up.
- Linux touch, mouse, wheel, and keys are the records from `decisions/linux_input.rs` (`linux_pointer_events`, `linux_repeat_events`, `linux_wheel_events`, `linux_key_events`). `linux.rs` writes those `input_event` values to `/dev/uinput`. Touch uses `ABS_MT` slot 0. Mouse uses `ABS_X`/`ABS_Y` plus `BTN_LEFT`/`BTN_RIGHT`. `linux_abs_axes` sets those position maxima to the primary screen size, the same size `scale_pointer` uses, so a coordinate equal to that size is the far edge. Wheel uses `REL_WHEEL`. `linux_wheel_events` divides the inverted Windows delta by `LINUX_WHEEL_NOTCH` (`120`) and keeps the remainder, so one notch is ±1. `{ENTER}` is `KEY_ENTER`. Other text is `EV_KEY`, and a character without a key code uses Ctrl+Shift+U.

## Session host: one `Input` thread that follows the input desktop (Windows)

Only for `--session-host` (the Windows service host). `serve` calls
`platform::start_input_thread(app)` before the probes; `win::start_input_thread` starts a thread
named `Input` and stores the sender of a `std::sync::mpsc` channel in `INPUT_TX`
(`[Input] Session host: input runs on the Input thread and follows the input desktop.`).
`routes.rs` is unchanged: `platform::inject_pointer`, `inject_wheel`, `inject_key`,
`end_contact`, `repeat_touch`, and `probe_touch` call `win::dispatch`, which queues an
`InputOp` when `INPUT_TX` is set and otherwise runs the same operation directly on the caller's
thread with results ignored (a normal run is unchanged). One channel keeps event order.
`probe_touch` waits up to 5 s for the thread's answer.

Before each event the thread runs its `DesktopAttachment` check (the rules are in
`documentation/features/infrastructure/windows-service/`): at most every 200 ms, at once after
a failed injection. On a switch:

- Before `SetThreadDesktop`: a held touch contact is ended (best effort; logged at `debug` if
  refused) and `TOUCH_XY` is marked up.
- After it: `app.inner.touch_down` is cleared, so the route ignores the moves and the up of a
  contact that started on the other desktop and the repeat tick stops; a left button pressed
  through the mouse path is released.
- If `InitializeTouchInjection` failed while the thread was on a secure desktop (the host
  started on the sign-in screen), it is retried on the first switch to `Default`; success sets
  `touch_enabled` and prints `[Input] Touch injection initialized on the Default desktop.`

Path choice is `pointer_path_on_desktop(kind, touch, touch_ready, on_default)`
(`decisions/input.rs`): off `Default` (`is_default_desktop`, case-insensitive; an unknown name
counts as `Default`) every pointer event is the mouse path (`SetCursorPos`, then
`SendInput` buttons) even in touch mode. The route still filters hover and tracks
`touch_down` in touch mode, so a tap arrives as `down` then `up` and becomes `SetCursorPos` +
left down, `SetCursorPos` + left up: a left click at the tapped position. Moves while held only
move the cursor. `touch_repeat_applies(contact_down, on_default)` makes the repeat tick do
nothing off `Default` (no contact exists there). `end_contact` on this thread also releases a
left button still held through the mouse path.

Every injection result is checked: `SendInput` must report one event inserted (otherwise
`SendInput(<what>) inserted 0 of 1 events: <GetLastError text>`), `SetCursorPos` and
`InjectTouchInput` errors carry the call and position. A failure is logged with
`App::log("debug", "[Input] <error> (desktop <name>)")` and forces a desktop check before the
next event. No mutex guard is held across an injection call.

Not yet run on Windows: whether `SendInput` reaches the sign-in and UAC screens as SYSTEM, and
whether touch injection still works on `Default` after a trip to `Winlogon`.

## Failure

`platform::probe_input` is the mouse/keyboard fact. On Linux it opens `/dev/uinput`; a failure logs the error, sets `input_enabled` false, and the socket still accepts messages without injecting them. On Windows it is always `true`. `platform::probe_touch` is a separate fact stored in `touch_enabled`: Windows `InitializeTouchInjection`, and on Linux always `true` (touch shares the uinput device). A touch failure never disables mouse or keyboard.
