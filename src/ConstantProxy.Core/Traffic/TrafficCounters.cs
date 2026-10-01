namespace ConstantProxy.Core.Traffic;

/// <summary>Cumulative payload bytes through the proxy: <see cref="UploadBytes"/> client to remote, <see cref="DownloadBytes"/> remote to client.</summary>
public readonly record struct TrafficCounters(long UploadBytes, long DownloadBytes);

/// <summary>Where ssh must listen when a traffic monitor sits in front of it.</summary>
public sealed record SshListenOverride(string Address, int Port);

/// <summary>
/// Replaceable traffic measurement backend (SPEC §16). Implementations must measure only proxy traffic and must
/// report "unavailable" (null totals) rather than guessing. Rates, peaks and averages are derived by
/// <see cref="TrafficStatistics"/>, so backends only supply monotonic cumulative totals.
/// </summary>
public interface ITrafficMonitor
{
    string Name { get; }

    /// <summary>False when this backend cannot produce trustworthy numbers (the UI then shows "Unavailable").</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// If the monitor sits in front of ssh, returns the loopback endpoint ssh must listen on instead of the
    /// user-facing one; otherwise null.
    /// </summary>
    SshListenOverride? PrepareBackend(Profile profile);

    /// <summary>Begins measuring once ssh is confirmed listening. May bind the user-facing endpoint.</summary>
    /// <exception cref="TrafficMonitorException">The monitor could not start.</exception>
    void Start(Profile profile, SshListenOverride? backend);

    /// <summary>Stops measuring and releases any sockets. Totals are kept until <see cref="ResetSession"/>.</summary>
    void Stop();

    /// <summary>Zeroes the cumulative totals; called when a new user session begins.</summary>
    void ResetSession();

    /// <summary>Cumulative totals for the current session, or null when unavailable.</summary>
    TrafficCounters? ReadTotals();
}

public sealed class TrafficMonitorException : Exception
{
    public TrafficMonitorException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Honest fallback: measures nothing and says so.</summary>
public sealed class NullTrafficMonitor : ITrafficMonitor
{
    public string Name => "none";

    public bool IsAvailable => false;

    public string? UnavailableReason => "Traffic measurement is turned off.";

    public SshListenOverride? PrepareBackend(Profile profile) => null;

    public void Start(Profile profile, SshListenOverride? backend)
    {
    }

    public void Stop()
    {
    }

    public void ResetSession()
    {
    }

    public TrafficCounters? ReadTotals() => null;
}
