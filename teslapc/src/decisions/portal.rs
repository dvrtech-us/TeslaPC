//! xdg-desktop-portal ScreenCast messages and the PipeWire hello header.
//! `linux.rs` writes these bytes on the session bus and the PipeWire socket.

/// Little-endian PipeWire native header: core id 0, Hello opcode, empty payload.
/// Wire layout is id, `(opcode << 24) | size`, seq, n_fds.
pub fn pipewire_hello(seq: u32) -> [u8; 16] {
    let mut buf = [0u8; 16];
    buf[8..12].copy_from_slice(&seq.to_le_bytes());
    buf
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PortalResponse {
    Failed(u32),
    Session(String),
    Node(u32),
    Empty,
}

pub fn create_session_message(serial: u32, session_token: &str, handle_token: &str) -> Vec<u8> {
    let body = dict_body(&[("session_handle_token", DictValue::Str(session_token)), ("handle_token", DictValue::Str(handle_token))]);
    encode_method_call(serial, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast", "CreateSession", "a{sv}", &body)
}

pub fn select_sources_message(serial: u32, session_path: &str, handle_token: &str) -> Vec<u8> {
    let mut body = Writer::new();
    body.string(session_path);
    body.dict(&[
        ("types", DictValue::U32(1)),
        ("multiple", DictValue::Bool(false)),
        ("cursor_mode", DictValue::U32(1)),
        ("handle_token", DictValue::Str(handle_token)),
    ]);
    encode_method_call(serial, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast", "SelectSources", "oa{sv}", &body.buf)
}

pub fn start_message(serial: u32, session_path: &str, handle_token: &str) -> Vec<u8> {
    let mut body = Writer::new();
    body.string(session_path);
    body.string("");
    body.dict(&[("handle_token", DictValue::Str(handle_token))]);
    encode_method_call(serial, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.ScreenCast", "Start", "osa{sv}", &body.buf)
}

/// Build a `ua{sv}` Response body. Used so the parser and the encoder agree,
/// and so tests can feed the parser a body this function did not invent by hand.
pub fn portal_response_body(code: u32, session: Option<&str>, node: Option<u32>) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(code);
    w.array(8, |w| {
        if let Some(session) = session {
            w.align(8);
            w.string("session_handle");
            w.signature("o");
            w.string(session);
        }
        if let Some(node) = node {
            w.align(8);
            w.string("streams");
            w.signature("a(ua{sv})");
            w.array(8, |w| {
                w.align(8);
                w.u32(node);
                w.array(8, |_| {});
            });
        }
    });
    w.buf
}

#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct DbusHeader {
    pub path: Option<String>,
    pub member: Option<String>,
    pub reply_serial: Option<u32>,
    pub signature: Option<String>,
}

/// Header-field bytes from a dbus message (the array that starts at offset 16).
pub fn parse_dbus_header(buf: &[u8]) -> DbusHeader {
    let mut reader = Reader::new(buf);
    let mut header = DbusHeader::default();
    while reader.pos < buf.len() {
        reader.align(8);
        if reader.pos >= buf.len() {
            break;
        }
        let Ok(code) = reader.u8() else { break };
        if code == 0 {
            break;
        }
        let Ok(sig) = reader.signature() else { break };
        match (code, sig.as_str()) {
            (1, "o") => header.path = reader.string().ok(),
            (3, "s") => header.member = reader.string().ok(),
            (2, "s") | (6, "s") | (7, "s") => {
                if reader.string().is_err() {
                    break;
                }
            }
            (5, "u") => header.reply_serial = reader.u32().ok(),
            (8, "g") => header.signature = reader.signature().ok(),
            _ => {
                if reader.skip_value(&sig).is_err() {
                    break;
                }
            }
        }
    }
    header
}

pub fn parse_object_path(body: &[u8]) -> Result<String, String> {
    Reader::new(body).string()
}

pub fn parse_portal_response(body: &[u8]) -> Result<PortalResponse, String> {
    let mut r = Reader::new(body);
    let code = r.u32()?;
    if code != 0 {
        return Ok(PortalResponse::Failed(code));
    }
    let end = r.array_begin(8)?;
    let mut session = None;
    let mut node = None;
    while r.pos < end {
        r.align(8);
        if r.pos >= end {
            break;
        }
        let key = r.string()?;
        let sig = r.signature()?;
        match (key.as_str(), sig.as_str()) {
            ("session_handle", "o") | ("session_handle", "s") => session = Some(r.string()?),
            ("streams", "a(ua{sv})") => node = r.first_stream_node()?,
            _ => r.skip_value(&sig)?,
        }
    }
    if let Some(path) = session {
        return Ok(PortalResponse::Session(path));
    }
    if let Some(id) = node {
        return Ok(PortalResponse::Node(id));
    }
    Ok(PortalResponse::Empty)
}

pub fn encode_method_call(serial: u32, destination: &str, path: &str, interface: &str, member: &str, signature: &str, body: &[u8]) -> Vec<u8> {
    let mut fields = Writer::new();
    fields.align(8);
    fields.u8(1);
    fields.signature("o");
    fields.string(path);
    fields.align(8);
    fields.u8(2);
    fields.signature("s");
    fields.string(interface);
    fields.align(8);
    fields.u8(3);
    fields.signature("s");
    fields.string(member);
    fields.align(8);
    fields.u8(6);
    fields.signature("s");
    fields.string(destination);
    if !signature.is_empty() {
        fields.align(8);
        fields.u8(8);
        fields.signature("g");
        fields.signature(signature);
    }
    let mut msg = Writer::new();
    msg.u8(b'l');
    msg.u8(1);
    msg.u8(0);
    msg.u8(1);
    msg.u32(body.len() as u32);
    msg.u32(serial);
    msg.u32(fields.buf.len() as u32);
    msg.buf.extend_from_slice(&fields.buf);
    while msg.buf.len() % 8 != 0 {
        msg.buf.push(0);
    }
    msg.buf.extend_from_slice(body);
    msg.buf
}

enum DictValue<'a> {
    Str(&'a str),
    U32(u32),
    Bool(bool),
}

fn dict_body(entries: &[(&str, DictValue<'_>)]) -> Vec<u8> {
    let mut w = Writer::new();
    w.dict(entries);
    w.buf
}

struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    fn new() -> Self {
        Self { buf: Vec::new() }
    }

    fn align(&mut self, n: usize) {
        while self.buf.len() % n != 0 {
            self.buf.push(0);
        }
    }

    fn u8(&mut self, value: u8) {
        self.buf.push(value);
    }

    fn u32(&mut self, value: u32) {
        self.align(4);
        self.buf.extend_from_slice(&value.to_le_bytes());
    }

    fn string(&mut self, value: &str) {
        self.u32(value.len() as u32);
        self.buf.extend_from_slice(value.as_bytes());
        self.buf.push(0);
    }

    fn signature(&mut self, value: &str) {
        self.u8(value.len() as u8);
        self.buf.extend_from_slice(value.as_bytes());
        self.buf.push(0);
    }

    fn array(&mut self, elem_align: usize, fill: impl FnOnce(&mut Writer)) {
        self.align(4);
        let len_at = self.buf.len();
        self.buf.extend_from_slice(&0u32.to_le_bytes());
        self.align(elem_align);
        let start = self.buf.len();
        fill(self);
        let len = (self.buf.len() - start) as u32;
        self.buf[len_at..len_at + 4].copy_from_slice(&len.to_le_bytes());
    }

    fn dict(&mut self, entries: &[(&str, DictValue<'_>)]) {
        self.array(8, |w| {
            for (key, value) in entries {
                w.align(8);
                w.string(key);
                match value {
                    DictValue::Str(text) => {
                        w.signature("s");
                        w.string(text);
                    }
                    DictValue::U32(n) => {
                        w.signature("u");
                        w.u32(*n);
                    }
                    DictValue::Bool(flag) => {
                        w.signature("b");
                        w.u32(if *flag { 1 } else { 0 });
                    }
                }
            }
        });
    }
}

struct Reader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    fn align(&mut self, n: usize) {
        let pad = (n - (self.pos % n)) % n;
        self.pos += pad;
    }

    fn bytes(&mut self, n: usize) -> Result<Vec<u8>, String> {
        if self.pos + n > self.buf.len() {
            return Err("dbus body truncated".into());
        }
        let out = self.buf[self.pos..self.pos + n].to_vec();
        self.pos += n;
        Ok(out)
    }

    fn u8(&mut self) -> Result<u8, String> {
        Ok(self.bytes(1)?[0])
    }

    fn u32(&mut self) -> Result<u32, String> {
        self.align(4);
        let raw = self.bytes(4)?;
        Ok(u32::from_le_bytes(raw.try_into().unwrap()))
    }

    fn string(&mut self) -> Result<String, String> {
        let len = self.u32()? as usize;
        let bytes = self.bytes(len)?;
        self.u8()?;
        String::from_utf8(bytes).map_err(|err| err.to_string())
    }

    fn signature(&mut self) -> Result<String, String> {
        let len = self.u8()? as usize;
        let bytes = self.bytes(len)?;
        self.u8()?;
        String::from_utf8(bytes).map_err(|err| err.to_string())
    }

    fn array_begin(&mut self, elem_align: usize) -> Result<usize, String> {
        let len = self.u32()? as usize;
        self.align(elem_align);
        Ok(self.pos + len)
    }

    fn first_stream_node(&mut self) -> Result<Option<u32>, String> {
        let end = self.array_begin(8)?;
        let mut node = None;
        if self.pos < end {
            self.align(8);
            node = Some(self.u32()?);
            self.skip_value("a{sv}")?;
        }
        self.pos = end;
        Ok(node)
    }

    fn skip_value(&mut self, sig: &str) -> Result<(), String> {
        match sig {
            "s" | "o" => {
                self.string()?;
            }
            "g" => {
                self.signature()?;
            }
            "u" | "i" | "b" => {
                self.u32()?;
            }
            "y" => {
                self.u8()?;
            }
            "a{sv}" => {
                let end = self.array_begin(8)?;
                while self.pos < end {
                    self.align(8);
                    if self.pos >= end {
                        break;
                    }
                    self.string()?;
                    let inner = self.signature()?;
                    self.skip_value(&inner)?;
                }
                self.pos = end;
            }
            "a(ua{sv})" => {
                self.first_stream_node()?;
            }
            other => return Err(format!("unsupported dbus signature {other}")),
        }
        Ok(())
    }
}
