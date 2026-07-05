# Web UI

The single-page browser client (`index.html`) that renders the remote screen, plays system
audio, and forwards mouse input. Vanilla JavaScript — no frameworks, no build step. All CSS
and JS are inline; the only external module is `PCMPlayerProcessor.js` (loaded as an AudioWorklet).

## User Flow

1. The browser loads `/` (served as `index.html`).
2. The remote screen appears immediately via `<img src="/stream">` (native MJPEG rendering).
3. The input WebSocket (`/ws/input`) opens automatically on page load; mouse actions over the image are forwarded.
4. The user clicks **Start playback** to begin audio (a user gesture is required by browsers); the button then reads **Restart playback**.

## Technical Flow

### Layout

- `<title>Remote Desktop</title>`; styling comes from the shared `/style.css` (dark in-car touch theme), not inline styles; a no-zoom `viewport` meta is set for touch.
- Video: `<img title="playback" src="/stream">` inside `<div class="screen">` (flex-centered, `object-fit: contain`). There is **no `<canvas>`** — MJPEG renders directly into the `<img>`.
- Controls live in a fixed bottom `<div class="controlbar">` with large `.btn` targets: **Start playback** (`#startPlayback`, primary), the **Keyboard** input (`#fakeKeyboard`), **Files** (→ `/list.html`), **Settings** (→ `/config.html`), **stream resolution** (`#resBtn` opens `#resPicker` with 480p/720p/1080p buttons — not a native `<select>`; Tesla browser breaks those), and **Fit screen**. See the [file-browser-vlc](../../media/file-browser-vlc/file-browser-vlc.md) feature.

### URL helper

- `getWsUrl(path)` chooses `wss://` on HTTPS and `ws://` on HTTP, always against `location.host` (same origin).

### Video

- Purely `<img src="/stream">`; the browser handles `multipart/x-mixed-replace`. No JS, no reconnect logic.

### Input (`mapPoint` / `sendInput`, `index.html`)

- The input WebSocket opens on load: `new WebSocket(getWsUrl('/ws/input'))`.
- **Mouse** listeners on the `<img>`: `click`, `mousemove`, `mousedown`, `mouseup`, and
  `contextmenu`→`rightclick` (desktop right-click, `preventDefault`ed).
- **Touch** listeners (Tesla/tablets), all `preventDefault`ed (`passive:false`) to stop scroll/zoom
  and avoid duplicate synthesized mouse events:
  - **quick tap** → `down`+`up` (left click)
  - **press + drag** (move > ~12px) → `down` then `move`…`up` (left-button drag)
  - **press & hold ~0.5s** → `rightclick` (long-press; the `down` is deferred so a hold isn't also a
    left press)
- `mapPoint(clientX, clientY)` converts a viewport point to the **actual video rectangle**
  (accounting for the `object-fit: contain` letterbox via `naturalWidth/Height`); points in the
  black bars return null and are ignored. `sendInput(type, x, y)` sends the mapped coords.
- Message: `{ "Type": "click|move|down|up|rightclick", "X": int, "Y": int, "DisplaySize": { "width": dispW, "height": dispH } }` where `dispW/dispH` are the displayed video size.
- **Keyboard** — three input paths all funnel through `sendKey(key, code)` (sends `{Type:'key', Key, KeyCode}` over `/ws/input`) and a helper `sendText(text)`:

  1. **On-screen box (`#fakeKeyboard`)** — a visible text input so touch devices (e.g. Tesla browser) can summon the on-screen keyboard. Its `input` event listener sends `event.data` for both `insertText` and `insertFromPaste` input types via `sendKey`.
  2. **Page-wide physical-keyboard typing** — a `document`-level `keydown` listener captures keystrokes without requiring the box to be focused. It returns early when the event target is an editable element (`isEditable` checks for `INPUT`/`TEXTAREA`/`contentEditable`, preventing double-send when a real field is focused), when any modifier key (`ctrlKey`, `metaKey`, `altKey`) is held (so browser shortcuts such as Ctrl+V are not stolen), or when the key is a standalone modifier (entries in the `modifierKeys` array). Single-character keys (`event.key.length === 1`) are forwarded via `sendKey(event.key)`; named and function keys (matched against the `namedKeys` list or `/^F\d{1,2}$/`) are forwarded via `sendKey(event.key, event.code)`. Both paths call `preventDefault()`.
  3. **Paste anywhere** — a `document`-level `paste` listener reads `event.clipboardData.getData('text')` and forwards the full string via `sendText`. It calls `preventDefault()`, which also suppresses the duplicate `input` event that would otherwise fire when pasting into `#fakeKeyboard`. The box is cleared if it was focused when paste occurred.

  `sendText(text)` sends a multi-character run one character at a time: `\r\n` (CRLF, consumed as one) and bare `\r`/`\n` map to the `Enter` key; `\t` maps to `Tab`; every other character is sent as its literal value.

  No reconnect or `onclose` handling exists on the input socket.

