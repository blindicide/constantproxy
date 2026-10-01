namespace ConstantProxy.Tests.Core;

public class ReconnectPolicyTests
{
    [Fact]
    public void DefaultSequenceMatchesSpecification()
    {
        var policy = new ReconnectPolicy(new ReconnectSettings());
        var delays = Enumerable.Range(0, 9).Select(_ => (int)policy.NextDelay().TotalSeconds).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 5, 10, 15, 15, 15, 15 }, delays);
    }

    [Fact]
    public void MaxDelayCapsTheSequence()
    {
        var settings = new ReconnectSettings { DelaysSeconds = new List<int> { 0, 30, 60 }, MaxDelaySeconds = 20 };
        var policy = new ReconnectPolicy(settings);
        Assert.Equal(TimeSpan.Zero, policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(20), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(20), policy.NextDelay());
    }

    [Fact]
    public void CustomSequenceIsHonouredAndLastEntryRepeats()
    {
        var policy = new ReconnectPolicy(new ReconnectSettings { DelaysSeconds = new List<int> { 3, 7 }, MaxDelaySeconds = 60 });
        Assert.Equal(new[] { 3.0, 7.0, 7.0 }, new[] { policy.NextDelay(), policy.NextDelay(), policy.NextDelay() }.Select(d => d.TotalSeconds));
    }

    [Fact]
    public void EmptySequenceFallsBackToMaxDelay()
    {
        var policy = new ReconnectPolicy(new ReconnectSettings { DelaysSeconds = new List<int>(), MaxDelaySeconds = 12 });
        Assert.Equal(TimeSpan.FromSeconds(12), policy.NextDelay());
    }

    [Fact]
    public void PeekDoesNotConsumeAFailure()
    {
        var policy = new ReconnectPolicy(new ReconnectSettings());
        policy.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(1), policy.PeekDelay());
        Assert.Equal(TimeSpan.FromSeconds(1), policy.PeekDelay());
        Assert.Equal(1, policy.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(29, 3)]
    [InlineData(30, 0)]
    [InlineData(300, 0)]
    public void HealthyPeriodResetsTheCounterOnlyAtOrAboveThreshold(int healthySeconds, int expectedFailures)
    {
        var policy = new ReconnectPolicy(new ReconnectSettings { HealthyResetSeconds = 30 });
        policy.NextDelay();
        policy.NextDelay();
        policy.NextDelay();
        policy.RegisterConnectionEnded(TimeSpan.FromSeconds(healthySeconds));
        Assert.Equal(expectedFailures, policy.ConsecutiveFailures);
        if (expectedFailures == 0)
        {
            Assert.Equal(TimeSpan.Zero, policy.PeekDelay());
        }
    }

    [Theory]
    [InlineData(0.0, 9.0)]
    [InlineData(0.5, 10.0)]
    [InlineData(1.0, 11.0)]
    public void JitterSpreadsNonZeroDelaysWithinConfiguredBounds(double random, double expectedSeconds)
    {
        var settings = new ReconnectSettings { DelaysSeconds = new List<int> { 10 }, MaxDelaySeconds = 100, JitterPercent = 10 };
        var policy = new ReconnectPolicy(settings, () => random);
        Assert.Equal(expectedSeconds, policy.NextDelay().TotalSeconds, 6);
    }

    [Fact]
    public void JitterNeverTouchesZeroDelayAndNeverExceedsMax()
    {
        var settings = new ReconnectSettings { DelaysSeconds = new List<int> { 0, 15 }, MaxDelaySeconds = 15, JitterPercent = 50 };
        var policy = new ReconnectPolicy(settings, () => 1.0);
        Assert.Equal(TimeSpan.Zero, policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(15), policy.NextDelay());
    }

    [Fact]
    public void DisabledSettingIsReported() =>
        Assert.False(new ReconnectPolicy(new ReconnectSettings { Enabled = false }).Enabled);
}
