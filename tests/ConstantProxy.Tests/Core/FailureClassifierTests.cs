namespace ConstantProxy.Tests.Core;

public class FailureClassifierTests
{
    private static FailureInfo Classify(string text, FailureStage stage = FailureStage.Running, int? exit = 255, bool listener = true) =>
        FailureClassifier.Classify(new FailureContext(stage, exit, text, listener, 10080));

    [Theory]
    [InlineData("user@host: Permission denied (publickey).", FailureCategory.Authentication, "ssh.auth", false)]
    [InlineData("Permission denied (publickey,password).", FailureCategory.Authentication, "ssh.auth", false)]
    [InlineData("Received disconnect from 1.2.3.4 port 22:2: Too many authentication failures", FailureCategory.Authentication, "ssh.auth", false)]
    [InlineData("@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@\nHost key verification failed.", FailureCategory.HostVerification, "ssh.hostkey", false)]
    [InlineData("Host key verification failed.", FailureCategory.HostVerification, "ssh.hostkey", false)]
    [InlineData("bind [127.0.0.1]:10080: Address already in use\nchannel_setup_fwd_listener_tcpip: cannot listen to port: 10080\nCould not request local forwarding.", FailureCategory.Forwarding, "forward.bind", false)]
    [InlineData("bind [127.0.0.1]:80: Permission denied", FailureCategory.Forwarding, "forward.bind", false)]
    [InlineData("ssh: Could not resolve hostname nosuchhost: Name or service not known", FailureCategory.Network, "network.dns", true)]
    [InlineData("ssh: Could not resolve hostname x: No such host is known.", FailureCategory.Network, "network.dns", true)]
    [InlineData("ssh: connect to host example.org port 22: Network is unreachable", FailureCategory.Network, "network.unreachable", true)]
    [InlineData("ssh: connect to host example.org port 22: No route to host", FailureCategory.Network, "network.unreachable", true)]
    [InlineData("ssh: connect to host example.org port 22: Connection timed out", FailureCategory.RemoteConnection, "remote.timeout", true)]
    [InlineData("ssh: connect to host example.org port 22: Connection refused", FailureCategory.RemoteConnection, "remote.refused", true)]
    [InlineData("ssh: connect to host h port 22: No connection could be made because the target machine actively refused it.", FailureCategory.RemoteConnection, "remote.refused", true)]
    [InlineData("Connection reset by 1.2.3.4 port 22", FailureCategory.RemoteConnection, "remote.dropped", true)]
    [InlineData("client_loop: send disconnect: Broken pipe", FailureCategory.RemoteConnection, "remote.dropped", true)]
    [InlineData("kex_exchange_identification: read: Connection reset by peer", FailureCategory.RemoteConnection, "remote.dropped", true)]
    [InlineData("command-line: line 0: Bad configuration option: foo", FailureCategory.LocalConfiguration, "ssh.args", false)]
    [InlineData("C:\\Users\\me/.ssh/config: line 4: Bad configuration option: x", FailureCategory.LocalConfiguration, "ssh.args", false)]
    public void RecognisesCommonOpenSshFailures(string text, FailureCategory category, string code, bool retryable)
    {
        var failure = Classify(text);
        Assert.Equal(category, failure.Category);
        Assert.Equal(code, failure.Code);
        Assert.Equal(retryable, failure.Retryable);
        Assert.Contains(text.Trim().Split('\n')[0].Trim(), failure.Details!);
    }

    [Fact]
    public void ClassificationIsCaseInsensitive() =>
        Assert.Equal("ssh.auth", Classify("PERMISSION DENIED (PUBLICKEY)").Code);

    [Fact]
    public void HostKeyBeatsAuthenticationWhenBothAppear() =>
        Assert.Equal("ssh.hostkey", Classify("Host key verification failed.\nPermission denied (publickey).").Code);

    [Fact]
    public void UnrecognisedExitIsUnknownAndRetryable()
    {
        var failure = Classify("some message nobody has seen before", exit: 1);
        Assert.Equal(FailureCategory.Unknown, failure.Category);
        Assert.Equal("ssh.exited", failure.Code);
        Assert.True(failure.Retryable);
        Assert.Contains("exit code 1", failure.Message);
    }

    [Fact]
    public void SilentCrashDuringStartupIsUnknown()
    {
        var failure = Classify("", FailureStage.Startup, exit: 255, listener: false);
        Assert.Equal("ssh.exited.startup", failure.Code);
        Assert.Equal(FailureCategory.Unknown, failure.Category);
    }

    [Fact]
    public void StartupTimeoutWithoutExitOrTextIsAForwardingProblem()
    {
        var failure = Classify("", FailureStage.Startup, exit: null, listener: false);
        Assert.Equal("startup.timeout", failure.Code);
        Assert.Equal(FailureCategory.Forwarding, failure.Category);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void StartupTimeoutStillSurfacesAKnownReasonFromOutput()
    {
        var failure = Classify("ssh: connect to host h port 22: Connection timed out", FailureStage.Startup, exit: null, listener: false);
        Assert.Equal("remote.timeout", failure.Code);
    }

    [Fact]
    public void HealthCheckFailureIsANetworkProblem()
    {
        var failure = Classify("", FailureStage.HealthCheck, exit: null);
        Assert.Equal("health.failed", failure.Code);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void PortFailuresCarryThePortForLocalization()
    {
        var inUse = FailureClassifier.PortInUse(10080, retryable: false);
        Assert.Equal("Port 10080 is already in use.", inUse.Message);
        Assert.Equal(new[] { "10080" }, inUse.Arguments);
        Assert.Equal(FailureCategory.LocalConfiguration, inUse.Category);
        Assert.False(inUse.Retryable);
        Assert.True(FailureClassifier.PortInUse(1, retryable: true).Retryable);
        Assert.False(FailureClassifier.PortAccessDenied(80).Retryable);
    }

    [Fact]
    public void VeryLongOutputIsTruncatedToTheTail()
    {
        var text = new string('x', 5000) + "Permission denied (publickey)";
        var failure = Classify(text);
        Assert.True(failure.Details!.Length <= 2000);
        Assert.EndsWith("(publickey)", failure.Details);
    }
}
