namespace ConstantProxy.Tests.Core;

public class ConnectionMonitoringTests
{
    private static readonly SocksProbeResult Good = SocksProbeResult.Ok(TimeSpan.FromMilliseconds(25));
    private static readonly SocksProbeResult Bad = SocksProbeResult.Fail(ProbeFailure.Timeout, "timeout");

    private sealed class Rig
    {
        public Rig()
        {
            Clock = new FakeClock();
            Launcher = new FakeLauncher(Clock);
            Verifier = new ScriptedVerifier();
            Ports = new FakePortProbe();
            Socks = new FakeSocksProbe();
            Manager = new ConnectionManager(Launcher, Verifier, Clock, random: () => 0.5, portProbe: Ports, socksProbe: Socks);
            Manager.EventRaised += e => { lock (Events) { Events.Add(e); } };
            Manager.StateChanged += c => { lock (States) { States.Add(c.New); } };
        }

        public FakeClock Clock { get; }

        public FakeLauncher Launcher { get; }

        public ScriptedVerifier Verifier { get; }

        public FakePortProbe Ports { get; }

        public FakeSocksProbe Socks { get; }

        public ConnectionManager Manager { get; }

        public List<ConnectionEvent> Events { get; } = new();

        public List<ConnectionState> States { get; } = new();

        public Profile Profile { get; } = new() { Host = "example-host", Port = 10080 };

        public Task WaitForState(ConnectionState s) => TestWait.Until(() => Manager.State == s, what: $"state {s} (now {Manager.State})");

        public Task WaitForProcesses(int n) => TestWait.Until(() => Launcher.Processes.Count >= n, what: $"{n} process(es)");

        public Task WaitForProbes(int n) => TestWait.Until(() => Socks.Calls >= n, what: $"{n} probe(s) (now {Socks.Calls})");

        public bool Saw(ConnectionEventType t) { lock (Events) { return Events.Any(e => e.Type == t); } }
    }

    // ---- port conflict detection (SPEC §12) ----

    [Fact]
    public async Task PortInUseOnFirstAttemptFailsWithoutStartingSshOrRetrying()
    {
        var rig = new Rig();
        rig.Ports.Status = PortStatus.InUse;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);

