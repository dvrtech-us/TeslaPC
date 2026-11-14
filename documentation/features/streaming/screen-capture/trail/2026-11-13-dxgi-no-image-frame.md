# DXGI frame without a present time is not a picture

- Date: 2026-11-13
- Feature: screen-capture

## Problem

On the lab laptop (logon task and SYSTEM session host) the first `AcquireNextFrame` after `DuplicateOutput` returned a frame whose texture was black. `frame_is_black` then logged `[Capture] Dxgi frame is black; trying Gdi.` and the normal desktop stayed on GDI, the slowest backend.

## Decision

- `grab_dxgi` checks `DXGI_OUTDUPL_FRAME_INFO::LastPresentTime` via the pure `decisions::capture::dxgi_frame_has_image`. Zero means the desktop image was not updated (pointer-only update or that first acquire): the frame is released and the grab returns `NoFrame`.
- Verified on the lab laptop: after a service restart the black-frame line is gone and DXGI delivered about 175 frames in 10 s.
- Unchanged: a real black picture (non-zero present time) still hands off to GDI. DXGI on the UAC desktop was refused with "Access is denied" in testing and fell back to GDI.
- Test: `dxgi_frame_without_a_present_time_is_not_a_picture`.

## Files

- `teslapc/src/win_capture.rs`, `teslapc/src/decisions/capture.rs`, `teslapc/tests/protocol.rs`
