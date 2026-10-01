using System.Net;

namespace ConstantProxy.Core.Connection;

/// <summary>
/// The single authoritative controller of the tunnel lifecycle (SPEC §8–§11, §37, §63, §64).
/// Public commands are serialized by one gate; one supervision task per connection owns the ssh process;
/// the <see cref="ConnectionStateMachine"/> is the only place state lives.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private const string Source = "connection";

    private readonly ISshProcessLauncher launcher;
    private readonly IStartupVerifier verifier;
    private readonly IClock clock;
    private readonly IAppLog log;
    private readonly Func<double> random;
    private readonly IPortProbe? portProbe;
    private readonly ISocksProbe? socksProbe;
    private readonly ITrafficMonitor traffic;
    private readonly ConnectionStateMachine machine;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly object sync = new();

    private Run? run;
    private ProcessInfo? currentProcess;
    private FailureInfo? lastFailure;
    private DateTimeOffset? sessionStartedUtc;
    private DateTimeOffset? connectedSinceUtc;
    private int reconnectCount;
    private HealthCheckResult? lastHealth;
    private TimeSpan? averageLatency;
    private int healthFailureCount;

    public ConnectionManager(
        ISshProcessLauncher launcher,
        IStartupVerifier verifier,
        IClock? clock = null,
        IAppLog? log = null,
        Func<double>? random = null,
        IPortProbe? portProbe = null,
        ISocksProbe? socksProbe = null,
        ITrafficMonitor? trafficMonitor = null)
    {
        traffic = trafficMonitor ?? new NullTrafficMonitor();
        this.portProbe = portProbe;
        this.socksProbe = socksProbe;
        this.launcher = launcher;
        this.verifier = verifier;
        this.clock = clock ?? SystemClock.Instance;
        this.log = log ?? NullAppLog.Instance;
        this.random = random ?? Random.Shared.NextDouble;
        machine = new ConnectionStateMachine(this.clock);
        machine.Changed += change =>
        {
            this.log.Info(Source, $"State {change.Old} -> {change.New}" + (change.Failure is null ? string.Empty : $" ({change.Failure.Code})"));
            StateChanged?.Invoke(change);
        };
    }

    public event Action<StateChange>? StateChanged;

    public event Action<ConnectionEvent>? EventRaised;

    public ITrafficMonitor TrafficMonitor => traffic;

    /// <summary>Raised after every health probe (success or failure).</summary>
    public event Action<HealthCheckResult>? HealthChecked;

    /// <summary>How long ssh gets to exit after a polite stop request before it is killed.</summary>
    public TimeSpan StopGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    public ConnectionState State => machine.Current;

    public FailureInfo? LastFailure => Volatile.Read(ref lastFailure);

    public ProcessInfo? CurrentProcess => Volatile.Read(ref currentProcess);

    public DateTimeOffset? SessionStartedUtc
    {
        get { lock (sync) { return sessionStartedUtc; } }
    }

    public DateTimeOffset? ConnectedSinceUtc
    {
        get { lock (sync) { return connectedSinceUtc; } }
    }

    public int ReconnectCount
    {
        get { lock (sync) { return reconnectCount; } }
    }

    /// <summary>The most recent health probe of the current session, if any.</summary>
    public HealthCheckResult? LastHealth
    {
        get { lock (sync) { return lastHealth; } }
    }

    public TimeSpan? AverageLatency
    {
        get { lock (sync) { return averageLatency; } }
    }

    /// <summary>Failed health probes in the current session.</summary>
    public int HealthFailureCount
    {
        get { lock (sync) { return healthFailureCount; } }
    }

    public Profile? ActiveProfile
    {
        get { lock (sync) { return run?.Profile; } }
    }

    /// <summary>Starts supervising a tunnel for <paramref name="profile"/>. Returns false if one is already active.</summary>
    public async Task<bool> ConnectAsync(Profile profile)
    {
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (machine.Current is not (ConnectionState.Disconnected or ConnectionState.Failed))
            {
                log.Debug(Source, "Connect ignored: a connection is already active.");
                return false;
            }

            var snapshot = profile.Clone();
            Raise(ConnectionEventType.ConnectionRequested, snapshot.Name);

            lock (sync)
            {
                sessionStartedUtc = clock.UtcNow;
                connectedSinceUtc = null;
                reconnectCount = 0;
                lastHealth = null;
                averageLatency = null;
                healthFailureCount = 0;
            }

            Volatile.Write(ref lastFailure, null);
            traffic.ResetSession();
            machine.Transition(ConnectionState.Starting);

            var validation = ProfileValidator.Validate(snapshot);
            if (!validation.IsValid)
            {
                var first = validation.Errors.First();
                Fail(new FailureInfo(FailureCategory.LocalConfiguration, "config." + first.Code, first.Message, Retryable: false));
                return false;
            }

            var newRun = new Run(snapshot);
            lock (sync)
            {
                run?.Cts.Dispose(); // a previous run that ended in Failed
                run = newRun;
            }

            newRun.Task = Task.Run(() => SuperviseAsync(newRun));
            return true;
        }
        finally
        {
            commandGate.Release();
        }
    }

    /// <summary>Stops the tunnel intentionally. Never treated as a failure and never triggers a reconnect.</summary>
    public async Task DisconnectAsync()
    {
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                var state = machine.Current;
                if (state == ConnectionState.Disconnected)
                {
                    return;
                }

                if (state == ConnectionState.Failed)
                {
                    if (machine.TryTransition(ConnectionState.Disconnected))
                    {
                        ClearSession();
                        return;
                    }

                    continue;
                }

                if (!machine.TryTransition(ConnectionState.Stopping))
                {
                    continue; // state changed underneath us; re-evaluate
                }

                break;
            }

            Raise(ConnectionEventType.ManualDisconnect);
            Run? active;
            lock (sync)
            {
                active = run;
            }

            if (active is not null)
            {
                await active.Cts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await active.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected: the supervisor observed our cancellation
                }
            }

            ClearSession();
            machine.Transition(ConnectionState.Disconnected);
        }
        finally
        {
            commandGate.Release();
        }
    }

    /// <summary>
    /// "Reconnect now" (SPEC §11): restarts ssh without increasing the failure backoff counter, and skips any
    /// pending retry delay. Returns false when there is nothing to reconnect.
    /// </summary>
    public async Task<bool> ReconnectNowAsync()
    {
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Run? active;
            lock (sync)
            {
                active = run;
            }

            if (active is null || machine.Current is not (ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting))
            {
                return false;
            }

            log.Info(Source, "Manual reconnect requested.");
            active.SignalReconnect();
            return true;
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task SuperviseAsync(Run r)
    {
        var ct = r.Cts.Token;
        var policy = new ReconnectPolicy(r.Profile.Reconnect, random);
        var attempt = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await RunAttemptAsync(r, policy, attempt, ct).ConfigureAwait(false);
                if (result.Kind == AttemptKind.Failed)
                {
                    Fail(result.Failure!);
                    return;
                }

                if (!policy.Enabled && !result.Manual)
                {
                    Fail(result.Failure ?? new FailureInfo(FailureCategory.Unknown, "ssh.exited", "The SSH process ended and automatic reconnect is disabled.", Retryable: true));
                    return;
                }

                var delay = result.Manual ? TimeSpan.Zero : policy.NextDelay();
                if (!machine.TryTransition(ConnectionState.Reconnecting, result.Failure))
                {
                    return; // Stopping or Failed took over
                }

                Raise(ConnectionEventType.ReconnectScheduled, $"{delay.TotalSeconds:0.#}s" + (result.Manual ? " (manual)" : string.Empty));
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var delayTask = clock.Delay(delay, delayCts.Token);
                    var skipped = await Task.WhenAny(delayTask, r.Signal).ConfigureAwait(false);
                    if (skipped == r.Signal)
                    {
                        r.ResetSignal();
                        await delayCts.CancelAsync().ConfigureAwait(false);
                    }

                    await ObserveAsync(delayTask).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                attempt++;
                lock (sync)
                {
                    reconnectCount++;
                }

                Raise(ConnectionEventType.ReconnectAttempt, $"attempt {attempt}");
                if (!machine.TryTransition(ConnectionState.Starting))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // intentional stop; DisconnectAsync completes the transition to Disconnected
        }
        catch (Exception ex)
        {
            log.Error(Source, "Unexpected supervisor error.", ex);
            Fail(new FailureInfo(FailureCategory.Unknown, "internal", "An unexpected internal error occurred.", Retryable: false, Details: ex.Message));
        }
    }

    private async Task<AttemptResult> RunAttemptAsync(Run r, ReconnectPolicy policy, int attempt, CancellationToken ct)
    {
        var profile = r.Profile;
        var backend = profile.TrafficMode == TrafficMode.Bridge ? traffic.PrepareBackend(profile) : null;
        var spec = new SshLaunchSpec(profile.SshExecutable, SshArgumentBuilder.Build(profile, backend));

        var portFailure = CheckLocalPort(profile, attempt);
        if (portFailure is not null)
        {
            Raise(ConnectionEventType.StartupFailed, portFailure.Code);
            return portFailure.Retryable ? AttemptResult.Retry(portFailure, manual: false) : AttemptResult.Failed(portFailure);
        }

        ISshProcess process;
        try
        {
            process = launcher.Start(spec);
        }
        catch (SshLaunchException ex)
        {
            log.Error(Source, "Could not start ssh: " + ex.Message, ex);
            Raise(ConnectionEventType.StartupFailed, ex.Kind.ToString());
            return AttemptResult.Failed(MapLaunchFailure(ex));
        }

        var info = new ProcessInfo { Pid = process.Pid, StartTimeUtc = process.StartTimeUtc, ProfileId = profile.Id, ReconnectAttempt = attempt };
        Volatile.Write(ref currentProcess, info);
        var tail = new OutputTail();
        void OnOutput(SshOutputLine line)
        {
            tail.Add(line);
            log.Debug("ssh", $"[{(line.Stream == OutputStream.StandardError ? "stderr" : "stdout")}] {line.Text}");
        }

        process.OutputReceived += OnOutput;
        log.Info(Source, $"ssh started, PID {process.Pid}");
        Raise(ConnectionEventType.SshStarted, $"pid {process.Pid}");

        try
        {
            machine.TryTransition(ConnectionState.Connecting);

            var startup = await WaitForStartupAsync(r, profile, process, backend, ct).ConfigureAwait(false);
            if (startup == StartupWaitResult.ManualReconnect)
            {
                return AttemptResult.Retry(failure: null, manual: true);
            }

            if (startup == StartupWaitResult.Ready)
            {
                try
                {
                    traffic.Start(profile, backend);
                }
                catch (TrafficMonitorException ex)
                {
                    log.Error(Source, "Traffic monitor could not start: " + ex.Message, ex);
                    Raise(ConnectionEventType.StartupFailed, "traffic.start");
                    return AttemptResult.Retry(new FailureInfo(FailureCategory.Forwarding, "traffic.start", "The local SOCKS port could not be opened.", Retryable: true, Details: ex.Message), manual: false);
                }

                return await MonitorAsync(r, policy, process, info, attempt, tail, backend, ct).ConfigureAwait(false);
            }

            // Process exited or startup timed out before the tunnel became usable.
            await RecordExitAsync(process, info, wait: startup == StartupWaitResult.Exited).ConfigureAwait(false);
            var failure = AdjustForBackend(FailureClassifier.Classify(new FailureContext(FailureStage.Startup, info.ExitCode, tail.AsText(), ListenerWasReady: false, profile.Port)), backend);
            Raise(ConnectionEventType.StartupFailed, failure.Code);
            return failure.Retryable ? AttemptResult.Retry(failure, manual: false) : AttemptResult.Failed(failure);
        }
        finally
        {
            process.OutputReceived -= OnOutput;
            traffic.Stop();
            await StopProcessAsync(process).ConfigureAwait(false);
            if (info.ExitTimeUtc is null)
            {
                await RecordExitAsync(process, info, wait: false).ConfigureAwait(false);
            }

            lock (sync)
            {
                connectedSinceUtc = null;
            }

            process.Dispose();
        }
    }

    private async Task<StartupWaitResult> WaitForStartupAsync(Run r, Profile profile, ISshProcess process, SshListenOverride? backend, CancellationToken ct)
    {
        using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var context = new StartupContext(profile, process, StartupTimeout(profile), backend?.Port, backend?.Address);
        var verifyTask = verifier.WaitUntilReadyAsync(context, verifyCts.Token);
        var first = await Task.WhenAny(verifyTask, r.Signal).ConfigureAwait(false);
        if (first == r.Signal)
        {
            r.ResetSignal();
            await verifyCts.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(verifyTask).ConfigureAwait(false);
            return StartupWaitResult.ManualReconnect;
        }

        var outcome = await verifyTask.ConfigureAwait(false);
        return outcome switch
        {
            StartupOutcome.Ready => StartupWaitResult.Ready,
            StartupOutcome.ProcessExited => StartupWaitResult.Exited,
            _ => StartupWaitResult.TimedOut,
        };
    }

    private async Task<AttemptResult> MonitorAsync(Run r, ReconnectPolicy policy, ISshProcess process, ProcessInfo info, int attempt, OutputTail tail, SshListenOverride? backend, CancellationToken ct)
    {
        var profile = r.Profile;
        var connectedAt = clock.UtcNow;
        if (!machine.TryTransition(ConnectionState.Connected))
        {
            ct.ThrowIfCancellationRequested();
            return AttemptResult.Retry(null, manual: false);
        }

        lock (sync)
        {
            connectedSinceUtc = connectedAt;
        }

        Raise(ConnectionEventType.SocksReady);
        Raise(ConnectionEventType.Connected);
        if (attempt > 0)
        {
            Raise(ConnectionEventType.ReconnectSucceeded, $"after {attempt} attempt(s)");
        }

        var health = profile.Monitoring;
        var evaluator = socksProbe is not null && health.Enabled ? new HealthCheckEvaluator(health) : null;
        var probeAddress = ProbeAddress(profile);
        var neverCompletes = new TaskCompletionSource().Task;

        var cancelled = new TaskCompletionSource();
        using var registration = ct.Register(() => cancelled.TrySetResult());

        while (true)
        {
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tick = evaluator is null ? neverCompletes : clock.Delay(TimeSpan.FromSeconds(health.IntervalSeconds), stepCts.Token);
            var finished = await Task.WhenAny(process.Exited, r.Signal, cancelled.Task, tick).ConfigureAwait(false);

            if (finished == tick && evaluator is not null)
            {
                var probe = socksProbe!.ProbeAsync(probeAddress, profile.Port, health.TargetHost, health.TargetPort, TimeSpan.FromSeconds(health.TimeoutSeconds), stepCts.Token);
                finished = await Task.WhenAny(process.Exited, r.Signal, cancelled.Task, probe).ConfigureAwait(false);
                if (finished == probe)
                {
                    var transition = RecordHealth(evaluator, await probe.ConfigureAwait(false));
                    if (transition == HealthTransition.Reconnect)
                    {
                        policy.RegisterConnectionEnded(clock.UtcNow - connectedAt);
                        return AttemptResult.Retry(FailureClassifier.Classify(new FailureContext(FailureStage.HealthCheck, null, string.Empty, ListenerWasReady: true, profile.Port)), manual: false);
                    }

                    continue;
                }

                await stepCts.CancelAsync().ConfigureAwait(false);
                await ObserveAsync(probe).ConfigureAwait(false);
            }
            else
            {
                await stepCts.CancelAsync().ConfigureAwait(false);
                if (evaluator is not null)
                {
                    await ObserveAsync(tick).ConfigureAwait(false);
                }
            }

            if (finished == r.Signal)
            {
                r.ResetSignal();
                return AttemptResult.Retry(null, manual: true);
            }

            ct.ThrowIfCancellationRequested();
            await RecordExitAsync(process, info, wait: true).ConfigureAwait(false);
            policy.RegisterConnectionEnded(clock.UtcNow - connectedAt);
            var failure = AdjustForBackend(FailureClassifier.Classify(new FailureContext(FailureStage.Running, info.ExitCode, tail.AsText(), ListenerWasReady: true, profile.Port)), backend);
            return failure.Retryable ? AttemptResult.Retry(failure, manual: false) : AttemptResult.Failed(failure);
        }
    }

    /// <summary>
    /// With a bridge, ssh listens on a freshly picked ephemeral port; a bind failure there is a rare race, not a
    /// persistent configuration error, so it must be retried rather than ending the session.
    /// </summary>
    private static FailureInfo AdjustForBackend(FailureInfo failure, SshListenOverride? backend) =>
        backend is not null && failure.Code == "forward.bind" ? failure with { Retryable = true } : failure;

    private HealthTransition RecordHealth(HealthCheckEvaluator evaluator, SocksProbeResult result)
    {
        var check = new HealthCheckResult(clock.UtcNow, result.Success, result.Latency, result.Detail ?? (result.Success ? null : result.Failure.ToString()));
        var transition = evaluator.Record(result);
        lock (sync)
        {
            lastHealth = check;
            averageLatency = evaluator.AverageLatency;
            if (!result.Success)
            {
                healthFailureCount++;
            }
        }

        if (result.Success)
        {
            log.Debug(Source, $"Health check ok ({result.Latency?.TotalMilliseconds:0} ms)");
        }
        else
        {
            log.Warn(Source, $"Health check failed ({check.Error}); {evaluator.ConsecutiveFailures} consecutive");
            Raise(ConnectionEventType.HealthCheckFailed, check.Error);
        }

        HealthChecked?.Invoke(check);

        switch (transition)
        {
            case HealthTransition.Degrade when machine.TryTransition(ConnectionState.Degraded):
                Raise(ConnectionEventType.Degraded, $"{evaluator.ConsecutiveFailures} consecutive health-check failures");
                break;
            case HealthTransition.Recover when machine.TryTransition(ConnectionState.Connected):
                Raise(ConnectionEventType.Recovered);
                break;
        }

        return transition;
    }

    private FailureInfo? CheckLocalPort(Profile profile, int attempt)
    {
        if (portProbe is null)
        {
            return null;
        }

        var status = portProbe.Check(IPAddress.Parse(profile.BindAddress.Trim().Trim('[', ']')), profile.Port);
        switch (status)
        {
            case PortStatus.InUse:
                // On the very first attempt this is a local configuration problem; on a reconnect our own previous ssh may
                // still be releasing the port, so back off and try again instead of giving up.
                return FailureClassifier.PortInUse(profile.Port, retryable: attempt > 0);
            case PortStatus.AccessDenied:
                return FailureClassifier.PortAccessDenied(profile.Port);
            default:
                return null;
        }
    }

    private static string ProbeAddress(Profile profile)
    {
        var address = IPAddress.Parse(profile.BindAddress.Trim().Trim('[', ']'));
        if (address.Equals(IPAddress.Any))
        {
            return IPAddress.Loopback.ToString();
        }

        return address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback.ToString() : address.ToString();
    }

    private async Task RecordExitAsync(ISshProcess process, ProcessInfo info, bool wait)
    {
        int? code = null;
        if (wait || process.HasExited)
        {
            code = await process.Exited.ConfigureAwait(false);
        }

        info.ExitTimeUtc = clock.UtcNow;
        info.ExitCode = code;
        log.Info(Source, $"ssh exited, PID {info.Pid}, code {(code?.ToString() ?? "n/a")}");
        Raise(ConnectionEventType.SshExited, $"pid {info.Pid}, code {(code?.ToString() ?? "n/a")}");
    }

    private async Task StopProcessAsync(ISshProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        if (process.RequestStop())
        {
            var done = await Task.WhenAny(process.Exited, clock.Delay(StopGracePeriod, CancellationToken.None)).ConfigureAwait(false);
            if (done == process.Exited)
            {
                return;
            }
        }

        log.Info(Source, $"Force-terminating ssh PID {process.Pid}.");
        process.Kill();
        await Task.WhenAny(process.Exited, clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
    }

    private static TimeSpan StartupTimeout(Profile profile) => TimeSpan.FromSeconds(Math.Max(profile.StartupTimeoutSeconds, 1));

    private static FailureInfo MapLaunchFailure(SshLaunchException ex) => ex.Kind switch
    {
        LaunchFailureKind.NotFound => new FailureInfo(FailureCategory.LocalConfiguration, "ssh.notfound", "OpenSSH could not be found. Check the configured ssh.exe path.", Retryable: false, Details: ex.Message),
        LaunchFailureKind.InvalidExecutable => new FailureInfo(FailureCategory.LocalConfiguration, "ssh.invalid", "The configured SSH executable is not a valid program.", Retryable: false, Details: ex.Message),
        LaunchFailureKind.AccessDenied => new FailureInfo(FailureCategory.LocalConfiguration, "ssh.access", "Access to the SSH executable was denied.", Retryable: false, Details: ex.Message),
        _ => new FailureInfo(FailureCategory.Unknown, "ssh.launch", "The SSH process could not be started.", Retryable: false, Details: ex.Message),
    };

    private void Fail(FailureInfo failure)
    {
        Volatile.Write(ref lastFailure, failure);
        if (machine.TryTransition(ConnectionState.Failed, failure))
        {
            log.Error(Source, $"Connection failed: {failure.Code} - {failure.Message}");
            Raise(ConnectionEventType.ConnectionFailed, failure.Code);
        }
    }

    private void ClearSession()
    {
        lock (sync)
        {
            run?.Cts.Dispose();
            run = null;
            connectedSinceUtc = null;
        }

        Volatile.Write(ref currentProcess, null);
    }

    private void Raise(ConnectionEventType type, string? detail = null)
    {
        var evt = new ConnectionEvent(clock.UtcNow, type, detail);
        log.Debug(Source, $"Event {type}" + (detail is null ? string.Empty : $": {detail}"));
        EventRaised?.Invoke(evt);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancellation is the expected way these tasks end
        }
    }

    private enum StartupWaitResult
    {
        Ready,
        Exited,
        TimedOut,
        ManualReconnect,
    }

    private enum AttemptKind
    {
        Retry,
        Failed,
    }

    private sealed record AttemptResult(AttemptKind Kind, FailureInfo? Failure, bool Manual)
    {
        public static AttemptResult Retry(FailureInfo? failure, bool manual) => new(AttemptKind.Retry, failure, manual);

        public static AttemptResult Failed(FailureInfo failure) => new(AttemptKind.Failed, failure, false);
    }

    private sealed class Run
    {
        private TaskCompletionSource signal = NewSignal();

        public Run(Profile profile)
        {
            Profile = profile;
        }

        public Profile Profile { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public Task Signal => Volatile.Read(ref signal).Task;

        public void SignalReconnect() => Volatile.Read(ref signal).TrySetResult();

        public void ResetSignal() => Volatile.Write(ref signal, NewSignal());

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
