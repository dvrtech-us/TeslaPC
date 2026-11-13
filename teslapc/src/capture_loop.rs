//! Shared capture loop. A black or failed frame tries the next backend.
//! Clients stay connected across a cap change (the epoch restarts the session only).
//! In `--session-host` mode the capture thread also follows the input desktop.

use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use crate::decisions::capture::{backend_after_frame, first_capture_backend, no_frame_action, retry_after_switch, BackendStep, NoFrameAction, DXGI_SWITCH_RETRIES, DXGI_SWITCH_RETRY_MS};
use crate::decisions::desktop::capture_restart_delay_ms;
use crate::decisions::stream::{fit_frame, FRAME_INTERVAL_MS};
use crate::encode::encode_bgra_jpeg;
use crate::platform::{self, DesktopCheck, DesktopFollower, GrabOutcome};
use crate::state::App;

/// Set by `--session-host`: start at DXGI (as if `TESLAPC_NO_WGC=1`) and follow the input
/// desktop, so the lock screen, sign-in screen and UAC prompts are captured.
static SESSION_HOST: AtomicBool = AtomicBool::new(false);

/// Session-host capture for this process. Called before `ensure_started`.
pub fn session_host_mode() {
    SESSION_HOST.store(true, Ordering::Relaxed);
}

/// Carried from a capture thread that could not change desktop (`ERROR_BUSY`) to the fresh
/// thread that replaces it.
#[derive(Default)]
struct Restart {
    /// Consecutive busy restarts; sets the wait before the new thread starts.
    busy_restarts: u32,
    /// Desktop handles the old thread could not close while attached to them.
    leftovers: Vec<usize>,
}

pub fn ensure_started(app: &App) {
    static STARTED: std::sync::Once = std::sync::Once::new();
    let app = app.clone();
    STARTED.call_once(|| spawn_capture(app, Restart::default()));
}

/// Start one `ScreenCapture` thread. A thread only ends to hand over to its replacement: it
/// spawns the next thread as its last action, so exactly one capture loop runs at a time.
/// `ensure_started`'s once-guard is not involved in a restart.
fn spawn_capture(app: App, restart: Restart) {
    let spawned = thread::Builder::new().name("ScreenCapture".into()).spawn(move || {
        if let Some(next) = loop_forever(app.clone(), restart) {
            spawn_capture(app, next);
        }
    });
    if let Err(err) = spawned {
        println!("[Capture] Could not start the capture thread: {err}");
    }
}

