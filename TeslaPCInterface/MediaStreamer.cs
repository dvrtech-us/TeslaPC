using System.Diagnostics;
using System.Globalization;
using AudioStreamingServer;
using Streaming;

namespace Media;

/// <summary>
/// In-app media player. Decodes a local video file with <c>ffmpeg</c> and feeds the result into the
/// existing streams: JPEG frames into the MJPEG <c>/stream</c> (via <see cref="ImageStreamingServer"/>)
/// and PCM audio into <c>/ws/audio</c> (via <see cref="AudioCapture"/>).
///
/// Why not just play a &lt;video&gt; in the browser? The Tesla in-car browser blocks native video
/// decoding while the car is in motion. The MJPEG image stream + separate Web-Audio path is not
/// subject to that lockout, so it keeps playing while driving. This class produces those two streams
/// from a file directly — no VLC, no screen capture, works with the host display off, and gives us
/// real play/pause/seek control with managed A/V pacing.
///
/// Two ffmpeg processes run per playback, both paced at real time with <c>-re</c> and started at the
/// same <c>-ss</c> offset: one emits a raw concatenated-JPEG video stream, one emits raw PCM in the
/// audio device's exact format. Pause/seek/resume kill and respawn at the tracked position.
/// </summary>
public sealed class MediaStreamer : IDisposable
{
    // Output pacing/quality. The vertical cap is shared with the screen-capture path via
    // ImageStreamingServer.MaxHeight (configurable, default 1080), so media respects the same setting.
    private const int Fps = 30;
    private const int JpegQuality = 6; // ffmpeg -q:v (2 best .. 31 worst)

    private readonly ImageStreamingServer _img;
    private readonly AudioCapture _audio;
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;

    private readonly object _gate = new();
    private readonly Stopwatch _sw = new();
    private bool _playing;
    private bool _paused;
    private string? _file;
    private double _seekBase;       // playback position (seconds) at the last (re)start
    private double _duration;       // total length (seconds), 0 if unknown
    private long _gen;              // bumped on every (re)start/kill; readers use it to detect supersession
    private Process? _videoProc;
    private Process? _audioProc;

    /// <summary>The directory the file browser is rooted at; playback is restricted to files under it.</summary>
    public string Root { get; set; } = @"C:\video\";

    public MediaStreamer(ImageStreamingServer img, AudioCapture audio)
    {
        _img = img;
        _audio = audio;
        _ffmpeg = ResolveExe("ffmpeg");
        _ffprobe = ResolveExe("ffprobe");
        Console.WriteLine(_ffmpeg != null
            ? $"[Media] ffmpeg: {_ffmpeg}" + (_ffprobe != null ? $", ffprobe: {_ffprobe}" : " (ffprobe not found; no seek bar)")
            : "[Media] ffmpeg not found — falling back to VLC for playback. Set TESLAPC_FFMPEG or add ffmpeg to PATH.");
    }

    /// <summary>True when ffmpeg was located, so in-app playback is available (else use the VLC fallback).</summary>
    public bool IsFfmpegAvailable => _ffmpeg != null;

    public MediaStatus Status()
    {
        lock (_gate)
        {
            return new MediaStatus
            {
                ffmpeg = _ffmpeg != null,
                playing = _playing,
                paused = _paused,
                position = CurrentPosition(),
                duration = _duration,
                file = _file != null ? Path.GetFileName(_file) : null
            };
        }
    }

