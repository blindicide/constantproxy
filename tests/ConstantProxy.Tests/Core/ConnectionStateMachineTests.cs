namespace ConstantProxy.Tests.Core;

public class ConnectionStateMachineTests
{
    private static readonly HashSet<(ConnectionState, ConnectionState)> Expected = new()
    {
        (ConnectionState.Disconnected, ConnectionState.Starting),
        (ConnectionState.Starting, ConnectionState.Connecting),
        (ConnectionState.Starting, ConnectionState.Reconnecting),
        (ConnectionState.Starting, ConnectionState.Failed),
        (ConnectionState.Starting, ConnectionState.Stopping),
        (ConnectionState.Connecting, ConnectionState.Connected),
        (ConnectionState.Connecting, ConnectionState.Reconnecting),
        (ConnectionState.Connecting, ConnectionState.Failed),
        (ConnectionState.Connecting, ConnectionState.Stopping),
        (ConnectionState.Connected, ConnectionState.Degraded),
        (ConnectionState.Connected, ConnectionState.Reconnecting),
        (ConnectionState.Connected, ConnectionState.Failed),
        (ConnectionState.Connected, ConnectionState.Stopping),
        (ConnectionState.Degraded, ConnectionState.Connected),
        (ConnectionState.Degraded, ConnectionState.Reconnecting),
        (ConnectionState.Degraded, ConnectionState.Failed),
        (ConnectionState.Degraded, ConnectionState.Stopping),
        (ConnectionState.Reconnecting, ConnectionState.Starting),
        (ConnectionState.Reconnecting, ConnectionState.Failed),
        (ConnectionState.Reconnecting, ConnectionState.Stopping),
        (ConnectionState.Stopping, ConnectionState.Disconnected),
        (ConnectionState.Failed, ConnectionState.Starting),
        (ConnectionState.Failed, ConnectionState.Disconnected),
    };

    public static IEnumerable<object[]> AllPairs() =>
        from source in Enum.GetValues<ConnectionState>()
        from target in Enum.GetValues<ConnectionState>()
        select new object[] { source, target };

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void TransitionTableMatchesSpecification(ConnectionState from, ConnectionState to)
    {
        Assert.Equal(Expected.Contains((from, to)), ConnectionStateMachine.IsAllowed(from, to));
    }

    [Fact]
    public void StartsDisconnected() => Assert.Equal(ConnectionState.Disconnected, new ConnectionStateMachine().Current);

    [Fact]
    public void FollowsTheSpecifiedHappyPathAndReconnectLoop()
    {
        var machine = new ConnectionStateMachine();
        foreach (var next in new[]
                 {
                     ConnectionState.Starting, ConnectionState.Connecting, ConnectionState.Connected,
                     ConnectionState.Degraded, ConnectionState.Reconnecting, ConnectionState.Starting,
                     ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Stopping,
                     ConnectionState.Disconnected,
                 })
        {
            machine.Transition(next);
            Assert.Equal(next, machine.Current);
        }
    }

    [Fact]
    public void RejectedTransitionLeavesStateUnchangedAndThrowsFromTransition()
    {
        var machine = new ConnectionStateMachine();
        Assert.False(machine.TryTransition(ConnectionState.Connected));
        var ex = Assert.Throws<InvalidStateTransitionException>(() => machine.Transition(ConnectionState.Connected));
        Assert.Equal(ConnectionState.Disconnected, ex.From);
        Assert.Equal(ConnectionState.Connected, ex.To);
        Assert.Equal(ConnectionState.Disconnected, machine.Current);
    }

    [Fact]
    public void RaisesChangedWithFailureInfoOnlyForSuccessfulTransitions()
    {
        var machine = new ConnectionStateMachine();
        var changes = new List<StateChange>();
        machine.Changed += changes.Add;
        var failure = new FailureInfo(FailureCategory.LocalConfiguration, "x", "m", false);

        machine.TryTransition(ConnectionState.Connected); // rejected
        machine.Transition(ConnectionState.Starting);
        machine.Transition(ConnectionState.Failed, failure);

        Assert.Equal(2, changes.Count);
        Assert.Equal((ConnectionState.Starting, ConnectionState.Failed), (changes[1].Old, changes[1].New));
        Assert.Same(failure, changes[1].Failure);
    }

    [Fact]
    public void StoppingCanOnlyLeadToDisconnected()
    {
        Assert.Equal(new[] { ConnectionState.Disconnected }, ConnectionStateMachine.NextStates(ConnectionState.Stopping));
    }
}
