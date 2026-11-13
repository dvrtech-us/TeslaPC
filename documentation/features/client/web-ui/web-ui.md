# Web UI

The browser UI is the files in `teslapc/assets/`, compiled into the binary with `include_bytes!`. `assets::asset` returns the bytes and a content type. `teslapc.png` is `image/png` and is not passed through a string decoder.

`index.html` opens `/stream`, `/ws/audio`, and `/ws/input` on the same origin (audio: see Audio player below). The main-page control bar has Keyboard, Files, Settings, Fit screen (`#fitBtn`, `fitScreen(this)`), and a read-only indicator (`#streamInfo`). There is no resolution control on the main page; it lives on Settings.

Fit screen calls `GET /display/match?w=&h=` with the `.screen` size in device pixels (`clientWidth/Height x devicePixelRatio`), one request at a time. The button is disabled until the response or a 5 s timeout. Its label is replaced for 4 s by `PC set to <w>x<h>` (`changed: true`; the server also sets the stream height, returned as `streamHeight`), `Unchanged <w>x<h>` (`changed: false`, which also covers a refused change), or `Change failed` (network error, non-2xx, non-JSON, or timeout). A newer message cancels the older timer; the label is then restored. `#fitBtn` has a fixed `190px` width so the bar does not jump; the messages are short so they fit it. The Fit screen icon is inline SVG (the Tesla browser has no glyph for U+26F6). The keyboard field keeps at least `150px`.

`#streamInfo` shows `<naturalWidth>×<naturalHeight> · <format>` of `#streamImg`, polled every 1 s, for example `1280×720 · MJPEG`. The format comes from the `VIDEO_STREAM` constant (`{ path: '/stream', format: 'MJPEG' }`), which `startVideoStream` also uses. Before a size exists, or while the connection-lost panel shows, it shows `—`. It has `pointer-events: none`, is not focusable, and does not shrink; it does not appear on `play.html`. Pointer events include `DisplaySize`. Paste is expanded in the page into per-character key messages; the server mapping is `expand_paste` / `map_key`.

## Audio player

`index.html` (inline client, `startAudioPlayback`) and `audio-client.js` (`startTeslaAudio`, used by `play.html`) are behaviorally identical. Both read the one JSON format frame from `/ws/audio`, create an `AudioContext` at the source rate, and load `PCMPlayerProcessor.js` with `audioWorklet.addModule`. Binary packets are dropped until the player that will be used is ready (the worklet node is created, or `addModule` failed and the fallback is chosen, and `resume()` has resolved), so nothing plays underneath the worklet at startup. Packets go to the worklet with `port.postMessage(data, [data])` (the ArrayBuffer is transferred, not copied).

`PCMPlayerProcessor.js` holds the only buffer control. It decodes `float`, `pcm16`, `pcm24`, and `pcm32`, resamples per packet with linear interpolation when the context rate differs from the source rate (unchanged), and writes into a fixed ring of 1 s of interleaved samples at the context rate. `process()` does not allocate.

| Option (`processorOptions`) | Default | Meaning |
|---|---|---|
| `TARGET_MS` | 100 | standing buffer that trims steer to |
| `START_MS` | 100 | warm-up level; the player waits for `START_MS` plus the largest packet seen (capped at 200 ms) |
| `TRIM_ABOVE_MS` | 200 | trim when the 2 s minimum is above this |
| `HARD_MAX_MS` | 500 | skip at once when the instantaneous level is above this |
| `MEASURE_WINDOW_MS` | 2000 | minimum-level window (8 buckets of 250 ms) |

States: `warming` (silence until the warm-up level), `playing`, `starved` (a block could not be filled: the frames left are played, the rest is silence, one underrun is counted, and the next block is `warming`). The minimum level is taken after each block's read while playing; the window is valid only after 8 full buckets since the last reset, and it resets on every state change and after every trim.

- Trim: valid windowed minimum > `TRIM_ABOVE_MS` removes `minMs - TARGET_MS` of whole frames from the read side.
- Hard: level > `HARD_MAX_MS` (checked before every block, in any state) removes `level - TARGET_MS`.
- Overflow: a packet that does not fit in the ring drops the oldest frames so the newest `TARGET_MS` (old tail plus the packet) remain. A packet larger than the ring keeps its last 1 s.
- Each skip blends the 5 ms (240 frames at 48 kHz) before the gap into the 5 ms after it, in place, frame-aligned for all channels; each skip counts one trim.

Once a second the worklet posts `{ type: 'stats', bufferedMs, minMs, underruns, trims }` (integers; `minMs` is the windowed minimum, or the current level until the window is valid). Every 10 s, while the socket is open, the page sends the text frame `{"type":"stats","bufferedMs","minMs","underruns","trims","contextRate"}` on `/ws/audio` with the latest worklet values and `audioContext.sampleRate`; nothing is sent before the worklet's first stats message.

Fallback (no AudioWorklet): each packet becomes an `AudioBufferSourceNode` scheduled at `scheduledTime`. Live sources are kept in a list and removed on `ended`. When `scheduledTime - currentTime` exceeds 0.25 s, the listed sources are stopped, `scheduledTime` becomes `currentTime + 0.1`, and one trim is counted. When `scheduledTime` has fallen behind `currentTime`, it becomes `currentTime + 0.02` and one underrun is counted (not for the first packet). Its stats report the scheduled lead in ms as both `bufferedMs` and `minMs`.

The worklet is tested in Node without a browser: `node teslapc/tests/js/pcm_player.test.js` (no npm packages; exit code 1 on failure). It simulates steady delivery, a 1.5 s stall and burst, a source clock 0.2% fast and 0.2% slow for 10 minutes, ±30 ms jitter, a 15 s gap with no packets, 1/10/100 ms packets, and 44.1 kHz resampled to 48 kHz, and checks order, channel alignment, crossfades, the standing buffer, and trim and underrun counts.

`list.html` is a template. The server replaces `{{GUTS}}`. `play.html` replaces `{{TITLE}}` and `{{FILEPATH}}`. `config.html` is the Settings page, including hotspot on/off.

HTML responses still rewrite `//LOCALHOST` to the request host and, on port `8443`, rewrite `ws://`/`http://` and the `8080` port.
