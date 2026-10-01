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

    public Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
    {
        if (!Block)
        {
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
