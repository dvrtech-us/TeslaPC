# Aspect-preserving stream scaling

- Date: 2026-06-27
- Feature: screen-capture
- Related code: `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/TeslaPcService.cs`

## Context

The stream output was `min(screenW, 1280) × min(screenH, 720)` — each dimension capped
independently. That preserved aspect only for 16:9 screens; a non-16:9 screen above the caps (e.g.
1920×1200) was **distorted** (squished to 1280×720 instead of 1280×800). The cap box was also
derived from the screen size at startup, so it didn't follow later resolution changes.

## Decisions

- Scale the live captured frame **uniformly** into a fixed `1280×720` cap box:
  `scale = min(1, 1280/screenW, 720/screenH)`, `out = round(screen × scale)`. Preserves aspect
  ratio, never upscales (screens ≤ the box stream at native size).
- `TeslaPcService` now passes a fixed `1280×720` cap (not `min(screen, …)`), so the per-session
  scale uses the **live** `DXGI CaptureSize` and adapts to resolution changes without a restart.

## Consequences

- Positive: the stream always matches the screen's shape (no distortion on any aspect ratio); a
  16:10 screen streams 1152×720, 1080p streams 1280×720, smaller screens stream native.
- Cap kept at 720p (user choice) for bandwidth; the browser's `object-fit: contain` then letterboxes
  to the viewport without ever distorting.
