# Touch through a synthetic pointer device

**Date:** 2026-11-13

## Problem

In touch mode a tap did nothing on the lab laptop (Windows 11 build 26100). With
`TESLAPC_LOG_LEVEL=debug` every frame logged
`InjectTouchInput(down at x, y): The parameter is incorrect. (0x80070057)`. Two-finger scroll
still worked because it is sent as wheel events, not touch.

A standalone test outside TeslaPC (PowerShell P/Invoke, run as the signed-in user) got error
87 from `InjectTouchInput` for every variant tried: with and without a contact rectangle,
with and without `touchMask`, pointer id 0 and 1, up as `UP` and as `INRANGE|UP`. The same
down / update / up frames through `CreateSyntheticPointerDevice` and
`InjectSyntheticPointerInput` were accepted.

## Decision

`win::probe_touch_now` creates a synthetic touch device first and keeps it for the life of
the process (`TOUCH_DEVICE`). `inject_touch` sends through it when it exists. The legacy
`InitializeTouchInjection` / `InjectTouchInput` path remains as the fallback for systems
without the synthetic API (before Windows 10 1809). The up frame is now `POINTER_FLAG_UP`
alone and `touchMask` is set, matching the frames that were verified.

Verified on the lab laptop as the SYSTEM session host: a touch-mode tap on the Start button
opened the Start menu and the debug log showed no injection error.

Not verified: why `InjectTouchInput` rejects frames on this build, drag and long-press in
touch mode, and touch on other Windows versions.

## Files

- `teslapc/src/win.rs` (`probe_touch_now`, `inject_touch`, `TOUCH_DEVICE`)
- `teslapc/Cargo.toml` (`Win32_UI_Controls`)
