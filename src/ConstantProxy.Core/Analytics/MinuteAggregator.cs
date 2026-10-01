namespace ConstantProxy.Core.Analytics;

/// <summary>
/// Collapses 1-second samples and health checks into one-minute rows so the database is written once a minute
/// rather than once a second (SPEC §17). Pure: no clock, no I/O.
/// </summary>
public sealed class MinuteAggregator
{
    private readonly SortedDictionary<DateTimeOffset, Bucket> buckets = new();
    private TrafficCounters? lastTotals;

    public void Reset()
    {
        buckets.Clear();
        lastTotals = null;
    }

    public void AddTraffic(TrafficPoint point)
    {
        var current = new TrafficCounters(point.UploadTotal, point.DownloadTotal);
        // A counter that went backwards means the monitor was reset; the new value is then all new traffic.
        var up = lastTotals is { } p ? Delta(current.UploadBytes, p.UploadBytes) : current.UploadBytes;
        var down = lastTotals is { } q ? Delta(current.DownloadBytes, q.DownloadBytes) : current.DownloadBytes;
        lastTotals = current;

        var bucket = BucketFor(point.TimeUtc);
        bucket.Upload += up;
        bucket.Download += down;
        bucket.PeakUp = Math.Max(bucket.PeakUp, point.UploadRate);
        bucket.PeakDown = Math.Max(bucket.PeakDown, point.DownloadRate);
    }

    public void AddHealth(DateTimeOffset time, bool success, TimeSpan? latency)
    {
        var bucket = BucketFor(time);
        bucket.Checks++;
        if (!success)
        {
            bucket.Failures++;
        }
        else if (latency is { } l)
        {
            var ms = l.TotalMilliseconds;
            bucket.LatencySum += ms;
            bucket.LatencyCount++;
            bucket.LatencyMax = Math.Max(bucket.LatencyMax, ms);
        }
    }

    /// <summary>Removes and returns minutes that are over (strictly before the minute containing <paramref name="now"/>).</summary>
    public IReadOnlyList<TrafficMinute> TakeCompleted(long sessionId, DateTimeOffset now)
    {
        var current = FloorToMinute(now);
        return Take(sessionId, minute => minute < current);
    }

    /// <summary>Removes and returns everything, including the minute in progress (used at session end).</summary>
    public IReadOnlyList<TrafficMinute> TakeAll(long sessionId) => Take(sessionId, _ => true);

    public static DateTimeOffset FloorToMinute(DateTimeOffset time) =>
        new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);

    private IReadOnlyList<TrafficMinute> Take(long sessionId, Func<DateTimeOffset, bool> predicate)
    {
        var result = new List<TrafficMinute>();
        foreach (var (minute, b) in buckets.Where(kv => predicate(kv.Key)).ToList())
        {
            result.Add(new TrafficMinute(sessionId, minute, b.Upload, b.Download, b.PeakUp, b.PeakDown, b.Checks, b.Failures, b.LatencySum, b.LatencyCount, b.LatencyMax));
            buckets.Remove(minute);
        }

        return result;
    }

    private Bucket BucketFor(DateTimeOffset time)
    {
        var key = FloorToMinute(time);
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket();
            buckets[key] = bucket;
        }

        return bucket;
    }

    private static long Delta(long now, long before) => now >= before ? now - before : now;

    private sealed class Bucket
    {
        public long Upload;
        public long Download;
        public double PeakUp;
        public double PeakDown;
        public int Checks;
        public int Failures;
        public double LatencySum;
        public int LatencyCount;
        public double LatencyMax;
    }
}
