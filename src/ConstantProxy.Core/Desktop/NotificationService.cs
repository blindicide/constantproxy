namespace ConstantProxy.Core.Desktop;

/// <summary>
/// Drives a <see cref="NotificationFilter"/> from connection state changes. While an outage is pending it waits
/// (one delay, no polling) until the minimum outage duration has passed; recovery cancels the wait.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly NotificationFilter filter;
    private readonly IClock clock;
    private readonly Action<AppNotification> sink;
    private readonly object gate = new();
    private CancellationTokenSource? waiting;
    private FailureInfo? lastFailure;

    public NotificationService(Func<NotificationConfig> settings, IClock clock, Action<AppNotification> sink)
    {
        filter = new NotificationFilter(settings);
        this.clock = clock;
        this.sink = sink;
    }

    public void Attach(ConnectionManager manager) => manager.StateChanged += OnStateChanged;

    public void OnStateChanged(StateChange change)
    {
        IReadOnlyList<AppNotification> now;
        DateTimeOffset? due;
        lock (gate)
        {
            waiting?.Cancel();
            waiting?.Dispose();
            waiting = null;
            lastFailure = change.Failure ?? lastFailure;
            now = filter.OnStateChanged(change);
            due = filter.NextCheckAt;
            if (due is not null)
            {
                waiting = new CancellationTokenSource();
                _ = WaitAndEvaluateAsync(due.Value, waiting.Token);
            }
        }

        foreach (var notification in now)
        {
            sink(notification);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            waiting?.Cancel();
            waiting?.Dispose();
            waiting = null;
        }
    }

    private async Task WaitAndEvaluateAsync(DateTimeOffset due, CancellationToken token)
    {
        try
        {
            var remaining = due - clock.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await clock.Delay(remaining, token).ConfigureAwait(false);
            }

            AppNotification? notification;
            lock (gate)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                notification = filter.Evaluate(clock.UtcNow, lastFailure);
            }

            if (notification is not null)
            {
                sink(notification);
            }
        }
        catch (OperationCanceledException)
        {
            // recovered or stopped before the threshold: stay silent
        }
    }
}
