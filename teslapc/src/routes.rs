//! HTTP and WebSocket routes. Every protocol choice calls a function in `decisions`.

use std::collections::HashMap;
use std::path::Path;
use std::sync::atomic::Ordering;
use std::time::Duration;

use axum::body::Body;
use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::FromRequestParts;
use axum::extract::{Query, Request, State};
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use bytes::Bytes;

use serde::Serialize;

use crate::assets;
use crate::capture_loop;
use crate::decisions::config::{apply_config_body, public_config_json, PublicConfig, KEY_ACME_EMAIL, KEY_CF_TOKEN, KEY_HTTPS_HOST, KEY_INPUT_MODE, KEY_LOG_LEVEL, KEY_STREAM_HEIGHT, KEY_VIDEO_ROOT};
use crate::decisions::input::{map_key, scale_pointer, touch_event_applies, wheel_delta, TOUCH_REPEAT_MS};
use crate::decisions::listing::{render_guts, resolve_browse};
use crate::decisions::socket::{classify_socket, SocketRole};
use crate::decisions::stream::{mjpeg_content_type, mjpeg_part, clamp_stream_height, SLOW_CLIENT_WAIT_MS};
use crate::paths;
use crate::platform;
use crate::playback;
use crate::state::App;

pub fn router(app: App) -> Router {
    Router::new()
        .route("/stream", get(stream))
        .route("/ws/audio", get(audio_ws))
        .route("/ws/input", get(input_ws))
        .route("/ws/{*rest}", get(input_ws))
        .route("/config", get(config_get).post(config_post))
        .route("/version", get(version))
        .route("/list.html", get(list_page))
        .route("/play.html", get(play_page))
        .route("/media/{action}", get(media_action))
        .route("/display/{action}", get(display_action))
        .route("/hotspot", post(hotspot))
        .fallback(fallback)
        .with_state(app)
}

async fn version() -> impl IntoResponse {
    json_body(&serde_json::json!({ "version": crate::crate_version() }).to_string())
}

async fn config_get(State(app): State<App>) -> impl IntoResponse {
    let host = app.inner.https_host.lock().map(|s| s.clone()).unwrap_or_default();
    let email = app.inner.acme_email.lock().map(|s| s.clone()).unwrap_or_default();
    let level = app.inner.log_level.lock().map(|s| s.clone()).unwrap_or_else(|_| "info".into());
    let mode = app.inner.input_mode.lock().map(|s| s.clone()).unwrap_or_else(|_| "touch".into());
    let body = public_config_json(&PublicConfig {
        https_host: host,
        acme_email: email,
        video_root: app.video_root(),
        log_level: level,
        stream_height: app.inner.stream_height.load(Ordering::Relaxed),
        input_mode: mode,
        version: crate::crate_version().into(),
        cf_token_set: app.cf_token_set(),
    });
    json_body(&body)
}

async fn config_post(State(app): State<App>, body: Bytes) -> impl IntoResponse {
    let text = String::from_utf8_lossy(&body);
    let existing = std::fs::read_to_string(paths::env_file()).unwrap_or_default();
    let saved = apply_config_body(&existing, &text);
    if let Err(err) = write_env(&saved.text) {
        return json_body(&serde_json::json!({ "saved": false, "error": err }).to_string());
    }
    for (key, value) in &saved.saved {
        std::env::set_var(key, value);
        match key.as_str() {
            KEY_VIDEO_ROOT => {
                if let Ok(mut root) = app.inner.video_root.lock() {
                    *root = value.clone();
                }
            }
            KEY_LOG_LEVEL => {
                if let Ok(mut level) = app.inner.log_level.lock() {
                    *level = value.clone();
                }
            }
            KEY_STREAM_HEIGHT => {
                if let Ok(h) = value.parse::<i32>() {
                    app.inner.stream_height.store(clamp_stream_height(h), Ordering::Relaxed);
                    app.bump_capture();
                }
            }
            KEY_INPUT_MODE => {
                if let Ok(mut mode) = app.inner.input_mode.lock() {
                    *mode = value.clone();
                }
            }
            KEY_HTTPS_HOST => {
                if let Ok(mut host) = app.inner.https_host.lock() {
                    *host = value.clone();
                }
            }
            KEY_ACME_EMAIL => {
                if let Ok(mut email) = app.inner.acme_email.lock() {
                    *email = value.clone();
                }
            }
            KEY_CF_TOKEN => {}
            _ => {}
        }
    }
    println!("[Config] Saved settings via web UI.");
    #[derive(Serialize)]
    struct Resp {
        saved: bool,
        #[serde(rename = "restartNeeded")]
        restart_needed: bool,
    }
    json_body(&serde_json::to_string(&Resp { saved: true, restart_needed: saved.restart_needed }).unwrap_or_else(|_| "{\"saved\":true}".into()))
}

