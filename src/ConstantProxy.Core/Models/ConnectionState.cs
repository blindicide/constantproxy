namespace ConstantProxy.Core.Models;

/// <summary>Explicit tunnel lifecycle states (SPEC §8).</summary>
public enum ConnectionState
{
    Disconnected,
    Starting,
    Connecting,
    Connected,
    Degraded,
    Reconnecting,
    Stopping,
    Failed,
}
