namespace ConstantProxy.Core.Connection;

public enum ProbeFailure
{
    None,
    ProxyUnreachable,
    Timeout,
    ProtocolError,
    Rejected,
}

public sealed record SocksProbeResult(bool Success, TimeSpan? Latency, ProbeFailure Failure, string? Detail = null)
{
    public static SocksProbeResult Ok(TimeSpan latency) => new(true, latency, ProbeFailure.None);

    public static SocksProbeResult Fail(ProbeFailure failure, string? detail = null) => new(false, null, failure, detail);
}

/// <summary>Performs one outbound connection attempt through the local SOCKS proxy (SPEC §14).</summary>
public interface ISocksProbe
{
    Task<SocksProbeResult> ProbeAsync(string proxyAddress, int proxyPort, string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>One recorded health check, for the UI, analytics and logs.</summary>
public sealed record HealthCheckResult(DateTimeOffset TimeUtc, bool Success, TimeSpan? Latency, string? Error);
