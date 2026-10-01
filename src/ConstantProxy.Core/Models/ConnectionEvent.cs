namespace ConstantProxy.Core.Models;

/// <summary>Structured connection event types (SPEC §78).</summary>
public enum ConnectionEventType
{
    ApplicationStarted,
    ConnectionRequested,
    SshStarted,
    SocksReady,
    Connected,
    HealthCheckFailed,
    Degraded,
    Recovered,
    SshExited,
    ConnectionLost,
    StartupFailed,
    ConnectionFailed,
    ReconnectScheduled,
    ReconnectAttempt,
    ReconnectSucceeded,
    ManualDisconnect,
    ApplicationExit,
    SessionInterrupted,
}

public sealed record ConnectionEvent(DateTimeOffset TimeUtc, ConnectionEventType Type, string? Detail = null);
