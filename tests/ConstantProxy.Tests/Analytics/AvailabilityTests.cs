namespace ConstantProxy.Tests.Analytics;

public class AvailabilityCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static StateInterval I(long session, IntervalKind kind, int startSec, int? endSec) =>
        new(0, session, kind, T0.AddSeconds(startSec), endSec is null ? null : T0.AddSeconds(endSec.Value));

    [Fact]
    public void UptimeIsUpOverMeasuredTime()
    {
        var report = AvailabilityCalculator.Compute(new[]
        {
            I(1, IntervalKind.Up, 0, 900),
            I(1, IntervalKind.Reconnecting, 900, 910),
            I(1, IntervalKind.Up, 910, 1000),
        }, T0, T0.AddSeconds(1000), T0.AddSeconds(1000));

        Assert.Equal(TimeSpan.FromSeconds(990), report.Up);
        Assert.Equal(TimeSpan.FromSeconds(10), report.Reconnecting);
        Assert.Equal(99.0, report.UptimePercent!.Value, 6);
        Assert.Equal(TimeSpan.FromSeconds(10), report.Downtime);
    }

    [Fact]
    public void IntentionalDisconnectAndInitialSetupAreNotOutages()
    {
        var report = AvailabilityCalculator.Compute(new[]
        {
            I(1, IntervalKind.Setup, 0, 5),
            I(1, IntervalKind.Up, 5, 105),
            I(1, IntervalKind.UserDisconnected, 105, 110),
        }, T0, T0.AddSeconds(110), T0.AddSeconds(110));

        Assert.Equal(100.0, report.UptimePercent!.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), report.Setup);
        Assert.Equal(TimeSpan.FromSeconds(5), report.UserDisconnected);
        Assert.Equal(TimeSpan.FromSeconds(100), report.Measured);
    }

    [Fact]
    public void DegradedAndFailedCountAsDowntime()
    {
        var report = AvailabilityCalculator.Compute(new[]
        {
            I(1, IntervalKind.Up, 0, 50),
            I(1, IntervalKind.Degraded, 50, 70),
            I(1, IntervalKind.Failed, 70, 100),
        }, T0, T0.AddSeconds(100), T0.AddSeconds(100));

        Assert.Equal(50.0, report.UptimePercent!.Value);
        Assert.Equal(TimeSpan.FromSeconds(20), report.Degraded);
        Assert.Equal(TimeSpan.FromSeconds(30), report.Failed);
    }

    [Fact]
    public void NothingMeasuredMeansNoPercentageRatherThanAFabricatedOne()
    {
        Assert.Null(AvailabilityCalculator.Compute(Array.Empty<StateInterval>(), T0, T0.AddHours(1), T0.AddHours(1)).UptimePercent);
        Assert.Null(AvailabilityCalculator.Compute(new[] { I(1, IntervalKind.UserDisconnected, 0, 60) }, T0, T0.AddSeconds(60), T0.AddSeconds(60)).UptimePercent);
    }

    [Fact]
    public void IntervalsAreClippedToTheWindow()
    {
        var report = AvailabilityCalculator.Compute(new[]
        {
            I(1, IntervalKind.Up, -100, 40),
            I(1, IntervalKind.Reconnecting, 40, 80),
            I(1, IntervalKind.Up, 80, 500),
        }, T0, T0.AddSeconds(100), T0.AddSeconds(500));

        Assert.Equal(TimeSpan.FromSeconds(40 + 20), report.Up);
        Assert.Equal(TimeSpan.FromSeconds(40), report.Reconnecting);
    }

    [Fact]
    public void OpenIntervalRunsUntilNow()
    {
        var report = AvailabilityCalculator.Compute(new[] { I(1, IntervalKind.Up, 0, null) }, T0, T0.AddHours(1), T0.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), report.Up);
    }

    [Fact]
    public void BucketizeGivesPerBucketUptimeAndNullForEmptyBuckets()
    {
        var intervals = new[]
        {
            I(1, IntervalKind.Up, 0, 50),
            I(1, IntervalKind.Reconnecting, 50, 100),
            I(1, IntervalKind.UserDisconnected, 100, 200),
        };
        var buckets = AvailabilityCalculator.Bucketize(intervals, T0, T0.AddSeconds(200), 4, T0.AddSeconds(200));

        Assert.Equal(4, buckets.Count);
        Assert.Equal(100.0, buckets[0].UptimePercent!.Value);
        Assert.Equal(0.0, buckets[1].UptimePercent!.Value);
        Assert.Null(buckets[2].UptimePercent);
        Assert.Null(buckets[3].UptimePercent);
        Assert.Equal(T0.AddSeconds(50), buckets[1].Start);
    }

    [Fact]
    public void BucketizeRejectsNonsenseRanges()
    {
        Assert.Empty(AvailabilityCalculator.Bucketize(Array.Empty<StateInterval>(), T0, T0, 5, T0));
        Assert.Empty(AvailabilityCalculator.Bucketize(Array.Empty<StateInterval>(), T0, T0.AddSeconds(1), 0, T0));
    }

    [Fact]
    public void LongestContinuousConnectionMergesUpAndDegradedButBreaksOnOutages()
    {
        var longest = AvailabilityCalculator.LongestContinuousConnection(new[]
        {
            I(1, IntervalKind.Setup, 0, 5),
            I(1, IntervalKind.Up, 5, 105),           // 100
            I(1, IntervalKind.Degraded, 105, 145),   // +40 = 140
            I(1, IntervalKind.Up, 145, 165),         // +20 = 160
            I(1, IntervalKind.Reconnecting, 165, 170),
            I(1, IntervalKind.Up, 170, 400),         // 230 (new longest)
            I(1, IntervalKind.Failed, 400, 410),
            I(2, IntervalKind.Up, 0, 100),
        }, T0.AddSeconds(500));

        Assert.Equal(TimeSpan.FromSeconds(230), longest);
    }

    [Fact]
    public void LongestContinuousCountsAnOpenIntervalUntilNowAndSeparatesSessions()
    {
        var longest = AvailabilityCalculator.LongestContinuousConnection(new[]
        {
            I(1, IntervalKind.Up, 0, 100),
            I(2, IntervalKind.Up, 100, null),
        }, T0.AddSeconds(150));
        Assert.Equal(TimeSpan.FromSeconds(100), longest); // sessions never merge: 100 vs 50
    }

    [Fact]
    public void LongestContinuousIsZeroWithoutConnections() =>
        Assert.Equal(TimeSpan.Zero, AvailabilityCalculator.LongestContinuousConnection(Array.Empty<StateInterval>(), T0));
}

