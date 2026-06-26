# Screen Capture & MJPEG Streaming

Captures the primary monitor and streams it to browsers as MJPEG over the `/stream` route.
A single shared capture loop feeds all connected clients. Capture prefers DXGI Desktop
Duplication (GPU) and falls back to GDI `CopyFromScreen`.

## User Flow

1. The browser loads `index.html`, which contains `<img src="/stream">`.
2. The browser opens the MJPEG stream; the server begins (or joins) the shared capture loop.
3. Each captured frame is JPEG-encoded once and pushed to every connected client.
4. When the last client disconnects, the capture loop stops and releases its resources.

## Technical Flow

### Per-client entry (`ImageStreamingServer.HandleStreamRequest`, `ImageStreamingServer.cs`)

1. `Interlocked.Increment(ref _clientCount)`.
2. `EnsureCaptureRunning()` starts the shared `"ScreenCapture"` background thread if not already alive (guarded by `lock (_frameLock)`).
3. A `MjpegWriter` is created with boundary `"boundary"`; `WriteHeader()` is called **synchronously on the request thread** (prevents http.sys 503).
4. `ThreadPool.QueueUserWorkItem(StreamToClient)` runs this client's send loop.

### Shared capture loop (`CaptureLoop` → `RunCaptureSession`)

1. `using var dxgiCapture = new DxgiScreenCapture()`; check `IsAvailable`.
2. Screen size from `dxgiCapture.CaptureSize` (DXGI) or `Screen.PrimaryScreen.Bounds` (GDI).
3. Output size clamped: `min(screen, _maxWidth/_maxHeight)`.
4. Allocate `srcImage` (`Format32bppArgb`) and, if resizing, `scaledImage` (`Format24bppRgb`).
5. Resolve the JPEG codec (`GetJpegCodec`) and set `Encoder.Quality = 60L`.
6. Per frame:
   - `dxgiCapture.TryCapture(srcImage)` or `srcGraphics.CopyFromScreen(...)`.
   - If resizing: `DrawImage` into `scaledImage`, then `scaledImage.Save(ms, jpegCodec, params)`. Else `srcImage.Save(...)`.
   - `PublishFrame(ms)`: copy bytes to `_currentFrame`, increment `_frameNumber`, `Monitor.PulseAll(_frameLock)`.
   - Pace: if `elapsed < Interval`, `Thread.Sleep(Interval - elapsed)` where `Interval = 1000 / fps`.
7. The inner loop continues while `_clientCount > 0` and not cancelled. `RunCaptureSession` ultimately returns false, ending the thread.

### Per-client send (`StreamToClient` → `TryWaitForLatestFrame`)

- Blocks on `Monitor.Wait(_frameLock, 1000)` until a newer `_frameNumber` is published.
- If a newer frame arrived while sending, the intermediate frame is **skipped** (slow clients always get the latest image).
- Sends via `MjpegWriter.Write(frame)`.

### DXGI capture (`DxgiScreenCapture.cs`)

- Public API: `IsAvailable` (bool), `CaptureSize` (Size), `TryCapture(Bitmap)` (bool), `Dispose()`.
- Init: `CreateDXGIFactory1` → find the output whose `DesktopCoordinates` matches the primary screen origin → `D3D11CreateDevice` (`BgraSupport`) → `DuplicateOutput`.
- `TryCapture`: `AcquireNextFrame(16ms)` → on `WaitTimeout` returns false; on `AccessLost`/`AccessDenied` calls `RecreateDuplication()` and returns false; copies the GPU texture to a cached CPU **staging texture**, maps it, and copies row-by-row (respecting `RowPitch`) into the target `Bitmap`.
- On any init failure the constructor sets `IsAvailable = false` and logs that GDI fallback will be used.

### MJPEG framing (`MjpegWriter.cs`)

- `WriteHeader()`: `Content-Type: multipart/x-mixed-replace; boundary=boundary`, `200`, `Cache-Control: no-cache, no-store, must-revalidate`, `Pragma: no-cache`, `SendChunked = true`, `KeepAlive = true`.
- `Write(jpeg)`: writes `\r\n--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: N\r\n\r\n`, then the JPEG bytes, then `\r\n`, then flushes.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `ImageStreamingServer` | `TeslaPCInterface/ImageStreamingServer.cs` | Shared capture loop, frame buffer, per-client send threads, JPEG encoding |
| `DxgiScreenCapture` | `TeslaPCInterface/DxgiScreenCapture.cs` | DXGI Desktop Duplication capture; signals unavailability for GDI fallback |
| `MjpegWriter` | `TeslaPCInterface/MjpegWriter.cs` | multipart/x-mixed-replace header + per-frame boundary framing |

## Constants

| Constant | Value | Location |
|----------|-------|----------|
| Frame interval | `1000 / fps` ms (33 ms at 30 FPS) | `ImageStreamingServer.cs:42` |
| JPEG quality | `60L` | `ImageStreamingServer.cs:157` |
| DXGI acquire timeout | `16` ms | `DxgiScreenCapture.cs:130` |
| Client wait timeout | `1000` ms | `ImageStreamingServer.cs:261` |
| Boundary string | `"boundary"` | `ImageStreamingServer.cs:63` |
| Source pixel format | `Format32bppArgb` | `ImageStreamingServer.cs:143` |
| Scaled pixel format | `Format24bppRgb` | `ImageStreamingServer.cs:146` |
| Scaling quality | `HighSpeed` / `Bilinear` / `SmoothingMode.None` | `ImageStreamingServer.cs:150-152` |
| Max resolution / FPS | `1280×720`, `30` FPS (set in `Program.cs`) | `Program.cs:27,29` |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/stream` | HTTP/HTTPS | none | `ImageStreamingServer.HandleStreamRequest` |

## Integration Points

- Invoked by [web-server](../../server/web-server/web-server.md) routing for `/stream`.
- Consumed by [web-ui](../../client/web-ui/web-ui.md) via a plain `<img>` tag.

## Database Schema

None.

## SQL Artifacts

None.
