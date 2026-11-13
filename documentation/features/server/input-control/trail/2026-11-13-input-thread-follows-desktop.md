# Session-host input runs on one thread that follows the input desktop

- Date: 2026-11-13
- Feature: input-control
- Plan: `documentation/planning/Infrastructure/2026-11-13-windows-service-session-host.md` (step 4)
- Related code: `teslapc/src/win.rs`, `teslapc/src/platform.rs`, `teslapc/src/serve.rs`, `teslapc/src/decisions/input.rs`, `teslapc/src/win_desktop.rs`

## Context

Input ran on whatever tokio worker handled the WebSocket message. `SetThreadDesktop` is per
thread, so re-parenting runtime workers to `Winlogon` was not acceptable, and injection from a
thread on `Default` does not reach the lock screen, sign-in screen, or UAC prompt.

## Decisions

- **One `Input` thread, routed inside `win.rs`.** The `platform::` calls and `routes.rs` are
  unchanged; `win::dispatch` queues when the thread exists and otherwise runs the same code
  directly, so a normal run keeps today's behavior. `InitializeTouchInjection` (the probe) also
  runs there and answers through a reply channel.
- **Mouse path off `Default`.** Touch injection on the secure desktop is not relied on (plan
  risk 3). `pointer_path_on_desktop` returns `Mouse` off `Default`. Because the route already
  turns touch-mode taps into `down`/`up`, those become a left click at the right spot without
  route changes; the touch repeat is a no-op there (`touch_repeat_applies`).
- **Switch hygiene.** End the touch contact before leaving a desktop, clear the route's
  `touch_down` after, and release a left button held through the mouse path. The input thread
  also tracks that button so `end_contact` (socket close) releases it. This is new for session
  host only; a normal mouse-mode run still does not release a held button on disconnect.
- **Touch probe retry.** At boot the host may start on the sign-in screen, where
  `InitializeTouchInjection` might fail; it is retried on the first switch to `Default`.
- **Checked results.** `SendInput` counts, `SetCursorPos`, and `InjectTouchInput` are checked;
  failures go to `App::log("debug", ...)` and force an immediate desktop check. The direct
  path computes the same results and ignores them.
- **No guard across injection.** Touch state stays in the `TOUCH_XY`/`TOUCH_READY` statics;
  every read copies the value out before injecting, as in the deadlock fix earlier today.

## Consequences

Not run on Windows yet. Ctrl+Alt+Del is still not sent, so a policy that requires it on the
sign-in screen cannot be satisfied from the browser.
