# Silence keepalive for loopback audio gaps

**Date:** 2026-07-05

## Problem

During short silent passages in a show, browser audio stopped decoding and often failed to
recover when sound returned. WASAPI loopback stops firing `DataAvailable` during silence, so
the WebSocket stream went quiet, the client buffer drained, and the AudioWorklet underrun path
returned without writing output (risking graph stall / `AudioContext` suspend on some browsers).

## Decision

1. **Server:** `SilenceKeepaliveAsync` injects zero-filled PCM chunks every 10 ms while
   loopback has been idle for at least 10 ms but less than 30 s. After 30 s of continuous
   idle, keepalive stops until real audio returns.
2. **Client worklet:** On underrun, explicitly `fill(0)` output channels (matches baseline).
3. **Client UI:** Call `audioContext.resume()` when binary frames arrive if the context was
   suspended.

## Constants

- `KeepaliveIntervalMs = 10`
- `SilenceKeepaliveMaxSeconds = 30`
- Keepalive chunk size = last observed `DataAvailable` `ByteCount`