# H264 MF low-latency + GOP/bitrate tuning

- Date: 2026-07-05
- Feature: screen-capture
- Related code: `H264MediaFoundationEncoder.cs`, `DisplayWebSocket.cs`, `AppSettings.cs`

## Context

Optimization #2 reduced NV12 conversion cost, but the Windows H264 MFT still used default
encoder settings tuned more for recording than live remote desktop. Long default GOPs slow decoder
recovery after frame drops, and the server had no way to force an IDR when a new H264 client
connected.

## Decisions

- Set `MF_MT_MAX_KEYFRAME_SPACING` on the output media type to match the configured GOP duration.
- Query `ICodecAPI` on the encoder MFT and best-effort set:
  - `CODECAPI_AVEncCommonLowLatency`
  - `CODECAPI_AVLowLatencyMode`
  - `CODECAPI_AVEncMPVDefaultBPictureCount = 0`
  - `CODECAPI_AVEncMPVGOPSize` (default one second = `fps` frames)
- Default bitrate formula changed from `width * height * fps / 8` to `width * height * fps * 0.10`
  (still clamped). Optional overrides via `.env`:
  - `TESLAPC_H264_GOP_FRAMES` (`1`–`300`)
  - `TESLAPC_H264_BITRATE` (`500_000`–`20_000_000` bps)
- Added `H264MediaFoundationEncoder.RequestKeyframe()` using
  `CODECAPI_AVEncVideoForceKeyFrame`; `DisplayWebSocket` calls it when a new H264 client connects.

## Trade-offs

- Positive: faster IDR recovery and quicker decode startup for new clients.
- Positive: lower encoder buffering via low-latency mode.
- Negative: shorter GOP increases keyframe rate and bandwidth unless bitrate is tuned down.
- Negative: some `ICodecAPI` properties may be ignored on certain Windows builds — failures are
  logged and non-fatal.

## Verification

- Release build on `DESKTOP-2AE5KJB`
- DVR teslapc probe against `https://192.168.12.235:8443/`
- Console should log `gop=` and `bitrate=` on encoder start