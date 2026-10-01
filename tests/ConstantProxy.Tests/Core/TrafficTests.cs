using System.Globalization;

namespace ConstantProxy.Tests.Core;

public class TrafficFormatterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(1, "1 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1023, "1023 B/s")]
    [InlineData(1024, "1.00 KB/s")]
    [InlineData(1536, "1.50 KB/s")]
    [InlineData(219136, "214 KB/s")]            // 214 KB/s from the SPEC mock-up
    [InlineData(1908408, "1.82 MB/s")]           // 1.82 MB/s from the SPEC mock-up
    [InlineData(10.5 * 1024, "10.5 KB/s")]
    [InlineData(99.96 * 1024, "100 KB/s")]
    [InlineData(1023.7 * 1024, "1.00 MB/s")]
    [InlineData(5.0 * 1024 * 1024 * 1024, "5.00 GB/s")]
    [InlineData(5000.0 * 1024 * 1024 * 1024, "5000 GB/s")]
    public void FormatsRates(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, TrafficFormatter.FormatRate(bytesPerSecond, Inv));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(4017233879L, "3.74 GB")]         // 3.74 GB from the SPEC mock-up
    [InlineData(647168000L, "617 MB")]
    [InlineData(1099511627776L, "1.00 TB")]
    [InlineData(5L * 1099511627776L, "5.00 TB")]
    [InlineData(2048L * 1099511627776L, "2048 TB")]
    public void FormatsTotals(long bytes, string expected) =>
        Assert.Equal(expected, TrafficFormatter.FormatBytes(bytes, Inv));

    [Fact]
    public void UsesTheSuppliedCultureForTheDecimalSeparator() =>
        Assert.Equal("1,50 KB/s", TrafficFormatter.FormatRate(1536, new CultureInfo("ru-RU")));

    [Fact]
    public void UsesSuppliedUnitLabels()
    {
        var ru = new TrafficUnitLabels(new[] { "Б/с", "КБ/с", "МБ/с", "ГБ/с" }, new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" });
        Assert.Equal("2.00 МБ/с", TrafficFormatter.FormatRate(2 * 1024 * 1024, Inv, ru));
        Assert.Equal("3 Б", TrafficFormatter.FormatBytes(3, Inv, ru));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5)]
    public void InvalidInputsFormatAsZero(double value) =>
        Assert.Equal("0 B/s", TrafficFormatter.FormatRate(value, Inv));
}

public class TrafficStatisticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrafficStatistics Started()
    {
        var s = new TrafficStatistics();
        s.Reset(T0);
        return s;
    }

    [Fact]
    public void BeforeAnySampleNothingIsAvailable()
    {
        var s = Started();
        Assert.False(s.IsAvailable);
        Assert.Null(s.CurrentDownloadRate);
        Assert.Null(s.SessionUploadBytes);
    }

    [Fact]
    public void ComputesCurrentRatesFromCumulativeDeltas()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(1000, 5000));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(1500, 9000));

        Assert.True(s.IsAvailable);
        Assert.Equal(500, s.CurrentUploadRate);
        Assert.Equal(4000, s.CurrentDownloadRate);
        Assert.Equal(1500, s.SessionUploadBytes);
        Assert.Equal(9000, s.SessionDownloadBytes);
    }

    [Fact]
    public void RatesAreNormalisedByTheElapsedTimeBetweenSamples()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(0, 0));
        s.Sample(T0.AddSeconds(3), new TrafficCounters(1000, 4000)); // 2 s later
        Assert.Equal(500, s.CurrentUploadRate);
        Assert.Equal(2000, s.CurrentDownloadRate);
    }

    [Fact]
    public void TracksPeaks()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(100, 100));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(1100, 9100));
        s.Sample(T0.AddSeconds(3), new TrafficCounters(1200, 9200));
        Assert.Equal(1000, s.PeakUploadRate);
        Assert.Equal(9000, s.PeakDownloadRate);
        Assert.Equal(100, s.CurrentUploadRate);
    }

    [Fact]
    public void AveragesOverTheSessionDuration()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(5), new TrafficCounters(1000, 10000));
        s.Sample(T0.AddSeconds(10), new TrafficCounters(2000, 30000));
        Assert.Equal(200, s.AverageUploadRate);
        Assert.Equal(3000, s.AverageDownloadRate);
    }

    [Fact]
    public void ZeroTrafficGivesZeroRates()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(0, 0));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(0, 0));
        Assert.Equal(0, s.CurrentDownloadRate);
        Assert.Equal(0, s.AverageUploadRate);
    }

    [Fact]
    public void UnavailableBackendNeverProducesNumbers()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(100, 100));
        s.Sample(T0.AddSeconds(2), null);
        Assert.False(s.IsAvailable);
        Assert.Null(s.CurrentUploadRate);
        Assert.Null(s.SessionDownloadBytes);
    }

    [Fact]
    public void CounterGoingBackwardsIsTreatedAsAReset()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(10_000, 10_000));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(300, 700));
        Assert.Equal(300, s.CurrentUploadRate);
        Assert.Equal(700, s.CurrentDownloadRate);
    }

    [Fact]
    public void DuplicateOrOutOfOrderTimestampsAreIgnored()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(2), new TrafficCounters(100, 100));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(999, 999));
        s.Sample(T0.AddSeconds(1), new TrafficCounters(999, 999));
        Assert.Equal(100, s.SessionUploadBytes);
        Assert.Single(s.History);
    }

    [Fact]
    public void HistoryIsBoundedAndOrdered()
    {
        var s = new TrafficStatistics(historyCapacity: 3);
        s.Reset(T0);
        for (var i = 1; i <= 5; i++)
        {
            s.Sample(T0.AddSeconds(i), new TrafficCounters(i * 10, i * 20));
        }

        Assert.Equal(new long[] { 30, 40, 50 }, s.History.Select(p => p.UploadTotal));
        Assert.Equal(new long[] { 40, 50 }, s.Recent(2).Select(p => p.UploadTotal));
        Assert.Equal(3, s.Recent(100).Count);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var s = Started();
        s.Sample(T0.AddSeconds(1), new TrafficCounters(100, 5000));
        s.Sample(T0.AddSeconds(2), new TrafficCounters(200, 9000));
        s.Reset(T0.AddSeconds(10));
        Assert.False(s.IsAvailable);
        Assert.Empty(s.History);
        Assert.Equal(0, s.PeakDownloadRate);
        Assert.Equal(0, s.AverageDownloadRate);
    }
}

