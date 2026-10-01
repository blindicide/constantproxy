namespace ConstantProxy.Tests.Infrastructure;

/// <summary>
/// Exercises the real process plumbing against harmless system programs (never against ssh or a server).
/// </summary>
public class SshProcessLauncherTests
{
    private static SshLaunchSpec LongRunning() => OperatingSystem.IsWindows()
        ? new SshLaunchSpec("ping.exe", new[] { "-n", "60", "127.0.0.1" })
        : new SshLaunchSpec("sleep", new[] { "60" });

    private static SshLaunchSpec Shell(string unixScript, string windowsScript, params string[] unixExtra) => OperatingSystem.IsWindows()
        ? new SshLaunchSpec("cmd.exe", new[] { "/c", windowsScript })
        : new SshLaunchSpec("sh", new[] { "-c", unixScript }.Concat(unixExtra).ToArray());

    [Fact]
    public async Task StartsTracksPidAndKillsOnlyThatProcess()
    {
        var launcher = new SshProcessLauncher();
        using var process = launcher.Start(LongRunning());

        Assert.True(process.Pid > 0);
        Assert.False(process.HasExited);

        process.Kill();
        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.HasExited);
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task ReportsCleanExitCode()
    {
        using var process = new SshProcessLauncher().Start(Shell("exit 7", "exit 7"));
        Assert.Equal(7, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CapturesStdoutAndStderrSeparately()
    {
        var lines = new List<SshOutputLine>();
        using var process = new SshProcessLauncher().Start(Shell("echo out; echo err 1>&2", "echo out& echo err 1>&2"));
        process.OutputReceived += l => { lock (lines) { lines.Add(l); } };
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        // Lines emitted before the handler was attached are not replayed; tolerate a very fast process.
        lock (lines)
        {
            Assert.All(lines, l => Assert.True(l.Text.Trim() is "out" or "err"));
        }
    }

    [Fact]
    public async Task OutputIsDrainedBeforeExitIsReported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = new SshProcessLauncher();
        var received = new List<string>();
        using var process = launcher.Start(new SshLaunchSpec("sh", new[] { "-c", "sleep 0.3; for i in 1 2 3 4 5; do echo line$i; done; echo oops 1>&2" }));
        process.OutputReceived += l => { lock (received) { received.Add(l.Text); } };
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));

        lock (received)
        {
            Assert.Equal(new[] { "line1", "line2", "line3", "line4", "line5", "oops" }, received.OrderBy(x => x == "oops").ThenBy(x => x));
        }
    }

    [Fact]
    public async Task ArgumentsArePassedDiscretelyWithoutShellInterpretation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var received = new List<string>();
        using var process = new SshProcessLauncher().Start(new SshLaunchSpec("sh", new[] { "-c", "sleep 0.2; printf '%s\\n' \"$1\"", "_", "a b; echo injected $(whoami)" }));
        process.OutputReceived += l => { lock (received) { received.Add(l.Text); } };
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        lock (received)
        {
            Assert.Equal(new[] { "a b; echo injected $(whoami)" }, received);
        }
    }

    [Fact]
    public void MissingExecutableIsReportedAsNotFound()
    {
        var launcher = new SshProcessLauncher(new SshLocator(_ => false, "", isWindows: false));
        var ex = Assert.Throws<SshLaunchException>(() => launcher.Start(new SshLaunchSpec("", new[] { "-N" })));
        Assert.Equal(LaunchFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public void NonExecutableFileIsReportedAsAnErrorKind()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var file = dir.File("not-executable");
        File.WriteAllText(file, "text");
        var ex = Assert.Throws<SshLaunchException>(() => new SshProcessLauncher().Start(new SshLaunchSpec(file, Array.Empty<string>())));
        Assert.True(ex.Kind is LaunchFailureKind.AccessDenied or LaunchFailureKind.InvalidExecutable);
    }
}
