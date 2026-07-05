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

        var args = new StringBuilder();
        args.Append("-hide_banner -loglevel error ");
        args.Append(CultureInv($"-f rawvideo -pix_fmt bgr24 -s {width}x{height} -r {_fps} -i pipe:0 "));
        args.Append("-c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline ");
        args.Append(CultureInv($"-g {_fps} -keyint_min {_fps} -sc_threshold 0 "));
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

    /// <summary>Feeds one BGR24 frame and returns the encoded Annex-B access unit, if any.</summary>
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
        }
        catch
        {
            return null;
        }

        return ReadEncodedFrame();
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

    private byte[]? ReadEncodedFrame()
    {
        if (_stdout == null)
            return null;

        using var ms = new MemoryStream();
        var buf = new byte[65536];
        long deadline = Environment.TickCount64 + 120;
        bool gotData = false;

        while (Environment.TickCount64 < deadline)
        {
            if (_stdout.CanRead && TryReadAvailable(buf, out int n) && n > 0)
            {
                ms.Write(buf, 0, n);
                gotData = true;
                deadline = Environment.TickCount64 + 25;
                continue;
            }

            if (gotData)
                break;

            Thread.Sleep(1);
        }

        return ms.Length > 0 ? ms.ToArray() : null;
    }

    private bool TryReadAvailable(byte[] buf, out int read)
    {
        read = 0;
        if (_stdout == null)
            return false;

        try
        {
            if (_stdout is not { CanRead: true })
                return false;

            if (_process?.StandardOutput.BaseStream != null)
            {
                // StandardOutput.BaseStream doesn't expose DataAvailable on all platforms;
                // blocking read with short timeout via poll pattern.
            }

            read = _stdout.Read(buf, 0, buf.Length);
            return read > 0;
        }
        catch
        {
            return false;
        }
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