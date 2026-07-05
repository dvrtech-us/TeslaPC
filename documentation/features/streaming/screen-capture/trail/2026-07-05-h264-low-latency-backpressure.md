# H264 Low-Latency Backpressure

- Date: 2026-07-05
- Feature: `streaming/screen-capture`
- Related code: `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/DisplayWebSocket.cs`, `TeslaPCInterface/display-client.js`, `TeslaPCInterface/WebServer.cs`

## Context

Native Media Foundation H264 was producing valid frames, but live use felt too slow. The server
still JPEG-encoded every captured frame even when all display clients had negotiated H264, and
`DisplayWebSocket` launched fire-and-forget `SendAsync` calls without per-client backpressure.
A slower browser could accumulate stale display sends, which is the wrong failure mode for
remote desktop.

## Decision

- `ImageStreamingServer` now computes JPEG demand per tick. It encodes JPEG only when there is
  at least one HTTP `/stream` client or one MJPEG display WebSocket client.
- H264-only display sessions still resize once, then extract BGR24 for the native encoder, but
  skip `Image.Save(..., image/jpeg)` and do not update `_currentFrame`.
- `DisplayWebSocket` now serializes sends per client. A client has one send loop, one latest
  pending packet, and one preserved H264 keyframe packet.
- Codec/transport config changes are global for WebSocket display clients. `POST /config` closes
  display WebSockets when `displayRenderer` or `displayTransport` changes; the browser re-reads
  `/config` and reconnects display.

## Verification

After deploying the patch to `DESKTOP-2AE5KJB`, the DVR/Chrome probe against
`https://192.168.12.235:8443/` reported:

```text
displayRenderer=h264
displayTransport=websocket
h264Available=true
displayFormat.renderer=h264
displayFrames=994
audioFrames=1485
errors=[]
```

Manual observation after the patch was that H264 felt substantially better.

## Consequences

- Positive: H264-only live desktop avoids unnecessary JPEG work.
- Positive: slow clients receive the newest display frame instead of consuming an old backlog.
- Positive: connected WebSocket clients converge on the current global codec setting.
- Tradeoff: a slow H264 client can drop delta frames. IDR packets are preserved so decoder state
  can recover.
