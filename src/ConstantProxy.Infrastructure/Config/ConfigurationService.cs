using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConstantProxy.Infrastructure.Config;

public enum ConfigLoadStatus
{
    /// <summary>The file was read successfully.</summary>
    Loaded,

    /// <summary>No file existed; defaults were produced (first run).</summary>
    Created,

    /// <summary>The file was unreadable; it was set aside and defaults were produced.</summary>
    RecoveredFromCorruption,

    /// <summary>The file came from an older version and was upgraded (a backup of the old file was kept).</summary>
    Migrated,

    /// <summary>The file came from a newer version; what could be understood was loaded and a backup was kept.</summary>
    LoadedFromNewerVersion,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, string? BackupPath = null, string? Error = null, int FileVersion = AppConfig.CurrentSchemaVersion);

/// <summary>Loads and atomically saves the JSON configuration (SPEC §22). A bad file must never crash the app.</summary>
public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, // hand-edited files may use any capitalisation
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string path;
    private readonly IAppLog log;
    private readonly Func<DateTimeOffset> now;

    public ConfigurationService(string path, IAppLog? log = null, Func<DateTimeOffset>? now = null)
    {
        this.path = path;
        this.log = log ?? NullAppLog.Instance;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ConfigLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new ConfigLoadResult(AppConfig.CreateDefault(), ConfigLoadStatus.Created);
        }

        try
        {
            var json = File.ReadAllText(path);
            var fileVersion = ReadSchemaVersion(json);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options)
                         ?? throw new JsonException("The configuration file is empty.");
            config.Normalize();

            if (fileVersion > AppConfig.CurrentSchemaVersion)
            {
                // Saving later would drop settings this version does not know about, so keep the original first.
                var backup = TryCopy($"{path}.v{fileVersion}.bak");
                config.SchemaVersion = AppConfig.CurrentSchemaVersion;
                return new ConfigLoadResult(config, ConfigLoadStatus.LoadedFromNewerVersion, backup, FileVersion: fileVersion);
            }

            if (fileVersion < AppConfig.CurrentSchemaVersion)
            {
                var backup = TryCopy($"{path}.v{fileVersion}.bak");
                var outcome = ConfigMigrator.Migrate(config, fileVersion);
                log.Info("config", $"Configuration upgraded from version {outcome.FromVersion} to {outcome.ToVersion}: {string.Join("; ", outcome.Applied)}");
                Save(config);
                return new ConfigLoadResult(config, ConfigLoadStatus.Migrated, backup, FileVersion: fileVersion);
            }

            return new ConfigLoadResult(config, ConfigLoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Error("config", "Configuration could not be read; using defaults.", ex);
            var backup = TryBackup();
            return new ConfigLoadResult(AppConfig.CreateDefault(), ConfigLoadStatus.RecoveredFromCorruption, backup, ex.Message);
        }
    }

    /// <summary>Writes via a temp file and rename so a crash cannot leave a half-written configuration.</summary>
    public void Save(AppConfig config)
    {
        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Version 0 when the file has no (or an unreadable) <c>schemaVersion</c>, i.e. it predates versioning.</summary>
    internal static int ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.Number } property
            && property.Value.TryGetInt32(out var version)
            && version >= 0)
        {
            return version;
        }

        return 0;
    }

    private string? TryCopy(string destination)
    {
        try
        {
            File.Copy(path, destination, overwrite: true);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("config", "Could not keep a backup of the configuration before upgrading it.", ex);
            return null;
        }
    }

    private string? TryBackup()
    {
        try
        {
            var backup = $"{path}.corrupt-{now():yyyyMMddTHHmmssZ}";
            File.Move(path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("config", "Could not set the unreadable configuration file aside.", ex);
            return null;
        }
    }
}
