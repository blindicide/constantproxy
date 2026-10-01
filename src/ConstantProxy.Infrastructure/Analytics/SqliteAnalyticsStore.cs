using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ConstantProxy.Infrastructure.Analytics;

public enum StoreOpenStatus
{
    Opened,
    Created,
    RecoveredFromCorruption,
}

public sealed record StoreOpenResult(SqliteAnalyticsStore Store, StoreOpenStatus Status, string? BackupPath = null, string? Error = null);

/// <summary>
/// SQLite-backed analytics (SPEC §21). One short-lived connection per operation (no pooling, so files are never
/// left locked), WAL journaling for crash safety, and timestamps stored as UTC unix milliseconds (SPEC §79).
/// </summary>
public sealed class SqliteAnalyticsStore : IAnalyticsStore
{
    private const string Source = "analytics";
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string connectionString;
    private readonly IAppLog log;

    private SqliteAnalyticsStore(string path, IAppLog log)
    {
        Path = path;
        this.log = log;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>
    /// Opens (creating and migrating as needed) the database at <paramref name="path"/>. A file that is not a valid
    /// database is set aside and replaced, so a damaged history never prevents the application from running.
    /// </summary>
    public static StoreOpenResult Open(string path, IAppLog? log = null, Func<DateTimeOffset>? now = null, IReadOnlyList<Migration>? migrations = null)
    {
        log ??= NullAppLog.Instance;
        now ??= () => DateTimeOffset.UtcNow;
        migrations ??= SchemaMigrations.All;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        try
        {
            var store = new SqliteAnalyticsStore(path, log);
            store.Initialize(migrations, existed);
            return new StoreOpenResult(store, existed ? StoreOpenStatus.Opened : StoreOpenStatus.Created);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            log.Error(Source, "The analytics database is damaged; setting it aside and starting a new one.", ex);
            var backup = $"{path}.corrupt-{now():yyyyMMddTHHmmssZ}";
            File.Move(path, backup, overwrite: true);
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
            var store = new SqliteAnalyticsStore(path, log);
            store.Initialize(migrations, existedBefore: false);
            return new StoreOpenResult(store, StoreOpenStatus.RecoveredFromCorruption, backup, ex.Message);
        }
    }

    public int SchemaVersion
    {
        get
        {
            using var connection = Connect();
            return MigrationRunner.GetVersion(connection);
        }
    }

    public void UpsertProfile(Guid id, string name, DateTimeOffset now)
    {
        using var connection = Connect();
        Exec(connection, """
            INSERT INTO profiles (id, name, created_utc, updated_utc) VALUES ($id, $name, $now, $now)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, updated_utc = excluded.updated_utc
            """, ("$id", id.ToString()), ("$name", name), ("$now", Ms(now)));
    }

    public long StartSession(Guid profileId, DateTimeOffset start)
    {
        using var connection = Connect();
        return ExecInsert(connection,
            "INSERT INTO sessions (profile_id, start_utc, updated_utc) VALUES ($p, $s, $s)",
            ("$p", profileId.ToString()), ("$s", Ms(start)));
    }

    public void UpdateSession(long sessionId, SessionProgress p, DateTimeOffset updatedUtc)
    {
        using var connection = Connect();
        Exec(connection, UpdateSql(ended: false), Params(sessionId, p, updatedUtc, null, null));
    }

    public void EndSession(long sessionId, SessionProgress p, DateTimeOffset end, SessionEndReason reason)
    {
        using var connection = Connect();
        Exec(connection, UpdateSql(ended: true), Params(sessionId, p, end, end, reason));
    }

    public long OpenInterval(long sessionId, IntervalKind kind, DateTimeOffset start)
    {
        using var connection = Connect();
        return ExecInsert(connection,
            "INSERT INTO state_intervals (session_id, kind, start_utc) VALUES ($s, $k, $t)",
            ("$s", sessionId), ("$k", (int)kind), ("$t", Ms(start)));
    }

    public void CloseInterval(long intervalId, DateTimeOffset end)
    {
        using var connection = Connect();
        Exec(connection, "UPDATE state_intervals SET end_utc = $t WHERE id = $id AND end_utc IS NULL", ("$t", Ms(end)), ("$id", intervalId));
    }

    public void RecordEvent(long? sessionId, Guid? profileId, DateTimeOffset timeUtc, string type, string? detail)
    {
        using var connection = Connect();
        Exec(connection,
            "INSERT INTO connection_events (session_id, profile_id, ts_utc, type, detail) VALUES ($s, $p, $t, $type, $d)",
            ("$s", sessionId), ("$p", profileId?.ToString()), ("$t", Ms(timeUtc)), ("$type", type), ("$d", Truncate(detail, 2000)));
    }

    public void AddTrafficMinutes(IReadOnlyList<TrafficMinute> minutes)
    {
        if (minutes.Count == 0)
        {
            return;
        }

        using var connection = Connect();
        using var transaction = connection.BeginTransaction();
        foreach (var m in minutes)
        {
            Exec(connection, """
                INSERT INTO traffic_samples (session_id, minute_utc, up_bytes, down_bytes, peak_up, peak_down, health_checks, health_failures, latency_sum_ms, latency_count, latency_max_ms)
                VALUES ($s, $m, $up, $down, $pu, $pd, $hc, $hf, $ls, $lc, $lm)
                ON CONFLICT(session_id, minute_utc) DO UPDATE SET
                    up_bytes = up_bytes + excluded.up_bytes,
                    down_bytes = down_bytes + excluded.down_bytes,
                    peak_up = MAX(peak_up, excluded.peak_up),
                    peak_down = MAX(peak_down, excluded.peak_down),
                    health_checks = health_checks + excluded.health_checks,
                    health_failures = health_failures + excluded.health_failures,
                    latency_sum_ms = latency_sum_ms + excluded.latency_sum_ms,
                    latency_count = latency_count + excluded.latency_count,
                    latency_max_ms = MAX(latency_max_ms, excluded.latency_max_ms)
                """,
                transaction,
                ("$s", m.SessionId), ("$m", Ms(m.MinuteUtc)), ("$up", m.UploadBytes), ("$down", m.DownloadBytes),
                ("$pu", m.PeakUploadRate), ("$pd", m.PeakDownloadRate), ("$hc", m.HealthChecks), ("$hf", m.HealthFailures),
                ("$ls", m.LatencySumMs), ("$lc", m.LatencyCount), ("$lm", m.LatencyMaxMs));
        }

        transaction.Commit();
    }

    public void AddHealthChecks(IReadOnlyList<HealthCheckRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var connection = Connect();
        using var transaction = connection.BeginTransaction();
        foreach (var r in rows)
        {
            Exec(connection, "INSERT INTO health_checks (session_id, ts_utc, success, latency_ms, error) VALUES ($s, $t, $ok, $l, $e)",
                transaction, ("$s", r.SessionId), ("$t", Ms(r.TimeUtc)), ("$ok", r.Success ? 1 : 0), ("$l", r.LatencyMs), ("$e", Truncate(r.Error, 500)));
        }

        transaction.Commit();
    }

    public int RecoverInterruptedSessions(DateTimeOffset now)
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction();
        var open = new List<(long Id, Guid Profile, long End)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, profile_id, MIN(MAX(updated_utc, start_utc), $now) FROM sessions WHERE end_utc IS NULL";
            select.Parameters.AddWithValue("$now", Ms(now));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                open.Add((reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2)));
            }
        }

        foreach (var (id, profile, end) in open)
        {
            Exec(connection, "UPDATE sessions SET end_utc = $e, end_reason = $r WHERE id = $id", transaction, ("$e", end), ("$r", (int)SessionEndReason.Interrupted), ("$id", id));
            Exec(connection, "UPDATE state_intervals SET end_utc = $e WHERE session_id = $id AND end_utc IS NULL", transaction, ("$e", end), ("$id", id));
            Exec(connection, "INSERT INTO connection_events (session_id, profile_id, ts_utc, type, detail) VALUES ($id, $p, $e, 'SessionInterrupted', 'The application stopped unexpectedly.')",
                transaction, ("$id", id), ("$p", profile.ToString()), ("$e", end));
        }

        transaction.Commit();
        return open.Count;
    }

    public PruneResult Prune(DateTimeOffset? cutoff, DateTimeOffset rawHealthCutoff)
    {
        using var connection = Connect();
        using var transaction = connection.BeginTransaction();
        var health = Exec(connection, "DELETE FROM health_checks WHERE ts_utc < $c", transaction, ("$c", Ms(rawHealthCutoff)));
        int sessions = 0, minutes = 0, events = 0;
        if (cutoff is { } c)
        {
            var ms = Ms(c);
            Exec(connection, "DELETE FROM state_intervals WHERE end_utc IS NOT NULL AND end_utc < $c", transaction, ("$c", ms));
            minutes = Exec(connection, "DELETE FROM traffic_samples WHERE minute_utc < $c", transaction, ("$c", ms));
            events = Exec(connection, "DELETE FROM connection_events WHERE ts_utc < $c", transaction, ("$c", ms));
            sessions = Exec(connection, "DELETE FROM sessions WHERE end_utc IS NOT NULL AND end_utc < $c", transaction, ("$c", ms));
        }

        transaction.Commit();
        return new PruneResult(sessions, minutes, events, health);
    }

    public IReadOnlyList<SessionRecord> GetSessions(Guid? profileId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, start_utc, end_utc, end_reason, connected_seconds, reconnect_count, failure_count, health_failures,
                   uploaded_bytes, downloaded_bytes, peak_upload, peak_download, avg_upload, avg_download
            FROM sessions
            WHERE start_utc < $to AND (end_utc IS NULL OR end_utc >= $from) AND ($p IS NULL OR profile_id = $p)
            ORDER BY start_utc, id
            """;
        Bind(command, ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        var result = new List<SessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SessionRecord(
                reader.GetInt64(0), Guid.Parse(reader.GetString(1)), FromMs(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : FromMs(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : (SessionEndReason)reader.GetInt32(4),
                reader.GetDouble(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
                reader.GetInt64(9), reader.GetInt64(10), reader.GetDouble(11), reader.GetDouble(12), reader.GetDouble(13), reader.GetDouble(14)));
        }

        return result;
    }

    public IReadOnlyList<TrafficMinute> GetTrafficMinutes(Guid? profileId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.session_id, t.minute_utc, t.up_bytes, t.down_bytes, t.peak_up, t.peak_down,
                   t.health_checks, t.health_failures, t.latency_sum_ms, t.latency_count, t.latency_max_ms
            FROM traffic_samples t JOIN sessions s ON s.id = t.session_id
            WHERE t.minute_utc >= $from AND t.minute_utc < $to AND ($p IS NULL OR s.profile_id = $p)
            ORDER BY t.minute_utc, t.session_id
            """;
        Bind(command, ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        var result = new List<TrafficMinute>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TrafficMinute(
                reader.GetInt64(0), FromMs(reader.GetInt64(1)), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetDouble(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetDouble(8), reader.GetInt32(9), reader.GetDouble(10)));
        }

        return result;
    }

    public IReadOnlyList<EventRecord> GetEvents(Guid? profileId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, ts_utc, type, detail FROM connection_events
            WHERE ts_utc >= $from AND ts_utc < $to AND ($p IS NULL OR profile_id = $p)
            ORDER BY ts_utc, id
            """;
        Bind(command, ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        var result = new List<EventRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EventRecord(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), FromMs(reader.GetInt64(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    public IReadOnlyList<StateInterval> GetIntervals(Guid? profileId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.session_id, i.kind, i.start_utc, i.end_utc
            FROM state_intervals i JOIN sessions s ON s.id = i.session_id
            WHERE i.start_utc < $to AND (i.end_utc IS NULL OR i.end_utc > $from) AND ($p IS NULL OR s.profile_id = $p)
            ORDER BY i.session_id, i.start_utc, i.id
            """;
        Bind(command, ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        var result = new List<StateInterval>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StateInterval(reader.GetInt64(0), reader.GetInt64(1), (IntervalKind)reader.GetInt32(2), FromMs(reader.GetInt64(3)), reader.IsDBNull(4) ? null : FromMs(reader.GetInt64(4))));
        }

        return result;
    }

    public TrafficTotals GetTrafficTotals(Guid? profileId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(t.up_bytes), 0), COALESCE(SUM(t.down_bytes), 0)
            FROM traffic_samples t JOIN sessions s ON s.id = t.session_id
            WHERE t.minute_utc >= $from AND t.minute_utc < $to AND ($p IS NULL OR s.profile_id = $p)
            """;
        Bind(command, ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new TrafficTotals(reader.GetInt64(0), reader.GetInt64(1));
    }

    public int GetEventCount(Guid? profileId, string type, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM connection_events WHERE type = $t AND ts_utc >= $from AND ts_utc < $to AND ($p IS NULL OR profile_id = $p)";
        Bind(command, ("$t", type), ("$from", Ms(from)), ("$to", Ms(to)), ("$p", profileId?.ToString()));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Initialize(IReadOnlyList<Migration> migrations, bool existedBefore)
    {
        using var connection = Connect();
        Exec(connection, "PRAGMA journal_mode = WAL");

        var current = MigrationRunner.GetVersion(connection);
        var latest = migrations.Count == 0 ? 0 : migrations[^1].Version;
        if (existedBefore && current > 0 && current < latest)
        {
            // Keep a copy before changing an existing database so a failed upgrade is never destructive.
            Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            var backup = $"{Path}.pre-v{latest}.bak";
            File.Copy(Path, backup, overwrite: true);
            log.Info(Source, $"Database backup written to {backup} before upgrading from version {current}.");
        }

        MigrationRunner.Migrate(connection, migrations, log);
        Exec(connection, "INSERT INTO settings (key, value) VALUES ('last_opened_utc', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$v", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string UpdateSql(bool ended) => $"""
        UPDATE sessions SET
            updated_utc = $u,
            connected_seconds = $cs, reconnect_count = $rc, failure_count = $fc, health_failures = $hf,
            uploaded_bytes = $ub, downloaded_bytes = $db, peak_upload = $pu, peak_download = $pd, avg_upload = $au, avg_download = $ad
            {(ended ? ", end_utc = $end, end_reason = $reason" : string.Empty)}
        WHERE id = $id
        """;

    private static (string, object?)[] Params(long id, SessionProgress p, DateTimeOffset updated, DateTimeOffset? end, SessionEndReason? reason)
    {
        var list = new List<(string, object?)>
        {
            ("$id", id), ("$u", Ms(updated)), ("$cs", p.ConnectedSeconds), ("$rc", p.ReconnectCount), ("$fc", p.FailureCount), ("$hf", p.HealthFailures),
            ("$ub", p.UploadedBytes), ("$db", p.DownloadedBytes), ("$pu", p.PeakUploadRate), ("$pd", p.PeakDownloadRate), ("$au", p.AverageUploadRate), ("$ad", p.AverageDownloadRate),
        };
        if (end is { } e)
        {
            list.Add(("$end", Ms(e)));
            list.Add(("$reason", (int)reason!.Value));
        }

        return list.ToArray();
    }

    private static int Exec(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Exec(connection, sql, null, parameters);

    private static int Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Bind(command, parameters);
        return command.ExecuteNonQuery();
    }

    private static long ExecInsert(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        Bind(command, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static long Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
