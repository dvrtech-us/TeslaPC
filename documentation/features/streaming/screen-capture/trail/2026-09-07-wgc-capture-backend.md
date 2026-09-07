# Windows Graphics Capture as the primary capture backend

- Date: 2026-09-07
- Feature: screen-capture
- Related code: `TeslaPCInterface/WgcScreenCapture.cs`, `TeslaPCInterface/ImageStreamingServer.cs` (`RunCaptureSession`), `TeslaPCInterface/TeslaPCInterface.csproj` (TFM)

## Context

Some video (local players, hardware-accelerated browser video) rendered as a black rectangle in
the stream. Cause: DXGI Desktop Duplication on some drivers does not composite multiplane-overlay
(MPO) planes, which is where hardware-accelerated video lives. Windows.Graphics.Capture (WGC)
captures the fully composited DWM output, so overlay content renders correctly — and the cursor
is included, which duplication never provided. (DRM-protected content — Netflix and similar —
stays black under every capture API by OS design; WGC does not and cannot change that.)

## Decisions

- New `WgcScreenCapture` mirrors `DxgiScreenCapture`'s public API (`IsAvailable`, `CaptureSize`,
  `TryCapture(Bitmap)`, `Dispose`) so `RunCaptureSession` treats backends uniformly.
- Backend chain is **WGC → DXGI → GDI**, probed once per capture session. WGC init failure is
  non-fatal (logged, falls through), so pre-1903 Windows keeps working unchanged.
- Interop: D3D11 device via Vortice (shared with the DXGI class), wrapped for WinRT with
  `CreateDirect3D11DeviceFromDXGIDevice`; monitor item via `IGraphicsCaptureItemInterop.CreateForMonitor`
  (`MonitorFromPoint(0,0)` = primary); frame texture unwrapped via `IDirect3DDxgiInterfaceAccess`.
  Frame pool is free-threaded, 2 buffers, `B8G8R8A8UIntNormalized`; `TryCapture` drains to the
  newest frame so the 30 FPS consumer never encodes a stale one.
- csproj TFM changed `net10.0-windows` → `net10.0-windows10.0.22000.0` with
  `SupportedOSPlatformVersion 10.0.17763.0` to get CsWinRT projections while still running on
  older Win10 (WGC availability is a runtime probe).
- Capture border (`IsBorderRequired = false`) is best-effort behind `ApiInformation` + try/catch —
  unpackaged apps may be denied, which only leaves the border visible.
- Kill switch: `TESLAPC_NO_WGC=1` skips WGC (troubleshooting a brand-new capture path on a
  machine that is often driven remotely).
- Mid-session death (monitor item `Closed`) flips `IsAvailable`; `RunCaptureSession` notices and
  restarts, re-probing the chain.

## Alternatives Considered

- Disable MPO system-wide (registry `OverlayTestMode=5`): fixes DXGI but degrades local video
  playback for the whole machine and is a hidden global side effect. Rejected.
- Ask users to disable hardware acceleration per app: works but is per-app manual toil. Rejected
  as the primary fix (still a valid workaround on pre-1903 systems stuck on DXGI).

## Consequences

- Positive: hardware-overlay video now streams; cursor is visible in the stream; DXGI/GDI remain
  as automatic fallbacks.
- Negative: TFM bump pulls in the Windows SDK projections (larger build output); a capture border
  may appear on systems where hiding it is denied; WGC requires Win10 1903+ (older systems fall
  back to today's behavior).

## Verification

- `dotnet build` succeeds (no new warnings).
- Live run on Windows 11: `[Capture] Windows Graphics Capture ready (3840x2160)`; 5 s curl of
  `/stream` delivered ~109 JPEG frames (~10.8 MB).
- Fallback and Tesla-browser behavior to be confirmed on the deployment machines.