fn write_env(text: &str) -> Result<(), String> {
    let path = paths::env_file();
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    std::fs::write(path, text).map_err(|e| e.to_string())
}

async fn list_page(State(app): State<App>, Query(query): Query<HashMap<String, String>>) -> impl IntoResponse {
    let root = app.video_root();
    let requested = query.get("path").map(String::as_str);
    let display = requested.unwrap_or(&root).to_string();
    let progress = app.inner.library.lock().ok().and_then(|g| g.as_ref().map(|l| l.get_all())).unwrap_or_default();
    let guts = match resolve_browse(Path::new(&root), requested) {
        Err(()) => render_guts(&root, &display, false, None, &[], &[], &progress),
        Ok(path) => {
            if !path.is_dir() {
                render_guts(&root, &path.to_string_lossy(), false, None, &[], &[], &progress)
            } else {
                match read_listing(&path) {
                    Ok((dirs, files)) => render_guts(&root, &path.to_string_lossy(), true, None, &dirs, &files, &progress),
                    Err(err) => {
                        app.log("warn", &format!("[Files] Could not read '{}': {err}", path.display()));
                        render_guts(&root, &path.to_string_lossy(), true, Some(&err), &[], &[], &progress)
                    }
                }
            }
        }
    };
    let page = assets::asset("/list.html").map(|a| String::from_utf8_lossy(a.bytes).replace("{{GUTS}}", &guts)).unwrap_or(guts);
    html_response(page.into_bytes(), 8080, "")
}

fn read_listing(path: &Path) -> Result<(Vec<String>, Vec<(String, String)>), String> {
    let mut dirs = Vec::new();
    let mut files = Vec::new();
    let exts = [".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts"];
    for entry in std::fs::read_dir(path).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let full = entry.path();
        let text = full.to_string_lossy().into_owned();
        if entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
            dirs.push(text);
        } else if exts.iter().any(|ext| text.to_ascii_lowercase().ends_with(ext)) {
            let name = entry.file_name().to_string_lossy().into_owned();
            files.push((text, name));
        }
    }
    dirs.sort_by(|a, b| a.to_ascii_lowercase().cmp(&b.to_ascii_lowercase()));
    files.sort_by(|a, b| a.1.to_ascii_lowercase().cmp(&b.1.to_ascii_lowercase()));
    Ok((dirs, files))
}

async fn play_page(State(app): State<App>, req: Request) -> impl IntoResponse {
    let query: HashMap<String, String> = req.uri().query().map(parse_query).unwrap_or_default();
    let file = query.get("FILENAME").cloned().unwrap_or_default();
    let port = req.uri().port_u16().unwrap_or(8080);
    let host = request_host(&req);
    if file.is_empty() {
        let page = template_play("No video file specified", "");
        return html_response(page, port, &host);
    }
    if playback::ffmpeg_available(&app) {
        playback::play(&app, &file);
        let title = Path::new(&file).file_stem().and_then(|s| s.to_str()).unwrap_or("Video");
        let page = template_play(&html_escape(title), &js_escape(&file));
        return html_response(page, port, &host);
    }
    #[cfg(windows)]
    {
        let page = playback::vlc_fallback_html(&file);
        return html_response(page.into_bytes(), port, &host);
    }
    #[cfg(not(windows))]
    {
        let page = playback::linux_player_error_html();
        html_response(page.into_bytes(), port, &host)
    }
}

