namespace ConstantProxy.Core.Connection;

/// <summary>
/// Backoff calculator for automatic reconnects (SPEC §10). It only counts and computes; it never sleeps or
/// spawns anything. Manual reconnects deliberately never touch it (SPEC §11).
/// </summary>
public sealed class ReconnectPolicy
{
    private readonly ReconnectSettings settings;
    private readonly Func<double> random;

    public ReconnectPolicy(ReconnectSettings settings, Func<double>? random = null)
    {
        this.settings = settings;
        this.random = random ?? Random.Shared.NextDouble;
    }

    public bool Enabled => settings.Enabled;

    public int ConsecutiveFailures { get; private set; }

    /// <summary>Returns the delay for the next retry and counts one more consecutive failure.</summary>
    public TimeSpan NextDelay()
    {
        var delay = ComputeDelay(settings, ConsecutiveFailures, random);
        ConsecutiveFailures++;
        return delay;
    }

    /// <summary>Delay the next retry would use, without consuming it.</summary>
    public TimeSpan PeekDelay() => ComputeDelay(settings, ConsecutiveFailures, random: null);

    /// <summary>Call when a connection ends; resets the counter if it had stayed healthy long enough.</summary>
    public void RegisterConnectionEnded(TimeSpan healthyFor)
    {
        if (healthyFor >= TimeSpan.FromSeconds(settings.HealthyResetSeconds))
        {
            Reset();
        }
    }

    public void Reset() => ConsecutiveFailures = 0;

    /// <summary>
    /// Pure delay calculation: sequence entry (last one repeats), capped by the maximum, optional jitter.
    /// With <paramref name="random"/> null, no jitter is applied.
    /// </summary>
    public static TimeSpan ComputeDelay(ReconnectSettings settings, int failureIndex, Func<double>? random)
    {
        var sequence = settings.DelaysSeconds;
        double seconds = sequence.Count == 0
            ? settings.MaxDelaySeconds
            : sequence[Math.Min(Math.Max(failureIndex, 0), sequence.Count - 1)];

        if (random is not null && settings.JitterPercent > 0 && seconds > 0)
        {
            var spread = settings.JitterPercent / 100.0;
            seconds *= 1 + ((random() * 2) - 1) * spread;
        }

        seconds = Math.Min(Math.Max(seconds, 0), Math.Max(settings.MaxDelaySeconds, 0));
        return TimeSpan.FromSeconds(seconds);
    }
}
