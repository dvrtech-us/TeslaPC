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
- Controls live in a fixed bottom `<div class="controlbar">` with large `.btn` targets: **Start playback** (`#startPlayback`, primary), the **Keyboard** input (`#fakeKeyboard`), and **Files** (→ `/list.html`). See the [file-browser-vlc](../../media/file-browser-vlc/file-browser-vlc.md) feature.

### URL helper

- `getWsUrl(path)` chooses `wss://` on HTTPS and `ws://` on HTTP, always against `location.host` (same origin).

### Video

- Purely `<img src="/stream">`; the browser handles `multipart/x-mixed-replace`. No JS, no reconnect logic.

### Input (`sendMouseEvent`, `index.html:295`)

- The input WebSocket opens on load: `new WebSocket(getWsUrl('/ws/input'))`.
- Listeners on the `<img>`: `click`, `mousemove`, `mousedown`, `mouseup`.
- Coordinates are computed relative to `img.getBoundingClientRect()` and `parseInt`-truncated.
- Message: `{ "Type": "click|move|down|up", "X": int, "Y": int, "DisplaySize": { "width": img.width, "height": img.height } }`.
- Keyboard: a visible `#fakeKeyboard` text input (so touch devices like the Tesla browser can summon the on-screen keyboard) forwards `keyup`/`keypress` as `{Type, Key, KeyCode}` over `/ws/input`; the field is cleared after each keyup. No reconnect/onclose handling on the input socket.

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
