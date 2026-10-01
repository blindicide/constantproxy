namespace ConstantProxy.Core.Connection;

public enum StartupOutcome
{
    Ready,
    ProcessExited,
    TimedOut,
}

/// <param name="ListenPort">The local port ssh was told to listen on, when it differs from <see cref="Profile.Port"/>.</param>
public sealed record StartupContext(Profile Profile, ISshProcess Process, TimeSpan Timeout, int? ListenPort = null);

/// <summary>Decides when a freshly started ssh process may be declared Connected (SPEC §13).</summary>
public interface IStartupVerifier
{
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal verifier used until SOCKS listener polling exists: the process must survive a short settle period.
/// </summary>
public sealed class ProcessAliveStartupVerifier : IStartupVerifier
{
    private readonly IClock clock;
    private readonly TimeSpan settle;

    public ProcessAliveStartupVerifier(IClock clock, TimeSpan settle)
    {
        this.clock = clock;
        this.settle = settle;
    }

    public async Task<StartupOutcome> WaitUntilReadyAsync(StartupContext context, CancellationToken cancellationToken)
    {
        var delay = clock.Delay(settle, cancellationToken);
        var finished = await Task.WhenAny(context.Process.Exited, delay).ConfigureAwait(false);
        if (finished == context.Process.Exited)
        {
            return StartupOutcome.ProcessExited;
        }

        await delay.ConfigureAwait(false);
        return StartupOutcome.Ready;
    }
}
