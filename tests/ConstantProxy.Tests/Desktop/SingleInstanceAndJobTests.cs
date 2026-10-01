using System.Diagnostics;

namespace ConstantProxy.Tests.Desktop;

public class SingleInstanceGuardTests
{
    private static string UniqueName() => "constantproxy-test-" + Guid.NewGuid().ToString("N")[..12];

    // Mutex ownership is thread-affine, so these tests are synchronous and attempt competing acquisitions on another thread.
    private static SingleInstanceGuard? AcquireOnAnotherThread(string name)
    {
        SingleInstanceGuard? result = null;
        var thread = new Thread(() => result = SingleInstanceGuard.TryAcquire(name));
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void FirstInstanceAcquiresAndASecondOneIsRefused()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(AcquireOnAnotherThread(name));
    }

    [Fact]
    public void DifferentNamesDoNotInterfere()
    {
        using var a = SingleInstanceGuard.TryAcquire(UniqueName());
        using var b = SingleInstanceGuard.TryAcquire(UniqueName());
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void ReleasingAllowsANewInstance()
    {
        var name = UniqueName();
        var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        first!.Dispose();

        var second = AcquireOnAnotherThread(name);
        Assert.NotNull(second);
        // Disposed on a different thread than acquired: must not throw.
        second!.Dispose();
    }

    [Fact]
    public void SecondInstanceCanActivateTheFirst()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name);
        using var activated = new ManualResetEventSlim();
        first!.StartListening(activated.Set);

        Assert.True(SingleInstanceGuard.TryActivateExisting(name, TimeSpan.FromSeconds(5)));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RepeatedActivationsAreAllDelivered()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name);
        var count = 0;
        using var three = new CountdownEvent(3);
        first!.StartListening(() =>
        {
            Interlocked.Increment(ref count);
            three.Signal();
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.True(SingleInstanceGuard.TryActivateExisting(name, TimeSpan.FromSeconds(5)));
        }

        Assert.True(three.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, count);
    }

    [Fact]
    public void ActivatingWhenNobodyListensFailsQuicklyAndHonestly()
    {
        var stopwatch = Stopwatch.StartNew();
        Assert.False(SingleInstanceGuard.TryActivateExisting(UniqueName(), TimeSpan.FromMilliseconds(300)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GarbageOnThePipeDoesNotActivate()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name);
        using var activated = new ManualResetEventSlim();
        first!.StartListening(activated.Set);

        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name + "-activate", System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            var bytes = System.Text.Encoding.UTF8.GetBytes("HELLO\n");
            client.Write(bytes, 0, bytes.Length);
        }

        Assert.False(activated.Wait(TimeSpan.FromMilliseconds(400)));
        Assert.True(SingleInstanceGuard.TryActivateExisting(name, TimeSpan.FromSeconds(5))); // still alive and responsive
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DisposeStopsTheListener()
    {
        var name = UniqueName();
        var first = SingleInstanceGuard.TryAcquire(name);
        first!.StartListening(() => { });
        first.Dispose();
        first.Dispose(); // idempotent
        Assert.False(SingleInstanceGuard.TryActivateExisting(name, TimeSpan.FromMilliseconds(300)));
    }

    [Theory]
    [InlineData("alice", "constantproxy-alice")]
    [InlineData("DOMAIN\\bob smith", "constantproxy-DOMAINbobsmith")]
    [InlineData("", "constantproxy-user")]
    [InlineData("!!!", "constantproxy-user")]
    public void NamesAreSanitisedPerUser(string user, string expected) =>
        Assert.Equal(expected, SingleInstanceGuard.NameFor(user));
}

public class ChildProcessJobTests
{
    [Fact]
    public void DoesNothingOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!;
        try
        {
            Assert.False(ChildProcessJob.TryAssign(process));
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public void AssignsAChildOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True(ChildProcessJob.TryAssign(process));
        }
        finally
        {
            process.Kill();
        }
    }
}