/// Runs until the process ends, except in session-host mode when `SetThreadDesktop` returns
/// `ERROR_BUSY`: then it returns what the replacement thread needs.
fn loop_forever(app: App, restart: Restart) -> Option<Restart> {
    let env_no_wgc = std::env::var("TESLAPC_NO_WGC").ok().as_deref() == Some("1");
    let session_host = SESSION_HOST.load(Ordering::Relaxed);
    let no_wgc = env_no_wgc || session_host;
    let restarting = restart.busy_restarts > 0;
    if session_host && !restarting {
        println!("[Capture] Session host: WGC skipped, starting at DXGI.");
    } else if env_no_wgc && !restarting {
        println!("[Capture] WGC disabled via TESLAPC_NO_WGC=1.");
    }
    if restarting {
        thread::sleep(Duration::from_millis(capture_restart_delay_ms(restart.busy_restarts)));
    }
    // Session host only: this thread follows the input desktop. A normal run never switches.
    let mut desktop = session_host.then(|| DesktopFollower::new("ScreenCapture"));
    if let Some(follower) = desktop.as_mut() {
        follower.adopt(restart.leftovers);
    }
    let mut busy_restarts = restart.busy_restarts;
    // Check at once on the first streaming tick, and again right after a failed grab.
    let mut force_check = true;
    // Since the last desktop switch the backend has produced no frame (DXGI retry window).
    let mut after_switch = false;
    let mut switch_retries = 0u32;
    let mut backend = first_capture_backend(no_wgc);
    // Per-selection state: reset whenever the backend is (re)selected.
    let mut selected_at = Instant::now();
    let mut produced = false;
    // Last encoded JPEG, re-published while the screen is still.
    let mut last_jpeg: Option<Vec<u8>> = None;
    let mut last_publish = Instant::now();
    loop {
        if app.inner.stream_clients.load(Ordering::Relaxed) == 0 || app.inner.media_mode.load(Ordering::Relaxed) {
            // Idle time does not count against a backend that has not produced a frame yet.
            selected_at = Instant::now();
            thread::sleep(Duration::from_millis(50));
            continue;
        }
        if let Some(follower) = desktop.as_mut() {
            let check = follower.check(force_check, platform::drop_capture_sessions);
            force_check = false;
            match check {
                DesktopCheck::Stay | DesktopCheck::Failed => {}
                DesktopCheck::Switched(name) => {
                    // Same reset as a capture_epoch change. The old desktop's picture is not
                    // re-published.
                    println!("[Capture] Desktop is now {name}; restarting capture at {:?}.", first_capture_backend(no_wgc));
                    backend = first_capture_backend(no_wgc);
                    selected_at = Instant::now();
                    produced = false;
                    last_jpeg = None;
                    after_switch = true;
                    switch_retries = 0;
                    busy_restarts = 0;
                }
                DesktopCheck::Busy => {
                    println!("[Capture] This thread cannot change desktop (busy); restarting the capture thread.");
                    let _ = std::io::Write::flush(&mut std::io::stdout());
                    let leftovers = desktop.take().map(DesktopFollower::release_for_restart).unwrap_or_default();
                    return Some(Restart { busy_restarts: busy_restarts.saturating_add(1), leftovers });
                }
            }
        }
        let epoch = app.inner.capture_epoch.load(Ordering::Relaxed);
        let height = app.inner.stream_height.load(Ordering::Relaxed);
        match platform::grab(backend) {
            Ok(GrabOutcome::Frame(frame)) => match backend_after_frame(backend, true, &frame.bgra) {
                BackendStep::Switch(next) => {
                    println!("[Capture] {backend:?} frame is black; trying {next:?}.");
                    let _ = std::io::Write::flush(&mut std::io::stdout());
                    backend = next;
                    selected_at = Instant::now();
                    produced = false;
                    continue;
                }
                BackendStep::Keep(_) => {
                    produced = true;
                    after_switch = false;
                    app.log("debug", &format!("[Capture] {backend:?} frame {}x{}", frame.width, frame.height));
                    let (out_w, out_h) = fit_frame(frame.width, frame.height, height);
                    match encode_bgra_jpeg(&frame.bgra, frame.width, frame.height, out_w, out_h) {
                        Ok(jpeg) if jpeg.len() >= 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 => {
                            last_jpeg = Some(jpeg.clone());
                            last_publish = Instant::now();
                            app.publish_jpeg(jpeg);
                        }
                        Ok(_) => println!("[Capture] encoder returned a non-JPEG buffer"),
                        Err(err) => println!("[Capture] JPEG encode failed: {err}"),
                    }
                }
            },
            Ok(GrabOutcome::NoFrame) => {
                match no_frame_action(produced, last_jpeg.is_some(), selected_at.elapsed().as_millis() as u64, last_publish.elapsed().as_millis() as u64) {
                    NoFrameAction::Wait => {}
                    NoFrameAction::Republish => {
                        if let Some(jpeg) = &last_jpeg {
                            last_publish = Instant::now();
                            app.publish_jpeg(jpeg.clone());
                        }
                    }
                    NoFrameAction::Fail => {
                        force_check = true;
                        println!("[Capture] {backend:?} produced no frame after selection.");
                        let _ = std::io::Write::flush(&mut std::io::stdout());
                        match backend_after_frame(backend, false, &[]) {
                            BackendStep::Switch(next) => {
                                println!("[Capture] switching to {next:?}");
                                backend = next;
                                selected_at = Instant::now();
                                produced = false;
                                continue;
                            }
                            BackendStep::Keep(_) => selected_at = Instant::now(),
                        }
                    }
                }
            }
            Err(err) => {
                // A grab fails when the input desktop changed under it: check at once.
                force_check = true;
                if retry_after_switch(backend, after_switch, switch_retries) {
                    switch_retries += 1;
                    println!("[Capture] {backend:?} failed right after a desktop switch ({err}); retry {switch_retries} of {DXGI_SWITCH_RETRIES}.");
                    let _ = std::io::Write::flush(&mut std::io::stdout());
                    thread::sleep(Duration::from_millis(DXGI_SWITCH_RETRY_MS));
                    continue;
                }
                println!("[Capture] {backend:?} failed: {err}");
                let _ = std::io::Write::flush(&mut std::io::stdout());
                match backend_after_frame(backend, false, &[]) {
                    BackendStep::Switch(next) => {
                        println!("[Capture] switching to {next:?}");
                        backend = next;
                        selected_at = Instant::now();
                        produced = false;
                        continue;
                    }
                    BackendStep::Keep(_) => thread::sleep(Duration::from_millis(500)),
                }
            }
        }
        if app.inner.capture_epoch.load(Ordering::Relaxed) != epoch {
            println!("[Capture] restarting capture session");
            backend = first_capture_backend(no_wgc);
            selected_at = Instant::now();
            produced = false;
            last_jpeg = None;
            after_switch = false;
            continue;
        }
        thread::sleep(Duration::from_millis(FRAME_INTERVAL_MS));
    }
}
