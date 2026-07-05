# Native H264 Display Encoder

- Date: 2026-07-05
- Feature: screen-capture
- Related code: `TeslaPCInterface/H264MediaFoundationEncoder.cs`, `TeslaPCInterface/DisplayWebSocket.cs`, `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/display-h264-worker.js`, `TeslaPCInterface/display-client.js`

## Context

The first H264 display implementation used a persistent ffmpeg process. Debug review found two
structural problems: x264 could fail when baseline profile was paired with an implicit 4:4:4
output pixel format, and the server forwarded arbitrary stdout pipe reads as WebSocket payloads.
Pipe reads are not H264 frame boundaries, so the browser could receive partial NAL units or
multiple frames in one message.

The desired direction is native Windows H264 encoding for live display streaming instead of an
external ffmpeg encoder.

## Decisions

- Replaced `H264FfmpegEncoder` with `H264MediaFoundationEncoder`, a direct wrapper around the
  Windows Media Foundation H264 encoder MFT (`CLSID_CMSH264EncoderMFT`, `mfh264enc.dll`).
- The encoder uses `MFVideoFormat_NV12` input and `MFVideoFormat_H264` output. `ImageStreamingServer`
  still captures/encodes JPEG for MJPEG clients, but when H264 clients are present it extracts
  tightly-packed BGR24 and `H264MediaFoundationEncoder` converts BGR24 to NV12 before `ProcessInput`.
- `DisplayWebSocket` now broadcasts each native encoder output sample as one WebSocket payload.
  `MFVideoFormat_H264` samples contain start codes, interleaved SPS/PPS, and one complete picture,
  which matches WebCodecs access-unit expectations better than ffmpeg stdout chunks.
- H264 availability is reported by `H264MediaFoundationEncoder.IsAvailable`, not ffmpeg discovery.
  If the native encoder is unavailable, H264 display clients fall back to MJPEG.
- `display-h264-worker.js` now treats each WebSocket H264 payload as a complete Annex-B access
  unit. It configures `VideoDecoder` from SPS/PPS NAL units and decodes IDR access units as key
  chunks and non-IDR access units as delta chunks.
- `display-client.js` now logs and exposes H264 worker/WebCodecs errors through
  `window.__teslaPcDebug.errors` when the DVR probe is installed.

## Alternatives Considered

- Keep ffmpeg and add `-pix_fmt yuv420p`: this would fix the immediate baseline/4:4:4 encoder
  failure but would still require a real Annex-B access-unit parser around stdout.
- Move all H264 framing to the browser worker: this would still send an ambiguous byte stream
  over WebSocket and keep server/client responsibilities unclear.
- WebRTC media tracks: rejected for this application path because the existing Tesla-compatible
  display model avoids `<video>` and browser-native playback lockout behavior.

## Consequences

- Positive: live H264 no longer depends on ffmpeg installation or stdout chunk timing.
- Positive: the WebSocket contract is now one complete H264 access unit per display payload.
- Positive: H264 availability is a Windows capability check (`mfh264enc.dll`/Media Foundation).
- Negative: native H264 is Windows-only; non-Windows development builds report H264 unavailable.
- Negative: BGR24-to-NV12 conversion is currently managed CPU work inside the capture loop.
- Negative: H264 media-mode playback still does not encode media JPEG frames; H264 display clients
  receive no media frames unless they reconnect/fall back to MJPEG.
