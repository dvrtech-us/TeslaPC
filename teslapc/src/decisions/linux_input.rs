//! Linux evdev records. `linux.rs` writes these bytes to `/dev/uinput`.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EvdevEvent {
    pub type_: u16,
    pub code: u16,
    pub value: i32,
}

pub const EV_SYN: u16 = 0;
pub const EV_KEY: u16 = 1;
pub const EV_REL: u16 = 2;
pub const EV_ABS: u16 = 3;
pub const SYN_REPORT: u16 = 0;

pub const ABS_X: u16 = 0x00;
pub const ABS_Y: u16 = 0x01;
pub const REL_WHEEL: u16 = 0x08;
pub const BTN_LEFT: u16 = 0x110;
pub const BTN_RIGHT: u16 = 0x111;
pub const BTN_TOUCH: u16 = 0x14a;
pub const ABS_MT_SLOT: u16 = 0x2f;
pub const ABS_MT_POSITION_X: u16 = 0x35;
pub const ABS_MT_POSITION_Y: u16 = 0x36;
pub const ABS_MT_TRACKING_ID: u16 = 0x39;

pub const KEY_ESC: u16 = 1;
pub const KEY_1: u16 = 2;
pub const KEY_0: u16 = 11;
pub const KEY_BACKSPACE: u16 = 14;
pub const KEY_TAB: u16 = 15;
pub const KEY_U: u16 = 22;
pub const KEY_ENTER: u16 = 28;
pub const KEY_LEFTCTRL: u16 = 29;
pub const KEY_A: u16 = 30;
pub const KEY_LEFTSHIFT: u16 = 42;
pub const KEY_SPACE: u16 = 57;
pub const KEY_F1: u16 = 59;
pub const KEY_F10: u16 = 68;
pub const KEY_F11: u16 = 87;
pub const KEY_F12: u16 = 88;
pub const KEY_HOME: u16 = 102;
pub const KEY_UP: u16 = 103;
pub const KEY_PAGEUP: u16 = 104;
pub const KEY_LEFT: u16 = 105;
pub const KEY_RIGHT: u16 = 106;
pub const KEY_END: u16 = 107;
pub const KEY_DOWN: u16 = 108;
pub const KEY_PAGEDOWN: u16 = 109;
pub const KEY_INSERT: u16 = 110;
pub const KEY_DELETE: u16 = 111;

fn ev(type_: u16, code: u16, value: i32) -> EvdevEvent {
    EvdevEvent { type_, code, value }
}

fn syn() -> EvdevEvent {
    ev(EV_SYN, SYN_REPORT, 0)
}

fn key_tap(code: u16) -> Vec<EvdevEvent> {
    vec![ev(EV_KEY, code, 1), syn(), ev(EV_KEY, code, 0), syn()]
}

/// One Windows wheel notch (`e.deltaY` / `dy * 1.6`) after `wheel_delta` has inverted the sign.
pub const LINUX_WHEEL_NOTCH: i32 = 120;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LinuxAbsAxis {
    pub code: u16,
    pub minimum: i32,
    pub maximum: i32,
}

/// Position axes use the primary size `scale_pointer` writes. A value equal to that size is the far edge.
pub fn linux_abs_axes(screen_w: i32, screen_h: i32) -> [LinuxAbsAxis; 6] {
    let xmax = screen_w.max(1);
    let ymax = screen_h.max(1);
    [
        LinuxAbsAxis { code: ABS_X, minimum: 0, maximum: xmax },
        LinuxAbsAxis { code: ABS_Y, minimum: 0, maximum: ymax },
        LinuxAbsAxis { code: ABS_MT_SLOT, minimum: 0, maximum: 0 },
        LinuxAbsAxis { code: ABS_MT_TRACKING_ID, minimum: -1, maximum: 65535 },
        LinuxAbsAxis { code: ABS_MT_POSITION_X, minimum: 0, maximum: xmax },
        LinuxAbsAxis { code: ABS_MT_POSITION_Y, minimum: 0, maximum: ymax },
    ]
}

pub fn linux_abs_value(pixel: i32, screen: i32) -> i32 {
    pixel.clamp(0, screen.max(1))
}

/// Touch uses `ABS_MT` slot 0. Mouse uses an absolute pointer plus buttons.
/// `screen` is the primary size, and it is also the axis maximum.
pub fn linux_pointer_events(kind: &str, x: i32, y: i32, touch: bool, screen: (i32, i32)) -> Vec<EvdevEvent> {
    let x = linux_abs_value(x, screen.0);
    let y = linux_abs_value(y, screen.1);
    if touch {
        return match kind {
            "down" => vec![
                ev(EV_ABS, ABS_MT_SLOT, 0),
                ev(EV_ABS, ABS_MT_TRACKING_ID, 1),
                ev(EV_ABS, ABS_MT_POSITION_X, x),
                ev(EV_ABS, ABS_MT_POSITION_Y, y),
                ev(EV_KEY, BTN_TOUCH, 1),
                syn(),
            ],
            "up" => vec![
                ev(EV_ABS, ABS_MT_SLOT, 0),
                ev(EV_ABS, ABS_MT_TRACKING_ID, -1),
                ev(EV_KEY, BTN_TOUCH, 0),
                syn(),
            ],
            "move" => vec![
                ev(EV_ABS, ABS_MT_SLOT, 0),
                ev(EV_ABS, ABS_MT_POSITION_X, x),
                ev(EV_ABS, ABS_MT_POSITION_Y, y),
                syn(),
            ],
            _ => Vec::new(),
        };
    }
    let mut out = vec![ev(EV_ABS, ABS_X, x), ev(EV_ABS, ABS_Y, y)];
    match kind {
        "down" => out.push(ev(EV_KEY, BTN_LEFT, 1)),
        "up" => out.push(ev(EV_KEY, BTN_LEFT, 0)),
        "rightclick" => {
            out.push(ev(EV_KEY, BTN_RIGHT, 1));
            out.push(syn());
            out.push(ev(EV_KEY, BTN_RIGHT, 0));
        }
        _ => {}
    }
    out.push(syn());
    out
}