public class TrafficSamplingServiceTests
{
    private sealed class ScriptedMonitor : ITrafficMonitor
    {
        public long Up;
        public long Down;
        public bool Available = true;
        public int ResetCalls;

        public string Name => "scripted";

        public bool IsAvailable => Available;

        public string? UnavailableReason => null;

        public SshListenOverride? PrepareBackend(Profile profile) => null;

        public void Start(Profile profile, SshListenOverride? backend)
        {
        }

        public void Stop()
        {
        }

        public void ResetSession()
        {
            ResetCalls++;
            Up = 0;
            Down = 0;
        }

        public TrafficCounters? ReadTotals() => Available ? new TrafficCounters(Up, Down) : null;
    }

    [Fact]
    public async Task SamplesOncePerReleasedIntervalAndComputesRates()
    {
        var clock = new FakeClock { BlockDelays = true };
        var monitor = new ScriptedMonitor();
        await using var service = new TrafficSamplingService(monitor, clock);
        var samples = 0;
        service.Sampled += _ => Interlocked.Increment(ref samples);

        service.StartSession();
        Assert.Equal(1, monitor.ResetCalls);
        await TestWait.Until(() => clock.RecordedDelays.Count == 1);

        monitor.Down = 2048;
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.ReleaseDelays();
        await TestWait.Until(() => Volatile.Read(ref samples) == 1);

        Assert.Equal(2048, service.Statistics.SessionDownloadBytes);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.RecordedDelays[0]);
    }

    [Fact]
    public async Task StopTakesOneFinalSampleAndStopsTheLoop()
    {
        var clock = new FakeClock { BlockDelays = true };
        var monitor = new ScriptedMonitor();
        var service = new TrafficSamplingService(monitor, clock);
        service.StartSession();
        await TestWait.Until(() => clock.RecordedDelays.Count == 1);

        monitor.Up = 777;
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Equal(777, service.Statistics.SessionUploadBytes);
        await service.StopAsync(); // idempotent
    }

    [Fact]
    public async Task StartingTwiceDoesNotStartASecondLoop()
    {
        var clock = new FakeClock { BlockDelays = true };
        var monitor = new ScriptedMonitor();
        await using var service = new TrafficSamplingService(monitor, clock);
        service.StartSession();
        service.StartSession();
        await TestWait.Until(() => clock.RecordedDelays.Count >= 1);
        await Task.Delay(30);
        Assert.Single(clock.RecordedDelays);
        Assert.Equal(1, monitor.ResetCalls);
    }

    [Fact]
    public async Task UnavailableMonitorYieldsUnavailableStatistics()
    {
        var clock = new FakeClock { BlockDelays = true };
        var monitor = new ScriptedMonitor { Available = false };
        var service = new TrafficSamplingService(monitor, clock);
        service.StartSession();
        await TestWait.Until(() => clock.RecordedDelays.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.StopAsync();
        Assert.False(service.Statistics.IsAvailable);
        Assert.Null(service.Statistics.CurrentDownloadRate);
    }

    [Fact]
    public void NullMonitorIsHonestlyUnavailable()
    {
        var monitor = new NullTrafficMonitor();
        Assert.False(monitor.IsAvailable);
        Assert.Null(monitor.ReadTotals());
        Assert.Null(monitor.PrepareBackend(new Profile()));
        Assert.NotNull(monitor.UnavailableReason);
    }
}
