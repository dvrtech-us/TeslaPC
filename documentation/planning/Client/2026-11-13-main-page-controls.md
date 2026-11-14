# Plan: move stream resolution off the main page, explain Fit screen

**Date:** 2026-11-13
**Status:** Revised after two adversarial reviews (Claude Opus, Codex). Awaiting owner sign-off. No code written.
**Implemented 2026-11-13:** revisions 1-7 and 9-10, plus the owner's addition of a read-only stream size and format indicator (`#streamInfo`) on the main page.
Where "Revisions after review" at the end differs from the text above, the revisions win.

## Owner request

"The resolution and fit screen buttons should probably be under settings with descriptions of
what they do and not on the main screen." After discussion: **Fit screen stays on the main
page** because it measures the browser it is tapped in. The resolution picker moves.

## Today

`teslapc/assets/index.html` control bar: Keyboard field, Files, Settings, a resolution button
(`#resBtn`, shows `1080p`, opens `#resPicker` with 480p/720p/1080p, each tap `POST /config`
`streamHeight=<h>`), and Fit screen (`fitScreen()` -> `GET /display/match?w=&h=`, result only
in `console.log`, button disabled 2.5 s, uses the implicit global `event`).

`teslapc/assets/config.html` already has a Stream resolution field (three `.res-choice`
buttons, hidden `streamHeight`, saved with the form) and a hint. It has nothing about Fit
screen.

## Changes

### Main page (`index.html`, `style.css`)

1. Remove `#resBtn`, `#resPicker`, and their script: `RES_OPTS`, `nearestRes`, `updateResUi`,
   `toggleResPicker`, `closeResPicker`, `setRes`, `pickRes`, and the `touchstart` close
   handler. Keep the `/config` fetch that sets `inputMode`.
2. Remove the `.res-picker*` rules from `style.css`. Keep `.res-choice` and `.res-choices`
   (used by `config.html`).
3. Fit screen stays. Changes:
   - Pass the button explicitly (`onclick="fitScreen(this)"`) instead of the global `event`.
   - Show the result on the page in a small status line in the control bar that clears after
     4 s:
     - changed: `PC display set to <w>x<h>`
     - not changed: `PC display already fits (<w>x<h>)`
     - request failed: `Could not change the PC display`
   - No behavior change on the server. `/display/match` already returns `changed`, `width`,
     `height`.

### Settings page (`config.html`)

4. Stream resolution field: keep the control. Rewrite the hint:
   "Size of the picture sent to the car. Higher is sharper and uses more data. This does not
   change the PC's own display. Tap a size, then Save settings."
   (The current hint says "Applies immediately", but on this page the value is only sent with
   Save settings. The hint must match what the page does.)
5. New read-only field "Fit screen" under it, no control:
   "The Fit screen button on the main screen changes the PC's real display resolution to the
   closest shape to the car's screen, so the picture fills it without black bars. It never
   picks a mode taller than the stream resolution above. The PC keeps that resolution until
   you change it."

### Not changing

- No new routes, no server code, no config keys.
- No Fit screen button on the Settings page (the Settings page has no `.screen` element; the
  measurement would differ).
- No undo for Fit screen.

## Tesla browser constraints

- Native `<select>` popups are broken there (comment in `index.html`); nothing here adds one.
- The status line must not change the control bar's height or push buttons off screen at the
  car's width; it is absolutely positioned above the bar or replaces the Fit screen label
  for 4 s. Decide by testing at 1200 px wide.

## Verification

- `cargo test` (assets are embedded; `teslapc/tests/protocol.rs` has asset checks to keep
  green or update).
- On the lab laptop from a desktop browser at about 1200x900: control bar shows Keyboard,
  Files, Settings, Fit screen; no resolution button; Fit screen shows each of the three
  messages (changed, already fits, server stopped); Settings saves `streamHeight` and the
  main page stream changes size after Save.
- Owner check in the car.

## Docs

`documentation/features/client/web-ui/` and `documentation/features/app/configuration/`:
feature docs, baselines, one trail entry each. `documentation/features/streaming/display-control/`
if it describes the button.

## Revisions after review (2026-11-13)

Both reviewers confirmed the removal list, that `play.html` and `list.html` do not use the
removed pieces, and that `streamHeight` posted alone persists and applies to live capture
without a restart.

1. **Settings resolution buttons apply on tap.** Each `.res-choice` tap sends
   `POST /config` with only `streamHeight` (what the main-page picker does today), marks the
   button active, and shows "Stream resolution set to <h>p". `streamHeight` is no longer part
   of Save settings. Reason: Save posts every field, and `apply_config_body` reports
   `restartNeeded` whenever `httpsHost` or `acmeEmail` is present, so a resolution change
   through Save would always show the restart message.
2. **A saved height that is not 480/720/1080** (the server allows 240-2160) is shown as
   "Current: <h>p" with no button active, instead of being rounded to the nearest button.
3. **Save settings sends only fields the user changed** since the page loaded, and is
   disabled until the first `GET /config` succeeds. Today a failed load leaves blank fields
   that Save would write, and every Save reports "restart needed".
4. **Fit screen messages.** `changed:false` from `/display/match` also covers a refused mode
   change and no eligible mode, so the page cannot say "already fits". Messages:
   `PC display set to <w>x<h>` when changed, `PC display unchanged (<w>x<h>)` otherwise,
   `Could not change the PC display` on a failed request, a non-2xx status, or a non-JSON
   body. No server change.
5. **Where the message shows.** It replaces the Fit screen button's label for 4 s. No
   overlay: `.controlbar` clips children placed above it, and anything over the stream
   would sit on the touch surface that forwards input. The button keeps its width, is
   disabled from tap until the response or a 5 s timeout, ignores taps while a request is
   in flight, and a newer message cancels the older timer.
6. **Cleanup also removes** the `.controlbar #resBtn` rule, the "Stream resolution" comment
   block in `style.css`, and the trailing `updateResUi(nearestRes(...))` call in
   `index.html`. `inputMode` loading stays.
7. **Hint texts.**
   - Stream resolution: "Largest picture height sent to the car. Higher is sharper and uses
     more data. It never enlarges the picture and does not change the PC's own display.
     Applies immediately. A video that is already playing keeps its size until it is
     started again."
   - Fit screen: "The Fit screen button on the main screen changes the PC's real display
     resolution to the supported mode closest in shape to the car's screen, to reduce black
     bars. It picks modes between 480 lines and the stream resolution above. TeslaPC does
     not restore the previous resolution afterwards."
8. **Lost on the main page (accepted by the owner's request):** changing stream resolution
   without leaving the stream, and seeing the current stream height there.
9. **Tests.** `tests/protocol.rs` has no checks on these pages today (the plan was wrong).
   Add one: the embedded `index.html` contains no `resBtn`, `toggleResPicker`, or `pickRes`,
   still loads `inputMode`, and `config.html` posts `streamHeight` on tap.
10. **Docs.** `display-control` feature doc and baseline are required (they describe the
    picker), with `web-ui` and `configuration`.
11. **Verification adds:** refused/no-mode Fit screen response, repeated taps, touch still
    reaching the stream while the message shows, `play.html` control bar unchanged, a custom
    `TESLAPC_STREAM_HEIGHT`, Save with nothing changed, Save after a failed load.
