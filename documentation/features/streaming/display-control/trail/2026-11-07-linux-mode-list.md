# Linux mode list comes from wlr-randr or xrandr

- Date: 2026-11-07
- Feature: display-control
- Related code: `teslapc/src/decisions/display.rs`, `teslapc/src/linux.rs`

## Context

`display_snapshot` returned only the current size, so `/display/match` could not see other modes. `try_set_mode` always called `xrandr`.

## Decisions

- `parse_xrandr` and `parse_wlr_randr` collect every `WxH` under the connected output.
- Wayland prefers wlr-randr. `display_set_args` selects `wlr-randr` or `xrandr`.
- `best_for_aspect` still chooses among that list.

## Alternatives Considered

- Inventing a mode list from the current size only: rejected. The match rule needs the modes the tools print.

## Consequences

- Positive: a sample `xrandr` listing with `1024x768` is what `best_for_aspect` selects for that aspect.
- Negative: a host without either tool still falls back to the current size.
