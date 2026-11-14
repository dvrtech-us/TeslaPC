//! WebSocket path classification. Anything that is not `/ws/audio` is the input socket.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SocketRole {
    Audio,
    Input,
}

pub fn classify_socket(path: &str) -> SocketRole {
    let path = path.split('?').next().unwrap_or(path);
    if path.to_ascii_lowercase().starts_with("/ws/audio") {
        SocketRole::Audio
    } else {
        SocketRole::Input
    }
}
