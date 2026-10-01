namespace ConstantProxy.Tests.Core;

public class ConnectionTrafficTests
{
    private sealed class RecordingMonitor : ITrafficMonitor
    {
        public List<string> Calls { get; } = new();

        public SshListenOverride? Backend { get; set; } = new("127.0.0.1", 45454);

        public TrafficMonitorException? FailStart { get; set; }

        public string Name => "recording";

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public SshListenOverride? PrepareBackend(Profile profile)
        {
            Calls.Add("prepare");
            return Backend;
        }

        public void Start(Profile profile, SshListenOverride? backend)
        {
            Calls.Add("start");
            if (FailStart is not null)
            {
                throw FailStart;
            }
        }

        public void Stop() => Calls.Add("stop");

        public void ResetSession() => Calls.Add("reset");

        public TrafficCounters? ReadTotals() => new(1, 2);
    }

    private static (ConnectionManager Manager, FakeLauncher Launcher, RecordingMonitor Monitor, ScriptedVerifier Verifier, FakeClock Clock) Rig()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var monitor = new RecordingMonitor();
        var verifier = new ScriptedVerifier();
        return (new ConnectionManager(launcher, verifier, clock, trafficMonitor: monitor), launcher, monitor, verifier, clock);
    }

    private static Profile Profile() => new() { Host = "h", Port = 10080 };

    [Fact]
    public async Task BridgeModeMovesSshToTheInternalEndpointAndStartsTheMonitorOnceReady()
    {
        var (manager, launcher, monitor, _, _) = Rig();
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => manager.State == ConnectionState.Connected);

        var args = launcher.Specs.Single().Arguments.ToList();
        Assert.Equal("127.0.0.1:45454", args[args.IndexOf("-D") + 1]);
        Assert.Equal(new[] { "reset", "prepare", "start" }, monitor.Calls);
        await manager.DisposeAsync();
        Assert.Equal("stop", monitor.Calls.Last());
    }

    [Fact]
    public async Task OffModeUsesTheConfiguredPortDirectlyAndNeverTouchesTheMonitorBackend()
    {
        var (manager, launcher, monitor, _, _) = Rig();
        var profile = Profile();
        profile.TrafficMode = TrafficMode.Off;
        await manager.ConnectAsync(profile);
        await TestWait.Until(() => manager.State == ConnectionState.Connected);

        var args = launcher.Specs.Single().Arguments.ToList();
        Assert.Equal("127.0.0.1:10080", args[args.IndexOf("-D") + 1]);
        Assert.DoesNotContain("prepare", monitor.Calls);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task VerifierIsToldWhichEndpointSshListensOn()
    {
        var clock = new FakeClock();
        StartupContext? seen = null;
        var verifier = new CapturingVerifier(c => seen = c);
        var manager = new ConnectionManager(new FakeLauncher(clock), verifier, clock, trafficMonitor: new RecordingMonitor());
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => seen is not null);
        Assert.Equal(45454, seen!.ListenPort);
        Assert.Equal("127.0.0.1", seen.ListenAddress);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task MonitorFailingToStartKillsSshAndRetries()
    {
        var (manager, launcher, monitor, _, _) = Rig();
        monitor.FailStart = new TrafficMonitorException("port busy");
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => launcher.Processes.Count >= 2);
        Assert.True(launcher.Processes[0].Killed);
        Assert.NotEqual(ConnectionState.Failed, manager.State);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task MonitorIsStoppedWhenSshExitsAndRestartedOnReconnect()
    {
        var (manager, launcher, monitor, _, _) = Rig();
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => manager.State == ConnectionState.Connected);
        launcher.Last!.Exit(255);
        await TestWait.Until(() => launcher.Processes.Count == 2 && monitor.Calls.Count(c => c == "start") == 2);

        var firstStop = monitor.Calls.IndexOf("stop");
        var secondStart = monitor.Calls.LastIndexOf("start");
        Assert.True(firstStop >= 0 && firstStop < secondStart);
        Assert.Single(monitor.Calls, c => c == "reset"); // session totals survive reconnects
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task EphemeralBackendBindFailureIsRetriedNotPermanent()
    {
        var (manager, launcher, _, verifier, _) = Rig();
        verifier.Outcome = StartupOutcome.ProcessExited;
        verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "bind [127.0.0.1]:45454: Address already in use");
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => launcher.Processes.Count >= 3);
        Assert.NotEqual(ConnectionState.Failed, manager.State);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DefaultManagerWithoutAMonitorStillWorksAndReportsUnavailableTraffic()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(clock);
        var manager = new ConnectionManager(launcher, new ScriptedVerifier(), clock);
        await manager.ConnectAsync(Profile());
        await TestWait.Until(() => manager.State == ConnectionState.Connected);
        Assert.False(manager.TrafficMonitor.IsAvailable);
        var args = launcher.Specs.Single().Arguments.ToList();
        Assert.Equal("127.0.0.1:10080", args[args.IndexOf("-D") + 1]);
        await manager.DisposeAsync();
    }

    private sealed class CapturingVerifier : IStartupVerifier
    {
        private readonly Action<StartupContext> capture;

        public CapturingVerifier(Action<StartupContext> capture) => this.capture = capture;

        public Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
        {
            capture(context);
            return Task.FromResult(StartupOutcome.Ready);
        }
    }
}
