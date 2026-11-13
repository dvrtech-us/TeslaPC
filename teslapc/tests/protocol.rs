//! Calls the shipped decision functions. Expected values are written out here;
//! the test does not reimplement the functions it checks.

use std::collections::HashMap;
use std::path::Path;

use teslapc::assets;
use teslapc::decisions::audio::{cover_silence, format_message, is_silent, keepalive_bytes, keepalive_noise, loopback_backoff_ms, should_inject_silence, KeepaliveInput, KEEPALIVE_NOISE_AMPLITUDE, KEEPALIVE_POLL_MS, LOOPBACK_BACKOFF_MAX_MS, LOOPBACK_BACKOFF_START_MS, LOOPBACK_HEALTHY_RUN_MS, SILENCE_MAX_MS, SILENCE_START_MS};
use teslapc::decisions::bypass::linux_bypass_plan;
use teslapc::decisions::capture::{backend_after_frame, first_capture_backend, no_frame_action, retry_after_switch, BackendStep, CaptureBackend, NoFrameAction, DXGI_SWITCH_RETRIES, DXGI_SWITCH_RETRY_MS, NO_FRAME_FAIL_MS, REPUBLISH_INTERVAL_MS};
use teslapc::decisions::config::{apply_config_body, public_config_json, PublicConfig, KEY_CF_TOKEN};
use teslapc::decisions::desktop::{
    capture_restart_delay_ms, desktop_check_due, desktop_error_is_new, desktop_switch_needed, is_default_desktop, CAPTURE_RESTART_MAX_MS, CAPTURE_RESTART_START_MS, DEFAULT_DESKTOP_NAME, DESKTOP_CHECK_INTERVAL_MS, DESKTOP_RETRY_AFTER_FAILURE_MS,
};
use teslapc::decisions::display::{best_for_aspect, Mode};
use teslapc::decisions::history::{watched_now, WATCHED_FRACTION};
use teslapc::decisions::input::{expand_paste, map_key, pointer_path, pointer_path_on_desktop, scale_pointer, touch_event_applies, touch_repeat_applies, PointerPath, wheel_delta, TOUCH_REPEAT_MS};
use teslapc::decisions::listing::{render_guts, resolve_browse, FileProgress};
use teslapc::decisions::media::{ffmpeg_video_args, linux_missing_ffmpeg_error, windows_vlc_plan};
use teslapc::decisions::renewal::{acme_directory_url, needs_renewal, staging_from_env, RENEWAL_CHECK_HOURS, RENEW_WHEN_DAYS_LEFT};
use teslapc::decisions::service::{
    host_backoff_ms, host_command_line, log_should_rotate, next_failure_count, parse_service_command, parse_supervisor_pid, restart_delay_ms, service_launch_arguments, supervisor_step, tasks_running_teslapc, ServiceCommand, SupervisorInput, SupervisorStep, EXIT_LAUNCH_FAILED, EXIT_PORT_IN_USE, HOST_BACKOFF_MAX_MS, HOST_BACKOFF_START_MS, HOST_HEALTHY_UPTIME_MS, LOG_ROTATE_BYTES, NO_CONSOLE_SESSION,
    PORT_IN_USE_WAIT_MS, SUPERVISOR_POLL_MS,
};
use teslapc::decisions::socket::{classify_socket, SocketRole};
use teslapc::decisions::stream::{clamp_stream_height, fit_frame, mjpeg_content_type, mjpeg_part, JPEG_QUALITY, SLOW_CLIENT_WAIT_MS};

const TOKEN: &str = "cf-token-SHOULD-NOT-LEAK-9f3a";

#[test]
fn pointer_scales_from_display_size_and_passes_through_when_missing() {
    let screen = (1920, 1080);
    let scaled = scale_pointer(820, 45, -120, Some((1280, 720)), screen);
    assert_eq!(scaled.x, 1230);
    assert_eq!(scaled.y, 67);
    assert_eq!(scaled.delta, -120);

    let passed = scale_pointer(820, 45, -120, None, screen);
    assert_eq!((passed.x, passed.y, passed.delta), (820, 45, -120));
}

#[test]
fn wheel_sign_is_inverted() {
    assert_eq!(wheel_delta(120), -120);
    assert_eq!(wheel_delta(-40), 40);
    assert_eq!(wheel_delta(0), 0);
}

#[test]
fn key_and_paste_mapping_matches_sendkeys() {
    assert_eq!(map_key("A").as_deref(), Some("A"));
    assert_eq!(map_key("+").as_deref(), Some("{+}"));
    assert_eq!(map_key("^").as_deref(), Some("{^}"));
    assert_eq!(map_key("%").as_deref(), Some("{%}"));
    assert_eq!(map_key("~").as_deref(), Some("{~}"));
    assert_eq!(map_key("(").as_deref(), Some("{(}"));
    assert_eq!(map_key(")").as_deref(), Some("{)}"));
    assert_eq!(map_key("{").as_deref(), Some("{{}"));
    assert_eq!(map_key("}").as_deref(), Some("{}}"));
    assert_eq!(map_key("[").as_deref(), Some("{[}"));
    assert_eq!(map_key("]").as_deref(), Some("{]}"));
    assert_eq!(map_key("ArrowLeft").as_deref(), Some("{LEFT}"));
    assert_eq!(map_key("Enter").as_deref(), Some("{ENTER}"));
    assert_eq!(map_key("F12").as_deref(), Some("{F12}"));
    assert_eq!(map_key("Shift"), None);
    assert_eq!(map_key("CapsLock"), None);
    assert_eq!(map_key("ArrowLeftExtra"), None);
    assert_eq!(map_key(""), None);

    assert_eq!(
        expand_paste("Hi+\r\n\t"),
        vec![
            "H".to_string(),
            "i".to_string(),
            "{+}".to_string(),
            "{ENTER}".to_string(),
            "{TAB}".to_string(),
        ]
    );
}

