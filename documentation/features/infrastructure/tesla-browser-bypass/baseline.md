# Tesla Browser Bypass — Behavior Baseline

- Bypass address is `100.64.0.1`.
- `--no-tesla-bypass` leaves Windows and Linux bypass off.
- An empty Linux iface leaves the bypass off and produces no nftables script.
- A set Linux iface produces `100.64.0.1/10`, DNAT for `8080` and `8443`, a forward chain, and `masquerade`.
- Windows `TURNEDON` waits `2000` ms, then setup runs. Settings `ALREADYON` with `100.64.0.1` missing waits `1500` ms, then setup runs. The watchdog does that setup immediately. An address that is already present skips setup. Watchdog period is `60` s.
- Manual off from `POST /hotspot` is not turned back on by the watchdog.
- `--localhost` does not start the bypass.
