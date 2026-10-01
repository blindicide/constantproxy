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

    /// <summary>When set, delays for which this returns true block (others stay instant); lets one clock drive several loops.</summary>
    public Func<TimeSpan, bool>? BlockWhen { get; set; }

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
            if (!BlockDelays && !(BlockWhen?.Invoke(delay) ?? false))
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
