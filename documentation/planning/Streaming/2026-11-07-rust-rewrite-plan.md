# Rust rewrite plan for TeslaPC

- Date: 2026-11-07
- Status: plan only
- Destination: one Rust application is TeslaPC. The C# project is removed when that application covers the behavior in this document.

TeslaPC stays a browser-reachable remote desktop for the Tesla in-car browser. The host streams
the screen and system audio, and the browser sends pointer, scroll, and keyboard input back.
The rewrite is Rust. Windows and Linux are both targets of this plan. macOS is outside it.
This document does not design a macOS capture, audio, or input backend.

This document does not implement the port. The crate is added while `TeslaPCInterface/` still
builds, so each milestone can be run on its own. The C# app is the operator's server only
until the last milestone. That milestone deletes the C# project.

## Done

The rewrite is done when all of the following are true:

- One Rust binary serves the current browser UI and speaks the routes and message formats below.
- A Windows host can turn on the Mobile Hotspot, publish `100.64.0.1`, and accept the car on
  `8080` and `8443`.
- A logged-in Linux desktop can capture the screen, capture the speaker mix, inject touch,
  and reach the car through the same CGNAT address with Nat being done so the vehicle can still reach the internet.
- HTTPS renews a publicly trusted certificate when DNS is configured, and still comes up
  when it is not.
- Folder browse, ffmpeg playback, and the SQLite watch history work.
- `TeslaPCInterface/` (the C# project, the solution entry, and the `build.ps1` path that
  builds it) is removed. `AGENTS.md` and `CLAUDE.md` describe the Rust binary.

## Why rewrite

The shipping app is a .NET 10 WinForms process (`TeslaPCInterface/TeslaPCInterface.csproj`,
`TargetFramework` `net10.0-windows10.0.22000.0`, `SupportedOSPlatformVersion` `10.0.17763.0`).
Capture, audio, touch injection, hotspot control, and the TLS listener are Win32, WinRT, or
http.sys calls. A Linux host cannot grow inside that process. Rust is the language because
one codebase can call the Windows and Linux capture APIs, ship one binary, and keep the
browser UI.

## What this plan includes

- The full server the browser already talks to: screen, audio, input, settings, display fit,
  files, and playback.
- HTTPS with a publicly trusted certificate when the operator configures DNS, and a local
  certificate when they do not.
- The Tesla reachability path: Mobile Hotspot on Windows, `100.64.0.1`, and forwarding of
  `8080` and `8443`.
- A Windows firewall rule for those ports.
- Removal of the C# application at the end.
- A platform-independent UI control panel. Status and settings live on the web UI and on stdout.
- it needs to be able to run as a service

## Outside this plan

- macOS.
- A new video codec as a requirement for the first stream. Hardware H.264 is a later phase.
  The first stream stays MJPEG so the current page keeps working.
- Authentication. Network placement stays the only control. A later plan can add a token.

## Tesla Android: ideas only

