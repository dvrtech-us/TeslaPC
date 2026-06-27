# Firewall Bootstrap — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- The rule is named `TeslaPC Server` and allows inbound TCP on `8080,8443` across `profile=any`.
- The operation is **idempotent**: if a rule of that name already covers both ports, nothing is changed and it returns `true`.
- If the rule is missing or does not cover both ports, the existing same-named rule is deleted and recreated.
- Port coverage is verified by parsing the `LocalPort:` field of `show rule ... verbose` with the regex `(?i)LocalPort:\s*([0-9,\s]+)`.

## Privilege / Failure

- Administrator is required; non-admin returns `false` without making changes, and the application continues (remote access may be blocked).
- Failure logs the netsh exit code and output; it is never fatal to startup.
- netsh timeout is 10 000 ms.

## Enablement

- Runs only when `--localhost` is **not** set. The `--no-tesla-bypass` flag does not affect it.
