namespace ConstantProxy.Tests.Support;

/// <summary>
/// Deterministic clock. By default delays complete immediately while advancing virtual time and being recorded;
/// with <see cref="BlockDelays"/> they stay pending until released or cancelled.
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object gate = new();
    private readonly List<TaskCompletionSource> pending = new();
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get { lock (gate) { return now; } }
    }

    public bool BlockDelays { get; set; }

    public List<TimeSpan> RecordedDelays { get; } = new();

    public void Advance(TimeSpan by)
    {
        lock (gate)
        {
            now += by;
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            RecordedDelays.Add(delay);
            if (!BlockDelays)
            {
                now += delay;
                return Task.CompletedTask;
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending.Add(tcs);
        }

        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public void ReleaseDelays()
    {
        lock (gate)
        {
            foreach (var tcs in pending)
            {
                tcs.TrySetResult();
            }

            pending.Clear();
        }
    }
}
