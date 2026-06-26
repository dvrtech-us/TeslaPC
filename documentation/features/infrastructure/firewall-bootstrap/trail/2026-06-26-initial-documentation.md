# Initial Documentation of Firewall Bootstrap

- Date: 2026-06-26
- Feature: firewall-bootstrap
- Related code: `TeslaPCInterface/FirewallBootstrap.cs`, `TeslaPCInterface/Program.cs`

## Context

Remote devices could not reach the server until a firewall rule was added manually. This
documents the automated, idempotent bootstrap as built.

## Decisions

- Recorded the idempotency strategy: parse `LocalPort:` from `show rule verbose` and only recreate when both ports are not already covered.
- Documented the non-fatal failure contract (non-admin or netsh error logs and returns false; the app keeps running).
- Captured the distinction from the IP-scoped `TeslaPC Bypass` rule.

## Alternatives Considered

- **Always delete + add on every start** — rejected in favor of the existence check to avoid needless churn of the rule.
- **Using the Windows Firewall COM API** — netsh was used for consistency with the other bootstrap/bypass code paths.

## Consequences

- Positive: remote reachability is set up automatically and safely re-runs each launch.
- Negative: requires elevation to take effect; documented as an explicit privilege requirement.