fn template_play(title: &str, filepath: &str) -> Vec<u8> {
    let raw = assets::asset("/play.html").map(|a| String::from_utf8_lossy(a.bytes).into_owned()).unwrap_or_default();
    raw.replace("{{TITLE}}", title).replace("{{FILEPATH}}", filepath).into_bytes()
}

async fn media_action(State(app): State<App>, axum::extract::Path(action): axum::extract::Path<String>, Query(query): Query<HashMap<String, String>>) -> Response {
    match action.to_ascii_lowercase().as_str() {
        "play" => {
            if let Some(path) = query.get("path") {
                playback::play(&app, path);
            }
        }
        "pause" => playback::pause(&app),
        "resume" => playback::resume(&app),
        "stop" => playback::stop(&app),
        "seek" => {
            if let Some(t) = query.get("t").and_then(|t| t.parse().ok()) {
                playback::seek(&app, t);
            }
        }
        "status" => {}
        _ => return StatusCode::NOT_FOUND.into_response(),
    }
    json_body(&playback::status_json(&app))
}

async fn display_action(State(app): State<App>, axum::extract::Path(action): axum::extract::Path<String>, Query(query): Query<HashMap<String, String>>) -> impl IntoResponse {
    let action = action.trim_end_matches('/').to_ascii_lowercase();
    let (_snap_changed, width, height, modes) = platform::display_snapshot();
    let mut changed = false;
    if action == "match" {
        if let (Some(vw), Some(vh)) = (query.get("w").and_then(|v| v.parse::<i32>().ok()), query.get("h").and_then(|v| v.parse::<i32>().ok())) {
            if vw > 0 && vh > 0 {
                let max_h = crate::decisions::display::match_max_height(app.inner.stream_height.load(Ordering::Relaxed));
                let mode_list: Vec<_> = modes.iter().copied().map(|(w, h)| crate::decisions::display::Mode { w, h }).collect();
                if let Some(best) = crate::decisions::display::best_for_aspect(&mode_list, vw as f64 / vh as f64, max_h) {
                    if best.w != width || best.h != height {
                        changed = platform::try_set_mode(best.w, best.h);
                        if changed {
                            app.bump_capture();
                            println!("[Display] Matched viewport {vw}x{vh} -> {}x{}", best.w, best.h);
                        }
                    }
                }
            }
        }
    } else if action == "set" {
        if let (Some(w), Some(h)) = (query.get("w").and_then(|v| v.parse::<i32>().ok()), query.get("h").and_then(|v| v.parse::<i32>().ok())) {
            if w > 0 && h > 0 {
                changed = platform::try_set_mode(w, h);
                if changed {
                    app.bump_capture();
                }
            }
        }
    }
    let (changed_now, width, height, modes) = if changed {
        let snap = platform::display_snapshot();
        (true, snap.1, snap.2, snap.3)
    } else {
        (false, width, height, modes)
    };
    let _ = changed_now;
    let modes_json: Vec<_> = modes.into_iter().map(|(w, h)| serde_json::json!({ "w": w, "h": h })).collect();
    json_body(&serde_json::json!({ "changed": changed, "width": width, "height": height, "modes": modes_json }).to_string())
}

