# Retry the first backend after a fallback

**Date:** 2026-11-13

## Problem

The lab laptop's log showed `[Capture] Dxgi frame is black; trying Gdi.` during normal use
from the car. A real black picture (for example a full-screen transition) hands capture to
GDI, and the loop only moved forward through backends, so it stayed on GDI, the slowest
one, until the next `capture_epoch` change or desktop switch.

## Decision

Off the first backend, the loop tries the first backend again after `FALLBACK_RETRY_MS`
(30 s). A kept frame ends the retry and resets the wait. A retry that falls back again
doubles the wait up to `FALLBACK_RETRY_MAX_MS` (5 min), so content that is black under DXGI
but visible under GDI costs one short attempt at a decreasing rate. The stored JPEG is kept
across the attempt.

Verified on the lab laptop as the session host: a full-screen black window for 4 s produced
`Dxgi frame is black; trying Gdi.`, then 30 s later `Trying Dxgi again after 30 s on Gdi.`
and `Back on Dxgi.`. The stream delivered 545 frames in about 52 s across the test.

## Constants

- `FALLBACK_RETRY_MS = 30000`
- `FALLBACK_RETRY_MAX_MS = 300000`

## Files

- `teslapc/src/capture_loop.rs`
- `teslapc/src/decisions/capture.rs` (`fallback_retry_due`, `next_fallback_wait_ms`)
- `teslapc/tests/protocol.rs`
