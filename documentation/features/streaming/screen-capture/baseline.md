# Screen Capture — Behavior Baseline

Known-good invariants for screen capture and MJPEG streaming. Update only when intended behavior changes.

## Pipeline Invariants

- **At most one capture thread** (`"ScreenCapture"`) runs at any time, regardless of client count. `EnsureCaptureRunning` is guarded by `lock (_frameLock)` and checks `IsAlive` before creating a new thread.
- The capture loop runs **only while `_clientCount > 0`**; it exits and the thread terminates when the count reaches 0.
- All clients share exactly one JPEG buffer (`_currentFrame`). The capture thread is the only writer; client threads read under `lock (_frameLock)`.
- `_frameNumber` is monotonically increasing and never reset; clients use it to detect new frames.
- A slow client **skips** intermediate frames and always sends the latest available frame.
- MJPEG response headers are written **synchronously on the HTTP handler thread** before the per-client send thread is queued (prevents http.sys 503).
- `MjpegWriter` only frames pre-encoded JPEG bytes; all encoding happens in `ImageStreamingServer`.
- `DxgiScreenCapture` is scoped to one `RunCaptureSession` (created with `using`, disposed at session end).

## Capture-Source Rules

- DXGI is preferred. GDI `CopyFromScreen` is used only when `DxgiScreenCapture.IsAvailable` is false at session start.
- DXGI `AcquireNextFrame` uses a 16 ms timeout; a timeout publishes no frame for that tick.
- DXGI `AccessLost`/`AccessDenied` triggers `RecreateDuplication()`; the current frame is skipped.
- The staging texture is lazily created and reused; reallocated only when texture dimensions change.

## Encoding / Pacing Defaults

- JPEG quality is fixed at `60`.
- Frame pacing is best-effort via `Environment.TickCount`; overruns are not compensated across frames.
- Scaling uses fixed `Bilinear` / `HighSpeed` / no-smoothing settings.
- Output resolution is clamped to the configured max (`1280×720`).

## Access Control

- `/stream` requires no authentication.

## Failure Behavior

- DXGI init failure → `IsAvailable = false`, GDI fallback for the whole session.
- Capture/encode error on a frame → logged, frame skipped, loop continues.
- Client disconnect → write throws, caught in `StreamToClient`; `_clientCount` decremented and the response stream closed in `finally`.
- No new frame within 1000 ms → client re-checks capture-thread liveness; exits if the thread is dead.