async fn hotspot(State(app): State<App>, body: Bytes) -> impl IntoResponse {
    let form: HashMap<String, String> = serde_urlencoded::from_bytes(&body).unwrap_or_default();
    let on = matches!(form.get("on").map(String::as_str), Some("1") | Some("true") | Some("on"));
    match platform::hotspot_set(on, &app) {
        Ok(message) => json_body(&serde_json::json!({ "ok": true, "message": message }).to_string()),
        Err(message) => json_body(&serde_json::json!({ "ok": false, "message": message }).to_string()),
    }
}

async fn stream(State(app): State<App>) -> impl IntoResponse {
    app.inner.stream_clients.fetch_add(1, Ordering::Relaxed);
    capture_loop::ensure_started(&app);
    let rx = app.inner.frames.subscribe();
    let app_stream = app.clone();
    let stream = futures_util::stream::unfold((rx, app_stream, StreamClientGuard(app.clone()), 0u64), |(mut rx, app, guard, mut last)| async move {
        loop {
            let current = rx.borrow().clone();
            if current.seq != 0 && current.seq != last {
                last = current.seq;
                let part = mjpeg_part(&current.jpeg);
                return Some((Ok::<Bytes, std::io::Error>(Bytes::from(part)), (rx, app, guard, last)));
            }
            let alive = app.inner.stream_clients.load(Ordering::Relaxed) > 0
                && (app.inner.media_mode.load(Ordering::Relaxed) || capture_running());
            match tokio::time::timeout(Duration::from_millis(SLOW_CLIENT_WAIT_MS), rx.changed()).await {
                Ok(Ok(())) => continue,
                Ok(Err(_)) => return None,
                Err(_) => {
                    if !alive && !app.inner.media_mode.load(Ordering::Relaxed) {
                        return None;
                    }
                }
            }
        }
    });
    let body = Body::from_stream(stream);
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, mjpeg_content_type())
        .header(header::CACHE_CONTROL, "no-cache, no-store, must-revalidate")
        .header(header::PRAGMA, "no-cache")
        .body(body)
        .unwrap()
}

fn capture_running() -> bool {
    true
}

struct StreamClientGuard(App);
impl Drop for StreamClientGuard {
    fn drop(&mut self) {
        self.0.inner.stream_clients.fetch_sub(1, Ordering::Relaxed);
    }
}

async fn audio_ws(State(app): State<App>, ws: WebSocketUpgrade) -> impl IntoResponse {
    ws.on_upgrade(move |socket| audio_socket(app, socket))
}

async fn audio_socket(app: App, mut socket: WebSocket) {
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
    if let Ok(mut clients) = app.inner.pcm.clients.lock() {
        clients.push(tx);
    }
    app.inner.pcm.client_count.fetch_add(1, Ordering::Relaxed);
    let announce = app.inner.audio_format.lock().map(|f| f.announce()).unwrap_or_else(|_| crate::decisions::audio::format_message(48000, 32, 2, "float"));
    if socket.send(Message::Text(announce.into())).await.is_err() {
        drop_audio_client(&app);
        return;
    }
    loop {
        tokio::select! {
            incoming = socket.recv() => {
                match incoming {
                    Some(Ok(Message::Close(_))) | None => break,
                    Some(Err(_)) => break,
                    _ => {}
                }
            }
            buf = rx.recv() => {
                match buf {
                    Some(bytes) => {
                        if socket.send(Message::Binary(bytes)).await.is_err() {
                            break;
                        }
                    }
                    None => break,
                }
            }
        }
    }
    drop_audio_client(&app);
}

fn drop_audio_client(app: &App) {
    app.inner.pcm.client_count.fetch_sub(1, Ordering::Relaxed);
}

async fn input_ws(State(app): State<App>, ws: WebSocketUpgrade) -> impl IntoResponse {
    ws.on_upgrade(move |socket| input_socket(app, socket))
}

