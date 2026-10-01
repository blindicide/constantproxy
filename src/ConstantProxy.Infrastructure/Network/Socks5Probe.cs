using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ConstantProxy.Infrastructure.Network;

/// <summary>
/// Opens a SOCKS5 CONNECT through the local proxy and measures the round trip (SPEC §14). Only the handshake and
/// the proxy's reply are exchanged; no application data is sent to the destination.
/// </summary>
public sealed class Socks5Probe : ISocksProbe
{
    public async Task<SocksProbeResult> ProbeAsync(string proxyAddress, int proxyPort, string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(proxyAddress, proxyPort, token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                return SocksProbeResult.Fail(ProbeFailure.ProxyUnreachable, ex.SocketErrorCode.ToString());
            }

            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token).ConfigureAwait(false);
            var method = await ReadExactlyAsync(stream, 2, token).ConfigureAwait(false);
            if (method is null || method[0] != 0x05 || method[1] != 0x00)
            {
                return SocksProbeResult.Fail(ProbeFailure.ProtocolError, "unexpected method selection");
            }

            await stream.WriteAsync(BuildConnectRequest(targetHost, targetPort), token).ConfigureAwait(false);
            var reply = await ReadExactlyAsync(stream, 4, token).ConfigureAwait(false);
            if (reply is null || reply[0] != 0x05)
            {
                return SocksProbeResult.Fail(ProbeFailure.ProtocolError, "unexpected reply");
            }

            stopwatch.Stop();
            return reply[1] == 0x00
                ? SocksProbeResult.Ok(stopwatch.Elapsed)
                : SocksProbeResult.Fail(ProbeFailure.Rejected, $"SOCKS reply {reply[1]}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SocksProbeResult.Fail(ProbeFailure.Timeout, "timeout");
        }
        catch (IOException ex)
        {
            return SocksProbeResult.Fail(ProbeFailure.ProtocolError, ex.Message);
        }
    }

    internal static byte[] BuildConnectRequest(string host, int port)
    {
        var request = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length is 0 or > 255)
            {
                throw new ArgumentException("Host name must be 1-255 bytes.", nameof(host));
            }

            request.Add(0x03);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }

        request.Add((byte)(port >> 8));
        request.Add((byte)(port & 0xFF));
        return request.ToArray();
    }

    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return buffer;
    }
}
