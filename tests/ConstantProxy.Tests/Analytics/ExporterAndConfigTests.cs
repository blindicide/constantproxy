using System.Text.Json;

namespace ConstantProxy.Tests.Analytics;

public class AnalyticsExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SqliteAnalyticsStore Seeded(TempDir dir)
    {
        var store = SqliteAnalyticsStore.Open(dir.File("a.db")).Store;
        var id = store.StartSession(ProfileId, T0);
        store.EndSession(id, new SessionProgress(3000, 2, 1, 4, 1000, 5000, 20.5, 90.25, 10, 50), T0.AddHours(1), SessionEndReason.User);
        store.AddTrafficMinutes(new[] { new TrafficMinute(id, T0, 100, 200, 5, 9, 3, 1, 60, 2, 40) });
        store.RecordEvent(id, ProfileId, T0, "ConnectionLost", "=HYPERLINK(\"http://evil\")");
        return store;
    }

    [Fact]
    public void ExportsThreeCsvFilesWithHeadersAndRows()
    {
        using var dir = new TempDir();
        var files = new AnalyticsExporter(Seeded(dir)).Export(dir.File("out"), ExportFormat.Csv, null, T0.AddDays(-1), T0.AddDays(1));

        Assert.Equal(new[] { "constantproxy-sessions.csv", "constantproxy-traffic.csv", "constantproxy-connection-events.csv" }, files.Select(Path.GetFileName));
        var sessions = File.ReadAllLines(files[0]);
        Assert.Equal(2, sessions.Length);
        Assert.StartsWith("﻿session_id,profile_id,start_utc,end_utc,end_reason,duration_seconds,connected_seconds,disconnected_seconds", sessions[0]);
        Assert.Contains("2026-06-01T12:00:00.000Z,2026-06-01T13:00:00.000Z,User,3600,3000,600,2,1,4,1000,5000,10,50,20.5,90.25", sessions[1]);

        var traffic = File.ReadAllLines(files[1]);
        Assert.Equal("1,2026-06-01T12:00:00.000Z,100,200,5,9,3,1,30,40", traffic[1]);
    }

    [Fact]
    public void CsvCellsThatLookLikeFormulasAreNeutralised()
    {
        using var dir = new TempDir();
        var files = new AnalyticsExporter(Seeded(dir)).Export(dir.File("out"), ExportFormat.Csv, null, T0.AddDays(-1), T0.AddDays(1));
        var events = File.ReadAllText(files[2]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", events);
        Assert.DoesNotContain(",=HYPERLINK", events);
    }

    [Fact]
    public void JsonExportIsStructuredAndCamelCased()
    {
        using var dir = new TempDir();
        var files = new AnalyticsExporter(Seeded(dir)).Export(dir.File("out"), ExportFormat.Json, ProfileId, T0.AddDays(-1), T0.AddDays(1));

        Assert.All(files, f => Assert.EndsWith(".json", f));
        using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
        var session = doc.RootElement[0];
        Assert.Equal(1000, session.GetProperty("uploadedBytes").GetInt64());
        Assert.Equal("user", session.GetProperty("endReason").GetString());
        Assert.Equal(ProfileId, session.GetProperty("profileId").GetGuid());
        Assert.False(File.ReadAllBytes(files[0]).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })); // no BOM for JSON
    }

    [Fact]
    public void ProfileFilterLimitsTheExport()
    {
        using var dir = new TempDir();
        var store = Seeded(dir);
        var files = new AnalyticsExporter(store).Export(dir.File("out"), ExportFormat.Csv, Guid.NewGuid(), T0.AddDays(-1), T0.AddDays(1));
        Assert.Single(File.ReadAllLines(files[0])); // header only
        Assert.Single(File.ReadAllLines(files[1]));
        Assert.Single(File.ReadAllLines(files[2]));
    }

    [Fact]
    public void OpenSessionExportsWithoutAnEndOrDuration()
    {
        using var dir = new TempDir();
        var store = SqliteAnalyticsStore.Open(dir.File("b.db")).Store;
        store.StartSession(ProfileId, T0);
        var csv = AnalyticsExporter.SessionsCsv(store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)));
        var row = csv.Split("\r\n")[1].Split(',');
        Assert.Equal(string.Empty, row[3]); // end_utc
        Assert.Equal(string.Empty, row[5]); // duration
    }

    [Fact]
    public void CustomPrefixIsUsed()
    {
        using var dir = new TempDir();
        var files = new AnalyticsExporter(Seeded(dir)).Export(dir.File("out"), ExportFormat.Csv, null, T0.AddDays(-1), T0.AddDays(1), "home");
        Assert.All(files, f => Assert.StartsWith("home-", Path.GetFileName(f)));
    }
}

public class AnalyticsConfigTests
{
    [Fact]
    public void DefaultsKeepHistoryFor90Days()
    {
        var a = new AppConfig().Analytics;
        Assert.True(a.StoreHistory);
        Assert.Equal(90, a.RetentionDays);
        Assert.Equal(string.Empty, a.DatabasePath);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(365, true)]
    [InlineData(-1, false)]
    [InlineData(100000, false)]
    public void RetentionValuesAreValidated(int days, bool ok) =>
        Assert.Equal(ok, ProfileValidator.ValidateAnalytics(new AnalyticsConfig { RetentionDays = days }).IsValid);

    [Fact]
    public void DatabasePathMustLiveInAnExistingFolder()
    {
        var bad = new AnalyticsConfig { DatabasePath = Path.Combine("nowhere", "db.sqlite") };
        Assert.Contains(ProfileValidator.ValidateAnalytics(bad, _ => false).Errors, e => e.Code == "database.path");
        Assert.True(ProfileValidator.ValidateAnalytics(bad, _ => true).IsValid);
        Assert.True(ProfileValidator.ValidateAnalytics(new AnalyticsConfig { DatabasePath = "db.sqlite" }, _ => false).IsValid); // relative file name: no folder to check
        Assert.True(ProfileValidator.ValidateAnalytics(new AnalyticsConfig(), _ => false).IsValid);
    }

    [Fact]
    public void NullAnalyticsSectionInAFileIsRepaired()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "analytics": null, "profiles": [ { "host": "x" } ] }""");
        var config = new ConfigurationService(path).Load().Config;
        Assert.NotNull(config.Analytics);
        Assert.Equal(90, config.Analytics.RetentionDays);
    }

    [Fact]
    public void AnalyticsSettingsRoundTrip()
    {
        using var dir = new TempDir();
        var service = new ConfigurationService(dir.File("config.json"));
        var config = AppConfig.CreateDefault();
        config.Analytics = new AnalyticsConfig { StoreHistory = false, RetentionDays = 180, DatabasePath = "/data/x.db" };
        service.Save(config);
        var loaded = service.Load().Config.Analytics;
        Assert.False(loaded.StoreHistory);
        Assert.Equal(180, loaded.RetentionDays);
        Assert.Equal("/data/x.db", loaded.DatabasePath);
    }
}
