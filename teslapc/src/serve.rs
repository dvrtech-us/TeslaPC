//! Process entry. One binary, no window. `--localhost` serves HTTP on 127.0.0.1:8080
//! and skips the firewall, ACME, and HTTPS. On Windows `--service` is the supervisor only
//! (`win_service.rs`); `--session-host` is this server started by it in the console session.

use std::net::SocketAddr;
use std::sync::Arc;

use tokio::net::TcpListener;
use tokio_rustls::TlsAcceptor;

use crate::certs::{self, CertSlot};
use crate::decisions::bypass::bypass_wanted;
use crate::decisions::config::{merge_dotenv, KEY_HTTPS_HOST};
use crate::decisions::service::{parse_service_command, parse_supervisor_pid, ServiceCommand, EXIT_PORT_IN_USE, SERVICE_USAGE};
use crate::paths;
use crate::platform;
use crate::playback;
use crate::routes;
use crate::state::App;

#[derive(Clone)]
struct Opts {
    localhost: bool,
    no_bypass: bool,
    https_host: Option<String>,
    service: bool,
    /// `--session-host`: started by the Windows service supervisor in the console session.
    session_host: bool,
    supervisor_pid: Option<u32>,
}

/// `serve` fails only when port 8080 cannot be bound. A session host then exits with
/// `EXIT_PORT_IN_USE` so the supervisor can tell it apart from a crash.
struct HttpBindError(String);

pub fn run() -> i32 {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("service") {
        return match parse_service_command(&args) {
            ServiceCommand::Install => platform::service_install(),
            ServiceCommand::Uninstall => platform::service_uninstall(),
            ServiceCommand::Usage => {
                println!("{SERVICE_USAGE}");
                2
            }
        };
    }
    let session_host = args.iter().any(|a| a == "--session-host");
    if session_host {
        // Before anything prints: this process has no console the operator can see.
        platform::redirect_host_log();
    }
    load_dotenv();
    let opts = parse_args(&args);
    if opts.service && !opts.session_host && platform::run_as_service() {
        return 0;
    }
    if opts.session_host {
        println!("[Service] Session host started (supervisor pid {}).", opts.supervisor_pid.map(|p| p.to_string()).unwrap_or_else(|| "none".into()));
        crate::capture_loop::skip_wgc();
        if let Some(pid) = opts.supervisor_pid {
            platform::exit_with_supervisor(pid);
        }
    }
    block_on(opts)
}

fn block_on(opts: Opts) -> i32 {
    let runtime = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(rt) => rt,
        Err(err) => {
            println!("Failed to start runtime: {err}");
            return 1;
        }
    };
    let session_host = opts.session_host;
    match runtime.block_on(serve(opts)) {
        Ok(()) => 0,
        Err(HttpBindError(err)) if session_host => {
            println!("Server failed: {err}");
            println!("[Service] Port 8080 is in use. Exiting with code {EXIT_PORT_IN_USE}.");
            EXIT_PORT_IN_USE
        }
        Err(HttpBindError(err)) => {
            println!("Server failed: {err}");
            1
        }
    }
}

fn parse_args(args: &[String]) -> Opts {
    let mut https_host = None;
    let mut i = 0;
    while i < args.len() {
        if args[i] == "--https-host" {
            https_host = args.get(i + 1).cloned();
            i += 2;
            continue;
        }
        i += 1;
    }
    if https_host.as_ref().map(|s| s.trim().is_empty()).unwrap_or(true) {
        https_host = std::env::var(KEY_HTTPS_HOST).ok().filter(|s| !s.trim().is_empty());
    }
    Opts {
        localhost: args.iter().any(|a| a == "--localhost"),
        no_bypass: args.iter().any(|a| a == "--no-tesla-bypass"),
        https_host,
        service: args.iter().any(|a| a == "--service"),
        session_host: args.iter().any(|a| a == "--session-host"),
        supervisor_pid: parse_supervisor_pid(args),
    }
}

fn load_dotenv() {
    let mut files = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            files.push(dir.join(".env"));
        }
    }
    files.push(paths::env_file());
    let real: Vec<String> = std::env::vars().map(|(k, _)| k).collect();
    // Later files override earlier ones. ProgramData is last. A variable that was already
    // set in the real environment is left alone.
    for path in &files {
        let Ok(text) = std::fs::read_to_string(path) else { continue };
        for (key, value) in merge_dotenv(&[], &text) {
            if real.iter().any(|k| k == &key) {
                continue;
            }
            std::env::set_var(key, value);
        }
    }
}