### Audio (`startAudioPlayback`, `index.html:86`)

1. Guard against re-entry (`audioConnecting`); if restarting, close the prior socket/context and reset state.
2. Open `/ws/audio` with `binaryType = 'arraybuffer'`.
3. First (text) message → parse format → create `AudioContext({ sampleRate, latencyHint: 'interactive' })` → warn if the browser clamped the rate → `audioWorklet.addModule('PCMPlayerProcessor.js')` → create `AudioWorkletNode` (`outputChannelCount: [channels]`, format in `processorOptions`) → connect to destination.
4. On worklet failure → `useWorklet = false`, fall back to `playPcmChunkFallback`.
5. `audioContext.resume()`.
6. Subsequent (binary) messages → `pcmPlayerNode.port.postMessage(data)` (worklet) or `playPcmChunkFallback(data)`.

### Audio fallback (`playPcmChunkFallback`, `index.html:254`)

- `decodePcmToFloat32` → `resampleFloat32` → `createBuffer` → deinterleave → schedule via `AudioBufferSourceNode.start(scheduledTime)`.
- If `scheduledTime < currentTime`, reset to `currentTime + 0.02` (20 ms look-ahead) to smooth gaps.

## Key Functions

| Function | File | Responsibility |
|----------|------|----------------|
| `getWsUrl(path)` | `index.html:60` | Build same-origin `ws://`/`wss://` URL |
| `sendMouseEvent(type, event)` | `index.html:295` | Forward image-relative mouse coords to `/ws/input` |
| `sendKey(key, code)` | `index.html` | Send `{Type:'key', Key, KeyCode}` over `/ws/input` |
| `sendText(text)` | `index.html` | Forward a text run one character at a time; maps `\r`/`\n`→Enter, `\t`→Tab |
| `isEditable(el)` | `index.html` | Return true when `el` is an `INPUT`, `TEXTAREA`, or has `contentEditable` set |
| `isNamedKey(event)` | `index.html` | Return true when the key matches `namedKeys` or `/^F\d{1,2}$/` |
| `startAudioPlayback()` | `index.html:86` | Negotiate format and start AudioWorklet (or fallback) |
| `playPcmChunkFallback(rawData)` | `index.html:254` | Schedule PCM via `AudioBufferSourceNode` when no AudioWorklet |
| `decodePcmToFloat32` / `resampleFloat32` | `index.html:177,219` | Decode + linear-interpolation resample (mirror of the worklet) |

## Routes and Access Control

The client consumes `/` (HTML), `/stream` (video), `/ws/input` (input), and `/ws/audio` (audio).
All are same-origin and unauthenticated.

## Integration Points

- Served by [web-server](../../server/web-server/web-server.md) static-file handling.
- Renders [screen-capture](../../streaming/screen-capture/screen-capture.md) and drives [input-control](../../server/input-control/input-control.md) and [audio-capture](../../streaming/audio-capture/audio-capture.md).

## Database Schema

None.

## SQL Artifacts

None.
