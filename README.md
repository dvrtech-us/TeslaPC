# TeslaPC

Turn an old laptop into a full desktop you can use from a Tesla's built-in browser, parked or
in motion.

The laptop rides along in the car and runs one small program. The car connects to the
laptop's Wi-Fi hotspot, you open a web page in the car's browser, and the laptop's screen,
sound, touch, and keyboard are on the car's display. Anything the laptop can run, the car can
show: a full desktop browser, streaming sites, your own video files, documents, a terminal.

There is nothing to install in the car and no app to pair. It is a web page.

> **Passengers only while the car is moving.** A screen showing video or a desktop takes the
> driver's attention off the road, and watching one while driving is illegal in many places.
> The driver drives. TeslaPC is for the people in the other seats, and for the driver when
> parked.

## How it works

```
 laptop (Windows or Linux)                          car
┌──────────────────────────────┐   Wi-Fi hotspot  ┌───────────────────────┐
│ TeslaPC server               │◄────────────────►│ built-in web browser  │
│  screen  -> MJPEG video      │                  │  https://your.host    │
│  sound   -> PCM audio        │                  │        :8443          │
│  input   <- touch, keys      │                  └───────────────────────┘
│ Mobile Hotspot + 100.64.0.1  │
└──────────────┬───────────────┘
               │ laptop's own internet (phone hotspot, Starlink, home Wi-Fi)
               ▼
            internet
```

- **It is remote desktop in a web page.** This is not Microsoft's RDP protocol and needs no
  RDP client. The server sends the screen as a stream of JPEG images and the sound as raw
  audio over a WebSocket; the page sends touches and key presses back.
- **Why it works while driving.** The page is ordinary images and Web Audio, so it runs
  wherever the car's browser runs.
- **Why the odd address.** The car's browser refuses private network addresses such as
  `192.168.x.x`. TeslaPC turns on the laptop's Mobile Hotspot, gives the hotspot adapter the
  address `100.64.0.1`, and forwards ports `8080` and `8443` to itself.
- **Why HTTPS.** The car's browser treats the page like any other website. TeslaPC can get
  and renew a real Let's Encrypt certificate for your own hostname by itself (DNS-01 through
  Cloudflare), so the car shows no certificate warning.

## What you get

- The laptop's live screen at up to 1080p, with system audio.
- Touch (tap, drag, long-press for right-click) or mouse mode, an on-screen keyboard field,
  and scrolling.
- The Windows lock screen, sign-in screen, and elevation prompts, when run as a service.
- **Fit screen:** one tap sets the laptop's display mode and the stream size to suit the car's
  screen.
- A file browser and video player for a folder of your own videos, with resume and "watched"
  marks.
- A Settings page: stream resolution, input mode, hotspot on/off, video folder, HTTPS host.
- Starts with Windows as a service. With auto sign-in on the laptop, it is ready by the time
  the car has joined the hotspot.

## What you need

