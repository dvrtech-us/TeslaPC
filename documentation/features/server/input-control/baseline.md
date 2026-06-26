# Input Control — Behavior Baseline

Known-good invariants for remote input. Update only when intended behavior changes.

## Flow Invariants

- The input WebSocket is reached for **any** WebSocket path that is not `/ws/audio`.
- The receive buffer is `4096` bytes.
- Every inbound message is parsed as a `MousePosition` JSON object.
- The cursor is moved on **every** message (`SetCursorPos`), regardless of `Type`.
- A left button-down fires only for `Type == "down"`; button-up only for `Type == "up"`.
- Coordinates are scaled from `DisplaySize` to `Screen.PrimaryScreen.Bounds`; when `DisplaySize` is null, coordinates are used as-is.

## Event Rules

| `Type` | Cursor moved? | Button action |
|--------|---------------|---------------|
| `move` | yes | none |
| `down` | yes | left button down |
| `up` | yes | left button up |
| `click` | yes | none (no dedicated click handling) |

## Not Implemented (intentional current state)

- Keyboard input: `keybd_event` is declared but never invoked.
- Right-click, middle-click, and scroll wheel.
- A dedicated `click` (down+up) action.

## Access Control

- No authentication. Network-layer controls only.

## Failure Behavior

- A `Close` frame ends the session with `NormalClosure`.
- A `WebSocketException` in the loop is logged and does **not** break the loop directly; the loop exits when `State != Open`.
