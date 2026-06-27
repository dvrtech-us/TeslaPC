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

    public const string DefaultVideoRoot = @"C:\video\";
    public const int DefaultStreamHeight = 1080;

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
