//! Linux CGNAT bypass. Empty iface or `--no-tesla-bypass` leaves it off.
//! A set iface produces an address plus nftables forward and NAT rules.

pub const BYPASS_IP: &str = "100.64.0.1";
pub const LINUX_BYPASS_CIDR: &str = "100.64.0.1/10";

/// Windows waits this long after a fresh hotspot-on before attaching the address.
pub const HOTSPOT_FRESH_ON_WAIT_MS: u64 = 2000;
/// Windows waits this long after an off before the next setup.
pub const HOTSPOT_AFTER_OFF_WAIT_MS: u64 = 1500;
/// Watchdog period.
pub const HOTSPOT_WATCHDOG_MS: u64 = 60_000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinuxBypassPlan {
    pub enabled: bool,
    pub iface: String,
    pub address_cidr: String,
    /// Full nftables script: DNAT of 8080 and 8443 plus masquerade so the vehicle still has internet.
    pub nft_script: String,
}

pub fn linux_bypass_plan(
    no_tesla_bypass: bool,
    iface: Option<&str>,
    http_port: u16,
    https_port: u16,
) -> LinuxBypassPlan {
    let iface = iface.unwrap_or("").trim().to_string();
    if no_tesla_bypass || iface.is_empty() {
        return LinuxBypassPlan {
            enabled: false,
            iface,
            address_cidr: String::new(),
            nft_script: String::new(),
        };
    }
    let nft_script = format!(
        "\
add table ip teslapc
delete table ip teslapc
table ip teslapc {{
  chain prerouting {{
    type nat hook prerouting priority dstnat; policy accept;
    iifname \"{iface}\" tcp dport {http_port} dnat to 127.0.0.1:{http_port}
    iifname \"{iface}\" tcp dport {https_port} dnat to 127.0.0.1:{https_port}
  }}
  chain postrouting {{
    type nat hook postrouting priority srcnat; policy accept;
    oifname \"{iface}\" masquerade
  }}
  chain forward {{
    type filter hook forward priority filter; policy accept;
    iifname \"{iface}\" tcp dport {{ {http_port}, {https_port} }} accept
    ct state established,related accept
    oifname \"{iface}\" accept
  }}
}}
"
    );
    LinuxBypassPlan {
        enabled: true,
        iface,
        address_cidr: LINUX_BYPASS_CIDR.to_string(),
        nft_script,
    }
}

pub fn windows_portproxy_args(listen_port: u16) -> String {
    format!(
        "interface portproxy add v4tov4 listenaddress={BYPASS_IP} listenport={listen_port} connectaddress=127.0.0.1 connectport={listen_port}"
    )
}

pub fn bypass_wanted(no_tesla_bypass: bool) -> bool {
    !no_tesla_bypass
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HotspotReport {
    TurnedOn,
    AlreadyOn,
    TurnedOff,
    AlreadyOff,
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HotspotSource {
    SettingsOn,
    Watchdog,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HotspotStep {
    pub wait_ms: u64,
    pub setup_bypass: bool,
}

pub fn parse_hotspot_report(text: &str) -> HotspotReport {
    if text.contains("TURNEDON") {
        HotspotReport::TurnedOn
    } else if text.contains("ALREADYON") {
        HotspotReport::AlreadyOn
    } else if text.contains("TURNEDOFF") {
        HotspotReport::TurnedOff
    } else if text.contains("ALREADYOFF") {
        HotspotReport::AlreadyOff
    } else {
        HotspotReport::Failed
    }
}

pub fn bypass_address_present(text: &str) -> bool {
    text.contains(BYPASS_IP)
}

/// Fresh on (`TURNEDON`) waits 2000 ms. A re-setup after the hotspot is already on but
/// `100.64.0.1` is missing waits 1500 ms from the Settings page and runs immediately from the watchdog.
/// The watchdog does nothing while the user left the hotspot off.
pub fn hotspot_step(source: HotspotSource, manually_off: bool, report: HotspotReport, address_present: bool) -> HotspotStep {
    if source == HotspotSource::Watchdog && manually_off {
        return HotspotStep { wait_ms: 0, setup_bypass: false };
    }
    match report {
        HotspotReport::TurnedOn => HotspotStep { wait_ms: HOTSPOT_FRESH_ON_WAIT_MS, setup_bypass: true },
        HotspotReport::AlreadyOn if !address_present => HotspotStep {
            wait_ms: if source == HotspotSource::SettingsOn { HOTSPOT_AFTER_OFF_WAIT_MS } else { 0 },
            setup_bypass: true,
        },
        _ => HotspotStep { wait_ms: 0, setup_bypass: false },
    }
}