#[test]
fn touch_ignores_hover_and_repeats_every_100ms() {
    assert!(!touch_event_applies("move", false));
    assert!(!touch_event_applies("up", false));
    assert!(touch_event_applies("down", false));
    assert!(touch_event_applies("move", true));
    assert!(!touch_event_applies("down", true));
    assert_eq!(TOUCH_REPEAT_MS, 100);
}

#[test]
fn stream_height_clamp_and_uniform_scale_never_upscales() {
    assert_eq!(clamp_stream_height(1080), 1080);
    assert_eq!(clamp_stream_height(240), 240);
    assert_eq!(clamp_stream_height(2160), 2160);
    assert_eq!(clamp_stream_height(100), 1080);
    assert_eq!(clamp_stream_height(2161), 1080);

    assert_eq!(fit_frame(1920, 1080, 1080), (1920, 1080));
    assert_eq!(fit_frame(1920, 1080, 720), (1280, 720));
    assert_eq!(fit_frame(1920, 1200, 1080), (1728, 1080));
    assert_eq!(fit_frame(800, 600, 1080), (800, 600));
    assert_eq!(JPEG_QUALITY, 60);
    assert_eq!(SLOW_CLIENT_WAIT_MS, 1000);
}

#[test]
fn mjpeg_part_headers() {
    assert_eq!(mjpeg_content_type(), "multipart/x-mixed-replace; boundary=boundary");
    let jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
    let part = mjpeg_part(&jpeg);
    let text = String::from_utf8_lossy(&part);
    assert!(text.starts_with("\r\n--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: 4\r\n\r\n"));
    assert!(part.ends_with(b"\r\n"));
    assert!(part.windows(4).any(|w| w == jpeg));
}

