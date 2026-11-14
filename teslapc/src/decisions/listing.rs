//! `/list.html` body. Folders come before files. A missing folder is an empty state, not an error.

use std::collections::HashMap;
use std::path::{Component, Path, PathBuf};

use super::history::percent_watched;

#[derive(Debug, Clone)]
pub struct FileProgress {
    pub position: f64,
    pub duration: f64,
    pub watched: bool,
}

pub fn render_guts(
    root_display: &str,
    path_display: &str,
    exists: bool,
    read_error: Option<&str>,
    directories: &[String],
    files: &[(String, String)],
    progress: &HashMap<String, FileProgress>,
) -> String {
    let mut sb = String::new();
    let at_root = trim_sep(path_display).eq_ignore_ascii_case(trim_sep(root_display));
    sb.push_str("<div class=\"topbar\">");
    sb.push_str("<a class=\"navbtn\" href=\"/\"><svg class=\"btn-icon\" viewBox=\"0 0 24 24\" width=\"1em\" height=\"1em\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M3 11l9-8 9 8M5 10v10h5v-6h4v6h5V10\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg> Screen</a>");
    sb.push_str("<a class=\"navbtn\" href=\"/config.html\">&#9881; Settings</a>");
    if !at_root {
        let parent = parent_display(path_display).unwrap_or_else(|| root_display.to_string());
        sb.push_str("<a class=\"navbtn\" href=\"/list.html?path=");
        sb.push_str(&url_encode(&parent));
        sb.push_str("\">&#8593; Up</a>");
    }
    sb.push_str("<span class=\"path\">");
    sb.push_str(&html_escape(path_display));
    sb.push_str("</span></div>");

    if !exists {
        sb.push_str("<div class=\"empty\">This folder doesn't exist:<br>");
        sb.push_str(&html_escape(path_display));
        sb.push_str("<br><br>Pick a valid <b>Video folder</b> in <a class=\"navbtn\" href=\"/config.html\">&#9881; Settings</a>.</div>");
        return sb;
    }
    if let Some(err) = read_error {
        sb.push_str("<div class=\"empty\">Couldn't read this folder:<br>");
        sb.push_str(&html_escape(err));
        sb.push_str("</div>");
        return sb;
    }

    sb.push_str("<div class=\"list\">");
    for directory in directories {
        let name = file_name(directory);
        sb.push_str("<a class=\"row folder\" href=\"/list.html?path=");
        sb.push_str(&url_encode(directory));
        sb.push_str("\"><span class=\"ic\">&#128193;</span><span class=\"nm\">");
        sb.push_str(&html_escape(&name));
        sb.push_str("</span><span class=\"chev\">&#8250;</span></a>");
    }
    for (full, name) in files {
        let prog = progress.get(&full.to_ascii_lowercase());
        let watched = prog.map(|p| p.watched).unwrap_or(false);
        let pct = prog
            .map(|p| percent_watched(p.position, p.duration))
            .unwrap_or(0);
        sb.push_str("<a class=\"row file");
        if watched {
            sb.push_str(" watched");
        }
        sb.push_str("\" href=\"/play.html?FILENAME=");
        sb.push_str(&url_encode(full));
        sb.push_str("\"><span class=\"ic\">");
        sb.push_str(if watched { "<svg class=\"btn-icon\" viewBox=\"0 0 24 24\" width=\"1em\" height=\"1em\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M4 12l5 5L20 6\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>" } else { "&#127916;" });
        sb.push_str("</span><span class=\"nm\">");
        sb.push_str(&html_escape(name));
        sb.push_str("</span>");
        if watched {
            sb.push_str("<span class=\"badge done\">Watched</span>");
        } else if pct > 0 {
            sb.push_str("<span class=\"badge\">");
            sb.push_str(&pct.to_string());
            sb.push_str("%</span>");
        }
        sb.push_str("</a>");
    }
    sb.push_str("</div>");
    if directories.is_empty() && files.is_empty() {
        sb.push_str("<div class=\"empty\">This folder is empty.</div>");
    }
    sb
}

/// Resolve a browse path. `Ok` is inside the root (it may still be missing on disk).
/// `Err` means the request escaped the root and must not be read.
pub fn resolve_browse(root: &Path, requested: Option<&str>) -> Result<PathBuf, ()> {
    let raw = requested.map(str::trim).filter(|s| !s.is_empty());
    let candidate = match raw {
        None => root.to_path_buf(),
        Some(text) => {
            let p = Path::new(text);
            if p.is_absolute() {
                p.to_path_buf()
            } else {
                root.join(p)
            }
        }
    };
    let root_key = normalize_key(root);
    let cand_key = normalize_key(&candidate);
    if cand_key == root_key || cand_key.starts_with(&(root_key.clone() + std::path::MAIN_SEPARATOR_STR)) {
        Ok(candidate)
    } else {
        Err(())
    }
}

fn normalize_key(path: &Path) -> String {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    let text = out.to_string_lossy().trim_end_matches(['\\', '/']).to_string();
    text.to_ascii_lowercase()
}

fn trim_sep(path: &str) -> &str {
    path.trim_end_matches(['\\', '/'])
}

fn parent_display(path: &str) -> Option<String> {
    let trimmed = trim_sep(path);
    let idx = trimmed.rfind(['\\', '/'])?;
    if idx == 0 {
        return None;
    }
    Some(trimmed[..idx].to_string())
}

fn file_name(path: &str) -> String {
    let trimmed = trim_sep(path);
    trimmed
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or(trimmed)
        .to_string()
}

pub fn html_escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for ch in text.chars() {
        match ch {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            _ => out.push(ch),
        }
    }
    out
}

pub fn url_encode(text: &str) -> String {
    urlencoding::encode(text).into_owned()
}
