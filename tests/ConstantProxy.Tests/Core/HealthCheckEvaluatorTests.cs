namespace ConstantProxy.Tests.Core;

public class HealthCheckEvaluatorTests
{
    private static readonly SocksProbeResult Good = SocksProbeResult.Ok(TimeSpan.FromMilliseconds(20));
    private static readonly SocksProbeResult Bad = SocksProbeResult.Fail(ProbeFailure.Timeout);

    [Fact]
    public void SingleFailureDoesNotDegrade()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 3 });
        Assert.Equal(HealthTransition.None, e.Record(Bad));
        Assert.False(e.IsDegraded);
    }

    [Fact]
    public void ThresholdConsecutiveFailuresDegradeExactlyOnce()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 3 });
        Assert.Equal(HealthTransition.None, e.Record(Bad));
        Assert.Equal(HealthTransition.None, e.Record(Bad));
        Assert.Equal(HealthTransition.Degrade, e.Record(Bad));
        Assert.Equal(HealthTransition.None, e.Record(Bad));
        Assert.True(e.IsDegraded);
        Assert.Equal(4, e.ConsecutiveFailures);
    }

    [Fact]
    public void SuccessInBetweenResetsTheConsecutiveCount()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 3 });
        e.Record(Bad);
        e.Record(Bad);
        e.Record(Good);
        Assert.Equal(HealthTransition.None, e.Record(Bad));
        Assert.Equal(1, e.ConsecutiveFailures);
        Assert.Equal(3, e.TotalFailures);
        Assert.Equal(4, e.TotalChecks);
    }

    [Fact]
    public void FirstSuccessAfterDegradationRecovers()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 1 });
        Assert.Equal(HealthTransition.Degrade, e.Record(Bad));
        Assert.Equal(HealthTransition.Recover, e.Record(Good));
        Assert.False(e.IsDegraded);
        Assert.Equal(HealthTransition.None, e.Record(Good));
    }

    [Fact]
    public void ReconnectIsOnlyRequestedWhenEnabledAndThresholdReached()
    {
        var off = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 2, ReconnectOnFailure = false, ReconnectAfterFailures = 3 });
        var results = Enumerable.Range(0, 10).Select(_ => off.Record(Bad)).ToList();
        Assert.DoesNotContain(HealthTransition.Reconnect, results);

        var on = new HealthCheckEvaluator(new HealthCheckSettings { FailureThreshold = 2, ReconnectOnFailure = true, ReconnectAfterFailures = 4 });
        Assert.Equal(new[] { HealthTransition.None, HealthTransition.Degrade, HealthTransition.None, HealthTransition.Reconnect },
            Enumerable.Range(0, 4).Select(_ => on.Record(Bad)).ToArray());
    }

    [Fact]
    public void LatencyIsTrackedAndAveragedOverRecentSuccesses()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings());
        Assert.Null(e.AverageLatency);
        e.Record(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(10)));
        e.Record(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(30)));
        Assert.Equal(TimeSpan.FromMilliseconds(30), e.LastLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(20), e.AverageLatency);
        e.Record(Bad);
        Assert.Null(e.LastLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(20), e.AverageLatency);
    }

    [Fact]
    public void LatencyWindowIsBounded()
    {
        var e = new HealthCheckEvaluator(new HealthCheckSettings());
        for (var i = 0; i < 20; i++)
        {
            e.Record(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(1000)));
        }

        for (var i = 0; i < 20; i++)
        {
            e.Record(SocksProbeResult.Ok(TimeSpan.FromMilliseconds(10)));
        }

        Assert.Equal(TimeSpan.FromMilliseconds(10), e.AverageLatency);
    }
}
