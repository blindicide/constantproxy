namespace ConstantProxy.Infrastructure.Ssh;

/// <summary>Finds the OpenSSH client (SPEC §84). Never downloads or installs anything.</summary>
public sealed class SshLocator
{
    private readonly Func<string, bool> fileExists;
    private readonly string? pathVariable;
    private readonly bool isWindows;
    private readonly string? systemRoot;

    public SshLocator(Func<string, bool>? fileExists = null, string? pathVariable = null, bool? isWindows = null, string? systemRoot = null)
    {
        this.fileExists = fileExists ?? File.Exists;
        this.pathVariable = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        this.isWindows = isWindows ?? OperatingSystem.IsWindows();
        this.systemRoot = systemRoot ?? Environment.GetEnvironmentVariable("SystemRoot");
    }

    /// <summary>Returns a launchable path for <paramref name="configured"/> (empty = automatic), or null if not found.</summary>
    public string? Resolve(string? configured)
    {
        var name = configured?.Trim() ?? string.Empty;
        if (name.Length > 0 && (name.Contains('/') || name.Contains('\\')))
        {
            return fileExists(name) ? name : null;
        }

        var candidates = name.Length == 0
            ? new[] { isWindows ? "ssh.exe" : "ssh" }
            : isWindows && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { name, name + ".exe" } : new[] { name };

        var separator = isWindows ? ';' : ':';
        foreach (var directory in (pathVariable ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Combine(directory.Trim('"'), candidate);
                if (fileExists(full))
                {
                    return full;
                }
            }
        }

        if (name.Length == 0 && isWindows && !string.IsNullOrEmpty(systemRoot))
        {
            var builtIn = $"{systemRoot.TrimEnd('\\')}\\System32\\OpenSSH\\ssh.exe";
            if (fileExists(builtIn))
            {
                return builtIn;
            }
        }

        return null;
    }

    private string Combine(string directory, string file) =>
        isWindows ? $"{directory.TrimEnd('\\')}\\{file}" : $"{directory.TrimEnd('/')}/{file}";
}
