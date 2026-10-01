using Microsoft.Data.Sqlite;

namespace ConstantProxy.Tests.Analytics;

public class MigrationRunnerTests
{
    private static SqliteConnection Open(TempDir dir)
    {
        var connection = new SqliteConnection($"Data Source={dir.File("m.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private static Migration Create(int version, string table) =>
        new(version, table, c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE TABLE {table} (id INTEGER)";
            cmd.ExecuteNonQuery();
        });

    private static bool TableExists(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    [Fact]
    public void AppliesPendingMigrationsInOrderAndStampsTheVersion()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        var applied = MigrationRunner.Migrate(c, new[] { Create(1, "a"), Create(2, "b") });
        Assert.Equal(2, applied);
        Assert.Equal(2, MigrationRunner.GetVersion(c));
        Assert.True(TableExists(c, "a") && TableExists(c, "b"));
    }

    [Fact]
    public void RerunningIsANoOp()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        var set = new[] { Create(1, "a") };
        MigrationRunner.Migrate(c, set);
        Assert.Equal(0, MigrationRunner.Migrate(c, set));
    }

    [Fact]
    public void OnlyMissingMigrationsRunOnAnOlderDatabase()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        MigrationRunner.Migrate(c, new[] { Create(1, "a") });
        Assert.Equal(1, MigrationRunner.Migrate(c, new[] { Create(1, "a"), Create(2, "b") }));
        Assert.Equal(2, MigrationRunner.GetVersion(c));
    }

    [Fact]
    public void FailingMigrationRollsBackAndKeepsTheOldVersion()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        var broken = new Migration(2, "broken", conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE half (id INTEGER); THIS IS NOT SQL";
            cmd.ExecuteNonQuery();
        });
        MigrationRunner.Migrate(c, new[] { Create(1, "a") });

        Assert.ThrowsAny<SqliteException>(() => MigrationRunner.Migrate(c, new[] { Create(1, "a"), broken }));
        Assert.Equal(1, MigrationRunner.GetVersion(c));
        Assert.False(TableExists(c, "half"));
    }

    [Fact]
    public void DatabaseFromANewerVersionIsRefused()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        MigrationRunner.Migrate(c, new[] { Create(1, "a"), Create(2, "b") });
        var ex = Assert.Throws<DatabaseTooNewException>(() => MigrationRunner.Migrate(c, new[] { Create(1, "a") }));
        Assert.Equal((2, 1), (ex.DatabaseVersion, ex.SupportedVersion));
    }

    [Fact]
    public void MigrationNumbersMustBeContiguousFromOne()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        Assert.Throws<ArgumentException>(() => MigrationRunner.Migrate(c, new[] { Create(2, "a") }));
        Assert.Throws<ArgumentException>(() => MigrationRunner.Migrate(c, new[] { Create(1, "a"), Create(3, "c") }));
    }

    [Fact]
    public void ShippedMigrationsAreContiguousAndCreateTheSpecifiedTables()
    {
        using var dir = new TempDir();
        using var c = Open(dir);
        MigrationRunner.Migrate(c, SchemaMigrations.All);
        foreach (var table in new[] { "profiles", "sessions", "traffic_samples", "connection_events", "health_checks", "settings", "state_intervals" })
        {
            Assert.True(TableExists(c, table), table);
        }

        Assert.Equal(SchemaMigrations.Latest, MigrationRunner.GetVersion(c));
    }
}

