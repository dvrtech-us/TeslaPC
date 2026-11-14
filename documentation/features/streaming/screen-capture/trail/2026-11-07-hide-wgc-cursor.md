# Hide the host cursor in Windows Graphics Capture frames

- Date: 2026-11-07
- Feature: screen-capture
- Related code: `TeslaPCInterface/WgcScreenCapture.cs` (`TryDisableCursorCapture`)

## Context

Switching the primary backend to Windows Graphics Capture (2026-09-07) made the host mouse
pointer appear in the MJPEG stream. WGC includes the cursor by default; DXGI and GDI never
did. The remote client already moves that pointer, so the burned-in cursor is noise on the
Tesla screen.

## Decisions

- After `CreateCaptureSession` and before `StartCapture`, `TryDisableCursorCapture` sets
  `GraphicsCaptureSession.IsCursorCaptureEnabled = false` when
  `ApiInformation.IsPropertyPresent` reports the property (Windows 10 2004 / build 19041+).
- Failure is logged and non-fatal, same pattern as `TryDisableCaptureBorder`. On older builds
  the property is absent and the WGC default (cursor on) stays.
- No client-side cursor is drawn. The stream simply stops compositing the host pointer.

## Alternatives Considered

- Leave the cursor visible so a local user at the PC can be seen by the remote client. Rejected:
  the remote session is the pointer, and the old DXGI path never showed it.
- Draw a client-side cursor in `index.html`. Out of scope; the complaint was the extra cursor
  in the capture.

## Consequences

- Positive: stream matches the pre-WGC look for the pointer. MPO video capture is unchanged.
- Negative: a person sitting at the host PC is no longer visible as a moving pointer in the stream.
