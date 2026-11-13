# Display Control — Behavior Baseline

- Match cap is `max(480, stream height)`.
- Modes shorter than `480` or taller than the cap are ignored.
- Equal aspect prefers the larger area. A closer aspect wins when it beats the current error by more than `0.01`.
- `changed` is true only after the OS accepts the mode.
- A refused change returns the current size and `changed: false`.
- The 480/720/1080 control (Settings page only) writes `TESLAPC_STREAM_HEIGHT` only.
- The Fit screen UI never claims the display "already fits"; `changed: false` is reported as unchanged.
- Linux match uses the mode list from `wlr-randr` on Wayland and from `xrandr` otherwise. The list is more than the current size when the tool prints more than one mode.
