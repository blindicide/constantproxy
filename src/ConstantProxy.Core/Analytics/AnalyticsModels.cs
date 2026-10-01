namespace ConstantProxy.Core.Analytics;

/// <summary>
/// How a stretch of session time is classified for availability (SPEC §19). Time with no session at all
/// (the user disconnected, or the application was not running) is simply absent from the data.
/// </summary>
public enum IntervalKind
{
    /// <summary>Connected and healthy.</summary>
    Up,

    /// <summary>Connected but health checks are failing.</summary>
    Degraded,

    /// <summary>Lost the connection and (re)starting it.</summary>
    Reconnecting,

    /// <summary>The connection failed and is waiting for the user.</summary>
    Failed,

    /// <summary>The initial connect before the first successful connection of a session; not an outage.</summary>
    Setup,

    /// <summary>The user asked to stop; not an outage.</summary>
    UserDisconnected,
}

public enum SessionEndReason
{
    /// <summary>The user disconnected.</summary>
    User,

    /// <summary>The session ended in the Failed state.</summary>
    Failed,

    /// <summary>The application exited while connected.</summary>
    Shutdown,

    /// <summary>The application crashed or was killed; detected on the next launch (SPEC §82).</summary>
    Interrupted,
}

public sealed record StateInterval(long Id, long SessionId, IntervalKind Kind, DateTimeOffset Start, DateTimeOffset? End);

public sealed record SessionRecord(
    long Id,
    Guid ProfileId,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    SessionEndReason? EndReason,
    double ConnectedSeconds,
    int ReconnectCount,
    int FailureCount,
    int HealthFailures,
    long UploadedBytes,
    long DownloadedBytes,
    double PeakUploadRate,
    double PeakDownloadRate,
    double AverageUploadRate,
    double AverageDownloadRate)
{
    public TimeSpan? Duration => EndUtc is null ? null : EndUtc - StartUtc;
}

/// <summary>One-minute aggregate (SPEC §17): traffic, peak rates and health-check results.</summary>
public sealed record TrafficMinute(
    long SessionId,
    DateTimeOffset MinuteUtc,
    long UploadBytes,
    long DownloadBytes,
    double PeakUploadRate,
    double PeakDownloadRate,
    int HealthChecks,
    int HealthFailures,
    double LatencySumMs,
    int LatencyCount,
    double LatencyMaxMs)
{
    public double? AverageLatencyMs => LatencyCount == 0 ? null : LatencySumMs / LatencyCount;
}

public sealed record EventRecord(long Id, long? SessionId, DateTimeOffset TimeUtc, string Type, string? Detail);

public sealed record HealthCheckRow(long SessionId, DateTimeOffset TimeUtc, bool Success, double? LatencyMs, string? Error);

public sealed record TrafficTotals(long UploadBytes, long DownloadBytes);

public sealed record SessionProgress(
    double ConnectedSeconds,
    int ReconnectCount,
    int FailureCount,
    int HealthFailures,
    long UploadedBytes,
    long DownloadedBytes,
    double PeakUploadRate,
    double PeakDownloadRate,
    double AverageUploadRate,
    double AverageDownloadRate);

public sealed record AnalyticsSettings
{
    public bool StoreHistory { get; init; } = true;

    /// <summary>Days of history to keep; 0 means forever (SPEC §80).</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>Raw per-check health rows are only kept this long; per-minute aggregates follow the main retention.</summary>
    public int RawHealthDays { get; init; } = 7;
}
