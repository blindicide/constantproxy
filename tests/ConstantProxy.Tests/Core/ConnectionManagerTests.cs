namespace ConstantProxy.Tests.Core;

public class ConnectionManagerTests
{
    private sealed class Rig
    {
        public Rig()
        {
            Clock = new FakeClock();
            Launcher = new FakeLauncher(Clock);
            Verifier = new ScriptedVerifier();
            Manager = new ConnectionManager(Launcher, Verifier, Clock, random: () => 0.5);
            Manager.EventRaised += e => { lock (Events) { Events.Add(e); } };
            Manager.StateChanged += c => { lock (States) { States.Add(c.New); } };
        }

        public FakeClock Clock { get; }

        public FakeLauncher Launcher { get; }

        public ScriptedVerifier Verifier { get; }

        public ConnectionManager Manager { get; }

        public List<ConnectionEvent> Events { get; } = new();

        public List<ConnectionState> States { get; } = new();

        public Profile Profile { get; set; } = new() { Host = "example-host", Port = 10080 };

        public Task WaitForState(ConnectionState state) =>
            TestWait.Until(() => Manager.State == state, what: $"state {state} (now {Manager.State})");

        public Task WaitForProcesses(int count) =>
            TestWait.Until(() => Launcher.Processes.Count >= count, what: $"{count} process(es)");

        public bool Saw(ConnectionEventType type) { lock (Events) { return Events.Any(e => e.Type == type); } }

        public int Count(ConnectionEventType type) { lock (Events) { return Events.Count(e => e.Type == type); } }
    }