public class TimeWindowsTests
{
    // UTC+3, no DST
    private static readonly TimeZoneInfo Plus3 = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");

    // UTC+1 with DST (+2) from the last Sunday of March 02:00 to the last Sunday of October 03:00 (EU rules).
    private static readonly TimeZoneInfo Eu = TimeZoneInfo.CreateCustomTimeZone(
        "eu", TimeSpan.FromHours(1), "eu", "eu", "eu-dst",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        });

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void TodayStartsAtLocalMidnightExpressedInUtc()
    {
        var (from, to) = TimeWindows.Today(Utc(2026, 6, 10, 22, 30), Plus3); // local 01:30 on the 11th
        Assert.Equal(Utc(2026, 6, 10, 21, 0), from);
        Assert.Equal(Utc(2026, 6, 10, 22, 30), to);
    }

    [Fact]
    public void WeekStartsOnMonday()
    {
        // 2026-06-10 is a Wednesday
        var (from, _) = TimeWindows.ThisWeek(Utc(2026, 6, 10, 12), Plus3);
        Assert.Equal(Utc(2026, 6, 7, 21, 0), from); // Monday 2026-06-08 00:00 at UTC+3
    }

    [Fact]
    public void WeekOnAMondayStartsThatSameDay()
    {
        var (from, _) = TimeWindows.ThisWeek(Utc(2026, 6, 8, 12), Plus3);
        Assert.Equal(Utc(2026, 6, 7, 21, 0), from);
    }

    [Fact]
    public void WeekOnASundayReachesBackSixDays()
    {
        var (from, _) = TimeWindows.ThisWeek(Utc(2026, 6, 14, 12), Plus3); // Sunday
        Assert.Equal(Utc(2026, 6, 7, 21, 0), from);
    }

    [Fact]
    public void MonthStartsOnTheFirstLocalMidnight()
    {
        var (from, _) = TimeWindows.ThisMonth(Utc(2026, 6, 10, 12), Plus3);
        Assert.Equal(Utc(2026, 5, 31, 21, 0), from);
    }

    [Fact]
    public void LocalMidnightAroundDstUsesTheOffsetInForceOnThatDay()
    {
        // Winter (UTC+1): midnight local = 23:00 UTC the day before.
        Assert.Equal(Utc(2026, 1, 14, 23, 0), TimeWindows.Today(Utc(2026, 1, 15, 12), Eu).From);
        // Summer (UTC+2): midnight local = 22:00 UTC the day before.
        Assert.Equal(Utc(2026, 7, 14, 22, 0), TimeWindows.Today(Utc(2026, 7, 15, 12), Eu).From);
    }

    [Fact]
    public void WeekSpanningTheSpringForwardChangeStartsAtTheWinterMidnight()
    {
        // Mon 2026-03-23 is before the change on Sun 2026-03-29; now is Tue 2026-03-31 (summer time).
        var (from, _) = TimeWindows.ThisWeek(Utc(2026, 3, 31, 12), Eu);
        Assert.Equal(Utc(2026, 3, 29, 22, 0), from); // Monday 03-30 00:00 local, already UTC+2
        var (from2, _) = TimeWindows.ThisWeek(Utc(2026, 3, 29, 12), Eu); // Sunday: week began Mon 03-23 at UTC+1
        Assert.Equal(Utc(2026, 3, 22, 23, 0), from2);
    }

    [Fact]
    public void RollingWindowEndsNow()
    {
        var now = Utc(2026, 6, 10, 12);
        var (from, to) = TimeWindows.Last(now, TimeSpan.FromDays(7));
        Assert.Equal(now.AddDays(-7), from);
        Assert.Equal(now, to);
    }
}

