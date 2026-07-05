# BGR→NV12 fast path for native H264 encoding

- Date: 2026-07-05
- Feature: screen-capture
- Related code: `Bgr24ToNv12Converter.cs`, `H264MediaFoundationEncoder.cs`, `ImageStreamingServer.cs`, `DisplayWebSocket.cs`

## Context

The native MF H264 encoder requires NV12 input. The capture loop already holds DXGI/GDI frames as
`Format32bppArgb` bitmaps. The previous path converted 32bpp → BGR24 (often via `Graphics.DrawImage`
to 24bpp) and then converted BGR24 → NV12 inside `H264MediaFoundationEncoder` on every tick. That
added an extra color conversion and per-frame NV12 allocations.

## Decisions

- Extract BT.601 BGR/BGRA → NV12 into `Bgr24ToNv12Converter` with scalar 4-pixel unrolled loops
  and explicit 2×2 UV block indexing.
- Reuse one `_nv12Scratch` byte array per `H264MediaFoundationEncoder` session instead of allocating
  NV12 per frame.
- Add `EncodeBgra32Frame(IntPtr scan0, int stride)` on the encoder and `EncodeH264Bgra32` on
  `DisplayWebSocket`.
- When `needsResize` is false (common at native resolution), lock `srcImage` and feed BGRA directly
  to the encoder. When resizing, keep the existing `BitmapToBgr24(scaledImage)` → `EncodeH264`
  path.
- Enable `AllowUnsafeBlocks` in `TeslaPCInterface.csproj` for pointer-based BGRA reads.

## Trade-offs

- Positive: removes one full-frame color conversion on the no-resize path; fewer allocations in the
  hot loop.
- Negative: still managed CPU NV12 work — no SIMD/AVX2 or GPU color conversion yet.
- Negative: resized streams still pay BGR24 pack cost because `scaledImage` is `Format24bppRgb`.

## Follow-ups

- SIMD or D3D11 compute for BGRA → NV12 when profiling shows conversion still dominates.
- Consider MF low-latency GOP/bitrate tuning (optimization #3).