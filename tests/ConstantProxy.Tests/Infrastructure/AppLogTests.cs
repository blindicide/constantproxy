namespace ConstantProxy.Tests.Infrastructure;

public class AppLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 6, 7, 8, 9, 123, TimeSpan.Zero);

    [Fact]
    public void FiltersBelowMinimumSeverity()
    {
        var log = new AppLog(null, LogSeverity.Information, now: () => Now);
        log.Debug("x", "hidden");
        log.Info("x", "shown");
        Assert.Equal("shown", Assert.Single(log.Snapshot()).Message);

        log.MinimumSeverity = LogSeverity.Debug; // "Verbose"
        log.Debug("x", "now visible");
        Assert.Equal(2, log.Snapshot().Count);
    }

    [Fact]
    public void MemoryRingKeepsOnlyTheNewestEntries()
    {
        var log = new AppLog(null, memoryCapacity: 3, now: () => Now);
        for (var i = 0; i < 5; i++)
        {
            log.Info("s", $"m{i}");
        }

        Assert.Equal(new[] { "m2", "m3", "m4" }, log.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void SnapshotCanBeFilteredBySource()
    {
        var log = new AppLog(null, LogSeverity.Debug, now: () => Now);
        log.Debug("ssh", "banner");
        log.Info("connection", "state");
        Assert.Equal("banner", Assert.Single(log.Snapshot("ssh")).Message);
    }

    [Fact]
    public void WritesFormattedLinesToFileAndCreatesTheDirectory()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "logs", "app.log");
        var log = new AppLog(file, now: () => Now);
        log.Warn("connection", "careful");
        log.Error("connection", "boom", new InvalidOperationException("why"));

        var lines = File.ReadAllLines(file);
        Assert.Equal("2026-05-06T07:08:09.123Z [WARN ] connection: careful", lines[0]);
        Assert.Contains("boom (InvalidOperationException: why)", lines[1]);
    }

    [Fact]
    public void RaisesEntryAddedAndSurvivesThrowingSubscribers()
    {
        var log = new AppLog(null, now: () => Now);
        var seen = new List<string>();
        log.EntryAdded += _ => throw new InvalidOperationException("subscriber bug");
        log.EntryAdded += e => seen.Add(e.Message);
        log.Info("s", "hello");
        Assert.Equal(new[] { "hello" }, seen);
    }

    [Fact]
    public void UnwritablePathNeverThrows()
    {
        using var dir = new TempDir();
        var blocker = dir.File("blocker");
        File.WriteAllText(blocker, "x"); // a file where a directory is needed
        var log = new AppLog(Path.Combine(blocker, "sub", "app.log"), now: () => Now);
        log.Info("s", "still fine");
        Assert.Single(log.Snapshot());
    }
}