#[test]
fn audio_format_and_silence_keepalive() {
    assert_eq!(SILENCE_START_MS, 250);
    assert_eq!(SILENCE_MAX_MS, 10_000);
    assert_eq!(KEEPALIVE_POLL_MS, 20);

    let json = format_message(48000, 32, 2, "float");
    assert!(json.contains("\"type\":\"format\""));
    assert!(json.contains("\"sampleRate\":48000"));
    assert!(json.contains("\"bitsPerSample\":32"));
    assert!(json.contains("\"channels\":2"));
    assert!(json.contains("\"sampleFormat\":\"float\""));

    let base = KeepaliveInput {
        idle_ms: 250,
        silent_ms: 250,
        queue_empty: true,
        capturing: true,
        media_mode: false,
        has_clients: true,
        typical_buffer_bytes: 3840,
    };
    assert!(should_inject_silence(base));
    assert!(!should_inject_silence(KeepaliveInput { idle_ms: 249, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { idle_ms: 10_000, silent_ms: 10_000, ..base }));
    assert!(should_inject_silence(KeepaliveInput { idle_ms: 9_999, silent_ms: 9_999, ..base }));
    // Silent packets kept arriving until now, so the packet gap is short but the silence is long.
    assert!(!should_inject_silence(KeepaliveInput { idle_ms: 300, silent_ms: 10_000, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { media_mode: true, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { queue_empty: false, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { has_clients: false, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { typical_buffer_bytes: 0, ..base }));
    assert!(!should_inject_silence(KeepaliveInput { capturing: false, ..base }));
}

#[test]
fn keepalive_noise_is_quiet_nonzero_and_stops_after_ten_seconds() {
    assert!(cover_silence(0));
    assert!(cover_silence(9_999));
    assert!(!cover_silence(10_000));

    assert!(is_silent(&[0u8; 3840]));
    let mut one = [0u8; 3840];
    one[1234] = 1;
    assert!(!is_silent(&one));

    let mut seed = 0u32;
    let float = keepalive_noise(3840, 32, true, &mut seed);
    assert_eq!(float.len(), 3840);
    assert!(!is_silent(&float));
    let peak = float.chunks_exact(4).map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]]).abs()).fold(0.0f32, f32::max);
    assert!(peak > 0.0 && peak <= KEEPALIVE_NOISE_AMPLITUDE);
    // The generator state advances, so the next buffer differs.
    assert_ne!(keepalive_noise(3840, 32, true, &mut seed), float);

    let pcm16 = keepalive_noise(1920, 16, false, &mut seed);
    assert!(!is_silent(&pcm16));
    let peak16 = pcm16.chunks_exact(2).map(|b| i16::from_le_bytes([b[0], b[1]]).unsigned_abs()).max().unwrap();
    assert!(peak16 >= 1 && peak16 <= 8);

    let pcm24 = keepalive_noise(2880, 24, false, &mut seed);
    assert!(!is_silent(&pcm24));
    let pcm32 = keepalive_noise(3840, 32, false, &mut seed);
    let peak32 = pcm32.chunks_exact(4).map(|b| i32::from_le_bytes([b[0], b[1], b[2], b[3]]).unsigned_abs()).max().unwrap();
    assert!(peak32 >= 1 && peak32 <= (1u32 << 31) / 4096);

    // Keepalive buffers cover the elapsed time in whole frames, capped at 100 ms.
    assert_eq!(keepalive_bytes(20, 48000, 32, 2), 960 * 8);
    assert_eq!(keepalive_bytes(31, 48000, 32, 2), 1488 * 8);
    assert_eq!(keepalive_bytes(31, 44100, 16, 2), 1367 * 4);
    assert_eq!(keepalive_bytes(5_000, 48000, 32, 2), 4800 * 8);
    assert_eq!(keepalive_bytes(0, 48000, 32, 2), 0);

    // An unknown width stays silent, and a partial trailing sample is left zero.
    assert!(is_silent(&keepalive_noise(100, 8, false, &mut seed)));
    let odd = keepalive_noise(6, 32, true, &mut seed);
    assert_eq!(&odd[4..], &[0, 0]);
}

#[test]
fn config_keeps_unknown_lines_and_blank_token_and_omits_token_from_get() {
    let existing = format!(
        "# keep me\r\nFOO=bar\r\n{KEY_CF_TOKEN}={TOKEN}\r\nTESLAPC_HTTPS_HOST=old.example\r\nOTHER=1\r\n"
    );
    let saved = apply_config_body(
        &existing,
        "httpsHost=new.example&acmeEmail=a%40b.co&videoRoot=D%3A%5Cmovies&logLevel=Debug&streamHeight=720&inputMode=Mouse&cfToken=",
    );
    assert!(saved.text.contains("# keep me"));
    assert!(saved.text.contains("FOO=bar"));
    assert!(saved.text.contains("OTHER=1"));
    assert!(saved.text.contains("TESLAPC_HTTPS_HOST=new.example"));
    assert!(saved.text.contains("TESLAPC_ACME_EMAIL=a@b.co"));
    assert!(saved.text.contains(r"TESLAPC_VIDEO_ROOT=D:\movies"));
    assert!(saved.text.contains("TESLAPC_LOG_LEVEL=debug"));
    assert!(saved.text.contains("TESLAPC_STREAM_HEIGHT=720"));
    assert!(saved.text.contains("TESLAPC_INPUT_MODE=mouse"));
    assert!(saved.text.contains(&format!("{KEY_CF_TOKEN}={TOKEN}")));
    assert!(saved.restart_needed);

    let blank_only = apply_config_body(&saved.text, "logLevel=info");
    assert!(!blank_only.restart_needed);
    assert!(blank_only.text.contains("TESLAPC_LOG_LEVEL=info"));
    assert!(blank_only.text.contains("FOO=bar"));
    assert!(blank_only.text.contains(&format!("{KEY_CF_TOKEN}={TOKEN}")));

    let public = public_config_json(&PublicConfig {
        https_host: "new.example".into(),
        acme_email: "a@b.co".into(),
        video_root: r"D:\movies".into(),
        log_level: "info".into(),
        stream_height: 720,
        input_mode: "mouse".into(),
        version: teslapc::crate_version().into(),
        cf_token_set: true,
    });
    assert!(!public.contains(TOKEN));
    assert!(!public.contains("TESLAPC_CF_TOKEN"));
    for field in ["httpsHost", "acmeEmail", "videoRoot", "logLevel", "streamHeight", "inputMode", "version", "cfTokenSet"] {
        assert!(public.contains(field), "{field} missing from {public}");
    }
    assert_eq!(teslapc::crate_version(), env!("CARGO_PKG_VERSION"));
}

#[test]
fn watched_is_sticky_at_95_percent() {
    assert!((WATCHED_FRACTION - 0.95).abs() < f64::EPSILON);
    assert!(!watched_now(94.0, 100.0, false));
    assert!(watched_now(95.0, 100.0, false));
    assert!(watched_now(10.0, 100.0, true));
    assert!(!watched_now(100.0, 0.0, false));
}

#[test]
fn display_mode_picks_nearest_aspect_inside_the_cap() {
    let modes = [
        Mode { w: 1920, h: 1080 },
        Mode { w: 1680, h: 1050 },
        Mode { w: 1280, h: 720 },
        Mode { w: 1024, h: 768 },
        Mode { w: 800, h: 600 },
        Mode { w: 640, h: 480 },
    ];
    assert_eq!(best_for_aspect(&modes, 16.0 / 9.0, 1080), Some(Mode { w: 1920, h: 1080 }));
    assert_eq!(best_for_aspect(&modes, 16.0 / 9.0, 720), Some(Mode { w: 1280, h: 720 }));
    assert_eq!(best_for_aspect(&modes, 4.0 / 3.0, 1080), Some(Mode { w: 1024, h: 768 }));
    assert_eq!(best_for_aspect(&[], 1.7, 1080), None);
    assert_eq!(best_for_aspect(&modes, 0.0, 1080), None);

    // 1920x960 is closer in area but 0.04 off 1.96. 1600x820 is within the
    // 0.01 gap past that error, so the closer aspect wins.
    let near = [Mode { w: 1920, h: 960 }, Mode { w: 1600, h: 820 }];
    assert_eq!(best_for_aspect(&near, 1.96, 1080), Some(Mode { w: 1600, h: 820 }));
}

#[test]
fn websocket_paths() {
    assert_eq!(classify_socket("/ws/audio"), SocketRole::Audio);
    assert_eq!(classify_socket("/WS/Audio"), SocketRole::Audio);
    assert_eq!(classify_socket("/ws/audio/extra"), SocketRole::Audio);
    assert_eq!(classify_socket("/ws/input"), SocketRole::Input);
    assert_eq!(classify_socket("/anything"), SocketRole::Input);
}

#[test]
fn renewal_under_30_days_and_staging_directory() {
    assert_eq!(RENEW_WHEN_DAYS_LEFT, 30);
    assert_eq!(RENEWAL_CHECK_HOURS, 12);
    assert!(!needs_renewal(31));
    assert!(needs_renewal(30));
    assert!(needs_renewal(29));
    assert!(needs_renewal(0));
    assert!(needs_renewal(-1));
    assert!(staging_from_env(Some("1")));
    assert!(!staging_from_env(Some("0")));
    assert!(acme_directory_url(true).contains("staging"));
    assert!(!acme_directory_url(false).contains("staging"));
}

#[test]
fn bypass_gating_and_linux_forward_plus_nat() {
    let off_flag = linux_bypass_plan(true, Some("wlan0"), 8080, 8443);
    assert!(!off_flag.enabled);
    assert!(off_flag.nft_script.is_empty());

    let off_empty = linux_bypass_plan(false, Some("  "), 8080, 8443);
    assert!(!off_empty.enabled);
    let off_missing = linux_bypass_plan(false, None, 8080, 8443);
    assert!(!off_missing.enabled);

    let on = linux_bypass_plan(false, Some("wlan0"), 8080, 8443);
    assert!(on.enabled);
    assert_eq!(on.address_cidr, "100.64.0.1/10");
    assert!(on.nft_script.contains("8080"));
    assert!(on.nft_script.contains("8443"));
    assert!(on.nft_script.contains("dnat to 127.0.0.1:8080"));
    assert!(on.nft_script.contains("dnat to 127.0.0.1:8443"));
    assert!(on.nft_script.contains("masquerade"));
    assert!(on.nft_script.contains("chain forward"));
}

#[test]
fn vlc_plan_and_linux_missing_ffmpeg_and_video_filter() {
    let plan = windows_vlc_plan(r"D:\video\a.mp4");
    assert_eq!(plan.image_name, "vlc.exe");
    assert_eq!(plan.wait_ms, 2000);
    assert_eq!(plan.executable, r"C:\Program Files\VideoLAN\VLC\vlc.exe");
    assert_eq!(plan.arguments, " -vvv \"D:\\video\\a.mp4\" --fullscreen");

    let err = linux_missing_ffmpeg_error();
    assert!(err.contains("ffmpeg"));
    assert!(err.to_ascii_lowercase().contains("does not launch"));

    let args = ffmpeg_video_args(r"D:\video\a.mp4", 12.5, 720);
    assert!(args.contains("scale=-2:'min(720,ih)',fps=30"));
    assert!(args.contains("-c:v mjpeg -q:v 6"));
}

#[test]
fn black_frame_tries_the_next_backend() {
    assert_eq!(first_capture_backend(false), CaptureBackend::Wgc);
    assert_eq!(first_capture_backend(true), CaptureBackend::Dxgi);
    let black = vec![0u8; 16];
    let color = {
        let mut px = vec![0u8; 16];
        px[2] = 255;
        px
    };
    assert_eq!(
        backend_after_frame(CaptureBackend::Wgc, true, &black),
        BackendStep::Switch(CaptureBackend::Dxgi)
    );
    assert_eq!(
        backend_after_frame(CaptureBackend::Dxgi, false, &[]),
        BackendStep::Switch(CaptureBackend::Gdi)
    );
    assert_eq!(
        backend_after_frame(CaptureBackend::Gdi, true, &black),
        BackendStep::Keep(CaptureBackend::Gdi)
    );
    assert_eq!(
        backend_after_frame(CaptureBackend::Wgc, true, &color),
        BackendStep::Keep(CaptureBackend::Wgc)
    );
}

#[test]
fn still_screen_keeps_the_backend_and_republishes_the_last_jpeg() {
    assert_eq!(REPUBLISH_INTERVAL_MS, 1000);
    assert_eq!(NO_FRAME_FAIL_MS, 2000);
    // A backend that has produced a frame never fails on "no new frame", however long it is still.
    assert_eq!(no_frame_action(true, true, 600_000, 0), NoFrameAction::Wait);
    assert_eq!(no_frame_action(true, true, 600_000, 999), NoFrameAction::Wait);
    assert_eq!(no_frame_action(true, true, 600_000, 1000), NoFrameAction::Republish);
    // No stored JPEG (the encode failed) means nothing to repeat.
    assert_eq!(no_frame_action(true, false, 600_000, 5000), NoFrameAction::Wait);
}

#[test]
fn backend_that_never_produced_a_frame_fails_after_two_seconds() {
    assert_eq!(no_frame_action(false, false, 0, 0), NoFrameAction::Wait);
    assert_eq!(no_frame_action(false, false, 1999, 5000), NoFrameAction::Wait);
    assert_eq!(no_frame_action(false, false, 2000, 0), NoFrameAction::Fail);
    // A JPEG left over from a previous backend is not repeated for one that has produced nothing.
    assert_eq!(no_frame_action(false, true, 1500, 5000), NoFrameAction::Wait);
    assert_eq!(no_frame_action(false, true, 2500, 5000), NoFrameAction::Fail);
}

#[test]
fn browse_paths_stay_under_the_root_and_folders_render_first() {
    let root = Path::new(r"C:\video");
    assert!(resolve_browse(root, None).is_ok());
    assert!(resolve_browse(root, Some(r"C:\video\movies")).is_ok());
    assert!(resolve_browse(root, Some(r"C:\video\..\Windows")).is_err());

    let mut progress = HashMap::new();
    progress.insert(
        r"c:\video\b.mp4".to_string(),
        FileProgress { position: 95.0, duration: 100.0, watched: true },
    );
    let html = render_guts(
        r"C:\video",
        r"C:\video",
        true,
        None,
        &[r"C:\video\Folder".to_string()],
        &[(r"C:\video\b.mp4".to_string(), "b.mp4".to_string())],
        &progress,
    );
    let folder_at = html.find("row folder").unwrap();
    let file_at = html.find("row file").unwrap();
    assert!(folder_at < file_at);
    assert!(html.contains("Watched"));

    let missing = render_guts(r"C:\video", r"C:\video\nope", false, None, &[], &[], &HashMap::new());
    assert!(missing.contains("doesn't exist"));
}

#[test]
fn png_asset_is_raw_bytes() {
    let asset = assets::asset("/teslapc.png").unwrap();
    assert_eq!(asset.content_type, "image/png");
    assert!(asset.bytes.starts_with(&[0x89, b'P', b'N', b'G']));
    assert!(!asset.html);
}

#[test]
fn linux_uinput_records_cover_touch_mouse_wheel_and_keys() {
    use teslapc::decisions::linux_input::{linux_abs_axes, linux_key_events, linux_pointer_events, linux_repeat_events, linux_wheel_events, ABS_MT_POSITION_X, ABS_MT_POSITION_Y, ABS_MT_SLOT, ABS_MT_TRACKING_ID, ABS_X, ABS_Y, BTN_LEFT, EV_ABS, EV_KEY, EV_REL, KEY_ENTER, KEY_LEFTCTRL, KEY_U, LINUX_WHEEL_NOTCH, REL_WHEEL};

    let screen = (1920, 1080);
    let axes = linux_abs_axes(screen.0, screen.1);
    let x_max = axes.iter().find(|axis| axis.code == ABS_X).unwrap().maximum;
    let y_max = axes.iter().find(|axis| axis.code == ABS_Y).unwrap().maximum;
    let mt_x_max = axes.iter().find(|axis| axis.code == ABS_MT_POSITION_X).unwrap().maximum;
    let mt_y_max = axes.iter().find(|axis| axis.code == ABS_MT_POSITION_Y).unwrap().maximum;
    assert_eq!(x_max, 1920);
    assert_eq!(y_max, 1080);
    assert_eq!(mt_x_max, 1920);
    assert_eq!(mt_y_max, 1080);

    let edge = linux_pointer_events("down", 1920, 1080, false, screen);
    assert_eq!(edge.iter().find(|event| event.type_ == EV_ABS && event.code == ABS_X).unwrap().value, x_max);
    assert_eq!(edge.iter().find(|event| event.type_ == EV_ABS && event.code == ABS_Y).unwrap().value, y_max);

    let touch = linux_pointer_events("down", 10, 20, true, screen);
    assert!(touch.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_MT_SLOT && event.value == 0));
    assert!(touch.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_MT_TRACKING_ID && event.value == 1));
    assert!(touch.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_MT_POSITION_X && event.value == 10));

    let mouse = linux_pointer_events("down", 3, 4, false, screen);
    assert!(mouse.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_X && event.value == 3));
    assert!(mouse.iter().any(|event| event.type_ == EV_KEY && event.code == BTN_LEFT && event.value == 1));

    let (notch, rest) = linux_wheel_events(-LINUX_WHEEL_NOTCH, 0);
    assert_eq!(rest, 0);
    assert!(notch.iter().any(|event| event.type_ == EV_REL && event.code == REL_WHEEL && event.value == -1));
    let (partial, rest) = linux_wheel_events(-40, 0);
    assert!(partial.iter().all(|event| event.code != REL_WHEEL));
    assert_eq!(rest, -40);
    let (filled, rest) = linux_wheel_events(-80, rest);
    assert_eq!(rest, 0);
    assert!(filled.iter().any(|event| event.type_ == EV_REL && event.code == REL_WHEEL && event.value == -1));

    let enter = linux_key_events("{ENTER}");
    assert!(enter.iter().any(|event| event.type_ == EV_KEY && event.code == KEY_ENTER && event.value == 1));
    assert!(enter.iter().any(|event| event.type_ == EV_KEY && event.code == KEY_ENTER && event.value == 0));

    let unicode = linux_key_events("{+}");
    assert!(unicode.iter().any(|event| event.type_ == EV_KEY && event.code == KEY_LEFTCTRL && event.value == 1));
    assert!(unicode.iter().any(|event| event.type_ == EV_KEY && event.code == KEY_U && event.value == 1));

    let repeat = linux_repeat_events(10, 20, screen);
    assert!(repeat.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_MT_SLOT && event.value == 0));
    assert!(repeat.iter().any(|event| event.type_ == EV_ABS && event.code == ABS_MT_POSITION_X && event.value == 10));
}

