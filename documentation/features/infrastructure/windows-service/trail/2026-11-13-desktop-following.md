# Session host follows the input desktop

- Date: 2026-11-13
- Feature: windows-service
- Plan: `documentation/planning/Infrastructure/2026-11-13-windows-service-session-host.md` (step 4, "Revisions after review")
- Related code: `teslapc/src/win_desktop.rs`, `teslapc/src/decisions/desktop.rs`, `teslapc/src/capture_loop.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/win.rs`, `teslapc/src/platform.rs`, `teslapc/src/serve.rs`

## Context

After step 2 the host streamed and drove the user's desktop but not the lock screen, the
sign-in screen, or UAC prompts. Those are on the `Winlogon` desktop; the host's threads stay on
`Default`, the desktop they were created on.

## Decisions

- **Per-thread attachment, per-thread handles.** `DesktopAttachment` is owned by the thread it
  moves. Handles are never shared between threads, because `CloseDesktop` fails while any
  thread of the process is attached through that handle.
- **`MAXIMUM_ALLOWED`** as the reviewers recommended. The `windows` 0.61.3 binding takes a
  `DESKTOP_ACCESS_FLAGS`, so the value is `DESKTOP_ACCESS_FLAGS(0x0200_0000)`; no extra crate
  feature is needed for the constant. `Win32_System_StationsAndDesktops` was enabled.
- **Rate limit with a forced path.** 200 ms between checks, immediately after a failed
  grab/injection, and 2 s after a failed `SetThreadDesktop`, so a thread that cannot move does
  not retry on every frame. The backoff after a failed switch wins over a forced check;
  otherwise a persistently failing grab would retry every frame.
- **Teardown first.** The capture thread drops its WGC/DXGI sessions in the `before_switch`
  callback, before `SetThreadDesktop`; the input thread ends its touch contact there.
- **`ERROR_BUSY` restarts the capture thread.** The busy thread spawns its replacement as its
  last act and returns, so one capture loop runs at a time and `ensure_started`'s `Once` is not
  involved. A thread cannot close a desktop handle it is still attached to, so the busy thread
  hands its attached handle (as a raw value) to the replacement, which closes it on its first
  check after the old thread exits. Consecutive busy restarts wait 250 ms doubling to 5 s
  (`capture_restart_delay_ms`), so a thread that always comes up busy does not spin.
- **Only in session-host mode.** A normal run never creates a follower and never starts the
  `Input` thread, so it behaves as before.

## Not done

Nothing here has run on Windows. The code compiles for `x86_64-pc-windows-msvc` with no
warnings, and the pure rules are covered by `cargo test`. Ctrl+Alt+Del (`SendSAS`) is still out
of scope.