public class MinuteAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TrafficPoint P(int sec, long up, long down, double upRate = 0, double downRate = 0) =>
        new(T0.AddSeconds(sec), upRate, downRate, up, down);

    [Fact]
    public void SumsDeltasIntoMinuteBucketsWithPeaks()
    {
        var agg = new MinuteAggregator();
        agg.AddTraffic(P(1, 100, 1000, 100, 1000));
        agg.AddTraffic(P(2, 300, 1500, 200, 500));
        agg.AddTraffic(P(61, 400, 4500, 100, 3000));

        var done = agg.TakeCompleted(7, T0.AddSeconds(90));
        var first = Assert.Single(done);
        Assert.Equal(T0, first.MinuteUtc);
        Assert.Equal(300, first.UploadBytes);
        Assert.Equal(1500, first.DownloadBytes);
        Assert.Equal(200, first.PeakUploadRate);
        Assert.Equal(1000, first.PeakDownloadRate);
        Assert.Equal(7, first.SessionId);

        var rest = agg.TakeAll(7);
        var second = Assert.Single(rest);
        Assert.Equal(T0.AddMinutes(1), second.MinuteUtc);
        Assert.Equal(100, second.UploadBytes);
        Assert.Equal(3000, second.DownloadBytes);
    }

    [Fact]
    public void TakeCompletedLeavesTheMinuteInProgress()
    {
        var agg = new MinuteAggregator();
        agg.AddTraffic(P(10, 5, 5));
        Assert.Empty(agg.TakeCompleted(1, T0.AddSeconds(30)));
        Assert.Single(agg.TakeAll(1));
        Assert.Empty(agg.TakeAll(1));
    }

    [Fact]
    public void CounterResetsAreTreatedAsNewTraffic()
    {
        var agg = new MinuteAggregator();
        agg.AddTraffic(P(1, 1000, 1000));
        agg.AddTraffic(P(2, 50, 70));
        var minute = Assert.Single(agg.TakeAll(1));
        Assert.Equal(1050, minute.UploadBytes);
        Assert.Equal(1070, minute.DownloadBytes);
    }

    [Fact]
    public void HealthChecksAreCountedWithLatencyStatistics()
    {
        var agg = new MinuteAggregator();
        agg.AddHealth(T0.AddSeconds(5), true, TimeSpan.FromMilliseconds(20));
        agg.AddHealth(T0.AddSeconds(15), true, TimeSpan.FromMilliseconds(60));
        agg.AddHealth(T0.AddSeconds(25), false, null);

        var minute = Assert.Single(agg.TakeAll(1));
        Assert.Equal(3, minute.HealthChecks);
        Assert.Equal(1, minute.HealthFailures);
        Assert.Equal(2, minute.LatencyCount);
        Assert.Equal(40, minute.AverageLatencyMs!.Value, 6);
        Assert.Equal(60, minute.LatencyMaxMs);
    }

    [Fact]
    public void MinuteWithoutLatencyHasNoAverage()
    {
        var agg = new MinuteAggregator();
        agg.AddHealth(T0, false, null);
        Assert.Null(agg.TakeAll(1).Single().AverageLatencyMs);
    }

    [Fact]
    public void ResetDropsEverything()
    {
        var agg = new MinuteAggregator();
        agg.AddTraffic(P(1, 5, 5));
        agg.Reset();
        Assert.Empty(agg.TakeAll(1));
        agg.AddTraffic(P(2, 7, 9)); // baseline forgotten: first delta is the full total again
        Assert.Equal(7, agg.TakeAll(1).Single().UploadBytes);
    }

    [Fact]
    public void FloorToMinuteTruncatesSecondsAndMilliseconds() =>
        Assert.Equal(T0, MinuteAggregator.FloorToMinute(T0.AddSeconds(59).AddMilliseconds(999)));
}

