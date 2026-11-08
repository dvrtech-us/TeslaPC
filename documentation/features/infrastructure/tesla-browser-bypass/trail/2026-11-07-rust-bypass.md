# Bypass rules are a tested plan

- Date: 2026-11-07
- Feature: tesla-browser-bypass
- Related code: `teslapc/src/decisions/bypass.rs`, `teslapc/src/win.rs`, `teslapc/src/linux.rs`

## Context

Windows used Mobile Hotspot, `100.64.0.1`, and portproxy. Linux had no bypass.

## Decisions

`linux_bypass_plan` is the tested description of the Linux address, forward, and NAT. Windows still uses hotspot, portproxy, and the 60 second watchdog. Hotspot on/off is on the Settings page.

## Alternatives Considered

- Start hostapd from the server: rejected. The Linux access point stays the operator's job.

## Consequences

- Positive: an empty iface cannot install nftables rules.
- Negative: `--localhost` does not publish `100.64.0.1`, so a local test does not change the hotspot.
