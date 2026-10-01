using System.Threading.Channels;

namespace ConstantProxy.Core.Analytics;

/// <summary>
/// Turns connection activity into stored analytics (SPEC §18, §21, §78, §82). Event handlers only enqueue work; a
/// single background worker owns all session bookkeeping and database writes, so the UI and the connection
/// supervisor never wait on the database, and a failing database is logged but never breaks supervision.
/// </summary>
public sealed class AnalyticsRecorder : IAsyncDisposable
{
    private const string Source = "analytics";

    private readonly IAnalyticsStore store;
    private readonly IClock clock;
    private readonly IAppLog log;
    private readonly AnalyticsSettings settings;
    private readonly TimeSpan flushInterval;
    private readonly Channel<Action> queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private readonly CancellationTokenSource stop = new();
    private Task? flushLoop;
    private volatile bool shuttingDown;
    private bool disposed;

    // Worker-thread state only.
    private Session? session;
    private DateTimeOffset lastPrune = DateTimeOffset.MinValue;

    public AnalyticsRecorder(IAnalyticsStore store, IClock? clock = null, IAppLog? log = null, AnalyticsSettings? settings = null, TimeSpan? flushInterval = null)
    {
        this.store = store;
        this.clock = clock ?? SystemClock.Instance;
        this.log = log ?? NullAppLog.Instance;
        this.settings = settings ?? new AnalyticsSettings();
        this.flushInterval = flushInterval ?? TimeSpan.FromMinutes(1);
        worker = Task.Run(WorkAsync);
    }

    /// <summary>Subscribes to the manager and (optionally) the traffic sampler. Does nothing when history is disabled.</summary>
    public void Attach(ConnectionManager manager, TrafficSamplingService? sampling)
    {
        if (!settings.StoreHistory)
        {
            return;
        }

        manager.StateChanged += change =>
        {
            var profile = manager.ActiveProfile;
            Enqueue(() => OnStateChanged(change, profile));
        };
        manager.EventRaised += e =>
        {
            var profile = manager.ActiveProfile;
            Enqueue(() => OnEvent(e, profile?.Id));
        };
        manager.HealthChecked += result => Enqueue(() => OnHealth(result));
        if (sampling is not null)
        {
            sampling.Sampled += point => Enqueue(() => OnSample(point));
        }
    }