async fn input_socket(app: App, mut socket: WebSocket) {
    while let Some(message) = socket.recv().await {
        let Ok(message) = message else { break };
        if matches!(message, Message::Close(_)) {
            break;
        }
        let text = match message {
            Message::Text(text) => text.to_string(),
            _ => continue,
        };
        app.log("debug", &format!("Received input: {text}"));
        if let Err(err) = handle_input_text(&app, &text) {
            println!("Error: {err}");
        }
    }
    if let Ok(mut down) = app.inner.touch_down.lock() {
        *down = false;
    }
    platform::end_contact();
}

fn handle_input_text(app: &App, text: &str) -> Result<(), String> {
    if !app.inner.input_enabled.load(Ordering::Relaxed) {
        return Ok(());
    }
    let value: serde_json::Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
    let kind = value.get("Type").and_then(|v| v.as_str()).unwrap_or("").to_string();
    if kind.eq_ignore_ascii_case("key") {
        let key = value.get("Key").and_then(|v| v.as_str()).unwrap_or("");
        if let Some(token) = map_key(key) {
            platform::inject_key(&token);
        }
        return Ok(());
    }
    let x = value.get("X").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
    let y = value.get("Y").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
    let delta = value.get("Delta").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
    let display = value.get("DisplaySize").and_then(|d| {
        let w = d.get("width").and_then(|v| v.as_i64())? as i32;
        let h = d.get("height").and_then(|v| v.as_i64())? as i32;
        Some((w, h))
    });
    let screen = platform::primary_size();
    let scaled = scale_pointer(x, y, delta, display, screen);
    if kind == "wheel" {
        let wheel = wheel_delta(scaled.delta);
        let has_position = display.is_some() || x != 0 || y != 0;
        if has_position {
            platform::inject_pointer("move", scaled.x, scaled.y, false);
        }
        app.log("debug", &format!("Wheel delta={wheel} (raw {delta})"));
        platform::inject_wheel(wheel);
        return Ok(());
    }
    let touch = app.inner.input_mode.lock().map(|m| m.as_str() != "mouse").unwrap_or(true);
    let touch_ok = app.inner.touch_enabled.load(Ordering::Relaxed);
    if crate::decisions::input::pointer_path(&kind, touch, touch_ok) == crate::decisions::input::PointerPath::Touch {
        let down = app.inner.touch_down.lock().map(|d| *d).unwrap_or(false);
        if !touch_event_applies(&kind, down) {
            return Ok(());
        }
        if kind == "down" {
            if let Ok(mut flag) = app.inner.touch_down.lock() {
                *flag = true;
            }
        }
        if kind == "up" {
            if let Ok(mut flag) = app.inner.touch_down.lock() {
                *flag = false;
            }
        }
        platform::inject_pointer(&kind, scaled.x, scaled.y, true);
        return Ok(());
    }
    platform::inject_pointer(&kind, scaled.x, scaled.y, false);
    Ok(())
}

pub async fn touch_repeat(app: App) {
    loop {
        tokio::time::sleep(Duration::from_millis(TOUCH_REPEAT_MS)).await;
        let down = app.inner.touch_down.lock().map(|d| *d).unwrap_or(false);
        if down && app.inner.input_enabled.load(Ordering::Relaxed) {
            platform::repeat_touch();
        }
    }
}

pub async fn silence_keepalive(app: App) {
    let mut last_tick = std::time::Instant::now();
    loop {
        tokio::time::sleep(Duration::from_millis(crate::decisions::audio::KEEPALIVE_POLL_MS)).await;
        // The sleep overshoots, so each buffer covers the time that really passed.
        let elapsed_ms = last_tick.elapsed().as_millis() as u64;
        last_tick = std::time::Instant::now();
        let input = crate::decisions::audio::KeepaliveInput {
            idle_ms: app.inner.pcm.idle_ms(),
            silent_ms: app.inner.pcm.silent_ms(),
            queue_empty: app.inner.pcm.queue_empty(),
            capturing: app.inner.pcm.capturing.load(Ordering::Relaxed),
            media_mode: app.inner.pcm.media_mode.load(Ordering::Relaxed),
            has_clients: app.inner.pcm.client_count.load(Ordering::Relaxed) > 0,
            typical_buffer_bytes: app.inner.pcm.typical_bytes.load(Ordering::Relaxed),
        };
        if crate::decisions::audio::should_inject_silence(input) {
            let n = app
                .inner
                .audio_format
                .lock()
                .map(|f| crate::decisions::audio::keepalive_bytes(elapsed_ms, f.sample_rate, f.bits_per_sample, f.channels))
                .unwrap_or(0);
            app.inner.pcm.push(app.inner.pcm.noise(n), false);
        }
    }
}

