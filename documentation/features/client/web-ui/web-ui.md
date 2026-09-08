# Web UI

The single-page browser client (`index.html`) that renders the remote screen, plays system
audio, and forwards mouse input. Vanilla JavaScript — no frameworks, no build step. All CSS
is in `/style.css`; shared A/V PTS scheduling lives in `av-scheduler.js`; display streaming
logic lives in `display-client.js`; H264 decode/render logic lives in `display-h264-worker.js`;
audio decode logic lives in `PCMPlayerProcessor.js`.

## User Flow

1. The browser loads `/` (served as `index.html`). `#streamImg` has no `src` yet and
   `#streamCanvas` is hidden (black screen).
2. The input WebSocket (`/ws/input`) opens on page load; mouse actions over the image are forwarded once frames arrive.
3. The user taps the **screen overlay** (“Tap to connect”, user gesture):
   `TeslaDisplay.start()` reads `/config` and opens either `/stream` (legacy HTTP MJPEG) or
   `/ws/display?renderer=mjpeg|h264`; the same tap opens `/ws/audio` so display and audio begin
   together. If `/ws/audio` or `/ws/input` drops after connect, `#disconnectPanel` warns the
   user and offers a **Refresh page** button. A `/ws/display` close reconnects the display
   transport in place so global codec changes can apply without interrupting audio/input.
4. MJPEG renders via `<img>`; H264 renders via `<canvas>` using WebCodecs `VideoDecoder` in
   `display-h264-worker.js`. There is still **no `<video>`** element (Tesla driving lockout).

## Technical Flow

### Layout

- `<title>Remote Desktop</title>`; styling comes from the shared `/style.css` (dark in-car touch theme), not inline styles; a no-zoom `viewport` meta is set for touch.
- Video: `<img id="streamImg">` and `<canvas id="streamCanvas">` inside `<div class="screen">`
  (flex-centered, `object-fit: contain`). MJPEG uses the image; H264 hides the image and shows
  the canvas.
- Controls live in a fixed bottom `<div class="controlbar">` with large `.btn` targets: the
  **Keyboard** input (`#fakeKeyboard`), **Files** (→ `/list.html`), **Settings** (→
  `/config.html`), **stream resolution** (`#resBtn` opens `#resPicker` with 480p/720p/1080p
  buttons), **video codec** (`#codecBtn` opens `#codecPicker` with MJPEG/H264 buttons), and
  **Fit screen**. Native `<select>` popups are avoided where Tesla browser behavior is unreliable.
  Connect A/V via the **Tap to connect** screen overlay (no control-bar playback button).

### URL helper

- `getWsUrl(path)` chooses `wss://` on HTTPS and `ws://` on HTTP, always against `location.host` (same origin).

### Display (`display-client.js`)

- `TeslaDisplay.start(onDisconnect)` fetches `/config`. If `displayTransport` is `http`, it sets
  `#streamImg.src = "/stream?_=" + Date.now()` and hides `#streamCanvas`.
- Default display transport is WebSocket: `new WebSocket(getWsUrl('/ws/display') +
  '?renderer=' + encodeURIComponent(displayRenderer))`.
- The first WebSocket message is JSON format metadata. Binary messages are parsed by stripping
  the 8-byte little-endian host PTS prefix.
- MJPEG WebSocket payloads become `Blob([payload], { type: "image/jpeg" })`, assigned to
  `#streamImg` via a short-lived object URL.
- H264 WebSocket payloads are copied and transferred to `display-h264-worker.js` as
  `{ h264Data: ArrayBuffer, hostPtsUs, schedulerSync }` when `formatVersion >= 2`.
- MJPEG WebSocket frames still blit immediately to `<img>` (no PTS scheduler in phase 3a).
- If the display WebSocket closes unexpectedly, `display-client.js` waits 250 ms, re-reads
  `/config`, and reconnects display only. This is how server-side renderer/transport changes
  apply to connected WebSocket clients.
