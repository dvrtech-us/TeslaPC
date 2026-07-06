# Continuous audio stream pump

- Date: 2026-07-05
- Feature: audio-capture
- Related code: `AudioStreamingServer.cs`

## Context

WASAPI loopback does not fire `DataAvailable` during digital silence in a show. A reactive
silence keepalive (inject only after N ms idle, stop after 30 s) still left gaps where strict
clients (Tesla browser) handed audio back to vehicle radio.

## Decisions

- Replace separate `BroadcastAudioAsync` + `SilenceKeepaliveAsync` with one
  `ContinuousStreamPumpAsync` on a 10 ms clock.
- Live loopback mode always sends one PCM chunk per tick: dequeue real audio when present, else
  synthetic silence (`CreateSilenceChunk`).
- Remove idle start threshold and 30 s idle cap — the stream stays continuous for the whole session.
- Cap the PCM queue at 8 chunks; drop oldest on burst enqueue to limit latency.
- Media mode unchanged: pump only sends queued file PCM (no synthetic silence between ffmpeg chunks).

## Trade-offs

- Positive: WebSocket PCM rate stays steady through multi-second show-silence gaps.
- Negative: occasional 10 ms synthetic silence may be inserted between sparse WASAPI buffers during
  active playback if loopback delivery hiccups; queue cap drops excess burst buffers instead of
  delaying the stream.