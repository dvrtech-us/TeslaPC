# Settings: resolution applies on tap, Save sends only changes

- Date: 2026-11-13
- Feature: app/configuration

## Problem

Save posted every field, so every save reported `restartNeeded`, and a failed load left blank fields that Save would write. A resolution change through Save was never immediate.

## Decision

Resolution buttons post only `streamHeight` on tap. Save posts only changed fields, is disabled until the first successful load, and says `Nothing to save.` when nothing changed. Non-standard heights show `Current: <h>p`. Added the two revised hint texts and a Fit screen field.

## Files

`teslapc/assets/config.html`, `teslapc/tests/protocol.rs`
