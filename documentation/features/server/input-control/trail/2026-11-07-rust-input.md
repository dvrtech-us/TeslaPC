# Input mapping moved into tested Rust functions

- Date: 2026-11-07
- Feature: input-control
- Related code: `teslapc/src/decisions/input.rs`, `teslapc/src/win.rs`, `teslapc/src/linux.rs`

## Context

The C# server scaled pointers in `InputData.GetAdjusted` and typed keys with `SendKeys`.

## Decisions

`scale_pointer`, `wheel_delta`, `map_key`, and `expand_paste` are the functions both the socket handler and the tests call. Windows uses `InjectTouchInput` for touch and `SendInput` for the mouse, wheel, and keys. Linux uses uinput. A failed probe disables injection and leaves HTTP up.

## Alternatives Considered

- Shell out to a helper for keys: rejected. The mapping stays in-process.

## Consequences

- Positive: the mapping is tested without admin or a desktop.
- Negative: Linux uinput needs `/dev/uinput`.