async fn serve(opts: Opts) -> Result<(), HttpBindError> {
    platform::init_dpi_awareness();
    if let Some(host) = &opts.https_host {
        std::env::set_var(KEY_HTTPS_HOST, host);
    }
    let (ffmpeg, ffprobe) = playback::locate_tools();
    let app = App::new(opts.localhost, opts.no_bypass, crate::state::Playback::new(ffmpeg, ffprobe));
    if let Ok(mut host) = app.inner.https_host.lock() {
        if host.is_empty() {
            if let Some(value) = &opts.https_host {
                *host = value.clone();
            }
        }
    }
    let input_ok = platform::probe_input();
    app.inner.input_enabled.store(input_ok, std::sync::atomic::Ordering::Relaxed);
    if !input_ok {
        println!("[Input] Input privilege is missing. Input is disabled for this launch. HTTP will still start.");
    }
    let touch_ok = input_ok && platform::probe_touch();
    app.inner.touch_enabled.store(touch_ok, std::sync::atomic::Ordering::Relaxed);
    if input_ok && !touch_ok {
        let touch_mode = app.inner.input_mode.lock().map(|m| m.as_str() != "mouse").unwrap_or(true);
        if touch_mode {
            println!("[Input] Touch injection is unavailable. Pointer events use the mouse instead.");
        }
    }
    platform::start_audio(app.clone());
    crate::capture_loop::ensure_started(&app);
    let app_k = app.clone();
    tokio::spawn(async move { routes::silence_keepalive(app_k).await });
    let app_d = app.clone();
    tokio::spawn(async move { routes::pcm_dispatch(app_d).await });
    let app_t = app.clone();
    tokio::spawn(async move { routes::touch_repeat(app_t).await });

    let http_addr: SocketAddr = if opts.localhost {
        "127.0.0.1:8080".parse().unwrap()
    } else {
        "0.0.0.0:8080".parse().unwrap()
    };
    let listener = TcpListener::bind(http_addr).await.map_err(|e| HttpBindError(format!("bind {http_addr}: {e}")))?;
    println!("Unified server listening on:");
    println!("\thttp://{http_addr}/");
    println!();
    println!("==================== TeslaPC ====================");
    if opts.localhost {
        println!("  Running (localhost only)");
        println!("  http://127.0.0.1:8080/");
        let _ = std::io::Write::flush(&mut std::io::stdout());
    } else {
        println!("  Running on HTTP 8080");
    }

    let router = routes::router(app.clone());
    let http = tokio::spawn(async move {
        if let Err(err) = axum::serve(listener, router).await {
            println!("HTTP server error: {err}");
        }
    });

    if !opts.localhost {
        platform::open_firewall(8080, 8443);
        if bypass_wanted(opts.no_bypass) {
            platform::start_bypass(app.clone());
        }
        certs::install_provider();
        if let Err(err) = certs::ensure_local_cert() {
            println!("[HTTPS] Local certificate was not created: {err}");
        } else if let Ok(config) = certs::load_server_config() {
            let slot: CertSlot = Arc::new(std::sync::RwLock::new(config));
            let app_r = app.clone();
            let slot_r = slot.clone();
            tokio::spawn(async move {
                if certs::maybe_issue(&app_r).await {
                    if let Ok(fresh) = certs::load_server_config() {
                        if let Ok(mut guard) = slot_r.write() {
                            *guard = fresh;
                        }
                    }
                }
                certs::renewal_loop(app_r, slot_r).await;
            });
            let tls_router = routes::router(app.clone());
            tokio::spawn(async move {
                if let Err(err) = serve_tls(slot, tls_router).await {
                    println!("[HTTPS] {err}");
                }
            });
            println!("\thttps://0.0.0.0:8443/");
        }
    }

    match tokio::signal::ctrl_c().await {
        Ok(()) => println!("Shutting down."),
        Err(err) => {
            println!("[Signal] No console control handler ({err}). Serving until the process is stopped.");
            std::future::pending::<()>().await;
        }
    }
    http.abort();
    Ok(())
}

async fn serve_tls(slot: CertSlot, app: axum::Router) -> Result<(), String> {
    let listener = TcpListener::bind("0.0.0.0:8443").await.map_err(|e| e.to_string())?;
    loop {
        let (tcp, _) = listener.accept().await.map_err(|e| e.to_string())?;
        let config = slot.read().map_err(|e| e.to_string())?.clone();
        let acceptor = TlsAcceptor::from(config);
        let app = app.clone();
        tokio::spawn(async move {
            let Ok(tls) = acceptor.accept(tcp).await else { return };
            let io = hyper_util::rt::TokioIo::new(tls);
            let service = hyper_util::service::TowerToHyperService::new(app);
            let _ = hyper::server::conn::http1::Builder::new()
                .serve_connection(io, service)
                .with_upgrades()
                .await;
        });
    }
}
