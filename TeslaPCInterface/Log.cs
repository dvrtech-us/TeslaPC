/// <summary>
/// Minimal leveled logger. Everything still goes through <see cref="Console"/> (so the WinForms Log
/// tab tee and any redirected log file keep working), but messages below the current threshold are
/// dropped. The threshold comes from <c>TESLAPC_LOG_LEVEL</c> at startup (default <c>Info</c>) and can
/// be changed live via <see cref="SetLevel"/> from the Settings UI.
///
/// High-frequency traffic (per mouse-move input, per HTTP request) logs at <see cref="Level.Debug"/>,
/// so it is silent unless the level is raised — no more echoing every mouse move.
/// </summary>
public static class Log
{
    public enum Level { Error = 0, Warn = 1, Info = 2, Debug = 3 }

    private static volatile Level _threshold = Parse(Environment.GetEnvironmentVariable("TESLAPC_LOG_LEVEL")) ?? Level.Info;

    public static Level Threshold => _threshold;

    /// <summary>Current level as the canonical lowercase name (for the config UIs).</summary>
    public static string LevelName => _threshold.ToString().ToLowerInvariant();

    public static Level? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToLowerInvariant() switch
        {
            "error" or "0" => Level.Error,
            "warn" or "warning" or "1" => Level.Warn,
            "info" or "2" => Level.Info,
            "debug" or "verbose" or "3" => Level.Debug,
            _ => null
        };
    }

    /// <summary>Sets the threshold from a name/number; ignored if unrecognized.</summary>
    public static void SetLevel(string? s)
    {
        var l = Parse(s);
        if (l.HasValue) _threshold = l.Value;
    }

    public static void Error(string message) => Write(Level.Error, message);
    public static void Warn(string message) => Write(Level.Warn, message);
    public static void Info(string message) => Write(Level.Info, message);
    public static void Debug(string message) => Write(Level.Debug, message);

    private static void Write(Level level, string message)
    {
        if (level > _threshold) return;
        string tag = level switch
        {
            Level.Error => "[ERROR] ",
            Level.Warn => "[WARN] ",
            Level.Debug => "[DEBUG] ",
            _ => ""
        };
        Console.WriteLine(tag + message);
    }
}
