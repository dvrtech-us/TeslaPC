# Capture thread follows the input desktop (session host)

- Date: 2026-11-13
- Feature: screen-capture
- Plan: `documentation/planning/Infrastructure/2026-11-13-windows-service-session-host.md` (step 4)
- Related code: `teslapc/src/capture_loop.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/win.rs`, `teslapc/src/decisions/capture.rs`, `teslapc/src/decisions/desktop.rs`, `teslapc/src/win_desktop.rs`

## Context

As SYSTEM in the console session the host could capture `Default` but not the lock screen,
sign-in screen, or UAC prompts on `Winlogon`. DXGI duplication and `GetDC(None)` both work on
the desktop the calling thread is attached to.

## Decisions

- **Check before the grab, only while streaming.** An idle loop does not open desktops. The
  tick after any `Err` or never-produced backend forces a check, since that is what a desktop
  switch looks like to a grab.
- **A switch is an epoch restart.** Sessions are dropped before `SetThreadDesktop` (the
  duplication belongs to the old desktop), selection restarts at DXGI, and the stored JPEG is
  cleared so the lock screen is not shown after unlocking (or the reverse).
- **DXGI retry window.** Duplication often cannot be created for a moment after a switch.
  `retry_after_switch` keeps DXGI for up to 5 errors, 200 ms apart, until the first frame. A
  pure function so the rule is tested.
- **`ERROR_BUSY` → new thread.** Implemented as the old thread spawning its replacement and
  returning, not through `ensure_started` (whose `Once` would block it). The replacement closes
  the old thread's desktop handle. The renamed `capture_loop::session_host_mode()` replaces
  `skip_wgc()` because the flag now also turns on desktop following.
- **GDI needed no change.** It already takes a fresh `GetDC(None)` per grab. The per-grab
  `SetProcessDPIAware()` call was left alone.

## Consequences

Not run on Windows yet. If DXGI cannot open on `Winlogon`, the secure desktop is captured with
GDI after five retries (about one second), and the loop stays on GDI until the next switch or
epoch change.