/// Hold-repeat while a touch contact is down: another slot-0 move.
pub fn linux_repeat_events(x: i32, y: i32, screen: (i32, i32)) -> Vec<EvdevEvent> {
    linux_pointer_events("move", x, y, true, screen)
}

/// `delta` is already sign-inverted by `wheel_delta` and is still in Windows wheel units.
/// Returns the detent events and the unused remainder. One notch (`120`) is `REL_WHEEL` ±1.
pub fn linux_wheel_events(delta: i32, pending: i32) -> (Vec<EvdevEvent>, i32) {
    let total = pending.saturating_add(delta);
    let notches = total / LINUX_WHEEL_NOTCH;
    let rest = total % LINUX_WHEEL_NOTCH;
    if notches == 0 {
        return (Vec::new(), rest);
    }
    (vec![ev(EV_REL, REL_WHEEL, notches), syn()], rest)
}

/// Named SendKeys tokens become `EV_KEY`. Other text uses `EV_KEY` plus the Ctrl+Shift+U Unicode path.
pub fn linux_key_events(token: &str) -> Vec<EvdevEvent> {
    if let Some(code) = named_key(token) {
        return key_tap(code);
    }
    let ch = if token.chars().count() == 1 {
        token.chars().next()
    } else {
        token.strip_prefix('{').and_then(|s| s.strip_suffix('}')).filter(|s| s.chars().count() == 1).and_then(|s| s.chars().next())
    };
    ch.map(unicode_events).unwrap_or_default()
}

fn named_key(token: &str) -> Option<u16> {
    Some(match token {
        "{BACKSPACE}" => KEY_BACKSPACE,
        "{ENTER}" => KEY_ENTER,
        "{TAB}" => KEY_TAB,
        "{ESC}" => KEY_ESC,
        "{DELETE}" => KEY_DELETE,
        "{INSERT}" => KEY_INSERT,
        "{HOME}" => KEY_HOME,
        "{END}" => KEY_END,
        "{PGUP}" => KEY_PAGEUP,
        "{PGDN}" => KEY_PAGEDOWN,
        "{LEFT}" => KEY_LEFT,
        "{RIGHT}" => KEY_RIGHT,
        "{UP}" => KEY_UP,
        "{DOWN}" => KEY_DOWN,
        "{F1}" => KEY_F1,
        "{F2}" => KEY_F1 + 1,
        "{F3}" => KEY_F1 + 2,
        "{F4}" => KEY_F1 + 3,
        "{F5}" => KEY_F1 + 4,
        "{F6}" => KEY_F1 + 5,
        "{F7}" => KEY_F1 + 6,
        "{F8}" => KEY_F1 + 7,
        "{F9}" => KEY_F1 + 8,
        "{F10}" => KEY_F10,
        "{F11}" => KEY_F11,
        "{F12}" => KEY_F12,
        _ => return None,
    })
}

fn unicode_events(ch: char) -> Vec<EvdevEvent> {
    if let Some((code, shift)) = ascii_key(ch) {
        let mut out = Vec::new();
        if shift {
            out.push(ev(EV_KEY, KEY_LEFTSHIFT, 1));
        }
        out.extend(key_tap(code));
        if shift {
            out.push(ev(EV_KEY, KEY_LEFTSHIFT, 0));
            out.push(syn());
        }
        return out;
    }
    let hex = format!("{:X}", ch as u32);
    let mut out = vec![
        ev(EV_KEY, KEY_LEFTCTRL, 1),
        ev(EV_KEY, KEY_LEFTSHIFT, 1),
        syn(),
    ];
    out.extend(key_tap(KEY_U));
    for digit in hex.chars() {
        if let Some((code, _)) = ascii_key(digit) {
            out.extend(key_tap(code));
        }
    }
    out.extend(key_tap(KEY_ENTER));
    out.push(ev(EV_KEY, KEY_LEFTSHIFT, 0));
    out.push(ev(EV_KEY, KEY_LEFTCTRL, 0));
    out.push(syn());
    out
}

fn ascii_key(ch: char) -> Option<(u16, bool)> {
    match ch {
        'a'..='z' => Some((KEY_A + (ch as u16 - 'a' as u16), false)),
        'A'..='Z' => Some((KEY_A + (ch as u16 - 'A' as u16), true)),
        '1'..='9' => Some((KEY_1 + (ch as u16 - '1' as u16), false)),
        '0' => Some((KEY_0, false)),
        ' ' => Some((KEY_SPACE, false)),
        _ => None,
    }
}
