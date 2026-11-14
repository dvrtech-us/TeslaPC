# Linux axis range matches the primary screen, and a wheel notch is one detent

- Date: 2026-11-07
- Feature: input-control
- Related code: `teslapc/src/decisions/linux_input.rs`, `teslapc/src/linux.rs`

## Context

uinput position axes were declared as `0..=65535` while events carried primary-screen pixels. A click at `(1920, 1080)` was about `1920/65535` of the desktop. `REL_WHEEL` also stored the inverted Windows delta, so one notch of 120 became 120 detents.

## Decisions

- `linux_abs_axes` sets `ABS_X`, `ABS_Y`, `ABS_MT_POSITION_X`, and `ABS_MT_POSITION_Y` maxima to the primary size.
- `linux_pointer_events` clamps each coordinate to that maximum. `linux.rs` creates the device with `linux_abs_axes(primary_size())`.
- `linux_wheel_events` divides by `LINUX_WHEEL_NOTCH` (120) and returns the remainder. `inject_wheel` keeps that remainder.

## Alternatives Considered

- Scaling pixels into a fixed `0..=65535` range: rejected. The axis maximum is the same size `scale_pointer` already produces.

## Consequences

- Positive: a coordinate equal to the primary size is the far edge of the declared axis, and one Windows notch is `REL_WHEEL` ±1.
- Negative: a resolution change after `/dev/uinput` is created leaves the axis maximum at the size from setup.
