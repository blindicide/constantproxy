namespace ConstantProxy.Core.Models;

/// <summary>
/// SOCKS health-check configuration (SPEC §14). The destination is a neutral, user-changeable default;
/// nothing project-controlled is ever contacted.
/// </summary>
public sealed class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 5;

    public string TargetHost { get; set; } = "example.com";

    public int TargetPort { get; set; } = 443;

    /// <summary>Consecutive failed checks before Connected becomes Degraded.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>When true, persistent failure restarts the tunnel (Degraded to Reconnecting).</summary>
    public bool ReconnectOnFailure { get; set; }

    /// <summary>Consecutive failures at which a reconnect is triggered (only if <see cref="ReconnectOnFailure"/>).</summary>
    public int ReconnectAfterFailures { get; set; } = 6;

    public HealthCheckSettings Clone() => (HealthCheckSettings)MemberwiseClone();
}