    [Fact]
    public async Task ConnectLaunchesSshWithTheGeneratedArgumentsAndBecomesConnected()
    {
        var rig = new Rig();
        Assert.True(await rig.Manager.ConnectAsync(rig.Profile));
        await rig.WaitForState(ConnectionState.Connected);

        var spec = Assert.Single(rig.Launcher.Specs);
        Assert.Equal(SshArgumentBuilder.Build(rig.Profile), spec.Arguments);
        Assert.Equal(rig.Launcher.Last!.Pid, rig.Manager.CurrentProcess!.Pid);
        Assert.NotNull(rig.Manager.ConnectedSinceUtc);
        Assert.Equal(new[] { ConnectionState.Starting, ConnectionState.Connecting, ConnectionState.Connected }, rig.States);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ConnectPressedTwiceStartsOnlyOneProcess()
    {
        var rig = new Rig();
        var results = await Task.WhenAll(rig.Manager.ConnectAsync(rig.Profile), rig.Manager.ConnectAsync(rig.Profile));
        await rig.WaitForState(ConnectionState.Connected);
        Assert.Single(results, r => r);
        Assert.Single(rig.Launcher.Processes);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ProfileChangesAfterConnectDoNotAffectTheRunningConnection()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        rig.Profile.Port = 1;
        rig.Profile.Host = "other";
        await rig.WaitForState(ConnectionState.Connected);
        Assert.Equal(10080, rig.Manager.ActiveProfile!.Port);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedExitReconnectsAutomaticallyWithoutDelayFirst()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Exit(255);
        await rig.WaitForProcesses(2);
        await TestWait.Until(() => rig.Manager.State == ConnectionState.Connected && rig.Manager.ReconnectCount == 1);

        Assert.Contains(ConnectionState.Reconnecting, rig.States);
        Assert.Equal(TimeSpan.Zero, rig.Clock.RecordedDelays.First());
        Assert.True(rig.Saw(ConnectionEventType.SshExited));
        Assert.True(rig.Saw(ConnectionEventType.ReconnectSucceeded));
        Assert.Equal(255, await rig.Launcher.Processes[0].Exited);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedFailuresFollowTheBackoffSequence()
    {
        var rig = new Rig();
        rig.Verifier.Outcome = StartupOutcome.ProcessExited; // every attempt dies during startup
        await rig.Manager.ConnectAsync(rig.Profile);

        for (var i = 0; i < 7; i++)
        {
            await rig.WaitForProcesses(i + 1);
            rig.Launcher.Processes[i].Exit(1);
        }

        await rig.WaitForProcesses(8);
        var seconds = rig.Clock.RecordedDelays.Select(d => d.TotalSeconds).ToList();
        // Reconnect delays interleave with stop-grace waits only for live processes; here all were already dead.
        Assert.Equal(new[] { 0.0, 1, 2, 5, 10, 15, 15 }, seconds.Take(7));
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionHealthyForLongEnoughResetsTheBackoff()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Exit(1);                       // failure #1 -> delay 0
        await rig.WaitForProcesses(2);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.Advance(TimeSpan.FromSeconds(31));       // healthy for > 30 s
        rig.Launcher.Last!.Exit(1);                       // would be delay 1 s without the reset
        await rig.WaitForProcesses(3);

        Assert.Equal(new[] { 0.0, 0.0 }, rig.Clock.RecordedDelays.Take(2).Select(d => d.TotalSeconds));
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionThatDiesQuicklyDoesNotResetTheBackoff()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Exit(1);
        await rig.WaitForProcesses(2);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Launcher.Last!.Exit(1);
        await rig.WaitForProcesses(3);

        Assert.Equal(new[] { 0.0, 1.0 }, rig.Clock.RecordedDelays.Take(2).Select(d => d.TotalSeconds));
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectStopsSshAndNeverReconnects()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        var process = rig.Launcher.Last!;

        await rig.Manager.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
        Assert.Single(rig.Launcher.Processes);
        Assert.True(rig.Saw(ConnectionEventType.ManualDisconnect));
        Assert.Null(rig.Manager.CurrentProcess);
        Assert.Equal(new[] { ConnectionState.Stopping, ConnectionState.Disconnected }, rig.States.TakeLast(2));
        Assert.DoesNotContain(ConnectionState.Reconnecting, rig.States);
    }

    [Fact]
    public async Task PoliteStopIsPreferredOverKill()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        var process = rig.Launcher.Last!;
        process.HonorsStopRequest = true;

        await rig.Manager.DisconnectAsync();

        Assert.True(process.StopRequested);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task DisconnectDuringReconnectDelayReturnsImmediatelyWithoutRespawning()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.BlockDelays = true; // the retry delay will now wait "forever"
        rig.Launcher.Last!.Exit(1);
        rig.Launcher.Last!.Exit(1);
        await rig.WaitForState(ConnectionState.Reconnecting);

        var disconnect = rig.Manager.DisconnectAsync();
        Assert.Same(disconnect, await Task.WhenAny(disconnect, Task.Delay(3000)));

        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
        Assert.Single(rig.Launcher.Processes);
    }

    [Fact]
    public async Task DisconnectDuringConnectingCancelsStartupAndKillsTheProcess()
    {
        var rig = new Rig { };
        rig.Verifier.Block = true;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connecting);
        var process = rig.Launcher.Last!;

        await rig.Manager.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
        Assert.True(process.Killed);
        Assert.DoesNotContain(ConnectionState.Connected, rig.States);
    }

    [Fact]
    public async Task ManualReconnectRestartsSshWithoutConsumingBackoff()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        var first = rig.Launcher.Last!;

        Assert.True(await rig.Manager.ReconnectNowAsync());
        await rig.WaitForProcesses(2);
        await TestWait.Until(() => rig.Manager.State == ConnectionState.Connected && rig.Launcher.Processes.Count == 2);

        Assert.True(first.Killed);
        Assert.Equal(1, rig.Manager.ReconnectCount);

        // The first automatic failure afterwards must still be the "immediate" one.
        rig.Launcher.Last!.Exit(1);
        await rig.WaitForProcesses(3);
        Assert.Equal(new[] { 0.0, 0.0 }, rig.Clock.RecordedDelays.Where(d => d == TimeSpan.Zero).Take(2).Select(d => d.TotalSeconds));
        Assert.DoesNotContain(TimeSpan.FromSeconds(1), rig.Clock.RecordedDelays);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ManualReconnectDuringConnectingRestartsInsteadOfWaiting()
    {
        var rig = new Rig();
        rig.Verifier.Block = true;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connecting);

        Assert.True(await rig.Manager.ReconnectNowAsync());
        await rig.WaitForProcesses(2);

        Assert.True(rig.Launcher.Processes[0].Killed);
        await rig.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task ManualReconnectSkipsAPendingRetryDelay()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Clock.BlockDelays = true;
        rig.Launcher.Last!.Exit(1);
        await rig.WaitForState(ConnectionState.Reconnecting);
        rig.Clock.BlockDelays = false;

