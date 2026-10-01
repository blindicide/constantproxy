namespace ConstantProxy.Tests.Core;

public class ProfileValidatorTests
{
    private static Profile Valid() => new() { Host = "blindicide" };

    private static ValidationResult Check(Profile p, Func<string, bool>? exists = null) =>
        ProfileValidator.Validate(p, exists ?? (_ => true));

    [Fact]
    public void DefaultsWithAHostAreValid() => Assert.True(Check(Valid()).IsValid);

    [Fact]
    public void DefaultProfileHasNoHardcodedTarget() => Assert.Equal(string.Empty, new Profile().Host);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10080, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(-5, false)]
    public void PortMustBeInRange(int port, bool ok)
    {
        var p = Valid();
        p.Port = port;
        Assert.Equal(ok, Check(p).IsValid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("my host", false)]
    [InlineData("-oProxyCommand=evil", false)]
    [InlineData("user@example.org", true)]
    [InlineData("blindicide", true)]
    public void HostRules(string host, bool ok)
    {
        var p = Valid();
        p.Host = host;
        Assert.Equal(ok, Check(p).IsValid);
    }

    [Fact]
    public void InvalidBindAddressIsAnError()
    {
        var p = Valid();
        p.BindAddress = "localhost-ish";
        Assert.Contains(Check(p).Errors, i => i.Code == "bind.invalid");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void LoopbackBindHasNoWarning(string bind)
    {
        var p = Valid();
        p.BindAddress = bind;
        Assert.Empty(Check(p).Issues);
    }

    [Fact]
    public void NonLoopbackBindWarnsButIsNotBlocked()
    {
        var p = Valid();
        p.BindAddress = "0.0.0.0";
        var result = Check(p);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, i => i.Code == "bind.nonloopback");
    }

    [Fact]
    public void ExplicitExecutablePathMustExist()
    {
        var p = Valid();
        p.SshExecutable = @"C:\Windows\System32\OpenSSH\ssh.exe";
        Assert.Contains(Check(p, _ => false).Errors, i => i.Code == "ssh.notfound");
        Assert.True(Check(p, _ => true).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh")]
    [InlineData("ssh.exe")]
    public void AutomaticOrBareNameIsNotCheckedAgainstTheFileSystem(string exe)
    {
        var p = Valid();
        p.SshExecutable = exe;
        Assert.True(Check(p, _ => false).IsValid);
    }

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(30, 0, false)]
    [InlineData(1, 1, true)]
    public void ServerAliveValuesMustBePositive(int interval, int count, bool ok)
    {
        var p = Valid();
        p.ServerAliveInterval = interval;
        p.ServerAliveCountMax = count;
        Assert.Equal(ok, Check(p).IsValid);
    }

    [Fact]
    public void ReconnectDelayRules()
    {
        var p = Valid();
        p.Reconnect.DelaysSeconds = new List<int>();
        Assert.Contains(Check(p).Errors, i => i.Code == "reconnect.delays");

        p.Reconnect.DelaysSeconds = new List<int> { 0, -1 };
        Assert.Contains(Check(p).Errors, i => i.Code == "reconnect.delays");

        p.Reconnect.DelaysSeconds = new List<int> { 1, 0 };
        Assert.Contains(Check(p).Errors, i => i.Code == "reconnect.delays.last");

        p.Reconnect.DelaysSeconds = new List<int> { 0, 5 };
        Assert.True(Check(p).IsValid);
    }

    [Fact]
    public void ReconnectTimingAndJitterRules()
    {
        var p = Valid();
        p.Reconnect.MaxDelaySeconds = 0;
        p.Reconnect.HealthyResetSeconds = 0;
        p.Reconnect.JitterPercent = 101;
        Assert.Equal(3, Check(p).Errors.Count());
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        var p = Valid();
        p.Name = " ";
        Assert.Contains(Check(p).Errors, i => i.Code == "profile.name.empty");
    }

    [Fact]
    public void StartupTimeoutMustBePositive()
    {
        var p = Valid();
        p.StartupTimeoutSeconds = 0;
        Assert.Contains(Check(p).Errors, i => i.Field == nameof(Profile.StartupTimeoutSeconds));
    }

    [Fact]
    public void HealthCheckDefaultsAreValidAndNeutral()
    {
        var m = new Profile().Monitoring;
        Assert.True(m.Enabled);
        Assert.Equal(10, m.IntervalSeconds);
        Assert.Equal(5, m.TimeoutSeconds);
        Assert.Equal(3, m.FailureThreshold);
        Assert.False(m.ReconnectOnFailure);
        Assert.True(Check(Valid()).IsValid);
    }

    [Theory]
    [InlineData("", 443, "health.host")]
    [InlineData("bad host", 443, "health.host")]
    [InlineData("example.com", 0, "port.range")]
    [InlineData("example.com", 70000, "port.range")]
    public void HealthTargetIsValidated(string host, int port, string code)
    {
        var p = Valid();
        p.Monitoring.TargetHost = host;
        p.Monitoring.TargetPort = port;
        Assert.Contains(Check(p).Errors, i => i.Code == code && i.Field == nameof(Profile.Monitoring));
    }

    [Fact]
    public void HealthTimingAndThresholdsAreValidated()
    {
        var p = Valid();
        p.Monitoring.IntervalSeconds = 0;
        p.Monitoring.FailureThreshold = 0;
        var codes = Check(p).Errors.Select(i => i.Code).ToList();
        Assert.Contains("timing.positive", codes);
        Assert.Contains("health.threshold", codes);
    }

    [Fact]
    public void ReconnectThresholdMayNotBeBelowTheFailureThreshold()
    {
        var p = Valid();
        p.Monitoring.ReconnectOnFailure = true;
        p.Monitoring.FailureThreshold = 5;
        p.Monitoring.ReconnectAfterFailures = 3;
        Assert.Contains(Check(p).Errors, i => i.Code == "health.reconnectafter");
    }

    [Fact]
    public void DisabledHealthCheckingSkipsItsValidation()
    {
        var p = Valid();
        p.Monitoring.Enabled = false;
        p.Monitoring.TargetHost = "";
        p.Monitoring.IntervalSeconds = 0;
        Assert.True(Check(p).IsValid);
    }
}
