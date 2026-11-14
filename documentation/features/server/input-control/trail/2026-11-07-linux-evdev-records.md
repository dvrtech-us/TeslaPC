# Linux uinput writes the tested evdev records

- Date: 2026-11-07
- Feature: input-control
- Related code: `teslapc/src/decisions/linux_input.rs`, `teslapc/src/linux.rs`

## Context

The Linux injectors opened `/dev/uinput` and then ignored the coordinates, the wheel delta, and named keys such as `{ENTER}`.

## Decisions

- `linux_pointer_events`, `linux_repeat_events`, `linux_wheel_events`, and `linux_key_events` build the `EV_ABS`, `EV_KEY`, and `EV_REL` records.
- `linux.rs` writes those records after `UI_DEV_SETUP` / `UI_DEV_CREATE`.
- `{ENTER}` is `KEY_ENTER`. Characters without a key code use Ctrl+Shift+U.

## Alternatives Considered

- Typing only a single Unicode scalar: rejected because named tokens never became keys.

## Consequences

- Positive: protocol tests assert slot 0, the mouse button, `REL_WHEEL` value `-1`, and `{ENTER}`.
- Negative: the write path still needs `/dev/uinput`, which this Windows host does not have.