        Assert.True(await rig.Manager.ReconnectNowAsync());
        await rig.WaitForProcesses(2);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectNowWithoutAnActiveConnectionDoesNothing()
    {
        var rig = new Rig();
        Assert.False(await rig.Manager.ReconnectNowAsync());
        Assert.Empty(rig.Launcher.Processes);
    }

    [Fact]
    public async Task MissingSshIsAPermanentConfigurationFailureNotARetryLoop()
    {
        var rig = new Rig();
        rig.Launcher.AlwaysNotFound = true;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);

        Assert.Single(rig.Launcher.Specs);
        Assert.Equal("ssh.notfound", rig.Manager.LastFailure!.Code);
        Assert.Equal(FailureCategory.LocalConfiguration, rig.Manager.LastFailure.Category);
        Assert.False(rig.Manager.LastFailure.Retryable);
    }

    [Fact]
    public async Task InvalidProfileFailsBeforeAnyProcessIsStarted()
    {
        var rig = new Rig { Profile = new Profile { Host = "" } };
        Assert.False(await rig.Manager.ConnectAsync(rig.Profile));
        Assert.Equal(ConnectionState.Failed, rig.Manager.State);
        Assert.Empty(rig.Launcher.Specs);
        Assert.Equal("config.host.empty", rig.Manager.LastFailure!.Code);
    }

    [Fact]
    public async Task DisabledAutoReconnectEndsInFailedInsteadOfRespawning()
    {
        var rig = new Rig();
        rig.Profile.Reconnect.Enabled = false;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);

        rig.Launcher.Last!.Exit(1);
        await rig.WaitForState(ConnectionState.Failed);
        Assert.Single(rig.Launcher.Processes);
    }

    [Fact]
    public async Task CanConnectAgainAfterAFailure()
    {
        var rig = new Rig();
        rig.Launcher.FailNextWith = new SshLaunchException(LaunchFailureKind.InvalidExecutable, "bad");
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        Assert.Equal("ssh.invalid", rig.Manager.LastFailure!.Code);

        Assert.True(await rig.Manager.ConnectAsync(rig.Profile));
        await rig.WaitForState(ConnectionState.Connected);
        Assert.Null(rig.Manager.LastFailure);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectFromFailedStateReturnsToDisconnected()
    {
        var rig = new Rig();
        rig.Launcher.AlwaysNotFound = true;
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Failed);
        await rig.Manager.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
    }

    [Fact]
    public async Task DisconnectWhenAlreadyDisconnectedIsANoOp()
    {
        var rig = new Rig();
        await rig.Manager.DisconnectAsync();
        Assert.Empty(rig.States);
    }

    [Fact]
    public async Task SshExitingWhileDisconnectIsInProgressIsNotTreatedAsAFailure()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        var process = rig.Launcher.Last!;
        process.HonorsStopRequest = true; // exits "by itself" at the moment we ask

        await rig.Manager.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, rig.Manager.State);
        Assert.Null(rig.Manager.LastFailure);
        Assert.Single(rig.Launcher.Processes);
    }

    [Fact]
    public async Task SessionBookkeepingResetsOnNewConnect()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        rig.Launcher.Last!.Exit(1);
        await rig.WaitForProcesses(2);
        await rig.Manager.DisconnectAsync();
        Assert.Equal(1, rig.Manager.ReconnectCount);

        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        Assert.Equal(0, rig.Manager.ReconnectCount);
        Assert.NotNull(rig.Manager.SessionStartedUtc);
        await rig.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ProcessInfoRecordsExitDetails()
    {
        var rig = new Rig();
        await rig.Manager.ConnectAsync(rig.Profile);
        await rig.WaitForState(ConnectionState.Connected);
        var info = rig.Manager.CurrentProcess!;
        Assert.Equal(rig.Profile.Id, info.ProfileId);
        Assert.Equal(0, info.ReconnectAttempt);

        rig.Launcher.Last!.Exit(255);
        await rig.WaitForProcesses(2);
        await TestWait.Until(() => info.ExitCode == 255, what: "exit code recorded");
        Assert.NotNull(info.ExitTimeUtc);
        await rig.Manager.DisposeAsync();
    }
}
