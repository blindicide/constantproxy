using System.Net;
using System.Net.Sockets;

namespace ConstantProxy.Infrastructure.Network;

/// <summary>Checks whether an endpoint can be bound right now by briefly binding it (SPEC §12).</summary>
public sealed class TcpPortProbe : IPortProbe
{
    public PortStatus Check(IPAddress address, int port)
    {
        if (port is < 1 or > 65535)
        {
            return PortStatus.Unavailable;
        }

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Without this, Windows lets a second socket share the port and the conflict would go unnoticed.
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(new IPEndPoint(address, port));
            return PortStatus.Free;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return PortStatus.InUse;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied)
        {
            // Windows reports a port reserved by the system (or held exclusively by someone else) this way.
            return PortStatus.AccessDenied;
        }
        catch (SocketException)
        {
            return PortStatus.Unavailable;
        }
    }
}
