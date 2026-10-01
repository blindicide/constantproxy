namespace ConstantProxy.Core.Connection;

public enum OutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record SshOutputLine(OutputStream Stream, string Text);

/// <summary>Everything needed to launch ssh: an executable and a discrete argument list (no shell involved).</summary>
public sealed record SshLaunchSpec(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Why ssh could not even be started. Mapped to a classified failure by the connection manager.</summary>
public enum LaunchFailureKind
{
    NotFound,
    InvalidExecutable,
    AccessDenied,
    Other,
}

public sealed class SshLaunchException : Exception
{
    public SshLaunchException(LaunchFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public LaunchFailureKind Kind { get; }
}

/// <summary>A running ssh child process, tracked by PID. Real process state is the source of truth (SPEC §9).</summary>
public interface ISshProcess : IDisposable
{
    int Pid { get; }

    DateTimeOffset StartTimeUtc { get; }

    bool HasExited { get; }

    /// <summary>Completes with the exit code once the process has ended and its output has been drained.</summary>
    Task<int> Exited { get; }

    event Action<SshOutputLine>? OutputReceived;

    /// <summary>Politely asks the process to stop. Returns false if no polite mechanism applied (caller should Kill).</summary>
    bool RequestStop();

    /// <summary>Forcibly terminates this process (and its children) only.</summary>
    void Kill();
}

public interface ISshProcessLauncher
{
    /// <exception cref="SshLaunchException">The process could not be started.</exception>
    ISshProcess Start(SshLaunchSpec spec);
}
