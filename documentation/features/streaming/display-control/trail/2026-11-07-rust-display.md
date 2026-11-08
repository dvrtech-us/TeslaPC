# Display choice is a pure function

- Date: 2026-11-07
- Feature: display-control
- Related code: `teslapc/src/decisions/display.rs`, `teslapc/src/win.rs`

## Context

`DisplayManager.BestForAspect` picked the desktop mode for "fit my screen".

## Decisions

`best_for_aspect` copies that rule and is what `/display/match` calls before `ChangeDisplaySettingsW`.

## Alternatives Considered

- Always set the stream height instead of the desktop: rejected. Match still changes the desktop when the mode differs.

## Consequences

- Positive: mode choice is tested without changing the desktop.
- Negative: a driver can still refuse the change.