Reference: [https://github.com/tesla-android](https://github.com/tesla-android).

Tesla Android is an idea reference for how a Tesla browser consumes a remote screen. It is
an Android system image for a single-board computer, plus a browser UI the car loads. It is
a Windows or Linux remote-desktop host in none of its services.

No Tesla Android code enters this repository. That covers source files, snippets, diffs,
translations into Rust, line-by-line ports, encoders, capture loops, WebSocket framing,
muxers, frontend scripts, and assets. Reading the projects to name a behavior is allowed.
Implementations in TeslaPC are written from the behavior the current C# app already has, and
from the three ideas below. TeslaPC's own `index.html` and related pages stay the client.

The public repositories are GPL-3.0. Confirmed on the GitHub license badge and `LICENSE`
files for, at least:

- [android-raspberry-pi](https://github.com/tesla-android/android-raspberry-pi)
- [android-external-tesla-android-virtual-display](https://github.com/tesla-android/android-external-tesla-android-virtual-display)
- [android-external-tesla-android-audio-relay](https://github.com/tesla-android/android-external-tesla-android-audio-relay)
- [frontend](https://github.com/tesla-android/frontend)

This plan does not accept GPL-3.0 for TeslaPC, and it does not vendor those repositories.

### Takeaway 1 — hardware capture and encode aimed at the Tesla browser

`android-external-tesla-android-virtual-display` captures a virtual display and encodes it
for the car browser. The encoder path can use the board's hardware encoder or fall back to
JPEG. The browser side (`frontend`, `js/display`) offers three renderers: Motion JPEG, H.264
via WebCodecs, and H.264 via the legacy Broadway decoder. That split exists because the
in-car browser's codec support is uneven. WebCodecs is a safe assumption on none of the cars
this app already serves.

For TeslaPC that means:

- Keep an MJPEG path the current `<img src="/stream">` client can paint with no decoder.
- A later hardware-encode path (H.264) gets its own route. MJPEG stays available when the
  car browser cannot decode the new stream.
- Windows hardware encode would be a Media Foundation step on a captured frame. Linux
  hardware encode would be VA-API or V4L2 M2M where the machine has it.
- The encoder, capture loop, and stream framing are written for TeslaPC. Their minicap
  capture, encoder, and WebSocket framing stay in their repository.

### Takeaway 2 — audio taken before the hardware sink and relayed to the browser

Tesla Android's release notes describe reading the mix inside the Android audio server before
that mix is sent to HDMI or a headphone jack. `android-external-tesla-android-audio-relay`
then accepts that PCM and relays it to the browser over a WebSocket.

For TeslaPC that means: capture the same mix the local speakers would play. Windows already
does this with WASAPI loopback (`AudioStreamingServer`). Linux captures the monitor source of
the default sink (PipeWire or PulseAudio). Keep a WebSocket to the browser. Their muxer stays
in their repository. The Rust audio path is raw PCM plus the format message
`PCMPlayerProcessor.js` already expects.

### Takeaway 3 — browser touch sent to the host

Tesla Android starts a virtual touchscreen beside the virtual display, and the web frontend
has a `js/touchscreen` module that ships browser touches over a WebSocket. The host receives
touch contacts.

That matches the current C# app: `TESLAPC_INPUT_MODE=touch` by default, `TouchInput` injects
one contact, and Windows press-and-hold is the right-click. The Rust port keeps that
contract. On Linux the contact is injected with the uinput multitouch interface so the
desktop's own press-and-hold can fire. The mouse fallback stays for a desktop browser.

## Behavior the replacement reproduces

"Keep" means the browser-visible behavior stays. The implementation behind it is new.

### Screen stream

`ImageStreamingServer` runs one capture thread while any client is connected to `GET /stream`.
The thread stops when the last client leaves. Backend order on Windows is `WgcScreenCapture`
(Windows Graphics Capture, cursor off where `IsCursorCaptureEnabled` exists, yellow border
off where `IsBorderRequired` exists, composited output including MPO video), then
`DxgiScreenCapture`, then GDI `CopyFromScreen`. `TESLAPC_NO_WGC=1` skips Windows Graphics
Capture. The target is the primary monitor.

JPEG quality is `60`. The pace is 30 FPS (`Interval = 1000 / fps`, so 33 ms). The height cap
comes from `TESLAPC_STREAM_HEIGHT` (default `1080`, allowed `240`–`2160`). The width cap is
`height * 4`, so height is the binding dimension. Scale is uniform and never upscales:
`scale = min(1, maxWidth / screenW, maxHeight / screenH)`. A slow client receives the latest
frame and skips intermediates (`TryWaitForLatestFrame` waits up to 1000 ms). Changing the
cap, a desktop mode switch, or a closed WGC capture item restarts the session without
dropping clients.

`MjpegWriter` frames `multipart/x-mixed-replace` with boundary `boundary`,
`Content-Type: image/jpeg` per part, and the no-cache headers the writer sets today.

The Rust host keeps that contract and that single shared frame buffer. Windows keeps the
three-backend chain. Linux uses PipeWire or xdg-desktop-portal ScreenCast on Wayland, and
XComposite or XShm on X11. JPEG is encoded in-process. DXGI, WGC, and GDI exist only in the
Windows modules.

Some video has appeared black, take reasonable steps to capture it correctly since we are aiming for accurate screen representation.

### System audio

`AudioStreamingServer` uses CSCore WASAPI loopback. Each `/ws/audio` client receives one JSON
text frame, then raw PCM binary frames:

```json
{
  "type": "format",
  "sampleRate": 48000,
  "bitsPerSample": 32,
  "channels": 2,
  "sampleFormat": "float"
}
```

`sampleFormat` is `float`, `pcm16`, `pcm24`, or `pcm32`, taken from the device. The client
resamples. Silence keepalive injects zero PCM after `250` ms of idle and stops after `30` s
(`SilenceKeepaliveStartMs`, `SilenceKeepaliveMaxSeconds`). The poll interval is `20` ms.

While a file is playing, `MediaMode` suppresses loopback and `MediaStreamer` pushes the
file's PCM through the same queue (`EnqueueMediaAudio`). Stopping playback restores loopback.

The Rust host keeps the WebSocket protocol. Windows uses WASAPI loopback. Linux uses the
PipeWire or PulseAudio monitor of the default sink. CSCore and NAudio go away with the C#
project.

### Pointer, wheel, and keyboard

`AppSettings.InputMode` defaults to `touch`. Any value other than `mouse` is touch.
`TESLAPC_INPUT_MODE` selects the mode, and `POST /config` applies it live.

The page opens `/ws/input`. Any WebSocket whose path does not start with `/ws/audio` is the
input socket today. The receive buffer is `4096` bytes. Pointer messages:

```json
{ "Type": "move|down|up|rightclick", "X": 820, "Y": 45, "DisplaySize": { "width": 1280, "height": 720 } }
{ "Type": "wheel", "Delta": -120, "X": 500, "Y": 300, "DisplaySize": { "width": 1280, "height": 720 } }
```

`InputData.GetAdjusted` scales `X` and `Y` from `DisplaySize` onto the primary screen.
`Delta` is not scaled. A missing `DisplaySize` passes coordinates through.

Touch mode (`TouchInput`): `down` starts one contact (`pointerId` 1), `move` updates it only
while it is down (hover is ignored), `up` ends it. A timer repeats an update every `100` ms
while the finger is down so Windows press-and-hold is not cancelled. Socket close calls
`TouchInput.EndContact`. An explicit `rightclick` message is still a mouse right button.
The page's `500` ms long-press (`LONG_PRESS_MS` in `index.html`) runs only in mouse mode and
sends that `rightclick` message.

Mouse mode: `SetCursorPos`, then `mouse_event` for left down, left up, or right down plus
right up.

Wheel is always a mouse wheel, in both modes. The server sends `MOUSEEVENTF_WHEEL` with the
sign of `Delta` inverted, after moving the pointer when `X`, `Y`, and `DisplaySize` are
present. The page already turns a two-finger vertical drag into one `wheel` message and
negates it for the natural touch direction. The host keeps both steps so the page can stay
unchanged. Scroll stays one wheel message, not a second injected finger.

Keyboard messages are `{ "Type": "key", "Key": "...", "KeyCode": "..." }`. `WebServer.handleKey`
calls `SendKeys.SendWait`. There is no server-side debounce. The page sends one message per
character, and `sendText` walks a paste one character at a time, mapping newlines to `Enter`
and tabs to `Tab`. Modifiers and lock keys are ignored (`Shift`, `Control`, `Alt`, `Meta`,
and the rest of the list in `index.html`). Named keys map to `SendKeys` tokens (`Backspace`,
`Enter`, `Tab`, `Escape`, `Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, arrows,
`F1`–`F12`). An unknown name is dropped. Single characters are typed, with the `SendKeys`
metacharacters `+ ^ % ~ ( ) { } [ ]` wrapped in `{}`. `keybd_event` is declared in
`Program.cs` and unused. The Rust host does not declare it.

Linux touch uses uinput `ABS_MT` slot 0, held for the life of the contact, so the desktop
press-and-hold (where the environment has one) is the right-click. Linux mouse fallback uses
uinput absolute pointer plus buttons. Linux wheel uses uinput `REL_WHEEL` at the last contact
position. Linux keys use uinput `EV_KEY` for named keys, and a Unicode path for typed text.
Windows keeps `InjectTouchInput` for press-and-hold, because that gesture is a Windows
setting.

uinput needs the input group or root. A missing privilege is logged and input is disabled
for that launch. The HTTP server still comes up.

### Tesla reachability

`TeslaBrowserBypass` adds `100.64.0.1` (`BypassIP`, CGNAT `100.64.0.0/10`) on the Windows
Mobile Hotspot adapter and portproxies `8080` and `8443` to localhost. The car browser
refuses RFC 1918 addresses such as `192.168.137.x`. `HotspotManager.EnsureHotspotOn` turns
the hotspot on through WinRT. If it was just turned on, startup waits `2000` ms before
`Setup`. `--no-tesla-bypass` skips the whole path.

`TeslaPcService.StartHotspotWatchdog` checks every `60` s. If the user turned the hotspot
off (`HotspotManager.ManuallyOff`), the watchdog leaves it off. Otherwise it turns the
hotspot back on and, when the address is missing, waits `2000` ms and calls `Setup` again.
`ToggleHotspot` on the control panel is the manual switch. The off wait before re-setup is
`1500` ms.

The Rust host keeps that outcome on Windows: hotspot on, `100.64.0.1`, portproxy, watchdog,
and a manual on/off. The button moves to the web Settings page, because the WinForms panel
goes away. A failed elevation logs and the server still serves HTTP.

On Linux the app adds `100.64.0.1/10` on the hotspot interface and forwards `8080` and
`8443` with nftables, when it has the privileges to do so. The interface name is a setting
(`TESLAPC_BYPASS_IFACE`). An empty value means the bypass stays off until the operator names
the interface. Bringing the access point up stays with NetworkManager or hostapd. There is
no WinRT hotspot API to port. `--no-tesla-bypass` skips the address and the forward rules on
both systems. A host that is already reached by a public name can leave the bypass off.

nftables and adding the address need root. A missing privilege logs and the rest of the
server still starts.

### HTTPS

`SslCertificateBootstrap` binds a certificate to http.sys on `8443`. `AcmeCertificateManager`
issues and renews a Let's Encrypt certificate with Certes and a Cloudflare DNS-01 challenge
when `TESLAPC_HTTPS_HOST` (or `--https-host`) and `TESLAPC_CF_TOKEN` are set.
`TESLAPC_ACME_EMAIL` is the optional account contact. `TESLAPC_ACME_STAGING=1` uses the
staging directory. Renewal runs when fewer than `30` days remain (`RenewWhenDaysLeft`).
`TeslaPcService.StartRenewalLoop` checks every `12` hours and rebinds after a renewal.
`--localhost` skips firewall, ACME, and HTTPS.

The Rust host terminates TLS inside the listener with rustls. Certificates live as files
under the platform config directory (`%ProgramData%\TeslaPC\` on Windows,
`$XDG_CONFIG_HOME/teslapc/` on Linux, default `~/.config/teslapc/`). There is no http.sys
URL ACL and no certificate-store bind. Port `8443` is above 1024, so the listener itself
does not need elevation.

When a hostname and Cloudflare token are set, the same DNS-01 issuance and the same 30-day
renewal run in-process, and a renewed certificate is reloaded into rustls. Staging stays
behind `TESLAPC_ACME_STAGING=1`. When no public hostname is set, the server generates a
local certificate so HTTPS still listens. The car trusts the publicly issued certificate.
The local certificate is for a browser on the LAN.

An operator who already has a working http.sys certificate exports or re-issues once. rustls
does not read that binding.

### Firewall

`FirewallBootstrap` adds an inbound Windows Firewall rule for `8080` and `8443` unless
`--localhost` was passed. Failure is non-fatal.

The Rust host does the same on Windows: add the rule, log and continue when elevation is
missing. On Linux it logs the two ports and does not shell out to a distro firewall.

### Settings

`AppSettings` reads and writes `%ProgramData%\TeslaPC\.env`. `Save` updates the named keys
and leaves every other line in place. The Cloudflare token is never returned by `GET /config`
(the payload has `cfTokenSet`). A blank `cfToken` on `POST /config` keeps the stored token.

| Key                     | Meaning                                                                                     | Applied              |
| ----------------------- | ------------------------------------------------------------------------------------------- | -------------------- |
| `TESLAPC_HTTPS_HOST`    | Certificate hostname. `--https-host` overrides for one launch.                              | Next start           |
| `TESLAPC_CF_TOKEN`      | Cloudflare token, Zone read and DNS edit                                                    | Next start           |
| `TESLAPC_ACME_EMAIL`    | Let's Encrypt contact                                                                       | Next start           |
| `TESLAPC_ACME_STAGING`  | `1` uses the staging directory                                                              | Next start           |
| `TESLAPC_VIDEO_ROOT`    | Browse root. Default `C:\video\`                                                            | Live                 |
| `TESLAPC_LOG_LEVEL`     | `error`, `warn`, `info` (default), `debug`. Also accepts `warning`, `verbose`, and `0`–`3`. | Live                 |
| `TESLAPC_STREAM_HEIGHT` | Cap height `240`–`2160`, default `1080`. Width cap becomes `height * 4`.                    | Live                 |
| `TESLAPC_INPUT_MODE`    | `touch` (default) or `mouse`                                                                | Live                 |
| `TESLAPC_NO_WGC`        | `1` skips Windows Graphics Capture                                                          | Next capture session |
| `TESLAPC_FFMPEG`        | Optional path to ffmpeg                                                                     | Next playback        |
| `TESLAPC_BYPASS_IFACE`  | Linux hotspot interface for `100.64.0.1`. Empty leaves the bypass off.                      | Next start           |

`GET /config` returns `httpsHost`, `acmeEmail`, `videoRoot`, `logLevel`, `streamHeight`,
`inputMode`, `version`, and `cfTokenSet`. `POST /config` is
`application/x-www-form-urlencoded` with `httpsHost`, `acmeEmail`, `videoRoot`, `cfToken`,
`logLevel`, `streamHeight`, and `inputMode`. The response is `{ "saved": true, "restartNeeded": true }`
when the host, token, or email changed.

Windows keeps `%ProgramData%\TeslaPC\.env` so an existing install still loads. Linux uses
`$XDG_CONFIG_HOME/teslapc/.env` (default `~/.config/teslapc/.env`). The browser Settings page
is the editor on every OS. The WinForms Config tab goes away with `MainForm`.

### Display fit

`GET /display/info` returns the current mode and the supported modes.
`GET /display/match?w=&h=` picks the supported mode nearest the viewport aspect, capped at
`max(480, stream height)`, and changes the desktop when the current mode differs.
`GET /display/set?w=&h=` sets a specific supported mode. A successful change calls
`RestartCapture`. The JSON body is `{ changed, width, height, modes: [{ w, h }] }`. The
resolution picker on the page is `480`, `720`, or `1080` and posts `streamHeight`. It does
not change the desktop mode. Fit screen does.

Windows keeps `DisplayManager`. Linux shells out to the compositor (`wlr-randr` or `xrandr`)
with the same "best mode near the stream height" rule. A refused change returns the current
mode and `changed: false`. The process keeps running.

### Files and playback

`/list.html` is generated by `WebServer`, not read from disk. `?path=` browses under
`TESLAPC_VIDEO_ROOT`. A missing or unreadable folder returns HTTP 200 and an empty-state
message, not a 500. Folders are listed before files. File rows carry the watched badge from
SQLite.

`/play.html` is the player page. `MediaStreamer` plays a file by spawning two ffmpeg
processes into the existing `/stream` and `/ws/audio` pipelines. Video arguments use
`-vf scale=-2:'min({maxH},ih)',fps=30` and `-c:v mjpeg -q:v 6`. Audio is resampled to the
announced device rate, channel count, and sample format. Playback resumes from the saved
position. A file is watched at `95%` of its duration (`MediaLibrary.WatchedFraction`).
`/media/stop` restores the live screen and loopback audio.

When ffmpeg is missing on Windows, `WebServer` kills `vlc.exe`, waits `2000` ms, and launches
`C:\Program Files\VideoLAN\VLC\vlc.exe` with `-vvv "<file>" --fullscreen`. The Rust host keeps
that Windows fallback. On Linux a missing ffmpeg returns an error on the player page and
does not launch another desktop player. `TESLAPC_FFMPEG` overrides the executable.

SQLite stays at `%ProgramData%\TeslaPC\teslapc.db` on Windows. Linux uses
`$XDG_DATA_HOME/teslapc/teslapc.db` (default `~/.local/share/teslapc/teslapc.db`). The table
is created in code, the same columns as `MediaLibrary.Initialize`:

| Column       | Type                            | Meaning                                     |
| ------------ | ------------------------------- | ------------------------------------------- |
| `path`       | TEXT PRIMARY KEY COLLATE NOCASE | File path                                   |
| `position`   | REAL                            | Last playback position, seconds             |
| `duration`   | REAL                            | Duration, seconds                           |
| `watched`    | INTEGER                         | Sticky. Set once position / duration ≥ 0.95 |
| `play_count` | INTEGER                         | Incremented on play                         |
| `updated_at` | TEXT                            | Last write                                  |

Media routes, all returning the player status JSON:

- `/media/play?path=`
- `/media/pause`
- `/media/resume`
- `/media/seek?t=` (seconds, invariant culture)
- `/media/stop`
- `/media/status`

The path must stay under the browse root.

### Web UI and the control panel

These files stay and are TeslaPC's UI: `index.html`, `config.html`, `play.html`,
`PCMPlayerProcessor.js`, `style.css`, `teslapc.png`. The page already tap-starts audio and
video together, offers the resolution picker, Fit screen, the disconnect overlay, and the
keyboard box. They can be moved into a different frontend framework if desired but functionality should remain on par if not better.

`/list.html` remains a generated page. Every other static file is served as raw bytes. The
C# server reads files with `File.ReadAllText`, so `teslapc.png` is already wrong. The Rust
server fixes that by writing the file bytes and the matching content type.

`MainForm` (dashboard, log tab, hotspot buttons, config fields) is not ported. Logs go to
stdout at the configured level. Hotspot on/off is added to `config.html`, because that
control currently exists only on the WinForms panel. The rest of the Settings form stays.

`GET /version` returns `{ "version": "<crate version>" }`. The same string is the `version`
field of `GET /config`. The version is the Cargo package version. Release tags stay
`v<version>`.

### Routes

| Route                                                               | Role                                                                                         |
| ------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `/`                                                                 | `index.html`                                                                                 |
| `/config.html`                                                      | Settings page                                                                                |
| `/play.html`                                                        | Player page                                                                                  |
| `/list.html`                                                        | Generated folder listing                                                                     |
| `/stream`                                                           | MJPEG                                                                                        |
| `/ws/audio`                                                         | Audio WebSocket                                                                              |
| `/ws/input`                                                         | Input WebSocket. Any other WebSocket path is also input, matching `WebServer.HandleRequest`. |
| `GET` and `POST /config`                                            | Settings JSON                                                                                |
| `GET /version`                                                      | `{ "version": "..." }`                                                                       |
| `GET /display/info`, `/display/match`, `/display/set`               | Desktop mode                                                                                 |
| `GET /media/play`, `/pause`, `/resume`, `/seek`, `/stop`, `/status` | Playback                                                                                     |

Ports stay `8080` (HTTP) and `8443` (HTTPS), so the car bookmark does not change.

There is no authentication on any route.

## Shape of the Rust host

The crate is `teslapc/` at the repository root, beside `TeslaPCInterface/`, until the last
milestone removes the C# tree. One binary. Platform code sits behind traits: `Capture`,
`AudioSource`, `PointerInject`, `KeyInject`, `HotspotBypass`.

Windows implementations live in `#[cfg(target_os = "windows")]` modules. Linux
implementations live in `#[cfg(target_os = "linux")]` modules. This plan has no
`target_os = "macos"` module.

Dependencies stay permissively licensed (MIT, Apache-2.0, BSD). No crate and no file from
the Tesla Android organization. No translation of their sources into this tree.

## Milestones

Each milestone is a slice of the replacement and can be launched while the C# app is still
what the car uses. The C# project is deleted only in the last milestone.

1. Byte-correct static files, generated `/list.html` shell, `GET`/`POST /config` with the
   keys above, and `GET /version`.
2. MJPEG `/stream`: Windows Graphics Capture, DXGI, then GDI on Windows. PipeWire or X11 on
   Linux. Shared frame buffer, quality 60, 30 FPS, height cap, aspect-preserving scale.
3. `/ws/audio`: WASAPI loopback and a Linux monitor source. Format frame, raw PCM, silence
   keepalive.
4. `/ws/input`: touch default, mouse fallback, wheel, keys, paste, and `DisplaySize` scaling.
5. rustls, DNS-01 renewal (30 days, 12-hour check, staging), the local certificate when no
   hostname is set, and the Windows firewall rule.
6. Windows hotspot, `100.64.0.1`, portproxy, 60-second watchdog, and the Settings on/off
   control. Linux address and nftables forward when `TESLAPC_BYPASS_IFACE` is set.
7. Display fit, SQLite history, ffmpeg playback, the Windows VLC fallback, then delete
   `TeslaPCInterface/` and point `build.ps1`, `AGENTS.md`, and `CLAUDE.md` at the Rust binary.

Hardware H.264 is a phase after this list. It gets a second route. MJPEG `/stream` stays.

## Risks

- Tesla Android is an idea reference. Its GPL-3.0 sources stay in their repositories. The
  three ideas above are the only thing this plan takes from that project, and each one is
  implemented with TeslaPC's own code. Their capture, audio, and touch programs are Android
  framework code and do not describe a Windows or Linux host.
- Wayland screen capture needs a portal consent dialog or a running PipeWire session. A
  headless Linux box with no desktop session will not stream a GUI. The Linux target is a
  logged-in desktop session.
- Linux press-and-hold depends on the desktop environment. GNOME and KDE differ. The mouse-mode
  500 ms right-click remains for environments that do not generate one from a touch contact.
- A rustls listener does not use the http.sys certificate the C# app binds. Operators with a
  working http.sys certificate export or re-issue once when they move to the Rust host.
- uinput, the Windows firewall rule, the CGNAT address, and nftables need privileges. Each
  one logs a failure and leaves the rest of the server running. `--localhost` still skips
  firewall, ACME, and HTTPS.
- Some video stays black on Windows under every capture API, if possible we attempt multiple capture methods to recover it.
