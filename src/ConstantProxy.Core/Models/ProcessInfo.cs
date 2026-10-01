namespace ConstantProxy.Core.Models;

/// <summary>Facts about one supervised ssh process (SPEC §9).</summary>
public sealed class ProcessInfo
{
    public int Pid { get; init; }

    public DateTimeOffset StartTimeUtc { get; init; }

    public DateTimeOffset? ExitTimeUtc { get; set; }

    public int? ExitCode { get; set; }

    public Guid ProfileId { get; init; }

    /// <summary>0 for the first launch of a session, then increments with each reconnect.</summary>
    public int ReconnectAttempt { get; init; }
}
