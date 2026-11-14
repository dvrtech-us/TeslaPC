# Capture chain and MJPEG framing in Rust

- Date: 2026-11-07
- Feature: screen-capture
- Related code: `teslapc/src/capture_loop.rs`, `teslapc/src/decisions/capture.rs`, `teslapc/src/decisions/stream.rs`, `teslapc/src/encode.rs`

## Context

The C# capture loop used WGC, then DXGI, then GDI, and wrote MJPEG with boundary `boundary`.

## Decisions

Scale, height clamp, MJPEG headers, backend order, and the black-frame handoff are pure functions. JPEG is encoded in-process at quality 60. GDI `BitBlt` produces frames when WGC and DXGI do not return one.

## Alternatives Considered

- Hardware H.264: out of scope. `/stream` stays MJPEG.

## Consequences

- Positive: framing and scale are tested without a GPU.
- Negative: WGC and DXGI do not yet copy a frame; they fail over to GDI.
