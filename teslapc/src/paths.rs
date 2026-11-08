//! Config, certificate, and database locations.

use std::path::PathBuf;

pub fn config_dir() -> PathBuf {
    #[cfg(windows)]
    {
        program_data().join("TeslaPC")
    }
    #[cfg(not(windows))]
    {
        xdg_config().join("teslapc")
    }
}

pub fn data_dir() -> PathBuf {
    #[cfg(windows)]
    {
        program_data().join("TeslaPC")
    }
    #[cfg(not(windows))]
    {
        xdg_data().join("teslapc")
    }
}

pub fn env_file() -> PathBuf {
    config_dir().join(".env")
}

pub fn db_file() -> PathBuf {
    data_dir().join("teslapc.db")
}

pub fn cert_pem() -> PathBuf {
    config_dir().join("cert.pem")
}

pub fn key_pem() -> PathBuf {
    config_dir().join("key.pem")
}

pub fn acme_account() -> PathBuf {
    config_dir().join("acme-account.json")
}

#[cfg(windows)]
fn program_data() -> PathBuf {
    std::env::var_os("ProgramData")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"))
}

#[cfg(not(windows))]
fn xdg_config() -> PathBuf {
    if let Some(dir) = std::env::var_os("XDG_CONFIG_HOME") {
        return PathBuf::from(dir);
    }
    home().join(".config")
}

#[cfg(not(windows))]
fn xdg_data() -> PathBuf {
    if let Some(dir) = std::env::var_os("XDG_DATA_HOME") {
        return PathBuf::from(dir);
    }
    home().join(".local").join("share")
}

#[cfg(not(windows))]
fn home() -> PathBuf {
    std::env::var_os("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("/tmp"))
}

pub fn default_video_root() -> String {
    #[cfg(windows)]
    {
        r"C:\video\".to_string()
    }
    #[cfg(not(windows))]
    {
        let home = std::env::var_os("HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("/tmp"));
        home.join("Videos").to_string_lossy().into_owned()
    }
}

/// systemd unit for a non-interactive TeslaPC process. Logs go to the journal.
pub fn linux_systemd_unit(exec: &str) -> String {
    format!(
        "\
[Unit]
Description=TeslaPC remote desktop
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart={exec}
Restart=on-failure
RestartSec=2

[Install]
WantedBy=multi-user.target
"
    )
}

/// `sc.exe create` arguments. The binary is started with `--service` and no window.
pub fn windows_service_create_args(bin: &str) -> Vec<String> {
    vec![
        "create".into(),
        "TeslaPC".into(),
        format!("binPath= \"{bin}\" --service"),
        "start=".into(),
        "auto".into(),
        "DisplayName=".into(),
        "TeslaPC".into(),
    ]
}
