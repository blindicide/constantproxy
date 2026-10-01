using System.Text.RegularExpressions;

namespace ConstantProxy.Core.Connection;

/// <summary>Where in the lifecycle a failure was observed.</summary>
public enum FailureStage
{
    Launch,
    Startup,
    Running,
    HealthCheck,
}

public sealed record FailureContext(FailureStage Stage, int? ExitCode, string OutputText, bool ListenerWasReady, int Port);

/// <summary>
/// Maps what we observed (stage, exit code, listener state, stderr text) to a broad category (SPEC §77).
/// Text patterns are only one input; unknown is always an acceptable answer and certainty is never invented.
/// Authentication and host-key failures are treated as persistent: retrying cannot fix them and can trip server defences.
/// </summary>
public static class FailureClassifier
{
    private static readonly (Regex Pattern, FailureCategory Category, string Code, string Message, bool Retryable)[] Rules =
    {
        (Rx(@"remote host identification has changed|host key verification failed|host key for .* has changed|offending (rsa|ecdsa|ed25519) key"),
            FailureCategory.HostVerification, "ssh.hostkey", "The server's host key could not be verified. Check known_hosts before connecting again.", false),

        (Rx(@"permission denied \((publickey|password|keyboard|gssapi)|too many authentication failures|authentication failed|no supported authentication methods|permission denied, please try again"),
            FailureCategory.Authentication, "ssh.auth", "SSH authentication failed. Check your key, agent or account.", false),

        (Rx(@"address already in use|cannot listen to port|could not request local forwarding|bind(\s*\[[^\]]*\])?[^\n]*(permission denied|forbidden)|channel_setup_fwd_listener|forwarding listen"),
            FailureCategory.Forwarding, "forward.bind", "The local SOCKS port could not be opened. Another program may be using it.", false),

        (Rx(@"bad configuration option|unsupported option|unknown option|illegal option|invalid option|unknown cipher|bad port|usage: ssh|command-line line \d+|/\.ssh/config: line \d+|garbage at end of line"),
            FailureCategory.LocalConfiguration, "ssh.args", "OpenSSH rejected the configuration or arguments. Check the profile and your ssh config.", false),

        (Rx(@"could not resolve hostname|name or service not known|nodename nor servname|no such host is known|temporary failure in name resolution|getaddrinfo"),
            FailureCategory.Network, "network.dns", "The SSH host name could not be resolved. Check the SSH target and your network.", true),

        (Rx(@"network is unreachable|no route to host|host is unreachable"),
            FailureCategory.Network, "network.unreachable", "The network is unreachable.", true),

        (Rx(@"connection timed out|operation timed out|connect to host .* timed out|did not properly respond after a period of time"),
            FailureCategory.RemoteConnection, "remote.timeout", "The SSH server did not respond in time.", true),

        (Rx(@"connection refused|actively refused"),
            FailureCategory.RemoteConnection, "remote.refused", "The SSH server refused the connection.", true),

        (Rx(@"connection reset|broken pipe|connection closed by|kex_exchange_identification|connection to .* closed|timeout, server .* not responding|write failed"),
            FailureCategory.RemoteConnection, "remote.dropped", "The connection to the SSH server was lost.", true),
    };

    public static FailureInfo Classify(FailureContext context)
    {
        if (context.Stage == FailureStage.HealthCheck)
        {
            return new FailureInfo(FailureCategory.Network, "health.failed", "The SOCKS proxy stopped carrying traffic (health checks failing).", true);
        }

        var text = context.OutputText ?? string.Empty;
        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(text))
            {
                return new FailureInfo(rule.Category, rule.Code, rule.Message, rule.Retryable, Tail(text)) { ExitCode = context.ExitCode };
            }
        }

        // No recognisable text: fall back to what the process itself did.
        if (context.Stage == FailureStage.Startup && !context.ListenerWasReady && context.ExitCode is null)
        {
            return new FailureInfo(FailureCategory.Forwarding, "startup.timeout", "The SOCKS tunnel did not become available in time.", true, Tail(text)) { ExitCode = context.ExitCode };
        }

        return context.Stage switch
        {
            FailureStage.Startup => new FailureInfo(FailureCategory.Unknown, "ssh.exited.startup", "The SSH process ended before the tunnel was ready.", true, Tail(text)) { ExitCode = context.ExitCode },
            _ => new FailureInfo(FailureCategory.Unknown, "ssh.exited", "The SSH process exited unexpectedly.", true, Tail(text)) { ExitCode = context.ExitCode },
        };
    }

    public static FailureInfo PortInUse(int port, bool retryable) =>
        new(FailureCategory.LocalConfiguration, "port.inuse", $"Port {port} is already in use.", retryable) { Arguments = new[] { port.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    public static FailureInfo PortAccessDenied(int port) =>
        new(FailureCategory.LocalConfiguration, "port.denied", $"Access to port {port} was denied.", false) { Arguments = new[] { port.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[^2000..];
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
