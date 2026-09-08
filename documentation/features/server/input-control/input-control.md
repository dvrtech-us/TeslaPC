# Input Control

Remote mouse and keyboard control delivered from the browser over a WebSocket and replayed on
the host. Mouse uses Win32 P/Invoke (`SetCursorPos`/`mouse_event`); keyboard is replayed with
`System.Windows.Forms.SendKeys.SendWait` via `WebServer.handleKey`. (The `keybd_event` P/Invoke
remains declared but unused — keyboard goes through `SendKeys`, not `keybd_event`.)

## User Flow

1. The browser opens a WebSocket to `/ws/input` on page load.
2. As the user moves, presses, releases, scrolls the mouse (or performs two-finger touch scroll), the client sends a JSON event.
3. The server scales the coordinates from the browser's display size to the host's primary-screen resolution (for pointer events) and replays actions with `SetCursorPos` + `mouse_event` (including `MOUSEEVENTF_WHEEL`).

## Technical Flow

### Acceptance and receive loop (`WebServer.AcceptWebSocketAsync`, `WebServer.cs:271`)

- Reached for any WebSocket request whose path is not `/ws/audio` (so `/ws/input` lands here by exclusion).
- `context.AcceptWebSocketAsync(null)` accepts with no sub-protocol.
- Loops while `webSocket.State == WebSocketState.Open`, reading into a `4096`-byte buffer.
- A `Close` message triggers a `NormalClosure` close frame and exits the loop.
- Each text message is UTF-8 decoded and deserialized to a `MousePosition` via `JsonSerializer.Deserialize<MousePosition>`.

### Message protocol

```json
{ "Type": "move|down|up|rightclick", "X": 820, "Y": 45, "DisplaySize": { "width": 1280, "height": 720 } }
{ "Type": "wheel", "Delta": -120, "X": 500, "Y": 300, "DisplaySize": { "width": 1280, "height": 720 } }
```

- `Type` — event kind.
  - Pointer: `"move"`, `"down"`, `"up"`, `"rightclick"`. Only down/up/rightclick produce button actions; all pointer types move the cursor via `SetCursorPos`.
  - `"wheel"` — scroll. `Delta` is the scroll amount (typically a browser `deltaY` value or a scaled touch delta). X/Y (if present) are used to position the cursor before emitting the wheel.
- `X`, `Y` — coordinates in the browser's rendered image space.
- `DisplaySize` — the rendered pixel size of the video `<img>` element, used for scaling.
- `Delta` — wheel-specific; the signed scroll delta. The server inverts the sign so that typical client "scroll down" values produce the expected direction on Windows.

### Coordinate scaling (`InputData.GetAdjusted`, `Program.cs`)

- Applies only to `X`/`Y` when `DisplaySize` is provided (used for pointer and wheel positioning).
- If `DisplaySize` is null, coordinates pass through unchanged.
- Otherwise: `x = (int)(X / DisplaySize.width * Screen.PrimaryScreen.Bounds.Width)`, same for `y`.
- `Delta` (wheel) is preserved as-is; it is never scaled.
- Returns a new `InputData` instance.

### Replay (`WebServer.cs`)

Pointer events (non-`wheel`) always call `SetCursorPos` first, then emit button events as needed.

| Action | Condition |
|--------|-----------|
| `Win32.SetCursorPos(x, y)` | always for pointer events; for wheel only when X/Y/DisplaySize were supplied |
| `Win32.mouse_event(MOUSEEVENTF_LEFTDOWN, …)` | `Type == "down"` |
| `Win32.mouse_event(MOUSEEVENTF_LEFTUP, …)` | `Type == "up"` |
| `Win32.mouse_event(MOUSEEVENTF_RIGHTDOWN, …)` + `RIGHTUP` | `Type == "rightclick"` |
| `Win32.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -Delta, 0)` | `Type == "wheel"` (sign inverted on server) |

Wheel messages can include an X/Y position; when present the cursor is moved to that point before the wheel is emitted so the scroll affects the element under the pointer.

The client produces `"rightclick"` from a desktop right-mouse (`contextmenu`) event or a touch
**long-press** (~0.5 s).

### Wheel & Touch Scroll

