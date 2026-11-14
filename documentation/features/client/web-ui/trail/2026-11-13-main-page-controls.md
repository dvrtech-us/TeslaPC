# Main page: resolution picker removed, Fit screen feedback, stream indicator

- Date: 2026-11-13
- Feature: client/web-ui

## Problem

The main page carried a stream resolution picker that belongs in Settings. Fit screen gave no on-page result and used the implicit global `event`. Owner also asked for a read-only stream size and format label.

## Decision

Removed `#resBtn`, `#resPicker`, and their script and CSS. Fit screen takes its button, allows one request, checks `r.ok`, and reports in its own label for 4 s. Added `#streamInfo` (`<w>×<h> · MJPEG`, polled 1 s from `naturalWidth`/`naturalHeight`, format from `VIDEO_STREAM`). Plan: `documentation/planning/Client/2026-11-13-main-page-controls.md`.

## Files

`teslapc/assets/index.html`, `teslapc/assets/style.css`, `teslapc/tests/protocol.rs`
