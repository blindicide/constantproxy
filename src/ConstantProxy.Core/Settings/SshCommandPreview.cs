using System.Text.RegularExpressions;

namespace ConstantProxy.Core.Settings;

/// <summary>
/// The read-only "Show generated SSH command" text (SPEC §75). Values that could carry secrets are redacted so the
/// command can be pasted into a bug report; the structure stays intact for troubleshooting.
/// </summary>
public static class SshCommandPreview
{
    public const string RedactedTarget = "<ssh-target>";
    public const string RedactedValue = "***";
    public const string BridgePortPlaceholder = "<internal-port>";

    private static readonly Regex SensitiveName = new(@"pass|secret|token|key|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <param name="redactTarget">Hides the SSH target, which may reveal a private server.</param>
    public static string Build(Profile profile, bool redactTarget, string? resolvedExecutable = null)
    {
        var listen = profile.TrafficMode == TrafficMode.Bridge
            ? new Traffic.SshListenOverride("127.0.0.1", 0)
            : null;
        var args = SshArgumentBuilder.Build(profile, listen).ToList();

        // The bridge picks a fresh port for every attempt, so show a placeholder instead of a fake number.
        var dynamic = args.IndexOf("-D");
        if (listen is not null && dynamic >= 0)
        {
            args[dynamic + 1] = $"127.0.0.1:{BridgePortPlaceholder}";
        }

        Redact(args, profile, redactTarget);
        return SshArgumentBuilder.FormatCommandLine(string.IsNullOrWhiteSpace(resolvedExecutable) ? ExecutableName(profile) : resolvedExecutable!, args);
    }

    private static string ExecutableName(Profile profile) =>
        string.IsNullOrWhiteSpace(profile.SshExecutable) ? "ssh" : profile.SshExecutable.Trim();

    private static void Redact(List<string> args, Profile profile, bool redactTarget)
    {
        var additionalStart = args.Count - 1 - profile.AdditionalArguments.Count;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Count - 1)
            {
                var option = args[i + 1];
                var eq = option.IndexOf('=');
                if (eq > 0 && SensitiveName.IsMatch(option[..eq]) && !option[..eq].Equals("ServerAliveInterval", StringComparison.OrdinalIgnoreCase))
                {
                    args[i + 1] = option[..eq] + "=" + RedactedValue;
                }
            }
        }

        for (var i = additionalStart; i >= 0 && i < args.Count - 1; i++)
        {
            // Free-form extra arguments such as "--password=..." are redacted when their name looks sensitive.
            var eq = args[i].IndexOf('=');
            if (args[i].StartsWith('-') && eq > 0 && SensitiveName.IsMatch(args[i][..eq]))
            {
                args[i] = args[i][..eq] + "=" + RedactedValue;
            }
        }

        if (redactTarget)
        {
            args[^1] = RedactedTarget;
        }
    }
}