#[test]
fn hotspot_fresh_on_waits_2000_and_watchdog_repairs_a_missing_address() {
    use teslapc::decisions::bypass::{hotspot_step, HotspotReport, HotspotSource};

    let fresh = hotspot_step(HotspotSource::SettingsOn, false, HotspotReport::TurnedOn, false);
    assert_eq!(fresh.wait_ms, 2000);
    assert!(fresh.setup_bypass);

    let settings_missing = hotspot_step(HotspotSource::SettingsOn, false, HotspotReport::AlreadyOn, false);
    assert_eq!(settings_missing.wait_ms, 1500);
    assert!(settings_missing.setup_bypass);

    let settings_present = hotspot_step(HotspotSource::SettingsOn, false, HotspotReport::AlreadyOn, true);
    assert!(!settings_present.setup_bypass);

    let manual = hotspot_step(HotspotSource::Watchdog, true, HotspotReport::TurnedOn, false);
    assert!(!manual.setup_bypass);

    let watchdog_fresh = hotspot_step(HotspotSource::Watchdog, false, HotspotReport::TurnedOn, false);
    assert_eq!(watchdog_fresh.wait_ms, 2000);
    assert!(watchdog_fresh.setup_bypass);

    let watchdog_missing = hotspot_step(HotspotSource::Watchdog, false, HotspotReport::AlreadyOn, false);
    assert_eq!(watchdog_missing.wait_ms, 0);
    assert!(watchdog_missing.setup_bypass);

    let watchdog_present = hotspot_step(HotspotSource::Watchdog, false, HotspotReport::AlreadyOn, true);
    assert!(!watchdog_present.setup_bypass);
}

