//! ffmpeg playback into the shared MJPEG and PCM queues.
//! Windows falls back to VLC only from the player page. Linux returns an error and launches nothing.

use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::Ordering;
use std::time::Instant;

use crate::decisions::audio::audio_chunk_bytes;
use crate::decisions::history::WATCHED_FRACTION;
use crate::decisions::listing::resolve_browse;
use crate::decisions::media::{
    ffmpeg_audio_arg_list, ffmpeg_video_arg_list, linux_missing_ffmpeg_error, take_jpegs, windows_vlc_plan,
};
use crate::state::{App, Playback};

pub fn locate_tools() -> (Option<PathBuf>, Option<PathBuf>) {
    let ffmpeg = locate("ffmpeg");
    let ffprobe = locate("ffprobe");
    match &ffmpeg {
        Some(path) => println!(
            "[Media] ffmpeg: {}",
            path.display()
        ),
        None => println!("[Media] ffmpeg not found. Set TESLAPC_FFMPEG or add ffmpeg to PATH."),
    }
    (ffmpeg, ffprobe)
}

fn locate(name: &str) -> Option<PathBuf> {
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Ok(env) = std::env::var("TESLAPC_FFMPEG") {
        let env_path = PathBuf::from(env.trim());
        if env_path.is_file() {
            if let Some(dir) = env_path.parent() {
                candidates.push(dir.join(exe_name(name)));
            }
        } else if env_path.is_dir() {
            candidates.push(env_path.join(exe_name(name)));
        } else if let Some(dir) = env_path.parent() {
            candidates.push(dir.join(exe_name(name)));
        }
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join(exe_name(name)));
        }
    }
    candidates.push(PathBuf::from(name));
    for candidate in candidates {
        if candidate.is_file() || (!candidate.is_absolute() && can_launch(&candidate)) {
            return Some(candidate);
        }
    }
    None
}

fn exe_name(name: &str) -> String {
    #[cfg(windows)]
    {
        format!("{name}.exe")
    }
    #[cfg(not(windows))]
    {
        name.to_string()
    }
}

