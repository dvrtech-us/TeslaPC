# Fit screen reports unchanged, not already fits

- Date: 2026-11-13
- Feature: streaming/display-control

## Problem

`/display/match` returns `changed: false` for an already matching display, a refused mode change, and no eligible mode, so the UI could not claim the display already fits.

## Decision

The main page reports `PC display unchanged (<w>x<h>)`. No server change. The resolution picker moved to Settings.

## Files

`teslapc/assets/index.html`, `teslapc/assets/config.html`
