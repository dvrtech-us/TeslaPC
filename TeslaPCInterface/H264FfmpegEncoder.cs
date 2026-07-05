using System.Diagnostics;
using System.Text;

/// <summary>
/// Low-latency raw BGR24 → Annex-B H264 via a persistent ffmpeg pipe.
/// Requires ffmpeg on PATH or <c>TESLAPC_FFMPEG</c> (same resolution as <see cref="Media.MediaStreamer"/>).
/// </summary>
public sealed class H264FfmpegEncoder : IDisposable
{
    private readonly string? _ffmpeg;
    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private int _width;
    private int _height;
    private int _fps;
    private bool _disposed;
    private int _framesFed;
    private int _framesOut;
    private int _nullReads;
    private long _lastStatsUtc = Environment.TickCount64;

    public H264FfmpegEncoder()
    {
        _ffmpeg = ResolveFfmpeg();
    }

    public bool IsAvailable => _ffmpeg != null;

    public void Start(int width, int height, int fps)
    {
        if (_ffmpeg == null)
            throw new InvalidOperationException("ffmpeg not found — set TESLAPC_FFMPEG or add ffmpeg to PATH.");

        Stop();

        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _framesFed = 0;
        _framesOut = 0;
        _nullReads = 0;

        var args = new StringBuilder();
        args.Append("-hide_banner -loglevel error ");
        args.Append("-fflags nobuffer -flags low_delay ");
        args.Append(CultureInv($"-f rawvideo -pix_fmt bgr24 -s {width}x{height} -r {_fps} -i pipe:0 "));
        args.Append("-c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline ");
        args.Append(CultureInv($"-g {_fps} -keyint_min 1 -sc_threshold 0 "));
        args.Append("-x264-params repeat-headers=1 ");
        args.Append("-bsf:v h264_mp4toannexb -f h264 pipe:1");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            Arguments = args.ToString(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg H264 encoder.");

        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;

        Console.WriteLine($"[H264] Encoder started {_width}x{_height}@{_fps} via {_ffmpeg}");

        _ = Task.Run(async () =>
        {
            try
            {
                while (_process is { HasExited: false })
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        Console.WriteLine($"[H264/ffmpeg] {line}");
                }
            }
            catch { /* encoder shutting down */ }
        });
    }

    /// <summary>Feeds one tightly-packed BGR24 frame and returns the encoded Annex-B access unit, if any.</summary>
    public byte[]? EncodeFrame(byte[] bgr24)
    {
        if (_stdin == null || _stdout == null || _process is { HasExited: true })
            return null;

        int expected = _width * _height * 3;
        if (bgr24.Length < expected)
            return null;

        try
        {
            _stdin.Write(bgr24, 0, expected);
            _stdin.Flush();
            _framesFed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[H264] stdin write failed: {ex.Message}");
            return null;
        }

        var encoded = ReadEncodedFrame();
        if (encoded == null || encoded.Length == 0)
        {
            _nullReads++;
            MaybeLogStats();
            return null;
        }

        _framesOut++;
        MaybeLogStats();
        return encoded;
    }

    public void Stop()
    {
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { }
        _process?.Dispose();
        _process = null;
        _stdin = null;
        _stdout = null;
    }

    private void MaybeLogStats()
    {
        long now = Environment.TickCount64;
        if (now - _lastStatsUtc < 5000)
            return;
        _lastStatsUtc = now;
        Console.WriteLine($"[H264] fed={_framesFed} out={_framesOut} null={_nullReads}");
    }

    private byte[]? ReadEncodedFrame()
    {
        if (_stdout == null)
            return null;

        using var ms = new MemoryStream();
        var buf = new byte[65536];
        // First frames need SPS/PPS/IDR; allow more time on startup.
        int timeoutMs = _framesOut == 0 ? 500 : 150;
        long deadline = Environment.TickCount64 + timeoutMs;
        bool gotData = false;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                int remaining = Math.Max(1, (int)(deadline - Environment.TickCount64));
                var readTask = Task.Run(() => _stdout!.Read(buf, 0, buf.Length));
                if (!readTask.Wait(remaining))
                    break;

                int n = readTask.Result;
                if (n <= 0)
                    break;

                ms.Write(buf, 0, n);
                gotData = true;
                deadline = Environment.TickCount64 + 30;
            }
            catch
            {
                break;
            }
        }

        return gotData && ms.Length > 0 ? ms.ToArray() : null;
    }

    private static string CultureInv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string? ResolveFfmpeg()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("TESLAPC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string? dir = File.Exists(env) ? Path.GetDirectoryName(env) : (Directory.Exists(env) ? env : Path.GetDirectoryName(env));
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, "ffmpeg.exe"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidates.Add("ffmpeg");

        foreach (var c in candidates)
        {
            if (Path.IsPathRooted(c))
            {
                if (File.Exists(c))
                    return c;
            }
            else if (CanLaunch(c))
                return c;
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
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}