#[test]
fn linux_display_query_lists_modes_and_wayland_uses_wlr_randr() {
    use teslapc::decisions::display::{best_for_aspect, choose_display_query, display_set_args, parse_wlr_randr, parse_xrandr, Mode};

    let xrandr = "\
eDP-1 connected primary 1920x1080+0+0
   1920x1080     60.00*+
   1600x900      60.00
   1280x720      60.00
   1024x768      60.00
HDMI-1 disconnected
";
    let wlr = "\
eDP-1
  1920x1080 px, 60.000000 Hz (current)
  1280x720 px, 60.000000 Hz
  1024x768 px, 60.000000 Hz
";
    let parsed = parse_xrandr(xrandr).unwrap();
    assert_eq!(parsed.output, "eDP-1");
    assert_eq!(parsed.current, (1920, 1080));
    assert!(parsed.modes.len() >= 4);
    assert!(parsed.modes.contains(&(1280, 720)));
    assert!(parsed.modes.contains(&(1024, 768)));

    let wlr_parsed = parse_wlr_randr(wlr).unwrap();
    assert!(wlr_parsed.modes.len() >= 3);
    assert_eq!(choose_display_query(xrandr, wlr, true).unwrap().modes, wlr_parsed.modes);
    assert_eq!(choose_display_query(xrandr, "", false).unwrap().output, "eDP-1");

    let set = display_set_args(true, "eDP-1", 1280, 720);
    assert_eq!(set[0], "wlr-randr");
    assert_eq!(set, ["wlr-randr", "--output", "eDP-1", "--mode", "1280x720"]);
    assert_eq!(display_set_args(false, "eDP-1", 1920, 1080)[0], "xrandr");

    let modes: Vec<Mode> = parsed.modes.iter().map(|(w, h)| Mode { w: *w, h: *h }).collect();
    let picked = best_for_aspect(&modes, 1024.0 / 768.0, 1080).unwrap();
    assert_eq!((picked.w, picked.h), (1024, 768));
}