public class SqliteAnalyticsStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly SessionProgress Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static SqliteAnalyticsStore NewStore(TempDir dir) => SqliteAnalyticsStore.Open(dir.File("a.db")).Store;

    [Fact]
    public void CreatesAndMigratesANewDatabase()
    {
        using var dir = new TempDir();
        var result = SqliteAnalyticsStore.Open(dir.File("a.db"));
        Assert.Equal(StoreOpenStatus.Created, result.Status);
        Assert.Equal(SchemaMigrations.Latest, result.Store.SchemaVersion);
        Assert.Equal(StoreOpenStatus.Opened, SqliteAnalyticsStore.Open(dir.File("a.db")).Status);
    }

    [Fact]
    public void SessionLifecycleRoundTripsAllFields()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.UpsertProfile(ProfileA, "Home", T0);
        var id = store.StartSession(ProfileA, T0);

        var open = Assert.Single(store.GetSessions(ProfileA, T0.AddDays(-1), T0.AddDays(1)));
        Assert.Null(open.EndUtc);
        Assert.Null(open.EndReason);

        var progress = new SessionProgress(3500.5, 2, 3, 4, 1_000_000, 5_000_000_000, 1500.5, 9_000_000.25, 100.5, 200.25);
        store.EndSession(id, progress, T0.AddHours(1), SessionEndReason.Failed);

        var s = Assert.Single(store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)));
        Assert.Equal((id, ProfileA, T0, T0.AddHours(1), SessionEndReason.Failed), (s.Id, s.ProfileId, s.StartUtc, s.EndUtc, s.EndReason));
        Assert.Equal(3500.5, s.ConnectedSeconds);
        Assert.Equal((2, 3, 4), (s.ReconnectCount, s.FailureCount, s.HealthFailures));
        Assert.Equal((1_000_000L, 5_000_000_000L), (s.UploadedBytes, s.DownloadedBytes));
        Assert.Equal((1500.5, 9_000_000.25, 100.5, 200.25), (s.PeakUploadRate, s.PeakDownloadRate, s.AverageUploadRate, s.AverageDownloadRate));
        Assert.Equal(TimeSpan.FromHours(1), s.Duration);
    }

    [Fact]
    public void UpdateSessionKeepsItOpenButSavesProgress()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var id = store.StartSession(ProfileA, T0);
        store.UpdateSession(id, Zero with { UploadedBytes = 42 }, T0.AddMinutes(1));
        var s = Assert.Single(store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)));
        Assert.Equal(42, s.UploadedBytes);
        Assert.Null(s.EndUtc);
    }

    [Fact]
    public void SessionQueriesFilterByProfileAndOverlap()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var a = store.StartSession(ProfileA, T0);
        store.EndSession(a, Zero, T0.AddHours(1), SessionEndReason.User);
        var b = store.StartSession(ProfileB, T0.AddHours(2));
        store.EndSession(b, Zero, T0.AddHours(3), SessionEndReason.User);

        Assert.Equal(new[] { a }, store.GetSessions(ProfileA, T0.AddDays(-1), T0.AddDays(1)).Select(s => s.Id));
        Assert.Equal(new[] { b }, store.GetSessions(ProfileB, T0.AddDays(-1), T0.AddDays(1)).Select(s => s.Id));
        Assert.Equal(2, store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)).Count);
        Assert.Empty(store.GetSessions(null, T0.AddHours(1.5), T0.AddHours(1.9)));          // gap between sessions
        Assert.Single(store.GetSessions(null, T0.AddHours(0.5), T0.AddHours(0.6)));          // inside the first
        Assert.Single(store.GetSessions(null, T0.AddHours(0.9), T0.AddHours(2.1)).Where(s => s.Id == a)); // straddles
    }

    [Fact]
    public void IntervalsOpenCloseAndOverlapQueries()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var session = store.StartSession(ProfileA, T0);
        var up = store.OpenInterval(session, IntervalKind.Up, T0);
        store.CloseInterval(up, T0.AddMinutes(10));
        store.OpenInterval(session, IntervalKind.Reconnecting, T0.AddMinutes(10)); // left open

        var all = store.GetIntervals(ProfileA, T0.AddHours(-1), T0.AddHours(1));
        Assert.Equal(new[] { IntervalKind.Up, IntervalKind.Reconnecting }, all.Select(i => i.Kind));
        Assert.Equal(T0.AddMinutes(10), all[0].End);
        Assert.Null(all[1].End);
        Assert.Single(store.GetIntervals(ProfileA, T0.AddMinutes(11), T0.AddHours(1))); // only the open one overlaps
        Assert.Empty(store.GetIntervals(ProfileB, T0.AddHours(-1), T0.AddHours(1)));

        store.CloseInterval(up, T0.AddMinutes(99)); // closing twice must not move the end
        Assert.Equal(T0.AddMinutes(10), store.GetIntervals(ProfileA, T0.AddHours(-1), T0.AddHours(1))[0].End);
    }

    [Fact]
    public void EventsAreStoredWithTheirTypeDetailAndProfile()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var session = store.StartSession(ProfileA, T0);
        store.RecordEvent(session, ProfileA, T0.AddSeconds(1), "Connected", null);
        store.RecordEvent(null, null, T0, "ApplicationStarted", "v1");
        store.RecordEvent(session, ProfileA, T0.AddSeconds(2), "ConnectionLost", "ssh.auth: it's \"quoted\"; DROP TABLE sessions;--");

        var all = store.GetEvents(null, T0.AddMinutes(-1), T0.AddMinutes(1));
        Assert.Equal(new[] { "ApplicationStarted", "Connected", "ConnectionLost" }, all.Select(e => e.Type));
        Assert.Equal("ssh.auth: it's \"quoted\"; DROP TABLE sessions;--", all[2].Detail);
        Assert.Single(store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1))); // the table survived

        Assert.Equal(2, store.GetEvents(ProfileA, T0.AddMinutes(-1), T0.AddMinutes(1)).Count);
        Assert.Equal(1, store.GetEventCount(ProfileA, "ConnectionLost", T0.AddMinutes(-1), T0.AddMinutes(1)));
        Assert.Equal(0, store.GetEventCount(ProfileB, "ConnectionLost", T0.AddMinutes(-1), T0.AddMinutes(1)));
    }

    [Fact]
    public void LongEventDetailIsTruncatedNotRejected()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.RecordEvent(null, null, T0, "X", new string('x', 10_000));
        Assert.Equal(2000, store.GetEvents(null, T0.AddMinutes(-1), T0.AddMinutes(1)).Single().Detail!.Length);
    }

    private static TrafficMinute Minute(long session, int minute, long up, long down, double peakUp = 0, double peakDown = 0, int checks = 0, int failures = 0, double latSum = 0, int latCount = 0, double latMax = 0) =>
        new(session, T0.AddMinutes(minute), up, down, peakUp, peakDown, checks, failures, latSum, latCount, latMax);

    [Fact]
    public void TrafficMinutesAreAdditiveOnConflictAndKeepTheLargerPeaks()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var session = store.StartSession(ProfileA, T0);

        store.AddTrafficMinutes(new[] { Minute(session, 0, 100, 1000, 10, 100, 2, 1, 50, 1, 50) });
        store.AddTrafficMinutes(new[] { Minute(session, 0, 50, 500, 30, 60, 3, 0, 90, 3, 40) }); // flushed again within the same minute

        var m = Assert.Single(store.GetTrafficMinutes(null, T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal((150L, 1500L), (m.UploadBytes, m.DownloadBytes));
        Assert.Equal((30.0, 100.0), (m.PeakUploadRate, m.PeakDownloadRate));
        Assert.Equal((5, 1), (m.HealthChecks, m.HealthFailures));
        Assert.Equal((140.0, 4, 50.0), (m.LatencySumMs, m.LatencyCount, m.LatencyMaxMs));
        Assert.Equal(35.0, m.AverageLatencyMs!.Value);
    }

    [Fact]
    public void TrafficQueriesRespectWindowAndProfileAndTotalsSum()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var a = store.StartSession(ProfileA, T0);
        var b = store.StartSession(ProfileB, T0);
        store.AddTrafficMinutes(new[] { Minute(a, 0, 1, 10), Minute(a, 1, 2, 20), Minute(a, 60, 4, 40), Minute(b, 0, 100, 1000) });

        Assert.Equal(new TrafficTotals(7, 70), store.GetTrafficTotals(ProfileA, T0.AddHours(-1), T0.AddHours(2)));
        Assert.Equal(new TrafficTotals(3, 30), store.GetTrafficTotals(ProfileA, T0, T0.AddMinutes(2)));
        Assert.Equal(new TrafficTotals(100, 1000), store.GetTrafficTotals(ProfileB, T0.AddHours(-1), T0.AddHours(2)));
        Assert.Equal(new TrafficTotals(107, 1070), store.GetTrafficTotals(null, T0.AddHours(-1), T0.AddHours(2)));
        Assert.Equal(new TrafficTotals(0, 0), store.GetTrafficTotals(null, T0.AddDays(5), T0.AddDays(6)));
        Assert.Equal(3, store.GetTrafficMinutes(ProfileA, T0, T0.AddDays(1)).Count);
    }

    [Fact]
    public void EmptyBatchesAreIgnored()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.AddTrafficMinutes(Array.Empty<TrafficMinute>());
        store.AddHealthChecks(Array.Empty<HealthCheckRow>());
    }

    [Fact]
    public void RecoverInterruptedSessionsClosesThemAtTheirLastKnownActivity()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var crashed = store.StartSession(ProfileA, T0);
        store.OpenInterval(crashed, IntervalKind.Up, T0);
        store.UpdateSession(crashed, Zero with { UploadedBytes = 9 }, T0.AddMinutes(7)); // last periodic flush
        var finished = store.StartSession(ProfileA, T0.AddHours(5));
        store.EndSession(finished, Zero, T0.AddHours(6), SessionEndReason.User);

        var repaired = store.RecoverInterruptedSessions(T0.AddDays(1));

        Assert.Equal(1, repaired);
        var sessions = store.GetSessions(null, T0.AddDays(-1), T0.AddDays(2));
        var s = sessions.Single(x => x.Id == crashed);
        Assert.Equal(T0.AddMinutes(7), s.EndUtc);
        Assert.Equal(SessionEndReason.Interrupted, s.EndReason);
        Assert.Equal(9, s.UploadedBytes);
        Assert.Equal(T0.AddMinutes(7), store.GetIntervals(null, T0.AddDays(-1), T0.AddDays(2)).Single().End);
        Assert.Equal(SessionEndReason.User, sessions.Single(x => x.Id == finished).EndReason); // untouched
        Assert.Equal(1, store.GetEventCount(ProfileA, "SessionInterrupted", T0.AddDays(-1), T0.AddDays(2)));
        Assert.Equal(0, store.RecoverInterruptedSessions(T0.AddDays(1))); // idempotent
    }

    [Fact]
    public void RecoveryNeverEndsASessionInTheFuture()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var id = store.StartSession(ProfileA, T0);
        store.UpdateSession(id, Zero, T0.AddDays(30)); // clock was wrong once
        store.RecoverInterruptedSessions(T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), store.GetSessions(null, T0.AddDays(-1), T0.AddDays(60)).Single().EndUtc);
    }

    [Fact]
    public void PruneRemovesOldDataAndKeepsRecentAndOpenSessions()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var old = store.StartSession(ProfileA, T0.AddDays(-100));
        store.OpenInterval(old, IntervalKind.Up, T0.AddDays(-100));
        store.AddTrafficMinutes(new[] { new TrafficMinute(old, T0.AddDays(-100), 1, 1, 0, 0, 0, 0, 0, 0, 0) });
        store.RecordEvent(old, ProfileA, T0.AddDays(-100), "Connected", null);
        store.EndSession(old, Zero, T0.AddDays(-99), SessionEndReason.User);

        var recent = store.StartSession(ProfileA, T0.AddDays(-1));
        store.AddTrafficMinutes(new[] { new TrafficMinute(recent, T0.AddDays(-1), 5, 5, 0, 0, 0, 0, 0, 0, 0) });
        store.EndSession(recent, Zero, T0, SessionEndReason.User);
        var stillOpen = store.StartSession(ProfileA, T0.AddDays(-200)); // open: must never be pruned

        var result = store.Prune(T0.AddDays(-90), T0.AddDays(-7));

        Assert.Equal(1, result.Sessions);
        Assert.Equal(new[] { recent, stillOpen }.OrderBy(x => x), store.GetSessions(null, T0.AddDays(-400), T0.AddDays(1)).Select(s => s.Id).OrderBy(x => x));
        Assert.Equal(new TrafficTotals(5, 5), store.GetTrafficTotals(null, T0.AddDays(-400), T0.AddDays(1)));
        Assert.Empty(store.GetEvents(null, T0.AddDays(-400), T0.AddDays(-50)));
        Assert.Empty(store.GetIntervals(null, T0.AddDays(-400), T0.AddDays(-50)).Where(i => i.SessionId == old));
    }

    [Fact]
    public void PruneWithoutACutoffKeepsEverythingButStillTrimsRawHealthRows()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var s = store.StartSession(ProfileA, T0.AddDays(-400));
        store.EndSession(s, Zero, T0.AddDays(-399), SessionEndReason.User);
        store.AddHealthChecks(new[]
        {
            new HealthCheckRow(s, T0.AddDays(-30), true, 12, null),
            new HealthCheckRow(s, T0.AddHours(-1), false, null, "timeout"),
        });

        var result = store.Prune(null, T0.AddDays(-7));

        Assert.Equal(new PruneResult(0, 0, 0, 1), result);
        Assert.Single(store.GetSessions(null, T0.AddDays(-500), T0));
    }

    [Fact]
    public void DeletingASessionCascadesToItsRows()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var id = store.StartSession(ProfileA, T0.AddDays(-100));
        store.OpenInterval(id, IntervalKind.Up, T0.AddDays(-100));
        store.AddTrafficMinutes(new[] { Minute(id, -100 * 24 * 60, 1, 1) });
        store.AddHealthChecks(new[] { new HealthCheckRow(id, T0.AddDays(-100), true, 1, null) });
        store.EndSession(id, Zero, T0.AddDays(-99), SessionEndReason.User);

        store.Prune(T0.AddDays(-90), T0.AddDays(-200)); // raw cutoff older, so only the cascade can remove health rows

        using var c = new SqliteConnection($"Data Source={store.Path};Pooling=False");
        c.Open();
        foreach (var table in new[] { "state_intervals", "traffic_samples", "health_checks" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void CorruptDatabaseIsSetAsideAndReplaced()
    {
        using var dir = new TempDir();
        var path = dir.File("a.db");
        File.WriteAllText(path, "this is definitely not a sqlite database, just text that is long enough to be read as a header");

        var result = SqliteAnalyticsStore.Open(path, now: () => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        Assert.Equal(StoreOpenStatus.RecoveredFromCorruption, result.Status);
        Assert.Equal(path + ".corrupt-20260102T030405Z", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(SchemaMigrations.Latest, result.Store.SchemaVersion);
        result.Store.StartSession(ProfileA, T0); // the new database is usable
    }

    [Fact]
    public void UpgradingAnOlderDatabaseKeepsDataAndWritesABackup()
    {
        using var dir = new TempDir();
        var path = dir.File("a.db");
        var v1 = SchemaMigrations.All.Take(1).ToList();
        var oldStore = SqliteAnalyticsStore.Open(path, migrations: v1).Store;
        var id = oldStore.StartSession(ProfileA, T0);
        oldStore.EndSession(id, Zero with { UploadedBytes = 777 }, T0.AddHours(1), SessionEndReason.User);

        var v2 = new Migration(2, "add note column", c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE sessions ADD COLUMN note TEXT NULL";
            cmd.ExecuteNonQuery();
        });
        var upgraded = SqliteAnalyticsStore.Open(path, migrations: v1.Append(v2).ToList());

        Assert.Equal(StoreOpenStatus.Opened, upgraded.Status);
        Assert.Equal(2, upgraded.Store.SchemaVersion);
        Assert.Equal(777, upgraded.Store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)).Single().UploadedBytes);
        Assert.True(File.Exists(path + ".pre-v2.bak"));
    }

    [Fact]
    public void DatabaseFromAFutureVersionIsNotDamagedOrReplaced()
    {
        using var dir = new TempDir();
        var path = dir.File("a.db");
        var v1 = SchemaMigrations.All.Take(1).ToList();
        var future = v1.Append(new Migration(2, "future", c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE future (x INTEGER)";
            cmd.ExecuteNonQuery();
        })).ToList();
        SqliteAnalyticsStore.Open(path, migrations: future);

        Assert.Throws<DatabaseTooNewException>(() => SqliteAnalyticsStore.Open(path, migrations: v1));
        Assert.True(File.Exists(path));
        Assert.Equal(2, SqliteAnalyticsStore.Open(path, migrations: future).Store.SchemaVersion);
    }

    [Fact]
    public void WritesFromSeveralThreadsDoNotCorruptOrDeadlock()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var id = store.StartSession(ProfileA, T0);
        Parallel.For(0, 40, i => store.RecordEvent(id, ProfileA, T0.AddSeconds(i), "E", i.ToString()));
        Assert.Equal(40, store.GetEvents(null, T0.AddMinutes(-1), T0.AddHours(1)).Count);
    }

    [Fact]
    public void ProfileUpsertRenamesInPlace()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.UpsertProfile(ProfileA, "Old", T0);
        store.UpsertProfile(ProfileA, "New", T0.AddHours(1));
        using var c = new SqliteConnection($"Data Source={store.Path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, created_utc, updated_utc FROM profiles";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("New", r.GetString(0));
        Assert.Equal(T0.ToUnixTimeMilliseconds(), r.GetInt64(1));
        Assert.Equal(T0.AddHours(1).ToUnixTimeMilliseconds(), r.GetInt64(2));
        Assert.False(r.Read());
    }

    [Fact]
    public void TimestampsAreStoredAsUtcMilliseconds()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var local = new DateTimeOffset(2026, 6, 1, 15, 0, 0, TimeSpan.FromHours(3)); // 12:00 UTC
        var id = store.StartSession(ProfileA, local);
        store.EndSession(id, Zero, local.AddMinutes(5), SessionEndReason.User);
        var s = store.GetSessions(null, T0.AddDays(-1), T0.AddDays(1)).Single();
        Assert.Equal(TimeSpan.Zero, s.StartUtc.Offset);
        Assert.Equal(T0, s.StartUtc);
    }
}
