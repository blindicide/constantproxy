namespace ConstantProxy.Core.Analytics;

public sealed record AvailabilityReport(
    TimeSpan Up,
    TimeSpan Degraded,
    TimeSpan Reconnecting,
    TimeSpan Failed,
    TimeSpan Setup,
    TimeSpan UserDisconnected)
{
    /// <summary>Time that counts for or against availability: user disconnects and initial setup are excluded.</summary>
    public TimeSpan Measured => Up + Degraded + Reconnecting + Failed;

    public TimeSpan Downtime => Degraded + Reconnecting + Failed;

    /// <summary>Uptime percentage (0-100), or null when there is nothing to measure (never a fabricated 100%).</summary>
    public double? UptimePercent => Measured <= TimeSpan.Zero ? null : 100.0 * Up.TotalSeconds / Measured.TotalSeconds;
}

/// <summary>
/// Availability math (SPEC §19). Intentional disconnects and the very first connect of a session are not outages;
/// reconnecting, failed and degraded time are. Intervals are clipped to the requested window.
/// </summary>
public static class AvailabilityCalculator
{
    public static AvailabilityReport Compute(IEnumerable<StateInterval> intervals, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var totals = new Dictionary<IntervalKind, TimeSpan>();
        foreach (var kind in Enum.GetValues<IntervalKind>())
        {
            totals[kind] = TimeSpan.Zero;
        }

        foreach (var interval in intervals)
        {
            var overlap = Overlap(interval, from, to, now);
            if (overlap > TimeSpan.Zero)
            {
                totals[interval.Kind] += overlap;
            }
        }

        return new AvailabilityReport(
            totals[IntervalKind.Up],
            totals[IntervalKind.Degraded],
            totals[IntervalKind.Reconnecting],
            totals[IntervalKind.Failed],
            totals[IntervalKind.Setup],
            totals[IntervalKind.UserDisconnected]);
    }

    /// <summary>Uptime percentage per equal-width bucket across the window; null where nothing was measured.</summary>
    public static IReadOnlyList<(DateTimeOffset Start, double? UptimePercent)> Bucketize(
        IReadOnlyList<StateInterval> intervals, DateTimeOffset from, DateTimeOffset to, int buckets, DateTimeOffset now)
    {
        if (buckets < 1 || to <= from)
        {
            return Array.Empty<(DateTimeOffset, double?)>();
        }

        var width = (to - from) / buckets;
        var result = new List<(DateTimeOffset, double?)>(buckets);
        for (var i = 0; i < buckets; i++)
        {
            var start = from + (width * i);
            var end = i == buckets - 1 ? to : start + width;
            result.Add((start, Compute(intervals, start, end, now).UptimePercent));
        }

        return result;
    }

    /// <summary>
    /// The longest unbroken stretch of connection (Up and Degraded time back to back), for the
    /// "longest continuous connection" statistic (SPEC §18). Intervals must belong to one or more sessions.
    /// </summary>
    public static TimeSpan LongestContinuousConnection(IEnumerable<StateInterval> intervals, DateTimeOffset now)
    {
        var longest = TimeSpan.Zero;
        var run = TimeSpan.Zero;
        long? lastSession = null;
        DateTimeOffset? lastEnd = null;

        foreach (var interval in intervals.OrderBy(i => i.SessionId).ThenBy(i => i.Start))
        {
            var end = interval.End ?? now;
            var connected = interval.Kind is IntervalKind.Up or IntervalKind.Degraded;
            var contiguous = lastSession == interval.SessionId && lastEnd is { } previousEnd && interval.Start <= previousEnd.AddSeconds(1);

            if (!connected)
            {
                run = TimeSpan.Zero;
            }
            else
            {
                run = contiguous && run > TimeSpan.Zero ? run + (end - interval.Start) : end - interval.Start;
                if (run > longest)
                {
                    longest = run;
                }
            }

            lastSession = interval.SessionId;
            lastEnd = end;
        }

        return longest;
    }

    private static TimeSpan Overlap(StateInterval interval, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var start = interval.Start > from ? interval.Start : from;
        var end = interval.End ?? now;
        if (end > to)
        {
            end = to;
        }

        return end > start ? end - start : TimeSpan.Zero;
    }
}
