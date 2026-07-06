# Silence keepalive — prevent Tesla radio handoff during show gaps

- Date: 2026-07-05
- Feature: audio-capture
- Related code: `AudioStreamingServer.cs`

## Context

Short silent passages (~1 s) in a show caused the Tesla in-car browser to stop playing the
WebSocket audio stream and fall back to the vehicle's default radio until PC sound resumed.
WASAPI loopback stops firing during silence; the existing keepalive injected zeros too slowly
(20 ms poll vs ~10 ms WASAPI chunks) and only after 250 ms idle, letting the client buffer
drain and strict media sessions treat the stream as inactive.

## Decisions

- `SilenceKeepaliveStartMs` reduced from `250` to `100` ms.
- `KeepaliveIntervalMs` reduced from `20` to `10` ms to better match real-time PCM rate.
- `KeepaliveChunkBytes()` falls back to a format-derived 10 ms chunk when `_typicalBufferBytes`
  is not yet known.
- Float keepalive chunks write `1e-5f` on the first sample (`SilenceKeepaliveMarkerLevel`) so
  all-zero PCM is not mistaken for a dead stream on the Tesla browser.

## Trade-offs

- Positive: continuous PCM delivery through multi-second show-silence gaps (up to 30 s cap).
- Negative: imperceptible marker is a pragmatic client-compat hack, not true audio content.
- Unchanged: keepalive still skips when the broadcast queue is non-empty to avoid delaying real
  audio after silence ends.