- `TeslaDisplay.stop()` closes the display socket and clears pending reconnect/blob state, but
  keeps the H264 worker alive if it was already created. `#streamCanvas` can only be transferred
  to an `OffscreenCanvas` once, so reconnects reuse the existing worker.
- `TeslaDisplay.getStreamSize()` returns the rendered video rectangle and letterbox offsets for
  input coordinate mapping. It uses `<img>.naturalWidth/Height` for MJPEG and the negotiated
  display size for H264.

### H264 worker (`display-h264-worker.js`)

- The main thread transfers `#streamCanvas` to an `OffscreenCanvas` and starts a worker only when
  `VideoDecoder` is available.
- The worker creates a WebGL/WebGL2 context, configures a `VideoDecoder` after seeing SPS/PPS
  NAL units, and draws decoded `VideoFrame` objects to the canvas texture.
- H264 payloads are treated as complete Annex-B access units. Parameter-set-only payloads update
  decoder configuration. Access units containing IDR NAL units are decoded as key chunks; access
  units containing non-IDR slices are decoded as delta chunks after a key frame has been accepted.
- The worker drops stale decoded frames and skips delta chunks when the decode/render queues are
  backlogged.
- When `schedulerSync` is present, the worker imports `av-scheduler.js`, maps host PTS to
  `EncodedVideoChunk.timestamp`, and delays WebGL canvas blit until
  `TeslaAvScheduler.hostPtsToPlayWallMs(hostPtsUs)` (default 200 ms target delay from anchor).
- Worker errors are posted back to `display-client.js`, logged to the console, and copied into
  `window.__teslaPcDebug.errors` when the DVR debug probe is installed.

### A/V scheduler (`av-scheduler.js`)

- Loaded before `display-client.js` on `index.html`; also `importScripts` in
  `display-h264-worker.js`.
- `TeslaAvScheduler.reset()` runs at the start of `startAudioPlayback()` (session boundary).
- First `hostPtsUs` seen on the main thread anchors `anchorHostPtsUs` + `anchorWallMs`; each H264
  frame posts `schedulerSync` so the worker shares the same anchor.
- `delayUntilPlayMs(hostPtsUs)` drives audio `setTimeout` delivery and H264 render `setTimeout`.

### Input (`mapPoint` / `sendInput`, `index.html`)

- The input WebSocket opens on load: `new WebSocket(getWsUrl('/ws/input'))`.
- **Mouse** listeners on the `<img>`: `click`, `mousemove`, `mousedown`, `mouseup`, `wheel`, and
  `contextmenu`→`rightclick` (desktop right-click, `preventDefault`ed).
- **Wheel / scroll**: `wheel` events are captured and forwarded as `{ Type: "wheel", Delta, X, Y, DisplaySize }`.
- **Touch** listeners (Tesla/tablets), all `preventDefault`ed (`passive:false`) to stop native page
  scroll/zoom and avoid duplicate synthesized mouse events:
  - **quick tap** → `down`+`up` (left click)
  - **press + drag** (move > ~12px) → `down` then `move`…`up` (left-button drag)
  - **press & hold ~0.5s** → `rightclick` (long-press; the `down` is deferred so a hold isn't also a
    left press)
  - **two or more simultaneous touches** (two-finger vertical drag) → wheel scroll messages. The
    average Y delta of the first two touches is scaled, negated (natural direction: content follows
    the fingers), and sent via the wheel path. Single-touch
    logic (timer, drag, tap) is bypassed while multi-touch is active. `touchcancel` is also handled.
- `mapPoint(clientX, clientY)` converts a viewport point to the **actual video rectangle**
  (accounting for the `object-fit: contain` letterbox via `naturalWidth/Height`); points in the
  black bars return null and are ignored. Used by both `sendInput` (pointer) and `sendWheel`.
