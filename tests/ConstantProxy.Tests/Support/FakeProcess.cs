namespace ConstantProxy.Tests.Support;

public sealed class FakeProcess : ISshProcess
{
    private static int nextPid = 1000;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeProcess(DateTimeOffset start)
    {
        Pid = Interlocked.Increment(ref nextPid);
        StartTimeUtc = start;
    }

    public int Pid { get; }

    public DateTimeOffset StartTimeUtc { get; }

    public bool HasExited => exited.Task.IsCompleted;

    public Task<int> Exited => exited.Task;

    /// <summary>When true, <see cref="RequestStop"/> performs a polite exit; otherwise it reports "no polite way".</summary>
    public bool HonorsStopRequest { get; set; }

    public bool StopRequested { get; private set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<SshOutputLine>? OutputReceived;

    public void Emit(OutputStream stream, string text) => OutputReceived?.Invoke(new SshOutputLine(stream, text));

    public void Exit(int code) => exited.TrySetResult(code);

    public bool RequestStop()
    {
        StopRequested = true;
        if (HonorsStopRequest)
        {
            Exit(0);
            return true;
        }

        return false;
    }

    public void Kill()
    {
        Killed = true;
        Exit(137);
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeLauncher : ISshProcessLauncher
{
    private readonly FakeClock clock;
    private readonly object gate = new();

    public FakeLauncher(FakeClock clock)
    {
        this.clock = clock;
    }

    public List<SshLaunchSpec> Specs { get; } = new();

    public List<FakeProcess> Processes { get; } = new();

    /// <summary>When set, the next Start throws this exception once.</summary>
    public SshLaunchException? FailNextWith { get; set; }

    /// <summary>When true, every Start throws NotFound.</summary>
    public bool AlwaysNotFound { get; set; }

    public FakeProcess? Last
    {
        get { lock (gate) { return Processes.LastOrDefault(); } }
    }

    public ISshProcess Start(SshLaunchSpec spec)
    {
        lock (gate)
        {
            Specs.Add(spec);
            if (AlwaysNotFound)
            {
                throw new SshLaunchException(LaunchFailureKind.NotFound, "not found");
            }

            if (FailNextWith is { } failure)
            {
                FailNextWith = null;
                throw failure;
            }

            var process = new FakeProcess(clock.UtcNow);
            Processes.Add(process);
            return process;
        }
    }
}

/// <summary>Verifier whose outcome the test controls; by default it reports Ready immediately.</summary>
public sealed class ScriptedVerifier : IStartupVerifier
{
    public bool Block { get; set; }

    public StartupOutcome Outcome { get; set; } = StartupOutcome.Ready;

    /// <summary>Lets a test make the process print something (for example an ssh error) before the outcome is reported.</summary>
    public Action<FakeProcess>? BeforeOutcome { get; set; }

    public Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
    {
        if (!Block)
        {
            if (context.Process is FakeProcess scripted)
            {
                BeforeOutcome?.Invoke(scripted);
            }

            if (Outcome == StartupOutcome.ProcessExited && context.Process is FakeProcess fake)
            {
                fake.Exit(1); // keep the fake consistent with what the verifier reports
            }

            return Task.FromResult(Outcome);
        }

        var tcs = new TaskCompletionSource<StartupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}

public static class TestWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + (what ?? "condition"));
            }

            await Task.Delay(5);
        }
    }
}

public sealed class FakePortProbe : IPortProbe
{
    public PortStatus Status { get; set; } = PortStatus.Free;

    /// <summary>Optional per-call script; when exhausted, <see cref="Status"/> is used.</summary>
    public Queue<PortStatus> Script { get; } = new();

    public int Calls { get; private set; }

    public PortStatus Check(System.Net.IPAddress address, int port)
    {
        Calls++;
        return Script.Count > 0 ? Script.Dequeue() : Status;
    }
}

/// <summary>Probe returning queued results; once the queue is empty it waits (until cancelled) so tests stay bounded.</summary>
public sealed class FakeSocksProbe : ISocksProbe
{
    private readonly object gate = new();
    private readonly Queue<SocksProbeResult> results = new();
    private int calls;

    public int Calls { get { lock (gate) { return calls; } } }

    public List<(string Address, int Port, string Host, int HostPort, TimeSpan Timeout)> Requests { get; } = new();

    public void Enqueue(params SocksProbeResult[] items)
    {
        lock (gate)
        {
            foreach (var item in items)
            {
                results.Enqueue(item);
            }
        }
    }

    public Task<SocksProbeResult> ProbeAsync(string proxyAddress, int proxyPort, string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            calls++;
            Requests.Add((proxyAddress, proxyPort, targetHost, targetPort, timeout));
            if (results.Count > 0)
            {
                return Task.FromResult(results.Dequeue());
            }
        }

        var tcs = new TaskCompletionSource<SocksProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}

/// <summary>Collects log entries so tests can assert on what was logged.</summary>
public sealed class RecordingLog : IAppLog
{
    private readonly object gate = new();

    public List<(LogSeverity Severity, string Source, string Message)> Entries { get; } = new();

    public void Log(LogSeverity severity, string source, string message, Exception? exception = null)
    {
        lock (gate)
        {
            Entries.Add((severity, source, message));
        }
    }

    public List<(LogSeverity Severity, string Source, string Message)> Snapshot()
    {
        lock (gate)
        {
            return Entries.ToList();
        }
    }
}
