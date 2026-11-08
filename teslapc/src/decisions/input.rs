//! Pointer scaling, wheel sign, and the SendKeys mapping (including paste).

/// While a touch contact is down, repeat an update this often so Windows does not cancel it.
pub const TOUCH_REPEAT_MS: u64 = 100;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ScaledPointer {
    pub x: i32,
    pub y: i32,
    /// Wheel distance. Never scaled with the pointer.
    pub delta: i32,
}

/// Scale `x`/`y` from `display` (the browser surface) onto `screen` (the primary
/// desktop). A missing display size passes coordinates through. `delta` is copied unchanged.
pub fn scale_pointer(
    x: i32,
    y: i32,
    delta: i32,
    display: Option<(i32, i32)>,
    screen: (i32, i32),
) -> ScaledPointer {
    let Some((dw, dh)) = display else {
        return ScaledPointer { x, y, delta };
    };
    if dw == 0 || dh == 0 {
        return ScaledPointer { x, y, delta };
    }
    let sx = ((x as f64) / (dw as f64) * (screen.0 as f64)) as i32;
    let sy = ((y as f64) / (dh as f64) * (screen.1 as f64)) as i32;
    ScaledPointer { x: sx, y: sy, delta }
}

/// Browser `deltaY` sign is inverted for `MOUSEEVENTF_WHEEL` (positive means wheel forward).
pub fn wheel_delta(raw: i32) -> i32 {
    raw.wrapping_neg()
}

/// Map one key name to a SendKeys token.
/// Modifiers and lock keys are ignored. Unknown names are dropped.
/// A single character is typed, with SendKeys metacharacters wrapped in braces.
pub fn map_key(key: &str) -> Option<String> {
    if key.is_empty() {
        return None;
    }
    match key {
        "Shift" | "Control" | "Alt" | "Meta" | "OS" | "AltGraph" | "CapsLock" | "NumLock"
        | "ScrollLock" | "ContextMenu" | "Fn" | "FnLock" | "Hyper" | "Super" | "Symbol"
        | "Dead" | "Process" | "Unidentified" => return None,
        _ => {}
    }
    if key.chars().count() > 1 {
        let token = match key {
            "Backspace" => "{BACKSPACE}",
            "Enter" => "{ENTER}",
            "Tab" => "{TAB}",
            "Escape" => "{ESC}",
            "Delete" => "{DELETE}",
            "Insert" => "{INSERT}",
            "Home" => "{HOME}",
            "End" => "{END}",
            "PageUp" => "{PGUP}",
            "PageDown" => "{PGDN}",
            "ArrowLeft" => "{LEFT}",
            "ArrowRight" => "{RIGHT}",
            "ArrowUp" => "{UP}",
            "ArrowDown" => "{DOWN}",
            "F1" => "{F1}",
            "F2" => "{F2}",
            "F3" => "{F3}",
            "F4" => "{F4}",
            "F5" => "{F5}",
            "F6" => "{F6}",
            "F7" => "{F7}",
            "F8" => "{F8}",
            "F9" => "{F9}",
            "F10" => "{F10}",
            "F11" => "{F11}",
            "F12" => "{F12}",
            _ => return None,
        };
        return Some(token.to_string());
    }
    let ch = key.chars().next().unwrap();
    if "+^%~(){}[]".contains(ch) {
        Some(format!("{{{ch}}}"))
    } else {
        Some(ch.to_string())
    }
}

/// Expand pasted text the way the browser client does: one token per character,
/// CR/LF collapsed to a single Enter, tabs to Tab. Each character goes through [`map_key`].
pub fn expand_paste(text: &str) -> Vec<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut out = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let ch = chars[i];
        if ch == '\r' || ch == '\n' {
            if ch == '\r' && chars.get(i + 1) == Some(&'\n') {
                i += 1;
            }
            if let Some(token) = map_key("Enter") {
                out.push(token);
            }
        } else if ch == '\t' {
            if let Some(token) = map_key("Tab") {
                out.push(token);
            }
        } else if let Some(token) = map_key(&ch.to_string()) {
            out.push(token);
        }
        i += 1;
    }
    out
}

/// Hover (move or up with no contact) is ignored. A down is accepted. One contact only.
pub fn touch_event_applies(kind: &str, contact_down: bool) -> bool {
    match kind {
        "down" => !contact_down,
        "move" | "up" => contact_down,
        _ => false,
    }
}
