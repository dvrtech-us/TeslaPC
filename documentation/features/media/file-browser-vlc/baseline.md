# File Browser and Playback — Behavior Baseline

- `/list.html` for a missing folder is HTTP 200 and contains `doesn't exist`.
- Folder rows render before file rows.
- Paths outside the video root are not listed.
- `/media/status` is JSON with `ffmpeg`, `playing`, `paused`, `position`, `duration`, `file`.
- ffmpeg video args include `scale=-2:'min({height},ih)',fps=30` and `-c:v mjpeg -q:v 6`.
- Windows VLC waits `2000` ms after stopping `vlc.exe`, then launches `C:\Program Files\VideoLAN\VLC\vlc.exe` fullscreen.
- Linux without ffmpeg does not launch VLC or another player.
