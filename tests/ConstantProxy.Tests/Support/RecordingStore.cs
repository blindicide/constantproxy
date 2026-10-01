namespace ConstantProxy.Tests.Support;

/// <summary>In-memory <see cref="IAnalyticsStore"/> that records what the recorder asked for.</summary>
public sealed class RecordingStore : IAnalyticsStore
{
    private readonly object gate = new();
    private long nextId = 1;

    public record Session(long Id, Guid ProfileId, DateTimeOffset Start)
    {
        public DateTimeOffset? End { get; set; }

        public SessionEndReason? Reason { get; set; }

        public SessionProgress? LastProgress { get; set; }

        public int Updates { get; set; }
    }

    public record Interval(long Id, long SessionId, IntervalKind Kind, DateTimeOffset Start)
    {
        public DateTimeOffset? End { get; set; }
    }

    public List<Session> Sessions { get; } = new();

    public List<Interval> Intervals { get; } = new();

    public List<(long? SessionId, Guid? ProfileId, DateTimeOffset Time, string Type, string? Detail)> Events { get; } = new();

    public List<TrafficMinute> Minutes { get; } = new();

    public List<HealthCheckRow> Health { get; } = new();

    public List<(DateTimeOffset? Cutoff, DateTimeOffset RawCutoff)> Prunes { get; } = new();

    public List<(Guid Id, string Name)> Profiles { get; } = new();

    public int Recoveries { get; private set; }

    /// <summary>When set, <see cref="RecordEvent"/> throws this (to prove analytics failures are contained).</summary>
    public Exception? FailEvents { get; set; }

    public T Locked<T>(Func<RecordingStore, T> read)
    {
        lock (gate)
        {
            return read(this);
        }
    }

    public void UpsertProfile(Guid id, string name, DateTimeOffset now)
    {
        lock (gate)
        {
            Profiles.Add((id, name));
        }
    }

    public long StartSession(Guid profileId, DateTimeOffset start)
    {
        lock (gate)
        {
            var s = new Session(nextId++, profileId, start);
            Sessions.Add(s);
            return s.Id;
        }
    }

    public void UpdateSession(long sessionId, SessionProgress progress, DateTimeOffset updatedUtc)
    {
        lock (gate)
        {
            var s = Sessions.Single(x => x.Id == sessionId);
            s.LastProgress = progress;
            s.Updates++;
        }
    }

    public void EndSession(long sessionId, SessionProgress progress, DateTimeOffset end, SessionEndReason reason)
    {
        lock (gate)
        {
            var s = Sessions.Single(x => x.Id == sessionId);
            s.LastProgress = progress;
            s.End = end;
            s.Reason = reason;
        }
    }

    public long OpenInterval(long sessionId, IntervalKind kind, DateTimeOffset start)
    {
        lock (gate)
        {
            var i = new Interval(nextId++, sessionId, kind, start);
            Intervals.Add(i);
            return i.Id;
        }
    }

    public void CloseInterval(long intervalId, DateTimeOffset end)
    {
        lock (gate)
        {
            Intervals.Single(i => i.Id == intervalId).End = end;
        }
    }

    public void RecordEvent(long? sessionId, Guid? profileId, DateTimeOffset timeUtc, string type, string? detail)
    {
        if (FailEvents is not null)
        {
            throw FailEvents;
        }

        lock (gate)
        {
            Events.Add((sessionId, profileId, timeUtc, type, detail));
        }
    }

    public void AddTrafficMinutes(IReadOnlyList<TrafficMinute> minutes)
    {
        lock (gate)
        {
            Minutes.AddRange(minutes);
        }
    }

    public void AddHealthChecks(IReadOnlyList<HealthCheckRow> rows)
    {
        lock (gate)
        {
            Health.AddRange(rows);
        }
    }

    public int RecoverInterruptedSessions(DateTimeOffset now)
    {
        lock (gate)
        {
            Recoveries++;
            return 0;
        }
    }

    public PruneResult Prune(DateTimeOffset? cutoff, DateTimeOffset rawHealthCutoff)
    {
        lock (gate)
        {
            Prunes.Add((cutoff, rawHealthCutoff));
            return new PruneResult(0, 0, 0, 0);
        }
    }

    public IReadOnlyList<SessionRecord> GetSessions(Guid? profileId, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public IReadOnlyList<TrafficMinute> GetTrafficMinutes(Guid? profileId, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public IReadOnlyList<EventRecord> GetEvents(Guid? profileId, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public IReadOnlyList<StateInterval> GetIntervals(Guid? profileId, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public TrafficTotals GetTrafficTotals(Guid? profileId, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public int GetEventCount(Guid? profileId, string type, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();

    public IReadOnlyDictionary<Guid, string> GetProfileNames() => throw new NotSupportedException();
}
