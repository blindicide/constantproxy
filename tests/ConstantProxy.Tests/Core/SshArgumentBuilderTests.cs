namespace ConstantProxy.Tests.Core;

public class SshArgumentBuilderTests
{
    private static Profile Example() => new()
    {
        Host = "blindicide",
        Port = 10080,
        IPv4Only = true,
        ServerAliveInterval = 30,
        ServerAliveCountMax = 3,
    };

    [Fact]
    public void ProducesTheExactArgumentListFromTheSpecification()
    {
        var expected = new[]
        {
            "-4", "-N", "-D", "127.0.0.1:10080",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3",
            "-o", "ExitOnForwardFailure=yes",
            "blindicide",
        };
        Assert.Equal(expected, SshArgumentBuilder.Build(Example()));
    }

    [Fact]
    public void OmitsIPv4FlagWhenAutomaticMode()
    {
        var p = Example();
        p.IPv4Only = false;
        Assert.DoesNotContain("-4", SshArgumentBuilder.Build(p));
    }

    [Fact]
    public void OmitsExitOnForwardFailureWhenDisabled()
    {
        var p = Example();
        p.ExitOnForwardFailure = false;
        Assert.DoesNotContain("ExitOnForwardFailure=yes", SshArgumentBuilder.Build(p));
    }

    [Fact]
    public void BatchModeIsOptIn()
    {
        var p = Example();
        Assert.DoesNotContain("BatchMode=yes", SshArgumentBuilder.Build(p));
        p.BatchMode = true;
        Assert.Contains("BatchMode=yes", SshArgumentBuilder.Build(p));
    }

    [Fact]
    public void AdditionalArgumentsStayDiscreteAndPrecedeTheTarget()
    {
        var p = Example();
        p.AdditionalArguments = new List<string> { "-o", "Compression=yes", "-i", @"C:\Users\some user\.ssh\id key" };
        var args = SshArgumentBuilder.Build(p);
        Assert.Equal("blindicide", args[^1]);
        Assert.Equal(@"C:\Users\some user\.ssh\id key", args[^2]);
        Assert.Equal("-i", args[^3]);
    }

    [Fact]
    public void ListenPortOverrideOnlyChangesTheDynamicForwardEndpoint()
    {
        var args = SshArgumentBuilder.Build(Example(), listenPortOverride: 45678);
        Assert.Equal("127.0.0.1:45678", args[args.ToList().IndexOf("-D") + 1]);
    }

    [Theory]
    [InlineData("127.0.0.1", 10080, "127.0.0.1:10080")]
    [InlineData("::1", 1080, "[::1]:1080")]
    [InlineData("[::1]", 1080, "[::1]:1080")]
    [InlineData("0.0.0.0", 8080, "0.0.0.0:8080")]
    public void FormatsEndpoints(string bind, int port, string expected) =>
        Assert.Equal(expected, SshArgumentBuilder.FormatEndpoint(bind, port));

    [Fact]
    public void CommandLineDisplayQuotesOnlyWhenNeeded()
    {
        var text = SshArgumentBuilder.FormatCommandLine("ssh", new[] { "-i", "C:\\my keys\\id", "host" });
        Assert.Equal("ssh -i \"C:\\my keys\\id\" host", text);
        Assert.StartsWith("ssh ", SshArgumentBuilder.FormatCommandLine("", new[] { "x" }));
    }
}

public class CommandLineSplitterTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-o Compression=yes", new[] { "-o", "Compression=yes" })]
    [InlineData("-i \"C:\\my keys\\id\" host", new[] { "-i", "C:\\my keys\\id", "host" })]
    [InlineData("-o 'A=b c'", new[] { "-o", "A=b c" })]
    [InlineData("a   b\tc", new[] { "a", "b", "c" })]
    [InlineData("\"\"", new[] { "" })]
    public void SplitsWithQuoteSupport(string input, string[] expected) =>
        Assert.Equal(expected, CommandLineSplitter.Split(input));

    [Fact]
    public void JoinThenSplitRoundTrips()
    {
        var args = new[] { "-i", "C:\\my keys\\id", "-o", "X=1" };
        Assert.Equal(args, CommandLineSplitter.Split(CommandLineSplitter.Join(args)));
    }

    [Fact]
    public void NullYieldsEmpty() => Assert.Empty(CommandLineSplitter.Split(null));
}
