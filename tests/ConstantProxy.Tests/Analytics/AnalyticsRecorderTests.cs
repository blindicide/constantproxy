namespace ConstantProxy.Tests.Analytics;

public class AnalyticsRecorderTests
{
    private sealed class ScriptedMonitor : ITrafficMonitor
    {
        public long Up;
        public long Down;

        public string Name => "scripted";

        public bool IsAvailable => true;

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
            Up = 0;
            Down = 0;
        }

        public TrafficCounters? ReadTotals() => new(Up, Down);
    }

    private sealed class Rig
    {
        public const int SampleSeconds = 7;
        public const int FlushSeconds = 60;

        public Rig(AnalyticsSettings? settings = null, bool withSampling = false, bool withHealth = false)
        {
            // One clock drives everything: only the sampler (7 s) and the flush loop (60 s) block.
            Clock = new FakeClock { BlockWhen = d => d == TimeSpan.FromSeconds(SampleSeconds) || d == TimeSpan.FromSeconds(FlushSeconds) };
            Launcher = new FakeLauncher(Clock);
            Verifier = new ScriptedVerifier();
            Socks = new FakeSocksProbe();
            Monitor = new ScriptedMonitor();
            Manager = new ConnectionManager(Launcher, Verifier, Clock, random: () => 0.5, socksProbe: withHealth ? Socks : null);
            Store = new RecordingStore();
            Sampling = withSampling ? new TrafficSamplingService(Monitor, Clock, TimeSpan.FromSeconds(SampleSeconds)) : null;
            Recorder = new AnalyticsRecorder(Store, Clock, settings: settings, flushInterval: TimeSpan.FromSeconds(FlushSeconds));
            Recorder.Attach(Manager, Sampling);
            Profile = new Profile { Name = "Home", Host = "h", Port = 10080 };
            Profile.Monitoring.Enabled = withHealth;
        }

        public FakeClock Clock { get; }

        public FakeLauncher Launcher { get; }

        public ScriptedVerifier Verifier { get; }

        public FakeSocksProbe Socks { get; }

        public ScriptedMonitor Monitor { get; }

        public ConnectionManager Manager { get; }

        public RecordingStore Store { get; }

        public TrafficSamplingService? Sampling { get; }

        public AnalyticsRecorder Recorder { get; }

        public Profile Profile { get; }

        public Task WaitForState(ConnectionState s) => TestWait.Until(() => Manager.State == s, what: $"state {s} (now {Manager.State})");

        public async Task Settle() => await Recorder.FlushAsync();

        public IntervalKind[] Kinds(int sessionIndex = 0)
        {
            var id = Store.Locked(s => s.Sessions[sessionIndex].Id);
            return Store.Locked(s => s.Intervals.Where(i => i.SessionId == id).Select(i => i.Kind).ToArray());
        }
    }

    [Fact]
    public async Task RecordsASimpleSessionWithItsIntervalsAndEndReason()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.Advance(TimeSpan.FromSeconds(100));
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        var session = Assert.Single(rig.Store.Sessions);
        Assert.Equal(rig.Profile.Id, session.ProfileId);
        Assert.Equal(SessionEndReason.User, session.Reason);
        Assert.NotNull(session.End);
        Assert.Equal(new[] { IntervalKind.Setup, IntervalKind.Up, IntervalKind.UserDisconnected }, rig.Kinds());
        Assert.All(rig.Store.Intervals, i => Assert.NotNull(i.End)); // nothing left open
        Assert.Equal(("Home", rig.Profile.Id), (rig.Store.Profiles[0].Name, rig.Store.Profiles[0].Id));
        Assert.True(session.LastProgress!.ConnectedSeconds >= 99);
    }

    [Fact]
    public async Task IntervalsAreContiguous()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        var intervals = rig.Store.Locked(s => s.Intervals.ToList());
        for (var i = 1; i < intervals.Count; i++)
        {
            Assert.Equal(intervals[i - 1].End, intervals[i].Start);
        }
    }

    [Fact]
    public async Task EventsAreStoredWithSessionAndProfile()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        var types = rig.Store.Locked(s => s.Events.Select(e => e.Type).ToList());
        Assert.Contains("ConnectionRequested", types);
        Assert.Contains("SshStarted", types);
        Assert.Contains("Connected", types);
        Assert.Contains("ManualDisconnect", types);
        var sessionId = rig.Store.Sessions[0].Id;
        Assert.All(rig.Store.Locked(s => s.Events.ToList()), e => Assert.Equal(rig.Profile.Id, e.ProfileId));
        Assert.Contains(rig.Store.Locked(s => s.Events.ToList()), e => e.SessionId == sessionId && e.Type == "Connected");
    }

    [Fact]
    public async Task UnexpectedExitCountsAsReconnectAndFailureAndShowsAsReconnectingTime()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Exit(255);
        await TestWait.Until(() => rig.Launcher.Processes.Count == 2 && rig.Manager.State == ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Contains(IntervalKind.Reconnecting, rig.Kinds());
        var progress = rig.Store.Sessions[0].LastProgress!;
        Assert.Equal(1, progress.ReconnectCount);
        Assert.Equal(1, progress.FailureCount);
        Assert.Equal(1, rig.Store.Locked(s => s.Events.Count(e => e.Type == "ConnectionLost")));
    }

    [Fact]
    public async Task ManualReconnectIsNotCountedAsAFailure()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.ReconnectNowAsync();
        await TestWait.Until(() => rig.Launcher.Processes.Count == 2 && rig.Manager.State == ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Equal(0, rig.Store.Sessions[0].LastProgress!.FailureCount);
        Assert.Equal(1, rig.Store.Sessions[0].LastProgress!.ReconnectCount);
    }

    [Fact]
    public async Task InitialConnectIsSetupButARetryBeforeTheFirstSuccessIsAnOutage()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.TimedOut; // the first attempts never come up
        await rig.Manager.ConnectAsync(rig.Profile);
        await TestWait.Until(() => rig.Launcher.Processes.Count >= 3);
        rig.Verifier.Outcome = StartupOutcome.Ready;
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        var kinds = rig.Kinds();
        Assert.Equal(IntervalKind.Setup, kinds[0]);
        Assert.Contains(IntervalKind.Reconnecting, kinds);
        Assert.Equal(IntervalKind.Up, kinds[^2]);
        // after the first failure nothing is "Setup" any more
        Assert.DoesNotContain(IntervalKind.Setup, kinds.Skip(1));
    }

    [Fact]
    public async Task FailedSessionEndsWithTheFailedReasonWhenTheUserDisconnects()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "Permission denied (publickey).");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Equal(SessionEndReason.Failed, rig.Store.Sessions[0].Reason);
        Assert.Contains(IntervalKind.Failed, rig.Kinds());
        Assert.Equal(1, rig.Store.Sessions[0].LastProgress!.FailureCount);
    }

    [Fact]
    public async Task ConnectingAgainAfterAFailureEndsTheOldSessionAndStartsANewOne()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "Host key verification failed.");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);

        rig.Verifier.Outcome = StartupOutcome.Ready;
        rig.Verifier.BeforeOutcome = null;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Equal(2, rig.Store.Sessions.Count);
        Assert.Equal(SessionEndReason.Failed, rig.Store.Sessions[0].Reason);
        Assert.Equal(SessionEndReason.User, rig.Store.Sessions[1].Reason);
        Assert.All(rig.Store.Locked(s => s.Intervals.ToList()), i => Assert.NotNull(i.End));
    }

    [Fact]
    public async Task ApplicationShutdownIsDistinguishedFromAUserDisconnect()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Recorder.MarkShuttingDown();
        await rig.Manager.DisposeAsync();
        await rig.Recorder.DisposeAsync();

        Assert.Equal(SessionEndReason.Shutdown, rig.Store.Sessions[0].Reason);
    }

    [Fact]
    public async Task DisposingTheRecorderWhileConnectedStillClosesTheSession()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        await rig.Recorder.DisposeAsync();

        var session = rig.Store.Sessions[0];
        Assert.NotNull(session.End);
        Assert.NotNull(session.Reason);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task TrafficSamplesBecomeOneMinuteRowsAndFeedTheSessionTotals()
    {
        var rig = new Rig(withSampling: true);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Sampling!.StartSession();
        await TestWait.Until(() => rig.Clock.RecordedDelays.Contains(TimeSpan.FromSeconds(Rig.SampleSeconds)));

        var samples = 0;
        rig.Sampling.Sampled += _ => Interlocked.Increment(ref samples);
        for (var i = 1; i <= 3; i++)
        {
            rig.Monitor.Up += 1000;
            rig.Monitor.Down += 7000;
            rig.Clock.Advance(TimeSpan.FromSeconds(Rig.SampleSeconds));
            rig.Clock.ReleaseDelays();
            await TestWait.Until(() => Volatile.Read(ref samples) == i, what: $"sample {i}");
        }

        await rig.Manager.DisconnectAsync();
        await rig.Sampling.StopAsync();
        await rig.Settle();

        var progress = rig.Store.Sessions[0].LastProgress!;
        Assert.Equal((3000L, 21000L), (progress.UploadedBytes, progress.DownloadedBytes));
        Assert.True(progress.PeakDownloadRate > 0);
        Assert.Equal(21000, rig.Store.Locked(s => s.Minutes.Sum(m => m.DownloadBytes)));
        Assert.Equal(3000, rig.Store.Locked(s => s.Minutes.Sum(m => m.UploadBytes)));
    }

    [Fact]
    public async Task HealthChecksAreAggregatedPerMinuteAndStoredRaw()
    {
        var rig = new Rig(withHealth: true);
        rig.Socks.Enqueue(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(20)), SocksProbeResult.Ok(TimeSpan.FromMilliseconds(40)), SocksProbeResult.Fail(ProbeFailure.Timeout, "timeout"));
        await rig.Manager.ConnectAsync(rig.Profile);
        await TestWait.Until(() => rig.Socks.Calls >= 4);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Equal(3, rig.Store.Locked(s => s.Health.Count));
        Assert.Equal(1, rig.Store.Locked(s => s.Health.Count(h => !h.Success)));
        var minute = rig.Store.Locked(s => s.Minutes.ToList()).Single();
        Assert.Equal((3, 1, 2), (minute.HealthChecks, minute.HealthFailures, minute.LatencyCount));
        Assert.Equal(30, minute.AverageLatencyMs!.Value, 6);
        Assert.Equal(1, rig.Store.Sessions[0].LastProgress!.HealthFailures);
    }

    [Fact]
    public async Task PeriodicFlushPersistsProgressWithoutEndingTheSession()
    {
        var rig = new Rig();
        rig.Recorder.Start();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await TestWait.Until(() => rig.Clock.RecordedDelays.Contains(TimeSpan.FromSeconds(Rig.FlushSeconds)));

        rig.Clock.Advance(TimeSpan.FromSeconds(Rig.FlushSeconds));
        rig.Clock.ReleaseDelays();
        await TestWait.Until(() => rig.Store.Locked(s => s.Sessions[0].Updates) >= 1, what: "progress flush");

        Assert.Null(rig.Store.Sessions[0].End);
        Assert.True(rig.Store.Sessions[0].LastProgress!.ConnectedSeconds >= 59);
        await rig.Manager.DisposeAsync();
        await rig.Recorder.DisposeAsync();
    }

    [Fact]
    public async Task StartRepairsInterruptedSessionsAndAppliesRetention()
    {
        var rig = new Rig(new AnalyticsSettings { RetentionDays = 30, RawHealthDays = 3 });
        rig.Recorder.Start();
        await rig.Settle();

        Assert.Equal(1, rig.Store.Recoveries);
        var (cutoff, raw) = Assert.Single(rig.Store.Prunes);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-30), cutoff);
        Assert.Equal(rig.Clock.UtcNow.AddDays(-3), raw);
        await rig.Recorder.DisposeAsync();
    }

    [Fact]
    public async Task RetentionOfZeroMeansForever()
    {
        var rig = new Rig(new AnalyticsSettings { RetentionDays = 0 });
        rig.Recorder.Start();
        await rig.Settle();
        Assert.Null(rig.Store.Prunes.Single().Cutoff);
        await rig.Recorder.DisposeAsync();
    }

    [Fact]
    public async Task DatabaseFailuresNeverBreakConnectionSupervision()
    {
        var rig = new Rig();
        rig.Store.FailEvents = new InvalidOperationException("disk full");

        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Launcher.Last!.Exit(255);
        await TestWait.Until(() => rig.Launcher.Processes.Count == 2 && rig.Manager.State == ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
        Assert.Equal(SessionEndReason.User, rig.Store.Sessions[0].Reason); // later writes still succeeded
    }

    [Fact]
    public async Task DisabledHistoryTouchesNoStorage()
    {
        var rig = new Rig(new AnalyticsSettings { StoreHistory = false });
        rig.Recorder.Start();
        rig.Recorder.RecordApplicationEvent(ConnectionEventType.ApplicationStarted);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.DisconnectAsync();
        await rig.Settle();

        Assert.Empty(rig.Store.Sessions);
        Assert.Empty(rig.Store.Events);
        Assert.Equal(0, rig.Store.Recoveries);
        Assert.Empty(rig.Store.Prunes);
    }

    [Fact]
    public async Task ApplicationEventsBelongToNoSession()
    {
        var rig = new Rig();
        rig.Recorder.RecordApplicationEvent(ConnectionEventType.ApplicationStarted, "1.2.3");
        await rig.Settle();
        var e = Assert.Single(rig.Store.Events);
        Assert.Equal((null, null, "ApplicationStarted", "1.2.3"), (e.SessionId, e.ProfileId, e.Type, e.Detail));
    }

    [Fact]
    public async Task EventsBeforeAnySessionAreStillKept()
    {
        var rig = new Rig();
        rig.Profile.Host = ""; // invalid: fails immediately but still produces a session with a Failed interval
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        await rig.Settle();
        Assert.Contains(rig.Store.Locked(s => s.Events.ToList()), e => e.Type == "ConnectionFailed");
        await rig.Manager.DisposeAsync();
    }
}
