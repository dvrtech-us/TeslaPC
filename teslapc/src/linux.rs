//! Linux desktop capture, uinput, Pulse/PipeWire monitor, nftables bypass, and systemd.

use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::os::unix::io::AsRawFd;
use std::os::unix::net::UnixStream;
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use x11rb::connection::{Connection, RequestConnection};
use x11rb::protocol::composite::{ConnectionExt as _, Redirect};
use x11rb::protocol::shm::ConnectionExt as _;
use x11rb::protocol::xproto::{ConnectionExt as _, ImageFormat};
use x11rb::rust_connection::RustConnection;

use crate::decisions::bypass::linux_bypass_plan;
use crate::decisions::capture::CaptureBackend;
use crate::decisions::display::{choose_display_query, display_set_args};
use crate::decisions::linux_input::{linux_abs_axes, linux_key_events, linux_pointer_events, linux_repeat_events, linux_wheel_events, EvdevEvent, BTN_LEFT, BTN_RIGHT, BTN_TOUCH, EV_ABS, EV_KEY, EV_REL, KEY_DELETE, REL_WHEEL};
use crate::decisions::portal::{create_session_message, parse_dbus_header, parse_object_path, parse_portal_response, pipewire_hello, select_sources_message, start_message, PortalResponse};
use crate::paths;
use crate::platform::Grab;
use crate::state::App;

pub fn grab(backend: CaptureBackend) -> Result<Grab, String> {
    match backend {
        CaptureBackend::Wgc => grab_portal(),
        CaptureBackend::Dxgi | CaptureBackend::Gdi => grab_x11(),
    }
}

fn is_wayland() -> bool {
    std::env::var_os("WAYLAND_DISPLAY").is_some() || std::env::var("XDG_SESSION_TYPE").ok().as_deref() == Some("wayland")
}

struct Feed {
    latest: Arc<Mutex<Option<Grab>>>,
    error: Arc<Mutex<String>>,
    node: u32,
    child: std::process::Child,
    announced: bool,
}

static FEED: Mutex<Option<Feed>> = Mutex::new(None);

fn grab_portal() -> Result<Grab, String> {
    if !is_wayland() {
        return Err("no Wayland session (WAYLAND_DISPLAY is unset) for PipeWire / xdg-desktop-portal ScreenCast".into());
    }
    if let Some(frame) = cached_frame(true) {
        return Ok(frame);
    }
    if FEED.lock().map(|slot| slot.is_none()).unwrap_or(true) {
        start_feed()?;
    }
    for _ in 0..100 {
        if let Some(frame) = cached_frame(false) {
            return Ok(frame);
        }
        thread::sleep(Duration::from_millis(20));
    }
    let (node, err) = FEED
        .lock()
        .ok()
        .and_then(|slot| {
            slot.as_ref().map(|feed| {
                let err = feed.error.lock().map(|text| text.clone()).unwrap_or_default();
                (feed.node, err)
            })
        })
        .unwrap_or((0, String::new()));
    Err(format!("PipeWire node {node} produced no PNG frame after ScreenCast Start: {err}"))
}

fn cached_frame(quiet: bool) -> Option<Grab> {
    let mut feed = FEED.lock().ok()?;
    let feed = feed.as_mut()?;
    let _ = feed.child.try_wait();
    let frame = feed.latest.lock().ok()?.as_ref().map(|frame| Grab {
        bgra: frame.bgra.clone(),
        width: frame.width,
        height: frame.height,
    })?;
    if !quiet && !feed.announced {
        println!("[Capture] PipeWire frame {}x{} node {}", frame.width, frame.height, feed.node);
        feed.announced = true;
    }
    Some(frame)
}