    /// <summary>
    /// Starts playing <paramref name="path"/> from the beginning. The path must be an existing file
    /// under <see cref="Root"/>. Returns false if ffmpeg is unavailable or the path is rejected.
    /// </summary>
    public bool Play(string path)
    {
        if (_ffmpeg == null)
            return false;
        if (!IsAllowed(path))
        {
            Console.WriteLine($"[Media] Refused to play (outside root or missing): {path}");
            return false;
        }

        lock (_gate)
        {
            KillCurrent();
            _file = path;
            _duration = ProbeDuration(path);
            _img.EnterMediaMode();
            _audio.MediaMode = true;
            StartFrom(0);
            Console.WriteLine($"[Media] Playing {Path.GetFileName(path)} ({_duration:0.0}s)");
        }
        return true;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing || _paused)
                return;
            _seekBase = CurrentPosition();
            _paused = true;
            _sw.Reset();
            KillCurrent();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_playing || !_paused)
                return;
            StartFrom(_seekBase);
        }
    }

    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (!_playing)
                return;
            double pos = Clamp(seconds);
            // Keep the target just shy of the end: seeking to exactly the duration makes ffmpeg
            // start past all frames, emit nothing, and instantly hit EOF — which would look like a
            // failed seek and revert to the live screen.
            if (_duration > 0 && pos > _duration - 1.0)
                pos = Math.Max(0, _duration - 1.0);
            KillCurrent();
            if (_paused)
                _seekBase = pos;        // resume will start here
            else
                StartFrom(pos);
            Console.WriteLine($"[Media] Seek to {pos:0.0}s");
        }
    }

    /// <summary>Stops playback and returns the streams to live screen capture / loopback audio.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            bool wasActive = _playing || _paused;
            KillCurrent();
            _playing = false;
            _paused = false;
            _file = null;
            _seekBase = 0;
            _duration = 0;
            _sw.Reset();
            if (wasActive)
            {
                _audio.MediaMode = false;
                _img.ExitMediaMode();
                Console.WriteLine("[Media] Stopped; live screen restored.");
            }
        }
    }

    // ---- internals -------------------------------------------------------------------------------

    private double CurrentPosition()
    {
        double pos = _seekBase + (_paused ? 0 : _sw.Elapsed.TotalSeconds);
        return Clamp(pos);
    }

    private double Clamp(double pos)
    {
        if (pos < 0) return 0;
        if (_duration > 0 && pos > _duration) return _duration;
        return pos;
    }

    /// <summary>Spawns the video+audio ffmpeg pair at <paramref name="start"/> seconds. Caller holds the lock.</summary>
    private void StartFrom(double start)
    {
        long gen = ++_gen;
        _seekBase = start;
        _paused = false;
        _playing = true;
        _sw.Restart();

        string ss = start.ToString("0.###", CultureInfo.InvariantCulture);
        string file = _file!;

        int maxH = _img.MaxHeight > 0 ? _img.MaxHeight : 1080;
        string videoArgs =
            $"-hide_banner -loglevel error -re -ss {ss} -i \"{file}\" -an " +
            $"-vf scale=-2:'min({maxH},ih)',fps={Fps} " +
            $"-c:v mjpeg -q:v {JpegQuality} -f image2pipe pipe:1";

        string fmt = FfmpegSampleFormat(_audio.SampleFormat);
        int rate = _audio.SampleRate > 0 ? _audio.SampleRate : 48000;
        int channels = _audio.Channels > 0 ? _audio.Channels : 2;
        string audioArgs =
            $"-hide_banner -loglevel error -re -ss {ss} -i \"{file}\" -vn " +
            $"-ar {rate} -ac {channels} -f {fmt} pipe:1";

        _videoProc = StartFfmpeg(videoArgs);
        _audioProc = StartFfmpeg(audioArgs);

        int frameBytes = channels * BytesPerSample(_audio.SampleFormat);
        var vStream = _videoProc.StandardOutput.BaseStream;
        var aStream = _audioProc.StandardOutput.BaseStream;
        new Thread(() => VideoReader(vStream, gen)) { IsBackground = true, Name = "MediaVideo" }.Start();
        new Thread(() => AudioReader(aStream, gen, frameBytes)) { IsBackground = true, Name = "MediaAudio" }.Start();
    }

    /// <summary>Kills the running ffmpeg pair and invalidates their reader threads. Caller holds the lock.</summary>
    private void KillCurrent()
    {
        _gen++; // any reader still draining an old process must not auto-stop or publish
        TryKill(ref _videoProc);
        TryKill(ref _audioProc);
    }

    private static void TryKill(ref Process? p)
    {
        if (p == null)
            return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
        p = null;
    }

    private Process StartFfmpeg(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg!,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        // Drain stderr on a background thread so the pipe can't fill and block ffmpeg.
        var err = p.StandardError;
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = err.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(line))
                        Console.WriteLine($"[Media/ffmpeg] {line}");
            }
            catch { }
        })
        { IsBackground = true, Name = "MediaFfmpegErr" }.Start();
        return p;
    }

    private void VideoReader(Stream stdout, long gen)
    {
        try
        {
            var split = new JpegSplitter();
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
            {
                if (Volatile.Read(ref _gen) != gen)
                    return; // superseded by a seek/stop; let the new readers take over
                split.Append(buf, n, frame =>
                {
                    if (Volatile.Read(ref _gen) == gen)
                        _img.PublishMediaFrame(frame);
                });
            }
        }
        catch { /* process killed or stream closed */ }
        finally
        {
            // EOF on the video stream with our generation still current means the file ended.
            EndOfStream(gen);
        }
    }

    private void AudioReader(Stream stdout, long gen, int frameBytes)
    {
        if (frameBytes <= 0)
            frameBytes = 4;
        try
        {
            byte[] buf = new byte[16 * 1024];
            int carry = 0; // bytes of a partial PCM frame held over to the next read
            int n;
            while ((n = stdout.Read(buf, carry, buf.Length - carry)) > 0)
            {
                if (Volatile.Read(ref _gen) != gen)
                    return;
                int total = carry + n;
                int whole = total - (total % frameBytes); // only emit frame-aligned PCM
                if (whole > 0)
                {
                    var chunk = new byte[whole];
                    Array.Copy(buf, 0, chunk, 0, whole);
                    if (Volatile.Read(ref _gen) == gen)
                        _audio.EnqueueMediaAudio(chunk);
                }
                carry = total - whole;
                if (carry > 0)
                    Array.Copy(buf, whole, buf, 0, carry);
            }
        }
        catch { /* process killed or stream closed */ }
    }

    /// <summary>Called when a video reader hits EOF; stops cleanly only if it wasn't superseded.</summary>
    private void EndOfStream(long gen)
    {
        lock (_gate)
        {
            if (gen != _gen || !_playing || _paused)
                return;
            _playing = false;
            _file = null;
            _seekBase = 0;
            _duration = 0;
            _sw.Reset();
            _audio.MediaMode = false;
            _img.ExitMediaMode();
            Console.WriteLine("[Media] Playback finished; live screen restored.");
        }
    }

    private bool IsAllowed(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(Root);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private double ProbeDuration(string file)
    {
        if (_ffprobe == null)
            return 0;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobe,
                Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{file}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return double.TryParse(outp, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    private static string FfmpegSampleFormat(string sampleFormat) => sampleFormat switch
    {
        "pcm16" => "s16le",
        "pcm24" => "s24le",
        "pcm32" => "s32le",
        _ => "f32le",
    };

    private static int BytesPerSample(string sampleFormat) => sampleFormat switch
    {
        "pcm16" => 2,
        "pcm24" => 3,
        "pcm32" => 4,
        _ => 4, // float
    };

    /// <summary>
    /// Locates ffmpeg/ffprobe. Order: <c>TESLAPC_FFMPEG</c> (file or dir), the app directory, then PATH.
    /// </summary>
    private static string? ResolveExe(string name)
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("TESLAPC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string? dir = File.Exists(env) ? Path.GetDirectoryName(env) : (Directory.Exists(env) ? env : Path.GetDirectoryName(env));
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, name + ".exe"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, name + ".exe"));
        candidates.Add(name); // resolved via PATH

        foreach (var c in candidates)
        {
            if (Path.IsPathRooted(c))
            {
                if (File.Exists(c))
                    return c;
            }
            else if (CanLaunch(c))
            {
                return c;
            }
        }
        return null;
    }

    private static bool CanLaunch(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    public void Dispose() => Stop();
}

/// <summary>Snapshot of the player state, serialized to JSON for the <c>/media/status</c> route.</summary>
public sealed class MediaStatus
{
    public bool ffmpeg { get; set; }
    public bool playing { get; set; }
    public bool paused { get; set; }
    public double position { get; set; }
    public double duration { get; set; }
    public string? file { get; set; }
}

/// <summary>
/// Splits a raw concatenated-JPEG byte stream (ffmpeg <c>image2pipe</c> output) into individual
/// frames by scanning for SOI (FF D8) … EOI (FF D9) markers. Holds a partial frame across reads.
/// </summary>
internal sealed class JpegSplitter
{
    private byte[] _buf = new byte[128 * 1024];
    private int _len;

    public void Append(byte[] data, int count, Action<byte[]> emit)
    {
        EnsureCapacity(_len + count);
        Array.Copy(data, 0, _buf, _len, count);
        _len += count;

        while (true)
        {
            int soi = FindMarker(0xD8, 0);
            if (soi < 0)
            {
                // No start marker yet; keep only a trailing byte (could be a split FF) to bound growth.
                if (_len > 1)
                    ShiftLeft(_len - 1);
                return;
            }
            if (soi > 0)
                ShiftLeft(soi); // drop bytes before the start marker

            int eoi = FindMarker(0xD9, 2);
            if (eoi < 0)
                return; // frame not complete yet

            int end = eoi + 2;
            var frame = new byte[end];
            Array.Copy(_buf, 0, frame, 0, end);
            emit(frame);
            ShiftLeft(end);
        }
    }

    private int FindMarker(byte second, int from)
    {
        for (int i = from; i < _len - 1; i++)
        {
            if (_buf[i] == 0xFF && _buf[i + 1] == second)
                return i;
        }
        return -1;
    }

    private void ShiftLeft(int count)
    {
        if (count <= 0)
            return;
        int remaining = _len - count;
        if (remaining > 0)
            Array.Copy(_buf, count, _buf, 0, remaining);
        _len = remaining;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
            return;
        int size = _buf.Length;
        while (size < needed)
            size *= 2;
        Array.Resize(ref _buf, size);
    }
}
