namespace ConstantProxy.Core.Analytics;

public readonly record struct SeriesPoint(DateTimeOffset Time, double? Value);

/// <summary>Builds graph series for the historical views (SPEC §20) from stored minute rows and intervals.</summary>
public static class HistorySeries
{
    /// <summary>Chooses a bucket width so a window is drawn with roughly <paramref name="targetPoints"/> points, never finer than a minute.</summary>
    public static TimeSpan BucketFor(TimeSpan window, int targetPoints = 120)
    {
        var raw = TimeSpan.FromTicks(window.Ticks / Math.Max(targetPoints, 1));
        var minutes = Math.Max(1, (int)Math.Ceiling(raw.TotalMinutes));
        return TimeSpan.FromMinutes(minutes);
    }

    public enum TrafficDirection
    {
        Download,
        Upload,
    }

    /// <summary>Average transfer rate (bytes per second) per bucket; empty buckets are zero because "connected, idle" is a real value.</summary>
    public static IReadOnlyList<SeriesPoint> Rate(IEnumerable<TrafficMinute> minutes, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, TrafficDirection direction)
    {
        var sums = BucketSums(from, to, bucket, out var count);
        foreach (var m in minutes)
        {
            var index = IndexOf(m.MinuteUtc, from, bucket, count);
            if (index >= 0)
            {
                sums[index] += direction == TrafficDirection.Download ? m.DownloadBytes : m.UploadBytes;
            }
        }

        return sums.Select((total, i) => new SeriesPoint(from + (bucket * i), total / bucket.TotalSeconds)).ToList();
    }

    /// <summary>Mean health-check latency (ms) per bucket; null where no check succeeded.</summary>
    public static IReadOnlyList<SeriesPoint> Latency(IEnumerable<TrafficMinute> minutes, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var sum = BucketSums(from, to, bucket, out var count);
        var n = new double[count];
        foreach (var m in minutes)
        {
            var index = IndexOf(m.MinuteUtc, from, bucket, count);
            if (index >= 0)
            {
                sum[index] += m.LatencySumMs;
                n[index] += m.LatencyCount;
            }
        }

        return Enumerable.Range(0, count).Select(i => new SeriesPoint(from + (bucket * i), n[i] > 0 ? sum[i] / n[i] : null)).ToList();
    }

    public static IReadOnlyList<SeriesPoint> Availability(IReadOnlyList<StateInterval> intervals, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset now)
    {
        var count = BucketCount(from, to, bucket);
        var points = new List<SeriesPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var start = from + (bucket * i);
            var end = start + bucket > to ? to : start + bucket;
            points.Add(new SeriesPoint(start, AvailabilityCalculator.Compute(intervals, start, end, now).UptimePercent));
        }

        return points;
    }

    private static double[] BucketSums(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, out int count)
    {
        count = BucketCount(from, to, bucket);
        return new double[count];
    }

    private static int BucketCount(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket) =>
        to <= from || bucket <= TimeSpan.Zero ? 0 : (int)Math.Ceiling((to - from) / bucket);

    private static int IndexOf(DateTimeOffset time, DateTimeOffset from, TimeSpan bucket, int count)
    {
        if (time < from)
        {
            return -1;
        }

        var index = (int)((time - from) / bucket);
        return index < count ? index : -1;
    }
}
