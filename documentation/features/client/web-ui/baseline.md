# Web UI — Behavior Baseline

Known-good invariants for the browser client. Update only when intended behavior changes.

## Flow Invariants

- The remote screen is rendered by a plain `<img src="/stream">` — no `<canvas>`, no JS in the video path.
- The input WebSocket (`/ws/input`) opens on page load, before any audio interaction.
- Audio only starts after the user clicks **Start playback** (browser user-gesture requirement).
- All WebSocket/stream URLs are same-origin via `getWsUrl` (`wss://` under HTTPS, `ws://` under HTTP).

## Input Rules

- Mouse listeners are attached to the video `<img>`: `click`, `mousemove`, `mousedown`, `mouseup`.
- Coordinates are relative to the image's bounding rect and sent with the image's rendered size as `DisplaySize`.
- All keyboard paths send over `/ws/input` via `sendKey(key, code)` (`{Type:'key', Key, KeyCode}`) or `sendText(text)`.
- Paste is forwarded regardless of whether `#fakeKeyboard` is focused: a `document`-level `paste` listener reads `clipboardData.getData('text')` and calls `sendText`; it also fires when the user pastes into the box itself (the box's own `input` event is suppressed by `preventDefault()`).
- Physical keyboard typing is captured page-wide by a `document`-level `keydown` listener; the user does **not** need to focus `#fakeKeyboard` for keystrokes to be forwarded.
- Focused editable elements (`INPUT`, `TEXTAREA`, `contentEditable`) receive no page-level keyboard capture (the listener returns early via `isEditable`), preventing double-send when a real editable is active.
- Modifier-held combos (`ctrlKey`, `metaKey`, `altKey`) and standalone modifier keys (`modifierKeys` list) are not captured by the page-level listener, so browser shortcuts (e.g. Ctrl+V) continue to work normally.
- `sendText` maps `\r\n` (CRLF, consumed as one unit), bare `\r`, and bare `\n` to the `Enter` key; `\t` to the `Tab` key; all other characters are forwarded as their literal value.
- `#fakeKeyboard` (visible text input) still accepts typed characters and paste via its `input` event, forwarding both `insertText` and `insertFromPaste` input types; it is the primary input path on touch/on-screen-keyboard devices.

## Audio Rules

- The AudioWorklet path is preferred; on `addModule`/node failure the client falls back to `AudioBufferSourceNode` scheduling.
- The `AudioContext` is created at the server's reported sample rate; if the browser clamps it, a warning is logged and client-side resampling engages.
- The **Start playback** button transitions: `Start playback` → `Connecting...` (disabled) → `Restart playback`.

## Failure Behavior

- Audio socket `onerror`/`onclose` → `resetAudioConnection()` re-enables the button; **no automatic reconnect**.
- The input socket and MJPEG `<img>` have **no reconnect logic**; a dropped connection fails silently / shows a broken image until reload.
