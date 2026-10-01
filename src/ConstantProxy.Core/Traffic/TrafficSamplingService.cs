namespace ConstantProxy.Core.Traffic;

/// <summary>
/// Samples an <see cref="ITrafficMonitor"/> once per interval (default 1 s, SPEC §17) into <see cref="TrafficStatistics"/>.
/// One light loop; nothing runs while no session exists.
/// </summary>
public sealed class TrafficSamplingService : IAsyncDisposable
{
    private readonly ITrafficMonitor monitor;
    private readonly IClock clock;
    private readonly TimeSpan interval;
    private readonly object gate = new();
    private CancellationTokenSource? cts;
    private Task? loop;

    public TrafficSamplingService(ITrafficMonitor monitor, IClock? clock = null, TimeSpan? interval = null, int historyCapacity = 3600)
    {
        this.monitor = monitor;
        this.clock = clock ?? SystemClock.Instance;
        this.interval = interval ?? TimeSpan.FromSeconds(1);
        Statistics = new TrafficStatistics(historyCapacity);
    }

    public TrafficStatistics Statistics { get; }

    public ITrafficMonitor Monitor => monitor;

    public bool IsRunning
    {
        get { lock (gate) { return loop is not null; } }
    }

    /// <summary>Raised on the sampling thread after each sample.</summary>
    public event Action<TrafficPoint?>? Sampled;

    /// <summary>Begins a new session: clears statistics and the monitor's totals, then starts sampling.</summary>
    public void StartSession()
    {
        lock (gate)
        {
            if (loop is not null)
            {
                return;
            }

            monitor.ResetSession();
            Statistics.Reset(clock.UtcNow);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Stops sampling after one final sample so the closing totals are not lost.</summary>
    public async Task StopAsync()
    {
        Task? running;
        CancellationTokenSource? source;
        lock (gate)
        {
            running = loop;
            source = cts;
            loop = null;
            cts = null;
        }

        if (running is null || source is null)
        {
            return;
        }

        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        source.Dispose();
        SampleOnce();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await clock.Delay(interval, token).ConfigureAwait(false);
            SampleOnce();
        }
    }

    private void SampleOnce()
    {
        Statistics.Sample(clock.UtcNow, monitor.ReadTotals());
        Sampled?.Invoke(Statistics.Recent(1).FirstOrDefault());
    }
}
