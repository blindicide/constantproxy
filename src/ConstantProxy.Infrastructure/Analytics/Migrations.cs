using Microsoft.Data.Sqlite;

namespace ConstantProxy.Infrastructure.Analytics;

/// <summary>One forward-only schema step. <see cref="Version"/> values must be 1, 2, 3, ... without gaps.</summary>
public sealed record Migration(int Version, string Description, Action<SqliteConnection> Apply);

public sealed class DatabaseTooNewException : Exception
{
    public DatabaseTooNewException(int databaseVersion, int supportedVersion)
        : base($"The analytics database is version {databaseVersion}, but this application only understands up to {supportedVersion}. It was probably created by a newer version.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }

    public int SupportedVersion { get; }
}

/// <summary>Applies pending migrations in order, each in its own transaction, tracked by <c>PRAGMA user_version</c> (SPEC §21).</summary>
public static class MigrationRunner
{
    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the number of migrations applied.</summary>
    public static int Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations, IAppLog? log = null)
    {
        Validate(migrations);
        var latest = migrations.Count == 0 ? 0 : migrations[^1].Version;
        var current = GetVersion(connection);
        if (current > latest)
        {
            throw new DatabaseTooNewException(current, latest);
        }

        var applied = 0;
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection);
            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = $"PRAGMA user_version = {migration.Version}"; // PRAGMA cannot take parameters; the value is an int
                stamp.ExecuteNonQuery();
            }

            transaction.Commit();
            log?.Info("analytics", $"Database migrated to version {migration.Version}: {migration.Description}");
            applied++;
        }

        return applied;
    }

    private static void Validate(IReadOnlyList<Migration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new ArgumentException($"Migrations must be numbered 1..n without gaps; found {migrations[i].Version} at position {i + 1}.", nameof(migrations));
            }
        }
    }
}

public static class SchemaMigrations
{
    public static IReadOnlyList<Migration> All { get; } = new[]
    {
        new Migration(1, "initial schema", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = InitialSchema;
            command.ExecuteNonQuery();
        }),
    };

    public static int Latest => All[^1].Version;

    // Timestamps are UTC unix milliseconds (SPEC §79).
    private const string InitialSchema = """
        CREATE TABLE profiles (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL
        );

        CREATE TABLE sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_id TEXT NOT NULL,
            start_utc INTEGER NOT NULL,
            end_utc INTEGER NULL,
            end_reason INTEGER NULL,
            updated_utc INTEGER NOT NULL,
            connected_seconds REAL NOT NULL DEFAULT 0,
            reconnect_count INTEGER NOT NULL DEFAULT 0,
            failure_count INTEGER NOT NULL DEFAULT 0,
            health_failures INTEGER NOT NULL DEFAULT 0,
            uploaded_bytes INTEGER NOT NULL DEFAULT 0,
            downloaded_bytes INTEGER NOT NULL DEFAULT 0,
            peak_upload REAL NOT NULL DEFAULT 0,
            peak_download REAL NOT NULL DEFAULT 0,
            avg_upload REAL NOT NULL DEFAULT 0,
            avg_download REAL NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_sessions_profile_start ON sessions(profile_id, start_utc);
        CREATE INDEX ix_sessions_end ON sessions(end_utc);

        CREATE TABLE state_intervals (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            start_utc INTEGER NOT NULL,
            end_utc INTEGER NULL
        );
        CREATE INDEX ix_intervals_session ON state_intervals(session_id, start_utc);
        CREATE INDEX ix_intervals_start ON state_intervals(start_utc);

        CREATE TABLE traffic_samples (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            minute_utc INTEGER NOT NULL,
            up_bytes INTEGER NOT NULL,
            down_bytes INTEGER NOT NULL,
            peak_up REAL NOT NULL,
            peak_down REAL NOT NULL,
            health_checks INTEGER NOT NULL,
            health_failures INTEGER NOT NULL,
            latency_sum_ms REAL NOT NULL,
            latency_count INTEGER NOT NULL,
            latency_max_ms REAL NOT NULL,
            PRIMARY KEY (session_id, minute_utc)
        ) WITHOUT ROWID;
        CREATE INDEX ix_traffic_minute ON traffic_samples(minute_utc);

        CREATE TABLE connection_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NULL REFERENCES sessions(id) ON DELETE CASCADE,
            profile_id TEXT NULL,
            ts_utc INTEGER NOT NULL,
            type TEXT NOT NULL,
            detail TEXT NULL
        );
        CREATE INDEX ix_events_ts ON connection_events(ts_utc);
        CREATE INDEX ix_events_profile ON connection_events(profile_id, ts_utc);

        CREATE TABLE health_checks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            ts_utc INTEGER NOT NULL,
            success INTEGER NOT NULL,
            latency_ms REAL NULL,
            error TEXT NULL
        );
        CREATE INDEX ix_health_ts ON health_checks(ts_utc);

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
