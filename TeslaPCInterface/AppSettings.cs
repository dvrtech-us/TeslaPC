using System.Reflection;
using System.Text;

/// <summary>
/// Central user-editable configuration, persisted to <c>%ProgramData%\TeslaPC\.env</c> — the same
/// file <c>Program.LoadDotEnv</c> reads at startup. Values are exposed as environment variables once
/// loaded; the rest of the app keeps reading them via <c>Environment.GetEnvironmentVariable</c>.
///
/// The UI (WinForms Config tab and the web /config page) reads current values with <see cref="Get"/>
/// and persists edits with <see cref="Save"/>, which rewrites only the touched keys and preserves any
/// other lines/comments in the file. Save also updates the in-process environment so changes that can
/// apply live (e.g. the video folder) take effect without a restart.
/// </summary>
public static class AppSettings
{
    public const string HttpsHostKey = "TESLAPC_HTTPS_HOST";
    public const string CloudflareTokenKey = "TESLAPC_CF_TOKEN";
    public const string AcmeEmailKey = "TESLAPC_ACME_EMAIL";
    public const string VideoRootKey = "TESLAPC_VIDEO_ROOT";
    public const string LogLevelKey = "TESLAPC_LOG_LEVEL";
    public const string StreamHeightKey = "TESLAPC_STREAM_HEIGHT";
    public const string DisplayRendererKey = "TESLAPC_DISPLAY_RENDERER";
    public const string DisplayTransportKey = "TESLAPC_DISPLAY_TRANSPORT";
    public const string AudioBoostKey = "TESLAPC_AUDIO_BOOST";
    public const string H264GopFramesKey = "TESLAPC_H264_GOP_FRAMES";
    public const string H264BitrateKey = "TESLAPC_H264_BITRATE";

    public const string DefaultVideoRoot = @"C:\video\";
    public const int MinH264GopFrames = 1;
    public const int MaxH264GopFrames = 300;
    public const int MinH264Bitrate = 500_000;
    public const int MaxH264Bitrate = 20_000_000;
    public const int DefaultStreamHeight = 1080;
    public const string DefaultDisplayRenderer = "mjpeg";
    public const string DefaultDisplayTransport = "websocket";
    public const double DefaultAudioBoost = 1.0;
    public const double MinAudioBoost = 0.25;
    public const double MaxAudioBoost = 6.0;

    /// <summary>Client playback gain multiplier (0.25–6.0; 1.0 = unity).</summary>
    public static double AudioBoost
    {
        get
        {
            var v = Get(AudioBoostKey);
            if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var gain))
                return Math.Clamp(gain, MinAudioBoost, MaxAudioBoost);
            return DefaultAudioBoost;
        }
    }

    /// <summary>Display codec for WebSocket transport: <c>mjpeg</c> or <c>h264</c>.</summary>
    public static string DisplayRenderer
    {
        get
        {
            var v = (Get(DisplayRendererKey) ?? DefaultDisplayRenderer).Trim().ToLowerInvariant();
            return v == "h264" ? "h264" : "mjpeg";
        }
    }

    /// <summary>Display transport: <c>websocket</c> (default) or <c>http</c> (legacy <c>/stream</c>).</summary>
    public static string DisplayTransport
    {
        get
        {
            var v = (Get(DisplayTransportKey) ?? DefaultDisplayTransport).Trim().ToLowerInvariant();
            return v == "http" ? "http" : "websocket";
        }
    }

    /// <summary>H264 GOP length in frames. Unset or invalid values default to one second at the stream FPS.</summary>
    public static int H264GopFrames(int fps)
    {
        var v = Get(H264GopFramesKey);
        if (int.TryParse(v, out var gop) && gop >= MinH264GopFrames && gop <= MaxH264GopFrames)
            return gop;
        return Math.Max(1, fps);
    }

    /// <summary>Optional H264 average bitrate override in bps. Returns null when unset/invalid.</summary>
    public static int? H264BitrateOverride
    {
        get
        {
            var v = Get(H264BitrateKey);
            if (int.TryParse(v, out var bps) && bps >= MinH264Bitrate && bps <= MaxH264Bitrate)
                return bps;
            return null;
        }
    }

    /// <summary>Max vertical pixels for the stream (the live screen/media is scaled into this cap).</summary>
    public static int StreamHeight
    {
        get
        {
            var v = Get(StreamHeightKey);
            return int.TryParse(v, out var h) && h >= 240 && h <= 2160 ? h : DefaultStreamHeight;
        }
    }

    /// <summary><c>%ProgramData%\TeslaPC\.env</c> — the persistent config file (survives rebuilds).</summary>
    public static string EnvFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeslaPC", ".env");

    public static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    /// <summary>The app version (from the assembly, set by &lt;Version&gt; in the csproj).</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Configured video folder, or the default when unset/blank.</summary>
    public static string VideoRoot
    {
        get
        {
            var v = Get(VideoRootKey);
            return string.IsNullOrWhiteSpace(v) ? DefaultVideoRoot : v.Trim();
        }
    }

    /// <summary>
    /// Persists the given key/value pairs to the .env file (updating existing keys in place, appending
    /// new ones, leaving everything else untouched) and mirrors them into the current process env.
    /// Pass only the keys you want to change; omit a key to leave it as-is.
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, string> values)
    {
        string path = EnvFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        foreach (var kv in values)
        {
            string line = $"{kv.Key}={kv.Value}";
            int idx = lines.FindIndex(l =>
            {
                var t = l.TrimStart();
                if (t.StartsWith("#")) return false;
                int eq = t.IndexOf('=');
                return eq > 0 && t.Substring(0, eq).Trim() == kv.Key;
            });
            if (idx >= 0) lines[idx] = line;
            else lines.Add(line);

            // Reflect into the live process so in-process readers see the new value immediately.
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }
}
