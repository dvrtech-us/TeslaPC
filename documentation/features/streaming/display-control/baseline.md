# Display Control — Behavior Baseline

- Fit uses the stream area in device pixels. Modes shorter than `480` are ignored.
- Modes that cover the area in both directions are preferred; closest aspect wins, a near-tie (within `0.01`) goes to the smallest area. With no covering mode, closest aspect wins and a near-tie goes to the largest.
- The stream height is the fitted picture height, at most the mode height, at least `240`, passed through `clamp_stream_height`.
- The stream height is saved and applied only when the chosen mode is the active one; the response carries `streamHeight`.
- `changed` is true only after the OS accepts the mode.
- A refused change returns the current size and `changed: false`.
- The 480/720/1080 control (Settings page only) writes `TESLAPC_STREAM_HEIGHT` only.
- The Fit screen UI never claims the display "already fits"; `changed: false` is reported as unchanged.
- Linux match uses the mode list from `wlr-randr` on Wayland and from `xrandr` otherwise. The list is more than the current size when the tool prints more than one mode.
