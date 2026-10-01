namespace ConstantProxy.Core.Desktop;

public enum NotificationKind
{
    ConnectionLost,
    ConnectionFailed,
    ConnectionRestored,
}

/// <summary>A user-facing notification. Text is composed by the presentation layer (and localized) from the kind and arguments.</summary>
public sealed record AppNotification(NotificationKind Kind, TimeSpan? Downtime = null, FailureInfo? Failure = null);

/// <summary>
/// Decides which notifications are worth showing (SPEC §33): brief outages stay silent, a "restored" message is only
/// sent if the user was told about the loss, and intentional disconnects never notify. Pure and clock-free; the
/// caller supplies times and asks <see cref="Evaluate"/> when <see cref="NextCheckAt"/> arrives.
/// </summary>
public sealed class NotificationFilter
{
    private readonly Func<NotificationConfig> settings;
    private DateTimeOffset? outageStart;
    private bool lossNotified;

    public NotificationFilter(Func<NotificationConfig> settings)
    {
        this.settings = settings;
    }

    /// <summary>When an unannounced outage will become long enough to notify, or null if nothing is pending.</summary>
    public DateTimeOffset? NextCheckAt =>
        outageStart is { } start && !lossNotified && settings().NotifyOnFailure
            ? start + TimeSpan.FromSeconds(Math.Max(settings().MinimumOutageSeconds, 0))
            : null;

    public bool InOutage => outageStart is not null;

    public IReadOnlyList<AppNotification> OnStateChanged(StateChange change)
    {
        var s = settings();
        var result = new List<AppNotification>();

        switch (change.New)
        {
            case ConnectionState.Reconnecting:
                if (outageStart is null && change.Old is ConnectionState.Connected or ConnectionState.Degraded)
                {
                    outageStart = change.AtUtc;
                    lossNotified = false;
                    if (s.NotifyOnFailure && s.MinimumOutageSeconds <= 0)
                    {
                        lossNotified = true;
                        result.Add(new AppNotification(NotificationKind.ConnectionLost, Failure: change.Failure));
                    }
                }

                break;

            case ConnectionState.Failed:
                if (s.NotifyOnFailure)
                {
                    result.Add(new AppNotification(NotificationKind.ConnectionFailed, Failure: change.Failure));
                }

                // A failed connection waits for the user; a later success is a user action, not a "recovery".
                Reset();
                break;

            case ConnectionState.Connected:
                if (outageStart is { } start)
                {
                    var downtime = change.AtUtc - start;
                    var announce = lossNotified && s.NotifyOnRecovery;
                    Reset();
                    if (announce)
                    {
                        result.Add(new AppNotification(NotificationKind.ConnectionRestored, Downtime: downtime));
                    }
                }

                break;

            case ConnectionState.Stopping:
            case ConnectionState.Disconnected:
                Reset();
                break;
        }

        return result;
    }

    /// <summary>Call at or after <see cref="NextCheckAt"/>: yields the "lost" notification if the outage is still going on.</summary>
    public AppNotification? Evaluate(DateTimeOffset now, FailureInfo? failure = null)
    {
        if (NextCheckAt is not { } due || now < due)
        {
            return null;
        }

        lossNotified = true;
        return new AppNotification(NotificationKind.ConnectionLost, Failure: failure);
    }

    private void Reset()
    {
        outageStart = null;
        lossNotified = false;
    }
}
