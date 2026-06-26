# Audio Capture — Behavior Baseline

Known-good invariants for audio capture and streaming. Update only when intended behavior changes.

## Pipeline Invariants

- The capture format (sample rate, bit depth, channels) is **discovered from the device at runtime** and never hardcoded server-side.
- **No resampling happens on the server.** All resampling is client-side, triggered only when the browser's AudioContext rate differs from the server rate.
- Audio chunks are enqueued **only** when `e.ByteCount > 0` **and** at least one client is connected.
- Each binary WebSocket frame is one raw PCM chunk corresponding to exactly one CSCore `DataAvailable` event — no WAV header, no length prefix, no re-framing.
- The **first** frame to a new client is always the JSON format descriptor (text); all later frames are binary PCM.
- The server-to-client stream is broadcast: one dequeued buffer is fanned out to all open clients.

## Client Playback Rules

- The client buffer is capped at `playbackSampleRate * channels * 2` samples (~2 seconds); on overflow the oldest samples are dropped, aligned to a channel boundary (never splitting a frame).
- PCM normalization divisors: pcm16 `32768.0`, pcm24 `8388608.0`, pcm32 `2147483648.0`.
- On underrun, `process()` outputs silence and keeps the worklet alive.

## Access Control

- `/ws/audio` requires no authentication.

## Failure Behavior

- Per-client send exceptions are swallowed; the client is removed from `_clients` in the handler's finally block.
- The server receive path only watches for the close frame; any other client data is ignored.
- Browser audio requires a user gesture, so the stream is only consumed after the user clicks **Start playback**.
