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

`first_capture_backend` starts at WGC unless `TESLAPC_NO_WGC=1` or the process is a Windows service session host (`--session-host`, which calls `capture_loop::session_host_mode()` and logs `[Capture] Session host: WGC skipped, starting at DXGI.`), then DXGI, then GDI. `backend_after_frame` switches to the next backend when the grab fails or `frame_is_black` is true. After GDI, a black frame is kept so the chain is not retried forever inside one frame. `grab_gdi` uses `BitBlt` and `GetDIBits` on a fresh `GetDC(None)` every grab; nothing desktop-bound is cached, so it captures whichever desktop the thread is attached to.

Every backend's grab returns `Result<GrabOutcome, String>` (`platform.rs`). `GrabOutcome::Frame` is a new frame. `GrabOutcome::NoFrame` means the session is healthy and nothing changed. `Err` is a failed backend. WGC and DXGI only deliver a frame when the screen changes, so a still screen is `NoFrame`, not a failure. Only `Err`, a black frame, or a backend that never produced a frame (below) advances the backend order.

`win_capture::grab_wgc` calls `RoInitialize`, creates a `GraphicsCaptureItem` for the primary monitor, turns cursor capture off (`SetIsCursorCaptureEnabled(false)`) and the yellow border off (`SetIsBorderRequired(false)`) when the OS allows it, and copies a BGRA frame. `TryGetNextFrame` is called once per grab. The `windows` crate maps a null frame to `Error::empty()`, whose code is `S_OK`; `grab_wgc` treats an error with a success code as `NoFrame` and keeps the session. Any other error, or a surface or texture failure, drops the session and returns `Err`. `win_capture::grab_dxgi` calls `DuplicateOutput`, then one `AcquireNextFrame` with `DXGI_ACQUIRE_TIMEOUT_MS` (`100`) per grab, then checks `DXGI_OUTDUPL_FRAME_INFO::LastPresentTime` through `decisions::capture::dxgi_frame_has_image`. A zero present time means the desktop image was not updated (pointer-only update, or the first acquire right after `DuplicateOutput`, whose texture is black), so the frame is released and the grab returns `NoFrame` without copying. Otherwise it copies the texture. `ReleaseFrame` always follows a successful acquire. `DXGI_ERROR_WAIT_TIMEOUT` keeps the session and returns `NoFrame`. `DXGI_ERROR_ACCESS_LOST`, `DXGI_ERROR_INVALID_CALL`, and `DXGI_ERROR_SESSION_DISCONNECTED` drop the duplication and reopen it once in the same call; a failed reopen, or a second loss, is `Err`. Other errors drop the session and return `Err`. A failure is the OS error text. `TESLAPC_NO_WGC=1` returns before WGC is opened.

## Still screen

`decisions::capture::no_frame_action` decides what `capture_loop` does on `NoFrame`:

- The backend has produced a real frame since it was selected: stay on it. If a last JPEG is stored and `REPUBLISH_INTERVAL_MS` (`1000`) has passed since the last publish, publish the stored JPEG again through `App::publish_jpeg` (new `seq`, no re-encode). A connected client therefore gets a part at least once a second on a still screen. A client that connects later already receives the stored frame from the watch channel.
- The backend has not produced a frame since it was (re)selected: wait, and after `NO_FRAME_FAIL_MS` (`2000`) treat it as `Err` (next backend).
- The selection timer and "produced" flag reset on a backend switch, a `capture_epoch` change, and while the loop is idle (no `/stream` client or `media_mode`). A `capture_epoch` change also clears the stored JPEG.

The per-frame line `[Capture] <backend> frame WxH` is written with `App::log("debug", ...)`, so it only prints when `TESLAPC_LOG_LEVEL=debug`.

## Following the input desktop (`--session-host` only)

The capture thread is created by `capture_loop::spawn_capture` (`ScreenCapture`). In session-host
mode `loop_forever` creates a `platform::DesktopFollower("ScreenCapture")` (see
`documentation/features/infrastructure/windows-service/` for the attachment rules). A normal
run creates none and behaves as before.

- On each streaming tick (not while idle), before the grab, the follower checks the input
  desktop: the first tick at once, then at most every 200 ms, and at once on the tick after a
  grab `Err` or a `NoFrameAction::Fail`.
- On a switch, `platform::drop_capture_sessions()` (`win_capture::drop_sessions`, which clears
  the thread-local WGC and DXGI sessions) runs before `SetThreadDesktop`. After a successful
  switch the loop logs `[Capture] Desktop is now <name>; restarting capture at Dxgi.`, resets
  the backend to `first_capture_backend`, resets the selection timer and "produced" flag, and
  clears the stored JPEG (the other desktop's picture is not re-published), as a
  `capture_epoch` change does.
- DXGI after a switch: while DXGI has produced no frame since the switch,
  `decisions::capture::retry_after_switch` keeps DXGI on an `Err` up to `DXGI_SWITCH_RETRIES`
  (`5`) times, `DXGI_SWITCH_RETRY_MS` (`200`) apart, logging `[Capture] Dxgi failed right after
  a desktop switch (<error>); retry <n> of 5.` Then the normal step down to GDI applies. The
  first frame ends the retry window.
- `SetThreadDesktop` returning `ERROR_BUSY` (the thread owns windows or hooks): the loop logs
  `[Capture] This thread cannot change desktop (busy); restarting the capture thread.`, hands
  its attached desktop handle to a new `ScreenCapture` thread, and returns. The new thread waits
  `capture_restart_delay_ms(n)` (250 ms doubling to 5 s for consecutive busy restarts; reset by
  a successful switch), closes the handed-over handle, and starts at DXGI. Exactly one capture
  loop runs at a time. Any other `SetThreadDesktop` failure keeps the thread where it is.
- The black-frame handoff is unchanged on every desktop.

Not yet run on Windows: whether DXGI duplication opens on `Winlogon` as SYSTEM, and whether the
capture thread ever comes up `Busy`, are the first things to confirm (log lines in the windows
service feature doc and the step 4 report).

## Linux

On Wayland, `grab_portal` opens an xdg-desktop-portal ScreenCast session (`CreateSession`, `SelectSources`, `Start`) and reads a PipeWire frame for the returned node with `gst-launch-1.0 pipewiresrc`. The PNG is decoded to BGRA. If `gst-launch-1.0` cannot start, the error includes a read of the `pipewire-0` socket. On X11, `grab_x11` asks XComposite to name the root pixmap when the server allows it, and copies pixels with MIT-SHM `shm_get_image` (ZPixmap into the shared-memory segment). A missing MIT-SHM extension is the returned error. A logged-in desktop session is still required. Linux backends always return `GrabOutcome::Frame`.

## Media

While `media_mode` is set, the capture thread does not publish. `playback` publishes ffmpeg JPEGs into the same slot.
