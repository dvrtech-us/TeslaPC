# Defer RichTextBox updates to when the Log tab is visible

- Date: 2026-06-27
- Feature: app/control-panel
- Related code: `TeslaPCInterface/MainForm.cs`

## Context

`MainForm.AppendLog` previously appended every log line to the `RichTextBox` unconditionally,
even when the Log tab was not visible. Each append required a cross-thread marshal
(`Invoke`/`BeginInvoke`), a trim check, a `ScrollToCaret`, and a `Refresh` — non-trivial work
for every line. With the mouse-move and per-request debug lines now controlled by `Log.Level`,
the burst rate is lower under normal operation, but the pattern was still wasteful: if the user
never opens the Log tab, every line still paid the full `RichTextBox` update cost.

## Decisions

### Split AppendLog into a buffer path and a box path

`AppendLog` now always appends the incoming line to an in-memory `StringBuilder` (`_logBuffer`)
and checks `_logPanel.Visible` before calling `AppendToBox`. The buffer path is cheap — a
string append and a length check. The box path (`AppendToBox`) — marshal, trim, append,
`ScrollToCaret`) — runs only while the Log tab is the active panel.

### Buffer bounds: 120 000 chars cap, trimmed to 90 000

The buffer needs a cap to prevent unbounded growth during long sessions where the Log tab is
never opened. 120 000 chars is large enough to hold a meaningful history (several hundred
typical log lines) without consuming unreasonable memory. When the cap is exceeded, the buffer
is trimmed by removing a leading substring so the total drops to 90 000 chars, preserving the
most recent content. The trim is applied before the optional `AppendToBox` call, so both the
buffer and the `RichTextBox` are bounded by the same limit.

### ShowLogBuffer rebuilds the RichTextBox on tab open

When the user switches to the Log tab (`ShowTab` → `ShowLogBuffer`), the `RichTextBox` is
replaced with the current buffer contents in a single `Text =` assignment — one marshal, one
paint — rather than replaying every line. This is faster than incremental replay and avoids
`ScrollToCaret` thrash during the rebuild. After `ShowLogBuffer`, `AppendToBox` takes over for
live lines while the tab is visible.

### No change to the ControlWriter tee

`Console.Out` is still teed to the original stream. The buffer/box split happens entirely inside
`AppendLog`, which is the `ControlWriter` callback. Redirected log files are unaffected.

## Consequences

- **Positive:** No `RichTextBox` marshal or paint work while the Log tab is hidden. In
  server-heavy workloads (many HTTP requests, active mouse input at `Debug` level) this
  eliminates a steady stream of cross-thread invokes.
- **Positive:** Switching to the Log tab still shows recent history (up to 120 000 chars /
  ~90 000 after trim) rather than a blank or stale box.
- **Note:** The buffer contains text only — no colour markup. If log-level coloring is added
  to `AppendToBox` in the future, `ShowLogBuffer` would need to replay lines individually
  rather than using a bulk `Text =` assignment.
- **Note:** The 120 000/90 000 char thresholds match the existing `RichTextBox` trim thresholds
  that were already in the previous implementation; the values were not changed, only the
  condition under which the trim runs.
