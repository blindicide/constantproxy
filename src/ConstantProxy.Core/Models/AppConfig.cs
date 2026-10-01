namespace ConstantProxy.Core.Models;

public enum LogVerbosity
{
    Normal,
    Verbose,
}

/// <summary>Root of the user configuration file (SPEC §22). Kept separate from the analytics database.</summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid ActiveProfileId { get; set; }

    public List<Profile> Profiles { get; set; } = new();

    public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

    /// <summary>The active profile, or the first one if the stored id no longer exists.</summary>
    public Profile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    /// <summary>
    /// Repairs structurally missing pieces (null lists, no profiles, dangling active id) without altering
    /// values the user chose. Semantic problems are reported by <see cref="ProfileValidator"/> instead.
    /// </summary>
    public AppConfig Normalize()
    {
        Profiles ??= new List<Profile>();
        Profiles.RemoveAll(p => p is null);
        foreach (var p in Profiles)
        {
            p.Reconnect ??= new ReconnectSettings();
            p.Reconnect.DelaysSeconds ??= new List<int>();
            p.AdditionalArguments ??= new List<string>();
            p.Name ??= string.Empty;
            p.Host ??= string.Empty;
            p.BindAddress ??= string.Empty;
            p.SshExecutable ??= string.Empty;
            if (p.Id == Guid.Empty)
            {
                p.Id = Guid.NewGuid();
            }
        }

        if (Profiles.Count == 0)
        {
            Profiles.Add(new Profile());
        }

        if (!Profiles.Any(p => p.Id == ActiveProfileId))
        {
            ActiveProfileId = Profiles[0].Id;
        }

        return this;
    }

    public static AppConfig CreateDefault() => new AppConfig().Normalize();
}
