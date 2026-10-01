using System.Net;
using System.Net.Sockets;

namespace ConstantProxy.Infrastructure.Network;

/// <summary>
/// Declares the tunnel ready only once the SOCKS listener really accepts connections (SPEC §13),
/// not merely because ssh.exe exists. Polls, never spins, and gives up after the profile's startup timeout.
/// </summary>
public sealed class ListenerStartupVerifier : IStartupVerifier
{
    private readonly IClock clock;
    private readonly TimeSpan pollInterval;
    private readonly Func<string, int, CancellationToken, Task<bool>> tryConnect;

    public ListenerStartupVerifier(IClock clock, TimeSpan? pollInterval = null, Func<string, int, CancellationToken, Task<bool>>? tryConnect = null)
    {
        this.clock = clock;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        this.tryConnect = tryConnect ?? TryConnectAsync;
    }

    public async Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
    {
        var address = ConnectAddress(context.ListenAddress ?? context.Profile.BindAddress);
        var port = ConnectPort(context);
        var deadline = clock.UtcNow + context.Timeout;

        while (true)
        {
            if (context.Process.HasExited)
            {
                return StartupOutcome.ProcessExited;
            }

            if (await tryConnect(address, port, cancellationToken).ConfigureAwait(false))
            {
                return StartupOutcome.Ready;
            }

            if (clock.UtcNow >= deadline)
            {
                return StartupOutcome.TimedOut;
            }

            var wait = clock.Delay(pollInterval, cancellationToken);
            var finished = await Task.WhenAny(wait, context.Process.Exited).ConfigureAwait(false);
            if (finished != wait)
            {
                return StartupOutcome.ProcessExited;
            }

            await wait.ConfigureAwait(false);
        }
    }

    /// <summary>Port ssh actually listens on; differs from the profile port when a traffic bridge sits in front.</summary>
    private static int ConnectPort(StartupContext context) => context.ListenPort ?? context.Profile.Port;

    internal static string ConnectAddress(string bindAddress)
    {
        var text = bindAddress.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(text, out var ip))
        {
            return IPAddress.Loopback.ToString();
        }

        if (ip.Equals(IPAddress.Any))
        {
            return IPAddress.Loopback.ToString();
        }

        return ip.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback.ToString() : ip.ToString();
    }

    private static async Task<bool> TryConnectAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
