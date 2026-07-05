using System.Diagnostics;

/// <summary>
/// Monotonic host clock shared by display and audio WebSocket streams (phase 2 PTS).
/// </summary>
public sealed class StreamTiming
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Host presentation timestamp in microseconds since stream origin.</summary>
    public long HostPtsUs =>
        _stopwatch.Elapsed.Ticks * 1_000_000L / Stopwatch.Frequency;

    /// <summary>Always zero at origin; clients subtract <see cref="StreamEpochUs"/> from format JSON.</summary>
    public long StreamEpochUs => 0;

    /// <summary>Host clock frequency in Hz (Stopwatch ticks per second).</summary>
    public long HostClockHz => Stopwatch.Frequency;
}