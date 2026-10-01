using System.Reflection;

namespace ConstantProxy.Core;

/// <summary>
/// Application identity. The version comes from build metadata (Directory.Build.props), never from a
/// hand-maintained constant, so About, diagnostics and the executable always agree.
/// </summary>
public static class VersionInfo
{
    public const string ProductName = "constantproxy";
    public const string License = "MIT";
    public const string Description = "A lightweight Windows supervisor and analytics interface for SSH SOCKS proxies.";

    public static string Version { get; } = ReadVersion(typeof(VersionInfo).Assembly);

    public static string RepositoryUrl { get; } = ReadMetadata(typeof(VersionInfo).Assembly, "RepositoryUrl") ?? string.Empty;

    public static string ReadVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string? ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
