# Input Control

Remote mouse and keyboard control delivered from the browser over a WebSocket and replayed on
the host. Mouse uses Win32 P/Invoke (`SetCursorPos`/`mouse_event`); keyboard is replayed with
`System.Windows.Forms.SendKeys.SendWait` via `WebServer.handleKey`. (The `keybd_event` P/Invoke
remains declared but unused — keyboard goes through `SendKeys`, not `keybd_event`.)

## User Flow

1. The browser opens a WebSocket to `/ws/input` on page load.
2. As the user moves, presses, or releases the mouse over the video image, the client sends a JSON event.
3. The server scales the coordinates from the browser's display size to the host's primary-screen resolution and replays the action with `SetCursorPos` + `mouse_event`.

## Technical Flow

### Acceptance and receive loop (`WebServer.AcceptWebSocketAsync`, `WebServer.cs:271`)

- Reached for any WebSocket request whose path is not `/ws/audio` (so `/ws/input` lands here by exclusion).
- `context.AcceptWebSocketAsync(null)` accepts with no sub-protocol.
- Loops while `webSocket.State == WebSocketState.Open`, reading into a `4096`-byte buffer.
- A `Close` message triggers a `NormalClosure` close frame and exits the loop.
- Each text message is UTF-8 decoded and deserialized to a `MousePosition` via `JsonSerializer.Deserialize<MousePosition>`.

### Message protocol

```json
{ "Type": "click|move|down|up", "X": 820, "Y": 45, "DisplaySize": { "width": 1280, "height": 720 } }
```

- `Type` — event kind. Only `"down"` and `"up"` produce button events; all types move the cursor.
- `X`, `Y` — coordinates in the browser's rendered image space.
- `DisplaySize` — the rendered pixel size of the video `<img>` element, used for scaling.

### Coordinate scaling (`MousePosition.GetAdjusted`, `Program.cs:143`)

- If `DisplaySize` is null, coordinates pass through unchanged.
- Otherwise: `x = (int)(X / DisplaySize.width * Screen.PrimaryScreen.Bounds.Width)`, same for `y`.
- Returns a new `MousePosition` with scaled `X`/`Y` and the same `Type` (`DisplaySize` is not copied onto the result).

### Replay (`WebServer.cs:298` onward)

| Order | Action | Condition |
|-------|--------|-----------|
| 1 | `Win32.SetCursorPos(x, y)` | always |
| 2 | `Win32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0,0,0,0)` | `Type == "down"` |
| 3 | `Win32.mouse_event(MOUSEEVENTF_LEFTUP, 0,0,0,0)` | `Type == "up"` |

No handling exists for `"click"`, `"move"` (beyond the cursor move), right-click, or scroll.

### Keyboard (`WebServer.handleKey`)

Messages whose JSON contains `"key"` (the client sends `Type` of `"keyup"`/`"keypress"`) are
deserialized as `KeyData { Type, Key, KeyCode }` and replayed with `SendKeys.SendWait`.
`handleKey` debounces repeats of the same `Key` within 100 ms. Key handling:

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
| `MousePosition` | `TeslaPCInterface/Program.cs` | DTO + coordinate scaling (`GetAdjusted`) |
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

Constants: `MOUSEEVENTF_LEFTDOWN = 0x02`, `MOUSEEVENTF_LEFTUP = 0x04`.

## Routes and Access Control

| Route | Protocol | Auth |
|-------|----------|------|
| `/ws/input` (matched by exclusion) | WebSocket | none |

No authentication. Anyone who can reach the WebSocket can drive the mouse.

## Integration Points

- Hosted directly inside `WebServer`; no separate class.
- Depends on `Screen.PrimaryScreen.Bounds` for the scaling denominator.
- Client side is implemented in [web-ui](../../client/web-ui/web-ui.md) (`sendMouseEvent`).

## Database Schema

None.

## SQL Artifacts

None.
