namespace ConstantProxy.Tests.Analytics;

public class AnalyticsSummaryTests
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly SessionProgress Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Wednesday 2026-06-10 12:00 UTC
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static (SqliteAnalyticsStore Store, long Session) Session(TempDir dir, Guid profile, DateTimeOffset start, DateTimeOffset? end, params (IntervalKind Kind, double StartMin, double? EndMin)[] intervals)
    {
        var store = SqliteAnalyticsStore.Open(dir.File("a.db")).Store;
        var id = store.StartSession(profile, start);
        foreach (var (kind, s, e) in intervals)
        {
            var interval = store.OpenInterval(id, kind, start.AddMinutes(s));
            if (e is { } endMin)
            {
                store.CloseInterval(interval, start.AddMinutes(endMin));
            }
        }

        if (end is { } at)
        {
            store.EndSession(id, Zero, at, SessionEndReason.User);
        }

        return (store, id);
    }

    [Fact]
    public void EmptyDatabaseYieldsZerosAndNoFabricatedPercentages()
    {
        using var dir = new TempDir();
        var store = SqliteAnalyticsStore.Open(dir.File("a.db")).Store;
        var summary = new AnalyticsSummaryService(store).Compute(null, Now, Utc);

        Assert.Equal(new TrafficTotals(0, 0), summary.TrafficAllTime);
        Assert.Null(summary.UptimeToday);
        Assert.Null(summary.Uptime7Days);
        Assert.Null(summary.UptimeSession);
        Assert.Equal(TimeSpan.Zero, summary.TotalRuntime);
        Assert.Equal(0, summary.SessionCount);
    }

    [Fact]
    public void TrafficIsBucketedIntoTodayWeekMonthAndAllTime()
    {
        using var dir = new TempDir();
        var (store, id) = Session(dir, ProfileA, Now.AddDays(-40), Now.AddHours(-1));
        TrafficMinute M(DateTimeOffset t, long up, long down) => new(id, t, up, down, 0, 0, 0, 0, 0, 0, 0);
        store.AddTrafficMinutes(new[]
        {
            M(Now.AddHours(-2), 1, 10),        // today (06-10 10:00)
            M(Now.AddDays(-1), 2, 20),         // this week (Tue 06-09), not today
            M(Now.AddDays(-5), 4, 40),         // this month (Fri 06-05) but before Monday 06-08
            M(Now.AddDays(-30), 8, 80),        // older than this month
        });

        var s = new AnalyticsSummaryService(store).Compute(null, Now, Utc);

        Assert.Equal(new TrafficTotals(1, 10), s.TrafficToday);
        Assert.Equal(new TrafficTotals(3, 30), s.TrafficThisWeek);
        Assert.Equal(new TrafficTotals(7, 70), s.TrafficThisMonth);
        Assert.Equal(new TrafficTotals(15, 150), s.TrafficAllTime);
    }

    [Fact]
    public void TodayIncludesTheMinuteInProgress()
    {
        using var dir = new TempDir();
        var (store, id) = Session(dir, ProfileA, Now.AddHours(-1), null);
        store.AddTrafficMinutes(new[] { new TrafficMinute(id, MinuteAggregator.FloorToMinute(Now), 5, 6, 0, 0, 0, 0, 0, 0, 0) });
        Assert.Equal(new TrafficTotals(5, 6), new AnalyticsSummaryService(store).Compute(null, Now, Utc).TrafficToday);
    }

    [Fact]
    public void UptimeIsComputedPerWindowFromIntervals()
    {
        using var dir = new TempDir();
        var start = Now.AddHours(-10);
        // 10 h session: 9 h up, 1 h reconnecting (the last hour)
        var (store, _) = Session(dir, ProfileA, start, null,
            (IntervalKind.Setup, 0, 1),
            (IntervalKind.Up, 1, 540),
            (IntervalKind.Reconnecting, 540, 600));

        var s = new AnalyticsSummaryService(store).Compute(null, Now, Utc, sessionStartUtc: start);

        Assert.InRange(s.UptimeSession!.Value, 89.0, 91.0);   // 539 up of 599 measured minutes
        Assert.InRange(s.UptimeToday!.Value, 89.0, 91.0);
        Assert.Equal(s.Uptime7Days, s.Uptime30Days);
        Assert.Equal(TimeSpan.FromMinutes(539), s.TotalConnected);
        Assert.Equal(TimeSpan.FromHours(10), s.TotalRuntime);
        Assert.Equal(TimeSpan.FromMinutes(539), s.LongestContinuousConnection);
    }

    [Fact]
    public void UserDisconnectedTimeDoesNotLowerUptime()
    {
        using var dir = new TempDir();
        var start = Now.AddHours(-3);
        var (store, _) = Session(dir, ProfileA, start, start.AddHours(2),
            (IntervalKind.Up, 0, 60),
            (IntervalKind.UserDisconnected, 60, 120));
        Assert.Equal(100.0, new AnalyticsSummaryService(store).Compute(null, Now, Utc).UptimeToday!.Value, 6);
    }

    [Fact]
    public void CountsReconnectsAndFailuresFromEvents()
    {
        using var dir = new TempDir();
        var (store, id) = Session(dir, ProfileA, Now.AddHours(-3), null);
        foreach (var type in new[] { "ReconnectAttempt", "ReconnectAttempt", "ConnectionLost", "Connected" })
        {
            store.RecordEvent(id, ProfileA, Now.AddHours(-2), type, null);
        }

        var s = new AnalyticsSummaryService(store).Compute(null, Now, Utc);
        Assert.Equal((2, 1), (s.TotalReconnects, s.TotalFailures));
    }

    [Fact]
    public void ProfilesAreAnalysedIndependently()
    {
        using var dir = new TempDir();
        var (store, a) = Session(dir, ProfileA, Now.AddHours(-3), Now.AddHours(-2), (IntervalKind.Up, 0, 60));
        var b = store.StartSession(ProfileB, Now.AddHours(-3));
        store.OpenInterval(b, IntervalKind.Reconnecting, Now.AddHours(-3));
        store.AddTrafficMinutes(new[]
        {
            new TrafficMinute(a, Now.AddHours(-3), 10, 20, 0, 0, 0, 0, 0, 0, 0),
            new TrafficMinute(b, Now.AddHours(-3), 100, 200, 0, 0, 0, 0, 0, 0, 0),
        });

        var service = new AnalyticsSummaryService(store);
        var sa = service.Compute(ProfileA, Now, Utc);
        var sb = service.Compute(ProfileB, Now, Utc);
        var all = service.Compute(null, Now, Utc);

        Assert.Equal(new TrafficTotals(10, 20), sa.TrafficToday);
        Assert.Equal(new TrafficTotals(100, 200), sb.TrafficToday);
        Assert.Equal(new TrafficTotals(110, 220), all.TrafficToday);
        Assert.Equal(100.0, sa.UptimeToday!.Value);
        Assert.Equal(0.0, sb.UptimeToday!.Value);
        Assert.Equal(1, sa.SessionCount);
    }

    [Fact]
    public void TodayUsesTheUsersTimeZone()
    {
        using var dir = new TempDir();
        var (store, id) = Session(dir, ProfileA, Now.AddDays(-1), null);
        // 20:00 UTC yesterday is already "today" for a UTC+5 user (01:00), but not for a UTC user.
        store.AddTrafficMinutes(new[] { new TrafficMinute(id, new DateTimeOffset(2026, 6, 9, 20, 0, 0, TimeSpan.Zero), 7, 7, 0, 0, 0, 0, 0, 0, 0) });
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("p5", TimeSpan.FromHours(5), "p5", "p5");

        var service = new AnalyticsSummaryService(store);
        Assert.Equal(new TrafficTotals(0, 0), service.Compute(null, Now, Utc).TrafficToday);
        Assert.Equal(new TrafficTotals(7, 7), service.Compute(null, Now, plus5).TrafficToday);
    }
}
