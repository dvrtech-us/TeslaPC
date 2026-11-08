# Display Control

`GET /display/info`, `/display/match?w=&h=`, and `/display/set?w=&h=` return `{ changed, width, height, modes: [{ w, h }] }`.

`best_for_aspect` picks the mode whose aspect is closest to the viewport, heights in `[480, max(480, stream height)]`. A near-tie within `0.01` prefers the larger area. The desktop changes only when the chosen mode differs. `try_set_mode` uses `ChangeDisplaySettingsW`. A refused change returns the current mode and `changed: false`.

The resolution picker (`480`, `720`, `1080`) posts `streamHeight` only. It does not change the desktop.

Linux `display_snapshot` runs `xrandr --query` and `wlr-randr`, then `choose_display_query`. Wayland prefers the wlr-randr mode list. `try_set_mode` uses `display_set_args`: `wlr-randr --output <name> --mode WxH` when the session is Wayland and wlr-randr returned modes, otherwise `xrandr --output <name> --mode WxH`. A failure returns `changed: false`.
