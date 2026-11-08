# Input Control — Behavior Baseline

## Invariants

- Default input mode is `touch` unless the env or `POST /config` sets `mouse`.
- Missing `DisplaySize` does not scale `X`/`Y`. `Delta` is never scaled.
- Wheel injection uses the negated `Delta`.
- Touch hover (move or up with no contact) is ignored. One contact. Repeat every `100` ms while down. Socket close calls `end_contact`.
- `rightclick` is a mouse right button.
- `map_key("ArrowLeft")` is `{LEFT}`. `map_key("Shift")` is dropped. `map_key("+")` is `{+}`.
- `expand_paste` turns `\r\n` into one `{ENTER}` and `\t` into `{TAB}`.
- If input privilege is missing, injection is off and HTTP still starts.
- Linux `{ENTER}` is an `EV_KEY` tap of `KEY_ENTER` (down and up). A repeat while the contact is down is another slot-0 move.
- Linux absolute position maxima equal the primary width and height. A pointer at that size is the axis maximum.
- Linux `REL_WHEEL` is whole notches of 120 Windows units. A remainder smaller than one notch is kept for the next event.

## Access

No authentication. The input socket is every WebSocket that is not `/ws/audio`.