    /// <summary>Repairs sessions a crash left open and applies retention, then starts the once-a-minute flush.</summary>
    public void Start()
    {
        if (!settings.StoreHistory)
        {
            return;
        }

        Enqueue(() =>
        {
            var repaired = store.RecoverInterruptedSessions(clock.UtcNow);
            if (repaired > 0)
            {
                log.Warn(Source, $"{repaired} unfinished session(s) from a previous run were marked as interrupted.");
            }

            PruneIfDue(force: true);
        });
        flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>Records an application-level event that belongs to no session (start, exit).</summary>
    public void RecordApplicationEvent(ConnectionEventType type, string? detail = null)
    {
        if (!settings.StoreHistory)
        {
            return;
        }

        var time = clock.UtcNow;
        Enqueue(() => store.RecordEvent(null, null, time, type.ToString(), detail));
    }

    /// <summary>Marks that the application itself is exiting, so a session ending now is recorded as a shutdown.</summary>
    public void MarkShuttingDown() => shuttingDown = true;

    /// <summary>Completes once everything queued so far has been written.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(() => done.TrySetResult()))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await stop.CancelAsync().ConfigureAwait(false);
        if (flushLoop is not null)
        {
            try
            {
                await flushLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        // An application exit while a session is open must still produce a closed session.
        Enqueue(() => EndSession(clock.UtcNow, shuttingDown ? SessionEndReason.Shutdown : SessionEndReason.User));
        queue.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        stop.Dispose();
    }

    private void Enqueue(Action work) => queue.Writer.TryWrite(work);

    private async Task WorkAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Analytics must never take the application down; record the problem and keep going.
                log.Error(Source, "Analytics write failed.", ex);
            }
        }
    }

    private async Task FlushLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            await clock.Delay(flushInterval, stop.Token).ConfigureAwait(false);
            Enqueue(() =>
            {
                Flush(final: false);
                PruneIfDue(force: false);
            });
        }
    }

    private void OnStateChanged(StateChange change, Profile? profile)
    {
        if (change.New == ConnectionState.Starting && change.Old is ConnectionState.Disconnected or ConnectionState.Failed)
        {
            if (session is not null)
            {
                EndSession(change.AtUtc, SessionEndReason.Failed);
            }

            StartSession(change.AtUtc, profile);
            return;
        }

        if (session is null)
        {
            return;
        }

        if (change.New == ConnectionState.Disconnected)
        {
            CloseInterval(change.AtUtc);
            var reason = session.LastKind == IntervalKind.Failed ? SessionEndReason.Failed
                : shuttingDown ? SessionEndReason.Shutdown
                : SessionEndReason.User;
            EndSession(change.AtUtc, reason);
            return;
        }

        var kind = KindFor(change.New, session);
        if (change.New is ConnectionState.Reconnecting or ConnectionState.Failed)
        {
            session.Retrying = true;
        }

        if (change.New == ConnectionState.Connected)
        {
            session.EverConnected = true;
        }

        if (session.IntervalId is not null && session.LastKind == kind)
        {
            return; // Starting then Connecting are one stretch of the same kind
        }

        CloseInterval(change.AtUtc);
        session.IntervalId = store.OpenInterval(session.Id, kind, change.AtUtc);
        session.LastKind = kind;
        session.IntervalStart = change.AtUtc;
    }

    private static IntervalKind KindFor(ConnectionState state, Session s) => state switch
    {
        ConnectionState.Connected => IntervalKind.Up,
        ConnectionState.Degraded => IntervalKind.Degraded,
        ConnectionState.Failed => IntervalKind.Failed,
        ConnectionState.Stopping => IntervalKind.UserDisconnected,
        ConnectionState.Reconnecting => IntervalKind.Reconnecting,
        _ => s.EverConnected || s.Retrying ? IntervalKind.Reconnecting : IntervalKind.Setup, // Starting, Connecting
    };

    private void StartSession(DateTimeOffset start, Profile? profile)
    {
        var profileId = profile?.Id ?? Guid.Empty;
        store.UpsertProfile(profileId, profile?.Name ?? string.Empty, start);
        var id = store.StartSession(profileId, start);
        session = new Session(id, profileId, start);
        session.IntervalId = store.OpenInterval(id, IntervalKind.Setup, start);
        session.LastKind = IntervalKind.Setup;
        session.IntervalStart = start;
    }

    private void CloseInterval(DateTimeOffset at)
    {
        if (session is not { IntervalId: { } intervalId } s)
        {
            return;
        }

        store.CloseInterval(intervalId, at);
        if (s.LastKind is IntervalKind.Up or IntervalKind.Degraded)
        {
            s.ConnectedSeconds += Math.Max((at - s.IntervalStart).TotalSeconds, 0);
        }

        s.IntervalId = null;
    }

    private void EndSession(DateTimeOffset end, SessionEndReason reason)
    {
        if (session is not { } s)
        {
            return;
        }

        CloseInterval(end);
        Flush(final: true);
        store.EndSession(s.Id, Progress(s, end), end, reason);
        session = null;
    }

    private void OnEvent(ConnectionEvent e, Guid? profileId)
    {
        store.RecordEvent(session?.Id, session?.ProfileId ?? profileId, e.TimeUtc, e.Type.ToString(), e.Detail);
        if (session is null)
        {
            return;
        }

        switch (e.Type)
        {
            case ConnectionEventType.ReconnectAttempt:
                session.Reconnects++;
                break;
            case ConnectionEventType.ConnectionLost:
                session.Failures++;
                break;
        }
    }

    private void OnHealth(HealthCheckResult result)
    {
        if (session is not { } s)
        {
            return;
        }

        s.Aggregator.AddHealth(result.TimeUtc, result.Success, result.Latency);
        s.HealthBuffer.Add(new HealthCheckRow(s.Id, result.TimeUtc, result.Success, result.Latency?.TotalMilliseconds, result.Success ? null : result.Error));
        if (!result.Success)
        {
            s.HealthFailures++;
        }
    }

    private void OnSample(TrafficPoint? point)
    {
        if (session is not { } s || point is null)
        {
            return;
        }

        s.Aggregator.AddTraffic(point);
        s.PeakUp = Math.Max(s.PeakUp, point.UploadRate);
        s.PeakDown = Math.Max(s.PeakDown, point.DownloadRate);
        s.UploadBytes = point.UploadTotal;
        s.DownloadBytes = point.DownloadTotal;
    }

    private void Flush(bool final)
    {
        if (session is not { } s)
        {
            return;
        }

        var now = clock.UtcNow;
        var minutes = final ? s.Aggregator.TakeAll(s.Id) : s.Aggregator.TakeCompleted(s.Id, now);
        if (minutes.Count > 0)
        {
            store.AddTrafficMinutes(minutes);
        }

        if (s.HealthBuffer.Count > 0)
        {
            store.AddHealthChecks(s.HealthBuffer.ToArray());
            s.HealthBuffer.Clear();
        }

        if (!final)
        {
            store.UpdateSession(s.Id, Progress(s, now), now); // keeps a crash from losing the whole session's figures
        }
    }

    private void PruneIfDue(bool force)
    {
        var now = clock.UtcNow;
        if (!force && now - lastPrune < TimeSpan.FromHours(24))
        {
            return;
        }

        lastPrune = now;
        DateTimeOffset? cutoff = settings.RetentionDays > 0 ? now.AddDays(-settings.RetentionDays) : null;
        var rawCutoff = now.AddDays(-Math.Max(settings.RawHealthDays, 1));
        var result = store.Prune(cutoff, rawCutoff);
        if (result.Sessions + result.TrafficMinutes + result.Events + result.HealthChecks > 0)
        {
            log.Info(Source, $"Retention removed {result.Sessions} session(s), {result.TrafficMinutes} traffic row(s), {result.Events} event(s), {result.HealthChecks} health check(s).");
        }
    }

    private static SessionProgress Progress(Session s, DateTimeOffset now)
    {
        var connected = s.ConnectedSeconds;
        if (s.IntervalId is not null && s.LastKind is IntervalKind.Up or IntervalKind.Degraded)
        {
            connected += Math.Max((now - s.IntervalStart).TotalSeconds, 0);
        }

        var seconds = Math.Max((now - s.Start).TotalSeconds, 0);
        return new SessionProgress(
            connected,
            s.Reconnects,
            s.Failures,
            s.HealthFailures,
            s.UploadBytes,
            s.DownloadBytes,
            s.PeakUp,
            s.PeakDown,
            seconds > 0 ? s.UploadBytes / seconds : 0,
            seconds > 0 ? s.DownloadBytes / seconds : 0);
    }

    private sealed class Session
    {
        public Session(long id, Guid profileId, DateTimeOffset start)
        {
            Id = id;
            ProfileId = profileId;
            Start = start;
        }

        public long Id { get; }

        public Guid ProfileId { get; }

        public DateTimeOffset Start { get; }

        public long? IntervalId { get; set; }

        public IntervalKind LastKind { get; set; }

        public DateTimeOffset IntervalStart { get; set; }

        public bool EverConnected { get; set; }

        public bool Retrying { get; set; }

        public double ConnectedSeconds { get; set; }

        public int Reconnects { get; set; }

        public int Failures { get; set; }

        public int HealthFailures { get; set; }

        public double PeakUp { get; set; }

        public double PeakDown { get; set; }

        public long UploadBytes { get; set; }

        public long DownloadBytes { get; set; }

        public MinuteAggregator Aggregator { get; } = new();

        public List<HealthCheckRow> HealthBuffer { get; } = new();
    }
}
