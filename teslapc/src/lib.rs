//! TeslaPC server library. Protocol decisions live in [`decisions`] and are the
//! functions the HTTP server calls. Tests call those same functions.

pub mod assets;
pub mod capture_loop;
pub mod certs;
pub mod decisions;
pub mod encode;
pub mod paths;
pub mod platform;
pub mod playback;
pub mod routes;
pub mod serve;
pub mod state;
pub mod store;

#[cfg(windows)]
pub mod win;
#[cfg(windows)]
mod win_capture;
#[cfg(windows)]
mod win_desktop;
#[cfg(windows)]
mod win_service;
#[cfg(target_os = "linux")]
pub mod linux;

pub fn crate_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}
