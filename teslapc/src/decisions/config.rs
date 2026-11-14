//! `.env` read/write and the public `/config` document. The token is never serialized.

use serde::Serialize;
use serde_json::Value;

pub const KEY_HTTPS_HOST: &str = "TESLAPC_HTTPS_HOST";
pub const KEY_CF_TOKEN: &str = "TESLAPC_CF_TOKEN";
pub const KEY_ACME_EMAIL: &str = "TESLAPC_ACME_EMAIL";
pub const KEY_VIDEO_ROOT: &str = "TESLAPC_VIDEO_ROOT";
pub const KEY_LOG_LEVEL: &str = "TESLAPC_LOG_LEVEL";
pub const KEY_STREAM_HEIGHT: &str = "TESLAPC_STREAM_HEIGHT";
pub const KEY_INPUT_MODE: &str = "TESLAPC_INPUT_MODE";

/// Keys the running process applies without a restart.
pub const LIVE_KEYS: &[&str] = &[KEY_VIDEO_ROOT, KEY_LOG_LEVEL, KEY_STREAM_HEIGHT, KEY_INPUT_MODE];

#[derive(Debug, Clone)]
pub struct SavedConfig {
    pub text: String,
    /// Keys actually written by this save.
    pub saved: Vec<(String, String)>,
    pub restart_needed: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct PublicConfig {
    #[serde(rename = "httpsHost")]
    pub https_host: String,
    #[serde(rename = "acmeEmail")]
    pub acme_email: String,
    #[serde(rename = "videoRoot")]
    pub video_root: String,
    #[serde(rename = "logLevel")]
    pub log_level: String,
    #[serde(rename = "streamHeight")]
    pub stream_height: i32,
    #[serde(rename = "inputMode")]
    pub input_mode: String,
    pub version: String,
    #[serde(rename = "cfTokenSet")]
    pub cf_token_set: bool,
}

pub fn public_config_json(config: &PublicConfig) -> String {
    serde_json::to_string(config).unwrap_or_else(|_| "{}".to_string())
}

pub fn public_config_value(config: &PublicConfig) -> Value {
    serde_json::to_value(config).unwrap_or(Value::Null)
}

/// `error`, `warn`/`warning`, `info`, `debug`/`verbose`, or `0`–`3`. Anything else is ignored.
pub fn parse_log_level(raw: &str) -> Option<&'static str> {
    match raw.trim().to_ascii_lowercase().as_str() {
        "error" | "0" => Some("error"),
        "warn" | "warning" | "1" => Some("warn"),
        "info" | "2" => Some("info"),
        "debug" | "verbose" | "3" => Some("debug"),
        _ => None,
    }
}

pub fn input_mode_from(raw: Option<&str>) -> &'static str {
    match raw.map(|s| s.trim().to_ascii_lowercase()).as_deref() {
        Some("mouse") => "mouse",
        _ => "touch",
    }
}

/// Apply a `application/x-www-form-urlencoded` body to an env file.
/// A blank `cfToken` keeps the stored token. Unknown lines stay in place.
/// `restartNeeded` is true when host, token, or email was written.
pub fn apply_config_body(existing_env: &str, body: &str) -> SavedConfig {
    let form = form_pairs(body);
    let mut updates: Vec<(String, String)> = Vec::new();

    if let Some(host) = form.get("httpsHost") {
        updates.push((KEY_HTTPS_HOST.to_string(), host.trim().to_string()));
    }
    if let Some(email) = form.get("acmeEmail") {
        updates.push((KEY_ACME_EMAIL.to_string(), email.trim().to_string()));
    }
    if let Some(root) = form.get("videoRoot") {
        let root = root.trim();
        if !root.is_empty() {
            updates.push((KEY_VIDEO_ROOT.to_string(), root.to_string()));
        }
    }
    if let Some(level) = form.get("logLevel") {
        if let Some(parsed) = parse_log_level(level) {
            updates.push((KEY_LOG_LEVEL.to_string(), parsed.to_string()));
        }
    }
    if let Some(height) = form.get("streamHeight") {
        if let Ok(h) = height.trim().parse::<i32>() {
            if (240..=2160).contains(&h) {
                updates.push((KEY_STREAM_HEIGHT.to_string(), h.to_string()));
            }
        }
    }
    if let Some(mode) = form.get("inputMode") {
        let mode = mode.trim().to_ascii_lowercase();
        if mode == "touch" || mode == "mouse" {
            updates.push((KEY_INPUT_MODE.to_string(), mode));
        }
    }
    if let Some(token) = form.get("cfToken") {
        let token = token.trim();
        if !token.is_empty() {
            updates.push((KEY_CF_TOKEN.to_string(), token.to_string()));
        }
    }

    let restart_needed = updates.iter().any(|(k, _)| {
        k == KEY_HTTPS_HOST || k == KEY_CF_TOKEN || k == KEY_ACME_EMAIL
    });
    let text = update_env_text(existing_env, &updates);
    SavedConfig {
        text,
        saved: updates,
        restart_needed,
    }
}

pub fn update_env_text(existing: &str, updates: &[(String, String)]) -> String {
    let mut lines = split_env_lines(existing);
    for (key, value) in updates {
        let line = format!("{key}={value}");
        if let Some(idx) = lines.iter().position(|l| env_key(l) == Some(key.as_str())) {
            lines[idx] = line;
        } else {
            lines.push(line);
        }
    }
    if lines.is_empty() {
        String::new()
    } else {
        lines.join("\n") + "\n"
    }
}

/// Parse `KEY=VALUE` lines. Comments and blanks are skipped. Quoted values are unwrapped.
/// Existing keys in `base` win (real environment takes precedence over the file).
pub fn merge_dotenv(base: &[(String, String)], file_text: &str) -> Vec<(String, String)> {
    let mut out = base.to_vec();
    for line in split_env_lines(file_text) {
        let Some((key, value)) = split_env_kv(&line) else {
            continue;
        };
        if out.iter().any(|(k, _)| k == &key) {
            continue;
        }
        out.push((key, value));
    }
    out
}

fn split_env_lines(existing: &str) -> Vec<String> {
    if existing.is_empty() {
        return Vec::new();
    }
    let mut lines: Vec<String> = existing
        .split('\n')
        .map(|l| l.trim_end_matches('\r').to_string())
        .collect();
    if existing.ends_with('\n') || existing.ends_with("\r\n") {
        if lines.last().is_some_and(|l| l.is_empty()) {
            lines.pop();
        }
    }
    lines
}

fn env_key(line: &str) -> Option<&str> {
    let t = line.trim_start();
    if t.starts_with('#') || t.is_empty() {
        return None;
    }
    let (key, _) = t.split_once('=')?;
    let key = key.trim();
    if key.is_empty() { None } else { Some(key) }
}

fn split_env_kv(line: &str) -> Option<(String, String)> {
    let t = line.trim();
    if t.is_empty() || t.starts_with('#') {
        return None;
    }
    let (key, value) = t.split_once('=')?;
    let key = key.trim();
    if key.is_empty() {
        return None;
    }
    let value = value.trim().trim_matches('"').to_string();
    Some((key.to_string(), value))
}

fn form_pairs(body: &str) -> std::collections::HashMap<String, String> {
    serde_urlencoded::from_str(body).unwrap_or_default()
}

#[cfg(test)]
mod env_value_tests {
    use super::split_env_kv;
    #[test]
    fn splits_quoted() {
        let (k, v) = split_env_kv("TESLAPC_CF_TOKEN=\"abc\"").unwrap();
        assert_eq!(k, "TESLAPC_CF_TOKEN");
        assert_eq!(v, "abc");
    }
}
