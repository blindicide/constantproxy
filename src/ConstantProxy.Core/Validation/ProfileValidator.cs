using System.Net;

namespace ConstantProxy.Core.Validation;

/// <summary>Validates a profile before it is saved or used to connect (SPEC §73, §74).</summary>
public static class ProfileValidator
{
    public static ValidationResult Validate(Profile profile, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            result.Error(nameof(Profile.Name), "profile.name.empty", "Profile name must not be empty.");
        }

        ValidateHost(profile, result);
        ValidateBindAddress(profile, result);

        if (profile.Port is < 1 or > 65535)
        {
            result.Error(nameof(Profile.Port), "port.range", "Port must be between 1 and 65535.");
        }

        ValidateExecutable(profile, fileExists, result);

        if (profile.ServerAliveInterval < 1)
        {
            result.Error(nameof(Profile.ServerAliveInterval), "timing.positive", "ServerAliveInterval must be a positive number of seconds.");
        }

        if (profile.ServerAliveCountMax < 1)
        {
            result.Error(nameof(Profile.ServerAliveCountMax), "timing.positive", "ServerAliveCountMax must be a positive number.");
        }

        if (profile.StartupTimeoutSeconds < 1)
        {
            result.Error(nameof(Profile.StartupTimeoutSeconds), "timing.positive", "Startup timeout must be a positive number of seconds.");
        }

        ValidateReconnect(profile.Reconnect, result);
        ValidateMonitoring(profile.Monitoring, result);
        return result;
    }

    private static void ValidateHost(Profile profile, ValidationResult result)
    {
        var host = profile.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            result.Error(nameof(Profile.Host), "host.empty", "SSH target must not be empty.");
        }
        else if (host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            result.Error(nameof(Profile.Host), "host.invalid", "SSH target must not contain whitespace or control characters.");
        }
        else if (host.StartsWith('-'))
        {
            // Would be parsed by ssh as an option rather than a destination.
            result.Error(nameof(Profile.Host), "host.invalid", "SSH target must not start with '-'.");
        }
    }

    private static void ValidateBindAddress(Profile profile, ValidationResult result)
    {
        if (!IPAddress.TryParse(profile.BindAddress?.Trim().Trim('[', ']'), out var address))
        {
            result.Error(nameof(Profile.BindAddress), "bind.invalid", "Bind address must be a valid IP address.");
        }
        else if (!IPAddress.IsLoopback(address))
        {
            // SPEC §74: allowed, but the user must be warned and never silently overridden.
            result.Warning(nameof(Profile.BindAddress), "bind.nonloopback",
                "A non-loopback bind address may let other hosts reach the local SOCKS endpoint, depending on firewall and network configuration.");
        }
    }

    private static void ValidateExecutable(Profile profile, Func<string, bool> fileExists, ValidationResult result)
    {
        var exe = profile.SshExecutable?.Trim() ?? string.Empty;
        if (exe.Length == 0)
        {
            return; // automatic detection
        }

        var isExplicitPath = exe.Contains('/') || exe.Contains('\\');
        if (isExplicitPath && !fileExists(exe))
        {
            result.Error(nameof(Profile.SshExecutable), "ssh.notfound", "The configured SSH executable does not exist.");
        }
    }

    private static void ValidateReconnect(ReconnectSettings r, ValidationResult result)
    {
        const string field = nameof(Profile.Reconnect);
        if (r.DelaysSeconds.Count == 0 || r.DelaysSeconds.Any(d => d < 0))
        {
            result.Error(field, "reconnect.delays", "Reconnect delays must be a non-empty list of non-negative seconds.");
        }
        else if (r.DelaysSeconds[^1] < 1)
        {
            result.Error(field, "reconnect.delays.last", "The final reconnect delay must be at least 1 second to prevent a respawn storm.");
        }

        if (r.MaxDelaySeconds < 1)
        {
            result.Error(field, "timing.positive", "Maximum reconnect delay must be positive.");
        }

        if (r.HealthyResetSeconds < 1)
        {
            result.Error(field, "timing.positive", "Healthy reset period must be positive.");
        }

        if (r.JitterPercent is < 0 or > 100)
        {
            result.Error(field, "reconnect.jitter", "Jitter must be between 0 and 100 percent.");
        }
    }

    private static void ValidateMonitoring(HealthCheckSettings m, ValidationResult result)
    {
        const string field = nameof(Profile.Monitoring);
        if (!m.Enabled)
        {
            return;
        }

        if (m.IntervalSeconds < 1 || m.TimeoutSeconds < 1)
        {
            result.Error(field, "timing.positive", "Health-check interval and timeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(m.TargetHost) || m.TargetHost.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || m.TargetHost.Length > 255)
        {
            result.Error(field, "health.host", "Health-check target host must be a non-empty host name or IP address.");
        }

        if (m.TargetPort is < 1 or > 65535)
        {
            result.Error(field, "port.range", "Health-check port must be between 1 and 65535.");
        }

        if (m.FailureThreshold < 1)
        {
            result.Error(field, "health.threshold", "Failure threshold must be at least 1.");
        }

        if (m.ReconnectOnFailure && m.ReconnectAfterFailures < m.FailureThreshold)
        {
            result.Error(field, "health.reconnectafter", "Reconnect threshold must not be lower than the failure threshold.");
        }
    }

    /// <summary>Validates application-wide settings that are not part of a profile (SPEC §73).</summary>
    public static ValidationResult ValidateAnalytics(AnalyticsConfig analytics, Func<string, bool>? directoryExists = null)
    {
        var result = new ValidationResult();
        if (analytics.RetentionDays < 0)
        {
            result.Error(nameof(AppConfig.Analytics), "retention.invalid", "Retention must be zero (forever) or a positive number of days.");
        }

        if (analytics.RetentionDays > 36500)
        {
            result.Error(nameof(AppConfig.Analytics), "retention.invalid", "Retention is unreasonably long.");
        }

        var path = analytics.DatabasePath?.Trim() ?? string.Empty;
        if (path.Length > 0)
        {
            var directory = Path.GetDirectoryName(path);
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                result.Error(nameof(AppConfig.Analytics), "database.path", "The database path contains invalid characters.");
            }
            else if (!string.IsNullOrEmpty(directory) && !(directoryExists ?? Directory.Exists)(directory))
            {
                result.Error(nameof(AppConfig.Analytics), "database.path", "The database folder does not exist.");
            }
        }

        return result;
    }
}
