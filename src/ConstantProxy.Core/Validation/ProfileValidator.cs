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

        ValidateReconnect(profile.Reconnect, result);
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
}