- Message protocol now includes wheel:
  - Pointer: `{ "Type": "move|down|up|rightclick", "X": ..., "Y": ..., "DisplaySize": ... }`
  - Wheel:   `{ "Type": "wheel", "Delta": number, "X"?, "Y"?, "DisplaySize"?: ... }`
- New helper `sendWheel(delta, clientX, clientY)` centralizes wheel transmission.
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
6. Subsequent (binary) messages → `parseAudioFrame()` strips the 8-byte host PTS when
   `formatVersion >= 2`, then `scheduleAudioChunk(pcm, hostPtsUs)` delays delivery by
   `TeslaAvScheduler.delayUntilPlayMs` before posting to the worklet or fallback scheduler.
   Format v1 frames play immediately (no PTS prefix).

### Audio fallback (`playPcmChunkFallback`, `index.html:254`)

- `decodePcmToFloat32` → `resampleFloat32` → `createBuffer` → deinterleave → schedule via `AudioBufferSourceNode.start(scheduledTime)`.
- If `scheduledTime < currentTime`, reset to `currentTime + 0.02` (20 ms look-ahead) to smooth gaps.

## Key Functions

| Function | File | Responsibility |
|----------|------|----------------|
| `TeslaAvScheduler.*` | `av-scheduler.js` | Anchor host PTS, map to playout wall time / decoder timestamp |
| `getWsUrl(path)` | `index.html` / `display-client.js` | Build same-origin `ws://`/`wss://` URL |
| `TeslaDisplay.start(onDisconnect)` | `display-client.js` | Start HTTP MJPEG or WebSocket MJPEG/H264 display transport from `/config` |
| `TeslaDisplay.stop()` | `display-client.js` | Close display socket, clear reconnect/blob state, keep any transferred H264 worker alive |
| `TeslaDisplay.getStreamSize()` | `display-client.js` | Return rendered display size and letterbox offsets for input mapping |
| `handleEncodedData(data)` | `display-h264-worker.js` | Parse Annex-B H264 access units, configure decoder, submit WebCodecs chunks |
| `sendMouseEvent(type, event)` | `index.html:295` | Forward image-relative mouse coords to `/ws/input` |
| `sendKey(key, code)` | `index.html` | Send `{Type:'key', Key, KeyCode}` over `/ws/input` |
| `sendText(text)` | `index.html` | Forward a text run one character at a time; maps `\r`/`\n`→Enter, `\t`→Tab |
| `isEditable(el)` | `index.html` | Return true when `el` is an `INPUT`, `TEXTAREA`, or has `contentEditable` set |
| `isNamedKey(event)` | `index.html` | Return true when the key matches `namedKeys` or `/^F\d{1,2}$/` |
| `parseAudioFrame(arrayBuffer)` | `index.html` | Split format v2 audio into `{ pts, pcm }` |
| `scheduleAudioChunk(pcm, hostPtsUs)` | `index.html` | Delay audio delivery to worklet/fallback by host PTS |
| `startAudioPlayback()` | `index.html:86` | Negotiate format and start AudioWorklet (or fallback) |
| `playPcmChunkFallback(rawData, hostPtsUs)` | `index.html:254` | Schedule PCM via `AudioBufferSourceNode` when no AudioWorklet |
| `decodePcmToFloat32` / `resampleFloat32` | `index.html:177,219` | Decode + linear-interpolation resample (mirror of the worklet) |

## Routes and Access Control

The client consumes `/` (HTML), `/config` (display settings), `/stream` (legacy HTTP MJPEG),
`/ws/display` (WebSocket MJPEG/H264), `/ws/input` (input), and `/ws/audio` (audio). All are
same-origin and unauthenticated.

## Integration Points

- Served by [web-server](../../server/web-server/web-server.md) static-file handling.
- Renders [screen-capture](../../streaming/screen-capture/screen-capture.md) and drives [input-control](../../server/input-control/input-control.md) and [audio-capture](../../streaming/audio-capture/audio-capture.md).

## Database Schema

None.

## SQL Artifacts

None.
