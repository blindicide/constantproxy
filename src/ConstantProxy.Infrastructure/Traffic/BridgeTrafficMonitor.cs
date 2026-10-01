using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConstantProxy.Core.Traffic;

namespace ConstantProxy.Infrastructure.Traffic;

/// <summary>
/// Application-managed byte accounting (SPEC §16). constantproxy listens on the user-facing SOCKS endpoint and relays
/// every connection verbatim to ssh's internal loopback listener, counting the bytes that pass each way.
/// It never inspects or alters the stream, so only proxy traffic is counted and no elevated privileges are needed.
/// </summary>
public sealed class BridgeTrafficMonitor : ITrafficMonitor, IDisposable
{
    private const string Source = "traffic";
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan BackendConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppLog log;
    private readonly TimeSpan halfCloseGrace;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Relay, byte> relays = new();
    private long uploadBytes;
    private long downloadBytes;
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private Task? acceptLoop;

    /// <param name="halfCloseGrace">
    /// How long a connection may linger after the remote side has finished sending, waiting for the client to close.
    /// Prevents clients that ignore the close from leaking relays; it never cuts off a download in progress.
    /// </param>
    public BridgeTrafficMonitor(IAppLog? log = null, TimeSpan? halfCloseGrace = null)
    {
        this.log = log ?? NullAppLog.Instance;
        this.halfCloseGrace = halfCloseGrace ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "bridge";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    /// <summary>Number of connections currently being relayed.</summary>
    public int ActiveConnections => relays.Count;

    public SshListenOverride? PrepareBackend(Profile profile)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new SshListenOverride(IPAddress.Loopback.ToString(), port);
    }

    public void Start(Profile profile, SshListenOverride? backend)
    {
        if (backend is null)
        {
            throw new TrafficMonitorException("The bridge needs the internal ssh endpoint.");
        }

        lock (gate)
        {
            if (listener is not null)
            {
                throw new TrafficMonitorException("The bridge is already running.");
            }

            var bind = IPAddress.Parse(profile.BindAddress.Trim().Trim('[', ']'));
            var newListener = new TcpListener(bind, profile.Port);
            if (OperatingSystem.IsWindows())
            {
                newListener.ExclusiveAddressUse = true;
            }

            try
            {
                newListener.Start(backlog: 128);
            }
            catch (SocketException ex)
            {
                throw new TrafficMonitorException($"Could not listen on {bind}:{profile.Port}: {ex.SocketErrorCode}", ex);
            }

            listener = newListener;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var target = new IPEndPoint(IPAddress.Parse(backend.Address), backend.Port);
            acceptLoop = Task.Run(() => AcceptLoopAsync(newListener, target, token));
        }

        log.Info(Source, $"Traffic bridge listening on {profile.BindAddress}:{profile.Port} -> ssh {backend.Address}:{backend.Port}");
    }

    public void Stop()
    {
        TcpListener? oldListener;
        CancellationTokenSource? oldCts;
        Task? oldLoop;
        lock (gate)
        {
            oldListener = listener;
            oldCts = cts;
            oldLoop = acceptLoop;
            listener = null;
            cts = null;
            acceptLoop = null;
        }

        if (oldListener is null)
        {
            return;
        }

        oldCts?.Cancel();
        oldListener.Stop();
        foreach (var relay in relays.Keys)
        {
            relay.Abort();
        }

        try
        {
            oldLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the loop ends by cancellation
        }

        oldCts?.Dispose();
        log.Info(Source, "Traffic bridge stopped.");
    }

    public void ResetSession()
    {
        Interlocked.Exchange(ref uploadBytes, 0);
        Interlocked.Exchange(ref downloadBytes, 0);
    }

    public TrafficCounters? ReadTotals() => new(Interlocked.Read(ref uploadBytes), Interlocked.Read(ref downloadBytes));

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener source, IPEndPoint backend, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await source.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                log.Warn(Source, "Accept failed: " + ex.SocketErrorCode, ex);
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var relay = new Relay(this, client, backend, token);
            relays[relay] = 0;
            _ = relay.RunAsync();
        }
    }

    private sealed class Relay
    {
        private readonly BridgeTrafficMonitor owner;
        private readonly Socket client;
        private readonly IPEndPoint backend;
        private readonly CancellationTokenSource cts;

        public Relay(BridgeTrafficMonitor owner, Socket client, IPEndPoint backend, CancellationToken parent)
        {
            this.owner = owner;
            this.client = client;
            this.backend = backend;
            cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public void Abort()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already finished
            }
        }

        public async Task RunAsync()
        {
            Socket? upstream = null;
            try
            {
                client.NoDelay = true;
                upstream = new Socket(backend.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    connectCts.CancelAfter(BackendConnectTimeout);
                    await upstream.ConnectAsync(backend, connectCts.Token).ConfigureAwait(false);
                }

                var up = PumpAsync(client, upstream, upload: true);
                var down = PumpAsync(upstream, client, upload: false);
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
            {
                owner.log.Debug(Source, "Relay ended: " + ex.GetType().Name);
            }
            finally
            {
                upstream?.Dispose();
                client.Dispose();
                cts.Dispose();
                owner.relays.TryRemove(this, out _);
            }
        }

        /// <summary>Copies one direction; EOF becomes a half-close so protocols that finish sending first still work.</summary>
        private async Task PumpAsync(Socket from, Socket to, bool upload)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var read = await from.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var sent = 0;
                    while (sent < read)
                    {
                        sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, cts.Token).ConfigureAwait(false);
                    }

                    owner.Add(upload, read);
                }

                try
                {
                    to.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                    // peer already gone
                }

                if (!upload)
                {
                    // The remote side is done; the client gets a bounded time to close its side too.
                    cts.CancelAfter(owner.halfCloseGrace);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // One side broke: tear the whole connection down so the other pump does not linger.
                Abort();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void Add(bool upload, int bytes) =>
        Interlocked.Add(ref upload ? ref uploadBytes : ref downloadBytes, bytes);
}
