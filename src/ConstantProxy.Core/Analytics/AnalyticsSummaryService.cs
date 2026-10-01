namespace ConstantProxy.Core.Analytics;

public sealed record AnalyticsSummary(
    TrafficTotals TrafficToday,
    TrafficTotals TrafficThisWeek,
    TrafficTotals TrafficThisMonth,
    TrafficTotals TrafficAllTime,
    double? UptimeSession,
    double? UptimeToday,
    double? Uptime7Days,
    double? Uptime30Days,
    TimeSpan TotalRuntime,
    TimeSpan TotalConnected,
    int TotalReconnects,
    int TotalFailures,
    TimeSpan LongestContinuousConnection,
    int SessionCount);

/// <summary>Computes the aggregate statistics of SPEC §18 and §19 from the store for one profile (or all of them).</summary>
public sealed class AnalyticsSummaryService
{
    private readonly IAnalyticsStore store;

    public AnalyticsSummaryService(IAnalyticsStore store)
    {
        this.store = store;
    }

    /// <param name="sessionStartUtc">Start of the current session, if one is running, for the session uptime figure.</param>
    public AnalyticsSummary Compute(Guid? profileId, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? sessionStartUtc = null)
    {
        var all = DateTimeOffset.UnixEpoch;
        var tomorrow = now.AddMinutes(1); // minute rows are keyed by the start of their minute, so include the current one

        var today = TimeWindows.Today(now, zone);
        var week = TimeWindows.ThisWeek(now, zone);
        var month = TimeWindows.ThisMonth(now, zone);

        var thirtyDays = TimeWindows.Last(now, TimeSpan.FromDays(30));
        var intervals = store.GetIntervals(profileId, all, tomorrow);
        double? Uptime(DateTimeOffset from) => AvailabilityCalculator.Compute(intervals, from, now, now).UptimePercent;

        var sessions = store.GetSessions(profileId, all, tomorrow);
        var runtime = TimeSpan.Zero;
        foreach (var s in sessions)
        {
            runtime += (s.EndUtc ?? now) - s.StartUtc;
        }

        var report = AvailabilityCalculator.Compute(intervals, all, now, now);
        return new AnalyticsSummary(
            store.GetTrafficTotals(profileId, today.From, tomorrow),
            store.GetTrafficTotals(profileId, week.From, tomorrow),
            store.GetTrafficTotals(profileId, month.From, tomorrow),
            store.GetTrafficTotals(profileId, all, tomorrow),
            sessionStartUtc is { } start ? Uptime(start) : null,
            Uptime(today.From),
            Uptime(now.AddDays(-7)),
            Uptime(thirtyDays.From),
            runtime,
            report.Up + report.Degraded,
            store.GetEventCount(profileId, nameof(ConnectionEventType.ReconnectAttempt), all, tomorrow),
            store.GetEventCount(profileId, nameof(ConnectionEventType.ConnectionLost), all, tomorrow),
            AvailabilityCalculator.LongestContinuousConnection(intervals, now),
            sessions.Count);
    }
}
