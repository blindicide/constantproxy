namespace ConstantProxy.Core.Models;

/// <summary>One forward step of the configuration schema. Steps must be consecutive: 0 to 1, 1 to 2, and so on.</summary>
public sealed record ConfigMigration(int From, int To, string Description, Action<AppConfig> Apply);

public sealed record ConfigMigrationOutcome(int FromVersion, int ToVersion, IReadOnlyList<string> Applied);

/// <summary>
/// Brings configuration files written by older versions up to the current schema (SPEC §50, §71). A missing
/// <c>schemaVersion</c> means the file predates versioning and counts as version 0. Files from a newer version are
/// never "migrated"; the caller backs them up and loads what it understands.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>The shipped steps. Version 1 introduced explicit schema versioning; its fields were already compatible.</summary>
    public static IReadOnlyList<ConfigMigration> Steps { get; } = new[]
    {
        new ConfigMigration(0, 1, "Introduce schema versioning", config => config.Normalize()),
    };

    public static ConfigMigrationOutcome Migrate(AppConfig config, int fromVersion, IReadOnlyList<ConfigMigration>? steps = null)
    {
        steps ??= Steps;
        var target = steps.Count == 0 ? fromVersion : steps.Max(s => s.To);
        var applied = new List<string>();
        var version = fromVersion;

        while (version < target)
        {
            var step = steps.FirstOrDefault(s => s.From == version)
                       ?? throw new InvalidOperationException($"No configuration migration from version {version}.");
            step.Apply(config);
            applied.Add(step.Description);
            version = step.To;
        }

        config.SchemaVersion = Math.Max(version, fromVersion);
        return new ConfigMigrationOutcome(fromVersion, version, applied);
    }
}
