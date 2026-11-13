//! Shared capture loop. A black or failed frame tries the next backend.
//! Clients stay connected across a cap change (the epoch restarts the session only).

use std::sync::atomic::Ordering;
use std::thread;
use std::time::{Duration, Instant};

use crate::decisions::capture::{backend_after_frame, first_capture_backend, no_frame_action, BackendStep, NoFrameAction};
use crate::decisions::stream::{fit_frame, FRAME_INTERVAL_MS};
use crate::encode::encode_bgra_jpeg;
use crate::platform::{self, GrabOutcome};
use crate::state::App;

pub fn ensure_started(app: &App) {
    static STARTED: std::sync::Once = std::sync::Once::new();
    let app = app.clone();
    STARTED.call_once(|| {
        thread::Builder::new()
            .name("ScreenCapture".into())
            .spawn(move || loop_forever(app))
            .ok();
    });
}

fn loop_forever(app: App) {
    let no_wgc = std::env::var("TESLAPC_NO_WGC").ok().as_deref() == Some("1");
    if no_wgc {
        println!("[Capture] WGC disabled via TESLAPC_NO_WGC=1.");
    }
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
            continue;
        }
        thread::sleep(Duration::from_millis(FRAME_INTERVAL_MS));
    }
}
