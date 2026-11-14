//! Browser files compiled into the binary and served as raw bytes.

pub struct Asset {
    pub bytes: &'static [u8],
    pub content_type: &'static str,
    /// HTML is the only kind the server rewrites (host and port tokens).
    pub html: bool,
}

pub fn asset(path: &str) -> Option<Asset> {
    let path = path.split('?').next().unwrap_or(path);
    let name = path.trim_start_matches('/');
    if name.contains("..") || name.contains('\\') {
        return None;
    }
    let (bytes, content_type, html) = match name {
        "" | "index.html" => (INDEX, "text/html; charset=utf-8", true),
        "config.html" => (CONFIG, "text/html; charset=utf-8", true),
        "play.html" => (PLAY, "text/html; charset=utf-8", true),
        "list.html" => (LIST, "text/html; charset=utf-8", true),
        "style.css" => (CSS, "text/css; charset=utf-8", false),
        "PCMPlayerProcessor.js" => (PCM, "application/javascript; charset=utf-8", false),
        "audio-client.js" => (AUDIO_JS, "application/javascript; charset=utf-8", false),
        "teslapc.png" => (PNG, "image/png", false),
        _ => return None,
    };
    Some(Asset { bytes, content_type, html })
}

const INDEX: &[u8] = include_bytes!("../assets/index.html");
const CONFIG: &[u8] = include_bytes!("../assets/config.html");
const PLAY: &[u8] = include_bytes!("../assets/play.html");
const LIST: &[u8] = include_bytes!("../assets/list.html");
const CSS: &[u8] = include_bytes!("../assets/style.css");
const PCM: &[u8] = include_bytes!("../assets/PCMPlayerProcessor.js");
const AUDIO_JS: &[u8] = include_bytes!("../assets/audio-client.js");
const PNG: &[u8] = include_bytes!("../assets/teslapc.png");