fn can_launch(exe: &Path) -> bool {
    Command::new(exe)
        .arg("-version")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

pub fn ffmpeg_available(app: &App) -> bool {
    app.inner.playback.lock().ok().and_then(|p| p.ffmpeg.clone()).is_some()
}

/// Start playback. Returns false when ffmpeg is missing or the path is outside the video root.
pub fn play(app: &App, path: &str) -> bool {
    let ffmpeg = {
        let pb = app.inner.playback.lock().ok();
        pb.and_then(|p| p.ffmpeg.clone())
    };
    let Some(ffmpeg) = ffmpeg else {
        return false;
    };
    let root = app.video_root();
    let Ok(resolved) = resolve_browse(Path::new(&root), Some(path)) else {
        println!("[Media] Refused to play (outside root): {path}");
        return false;
    };
    if !resolved.is_file() {
        println!("[Media] Refused to play (missing): {}", resolved.display());
        return false;
    }
    let full = resolved.to_string_lossy().into_owned();
    let duration = probe_duration(app, &full);
    if let Ok(guard) = app.inner.library.lock() {
        if let Some(lib) = guard.as_ref() {
            lib.increment_play_count(&full);
        }
    }
    let mut start = 0.0;
    if let Ok(guard) = app.inner.library.lock() {
        if let Some(lib) = guard.as_ref() {
            if let Some(prog) = lib.get(&full) {
                if !prog.watched && duration > 0.0 && prog.position > 5.0 && prog.position < duration * WATCHED_FRACTION {
                    start = prog.position;
                }
            }
        }
    }
    app.inner.media_mode.store(true, Ordering::Relaxed);
    app.inner.pcm.media_mode.store(true, Ordering::Relaxed);
    {
        let mut pb = app.inner.playback.lock().unwrap();
        kill_locked(&mut pb);
        pb.playing = true;
        pb.paused = false;
        pb.file = Some(full.clone());
        pb.duration = duration;
        pb.position = start;
        pb.started = Some(Instant::now());
        pb.generation = pb.generation.wrapping_add(1);
    }
    spawn_pair(app, &ffmpeg, &full, start);
    println!("[Media] Playing {} ({duration:.1}s)", Path::new(&full).file_name().and_then(|s| s.to_str()).unwrap_or(&full));
    true
}

pub fn pause(app: &App) {
    let mut pb = app.inner.playback.lock().unwrap();
    if !pb.playing || pb.paused {
        return;
    }
    pb.position = current_locked(&pb);
    pb.paused = true;
    pb.started = None;
    let file = pb.file.clone();
    let pos = pb.position;
    let dur = pb.duration;
    kill_locked(&mut pb);
    drop(pb);
    if let Some(file) = file {
        if let Ok(guard) = app.inner.library.lock() {
            if let Some(lib) = guard.as_ref() {
                lib.save_progress(&file, pos, dur);
            }
        }
    }
}

pub fn resume(app: &App) {
    let (ffmpeg, file, start) = {
        let pb = app.inner.playback.lock().unwrap();
        if !pb.playing || !pb.paused {
            return;
        }
        (pb.ffmpeg.clone(), pb.file.clone(), pb.position)
    };
    let (Some(ffmpeg), Some(file)) = (ffmpeg, file) else {
        return;
    };
    {
        let mut pb = app.inner.playback.lock().unwrap();
        pb.paused = false;
        pb.started = Some(Instant::now());
        pb.generation = pb.generation.wrapping_add(1);
    }
    spawn_pair(app, &ffmpeg, &file, start);
}

pub fn seek(app: &App, seconds: f64) {
    let (ffmpeg, file, duration, paused) = {
        let pb = app.inner.playback.lock().unwrap();
        if !pb.playing {
            return;
        }
        (pb.ffmpeg.clone(), pb.file.clone(), pb.duration, pb.paused)
    };
    let (Some(ffmpeg), Some(file)) = (ffmpeg, file) else {
        return;
    };
    let mut pos = if seconds.is_finite() && seconds > 0.0 { seconds } else { 0.0 };
    if duration > 0.0 && pos > duration - 1.0 {
        pos = (duration - 1.0).max(0.0);
    }
    {
        let mut pb = app.inner.playback.lock().unwrap();
        kill_locked(&mut pb);
        pb.position = pos;
        pb.generation = pb.generation.wrapping_add(1);
        if !paused {
            pb.started = Some(Instant::now());
            pb.paused = false;
        } else {
            pb.started = None;
        }
    }
    if !paused {
        spawn_pair(app, &ffmpeg, &file, pos);
    }
    if let Ok(guard) = app.inner.library.lock() {
        if let Some(lib) = guard.as_ref() {
            lib.save_progress(&file, pos, duration);
        }
    }
}

pub fn stop(app: &App) {
    let mut pb = app.inner.playback.lock().unwrap();
    let was = pb.playing || pb.paused;
    if was {
        if let Some(file) = pb.file.clone() {
            let pos = current_locked(&pb);
            let dur = pb.duration;
            drop(pb);
            if let Ok(guard) = app.inner.library.lock() {
                if let Some(lib) = guard.as_ref() {
                    lib.save_progress(&file, pos, dur);
                }
            }
            pb = app.inner.playback.lock().unwrap();
        }
    }
    kill_locked(&mut pb);
    pb.playing = false;
    pb.paused = false;
    pb.file = None;
    pb.position = 0.0;
    pb.duration = 0.0;
    pb.started = None;
    drop(pb);
    if was {
        app.inner.pcm.media_mode.store(false, Ordering::Relaxed);
        app.inner.media_mode.store(false, Ordering::Relaxed);
        app.bump_capture();
        println!("[Media] Stopped; live screen restored.");
    }
}

pub fn status_json(app: &App) -> String {
    let pb = app.inner.playback.lock().unwrap();
    let position = current_locked(&pb);
    let body = serde_json::json!({
        "ffmpeg": pb.ffmpeg.is_some(),
        "playing": pb.playing && !pb.paused,
        "paused": pb.paused,
        "position": position,
        "duration": pb.duration,
        "file": pb.file.as_ref().and_then(|f| Path::new(f).file_name().map(|n| n.to_string_lossy().into_owned())),
    });
    body.to_string()
}

/// Windows-only. Returns the status card HTML. Does not run on Linux.
pub fn vlc_fallback_html(file: &str) -> String {
    let plan = windows_vlc_plan(file);
    let status = match launch_vlc(&plan, file) {
        Ok(()) => "Playing on PC (ffmpeg not installed)".to_string(),
        Err(err) => format!("Could not start VLC: {err}"),
    };
    format!(
        "<html><head><title>Now Playing</title>\
<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no\">\
<link rel=\"stylesheet\" type=\"text/css\" href=\"/style.css\" />\
<meta http-equiv=\"refresh\" content=\"3; url=http://LOCALHOST:8080/\"></head>\
<body><div class=\"center\"><div class=\"card\">\
<div class=\"big-ic\">&#127916;</div><h1>{}</h1>\
<a class=\"btn primary\" href=\"http://LOCALHOST:8080/\">&#9664; Back to Screen</a>\
</div></div></body></html>",
        html_escape(&status)
    )
}

pub fn linux_player_error_html() -> String {
    let status = linux_missing_ffmpeg_error();
    format!(
        "<html><head><title>Now Playing</title>\
<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no\">\
<link rel=\"stylesheet\" type=\"text/css\" href=\"/style.css\" /></head>\
<body><div class=\"center\"><div class=\"card\">\
<div class=\"big-ic\">&#127916;</div><h1>{}</h1>\
<a class=\"btn primary\" href=\"/\">&#9664; Back to Screen</a>\
</div></div></body></html>",
        html_escape(status)
    )
}

fn launch_vlc(plan: &crate::decisions::media::VlcPlan, file: &str) -> Result<(), String> {
    let _ = Command::new("taskkill").args(["/F", "/IM", plan.image_name]).status();
    std::thread::sleep(std::time::Duration::from_millis(plan.wait_ms));
    Command::new(plan.executable)
        .arg("-vvv")
        .arg(file)
        .arg("--fullscreen")
        .spawn()
        .map_err(|e| e.to_string())?;
    println!("[Media] VLC fallback launched for {file}");
    Ok(())
}

fn spawn_pair(app: &App, ffmpeg: &Path, file: &str, start: f64) {
    let height = app.inner.stream_height.load(Ordering::Relaxed);
    let (rate, channels, sample_format, frame_bytes) = {
        let fmt = app.inner.audio_format.lock().unwrap();
        let bps = match fmt.sample_format.as_str() {
            "pcm16" => 2usize,
            "pcm24" => 3,
            "pcm32" => 4,
            _ => 4,
        };
        (fmt.sample_rate, fmt.channels, fmt.sample_format.clone(), bps * fmt.channels as usize)
    };
    let gen = app.inner.playback.lock().unwrap().generation;
    let video_args = ffmpeg_video_arg_list(file, start, height);
    let audio_args = ffmpeg_audio_arg_list(file, start, &sample_format, rate, channels);
    match spawn_ffmpeg(ffmpeg, &video_args) {
        Ok(mut child) => {
            if let Some(out) = child.stdout.take() {
                let app_v = app.clone();
                std::thread::Builder::new()
                    .name("MediaVideo".into())
                    .spawn(move || video_reader(app_v, out, gen))
                    .ok();
            }
            drain_stderr(child.stderr.take());
            if let Ok(mut pb) = app.inner.playback.lock() {
                pb.video = Some(child);
            }
        }
        Err(err) => println!("[Media] video ffmpeg failed: {err}"),
    }
    match spawn_ffmpeg(ffmpeg, &audio_args) {
        Ok(mut child) => {
            if let Some(out) = child.stdout.take() {
                let app_a = app.clone();
                std::thread::Builder::new()
                    .name("MediaAudio".into())
                    .spawn(move || audio_reader(app_a, out, gen, frame_bytes.max(1), rate))
                    .ok();
            }
            drain_stderr(child.stderr.take());
            if let Ok(mut pb) = app.inner.playback.lock() {
                pb.audio = Some(child);
            }
        }
        Err(err) => println!("[Media] audio ffmpeg failed: {err}"),
    }
}

fn spawn_ffmpeg(ffmpeg: &Path, args: &[String]) -> Result<std::process::Child, String> {
    Command::new(ffmpeg)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| e.to_string())
}

