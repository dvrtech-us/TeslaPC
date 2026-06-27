using Microsoft.Data.Sqlite;

/// <summary>
/// Small SQLite store for media playback state, at <c>%ProgramData%\TeslaPC\teslapc.db</c>. Tracks,
/// per file, the last playback position, duration, a "watched" flag (set once ≥95% of the file has
/// been played), play count, and a timestamp. Used to resume playback and to mark watched files in
/// the file browser.
/// </summary>
public sealed class MediaLibrary
{
    /// <summary>A file is considered "watched" once playback reaches this fraction of its duration.</summary>
    public const double WatchedFraction = 0.95;

    private readonly string _connectionString;

    public MediaLibrary()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeslaPC");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "teslapc.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Initialize();
        Console.WriteLine($"[Library] SQLite playback DB at {dbPath}");
    }

    public sealed record Progress(double Position, double Duration, bool Watched, int PlayCount)
    {
        /// <summary>Percent watched (0–100) when the duration is known, else 0.</summary>
        public int Percent => Duration > 0 ? (int)Math.Round(Math.Min(1.0, Position / Duration) * 100) : 0;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS playback (
                path        TEXT PRIMARY KEY COLLATE NOCASE,
                position    REAL NOT NULL DEFAULT 0,
                duration    REAL NOT NULL DEFAULT 0,
                watched     INTEGER NOT NULL DEFAULT 0,
                play_count  INTEGER NOT NULL DEFAULT 0,
                updated_at  TEXT
              );";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the stored progress for a file, or null if never played.</summary>
    public Progress? Get(string path)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT position, duration, watched, play_count FROM playback WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new Progress(r.GetDouble(0), r.GetDouble(1), r.GetInt32(2) != 0, r.GetInt32(3));
        }
        catch (Exception ex) { Console.WriteLine($"[Library] Get failed: {ex.Message}"); }
        return null;
    }

    /// <summary>All stored rows, keyed by lower-cased path (for batch lookups in the file browser).</summary>
    public Dictionary<string, Progress> GetAll()
    {
        var map = new Dictionary<string, Progress>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path, position, duration, watched, play_count FROM playback";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[r.GetString(0).ToLowerInvariant()] =
                    new Progress(r.GetDouble(1), r.GetDouble(2), r.GetInt32(3) != 0, r.GetInt32(4));
        }
        catch (Exception ex) { Console.WriteLine($"[Library] GetAll failed: {ex.Message}"); }
        return map;
    }

    /// <summary>
    /// Upserts the playback position/duration for a file. The watched flag is sticky — once set it
    /// stays set — and is raised when position/duration crosses <see cref="WatchedFraction"/>.
    /// </summary>
    public void SaveProgress(string path, double position, double duration)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool watched = duration > 0 && position / duration >= WatchedFraction;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO playback (path, position, duration, watched, play_count, updated_at)
                  VALUES ($p, $pos, $dur, $w, 0, $t)
                  ON CONFLICT(path) DO UPDATE SET
                    position   = $pos,
                    duration   = CASE WHEN $dur > 0 THEN $dur ELSE duration END,
                    watched    = MAX(watched, $w),
                    updated_at = $t;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$pos", position);
            cmd.Parameters.AddWithValue("$dur", duration);
            cmd.Parameters.AddWithValue("$w", watched ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Console.WriteLine($"[Library] SaveProgress failed: {ex.Message}"); }
    }

    /// <summary>Marks a file fully watched (position = duration), e.g. on natural end-of-file.</summary>
    public void MarkWatched(string path, double duration)
    {
        if (duration > 0) SaveProgress(path, duration, duration);
    }

    /// <summary>Increments the play count for a file (called when playback starts).</summary>
    public void IncrementPlayCount(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO playback (path, play_count, updated_at) VALUES ($p, 1, $t)
                  ON CONFLICT(path) DO UPDATE SET play_count = play_count + 1, updated_at = $t;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Console.WriteLine($"[Library] IncrementPlayCount failed: {ex.Message}"); }
    }
}
