using System.Net;
using System.Net.Sockets;

namespace ConstantProxy.Tests.Infrastructure;

public class BridgeTrafficMonitorTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Stand-in for ssh's local listener.</summary>
    private sealed class Backend : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();

        public Backend(Func<Socket, CancellationToken, Task> handler)
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Socket s;
                    try
                    {
                        s = await listener.AcceptSocketAsync(cts.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (s)
                        {
                            try
                            {
                                await handler(s, cts.Token);
                            }
                            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
                            {
                                // peer went away
                            }
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
        }

        public static Task Echo(Socket s, CancellationToken ct) => EchoAsync(s, ct);

        private static async Task EchoAsync(Socket s, CancellationToken ct)
        {
            var buffer = new byte[4096];
            while (true)
            {
                var n = await s.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (n == 0)
                {
                    return;
                }

                await s.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, ct);
            }
        }
    }

    private static (BridgeTrafficMonitor Bridge, Profile Profile, int PublicPort) Started(Backend backend)
    {
        var bridge = new BridgeTrafficMonitor();
        var profile = new Profile { Host = "h", Port = FreePort() };
        bridge.Start(profile, new SshListenOverride("127.0.0.1", backend.Port));
        return (bridge, profile, profile.Port);
    }

    private static async Task<byte[]> ReadExactly(Socket s, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < count)
        {
            var n = await s.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cts.Token);
            if (n == 0)
            {
                throw new IOException("unexpected EOF");
            }

            read += n;
        }

        return buffer;
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, port);
        return s;
    }

    [Fact]
    public async Task RelaysDataBothWaysAndCountsExactBytes()
    {
        using var backend = new Backend(Backend.Echo);
        var (bridge, _, port) = Started(backend);
        using var _ = bridge;
        using var client = await ConnectAsync(port);

        var payload = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        await client.SendAsync(payload);
        var echoed = await ReadExactly(client, payload.Length);

        Assert.Equal(payload, echoed);
        await TestWait.Until(() => bridge.ReadTotals() == new TrafficCounters(10_000, 10_000), what: "totals");
    }

    [Fact]
    public async Task CountsUploadAndDownloadSeparately()
    {
        var reply = new byte[5000];
        using var backend = new Backend(async (s, ct) =>
        {
            var buf = new byte[100];
            var got = 0;
            while (got < 100)
            {
                got += await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            }

            await s.SendAsync(reply, SocketFlags.None, ct);
        });
        var (bridge, _, port) = Started(backend);
        using var _ = bridge;
        using var client = await ConnectAsync(port);

        await client.SendAsync(new byte[100]);
        await ReadExactly(client, 5000);

        await TestWait.Until(() => bridge.ReadTotals() == new TrafficCounters(100, 5000), what: "totals");
    }

    [Fact]
    public async Task ManyConcurrentConnectionsAreAllAccountedFor()
    {
        using var backend = new Backend(Backend.Echo);
        var (bridge, _, port) = Started(backend);
        using var _ = bridge;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var client = await ConnectAsync(port);
            var data = new byte[1000 + i];
            await client.SendAsync(data);
            await ReadExactly(client, data.Length);
        }));

        var expected = Enumerable.Range(0, 20).Sum(i => 1000L + i);
        await TestWait.Until(() => bridge.ReadTotals() == new TrafficCounters(expected, expected), what: "totals");
    }

    [Fact]
    public async Task HalfCloseFromTheClientStillDeliversTheResponse()
    {
        using var backend = new Backend(async (s, ct) =>
        {
            var buf = new byte[1024];
            var total = 0;
            int n;
            while ((n = await s.ReceiveAsync(buf, SocketFlags.None, ct)) > 0)
            {
                total += n;
            }

            await s.SendAsync(System.Text.Encoding.ASCII.GetBytes($"got {total}"), SocketFlags.None, ct);
        });
        var (bridge, _, port) = Started(backend);
        using var _ = bridge;
        using var client = await ConnectAsync(port);

        await client.SendAsync(new byte[300]);
        client.Shutdown(SocketShutdown.Send);
        var reply = await ReadExactly(client, "got 300".Length);

        Assert.Equal("got 300", System.Text.Encoding.ASCII.GetString(reply));
    }

    [Fact]
    public async Task OnlyBridgedTrafficIsCounted()
    {
        using var backend = new Backend(Backend.Echo);
        var (bridge, _, port) = Started(backend);
        using var _ = bridge;

        // Direct traffic to the backend (as unrelated applications would produce) must not be counted.
        using (var direct = await ConnectAsync(backend.Port))
        {
            await direct.SendAsync(new byte[4000]);
            await ReadExactly(direct, 4000);
        }

        using var client = await ConnectAsync(port);
        await client.SendAsync(new byte[10]);
        await ReadExactly(client, 10);

        await TestWait.Until(() => bridge.ReadTotals() == new TrafficCounters(10, 10), what: "totals");
        await Task.Delay(50);
        Assert.Equal(new TrafficCounters(10, 10), bridge.ReadTotals());
    }

    [Fact]
    public async Task ResetSessionZeroesTheTotalsAndStopKeepsThemUntilThen()
    {
        using var backend = new Backend(Backend.Echo);
        var (bridge, _, port) = Started(backend);
        using var client = await ConnectAsync(port);
        await client.SendAsync(new byte[50]);
        await ReadExactly(client, 50);
        await TestWait.Until(() => bridge.ReadTotals() == new TrafficCounters(50, 50));

        bridge.Stop();
        Assert.Equal(new TrafficCounters(50, 50), bridge.ReadTotals());
        bridge.ResetSession();
        Assert.Equal(new TrafficCounters(0, 0), bridge.ReadTotals());
    }

    [Fact]
    public async Task TotalsAccumulateAcrossRestartsWithinASession()
    {
        using var backend = new Backend(Backend.Echo);
        var bridge = new BridgeTrafficMonitor();
        var profile = new Profile { Host = "h", Port = FreePort() };

        for (var round = 1; round <= 2; round++)
        {
            bridge.Start(profile, new SshListenOverride("127.0.0.1", backend.Port));
            using var client = await ConnectAsync(profile.Port);
            await client.SendAsync(new byte[100]);
            await ReadExactly(client, 100);
            // Bytes are counted just after they are forwarded, so wait for the count rather than racing it.
            var expected = new TrafficCounters(100 * round, 100 * round);
            await TestWait.Until(() => bridge.ReadTotals() == expected, what: $"totals after round {round}");
            bridge.Stop();
        }

        Assert.Equal(new TrafficCounters(200, 200), bridge.ReadTotals());
    }

    [Fact]
    public async Task StopClosesActiveConnectionsAndReleasesThePort()
    {
        using var backend = new Backend(Backend.Echo);
        var (bridge, profile, port) = Started(backend);
        using var client = await ConnectAsync(port);
        await client.SendAsync(new byte[1]);
        await ReadExactly(client, 1);
        await TestWait.Until(() => bridge.ActiveConnections == 1);

        bridge.Stop();

        await TestWait.Until(() => bridge.ActiveConnections == 0, what: "relays closed");
        var buffer = new byte[1];
        var closed = await client.ReceiveAsync(buffer) == 0; // peer closed the connection
        Assert.True(closed);
        Assert.Equal(PortStatus.Free, new TcpPortProbe().Check(IPAddress.Loopback, profile.Port));
        bridge.Stop(); // idempotent
    }

    [Fact]
    public async Task UnreachableBackendClosesTheClientWithoutCrashing()
    {
        var bridge = new BridgeTrafficMonitor();
        var profile = new Profile { Host = "h", Port = FreePort() };
        bridge.Start(profile, new SshListenOverride("127.0.0.1", FreePort())); // nothing listens there
        using var _ = bridge;
        using var client = await ConnectAsync(profile.Port);

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token));
        Assert.Equal(new TrafficCounters(0, 0), bridge.ReadTotals());
    }

    [Fact]
    public async Task BackendDisconnectPropagatesToTheClient()
    {
        using var backend = new Backend((s, ct) => Task.CompletedTask); // accepts then immediately closes
        var bridge = new BridgeTrafficMonitor(halfCloseGrace: TimeSpan.FromMilliseconds(200));
        var profile = new Profile { Host = "h", Port = FreePort() };
        bridge.Start(profile, new SshListenOverride("127.0.0.1", backend.Port));
        var port = profile.Port;
        using var _ = bridge;
        using var client = await ConnectAsync(port);

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token));
        await TestWait.Until(() => bridge.ActiveConnections == 0);
    }

    [Fact]
    public void StartingOnAnOccupiedPortReportsAClearError()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var bridge = new BridgeTrafficMonitor();
        var ex = Assert.Throws<TrafficMonitorException>(() => bridge.Start(new Profile { Host = "h", Port = port }, new SshListenOverride("127.0.0.1", 1)));
        Assert.Contains(port.ToString(), ex.Message);
    }

    [Fact]
    public void StartWithoutABackendIsRejected() =>
        Assert.Throws<TrafficMonitorException>(() => new BridgeTrafficMonitor().Start(new Profile { Host = "h", Port = FreePort() }, null));

    [Fact]
    public void StartingTwiceIsRejected()
    {
        var bridge = new BridgeTrafficMonitor();
        var profile = new Profile { Host = "h", Port = FreePort() };
        bridge.Start(profile, new SshListenOverride("127.0.0.1", 1));
        using var _ = bridge;
        Assert.Throws<TrafficMonitorException>(() => bridge.Start(profile, new SshListenOverride("127.0.0.1", 1)));
    }

    [Fact]
    public void PrepareBackendPicksAFreeLoopbackPort()
    {
        var backend = new BridgeTrafficMonitor().PrepareBackend(new Profile())!;
        Assert.Equal("127.0.0.1", backend.Address);
        Assert.InRange(backend.Port, 1, 65535);
        Assert.Equal(PortStatus.Free, new TcpPortProbe().Check(IPAddress.Loopback, backend.Port));
    }

    [Fact]
    public void BridgeReportsItselfAsAvailable()
    {
        var bridge = new BridgeTrafficMonitor();
        Assert.True(bridge.IsAvailable);
        Assert.Null(bridge.UnavailableReason);
        Assert.Equal(new TrafficCounters(0, 0), bridge.ReadTotals());
    }
}
