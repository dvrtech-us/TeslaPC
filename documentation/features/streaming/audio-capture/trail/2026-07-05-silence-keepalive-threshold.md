# Raise silence-keepalive idle threshold

**Date:** 2026-07-05

## Problem

The initial keepalive fired after only 10 ms of loopback idle — shorter than normal gaps
between WASAPI `DataAvailable` callbacks during active playback. Silence frames were injected
between real audio buffers, causing severe stutter/distortion. Unbounded injection also queued
silence ahead of the broadcaster, delaying real audio when sound returned.

## Decision

- `SilenceKeepaliveStartMs = 250` — only treat loopback as "silent" after a quarter-second
  without `DataAvailable`.
- `KeepaliveIntervalMs = 20` — poll interval (unchanged semantics, slightly less CPU).
- Skip injection when `_audioDataQueue` is non-empty so keepalive cannot run ahead of broadcast.

## Files

- `TeslaPCInterface/AudioStreamingServer.cs` — `SilenceKeepaliveAsync`