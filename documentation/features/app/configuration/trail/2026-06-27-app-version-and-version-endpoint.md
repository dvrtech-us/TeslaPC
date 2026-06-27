# App version field in GET /config and GET /version endpoint

- Date: 2026-06-27
- Feature: app/configuration
- Related code: `TeslaPCInterface/AppSettings.cs`, `TeslaPCInterface/WebServer.cs`,
  `TeslaPCInterface/MainForm.cs`, `TeslaPCInterface/config.html`,
  `TeslaPCInterface/TeslaPCInterface.csproj`

## Context

There was no way for a connected browser or external tool to determine which version of
TeslaPC was running without examining the binary directly. As the project began receiving
git tags and feature releases, surfacing the version in the UI and over HTTP became useful
for support (confirming which build is deployed) and for the web Settings page footer.

## Decisions

### `<Version>` in the `.csproj`

The version is set via the standard MSBuild `<Version>` property in
`TeslaPCInterface.csproj`. This populates `AssemblyInformationalVersionAttribute` at build
time, which is what `AppSettings.Version` reads. Keeping the version in the `.csproj`
(rather than a separate constants file) is the idiomatic .NET approach and integrates
naturally with `dotnet build`, `dotnet publish`, and git-tag-based CI pipelines.

### `+git` suffix stripping

`dotnet` build tooling (and some CI systems) appends a `+<commit>` or `+<metadata>`
segment to the informational version. This suffix is meaningful for reproducibility but
clutters user-facing version labels. `AppSettings.Version` strips everything from the first
`+` onward before returning the string.

### `version` field in `GET /config`

Adding `version` to the existing `GET /config` response keeps the web Settings page's footer
simple — it can display the version from the same JSON payload it already fetches on load,
with no additional request. The field is read-only (there is no corresponding `POST /config`
field) and carries no security sensitivity.

### `GET /version` as a separate endpoint

A dedicated `/version` endpoint was added alongside the `version` field in `GET /config`
for external tooling and lightweight health checks that should not parse the full settings
object. The response is a minimal JSON object `{ "version": "1.0.0" }` handled directly in
`WebServer.HandleRequest` routing.

### WinForms label in the tab bar

A muted `v1.0.0` label is placed under the "TeslaPC" wordmark in `MainForm`'s tab bar.
It reads `AppSettings.Version` at form construction time. The label uses a subdued style
(small font, muted color) so it does not compete with the functional controls.

### Release tagging convention

The release convention (tag `v<Version>` at the released commit, e.g. `v1.0.0`) was
documented here and in `AGENTS.md` rather than in a separate release doc. Keeping it in
`AGENTS.md` ensures every agent and developer encounters it during normal workflow.

## Consequences

- **Positive:** Connected browsers and the WinForms panel always display the running version.
- **Positive:** `GET /version` gives CI/CD scripts a stable, minimal endpoint to confirm
  the expected binary is deployed.
- **No behavioral change** to any existing endpoint or setting. `POST /config` ignores the
  `version` field if submitted; `GET /config` response grows by one read-only field.
