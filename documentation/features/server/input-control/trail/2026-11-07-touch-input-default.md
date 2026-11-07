# Touch injection is the default pointer mode

- Date: 2026-11-07
- Feature: input-control
- Related code: `TeslaPCInterface/TouchInput.cs`, `TeslaPCInterface/WebServer.cs`, `TeslaPCInterface/index.html`, `TeslaPCInterface/AppSettings.cs`

## Context

The client is a touch screen. Replaying the finger as mouse moves draws a cursor and never
becomes a Windows press-and-hold. Hover is not needed. A setting is still required so a desktop
browser can drive the host with a real mouse.

## Decisions

- `TESLAPC_INPUT_MODE` is `touch` or `mouse`. Unset means `touch`. Exposed as `inputMode` on
  `GET`/`POST /config` and on the WinForms Config tab. Live, no restart. The screen page reads
  it once, so that page is reloaded after a change.
- Touch mode calls `InitializeTouchInjection(1, TOUCH_FEEDBACK_DEFAULT)` and injects one
  `POINTER_TOUCH_INFO` (`pointerId` 1). `down` / `move` / `up` map to contact down, update, and
  up. Moves with no contact are dropped. A 100 ms timer repeats the update while the finger is
  down, which is what Windows requires for press-and-hold to fire.
- The page, in touch mode, sends `down` on `touchstart` and `up` on `touchend`, and does not
  send its own `rightclick`. Mouse mode keeps the 500 ms long-press and the desktop right button.
- Two-finger scroll stays a mouse wheel in both modes.

## Alternatives Considered

- Touch only, no toggle. Rejected: desktop testing and any client that is a mouse still need
  the old path.
- Keep synthesizing `rightclick` on top of the touch contact. Rejected: that double-fires once
  Windows press-and-hold is active.

## Consequences

- Positive: taps and drags are touch; a still finger is the system right-click; no hover cursor
  from pointer moves.
- Negative: press-and-hold follows the Windows touch setting and its speed slider. If that
  setting is off, a long hold does not right-click. Two-finger scroll still moves the mouse
  cursor for the wheel event.
