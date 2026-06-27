# Initial Documentation of Audio Capture

- Date: 2026-06-26
- Feature: audio-capture
- Related code: `TeslaPCInterface/AudioStreamingServer.cs`, `TeslaPCInterface/PCMPlayerProcessor.js`

## Context

Documents the as-built WASAPI loopback audio pipeline when the documentation system was
introduced. Recent commit history references an "audio resample" change; this entry records
that resampling is **client-side**, with the server emitting the device's native format.

## Decisions

- Documented runtime format discovery and the format-descriptor handshake (text frame first, then raw binary PCM).
- Recorded the no-server-side-resampling design and the client-side linear-interpolation resample that activates only on a sample-rate mismatch.
- Captured the broadcast model and the ~2-second client buffer cap with channel-aligned overflow dropping.

## Alternatives Considered

- **Server-side resampling to a fixed rate** — rejected in the implemented design; pushing resampling to the client keeps the server format-agnostic and avoids a fixed-rate assumption.
- **Encoding to a compressed codec (e.g. Opus)** — not implemented; raw PCM is streamed as captured.

## Consequences

- Positive: the server adapts to any device format without configuration; the client handles rate matching.
- Negative: raw PCM is bandwidth-heavy; this is acceptable on a LAN/hotspot link but is documented for future codec consideration.