- A laptop with Windows 10 or 11 and Wi-Fi that supports Mobile Hotspot. Linux also works;
  see [Linux](#linux).
- An internet connection for the laptop (a phone hotspot or Starlink works). Windows will not
  start Mobile Hotspot without one.
- For a warning-free connection: a hostname you control on Cloudflare DNS, with an `A`
  record pointing at `100.64.0.1`, and a Cloudflare API token (Zone:Read and DNS:Edit).
- Optional: [ffmpeg](https://ffmpeg.org/) for the built-in video player.

## Set up the laptop

1. **Build** (Rust and the MSVC build tools on a Windows machine):

   ```powershell
   .\build.ps1
   ```

   The result is `teslapc\target\release\teslapc.exe`. The web pages are inside the binary.

2. **Copy** `teslapc.exe` to the laptop, for example `C:\Program Files\TeslaPC\`.

3. **Configure** `%ProgramData%\TeslaPC\.env`:

   ```
   TESLAPC_HTTPS_HOST=car.example.com
   TESLAPC_CF_TOKEN=<your Cloudflare API token>
   TESLAPC_ACME_EMAIL=you@example.com
   TESLAPC_VIDEO_ROOT=C:\video\
   ```

4. **Install and start the service** from an administrator prompt:

   ```powershell
   cd "C:\Program Files\TeslaPC"
   .\teslapc.exe service install
   sc.exe start TeslaPC
   ```

   The service starts at boot, turns the hotspot on, and restarts the server if it stops.
   `teslapc.exe service uninstall` removes it.

5. **Recommended:** set the laptop to sign in automatically and never sleep on power, and set
   the Mobile Hotspot name and password in Windows Settings.

Notes:

- Windows 11 Smart App Control blocks unsigned builds. Sign the binary or turn Smart App
  Control off on that laptop.
- Logs are `%ProgramData%\TeslaPC\teslapc.log` (server) and `teslapc-service.log`
  (service).
- To try it on a desk first: `teslapc.exe --localhost`, then open `http://127.0.0.1:8080/`.

## Use it in the car

1. Power on the laptop. Give it a minute to sign in and start the hotspot.
2. On the car's display, join the laptop's hotspot under Wi-Fi.
3. Open the browser and go to `https://car.example.com:8443/` (your hostname). Bookmark it.
4. Tap to connect. Tap **Fit screen** once so the picture fills the display.

Use **Files** to play videos from the laptop's video folder, and **Settings** to change the
stream resolution if the picture stutters.

## Settings

Set these in `.env`, or on the Settings page where one exists.

| Key | Default | Purpose |
|-----|---------|---------|
| `TESLAPC_HTTPS_HOST` | none | Hostname for the trusted certificate |
| `TESLAPC_CF_TOKEN` | none | Cloudflare API token for certificate issue and renewal |
| `TESLAPC_ACME_EMAIL` | none | Optional Let's Encrypt contact |
| `TESLAPC_VIDEO_ROOT` | `C:\video\` | Folder shown by the file browser |
| `TESLAPC_STREAM_HEIGHT` | `1080` | Largest picture height sent (`240`-`2160`) |
| `TESLAPC_INPUT_MODE` | `touch` | `touch` or `mouse` |
| `TESLAPC_LOG_LEVEL` | `info` | `error`, `warn`, `info`, `debug` |
| `TESLAPC_FFMPEG` | on `PATH` | ffmpeg executable or folder |
| `TESLAPC_BYPASS_IFACE` | empty | Linux only: interface that carries `100.64.0.1` |

Flags: `--localhost` (local testing, no hotspot or HTTPS), `--no-tesla-bypass` (skip the
`100.64.0.1` address), `--https-host <host>`.

## Linux

The same binary runs on Linux (X11 or Wayland through the desktop portal). `teslapc service
install` writes a systemd unit. Bringing up the Wi-Fi access point is left to you; set
`TESLAPC_BYPASS_IFACE` to the access point's interface and TeslaPC adds the address and the
forwarding rules. Linux has had less testing than Windows.

## Limits to know about

- **There is no password.** Anyone who joins the laptop's hotspot can see and control the
  laptop, including the sign-in screen. Use a strong hotspot password and do not expose ports
  `8080` or `8443` to any other network.
- The Windows service runs the server with full system rights so it can show the lock screen.
- Video is MJPEG. It is simple and works in the car's browser, and it uses more data than a
  modern video codec. Lower the stream resolution on a weak link.
- Sound and picture are kept close, not frame-locked.
- Some copy-protected video shows as a black rectangle.
- The laptop's display resolution changes when you tap Fit screen, and stays that way.

## For developers

```bash
cargo build --manifest-path teslapc/Cargo.toml
cargo test  --manifest-path teslapc/Cargo.toml
node teslapc/tests/js/pcm_player.test.js
```

[AGENTS.md](AGENTS.md) is the operating guide. Feature-by-feature documentation, behavior
baselines, and decision history are under [documentation/](documentation/README.md).

## Not affiliated with Tesla

This is an independent project. It is not made, endorsed, or supported by Tesla, Inc.
"Tesla" is a trademark of Tesla, Inc. and is used here only to say which car browser the
project works with.
