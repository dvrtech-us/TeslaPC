//! Pick the supported desktop mode nearest a viewport aspect ratio.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Mode {
    pub w: i32,
    pub h: i32,
}

/// Closest aspect to `target_aspect` (width/height), heights in `[480, max_height]`.
/// On a near-tie (within 1%) the larger area wins. `None` when nothing qualifies.
pub fn best_for_aspect(modes: &[Mode], target_aspect: f64, max_height: i32) -> Option<Mode> {
    if target_aspect <= 0.0 {
        return None;
    }
    let mut ordered: Vec<Mode> = modes
        .iter()
        .copied()
        .filter(|m| m.h >= 480 && m.h <= max_height && m.w > 0 && m.h > 0)
        .collect();
    ordered.sort_by(|a, b| (b.w as i64 * b.h as i64).cmp(&(a.w as i64 * a.h as i64)));

    let mut best: Option<Mode> = None;
    let mut best_err = f64::MAX;
    for mode in ordered {
        let err = ((mode.w as f64) / (mode.h as f64) - target_aspect).abs();
        let replace = if let Some(current) = best {
            err < best_err - 0.01
                || ((err - best_err).abs() <= 0.01 && mode.w as i64 * mode.h as i64 > current.w as i64 * current.h as i64)
        } else {
            true
        };
        if replace {
            best = Some(mode);
            best_err = best_err.min(err);
        }
    }
    best
}

/// Cap used by `/display/match`: at least 480, otherwise the stream height.
pub fn match_max_height(stream_height: i32) -> i32 {
    stream_height.max(480)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DisplayQuery {
    pub output: String,
    pub current: (i32, i32),
    pub modes: Vec<(i32, i32)>,
}

/// Parse `xrandr --query`. The connected output's mode list is every `WxH` under it.
pub fn parse_xrandr(text: &str) -> Option<DisplayQuery> {
    let mut output = None;
    let mut current = None;
    let mut modes = Vec::new();
    let mut in_output = false;
    for line in text.lines() {
        if !line.starts_with(' ') && !line.starts_with('\t') {
            in_output = false;
            if line.contains(" connected") {
                output = line.split_whitespace().next().map(|s| s.to_string());
                in_output = true;
                if let Some(mode) = line.split_whitespace().find_map(parse_wxh) {
                    current = Some(mode);
                }
            }
            continue;
        }
        if !in_output {
            continue;
        }
        let trimmed = line.trim();
        if let Some(mode) = parse_wxh(trimmed.split_whitespace().next().unwrap_or("")) {
            if trimmed.contains('*') {
                current = Some(mode);
            }
            if !modes.contains(&mode) {
                modes.push(mode);
            }
        }
    }
    let output = output?;
    let current = current.unwrap_or(modes.first().copied().unwrap_or((0, 0)));
    if modes.is_empty() && current != (0, 0) {
        modes.push(current);
    }
    Some(DisplayQuery { output, current, modes })
}

/// Parse `wlr-randr`. Modes look like `1920x1080 px, 60 Hz (current)`.
pub fn parse_wlr_randr(text: &str) -> Option<DisplayQuery> {
    let mut output = None;
    let mut current = None;
    let mut modes = Vec::new();
    for line in text.lines() {
        if !line.starts_with(' ') && !line.starts_with('\t') && !line.is_empty() {
            output = line.split_whitespace().next().map(|s| s.to_string());
            continue;
        }
        let trimmed = line.trim();
        if let Some(mode) = parse_wxh(trimmed.split_whitespace().next().unwrap_or("")) {
            if trimmed.contains("current") {
                current = Some(mode);
            }
            if !modes.contains(&mode) {
                modes.push(mode);
            }
        }
    }
    let output = output?;
    if modes.is_empty() {
        return None;
    }
    let current = current.unwrap_or(modes[0]);
    Some(DisplayQuery { output, current, modes })
}

pub fn choose_display_query(xrandr_text: &str, wlr_text: &str, wayland: bool) -> Option<DisplayQuery> {
    if wayland {
        parse_wlr_randr(wlr_text).or_else(|| parse_xrandr(xrandr_text))
    } else {
        parse_xrandr(xrandr_text).or_else(|| parse_wlr_randr(wlr_text))
    }
}

pub fn display_set_args(wayland: bool, output: &str, width: i32, height: i32) -> Vec<String> {
    let mode = format!("{width}x{height}");
    if wayland {
        vec!["wlr-randr".into(), "--output".into(), output.into(), "--mode".into(), mode]
    } else {
        vec!["xrandr".into(), "--output".into(), output.into(), "--mode".into(), mode]
    }
}

fn parse_wxh(token: &str) -> Option<(i32, i32)> {
    let (w, h) = token.split_once('x')?;
    let w = w.parse().ok()?;
    let h = h.parse().ok()?;
    if w > 0 && h > 0 { Some((w, h)) } else { None }
}