#[test]
fn portal_messages_round_trip_and_the_hand_built_response_parses() {
    use teslapc::decisions::portal::{create_session_message, parse_dbus_header, parse_portal_response, pipewire_hello, portal_response_body, PortalResponse};

    let message = create_session_message(7, "tok", "h1");
    assert_eq!(message[0], b'l');
    assert_eq!(message[1], 1);
    assert_eq!(u32::from_le_bytes(message[8..12].try_into().unwrap()), 7);
    let fields_len = u32::from_le_bytes(message[12..16].try_into().unwrap()) as usize;
    let header = parse_dbus_header(&message[16..16 + fields_len]);
    assert_eq!(header.member.as_deref(), Some("CreateSession"));
    assert_eq!(header.signature.as_deref(), Some("a{sv}"));
    assert!(message.windows(3).any(|window| window == b"tok"));

    let session = [
        0x00, 0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00, 0x0e, 0x00, 0x00, 0x00, b's', b'e', b's', b's', b'i', b'o', b'n', b'_', b'h', b'a', b'n', b'd', b'l', b'e', 0x00, 0x01, b'o', 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, b'/', b's', 0x00,
    ];
    assert_eq!(parse_portal_response(&session).unwrap(), PortalResponse::Session("/s".into()));

    let node = [
        0x00, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, b's', b't', b'r', b'e', b'a', b'm', b's', 0x00, 0x09, b'a', b'(', b'u', b'a', b'{', b's', b'v', b'}', b')', 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];
    assert_eq!(parse_portal_response(&node).unwrap(), PortalResponse::Node(7));
    assert_eq!(parse_portal_response(&portal_response_body(0, Some("/s"), None)).unwrap(), PortalResponse::Session("/s".into()));
    assert_eq!(parse_portal_response(&portal_response_body(2, None, None)).unwrap(), PortalResponse::Failed(2));
    assert_eq!(parse_portal_response(&portal_response_body(0, None, Some(42))).unwrap(), PortalResponse::Node(42));

    let hello = pipewire_hello(1);
    assert_eq!(hello.len(), 16);
    assert_eq!(&hello[..8], &[0, 0, 0, 0, 0, 0, 0, 0]);
    assert_eq!(u32::from_le_bytes(hello[8..12].try_into().unwrap()), 1);
}

#[test]
fn pointer_falls_back_to_mouse_when_touch_is_unavailable() {
    assert_eq!(pointer_path("down", true, true), PointerPath::Touch);
    assert_eq!(pointer_path("move", true, true), PointerPath::Touch);
    assert_eq!(pointer_path("up", true, true), PointerPath::Touch);
    assert_eq!(pointer_path("rightclick", true, true), PointerPath::Mouse);
    assert_eq!(pointer_path("down", true, false), PointerPath::Mouse);
    assert_eq!(pointer_path("up", true, false), PointerPath::Mouse);
    assert_eq!(pointer_path("down", false, true), PointerPath::Mouse);
    assert_eq!(pointer_path("down", false, false), PointerPath::Mouse);
}

#[test]
fn loopback_backoff_doubles_to_ten_seconds_and_resets_after_a_healthy_run() {
    let mut wait = 0;
    let mut seen = Vec::new();
    for _ in 0..6 {
        wait = loopback_backoff_ms(wait, 0);
        seen.push(wait);
    }
    assert_eq!(seen, vec![1_000, 2_000, 4_000, 8_000, 10_000, 10_000]);
    assert_eq!(loopback_backoff_ms(10_000, 30_001), 1_000);
    assert_eq!(loopback_backoff_ms(4_000, 30_000), 8_000);
    assert_eq!((LOOPBACK_BACKOFF_START_MS, LOOPBACK_BACKOFF_MAX_MS, LOOPBACK_HEALTHY_RUN_MS), (1_000, 10_000, 30_000));
}

fn supervisor(host_session: Option<u32>, console: u32) -> SupervisorInput {
    SupervisorInput {
        stopping: false,
        host_alive: host_session.is_some(),
        host_session,
        console_session: console,
        logoff_for_host_session: false,
        last_exit_code: None,
        uptime_ms: 0,
        since_exit_ms: 0,
        failures: 0,
    }
}

#[test]
fn supervisor_starts_keeps_and_follows_the_console_session() {
    // First launch: no host, nothing has exited yet.
    assert_eq!(supervisor_step(&supervisor(None, 1)), SupervisorStep::Start { session: 1 });
    // Host in the console session: keep.
    assert_eq!(supervisor_step(&supervisor(Some(1), 1)), SupervisorStep::Keep);
    // Console moved to another session: restart there.
    assert_eq!(supervisor_step(&supervisor(Some(1), 2)), SupervisorStep::Restart { session: 2 });
    // Logoff for the host's session with an unchanged id still restarts.
    let logoff = SupervisorInput { logoff_for_host_session: true, ..supervisor(Some(1), 1) };
    assert_eq!(supervisor_step(&logoff), SupervisorStep::Restart { session: 1 });
}

#[test]
fn supervisor_keeps_the_host_and_polls_when_no_console_session() {
    assert_eq!(NO_CONSOLE_SESSION, 0xFFFF_FFFF);
    assert_eq!(supervisor_step(&supervisor(Some(1), NO_CONSOLE_SESSION)), SupervisorStep::Keep);
    let logoff = SupervisorInput { logoff_for_host_session: true, ..supervisor(Some(1), NO_CONSOLE_SESSION) };
    assert_eq!(supervisor_step(&logoff), SupervisorStep::Keep);
    assert_eq!(supervisor_step(&supervisor(None, NO_CONSOLE_SESSION)), SupervisorStep::Wait { ms: SUPERVISOR_POLL_MS });
    assert_eq!(SUPERVISOR_POLL_MS, 2_000);
}

