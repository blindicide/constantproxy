namespace ConstantProxy.Core.Traffic;

public sealed record TrafficPoint(DateTimeOffset TimeUtc, double UploadRate, double DownloadRate, long UploadTotal, long DownloadTotal);

/// <summary>
/// Derives current rates, session totals, peaks and averages from periodic cumulative samples (SPEC §15, §17)
/// and keeps a bounded in-memory history for short-term graphs. Pure and clock-free: the caller passes the time.
/// </summary>
public sealed class TrafficStatistics
{
    private readonly int capacity;
    private readonly Queue<TrafficPoint> history = new();
    private DateTimeOffset? startedAt;
    private DateTimeOffset? lastTime;
    private TrafficCounters? lastTotals;
    private TrafficPoint? latest;

    /// <param name="historyCapacity">Number of samples kept; at one sample per second 3600 is one hour.</param>
    public TrafficStatistics(int historyCapacity = 3600)
    {
        capacity = historyCapacity;
    }

    public bool IsAvailable { get; private set; }

    public double? CurrentUploadRate => IsAvailable ? latest?.UploadRate ?? 0 : null;

    public double? CurrentDownloadRate => IsAvailable ? latest?.DownloadRate ?? 0 : null;

    public long? SessionUploadBytes => IsAvailable ? lastTotals?.UploadBytes : null;

    public long? SessionDownloadBytes => IsAvailable ? lastTotals?.DownloadBytes : null;

    public double PeakUploadRate { get; private set; }

    public double PeakDownloadRate { get; private set; }

    public double AverageUploadRate => Average(lastTotals?.UploadBytes);

    public double AverageDownloadRate => Average(lastTotals?.DownloadBytes);

    public IReadOnlyList<TrafficPoint> History => history.ToArray();

    /// <summary>Starts a new session at <paramref name="now"/>, discarding all previous figures.</summary>
    public void Reset(DateTimeOffset now)
    {
        history.Clear();
        startedAt = now;
        lastTime = now;
        lastTotals = null;
        latest = null;
        IsAvailable = false;
        PeakUploadRate = 0;
        PeakDownloadRate = 0;
    }

    /// <summary>Records a sample. Null totals mean the backend cannot measure; no values are invented.</summary>
    public void Sample(DateTimeOffset now, TrafficCounters? totals)
    {
        startedAt ??= now;
        if (totals is null)
        {
            IsAvailable = false;
            lastTotals = null;
            lastTime = now;
            return;
        }

        var current = totals.Value;
        double up = 0, down = 0;
        if (lastTotals is { } previous && lastTime is { } before)
        {
            var seconds = (now - before).TotalSeconds;
            if (seconds <= 0)
            {
                return; // duplicate or out-of-order timestamp
            }

            // A counter that went backwards means the monitor was reset; the new value is then all new traffic.
            up = Delta(current.UploadBytes, previous.UploadBytes) / seconds;
            down = Delta(current.DownloadBytes, previous.DownloadBytes) / seconds;
        }

        IsAvailable = true;
        lastTotals = current;
        lastTime = now;
        PeakUploadRate = Math.Max(PeakUploadRate, up);
        PeakDownloadRate = Math.Max(PeakDownloadRate, down);

        latest = new TrafficPoint(now, up, down, current.UploadBytes, current.DownloadBytes);
        history.Enqueue(latest);
        while (history.Count > capacity)
        {
            history.Dequeue();
        }
    }

    /// <summary>The most recent <paramref name="count"/> points (oldest first), for graphing.</summary>
    public IReadOnlyList<TrafficPoint> Recent(int count)
    {
        var all = history.ToArray();
        return all.Length <= count ? all : all[^count..];
    }

    private static long Delta(long now, long before) => now >= before ? now - before : now;

    private double Average(long? total)
    {
        if (total is null || startedAt is null || lastTime is null)
        {
            return 0;
        }

        var seconds = (lastTime.Value - startedAt.Value).TotalSeconds;
        return seconds > 0 ? total.Value / seconds : 0;
    }
}
