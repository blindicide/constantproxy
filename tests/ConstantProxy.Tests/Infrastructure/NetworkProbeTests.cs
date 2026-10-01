using System.Net;
using System.Net.Sockets;

namespace ConstantProxy.Tests.Infrastructure;

public class TcpPortProbeTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void UnusedPortIsFreeAndStaysFree()
    {
        var port = FreePort();
        var probe = new TcpPortProbe();
        Assert.Equal(PortStatus.Free, probe.Check(IPAddress.Loopback, port));
        Assert.Equal(PortStatus.Free, probe.Check(IPAddress.Loopback, port)); // the probe must not keep the port
    }

    [Fact]
    public void ListeningPortIsReportedInUse()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        Assert.Equal(PortStatus.InUse, new TcpPortProbe().Check(IPAddress.Loopback, port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void OutOfRangePortIsUnavailable(int port) =>
        Assert.Equal(PortStatus.Unavailable, new TcpPortProbe().Check(IPAddress.Loopback, port));

    [Fact]
    public void AddressNotOwnedByThisMachineIsUnavailable() =>
        Assert.Equal(PortStatus.Unavailable, new TcpPortProbe().Check(IPAddress.Parse("203.0.113.77"), 12345));
}

/// <summary>A tiny in-process SOCKS5 server to exercise the probe against a real socket.</summary>
internal sealed class FakeSocksServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cts = new();

    public FakeSocksServer(Func<NetworkStream, CancellationToken, Task> handler)
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await handler(client.GetStream(), cts.Token);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                            // the client went away
                        }
                    }
                });
            }
        });
    }

    public int Port { get; }

    public byte[]? LastRequest { get; private set; }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
    }

    public static async Task<byte[]> Read(NetworkStream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new IOException("eof");
            }

            read += n;
        }

        return buffer;
    }

    /// <summary>Handler that completes the greeting and answers CONNECT with the given reply code.</summary>
    public static Func<NetworkStream, CancellationToken, Task> Replying(byte replyCode, Action<byte[]>? captured = null) => async (s, ct) =>
    {
        var greeting = await Read(s, 3, ct);
        await s.WriteAsync(new byte[] { 5, 0 }, ct);
        var header = await Read(s, 4, ct);
        var lengthPrefix = header[3] == 3 ? await Read(s, 1, ct) : Array.Empty<byte>();
        int addressLength = header[3] switch
        {
            1 => 4,
            4 => 16,
            _ => lengthPrefix[0],
        };
        var rest = await Read(s, addressLength + 2, ct);
        captured?.Invoke(greeting.Concat(header).Concat(lengthPrefix).Concat(rest).ToArray());
        await s.WriteAsync(new byte[] { 5, replyCode, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
    };
}

public class Socks5ProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SuccessfulConnectReportsLatency()
    {
        using var server = new FakeSocksServer(FakeSocksServer.Replying(0));
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, Timeout, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.Latency >= TimeSpan.Zero);
        Assert.Equal(ProbeFailure.None, result.Failure);
    }

    [Fact]
    public async Task SendsDomainNameRequestWithPort()
    {
        byte[]? seen = null;
        using var server = new FakeSocksServer(FakeSocksServer.Replying(0, b => seen = b));
        await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, Timeout, CancellationToken.None);

        var expected = new byte[] { 5, 1, 0 }
            .Concat(new byte[] { 5, 1, 0, 3, 11 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("example.com"))
            .Concat(new byte[] { 0x01, 0xBB })
            .ToArray();
        Assert.Equal(expected, seen);
    }

    [Theory]
    [InlineData("93.184.216.34", 1, 4)]
    [InlineData("2001:db8::1", 4, 16)]
    public void IpLiteralTargetsUseAddressTypes(string ip, byte atyp, int length)
    {
        var request = Socks5Probe.BuildConnectRequest(ip, 80);
        Assert.Equal(atyp, request[3]);
        Assert.Equal(4 + length + 2, request.Length);
        Assert.Equal(new byte[] { 0, 80 }, request[^2..]);
    }

    [Fact]
    public void OverlongHostNameIsRejected() =>
        Assert.Throws<ArgumentException>(() => Socks5Probe.BuildConnectRequest(new string('a', 300), 1));

    [Fact]
    public async Task ProxyRejectingTheConnectionIsAFailureWithTheReplyCode()
    {
        using var server = new FakeSocksServer(FakeSocksServer.Replying(5));
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, Timeout, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ProbeFailure.Rejected, result.Failure);
        Assert.Contains("5", result.Detail);
    }

    [Fact]
    public async Task ClosedProxyPortIsUnreachable()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", port, "example.com", 443, Timeout, CancellationToken.None);
        Assert.Equal(ProbeFailure.ProxyUnreachable, result.Failure);
    }

    [Fact]
    public async Task SilentProxyTimesOut()
    {
        using var server = new FakeSocksServer(async (s, ct) => await Task.Delay(Timeout.Add(Timeout), ct));
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Equal(ProbeFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task NonSocksServiceIsAProtocolError()
    {
        using var server = new FakeSocksServer(async (s, ct) =>
        {
            await FakeSocksServer.Read(s, 3, ct);
            await s.WriteAsync(new byte[] { (byte)'H', (byte)'T' }, ct);
        });
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, Timeout, CancellationToken.None);
        Assert.Equal(ProbeFailure.ProtocolError, result.Failure);
    }

    [Fact]
    public async Task ServerClosingMidHandshakeIsAProtocolError()
    {
        using var server = new FakeSocksServer((s, ct) => Task.CompletedTask);
        var result = await new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, Timeout, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ProbeFailure.ProtocolError, result.Failure);
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfLookingLikeATimeout()
    {
        using var server = new FakeSocksServer(async (s, ct) => await Task.Delay(TimeSpan.FromSeconds(10), ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Socks5Probe().ProbeAsync("127.0.0.1", server.Port, "example.com", 443, TimeSpan.FromSeconds(10), cts.Token));
    }
}

public class ListenerStartupVerifierTests
{
    private static StartupContext Context(FakeProcess p, int port = 10080, int timeoutSeconds = 15) =>
        new(new Profile { Host = "h", Port = port }, p, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task ReadyAsSoonAsTheListenerAcceptsConnections()
    {
        var clock = new FakeClock();
        var attempts = 0;
        var verifier = new ListenerStartupVerifier(clock, tryConnect: (_, _, _) => Task.FromResult(++attempts >= 3));
        var outcome = await verifier.WaitUntilReadyAsync(Context(new FakeProcess(clock.UtcNow)), CancellationToken.None);
        Assert.Equal(StartupOutcome.Ready, outcome);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TimesOutWhenTheListenerNeverAppears()
    {
        var clock = new FakeClock();
        var verifier = new ListenerStartupVerifier(clock, TimeSpan.FromMilliseconds(250), (_, _, _) => Task.FromResult(false));
        var outcome = await verifier.WaitUntilReadyAsync(Context(new FakeProcess(clock.UtcNow), timeoutSeconds: 15), CancellationToken.None);
        Assert.Equal(StartupOutcome.TimedOut, outcome);
        Assert.True(clock.RecordedDelays.Count is >= 59 and <= 61);
    }

    [Fact]
    public async Task ReportsProcessExitInsteadOfWaitingForTheTimeout()
    {
        var clock = new FakeClock();
        var process = new FakeProcess(clock.UtcNow);
        var calls = 0;
        var verifier = new ListenerStartupVerifier(clock, tryConnect: (_, _, _) =>
        {
            if (++calls == 2)
            {
                process.Exit(255);
            }

            return Task.FromResult(false);
        });
        Assert.Equal(StartupOutcome.ProcessExited, await verifier.WaitUntilReadyAsync(Context(process), CancellationToken.None));
    }

    [Fact]
    public async Task AlreadyExitedProcessIsNeverProbed()
    {
        var clock = new FakeClock();
        var process = new FakeProcess(clock.UtcNow);
        process.Exit(1);
        var verifier = new ListenerStartupVerifier(clock, tryConnect: (_, _, _) => throw new InvalidOperationException("should not probe"));
        Assert.Equal(StartupOutcome.ProcessExited, await verifier.WaitUntilReadyAsync(Context(process), CancellationToken.None));
    }

    [Fact]
    public async Task ProbesTheListenPortOverrideWhenGiven()
    {
        var clock = new FakeClock();
        int? probed = null;
        var verifier = new ListenerStartupVerifier(clock, tryConnect: (_, port, _) => { probed = port; return Task.FromResult(true); });
        var context = Context(new FakeProcess(clock.UtcNow)) with { ListenPort = 45000 };
        await verifier.WaitUntilReadyAsync(context, CancellationToken.None);
        Assert.Equal(45000, probed);
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        var clock = new FakeClock { BlockDelays = true };
        var verifier = new ListenerStartupVerifier(clock, tryConnect: (_, _, _) => Task.FromResult(false));
        using var cts = new CancellationTokenSource();
        var task = verifier.WaitUntilReadyAsync(Context(new FakeProcess(clock.UtcNow)), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("garbage", "127.0.0.1")]
    public void BindAddressMapsToAConnectableAddress(string bind, string expected) =>
        Assert.Equal(expected, ListenerStartupVerifier.ConnectAddress(bind));

    [Fact]
    public async Task RealListenerIsDetected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var verifier = new ListenerStartupVerifier(SystemClock.Instance);
            var outcome = await verifier.WaitUntilReadyAsync(Context(new FakeProcess(DateTimeOffset.UtcNow), port), CancellationToken.None);
            Assert.Equal(StartupOutcome.Ready, outcome);
        }
        finally
        {
            listener.Stop();
        }
    }
}
