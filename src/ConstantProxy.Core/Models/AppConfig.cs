namespace ConstantProxy.Core.Models;

public enum LogVerbosity
{
    Normal,
    Verbose,
}

/// <summary>Root of the user configuration file (SPEC §22). Kept separate from the analytics database.</summary>
/// <summary>Window and startup behaviour (SPEC §28 Interface, §31, §32).</summary>
public sealed class InterfaceConfig
{
    /// <summary><c>auto</c> (follow the Windows UI language), <c>en</c> or <c>ru</c> (SPEC §30).</summary>
    public string Language { get; set; } = "auto";

    public bool StartMinimized { get; set; }

    /// <summary>Closing the main window hides it to the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimizing the main window hides it to the notification area.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>Connects the active profile whenever the application starts. Independent of <see cref="StartWithWindows"/>.</summary>
    public bool ConnectOnLaunch { get; set; }
}

/// <summary>Notification preferences (SPEC §33).</summary>
public sealed class NotificationConfig
{
    public bool NotifyOnFailure { get; set; } = true;

    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>An outage must last at least this long before the user is told; keeps brief blips quiet.</summary>
    public int MinimumOutageSeconds { get; set; } = 10;
}

public sealed class AnalyticsConfig
{
    public bool StoreHistory { get; set; } = true;

    /// <summary>Days of history to keep; 0 keeps everything. Typical choices: 30, 90, 180, 365, 0.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Empty means the default location inside the per-user data directory.</summary>
    public string DatabasePath { get; set; } = string.Empty;
}

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid ActiveProfileId { get; set; }

    public List<Profile> Profiles { get; set; } = new();

    public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

    /// <summary>Whether to keep history, and for how long (SPEC §28, §80).</summary>
    public AnalyticsConfig Analytics { get; set; } = new();

    public InterfaceConfig Interface { get; set; } = new();

    public NotificationConfig Notifications { get; set; } = new();

    /// <summary>The active profile, or the first one if the stored id no longer exists.</summary>
    public Profile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    /// <summary>
    /// Repairs structurally missing pieces (null lists, no profiles, dangling active id) without altering
    /// values the user chose. Semantic problems are reported by <see cref="ProfileValidator"/> instead.
    /// </summary>
    public AppConfig Normalize()
    {
        Interface ??= new InterfaceConfig();
        Interface.Language = string.IsNullOrWhiteSpace(Interface.Language) ? "auto" : Interface.Language.Trim().ToLowerInvariant();
        Notifications ??= new NotificationConfig();
        Analytics ??= new AnalyticsConfig();
        Analytics.DatabasePath ??= string.Empty;
        Profiles ??= new List<Profile>();
        Profiles.RemoveAll(p => p is null);
        foreach (var p in Profiles)
        {
            p.Reconnect ??= new ReconnectSettings();
            p.Reconnect.DelaysSeconds ??= new List<int>();
            p.Monitoring ??= new HealthCheckSettings();
            p.AdditionalArguments ??= new List<string>();
            p.Name ??= string.Empty;
            p.Host ??= string.Empty;
            p.BindAddress ??= string.Empty;
            p.SshExecutable ??= string.Empty;
            p.Monitoring.TargetHost ??= string.Empty;
            if (p.Id == Guid.Empty)
            {
                p.Id = Guid.NewGuid();
            }
        }

        // Ids must be unique: a hand-edited or copied file could otherwise make two profiles indistinguishable.
        var seen = new HashSet<Guid>();
        foreach (var p in Profiles.Where(p => !seen.Add(p.Id)))
        {
            p.Id = Guid.NewGuid();
            seen.Add(p.Id);
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