fn start_feed() -> Result<(), String> {
    let node = portal_node()?;
    let mut child = match Command::new("gst-launch-1.0")
        .args(["-q", "pipewiresrc", &format!("path={node}"), "!", "videoconvert", "!", "pngenc", "!", "fdsink"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
    {
        Ok(child) => child,
        Err(err) => return Err(format!("gst-launch-1.0 pipewiresrc path={node}: {err}; {}", pipewire_peek())),
    };
    let stdout = child.stdout.take().ok_or_else(|| format!("gst-launch-1.0 pipewiresrc path={node} returned no stdout; {}", pipewire_peek()))?;
    let latest = Arc::new(Mutex::new(None));
    let error = Arc::new(Mutex::new(String::new()));
    let latest_reader = Arc::clone(&latest);
    let error_reader = Arc::clone(&error);
    thread::spawn(move || pump_png(stdout, latest_reader, error_reader));
    if let Some(stderr) = child.stderr.take() {
        let error_log = Arc::clone(&error);
        thread::spawn(move || {
            let mut text = String::new();
            let _ = std::io::BufReader::new(stderr).read_to_string(&mut text);
            if !text.trim().is_empty() {
                if let Ok(mut slot) = error_log.lock() {
                    if slot.is_empty() {
                        *slot = text;
                    }
                }
            }
        });
    }
    if let Ok(mut slot) = FEED.lock() {
        *slot = Some(Feed { latest, error, node, child, announced: false });
    }
    Ok(())
}

fn pump_png(stdout: impl Read, latest: Arc<Mutex<Option<Grab>>>, error: Arc<Mutex<String>>) {
    let mut reader = std::io::BufReader::new(stdout);
    let mut acc = Vec::new();
    let mut buf = [0u8; 65536];
    loop {
        match reader.read(&mut buf) {
            Ok(0) => {
                if let Ok(mut slot) = error.lock() {
                    if slot.is_empty() {
                        slot.push_str("gst-launch stdout closed");
                    }
                }
                return;
            }
            Ok(n) => {
                acc.extend_from_slice(&buf[..n]);
                while let Some(png) = take_png(&mut acc) {
                    if let Ok(grab) = png_to_grab(&png) {
                        if let Ok(mut slot) = latest.lock() {
                            *slot = Some(grab);
                        }
                    }
                }
            }
            Err(err) => {
                if let Ok(mut slot) = error.lock() {
                    *slot = format!("gst-launch read: {err}");
                }
                return;
            }
        }
    }
}

fn take_png(acc: &mut Vec<u8>) -> Option<Vec<u8>> {
    let start = acc.windows(4).position(|mark| mark == [0x89, b'P', b'N', b'G'])?;
    if start > 0 {
        acc.drain(..start);
    }
    let iend = acc.windows(4).position(|mark| mark == *b"IEND")?;
    let end = iend + 8;
    if acc.len() < end {
        return None;
    }
    Some(acc.drain(..end).collect())
}

fn png_to_grab(bytes: &[u8]) -> Result<Grab, String> {
    let image = image::load_from_memory(bytes).map_err(|err| format!("png decode: {err}"))?;
    let rgba = image.to_rgba8();
    let (width, height) = rgba.dimensions();
    let mut bgra = Vec::with_capacity(rgba.len());
    for pixel in rgba.pixels() {
        bgra.extend_from_slice(&[pixel[2], pixel[1], pixel[0], pixel[3]]);
    }
    Ok(Grab { bgra, width: width as i32, height: height as i32 })
}

fn pipewire_peek() -> String {
    let runtime = std::env::var("XDG_RUNTIME_DIR").unwrap_or_default();
    let path = format!("{runtime}/pipewire-0");
    let mut sock = match UnixStream::connect(&path) {
        Ok(sock) => sock,
        Err(err) => return format!("pipewire-0 connect {path}: {err}"),
    };
    if let Err(err) = sock.write_all(&pipewire_hello(1)) {
        return format!("pipewire-0 write: {err}");
    }
    let _ = sock.set_read_timeout(Some(Duration::from_millis(400)));
    let mut buf = [0u8; 4096];
    match sock.read(&mut buf) {
        Ok(n) => format!("pipewire-0 read {n} bytes"),
        Err(err) => format!("pipewire-0 read: {err}"),
    }
}

struct Bus {
    stream: UnixStream,
    serial: u32,
}

struct Incoming {
    msg_type: u8,
    reply_serial: Option<u32>,
    path: Option<String>,
    member: Option<String>,
    body: Vec<u8>,
}

fn portal_node() -> Result<u32, String> {
    let mut bus = Bus::connect()?;
    let pid = std::process::id();
    let session_token = format!("teslapc{pid}");
    let request = bus.call(&create_session_message(bus.bump(), &session_token, &format!("h{pid}a")))?;
    let session = match bus.wait_response(&request)? {
        PortalResponse::Session(path) => path,
        PortalResponse::Failed(code) => return Err(format!("ScreenCast CreateSession response {code}")),
        other => return Err(format!("ScreenCast CreateSession response was {other:?}")),
    };
    let request = bus.call(&select_sources_message(bus.bump(), &session, &format!("h{pid}b")))?;
    match bus.wait_response(&request)? {
        PortalResponse::Failed(code) => return Err(format!("ScreenCast SelectSources response {code}")),
        PortalResponse::Empty | PortalResponse::Session(_) | PortalResponse::Node(_) => {}
    }
    let request = bus.call(&start_message(bus.bump(), &session, &format!("h{pid}c")))?;
    match bus.wait_response(&request)? {
        PortalResponse::Node(id) => Ok(id),
        PortalResponse::Failed(code) => Err(format!("ScreenCast Start response {code}")),
        PortalResponse::Empty => Err("ScreenCast Start response did not include a PipeWire node id".into()),
        PortalResponse::Session(path) => Err(format!("ScreenCast Start returned session {path}")),
    }
}

impl Bus {
    fn connect() -> Result<Self, String> {
        let addr = std::env::var("DBUS_SESSION_BUS_ADDRESS").map_err(|err| format!("DBUS_SESSION_BUS_ADDRESS: {err}"))?;
        let stream = connect_bus(&addr)?;
        stream.set_read_timeout(Some(Duration::from_secs(8))).map_err(|err| err.to_string())?;
        let _ = stream.set_write_timeout(Some(Duration::from_secs(8)));
        let uid = unsafe { libc::getuid() };
        let hex: String = uid.to_string().bytes().map(|byte| format!("{byte:02x}")).collect();
        stream_write(&stream, b"\0")?;
        stream_write(&stream, format!("AUTH EXTERNAL {hex}\r\n").as_bytes())?;
        let line = read_line(&stream)?;
        if line.starts_with("DATA") {
            stream_write(&stream, b"DATA\r\n")?;
            let again = read_line(&stream)?;
            if !again.starts_with("OK") {
                return Err(format!("dbus auth: {again}"));
            }
        } else if !line.starts_with("OK") {
            return Err(format!("dbus auth: {line}"));
        }
        stream_write(&stream, b"BEGIN\r\n")?;
        Ok(Self { stream, serial: 1 })
    }

    fn bump(&mut self) -> u32 {
        let serial = self.serial;
        self.serial = self.serial.wrapping_add(1).max(1);
        serial
    }

    fn call(&mut self, message: &[u8]) -> Result<String, String> {
        let serial = u32::from_le_bytes(message[8..12].try_into().unwrap());
        stream_write(&self.stream, message)?;
        let started = std::time::Instant::now();
        loop {
            if started.elapsed() > Duration::from_secs(8) {
                return Err("dbus method reply timed out".into());
            }
            let incoming = self.read_message()?;
            if incoming.reply_serial != Some(serial) {
                continue;
            }
            if incoming.msg_type == 3 {
                return Err(format!("dbus error: {}", String::from_utf8_lossy(&incoming.body)));
            }
            if incoming.msg_type == 2 {
                return parse_object_path(&incoming.body);
            }
        }
    }

    fn wait_response(&mut self, request_path: &str) -> Result<PortalResponse, String> {
        let started = std::time::Instant::now();
        loop {
            if started.elapsed() > Duration::from_secs(8) {
                return Err(format!("ScreenCast Response on {request_path} timed out"));
            }
            let incoming = self.read_message()?;
            if incoming.msg_type == 4 && incoming.member.as_deref() == Some("Response") && incoming.path.as_deref() == Some(request_path) {
                return parse_portal_response(&incoming.body);
            }
        }
    }

    fn read_message(&mut self) -> Result<Incoming, String> {
        let mut fixed = [0u8; 16];
        read_exact(&self.stream, &mut fixed)?;
        if fixed[0] != b'l' {
            return Err(format!("dbus endian {:02x} is not little-endian", fixed[0]));
        }
        let body_len = u32::from_le_bytes(fixed[4..8].try_into().unwrap()) as usize;
        let fields_len = u32::from_le_bytes(fixed[12..16].try_into().unwrap()) as usize;
        let mut fields = vec![0u8; fields_len];
        read_exact(&self.stream, &mut fields)?;
        let pad = (8 - ((16 + fields_len) % 8)) % 8;
        let mut padding = vec![0u8; pad];
        read_exact(&self.stream, &mut padding)?;
        let mut body = vec![0u8; body_len];
        read_exact(&self.stream, &mut body)?;
        let header = parse_dbus_header(&fields);
        Ok(Incoming {
            msg_type: fixed[1],
            reply_serial: header.reply_serial,
            path: header.path,
            member: header.member,
            body,
        })
    }
}

fn connect_bus(addr: &str) -> Result<UnixStream, String> {
    for part in addr.split(',') {
        if let Some(path) = part.strip_prefix("unix:path=") {
            return UnixStream::connect(path).map_err(|err| format!("session bus {path}: {err}"));
        }
        if let Some(name) = part.strip_prefix("unix:abstract=") {
            let address = std::os::unix::net::SocketAddr::from_abstract_name(name.as_bytes()).map_err(|err| format!("abstract bus {name}: {err}"))?;
            return UnixStream::connect_addr(&address).map_err(|err| format!("abstract bus {name}: {err}"));
        }
    }
    Err(format!("unsupported DBUS_SESSION_BUS_ADDRESS: {addr}"))
}

fn stream_write(mut stream: &UnixStream, bytes: &[u8]) -> Result<(), String> {
    stream.write_all(bytes).map_err(|err| format!("dbus write: {err}"))
}

fn read_exact(mut stream: &UnixStream, buf: &mut [u8]) -> Result<(), String> {
    std::io::Read::read_exact(&mut stream, buf).map_err(|err| format!("dbus read: {err}"))
}

fn read_line(stream: &UnixStream) -> Result<String, String> {
    let mut out = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        read_exact(stream, &mut byte)?;
        if byte[0] == b'\n' {
            break;
        }
        out.push(byte[0]);
    }
    Ok(String::from_utf8_lossy(&out).trim().to_string())
}

struct X11Live {
    conn: RustConnection,
    drawable: u32,
    width: u16,
    height: u16,
    shmseg: u32,
    map: *mut libc::c_void,
    size: usize,
    composite: bool,
}

impl Drop for X11Live {
    fn drop(&mut self) {
        unsafe {
            let _ = self.conn.shm_detach(self.shmseg).map(|cookie| cookie.check());
            let _ = self.conn.flush();
            libc::shmdt(self.map);
            if self.composite {
                let _ = self.conn.free_pixmap(self.drawable).map(|cookie| cookie.check());
                let _ = self.conn.flush();
            }
        }
    }
}

thread_local! {
    static X11: std::cell::RefCell<Option<X11Live>> = std::cell::RefCell::new(None);
}

fn grab_x11() -> Result<Grab, String> {
    if X11.with(|slot| slot.borrow().is_none()) {
        match open_x11() {
            Ok(live) => {
                let via = if live.composite { "XComposite+XShm" } else { "XShm" };
                println!("[Capture] {via} frame {}x{}", live.width, live.height);
                X11.with(|slot| *slot.borrow_mut() = Some(live));
            }
            Err(err) => return Err(err),
        }
    }
    let copied = X11.with(|slot| {
        let live = slot.borrow();
        let live = live.as_ref().ok_or_else(|| "X11 session dropped".to_string())?;
        copy_xshm(live)
    });
    if copied.is_err() {
        X11.with(|slot| *slot.borrow_mut() = None);
    }
    copied
}

fn open_x11() -> Result<X11Live, String> {
    let (conn, screen_num) = x11rb::connect(None).map_err(|err| format!("X11 connect failed: {err}"))?;
    let (root, width, height, depth) = {
        let screen = conn.setup().roots.get(screen_num).ok_or_else(|| "X11 screen index is missing".to_string())?;
        (screen.root, screen.width_in_pixels, screen.height_in_pixels, screen.root_depth)
    };
    if width == 0 || height == 0 {
        return Err(format!("X11 root is {width}x{height}"));
    }
    let shm_ready = conn.extension_information(x11rb::protocol::shm::X11_EXTENSION_NAME).map_err(|err| format!("MIT-SHM: {err}"))?;
    if shm_ready.is_none() {
        return Err("MIT-SHM extension is not present".into());
    }
    conn.shm_query_version().map_err(|err| format!("shm_query_version: {err}"))?.reply().map_err(|err| format!("shm_query_version: {err}"))?;
    let (drawable, composite) = match composite_pixmap(&conn, root) {
        Ok(pixmap) => (pixmap, true),
        Err(err) => {
            println!("[Capture] XComposite: {err}");
            (root, false)
        }
    };
    let bpp: usize = if depth > 16 { 4 } else if depth > 8 { 2 } else { 1 };
    let size = (width as usize) * (height as usize) * bpp;
    let id = unsafe { libc::shmget(libc::IPC_PRIVATE, size, libc::IPC_CREAT | 0o600) };
    if id < 0 {
        release_composite(&conn, drawable, composite);
        return Err(format!("shmget: {}", std::io::Error::last_os_error()));
    }
    let map = unsafe { libc::shmat(id, std::ptr::null(), 0) };
    if map as usize == usize::MAX {
        unsafe { libc::shmctl(id, libc::IPC_RMID, std::ptr::null_mut()) };
        release_composite(&conn, drawable, composite);
        return Err(format!("shmat: {}", std::io::Error::last_os_error()));
    }
    unsafe { libc::shmctl(id, libc::IPC_RMID, std::ptr::null_mut()) };
    let shmseg = conn.generate_id().map_err(|err| format!("shm seg id: {err}"))?;
    if let Err(err) = conn.shm_attach(shmseg, id as u32, false).and_then(|cookie| cookie.check()) {
        unsafe { libc::shmdt(map) };
        release_composite(&conn, drawable, composite);
        return Err(format!("shm_attach: {err}"));
    }
    Ok(X11Live { conn, drawable, width, height, shmseg, map, size, composite })
}

fn release_composite(conn: &RustConnection, drawable: u32, composite: bool) {
    if composite {
        let _ = conn.free_pixmap(drawable).map(|cookie| cookie.check());
        let _ = conn.flush();
    }
}

fn composite_pixmap(conn: &RustConnection, root: u32) -> Result<u32, String> {
    let present = conn.extension_information(x11rb::protocol::composite::X11_EXTENSION_NAME).map_err(|err| format!("composite extension: {err}"))?;
    if present.is_none() {
        return Err("XComposite extension is not present".into());
    }
    conn.composite_query_version(0, 4).map_err(|err| format!("composite_query_version: {err}"))?.reply().map_err(|err| format!("composite_query_version: {err}"))?;
    conn.composite_redirect_subwindows(root, Redirect::AUTOMATIC).map_err(|err| format!("composite_redirect_subwindows: {err}"))?.check().map_err(|err| format!("composite_redirect_subwindows: {err}"))?;
    let pixmap = conn.generate_id().map_err(|err| format!("composite pixmap id: {err}"))?;
    conn.composite_name_window_pixmap(root, pixmap).map_err(|err| format!("composite_name_window_pixmap: {err}"))?.check().map_err(|err| format!("composite_name_window_pixmap: {err}"))?;
    Ok(pixmap)
}

fn copy_xshm(live: &X11Live) -> Result<Grab, String> {
    let reply = live
        .conn
        .shm_get_image(live.drawable, 0, 0, live.width, live.height, u32::MAX, u8::from(ImageFormat::Z_PIXMAP), live.shmseg, 0)
        .map_err(|err| format!("shm_get_image: {err}"))?
        .reply()
        .map_err(|err| format!("shm_get_image: {err}"))?;
    let width = live.width as usize;
    let height = live.height as usize;
    let bpp = if live.size >= width * height * 4 { 4 } else if live.size >= width * height * 2 { 2 } else { 1 };
    let stride = width * bpp;
    let mut bgra = vec![0u8; width * height * 4];
    let available = (reply.size as usize).min(live.size);
    unsafe {
        let src = live.map as *const u8;
        for row in 0..height {
            let from = row * stride;
            if from >= available {
                break;
            }
            let take = stride.min(available - from);
            if bpp == 4 {
                std::ptr::copy_nonoverlapping(src.add(from), bgra.as_mut_ptr().add(row * width * 4), take.min(width * 4));
            } else {
                for col in 0..(take / bpp).min(width) {
                    let pixel = src.add(from + col * bpp);
                    let dest = bgra.as_mut_ptr().add((row * width + col) * 4);
                    std::ptr::copy_nonoverlapping(pixel, dest, bpp);
                    *dest.add(3) = 255;
                }
            }
        }
    }
    Ok(Grab { bgra, width: live.width as i32, height: live.height as i32 })
}

pub fn primary_size() -> (i32, i32) {
    if let Ok((conn, screen_num)) = x11rb::connect(None) {
        if let Some(screen) = conn.setup().roots.get(screen_num) {
            return (screen.width_in_pixels as i32, screen.height_in_pixels as i32);
        }
    }
    let query = choose_display_query("", &command_output("wlr-randr", &[]), true);
    query.map(|query| query.current).unwrap_or((1920, 1080))
}

static UINPUT: Mutex<Option<std::fs::File>> = Mutex::new(None);
static TOUCH_XY: Mutex<Option<(i32, i32)>> = Mutex::new(None);
static SCREEN: Mutex<(i32, i32)> = Mutex::new((1, 1));
static WHEEL_PENDING: Mutex<i32> = Mutex::new(0);

fn screen_size() -> (i32, i32) {
    SCREEN.lock().map(|slot| *slot).unwrap_or((1, 1))
}

pub fn probe_input() -> bool {
    let file = match OpenOptions::new().read(true).write(true).open("/dev/uinput") {
        Ok(file) => file,
        Err(err) => {
            println!("[Input] /dev/uinput is unavailable ({err}). Input is disabled for this launch.");
            return false;
        }
    };
    let screen = primary_size();
    if let Ok(mut slot) = SCREEN.lock() {
        *slot = screen;
    }
    if let Err(err) = setup_uinput(file.as_raw_fd(), screen.0, screen.1) {
        println!("[Input] uinput setup failed ({err}). Input is disabled for this launch.");
        return false;
    }
    if let Ok(mut slot) = UINPUT.lock() {
        *slot = Some(file);
    }
    true
}

pub fn inject_pointer(kind: &str, x: i32, y: i32, touch: bool) {
    if touch {
        if let Ok(mut slot) = TOUCH_XY.lock() {
            if kind == "up" {
                *slot = None;
            } else if kind == "down" || kind == "move" {
                *slot = Some((x, y));
            }
        }
    }
    write_events(&linux_pointer_events(kind, x, y, touch, screen_size()));
}

pub fn inject_wheel(delta: i32) {
    let pending = WHEEL_PENDING.lock().map(|slot| *slot).unwrap_or(0);
    let (events, rest) = linux_wheel_events(delta, pending);
    if let Ok(mut slot) = WHEEL_PENDING.lock() {
        *slot = rest;
    }
    write_events(&events);
}

pub fn inject_key(token: &str) {
    write_events(&linux_key_events(token));
}

pub fn end_contact() {
    let point = TOUCH_XY.lock().ok().and_then(|mut slot| slot.take()).unwrap_or((0, 0));
    write_events(&linux_pointer_events("up", point.0, point.1, true, screen_size()));
}

pub fn repeat_touch() {
    let Some((x, y)) = TOUCH_XY.lock().ok().and_then(|slot| *slot) else {
        return;
    };
    write_events(&linux_repeat_events(x, y, screen_size()));
}

fn write_events(events: &[EvdevEvent]) {
    if events.is_empty() {
        return;
    }
    let mut bytes = Vec::with_capacity(events.len() * std::mem::size_of::<libc::input_event>());
    for event in events {
        let mut raw: libc::input_event = unsafe { std::mem::zeroed() };
        raw.type_ = event.type_;
        raw.code = event.code;
        raw.value = event.value;
        let ptr = &raw as *const libc::input_event as *const u8;
        bytes.extend_from_slice(unsafe { std::slice::from_raw_parts(ptr, std::mem::size_of::<libc::input_event>()) });
    }
    let Ok(mut slot) = UINPUT.lock() else {
        return;
    };
    let Some(file) = slot.as_mut() else {
        return;
    };
    let _ = file.write_all(&bytes);
}

fn setup_uinput(fd: libc::c_int, screen_w: i32, screen_h: i32) -> Result<(), String> {
    ioctl_val(fd, ui_set_evbit(), EV_KEY as libc::c_int).map_err(|err| format!("UI_SET_EVBIT EV_KEY: {err}"))?;
    ioctl_val(fd, ui_set_evbit(), EV_ABS as libc::c_int).map_err(|err| format!("UI_SET_EVBIT EV_ABS: {err}"))?;
    ioctl_val(fd, ui_set_evbit(), EV_REL as libc::c_int).map_err(|err| format!("UI_SET_EVBIT EV_REL: {err}"))?;
    ioctl_val(fd, ui_set_relbit(), REL_WHEEL as libc::c_int).map_err(|err| format!("UI_SET_RELBIT REL_WHEEL: {err}"))?;
    for code in [BTN_LEFT, BTN_RIGHT, BTN_TOUCH] {
        ioctl_val(fd, ui_set_keybit(), code as libc::c_int).map_err(|err| format!("UI_SET_KEYBIT {code}: {err}"))?;
    }
    for code in 1..=KEY_DELETE {
        ioctl_val(fd, ui_set_keybit(), code as libc::c_int).map_err(|err| format!("UI_SET_KEYBIT {code}: {err}"))?;
    }
    for axis in linux_abs_axes(screen_w, screen_h) {
        let code = axis.code;
        ioctl_val(fd, ui_set_absbit(), code as libc::c_int).map_err(|err| format!("UI_SET_ABSBIT {code}: {err}"))?;
        let setup = UinputAbsSetup { code, _pad: 0, value: 0, minimum: axis.minimum, maximum: axis.maximum, fuzz: 0, flat: 0, resolution: 0 };
        ioctl_ptr(fd, ui_abs_setup(), &setup).map_err(|err| format!("UI_ABS_SETUP {code}: {err}"))?;
    }
    let mut setup = UinputSetup { bustype: 0x06, vendor: 0x1234, product: 0x5678, version: 1, name: [0; 80], ff_effects_max: 0 };
    let name = b"TeslaPC";
    setup.name[..name.len()].copy_from_slice(name);
    ioctl_ptr(fd, ui_dev_setup(), &setup).map_err(|err| format!("UI_DEV_SETUP: {err}"))?;
    ioctl_val(fd, ui_dev_create(), 0).map_err(|err| format!("UI_DEV_CREATE: {err}"))?;
    Ok(())
}

#[repr(C)]
struct UinputSetup {
    bustype: u16,
    vendor: u16,
    product: u16,
    version: u16,
    name: [u8; 80],
    ff_effects_max: u32,
}

#[repr(C)]
struct UinputAbsSetup {
    code: u16,
    _pad: u16,
    value: i32,
    minimum: i32,
    maximum: i32,
    fuzz: i32,
    flat: i32,
    resolution: i32,
}

fn ui_ioc(dir: u32, nr: u32, size: u32) -> libc::Ioctl {
    ((dir << 30) | (size << 16) | ((b'U' as u32) << 8) | nr) as libc::Ioctl
}

fn ui_set_evbit() -> libc::Ioctl {
    ui_ioc(1, 100, std::mem::size_of::<libc::c_int>() as u32)
}
fn ui_set_keybit() -> libc::Ioctl {
    ui_ioc(1, 101, std::mem::size_of::<libc::c_int>() as u32)
}
fn ui_set_relbit() -> libc::Ioctl {
    ui_ioc(1, 102, std::mem::size_of::<libc::c_int>() as u32)
}
fn ui_set_absbit() -> libc::Ioctl {
    ui_ioc(1, 103, std::mem::size_of::<libc::c_int>() as u32)
}
fn ui_dev_create() -> libc::Ioctl {
    ui_ioc(0, 1, 0)
}
fn ui_dev_setup() -> libc::Ioctl {
    ui_ioc(1, 3, std::mem::size_of::<UinputSetup>() as u32)
}
fn ui_abs_setup() -> libc::Ioctl {
    ui_ioc(1, 4, std::mem::size_of::<UinputAbsSetup>() as u32)
}

fn ioctl_val(fd: libc::c_int, request: libc::Ioctl, value: libc::c_int) -> Result<(), String> {
    let rc = unsafe { libc::ioctl(fd, request, value) };
    if rc < 0 { Err(std::io::Error::last_os_error().to_string()) } else { Ok(()) }
}

fn ioctl_ptr<T>(fd: libc::c_int, request: libc::Ioctl, value: &T) -> Result<(), String> {
    let rc = unsafe { libc::ioctl(fd, request, value as *const T) };
    if rc < 0 { Err(std::io::Error::last_os_error().to_string()) } else { Ok(()) }
}

pub fn start_audio(app: App) {
    std::thread::spawn(move || {
        let monitor = Command::new("pactl").args(["get-default-sink"]).output().ok().and_then(|output| {
            let name = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if name.is_empty() { None } else { Some(format!("{name}.monitor")) }
        });
        let Some(monitor) = monitor else {
            println!("[Audio] No PulseAudio or PipeWire default sink monitor.");
            return;
        };
        println!("[Audio] Loopback monitor {monitor}");
        let mut child = match Command::new("parecord")
            .args(["--device", &monitor, "--raw", "--format=float32le", "--rate=48000", "--channels=2"])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
        {
            Ok(child) => child,
            Err(err) => {
                println!("[Audio] parecord failed: {err}");
                return;
            }
        };
        app.inner.pcm.capturing.store(true, std::sync::atomic::Ordering::Relaxed);
        app.inner.pcm.set_sample_kind(32, true);
        if let Ok(mut fmt) = app.inner.audio_format.lock() {
            *fmt = crate::state::AudioFormat::from_wave(48000, 32, 2, true);
        }
        let Some(stdout) = child.stdout.take() else { return };
        let mut reader = std::io::BufReader::new(stdout);
        let mut buf = [0u8; 3840];
        loop {
            match reader.read(&mut buf) {
                Ok(0) | Err(_) => break,
                Ok(n) => {
                    if !app.inner.pcm.media_mode.load(std::sync::atomic::Ordering::Relaxed) {
                        app.inner.pcm.push(bytes::Bytes::copy_from_slice(&buf[..n]), true);
                    }
                }
            }
        }
    });
}

pub fn display_snapshot() -> (bool, i32, i32, Vec<(i32, i32)>) {
    let xrandr = command_output("xrandr", &["--query"]);
    let wlr = command_output("wlr-randr", &[]);
    if let Some(query) = choose_display_query(&xrandr, &wlr, is_wayland()) {
        return (false, query.current.0, query.current.1, query.modes);
    }
    let (width, height) = primary_size();
    (false, width, height, vec![(width, height)])
}

pub fn try_set_mode(width: i32, height: i32) -> bool {
    let xrandr = command_output("xrandr", &["--query"]);
    let wlr = command_output("wlr-randr", &[]);
    let wayland = is_wayland();
    let Some(query) = choose_display_query(&xrandr, &wlr, wayland) else {
        return false;
    };
    let use_wlr = wayland && crate::decisions::display::parse_wlr_randr(&wlr).is_some();
    let args = display_set_args(use_wlr, &query.output, width, height);
    let Some((bin, rest)) = args.split_first() else {
        return false;
    };
    Command::new(bin).args(rest).status().map(|status| status.success()).unwrap_or(false)
}

fn command_output(bin: &str, args: &[&str]) -> String {
    Command::new(bin).args(args).output().map(|output| String::from_utf8_lossy(&output.stdout).into_owned()).unwrap_or_default()
}

pub fn service_install() -> i32 {
    let Ok(exe) = std::env::current_exe() else {
        println!("[Service] Could not locate the TeslaPC executable.");
        return 1;
    };
    let unit = paths::linux_systemd_unit(&exe.display().to_string());
    let path = "/etc/systemd/system/teslapc.service";
    if let Err(err) = std::fs::write(path, unit) {
        println!("[Service] Could not write {path}: {err}");
        return 1;
    }
    let status = Command::new("systemctl").args(["daemon-reload"]).status();
    match status {
        Ok(code) if code.success() => {
            let _ = Command::new("systemctl").args(["enable", "teslapc.service"]).status();
            println!("[Service] Installed teslapc.service");
            0
        }
        Ok(code) => {
            println!("[Service] systemctl failed ({code})");
            code.code().unwrap_or(1)
        }
        Err(err) => {
            println!("[Service] {err}");
            1
        }
    }
}

pub fn start_bypass(app: App) {
    let iface = std::env::var("TESLAPC_BYPASS_IFACE").ok();
    let plan = linux_bypass_plan(app.inner.no_bypass, iface.as_deref(), 8080, 8443);
    if !plan.enabled {
        println!("[Bypass] Linux bypass is off.");
        return;
    }
    let addr = Command::new("ip").args(["addr", "add", &plan.address_cidr, "dev", &plan.iface]).output();
    match addr {
        Ok(out) if out.status.success() => println!("[Bypass] Added {} on {}", plan.address_cidr, plan.iface),
        Ok(out) => println!("[Bypass] address failed: {}", String::from_utf8_lossy(&out.stderr)),
        Err(err) => println!("[Bypass] {err}"),
    }
    match Command::new("nft").arg("-f").arg("-").stdin(Stdio::piped()).spawn() {
        Ok(mut child) => {
            if let Some(mut stdin) = child.stdin.take() {
                let _ = stdin.write_all(plan.nft_script.as_bytes());
            }
            match child.wait() {
                Ok(status) if status.success() => println!("[Bypass] nftables forward and NAT installed on {}", plan.iface),
                Ok(status) => println!("[Bypass] nft failed ({status})"),
                Err(err) => println!("[Bypass] {err}"),
            }
        }
        Err(err) => println!("[Bypass] nftables unavailable: {err}"),
    }
}
