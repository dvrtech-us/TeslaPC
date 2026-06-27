# Handle missing/unreadable browse folder gracefully (HTTP 200, not 500)

- Date: 2026-06-27
- Feature: media/file-browser-vlc
- Related code: `TeslaPCInterface/WebServer.cs` (`returnAllFilesAsHtmlLinks`)

## Context

The video browse root is now user-settable via the Config tab and the web Settings page
(see `documentation/features/app/configuration`). Before that change, the folder was
`C:\video\` — a hardcoded constant unlikely to be wrong. With a user-editable path, a
typo, a renamed folder, or a removed drive can cause `returnAllFilesAsHtmlLinks` to call
`Directory.GetDirectories` / `Directory.GetFiles` on a non-existent path. Both calls
throw under that condition, which bubbled up as an unhandled exception → HTTP 500 with a
generic error body.

An HTTP 500 on a routine browser page is a poor experience and gives the user no
indication of how to fix the problem.

## Decisions

### Check `Directory.Exists` before listing

`returnAllFilesAsHtmlLinks` now calls `Directory.Exists(path)` before attempting any
directory enumeration. If the folder does not exist, it returns the standard `.topbar`
markup (so the page still looks consistent) plus a `.empty` block explaining that the
folder was not found and linking to `/config.html` where the video folder can be corrected.
Response status is HTTP 200.

This is the preferred path over catching a `DirectoryNotFoundException` because
`Directory.Exists` is a deliberate check whose result is unambiguous; exception-based
flow here would swallow genuine bugs (e.g. a null path argument) alongside expected
missing-folder cases.

### Wrap GetDirectories / GetFiles in try/catch

Even when `Directory.Exists` returns true, enumeration can still fail (e.g. access denied
on a mapped network drive, a permission boundary, or a race between the check and the
read). The two enumeration calls are wrapped in a try/catch that:

- Calls `Log.Warn` with the exception message (visible at the default `info` threshold
  since Warn < Info is false — i.e. Warn is always shown at the default level).
- Returns a `.empty` message ("Couldn't read this folder") — HTTP 200.

### HTTP 200 (not 4xx or 5xx)

The folder not existing is a configuration issue, not a server error. Returning 200 with
a clear explanation is more useful than 500 (which would show a generic error or blank
page in the Tesla browser). A future change could introduce a proper 404-style page, but
200 is consistent with how the rest of the file-browser pages work (no file → `.empty`
message, not an error code).

## Consequences

- **Positive:** A misconfigured video folder now shows a legible message with a direct
  link to the Settings page rather than an HTTP 500.
- **Positive:** Access errors on existing but unreadable folders are also caught and
  shown rather than crashing the request handler.
- **Note:** The Failure Behavior section of `baseline.md` has been updated to reflect
  that a missing browse folder is no longer an HTTP 500 condition.
- **Note:** VLC `Process.Start` failures (ffmpeg absent, VLC missing) are not covered by
  this change and still throw → HTTP 500.
