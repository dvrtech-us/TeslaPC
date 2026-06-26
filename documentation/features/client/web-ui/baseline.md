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
- No keyboard handling exists in the client.

## Audio Rules

- The AudioWorklet path is preferred; on `addModule`/node failure the client falls back to `AudioBufferSourceNode` scheduling.
- The `AudioContext` is created at the server's reported sample rate; if the browser clamps it, a warning is logged and client-side resampling engages.
- The **Start playback** button transitions: `Start playback` → `Connecting...` (disabled) → `Restart playback`.

## Failure Behavior

- Audio socket `onerror`/`onclose` → `resetAudioConnection()` re-enables the button; **no automatic reconnect**.
- The input socket and MJPEG `<img>` have **no reconnect logic**; a dropped connection fails silently / shows a broken image until reload.
