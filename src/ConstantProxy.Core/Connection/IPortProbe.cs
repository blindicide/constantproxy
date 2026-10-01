using System.Net;

namespace ConstantProxy.Core.Connection;

public enum PortStatus
{
    Free,
    InUse,
    AccessDenied,
    Unavailable,
}

/// <summary>Checks whether a local endpoint can be bound before ssh is started (SPEC §12).</summary>
public interface IPortProbe
{
    PortStatus Check(IPAddress address, int port);
}
