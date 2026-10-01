namespace ConstantProxy.Core.Analytics;

/// <summary>
/// Persistent analytics storage (SPEC §21). All timestamps are UTC (SPEC §79). Implementations may throw on I/O
/// problems; <see cref="AnalyticsRecorder"/> contains such failures so analytics can never break supervision.
/// </summary>
public interface IAnalyticsStore
{
    void UpsertProfile(Guid id, string name, DateTimeOffset now);

    long StartSession(Guid profileId, DateTimeOffset start);

    void UpdateSession(long sessionId, SessionProgress progress, DateTimeOffset updatedUtc);

    void EndSession(long sessionId, SessionProgress progress, DateTimeOffset end, SessionEndReason reason);

    long OpenInterval(long sessionId, IntervalKind kind, DateTimeOffset start);

    void CloseInterval(long intervalId, DateTimeOffset end);

    void RecordEvent(long? sessionId, Guid? profileId, DateTimeOffset timeUtc, string type, string? detail);

    /// <summary>Adds to the minute row (creating it if needed): bytes and counts sum, peaks and maxima take the larger value.</summary>
    void AddTrafficMinutes(IReadOnlyList<TrafficMinute> minutes);

    void AddHealthChecks(IReadOnlyList<HealthCheckRow> rows);

    /// <summary>Closes sessions left open by a crash and returns how many were repaired.</summary>
    int RecoverInterruptedSessions(DateTimeOffset now);

    /// <summary>Deletes data older than <paramref name="cutoff"/> and raw health rows older than <paramref name="rawHealthCutoff"/>.</summary>
    PruneResult Prune(DateTimeOffset? cutoff, DateTimeOffset rawHealthCutoff);

    IReadOnlyList<SessionRecord> GetSessions(Guid? profileId, DateTimeOffset from, DateTimeOffset to);

    IReadOnlyList<TrafficMinute> GetTrafficMinutes(Guid? profileId, DateTimeOffset from, DateTimeOffset to);

    IReadOnlyList<EventRecord> GetEvents(Guid? profileId, DateTimeOffset from, DateTimeOffset to);

    /// <summary>Intervals overlapping the window (open intervals have a null end).</summary>
    IReadOnlyList<StateInterval> GetIntervals(Guid? profileId, DateTimeOffset from, DateTimeOffset to);

    TrafficTotals GetTrafficTotals(Guid? profileId, DateTimeOffset from, DateTimeOffset to);

    int GetEventCount(Guid? profileId, string type, DateTimeOffset from, DateTimeOffset to);
}

public sealed record PruneResult(int Sessions, int TrafficMinutes, int Events, int HealthChecks);
