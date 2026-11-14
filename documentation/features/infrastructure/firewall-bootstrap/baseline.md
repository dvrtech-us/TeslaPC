# Firewall — Behavior Baseline

- Rule name is `TeslaPC Server`.
- Ports are `8080` and `8443`, protocol TCP, direction in, action allow, profile any.
- `--localhost` does not call `open_firewall`.
- Failure is logged and does not stop HTTP.
