# Input Control — Behavior Baseline

Known-good invariants for remote input. Update only when intended behavior changes.

## Flow Invariants

- The input WebSocket is reached for **any** WebSocket path that is not `/ws/audio`.
- The receive buffer is `4096` bytes.
- Every inbound message is parsed as a `MousePosition` JSON object.
- Coordinates are scaled from `DisplaySize` to `Screen.PrimaryScreen.Bounds`; when `DisplaySize` is null, coordinates are used as-is.
- `AppSettings.UseTouchInput` is true unless `TESLAPC_INPUT_MODE=mouse`. Touch mode replays `down`/`move`/`up` with `TouchInput` (one contact, no hover). Mouse mode moves the cursor on every pointer message (`SetCursorPos`).
- In mouse mode, a left button-down fires only for `Type == "down"`; button-up only for `Type == "up"`.
- In touch mode the page puts the contact down on finger-down and up on finger-up. A still hold is Windows press-and-hold (right-click). The page's 500 ms `rightclick` timer runs only in mouse mode.

## Event Rules (mouse mode)

| `Type` | Cursor moved? | Action |
|--------|---------------|--------|
| `move` | yes | none (cursor only) |
| `down` | yes | left button down |
| `up` | yes | left button up |
| `rightclick` | yes | right button click (down+up pair) |
| `wheel` | only if X/Y supplied | `MOUSEEVENTF_WHEEL` with `-Delta` |
| `click` | yes | none (no dedicated click handling) |

## Event Rules (touch mode)

| `Type` | Action |
|--------|--------|
| `down` | touch contact down (or update if one is already down) |
| `move` | touch update while a contact is down; ignored otherwise |
| `up` | touch contact up |
| `rightclick` | mouse right button at that point |
| `wheel` | same as mouse mode |
| `click` | ignored |

Wheel deltas are forwarded (sign inverted on server for Windows convention). Two-finger touch on the client produces wheel messages.

## Keyboard

- The client sends **one message per keystroke** (`Type` = `"key"`): characters from the input
  box's `input` event, non-character keys from `keydown`. The server replays via
  `SendKeys.SendWait` in `WebServer.handleKey` with **no debounce** (so fast repeats like "ll"
  are preserved and nothing is double-typed). The `keybd_event` P/Invoke is declared but unused.
- **Standalone modifier/lock keys (`Shift`, `Control`, `Alt`, `Meta`, `CapsLock`, etc.) are
  never typed** — they return early. Shifted characters arrive pre-composed.
- Named keys map to `SendKeys` tokens (arrows, Delete, Home/End, PageUp/Down, Insert, F1–F12,
  Backspace/Enter/Tab/Escape). An unknown named key (length > 1) is dropped, never typed
  literally. Single characters are escaped only for `+ ^ % ~ ( ) { } [ ]`.

## Not Implemented (intentional current state)

- Middle-click.
- Horizontal wheel (`MOUSEEVENTF_HWHEEL`) — constant is declared but not wired in client or server yet.
- A dedicated `click` (down+up) action.

Right-click is fully supported (`Type == "rightclick"` via desktop `contextmenu` or touch long-press).
Vertical scroll (mouse wheel + two-finger touch drag) is now supported.

## Access Control

- No authentication. Network-layer controls only.

## Failure Behavior

- A `Close` frame ends the session with `NormalClosure`.
- A `WebSocketException` in the loop is logged and does **not** break the loop directly; the loop exits when `State != Open`.
