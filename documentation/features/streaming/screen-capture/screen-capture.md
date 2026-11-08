# Screen Capture & MJPEG Streaming

`capture_loop::loop_forever` captures one frame at a time and `App::publish_jpeg` stores the latest JPEG. Every `/stream` client reads that slot. Slow clients wait up to `SLOW_CLIENT_WAIT_MS` (`1000`) and then send the newest frame. They do not queue a backlog.

## Frame

- Content type `multipart/x-mixed-replace; boundary=boundary` from `mjpeg_content_type`.
- Each part is `mjpeg_part`: `\r\n--boundary`, `Content-Type: image/jpeg`, `Content-Length`, the JPEG, `\r\n`.
- JPEG quality is `JPEG_QUALITY` (`60`), encoded in `encode_bgra_jpeg`.
- Pace is `FRAME_INTERVAL_MS` (`33`), from 30 FPS.
- Height cap is `TESLAPC_STREAM_HEIGHT`, default `1080`, clamped to `240`–`2160` by `clamp_stream_height`. Width cap is `height * 4`.
- `fit_frame` scales uniformly and never upscales.
- Changing the cap bumps `capture_epoch`. The capture thread starts a new session. Connected clients stay on `/stream`.

## Windows order

`first_capture_backend` starts at WGC unless `TESLAPC_NO_WGC=1`, then DXGI, then GDI. `backend_after_frame` switches to the next backend when the grab fails or `frame_is_black` is true. After GDI, a black frame is kept so the chain is not retried forever inside one frame. `grab_gdi` uses `BitBlt` and `GetDIBits`.

`win_capture::grab_wgc` calls `RoInitialize`, creates a `GraphicsCaptureItem` for the primary monitor, turns cursor capture off (`SetIsCursorCaptureEnabled(false)`) and the yellow border off (`SetIsBorderRequired(false)`) when the OS allows it, and copies a BGRA frame. `win_capture::grab_dxgi` calls `DuplicateOutput` and `AcquireNextFrame`, then copies the texture. A failure is the OS error text. `TESLAPC_NO_WGC=1` returns before WGC is opened.

## Linux

On Wayland, `grab_portal` opens an xdg-desktop-portal ScreenCast session (`CreateSession`, `SelectSources`, `Start`) and reads a PipeWire frame for the returned node with `gst-launch-1.0 pipewiresrc`. The PNG is decoded to BGRA. If `gst-launch-1.0` cannot start, the error includes a read of the `pipewire-0` socket. On X11, `grab_x11` asks XComposite to name the root pixmap when the server allows it, and copies pixels with MIT-SHM `shm_get_image` (ZPixmap into the shared-memory segment). A missing MIT-SHM extension is the returned error. A logged-in desktop session is still required.

## Media

While `media_mode` is set, the capture thread does not publish. `playback` publishes ffmpeg JPEGs into the same slot.
