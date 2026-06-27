# Tesla Browser Bypass

Lets the Tesla in-car browser reach TeslaPC over the PC's Mobile Hotspot. The Tesla browser
**blocks RFC 1918 private IPs** (10.x, 172.16–31.x, 192.168.x), and the hotspot adapter is
assigned `192.168.137.x`. The bypass adds a CGNAT-range secondary IP (`100.64.0.1`, in
`100.64.0.0/10`, which is not RFC 1918) to the same adapter and portproxies the server ports
through it, so the Tesla browser will connect.

## User Flow

1. TeslaPC launches (without `--no-tesla-bypass`) **as administrator**.
2. If Windows Mobile Hotspot is off, `HotspotManager.EnsureHotspotOn()` turns it on (WinRT
   tethering API via PowerShell), then waits ~2 s for the virtual adapter to come up.
3. The bypass adds `100.64.0.1` to the hotspot adapter and forwards `8080`/`8443` to localhost.
4. The user connects the Tesla to the hotspot and browses to `https://my.thelpers.com:8443/`
   (trusted) or `https://100.64.0.1:8443/`.
5. On shutdown, the bypass removes the IP, portproxy rules, and firewall rule.

## Technical Flow (`TeslaBrowserBypass`, `TeslaBrowserBypass.cs`, namespace `TeslaBrowserBypass`)

### `Setup(int httpPort = 8080, int httpsPort = 8443)` (`:34`)

1. `FindHotspotAdapter()` — returns false if not found.
2. `AddSecondaryIP(adapter)` — idempotent: skips if `100.64.0.1` is already on the correct adapter; returns false if it is on a different adapter.
3. `AddPortProxy(httpPort, httpPort)` and `AddPortProxy(httpsPort, httpsPort)`.
4. `AddFirewallRule(httpPort, httpsPort)`.
5. On any post-IP failure, calls `Cleanup()`.

### `FindHotspotAdapter()` (`:79`)

- Primary: an `Up` adapter whose `Description` contains `"Microsoft Wi-Fi Direct Virtual Adapter"` or `"Microsoft Hosted Network Virtual Adapter"`.
- Fallback: any adapter with a unicast address starting `192.168.137.`.

### netsh commands

| Operation | Command (args to `netsh`) |
|-----------|---------------------------|
| Add secondary IP | `interface ipv4 add address "{adapter}" 100.64.0.1 255.255.255.0 SkipAsSource=True` |
| Delete secondary IP | `interface ipv4 delete address "{adapter}" 100.64.0.1` |
| Add portproxy | `interface portproxy add v4tov4 listenaddress=100.64.0.1 listenport={p} connectaddress=127.0.0.1 connectport={p}` |
| Delete portproxy | `interface portproxy delete v4tov4 listenaddress=100.64.0.1 listenport={p}` |
| Add firewall rule | `advfirewall firewall add rule name="TeslaPC Bypass" dir=in action=allow protocol=TCP localip=100.64.0.1 localport=8080,8443` |
| Delete firewall rule | `advfirewall firewall delete rule name="TeslaPC Bypass"` |

Each `netsh` process is given a 10 000 ms `WaitForExit` timeout.

### Cleanup (`Cleanup` `:213` / `Dispose` `:279`)

- Removes each tracked portproxy rule, removes the secondary IP (only if `_ipAdded`), then removes the firewall rule.
- `Dispose()` is invoked from `Program.cs:119` (`bypass?.Dispose()`) on normal shutdown and `Ctrl+C`.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `TeslaBrowserBypass` | `TeslaPCInterface/TeslaBrowserBypass.cs` | Add CGNAT IP + portproxy on the hotspot adapter; clean up on dispose |

## Constants

| Constant | Value |
|----------|-------|
| `BypassIP` | `100.64.0.1` |
| `SubnetMask` | `255.255.255.0` |
| Firewall rule name | `TeslaPC Bypass` (distinct from `TeslaPC Server`) |
| Hotspot fallback range | `192.168.137.*` |
| Forwarded ports | `8080`, `8443` (listen-port == connect-port; connect to `127.0.0.1`) |

## Access Control / Privileges

- Requires administrator. Without it, every netsh command fails and `Setup()` returns false; the app continues **without** the bypass (`Program.cs` disposes and nulls the instance).

## Integration Points

- Invoked from `Program.Main` unless `--no-tesla-bypass` is set. `restart-server.bat` passes `--no-tesla-bypass`.
- Independent of [firewall-bootstrap](../firewall-bootstrap/firewall-bootstrap.md) and [https-bootstrap](../https-bootstrap/https-bootstrap.md), which still run unless `--localhost` is set.

## Database Schema

None.

## SQL Artifacts

None.