public class HistorySeriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrafficMinute M(int minute, long up, long down, double latSum = 0, int latCount = 0) =>
        new(1, T0.AddMinutes(minute), up, down, 0, 0, latCount, 0, latSum, latCount, 0);

    [Theory]
    [InlineData(60, 1)]          // 1 h at ~120 points -> never finer than a minute
    [InlineData(24 * 60, 12)]    // 24 h
    [InlineData(7 * 24 * 60, 84)]
    public void BucketWidthTargetsAboutOneHundredTwentyPoints(int windowMinutes, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), HistorySeries.BucketFor(TimeSpan.FromMinutes(windowMinutes)));

    [Fact]
    public void RateIsBytesOverTheBucketDuration()
    {
        var series = HistorySeries.Rate(new[] { M(0, 0, 6000), M(1, 0, 6000), M(5, 0, 600) }, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), HistorySeries.TrafficDirection.Download);
        Assert.Equal(2, series.Count);
        Assert.Equal(12000 / 300.0, series[0].Value);
        Assert.Equal(600 / 300.0, series[1].Value);
        Assert.Equal(T0.AddMinutes(5), series[1].Time);
    }

    [Fact]
    public void RateUsesTheRequestedDirectionAndZeroForIdleBuckets()
    {
        var up = HistorySeries.Rate(new[] { M(0, 3000, 1) }, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), HistorySeries.TrafficDirection.Upload);
        Assert.Equal(10, up[0].Value);
        Assert.Equal(0, up[1].Value);
    }

    [Fact]
    public void RateIgnoresRowsOutsideTheWindow()
    {
        var series = HistorySeries.Rate(new[] { M(-5, 999, 999), M(50, 999, 999) }, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), HistorySeries.TrafficDirection.Download);
        Assert.All(series, p => Assert.Equal(0, p.Value));
    }

    [Fact]
    public void LatencyIsTheWeightedMeanAndNullWhenThereWereNoSuccessfulChecks()
    {
        var series = HistorySeries.Latency(new[] { M(0, 0, 0, 100, 2), M(1, 0, 0, 300, 2) }, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5));
        Assert.Equal(100, series[0].Value);
        Assert.Null(series[1].Value);
    }

    [Fact]
    public void AvailabilitySeriesFollowsTheIntervals()
    {
        var intervals = new[]
        {
            new StateInterval(1, 1, IntervalKind.Up, T0, T0.AddMinutes(5)),
            new StateInterval(2, 1, IntervalKind.Reconnecting, T0.AddMinutes(5), T0.AddMinutes(10)),
        };
        var series = HistorySeries.Availability(intervals, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), T0.AddMinutes(10));
        Assert.Equal(100, series[0].Value);
        Assert.Equal(0, series[1].Value);
    }

    [Fact]
    public void EmptyOrInvertedWindowsGiveNoPoints()
    {
        Assert.Empty(HistorySeries.Rate(Array.Empty<TrafficMinute>(), T0, T0, TimeSpan.FromMinutes(1), HistorySeries.TrafficDirection.Download));
        Assert.Empty(HistorySeries.Latency(Array.Empty<TrafficMinute>(), T0, T0.AddMinutes(-1), TimeSpan.FromMinutes(1)));
    }
}

public class CsvFormatterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("", "")]
    public void QuotesPerRfc4180(string input, string expected) => Assert.Equal(expected, CsvFormatter.Cell(input));

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void NeutralisesSpreadsheetFormulas(string input, string expected) => Assert.Equal(expected, CsvFormatter.Cell(input));

    [Fact]
    public void NumbersAreInvariantAndUnquoted()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal("1.5", CsvFormatter.Cell(1.5));
            Assert.Equal("-3", CsvFormatter.Cell(-3)); // numbers are not text, so they are not neutralised
            Assert.Equal("1234567890123", CsvFormatter.Cell(1234567890123L));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DatesAreUtcIso8601WithMilliseconds() =>
        Assert.Equal("2026-06-01T10:20:30.123Z", CsvFormatter.Cell(new DateTimeOffset(2026, 6, 1, 12, 20, 30, 123, TimeSpan.FromHours(2))));

    [Fact]
    public void NullBoolAndGuid()
    {
        Assert.Equal(string.Empty, CsvFormatter.Cell(null));
        Assert.Equal("true", CsvFormatter.Cell(true));
        var g = Guid.NewGuid();
        Assert.Equal(g.ToString(), CsvFormatter.Cell(g));
    }

    [Fact]
    public void LineEndsWithCrLf() => Assert.Equal("a,\"b,c\",1\r\n", CsvFormatter.Line(new object[] { "a", "b,c", 1 }));
}