pub async fn pcm_dispatch(app: App) {
    loop {
        app.inner.pcm.notify.notified().await;
        let batch: Vec<Bytes> = app.inner.pcm.queue.lock().map(|mut q| q.drain(..).collect()).unwrap_or_default();
        let clients: Vec<_> = app.inner.pcm.clients.lock().map(|c| c.clone()).unwrap_or_default();
        for buf in batch {
            for tx in &clients {
                let _ = tx.send(buf.clone());
            }
        }
    }
}

async fn fallback(State(app): State<App>, req: Request) -> Response {
    let upgrade = req.headers().get(header::UPGRADE).and_then(|v| v.to_str().ok()).unwrap_or("");
    if upgrade.eq_ignore_ascii_case("websocket") {
        let path = req.uri().path().to_string();
        if classify_socket(&path) == SocketRole::Audio {
            return StatusCode::BAD_REQUEST.into_response();
        }
        let (mut parts, _body) = req.into_parts();
        return match WebSocketUpgrade::from_request_parts(&mut parts, &()).await {
            Ok(ws) => ws.on_upgrade(move |socket| input_socket(app, socket)).into_response(),
            Err(rejection) => rejection.into_response(),
        };
    }
    let path = req.uri().path().to_string();
    let port = req.uri().port_u16().unwrap_or(8080);
    let host = request_host(&req);
    match assets::asset(&path) {
        Some(asset) if asset.html => html_response(asset.bytes.to_vec(), port, &host),
        Some(asset) => Response::builder()
            .status(StatusCode::OK)
            .header(header::CONTENT_TYPE, asset.content_type)
            // Pages are embedded in the binary; a new build must not be hidden by the browser cache.
            .header(header::CACHE_CONTROL, "no-cache")
            .body(Body::from(Bytes::from(asset.bytes)))
            .unwrap(),
        None => StatusCode::NOT_FOUND.into_response(),
    }
}

fn html_response(bytes: Vec<u8>, port: u16, host: &str) -> Response {
    let mut html = String::from_utf8_lossy(&bytes).into_owned();
    if !host.is_empty() {
        html = html.replace("//LOCALHOST", &format!("//{host}"));
    }
    if port == 8443 {
        html = html.replace(":8080", ":8443").replace(":8081", ":8444").replace(":8082", ":8445").replace("ws://", "wss://").replace("http://", "https://");
    }
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "text/html; charset=utf-8")
        .header(header::CACHE_CONTROL, "no-cache")
        .body(Body::from(html))
        .unwrap()
}

fn json_body(body: &str) -> Response {
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "application/json")
        .body(Body::from(body.to_string()))
        .unwrap()
}

fn request_host(req: &Request) -> String {
    req.headers()
        .get(header::HOST)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .split(':')
        .next()
        .unwrap_or("")
        .to_string()
}

fn parse_query(query: &str) -> HashMap<String, String> {
    serde_urlencoded::from_str(query).unwrap_or_default()
}

fn html_escape(text: &str) -> String {
    crate::decisions::listing::html_escape(text)
}

fn js_escape(text: &str) -> String {
    text.replace('\\', "\\\\").replace('\'', "\\'").replace('"', "\\\"")
}
