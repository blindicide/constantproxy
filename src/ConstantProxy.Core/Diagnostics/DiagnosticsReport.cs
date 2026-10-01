using System.Text;

namespace ConstantProxy.Core.Diagnostics;

/// <summary>Everything the diagnostics view shows (SPEC §65). Gathered by the application; formatted here.</summary>
public sealed record DiagnosticsInfo
{
    public string AppVersion { get; init; } = VersionInfo.Version;

    public string OsVersion { get; init; } = string.Empty;

    public string RuntimeVersion { get; init; } = string.Empty;

    public string ConfiguredSsh { get; init; } = string.Empty;

    public string? ResolvedSshPath { get; init; }

    public string? SshVersion { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public string SshTarget { get; init; } = string.Empty;

    public int? SshPid { get; init; }

    public ConnectionState State { get; init; }

    public string SocksEndpoint { get; init; } = string.Empty;

    public DateTimeOffset? SessionStartUtc { get; init; }

    public int ReconnectCount { get; init; }

    public string TrafficMode { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;

    public string LogPath { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string? LastFailure { get; init; }

    public string SshCommand { get; init; } = string.Empty;
}

/// <summary>
/// Plain-text diagnostics for pasting into a bug report. The text is always English so it can be read by anyone helping
/// with the problem, and private values are left out unless explicitly requested (SPEC §65).
/// </summary>
public static class DiagnosticsReport
{
    public const string RedactedPlaceholder = "(hidden)";

    public static string Format(DiagnosticsInfo info, bool includePrivateDetails)
    {
        string Hide(string value) => includePrivateDetails || string.IsNullOrEmpty(value) ? value : RedactedPlaceholder;

        var sb = new StringBuilder();
        void Line(string label, string? value) => sb.Append(label).Append(": ").AppendLine(string.IsNullOrWhiteSpace(value) ? "-" : value);

        sb.AppendLine("constantproxy diagnostics");
        sb.AppendLine("=========================");
        Line("Application version", info.AppVersion);
        Line("OS version", info.OsVersion);
        Line(".NET version", info.RuntimeVersion);
        Line("Language", info.Language);
        sb.AppendLine();
        Line("Configured ssh", string.IsNullOrWhiteSpace(info.ConfiguredSsh) ? "(automatic detection)" : Hide(info.ConfiguredSsh));
        Line("Resolved ssh path", info.ResolvedSshPath is null ? "not found" : Hide(info.ResolvedSshPath));
        Line("SSH version", info.SshVersion ?? "unknown");
        sb.AppendLine();
        Line("Current profile", Hide(info.ProfileName));
        Line("SSH target", Hide(info.SshTarget));
        Line("Connection state", info.State.ToString());
        Line("Current SSH PID", info.SshPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        Line("SOCKS endpoint", info.SocksEndpoint);
        Line("Traffic measurement", info.TrafficMode);
        Line("Session start (UTC)", info.SessionStartUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        Line("Reconnect count", info.ReconnectCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Line("Last failure", info.LastFailure);
        sb.AppendLine();
        Line("Config file", Hide(info.ConfigPath));
        Line("Database", Hide(info.DatabasePath));
        Line("Log folder", Hide(info.LogPath));
        sb.AppendLine();
        Line("SSH command", info.SshCommand);
        if (!includePrivateDetails)
        {
            sb.AppendLine();
            sb.AppendLine("Private details (SSH target, profile name and local paths) are hidden.");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