#[test]
fn supervisor_stop_wins_over_everything_including_backoff() {
    let alive = SupervisorInput { stopping: true, ..supervisor(Some(1), 2) };
    assert_eq!(supervisor_step(&alive), SupervisorStep::Stop);
    let backoff = SupervisorInput { stopping: true, last_exit_code: Some(1), failures: 3, since_exit_ms: 10, ..supervisor(None, 1) };
    assert_eq!(supervisor_step(&backoff), SupervisorStep::Stop);
    let first = SupervisorInput { stopping: true, ..supervisor(None, 1) };
    assert_eq!(supervisor_step(&first), SupervisorStep::Stop);
}

#[test]
fn supervisor_waits_out_the_backoff_then_restarts() {
    let exited = SupervisorInput { last_exit_code: Some(1), uptime_ms: 500, failures: 3, since_exit_ms: 1_500, ..supervisor(None, 1) };
    assert_eq!(supervisor_step(&exited), SupervisorStep::Wait { ms: 2_500 });
    let due = SupervisorInput { since_exit_ms: 4_000, ..exited };
    assert_eq!(supervisor_step(&due), SupervisorStep::Start { session: 1 });
    // Launch failures back off the same way.
    let failed = SupervisorInput { last_exit_code: Some(EXIT_LAUNCH_FAILED), failures: 1, since_exit_ms: 0, ..supervisor(None, 1) };
    assert_eq!(supervisor_step(&failed), SupervisorStep::Wait { ms: 1_000 });
    // Port in use waits 30 s regardless of the failure count.
    let port = SupervisorInput { last_exit_code: Some(EXIT_PORT_IN_USE as u32), failures: 1, since_exit_ms: 10_000, ..supervisor(None, 1) };
    assert_eq!(supervisor_step(&port), SupervisorStep::Wait { ms: 20_000 });
    let port_due = SupervisorInput { since_exit_ms: 30_000, ..port };
    assert_eq!(supervisor_step(&port_due), SupervisorStep::Start { session: 1 });
}

#[test]
fn host_backoff_doubles_to_thirty_seconds_and_resets_after_a_minute() {
    assert_eq!((HOST_BACKOFF_START_MS, HOST_BACKOFF_MAX_MS, HOST_HEALTHY_UPTIME_MS), (1_000, 30_000, 60_000));
    assert_eq!(host_backoff_ms(0), 0);
    let schedule: Vec<u64> = (1..=7).map(host_backoff_ms).collect();
    assert_eq!(schedule, vec![1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000]);
    assert_eq!(host_backoff_ms(u32::MAX), 30_000);

    assert_eq!(next_failure_count(0, 10), 1);
    assert_eq!(next_failure_count(4, 59_999), 5);
    assert_eq!(next_failure_count(4, 60_000), 1);
    assert_eq!(next_failure_count(u32::MAX, 0), u32::MAX);

    assert_eq!(EXIT_PORT_IN_USE, 3);
    assert_eq!(PORT_IN_USE_WAIT_MS, 30_000);
    assert_eq!(restart_delay_ms(3, 1), 30_000);
    assert_eq!(restart_delay_ms(1, 2), 2_000);
    assert_eq!(restart_delay_ms(EXIT_LAUNCH_FAILED, 6), 30_000);
}

#[test]
fn service_logs_rotate_over_five_megabytes() {
    assert_eq!(LOG_ROTATE_BYTES, 5 * 1024 * 1024);
    assert!(!log_should_rotate(0));
    assert!(!log_should_rotate(5 * 1024 * 1024));
    assert!(log_should_rotate(5 * 1024 * 1024 + 1));
}

