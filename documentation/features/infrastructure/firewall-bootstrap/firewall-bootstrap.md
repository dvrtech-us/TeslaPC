# Firewall Bootstrap

Ensures a Windows Firewall inbound rule allows TCP on the server ports (`8080`/`8443`) so
remote devices can reach TeslaPC. Idempotent: it only recreates the rule when the existing
one does not already cover both ports.

## User Flow

This runs automatically at startup (no user interaction) when not in `--localhost` mode and
when the process is elevated.

## Technical Flow (`FirewallBootstrap`, `FirewallBootstrap.cs`, namespace `PrimaryProcess`)

`TryEnsureFirewallOpen(int httpPort, int httpsPort)` (`:15`), called from `Program.cs:55`:

1. **Admin check** via `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`. If not admin, logs a warning and returns `false` (server continues; remote devices may be blocked).
2. **Idempotency** — `RuleExists(httpPort, httpsPort)` (`:45`) runs `show rule`, parses the `LocalPort:` field with regex `(?i)LocalPort:\s*([0-9,\s]+)`, and confirms both ports are present. If so, logs that the rule already exists and returns `true`.
3. If the rule is missing or incomplete, it **deletes any rule of the same name** and then **adds** a fresh rule.

### netsh commands

| Operation | Command |
|-----------|---------|
| Check | `advfirewall firewall show rule name="TeslaPC Server" verbose` |
| Delete | `advfirewall firewall delete rule name="TeslaPC Server"` |
| Add | `advfirewall firewall add rule name="TeslaPC Server" dir=in action=allow protocol=TCP localport=8080,8443 enable=yes profile=any` |

Rule parameters: `dir=in`, `action=allow`, `protocol=TCP`, `localport=8080,8443`, `enable=yes`, `profile=any`. netsh timeout is 10 000 ms.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `FirewallBootstrap` | `TeslaPCInterface/FirewallBootstrap.cs` | Idempotent inbound TCP firewall rule for the server ports |

## Constants

| Constant | Value |
|----------|-------|
| Rule name | `TeslaPC Server` |
| Ports | `8080`, `8443` |
| Profile | `any` |

## Access Control / Privileges

- Requires administrator; non-admin returns `false` without attempting changes. The application keeps running.

## Integration Points

- Called from `Program.Main` before the web server starts, only when `--localhost` is not set.
- The general `TeslaPC Server` rule is distinct from the IP-scoped `TeslaPC Bypass` rule created by [tesla-browser-bypass](../tesla-browser-bypass/tesla-browser-bypass.md).

## Database Schema

None.

## SQL Artifacts

None.
