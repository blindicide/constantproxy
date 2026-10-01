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
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, string? BackupPath = null, string? Error = null);

/// <summary>Loads and atomically saves the JSON configuration (SPEC §22). A bad file must never crash the app.</summary>
public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options)
                         ?? throw new JsonException("The configuration file is empty.");
            return new ConfigLoadResult(config.Normalize(), ConfigLoadStatus.Loaded);
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