#[test]
fn service_command_lines_and_argument_parsing() {
    assert_eq!(host_command_line(r"C:\Program Files\TeslaPC\teslapc.exe", 4242), r#""C:\Program Files\TeslaPC\teslapc.exe" --session-host --supervisor-pid 4242"#);
    assert_eq!(service_launch_arguments(), vec!["--service".to_string()]);

    let words = |s: &str| s.split_whitespace().map(String::from).collect::<Vec<_>>();
    assert_eq!(parse_service_command(&words("service install")), ServiceCommand::Install);
    assert_eq!(parse_service_command(&words("service uninstall")), ServiceCommand::Uninstall);
    assert_eq!(parse_service_command(&words("service")), ServiceCommand::Usage);
    assert_eq!(parse_service_command(&words("service remove")), ServiceCommand::Usage);
    assert_eq!(parse_service_command(&words("service install now")), ServiceCommand::Usage);

    assert_eq!(parse_supervisor_pid(&words("--session-host --supervisor-pid 812")), Some(812));
    assert_eq!(parse_supervisor_pid(&words("--session-host")), None);
    assert_eq!(parse_supervisor_pid(&words("--session-host --supervisor-pid")), None);
    assert_eq!(parse_supervisor_pid(&words("--supervisor-pid abc")), None);
    assert_eq!(parse_supervisor_pid(&words("--supervisor-pid 0")), None);
}

#[test]
fn scheduled_tasks_that_run_teslapc_are_named_once() {
    let csv = r#""HostName","TaskName","Next Run Time","Status","Logon Mode","Last Run Time","Last Result","Author","Task To Run"
"LAB","\TeslaPC Logon","N/A","Running","Interactive only","11/13/2026","0","lab","C:\Tools\TeslaPC.EXE --no-tesla-bypass"
"LAB","\Other","N/A","Ready","Interactive only","N/A","0","lab","notepad.exe"
"HostName","TaskName","Next Run Time","Status","Logon Mode","Last Run Time","Last Result","Author","Task To Run"
"LAB","\TeslaPC Logon","N/A","Running","Interactive only","11/13/2026","0","lab","C:\Tools\teslapc.exe"
"LAB","\Quoted ""name""","N/A","Ready","x","N/A","0","lab","""C:\a,b\teslapc.exe"""
"#;
    assert_eq!(tasks_running_teslapc(csv), vec![r"\TeslaPC Logon".to_string(), r#"\Quoted "name""#.to_string()]);
    assert!(tasks_running_teslapc("").is_empty());
}

#[test]
fn hotspot_adapter_name_is_the_whole_interface_name() {
    use teslapc::decisions::bypass::hotspot_adapter_name;
    let listing = "\r\nAdmin State    State          Type             Interface Name\r\n-------------------------------------------------------------------------\r\nEnabled        Connected      Dedicated        Wi-Fi\r\nEnabled        Disconnected   Dedicated        Local Area Connection* 1\r\nEnabled        Connected      Dedicated        Local Area Connection* 2\r\n";
    // Never a bare index: `name=2` would address interface index 2, which can be Wi-Fi.
    assert_eq!(hotspot_adapter_name(listing).as_deref(), Some("Local Area Connection* 2"));
    let only_down = "Enabled        Disconnected   Dedicated        Local Area Connection* 1\r\n";
    assert_eq!(hotspot_adapter_name(only_down).as_deref(), Some("Local Area Connection* 1"));
    assert_eq!(hotspot_adapter_name("Enabled        Connected      Dedicated        Wi-Fi\r\n"), None);
    assert_eq!(hotspot_adapter_name(""), None);
}

#[test]
fn desktop_switch_only_when_the_input_desktop_name_differs() {
    assert!(!desktop_switch_needed(Some("Default"), "Default"));
    assert!(!desktop_switch_needed(Some("Default"), "default"));
    assert!(desktop_switch_needed(Some("Default"), "Winlogon"));
    assert!(desktop_switch_needed(Some("Winlogon"), "Default"));
    // The attached name could not be read: attach to the input desktop.
    assert!(desktop_switch_needed(None, "Default"));
    // No name is not a desktop: stay.
    assert!(!desktop_switch_needed(Some("Default"), ""));
    assert!(!desktop_switch_needed(None, ""));
}

#[test]
fn desktop_check_runs_every_200_ms_at_once_when_forced_and_backs_off_after_a_failed_switch() {
    assert_eq!((DESKTOP_CHECK_INTERVAL_MS, DESKTOP_RETRY_AFTER_FAILURE_MS), (200, 2_000));
    // A fresh thread checks at once.
    assert!(desktop_check_due(None, false, false));
    assert!(desktop_check_due(None, false, true));
    // Rate limit.
    assert!(!desktop_check_due(Some(0), false, false));
    assert!(!desktop_check_due(Some(199), false, false));
    assert!(desktop_check_due(Some(200), false, false));
    // A failed operation checks immediately.
    assert!(desktop_check_due(Some(0), true, false));
    // After a failed SetThreadDesktop, even a forced check waits two seconds.
    assert!(!desktop_check_due(Some(200), true, true));
    assert!(!desktop_check_due(Some(1_999), false, true));
    assert!(desktop_check_due(Some(2_000), true, true));
}

#[test]
fn only_the_default_desktop_counts_as_the_user_desktop() {
    assert_eq!(DEFAULT_DESKTOP_NAME, "Default");
    assert!(is_default_desktop(Some("Default")));
    assert!(is_default_desktop(Some("DEFAULT")));
    assert!(!is_default_desktop(Some("Winlogon")));
    assert!(!is_default_desktop(Some("Screen-saver")));
    // Unknown keeps today's behavior.
    assert!(is_default_desktop(None));
}

#[test]
fn desktop_errors_are_logged_once_until_they_change() {
    assert!(desktop_error_is_new(None, "OpenInputDesktop failed: Access is denied."));
    assert!(!desktop_error_is_new(Some("OpenInputDesktop failed: Access is denied."), "OpenInputDesktop failed: Access is denied."));
    assert!(desktop_error_is_new(Some("OpenInputDesktop failed: Access is denied."), "SetThreadDesktop(Winlogon) failed: busy"));
}

#[test]
fn busy_capture_restart_waits_double_up_to_five_seconds() {
    assert_eq!((CAPTURE_RESTART_START_MS, CAPTURE_RESTART_MAX_MS), (250, 5_000));
    let waits: Vec<u64> = (0..8).map(capture_restart_delay_ms).collect();
    assert_eq!(waits, vec![0, 250, 500, 1_000, 2_000, 4_000, 5_000, 5_000]);
    assert_eq!(capture_restart_delay_ms(u32::MAX), 5_000);
}

#[test]
fn dxgi_is_retried_a_few_times_only_right_after_a_desktop_switch() {
    assert_eq!((DXGI_SWITCH_RETRIES, DXGI_SWITCH_RETRY_MS), (5, 200));
    for used in 0..DXGI_SWITCH_RETRIES {
        assert!(retry_after_switch(CaptureBackend::Dxgi, true, used));
    }
    assert!(!retry_after_switch(CaptureBackend::Dxgi, true, DXGI_SWITCH_RETRIES));
    // Not after a switch (or a frame was produced since): the normal step down.
    assert!(!retry_after_switch(CaptureBackend::Dxgi, false, 0));
    // Only DXGI is retried.
    assert!(!retry_after_switch(CaptureBackend::Gdi, true, 0));
    assert!(!retry_after_switch(CaptureBackend::Wgc, true, 0));
}

#[test]
fn pointer_takes_the_mouse_path_off_the_default_desktop() {
    // On the user's desktop: same as pointer_path.
    assert_eq!(pointer_path_on_desktop("down", true, true, true), PointerPath::Touch);
    assert_eq!(pointer_path_on_desktop("rightclick", true, true, true), PointerPath::Mouse);
    assert_eq!(pointer_path_on_desktop("down", true, false, true), PointerPath::Mouse);
    assert_eq!(pointer_path_on_desktop("down", false, true, true), PointerPath::Mouse);
    // Lock screen, sign-in screen, UAC: a touch-mode tap (down, move, up) is mouse.
    for kind in ["down", "move", "up", "rightclick"] {
        assert_eq!(pointer_path_on_desktop(kind, true, true, false), PointerPath::Mouse);
        assert_eq!(pointer_path_on_desktop(kind, false, false, false), PointerPath::Mouse);
    }
}

#[test]
fn touch_repeat_only_keeps_a_held_contact_alive_on_the_default_desktop() {
    assert!(touch_repeat_applies(true, true));
    assert!(!touch_repeat_applies(false, true));
    assert!(!touch_repeat_applies(true, false));
    assert!(!touch_repeat_applies(false, false));
}