fn video_reader(app: App, mut stdout: std::process::ChildStdout, gen: u64) {
    let mut buf = Vec::new();
    let mut chunk = [0u8; 64 * 1024];
    loop {
        if app.inner.playback.lock().map(|p| p.generation != gen).unwrap_or(true) {
            break;
        }
        match stdout.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                buf.extend_from_slice(&chunk[..n]);
                let (frames, used) = take_jpegs(&buf);
                if used > 0 {
                    buf.drain(..used.min(buf.len()));
                }
                for frame in frames {
                    if app.inner.media_mode.load(Ordering::Relaxed) {
                        app.publish_jpeg(frame);
                    }
                }
            }
            Err(_) => break,
        }
    }
    finish_if_current(&app, gen);
}

fn audio_reader(app: App, mut stdout: std::process::ChildStdout, gen: u64, frame_bytes: usize, sample_rate: u32) {
    let chunk_bytes = audio_chunk_bytes(sample_rate, frame_bytes);
    let mut buf = Vec::new();
    let mut chunk = [0u8; 16 * 1024];
    loop {
        if app.inner.playback.lock().map(|p| p.generation != gen).unwrap_or(true) {
            break;
        }
        match stdout.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                buf.extend_from_slice(&chunk[..n]);
                while buf.len() >= chunk_bytes {
                    let pcm = bytes::Bytes::copy_from_slice(&buf[..chunk_bytes]);
                    buf.drain(..chunk_bytes);
                    if app.inner.pcm.media_mode.load(Ordering::Relaxed) {
                        app.inner.pcm.push(pcm, true);
                    }
                }
            }
            Err(_) => break,
        }
    }
    // Flush the tail as whole frames once ffmpeg's output ends.
    let whole = buf.len() / frame_bytes.max(1) * frame_bytes.max(1);
    let current = app.inner.playback.lock().map(|p| p.generation == gen).unwrap_or(false);
    if whole > 0 && current && app.inner.pcm.media_mode.load(Ordering::Relaxed) {
        app.inner.pcm.push(bytes::Bytes::copy_from_slice(&buf[..whole]), true);
    }
}

