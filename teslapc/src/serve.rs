//! Process entry. One binary, no window. `--localhost` serves HTTP on 127.0.0.1:8080
//! and skips the firewall, ACME, and HTTPS.

use std::net::SocketAddr;
use std::sync::Arc;

use tokio::net::TcpListener;
use tokio_rustls::TlsAcceptor;

use crate::certs::{self, CertSlot};
use crate::decisions::bypass::bypass_wanted;
use crate::decisions::config::{merge_dotenv, KEY_HTTPS_HOST};
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
}

pub fn run() -> i32 {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("service") {
        return platform::service_install();
    }
    load_dotenv();
    let opts = parse_args(&args);
    if opts.service && platform::run_as_service({
        let opts = opts.clone();
        move || {
            let _ = block_on(opts);
        }
    }) {
        return 0;
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
    match runtime.block_on(serve(opts)) {
        Ok(()) => 0,
        Err(err) => {
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

async fn serve(opts: Opts) -> Result<(), String> {
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
    let listener = TcpListener::bind(http_addr).await.map_err(|e| format!("bind {http_addr}: {e}"))?;
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
