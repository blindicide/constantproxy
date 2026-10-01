namespace ConstantProxy.Core.Connection;

public enum HealthTransition
{
    None,
    Degrade,
    Recover,
    Reconnect,
}

/// <summary>
/// Turns a stream of probe results into state-change decisions (SPEC §14): a single failure never degrades
/// the tunnel; only <see cref="HealthCheckSettings.FailureThreshold"/> consecutive failures do.
/// </summary>
public sealed class HealthCheckEvaluator
{
    private const int LatencyWindow = 20;

    private readonly HealthCheckSettings settings;
    private readonly Queue<TimeSpan> latencies = new();

    public HealthCheckEvaluator(HealthCheckSettings settings)
    {
        this.settings = settings;
    }

    public int ConsecutiveFailures { get; private set; }

    public int TotalFailures { get; private set; }

    public int TotalChecks { get; private set; }

    public bool IsDegraded { get; private set; }

    public TimeSpan? LastLatency { get; private set; }

    /// <summary>Mean of the most recent successful probes, or null before the first success.</summary>
    public TimeSpan? AverageLatency => latencies.Count == 0 ? null : TimeSpan.FromTicks((long)latencies.Average(l => l.Ticks));

    public HealthTransition Record(SocksProbeResult result)
    {
        TotalChecks++;
        if (result.Success)
        {
            ConsecutiveFailures = 0;
            LastLatency = result.Latency;
            if (result.Latency is { } latency)
            {
                latencies.Enqueue(latency);
                while (latencies.Count > LatencyWindow)
                {
                    latencies.Dequeue();
                }
            }

            if (IsDegraded)
            {
                IsDegraded = false;
                return HealthTransition.Recover;
            }

            return HealthTransition.None;
        }

        TotalFailures++;
        ConsecutiveFailures++;
        LastLatency = null;

        if (settings.ReconnectOnFailure && ConsecutiveFailures >= Math.Max(settings.ReconnectAfterFailures, settings.FailureThreshold))
        {
            return HealthTransition.Reconnect;
        }

        if (!IsDegraded && ConsecutiveFailures >= settings.FailureThreshold)
        {
            IsDegraded = true;
            return HealthTransition.Degrade;
        }

        return HealthTransition.None;
    }
}
