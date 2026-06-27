# Tesla Browser Bypass — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- The bypass IP is always `100.64.0.1` / `255.255.255.0` — a CGNAT (`100.64.0.0/10`) address, deliberately **not** RFC 1918, because the Tesla browser blocks private IPs.
- The secondary IP is added with `SkipAsSource=True` so outbound traffic does not originate from it.
- Portproxy always maps `listenport == connectport`, with `connectaddress = 127.0.0.1` (8080→8080, 8443→8443).
- `AddSecondaryIP` is idempotent: it skips if the IP is already on the correct adapter and fails if it is bound to a different adapter.
- The bypass firewall rule is named `TeslaPC Bypass` and is **scoped to `localip=100.64.0.1`** — distinct from the general `TeslaPC Server` rule.
- Cleanup on `Dispose()` removes portproxy rules, the secondary IP (only if it was added), and the firewall rule — and runs on both normal shutdown and `Ctrl+C`.

## Adapter Selection Rules

- Prefer an `Up` adapter described as a Microsoft Wi-Fi Direct / Hosted Network Virtual Adapter.
- Otherwise pick any adapter with a `192.168.137.*` address.

## Privilege / Failure

- Administrator is required; without it `Setup()` returns false and the app runs without the bypass.
- Any failure after the IP is added triggers `Cleanup()`.
- Each netsh invocation has a 10 000 ms timeout.

## Enablement

- Enabled by default; disabled by the `--no-tesla-bypass` flag (used by `restart-server.bat`).
