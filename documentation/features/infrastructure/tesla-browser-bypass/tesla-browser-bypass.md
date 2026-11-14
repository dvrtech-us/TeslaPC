# Tesla Browser Bypass

The car browser blocks RFC1918 addresses. TeslaPC publishes `100.64.0.1` and forwards `8080` and `8443` to localhost.

## Windows

Unless `--localhost` or `--no-tesla-bypass`, `win::start_bypass` turns Mobile Hotspot on (WinRT via PowerShell, the same scripts as before) and calls `hotspot_step`. `TURNEDON` waits `HOTSPOT_FRESH_ON_WAIT_MS` (`2000`) and then adds `100.64.0.1` with mask `255.255.255.0` plus the portproxy rules from `windows_portproxy_args`. `ALREADYON` with that address missing waits `HOTSPOT_AFTER_OFF_WAIT_MS` (`1500`) from the Settings page and `0` from the watchdog, then runs the same setup. `ALREADYON` with the address present does not run setup. A watchdog sleeps `HOTSPOT_WATCHDOG_MS` (`60000`). If the hotspot was switched off on the Settings page, `manual_hotspot_off` is set and the watchdog leaves it off.

Setup finds the adapter with `hotspot_adapter`, which passes `netsh interface show interface` to `hotspot_adapter_name` in `decisions/bypass.rs`. That function returns the whole interface name (for example `Local Area Connection* 2`) from the row after Admin State, State, and Type, preferring a row whose State is `Connected`; only rows named `Wi-Fi Direct` or `Local Area Connection` count. The address is added with `netsh interface ipv4 add address name=<full name> address=100.64.0.1 mask=255.255.255.0`. A bare number would be read as an interface index and could hit the Wi-Fi uplink.

If an older build turned DHCP off on Wi-Fi, restore it with `netsh interface ipv4 set address name="Wi-Fi" source=dhcp` and `netsh interface ipv4 set dnsservers name="Wi-Fi" source=dhcp`.

`POST /hotspot` with `on=1` or `on=0` is that Settings control.

## Linux

`linux_bypass_plan` stays disabled when `--no-tesla-bypass` is set or `TESLAPC_BYPASS_IFACE` is empty. A set iface adds `100.64.0.1/10` and an nftables script with DNAT of `8080` and `8443` to `127.0.0.1` plus `masquerade` so the vehicle can still reach the internet. Bringing up the access point itself is left to the operator.

A missing privilege is logged. HTTP still starts.