fn finish_if_current(app: &App, gen: u64) {
    let mut pb = match app.inner.playback.lock() {
        Ok(pb) => pb,
        Err(_) => return,
    };
    if pb.generation != gen || !pb.playing {
        return;
    }
    if let (Some(file), dur) = (pb.file.clone(), pb.duration) {
        if dur > 0.0 {
            drop(pb);
            if let Ok(guard) = app.inner.library.lock() {
                if let Some(lib) = guard.as_ref() {
                    lib.mark_watched(&file, dur);
                }
            }
            pb = match app.inner.playback.lock() {
                Ok(pb) => pb,
                Err(_) => return,
            };
        }
    }
    if pb.generation != gen {
        return;
    }
    pb.playing = false;
    pb.paused = false;
    app.inner.pcm.media_mode.store(false, Ordering::Relaxed);
    app.inner.media_mode.store(false, Ordering::Relaxed);
    println!("[Media] Playback ended; live screen restored.");
}

fn kill_locked(pb: &mut Playback) {
    pb.generation = pb.generation.wrapping_add(1);
    if let Some(mut child) = pb.video.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
    if let Some(mut child) = pb.audio.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
}

fn current_locked(pb: &Playback) -> f64 {
    let extra = if pb.paused {
        0.0
    } else {
        pb.started.map(|t| t.elapsed().as_secs_f64()).unwrap_or(0.0)
    };
    let pos = pb.position + extra;
    if pb.duration > 0.0 {
        pos.clamp(0.0, pb.duration)
    } else {
        pos.max(0.0)
    }
}

fn probe_duration(app: &App, file: &str) -> f64 {
    let probe = app.inner.playback.lock().ok().and_then(|p| p.ffprobe.clone());
    let Some(probe) = probe else {
        return 0.0;
    };
    let output = Command::new(probe)
        .args(["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .output();
    let Ok(output) = output else {
        return 0.0;
    };
    String::from_utf8_lossy(&output.stdout).trim().parse().unwrap_or(0.0)
}

fn drain_stderr(stderr: Option<std::process::ChildStderr>) {
    let Some(stderr) = stderr else { return };
    std::thread::spawn(move || {
        let reader = BufReader::new(stderr);
        for line in reader.lines().map_while(Result::ok) {
            if !line.is_empty() {
                println!("[Media/ffmpeg] {line}");
            }
        }
    });
}

fn html_escape(text: &str) -> String {
    crate::decisions::listing::html_escape(text)
}
