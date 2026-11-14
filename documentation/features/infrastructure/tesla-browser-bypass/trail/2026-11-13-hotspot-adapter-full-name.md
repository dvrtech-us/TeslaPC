# Hotspot adapter addressed by full name

- Date: 2026-11-13
- Feature: tesla-browser-bypass
- Related code: `teslapc/src/decisions/bypass.rs`, `teslapc/src/win.rs`, `teslapc/tests/protocol.rs`

## Problem

Found on the lab laptop after a reboot. `hotspot_adapter()` took the last whitespace-separated token of the `netsh interface show interface` row, so `Local Area Connection* 2` became `2`. `setup_bypass` then ran `netsh interface ipv4 add address name=2 100.64.0.1 255.255.255.0`. netsh reads a bare number as an interface index, and index 2 was the Wi-Fi uplink. DHCP was switched off on Wi-Fi and it fell back to a `169.254.x.x` address with no IPv4 internet. It only happened when `setup_bypass` actually ran (fresh hotspot on, or the watchdog finding the address missing), which is why it appeared after a reboot.

## Decision

- New pure function `hotspot_adapter_name` returns the whole interface name and prefers a row whose State is `Connected`. `hotspot_adapter()` calls it.
- The netsh call passes `address=` and `mask=` explicitly.
- Test `hotspot_adapter_name_is_the_whole_interface_name` covers it.
- Verified on the lab laptop: after removing `100.64.0.1`, the service re-added it on `Local Area Connection* 2` and Wi-Fi kept DHCP and its `192.168.4.x` address.
- Recovery for an affected machine: `netsh interface ipv4 set address name="Wi-Fi" source=dhcp` and `netsh interface ipv4 set dnsservers name="Wi-Fi" source=dhcp`.

## Files

- `teslapc/src/decisions/bypass.rs`
- `teslapc/src/win.rs`
- `teslapc/tests/protocol.rs`
