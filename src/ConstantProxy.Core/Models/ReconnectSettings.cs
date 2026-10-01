namespace ConstantProxy.Core.Models;

/// <summary>Reconnect/backoff configuration (SPEC §10). All values are user-configurable.</summary>
public sealed class ReconnectSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay before the Nth consecutive retry (index 0 = first retry). The last entry repeats.</summary>
    public List<int> DelaysSeconds { get; set; } = new() { 0, 1, 2, 5, 10, 15 };

    /// <summary>Upper bound applied to every computed delay.</summary>
    public int MaxDelaySeconds { get; set; } = 15;

    /// <summary>A connection that stayed up this long resets the failure counter.</summary>
    public int HealthyResetSeconds { get; set; } = 30;

    /// <summary>Random spread (0..100 percent) applied to non-zero delays.</summary>
    public int JitterPercent { get; set; }

    public ReconnectSettings Clone() => new()
    {
        Enabled = Enabled,
        DelaysSeconds = new List<int>(DelaysSeconds),
        MaxDelaySeconds = MaxDelaySeconds,
        HealthyResetSeconds = HealthyResetSeconds,
        JitterPercent = JitterPercent,
    };
}