        Assert.Empty(rig.Launcher.Specs);
        Assert.Equal("port.inuse", rig.Manager.LastFailure!.Code);
        Assert.Equal("Port 10080 is already in use.", rig.Manager.LastFailure.Message);
        Assert.Equal(1, rig.Ports.Calls);
    }

    [Fact]
    public async Task PortAccessDeniedIsPermanent()
    {
        var rig = new Rig();
        rig.Ports.Status = PortStatus.AccessDenied;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        Assert.Equal("port.denied", rig.Manager.LastFailure!.Code);
    }

    [Fact]
    public async Task PortStillHeldDuringAReconnectIsRetriedWithBackoffInsteadOfGivingUp()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Ports.Script.Enqueue(PortStatus.InUse); // our previous ssh is still releasing the port
        rig.Launcher.Last!.Exit(255);
        await rig.WaitForProcesses(2);
        await rig.WaitForState(ConnectionState.Connected);

        Assert.Equal(2, rig.Launcher.Processes.Count);
        Assert.Equal(3, rig.Ports.Calls); // first start, the blocked reconnect attempt, the retry
        var scheduled = rig.Events.Where(e => e.Type == ConnectionEventType.ReconnectScheduled).Select(e => e.Detail).ToArray();
        Assert.Equal(new[] { "0s", "1s" }, scheduled); // backoff advanced after the blocked attempt
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task UnavailablePortStatusDoesNotBlockStart()
    {
        var rig = new Rig();
        rig.Ports.Status = PortStatus.Unavailable;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await rig.Manager.DisposeAsync();
    }

    // ---- health checks (SPEC §14) ----

    [Fact]
    public async Task HealthChecksProbeThroughTheConfiguredEndpointAndTarget()
    {
        var rig = new Rig();
        rig.Profile.BindAddress = "0.0.0.0";
        rig.Profile.Monitoring.TargetHost = "check.example";
        rig.Profile.Monitoring.TargetPort = 8443;
        rig.Profile.Monitoring.TimeoutSeconds = 4;
        rig.Socks.Enqueue(Good);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(2); // the second call blocks: the first result was consumed

        var request = rig.Socks.Requests[0];
        Assert.Equal(("127.0.0.1", 10080, "check.example", 8443), (request.Address, request.Port, request.Host, request.HostPort));
        Assert.Equal(TimeSpan.FromSeconds(4), request.Timeout);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task LatencyAndLastResultAreExposed()
    {
        var rig = new Rig();
        rig.Socks.Enqueue(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(10)), SocksProbeResult.Ok(TimeSpan.FromMilliseconds(30)));
        var seen = new List<HealthCheckResult>();
        rig.Manager.HealthChecked += seen.Add;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(3);

        Assert.Equal(TimeSpan.FromMilliseconds(30), rig.Manager.LastHealth!.Latency);
        Assert.Equal(TimeSpan.FromMilliseconds(20), rig.Manager.AverageLatency);
        Assert.Equal(2, seen.Count);
        Assert.Equal(ConnectionState.Connected, rig.Manager.State);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ThresholdFailuresDegradeAndASuccessRecovers()
    {
        var rig = new Rig();
        rig.Socks.Enqueue(Bad, Bad, Bad, Good);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(5);

        Assert.Equal(new[] { ConnectionState.Starting, ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Degraded, ConnectionState.Connected }, rig.States);
        Assert.True(rig.Saw(ConnectionEventType.Degraded));
        Assert.True(rig.Saw(ConnectionEventType.Recovered));
        Assert.Equal(3, rig.Manager.HealthFailureCount);
        Assert.Single(rig.Launcher.Processes); // degradation alone never restarts ssh by default
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task SingleFailureDoesNotChangeState()
    {
        var rig = new Rig();
        rig.Socks.Enqueue(Bad, Good, Bad, Good);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(5);

        Assert.DoesNotContain(ConnectionState.Degraded, rig.States);
        Assert.Equal(2, rig.Manager.HealthFailureCount);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task PersistentFailureReconnectsWhenConfigured()
    {
        var rig = new Rig();
        rig.Profile.Monitoring.ReconnectOnFailure = true;
        rig.Profile.Monitoring.FailureThreshold = 2;
        rig.Profile.Monitoring.ReconnectAfterFailures = 4;
        rig.Socks.Enqueue(Bad, Bad, Bad, Bad);
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProcesses(2);

        Assert.True(rig.Launcher.Processes[0].Killed);
        Assert.Contains(ConnectionState.Degraded, rig.States);
        Assert.Contains(ConnectionState.Reconnecting, rig.States);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task DisabledHealthCheckingNeverProbes()
    {
        var rig = new Rig();
        rig.Profile.Monitoring.Enabled = false;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        await Task.Delay(50);
        Assert.Equal(0, rig.Socks.Calls);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectWhileAProbeIsInFlightStopsCleanly()
    {
        var rig = new Rig(); // no queued results: the first probe waits forever
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(1);

        var disconnect = rig.Manager.DisconnectAsync();
        Assert.Same(disconnect, await Task.WhenAny(disconnect, Task.Delay(3000)));
        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
    }

    [Fact]
    public async Task SshExitWhileAProbeIsInFlightReconnects()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(1);
        rig.Launcher.Last!.Exit(255);
        await rig.WaitForProcesses(2);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ManualReconnectWhileAProbeIsInFlightRestartsSsh()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProbes(1);
        Assert.True(await rig.Manager.ReconnectNowAsync());
        await rig.WaitForProcesses(2);
        await rig.Manager.DisposeAsync();
    }

    // ---- failure classification (SPEC §35, §77) ----

    [Fact]
    public async Task AuthenticationFailureAfterStartIsPermanent()
    {
        var rig = new Rig();
        rig.Profile.Monitoring.Enabled = false;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Emit(OutputStream.StandardError, "user@host: Permission denied (publickey).");
        rig.Launcher.Last.Exit(255);
        await rig.WaitForState(ConnectionState.Failed);

        Assert.Equal(FailureCategory.Authentication, rig.Manager.LastFailure!.Category);
        Assert.Single(rig.Launcher.Processes);
    }

    [Fact]
    public async Task AuthenticationFailureDuringStartupIsPermanent()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "Permission denied (publickey).");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);

        Assert.Equal("ssh.auth", rig.Manager.LastFailure!.Code);
        Assert.Contains("Permission denied", rig.Manager.LastFailure.Details);
        Assert.Single(rig.Launcher.Processes);
    }

    [Fact]
    public async Task HostKeyMismatchIsPermanent()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "Host key verification failed.");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        Assert.Equal(FailureCategory.HostVerification, rig.Manager.LastFailure!.Category);
    }

    [Fact]
    public async Task NetworkFailureDuringStartupIsRetriedWithBackoff()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "ssh: connect to host h port 22: Network is unreachable");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProcesses(3);
        Assert.NotEqual(ConnectionState.Failed, rig.Manager.State);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task StartupTimeoutKillsSshAndRetries()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.TimedOut;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForProcesses(2);

        Assert.True(rig.Launcher.Processes[0].Killed);
        Assert.True(rig.Saw(ConnectionEventType.StartupFailed));
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task BindFailureReportedBySshIsPermanent()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited;
        rig.Verifier.BeforeOutcome = p => p.Emit(OutputStream.StandardError, "bind [127.0.0.1]:10080: Address already in use\nCould not request local forwarding.");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        Assert.Equal(FailureCategory.Forwarding, rig.Manager.LastFailure!.Category);
    }

    [Fact]
    public async Task StartupTimeoutValueComesFromTheProfile()
    {
        var rig = new Rig();
        rig.Profile.StartupTimeoutSeconds = 42;
        TimeSpan? seen = null;
        var verifier = new CapturingVerifier(t => seen = t);
        var manager = new ConnectionManager(rig.Launcher, verifier, rig.Clock);
        await manager.ConnectAsync(rig.Profile);
        await TestWait.Until(() => seen is not null);
        Assert.Equal(TimeSpan.FromSeconds(42), seen);
        await manager.DisposeAsync();
    }

    private sealed class CapturingVerifier : IStartupVerifier
    {
        private readonly Action<TimeSpan> capture;

        public CapturingVerifier(Action<TimeSpan> capture) => this.capture = capture;

        public Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
        {
            capture(context.Timeout);
            return Task.FromResult(StartupOutcome.Ready);
        }
    }
}
