namespace ConstantProxy.Core.Connection;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(ConnectionState from, ConnectionState to)
        : base($"Transition {from} -> {to} is not allowed.")
    {
        From = from;
        To = to;
    }

    public ConnectionState From { get; }

    public ConnectionState To { get; }
}

public sealed record StateChange(ConnectionState Old, ConnectionState New, DateTimeOffset AtUtc, FailureInfo? Failure);

/// <summary>
/// The single authority for connection state (SPEC §8, §64). Only the transitions in <see cref="Allowed"/>
/// can happen; everything else is rejected so scattered flags cannot drift out of sync.
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly IReadOnlyDictionary<ConnectionState, ConnectionState[]> Allowed =
        new Dictionary<ConnectionState, ConnectionState[]>
        {
            [ConnectionState.Disconnected] = new[] { ConnectionState.Starting },
            [ConnectionState.Starting] = new[] { ConnectionState.Connecting, ConnectionState.Reconnecting, ConnectionState.Failed, ConnectionState.Stopping },
            [ConnectionState.Connecting] = new[] { ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Failed, ConnectionState.Stopping },
            [ConnectionState.Connected] = new[] { ConnectionState.Degraded, ConnectionState.Reconnecting, ConnectionState.Failed, ConnectionState.Stopping },
            [ConnectionState.Degraded] = new[] { ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Failed, ConnectionState.Stopping },
            [ConnectionState.Reconnecting] = new[] { ConnectionState.Starting, ConnectionState.Failed, ConnectionState.Stopping },
            [ConnectionState.Stopping] = new[] { ConnectionState.Disconnected },
            [ConnectionState.Failed] = new[] { ConnectionState.Starting, ConnectionState.Disconnected },
        };

    private readonly object gate = new();
    private readonly IClock clock;
    private ConnectionState current = ConnectionState.Disconnected;

    public ConnectionStateMachine(IClock? clock = null)
    {
        this.clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Raised after a successful transition, outside the internal lock.</summary>
    public event Action<StateChange>? Changed;

    public ConnectionState Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public static bool IsAllowed(ConnectionState from, ConnectionState to) => Allowed[from].Contains(to);

    public static IReadOnlyCollection<ConnectionState> NextStates(ConnectionState from) => Allowed[from];

    public bool TryTransition(ConnectionState to, FailureInfo? failure = null)
    {
        StateChange change;
        lock (gate)
        {
            if (!IsAllowed(current, to))
            {
                return false;
            }

            change = new StateChange(current, to, clock.UtcNow, failure);
            current = to;
        }

        Changed?.Invoke(change);
        return true;
    }

    public void Transition(ConnectionState to, FailureInfo? failure = null)
    {
        ConnectionState from;
        lock (gate)
        {
            from = current;
        }

        if (!TryTransition(to, failure))
        {
            throw new InvalidStateTransitionException(from, to);
        }
    }
}
