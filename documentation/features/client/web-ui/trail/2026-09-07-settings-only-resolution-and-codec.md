# Move stream resolution and video codec off the main screen to Settings

- Date: 2026-09-07
- Feature: web-ui (client)
- Related code: `TeslaPCInterface/index.html` (control bar + removed picker scripts), `TeslaPCInterface/config.html` (canonical controls), `TeslaPCInterface/style.css` (removed `.res-picker*`)

## Context

The main screen control bar carried inline pop-up pickers for stream resolution (`#resBtn` /
`#resPicker`) and video codec (`#codecBtn` / `#codecPicker`), duplicating controls that already
exist on the Settings page. Configuration belongs on the Settings page; the main screen should
carry the display plus actions only. (Done as part of merging `Dev` — WGC/wheel/ACME — into the
A/V-sync phase-2 branch that introduced the H264 codec toggle.)

## Decisions

- Removed both pop-up pickers and their JS (`toggleResPicker`/`pickRes`/`updateResUi`/`setRes`,
  `toggleCodecPicker`/`pickCodec`/`updateCodecUi`/`setCodec`, the outside-tap close handlers, and
  the `nearestRes`/`RES_OPTS` helpers) from `index.html`.
- The control bar now holds only Keyboard, Files, Settings, and **Fit screen** (an action, not a
  setting, so it stays).
- Resolution and codec remain fully controlled on `config.html`, which already posts
  `streamHeight` and `displayRenderer`/`displayTransport` to `/config` and shows the H264 bitrate
  control. No server changes.
- Removed the now-unused `.res-picker*` CSS; kept `.res-choices` / `.res-choice.active` /
  `.codec-choice.active`, which the Settings page uses.
- The main screen's remaining `/config` fetch keeps only `audioBoost` (resolution/codec UI no
  longer needs to hydrate there).

## Consequences

- Positive: one place to change resolution/codec; simpler, less cluttered main screen; less
  duplicated JS.
- Trade-off: changing these now takes one extra tap (open Settings) instead of an inline popup —
  acceptable for settings that are set once, not per-session.