- Desktop: `wheel` events on the stream image forward `e.deltaY`.
- Touch (Tesla and tablets): when two or more simultaneous touches are detected, the handlers switch to scroll mode. The average vertical movement of the first two fingers is scaled (see `T2_SCROLL_FACTOR` in `index.html`), **negated** (natural touch convention: content follows the fingers, so dragging down scrolls up), and sent as wheel deltas. Single-touch tap/drag/long-press logic is bypassed while multi-touch is active.
- Client: `TeslaPCInterface/index.html` (`sendWheel`, wheel listener, two-finger logic in touch handlers).
- Server always runs the wheel through the existing scaling path for coordinates (when provided) before calling `mouse_event`.

### Keyboard (`WebServer.handleKey`)

Messages whose JSON contains `"key"` are deserialized as `KeyData { Type, Key, KeyCode }` and
replayed with `SendKeys.SendWait`. The client sends **exactly one message per keystroke**
(`Type` = `"key"`): characters via the input box's `input` event, non-character keys via
`keydown`. `handleKey` therefore does **no** server-side debounce (legitimate fast repeats like
"ll" are preserved). Key handling:

- **Modifier / lock / non-text keys are ignored** — `Shift`, `Control`, `Alt`, `Meta`, `OS`,
  `AltGraph`, `CapsLock`, `NumLock`, `ScrollLock`, `ContextMenu`, `Dead`, `Unidentified`, etc.
  return without typing anything (so pressing Shift never types "Shift"). Shifted characters
  already arrive composed (e.g. `A`, `!`).
- **Named keys (length > 1)** map to `SendKeys` tokens: `Backspace`→`{BACKSPACE}`,
  `Enter`→`{ENTER}`, `Tab`→`{TAB}`, `Escape`→`{ESC}`, `Delete`→`{DELETE}`, `Insert`→`{INSERT}`,
  `Home`/`End`→`{HOME}`/`{END}`, `PageUp`/`PageDown`→`{PGUP}`/`{PGDN}`,
  `ArrowLeft/Right/Up/Down`→`{LEFT}`/`{RIGHT}`/`{UP}`/`{DOWN}`, `F1`–`F12`→`{F1}`…`{F12}`.
  An **unknown named key is dropped** (never typed as its literal name).
- **Single characters** are sent as-is, except the SendKeys metacharacters `+ ^ % ~ ( ) { } [ ]`,
  which are wrapped in `{}`.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `WebServer.AcceptWebSocketAsync` | `TeslaPCInterface/WebServer.cs` | Accepts the input WebSocket and replays events |
| `InputData` | `TeslaPCInterface/Program.cs` | DTO for pointer + wheel messages + coordinate scaling (`GetAdjusted`) |
| `DisplaySize` | `TeslaPCInterface/Program.cs` | Nested DTO carrying the browser's rendered image dimensions |
| `Win32` | `TeslaPCInterface/Program.cs` | P/Invoke to `user32.dll` |

### Win32 P/Invoke surface (`Program.cs:159`)

| Function | DLL | Used? |
|----------|-----|-------|
| `SetCursorPos(int x, int y)` | user32 | yes |
| `mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo)` | user32 | yes |
| `GetCursorPos(out POINT)` | user32 | declared, unused |
| `ClientToScreen(IntPtr, ref POINT)` | user32 | declared, unused |
| `keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo)` | user32 | declared, **unused** (keyboard uses `SendKeys`, not `keybd_event`) |

Constants (in `Win32`, `Program.cs`):
`MOUSEEVENTF_LEFTDOWN = 0x02`, `LEFTUP = 0x04`, `RIGHTDOWN = 0x08`, `RIGHTUP = 0x10`,
`WHEEL = 0x0800`, `HWHEEL = 0x1000` (horizontal prepared for future use).

## Routes and Access Control

| Route | Protocol | Auth |
|-------|----------|------|
| `/ws/input` (matched by exclusion) | WebSocket | none |

No authentication. Anyone who can reach the WebSocket can drive the mouse.

## Integration Points

- Hosted directly inside `WebServer`; no separate class.
- Depends on `Screen.PrimaryScreen.Bounds` for the scaling denominator.
- Client side (mouse, wheel, touch gestures) is implemented in [web-ui](../../client/web-ui/web-ui.md) (`sendInput`, `sendWheel`, touch + wheel listeners in `index.html`).

## Database Schema

None.

## SQL Artifacts

None.
