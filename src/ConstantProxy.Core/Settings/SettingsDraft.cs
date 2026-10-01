using System.Globalization;

namespace ConstantProxy.Core.Settings;

/// <summary>A problem with one settings field, ready to be shown next to it.</summary>
public sealed record SettingsIssue(string Field, ValidationIssue Issue);

public sealed class SettingsBuildResult
{
    public IReadOnlyList<SettingsIssue> Issues { get; init; } = Array.Empty<SettingsIssue>();

    public IEnumerable<SettingsIssue> Errors => Issues.Where(i => i.Issue.Severity == IssueSeverity.Error);

    public IEnumerable<SettingsIssue> Warnings => Issues.Where(i => i.Issue.Severity == IssueSeverity.Warning);

    public bool IsValid => !Errors.Any();

    /// <summary>The profile as edited. Only meaningful when <see cref="IsValid"/>.</summary>
    public Profile Profile { get; init; } = new();

    public AnalyticsConfig Analytics { get; init; } = new();

    public InterfaceConfig Interface { get; init; } = new();

    public NotificationConfig Notifications { get; init; } = new();

    public LogVerbosity LogVerbosity { get; init; }
}

/// <summary>
/// The editable state of the settings dialog (SPEC §28): numbers are held as the text the user typed, so half-typed or
/// invalid input can be reported next to its field instead of being lost. <see cref="Build"/> parses and validates it
/// (SPEC §73, §49 "settings validation") without touching the real configuration.
/// </summary>
public sealed class SettingsDraft
{
    // Connection
    public string ProfileName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string BindAddress { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public string SshExecutable { get; set; } = string.Empty;
    public bool IPv4Only { get; set; }
    public string ServerAliveInterval { get; set; } = string.Empty;
    public string ServerAliveCountMax { get; set; } = string.Empty;
    public bool ExitOnForwardFailure { get; set; }
    public string StartupTimeoutSeconds { get; set; } = string.Empty;

    // Reconnect
    public bool AutoReconnect { get; set; }
    public string ReconnectDelays { get; set; } = string.Empty;
    public string MaxDelaySeconds { get; set; } = string.Empty;
    public string HealthyResetSeconds { get; set; } = string.Empty;
    public string JitterPercent { get; set; } = string.Empty;

    // Monitoring
    public bool HealthEnabled { get; set; }
    public string HealthHost { get; set; } = string.Empty;
    public string HealthPort { get; set; } = string.Empty;
    public string HealthInterval { get; set; } = string.Empty;
    public string HealthTimeout { get; set; } = string.Empty;
    public string FailureThreshold { get; set; } = string.Empty;
    public bool ReconnectOnFailure { get; set; }
    public string ReconnectAfterFailures { get; set; } = string.Empty;

    // Analytics
    public bool StoreHistory { get; set; }
    public int RetentionDays { get; set; }
    public string DatabasePath { get; set; } = string.Empty;

    // Interface
    public string Language { get; set; } = LanguageCodes.Auto;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ConnectOnLaunch { get; set; }

    // Notifications
    public bool NotifyOnFailure { get; set; }
    public bool NotifyOnRecovery { get; set; }
    public string MinimumOutageSeconds { get; set; } = string.Empty;

    // Advanced
    public string AdditionalArguments { get; set; } = string.Empty;
    public bool BatchMode { get; set; }
    public TrafficMode TrafficMode { get; set; }
    public LogVerbosity LogVerbosity { get; set; }

    /// <summary>Choices offered for the retention setting (SPEC §80); 0 means forever.</summary>
    public static IReadOnlyList<int> RetentionChoices { get; } = new[] { 30, 90, 180, 365, 0 };

    /// <summary>Maps a validator field name to the draft property that edits it.</summary>
    private static readonly IReadOnlyDictionary<string, string> FieldMap = new Dictionary<string, string>
    {
        [nameof(Profile.Name)] = nameof(ProfileName),
        [nameof(Profile.Host)] = nameof(Host),
        [nameof(Profile.BindAddress)] = nameof(BindAddress),
        [nameof(Profile.Port)] = nameof(Port),
        [nameof(Profile.SshExecutable)] = nameof(SshExecutable),
        [nameof(Profile.ServerAliveInterval)] = nameof(ServerAliveInterval),
        [nameof(Profile.ServerAliveCountMax)] = nameof(ServerAliveCountMax),
        [nameof(Profile.StartupTimeoutSeconds)] = nameof(StartupTimeoutSeconds),
        [nameof(Profile.AdditionalArguments)] = nameof(AdditionalArguments),
        ["Reconnect.DelaysSeconds"] = nameof(ReconnectDelays),
        ["Reconnect.MaxDelaySeconds"] = nameof(MaxDelaySeconds),
        ["Reconnect.HealthyResetSeconds"] = nameof(HealthyResetSeconds),
        ["Reconnect.JitterPercent"] = nameof(JitterPercent),
        ["Monitoring.TargetHost"] = nameof(HealthHost),
        ["Monitoring.TargetPort"] = nameof(HealthPort),
        ["Monitoring.IntervalSeconds"] = nameof(HealthInterval),
        ["Monitoring.TimeoutSeconds"] = nameof(HealthTimeout),
        ["Monitoring.FailureThreshold"] = nameof(FailureThreshold),
        ["Monitoring.ReconnectAfterFailures"] = nameof(ReconnectAfterFailures),
        [nameof(AnalyticsConfig.RetentionDays)] = nameof(RetentionDays),
        [nameof(AnalyticsConfig.DatabasePath)] = nameof(DatabasePath),
        [nameof(NotificationConfig.MinimumOutageSeconds)] = nameof(MinimumOutageSeconds),
    };

    public static SettingsDraft From(AppConfig config, Profile profile) => new()
    {
        ProfileName = profile.Name,
        Host = profile.Host,
        BindAddress = profile.BindAddress,
        Port = Str(profile.Port),
        SshExecutable = profile.SshExecutable,
        IPv4Only = profile.IPv4Only,
        ServerAliveInterval = Str(profile.ServerAliveInterval),
        ServerAliveCountMax = Str(profile.ServerAliveCountMax),
        ExitOnForwardFailure = profile.ExitOnForwardFailure,
        StartupTimeoutSeconds = Str(profile.StartupTimeoutSeconds),
        AutoReconnect = profile.Reconnect.Enabled,
        ReconnectDelays = string.Join(", ", profile.Reconnect.DelaysSeconds.Select(Str)),
        MaxDelaySeconds = Str(profile.Reconnect.MaxDelaySeconds),
        HealthyResetSeconds = Str(profile.Reconnect.HealthyResetSeconds),
        JitterPercent = Str(profile.Reconnect.JitterPercent),
        HealthEnabled = profile.Monitoring.Enabled,
        HealthHost = profile.Monitoring.TargetHost,
        HealthPort = Str(profile.Monitoring.TargetPort),
        HealthInterval = Str(profile.Monitoring.IntervalSeconds),
        HealthTimeout = Str(profile.Monitoring.TimeoutSeconds),
        FailureThreshold = Str(profile.Monitoring.FailureThreshold),
        ReconnectOnFailure = profile.Monitoring.ReconnectOnFailure,
        ReconnectAfterFailures = Str(profile.Monitoring.ReconnectAfterFailures),
        StoreHistory = config.Analytics.StoreHistory,
        RetentionDays = config.Analytics.RetentionDays,
        DatabasePath = config.Analytics.DatabasePath,
        Language = config.Interface.Language,
        StartMinimized = config.Interface.StartMinimized,
        CloseToTray = config.Interface.CloseToTray,
        MinimizeToTray = config.Interface.MinimizeToTray,
        StartWithWindows = config.Interface.StartWithWindows,
        ConnectOnLaunch = config.Interface.ConnectOnLaunch,
        NotifyOnFailure = config.Notifications.NotifyOnFailure,
        NotifyOnRecovery = config.Notifications.NotifyOnRecovery,
        MinimumOutageSeconds = Str(config.Notifications.MinimumOutageSeconds),
        AdditionalArguments = CommandLineSplitter.Join(profile.AdditionalArguments),
        BatchMode = profile.BatchMode,
        TrafficMode = profile.TrafficMode,
        LogVerbosity = config.LogVerbosity,
    };

    /// <summary>Parses and validates the draft against <paramref name="original"/> (whose identity is preserved).</summary>
    /// <param name="repository">Used for the unique-name check; may be null to skip it.</param>
    public SettingsBuildResult Build(Profile original, ProfileRepository? repository = null, Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        var issues = new List<SettingsIssue>();
        var profile = original.Clone();

        profile.Name = ProfileName.Trim();
        profile.Host = Host.Trim();
        profile.BindAddress = BindAddress.Trim();
        profile.SshExecutable = SshExecutable.Trim();
        profile.IPv4Only = IPv4Only;
        profile.ExitOnForwardFailure = ExitOnForwardFailure;
        profile.BatchMode = BatchMode;
        profile.TrafficMode = TrafficMode;
        profile.AdditionalArguments = CommandLineSplitter.Split(AdditionalArguments);
        profile.Reconnect.Enabled = AutoReconnect;
        profile.Monitoring.Enabled = HealthEnabled;
        profile.Monitoring.TargetHost = HealthHost.Trim();
        profile.Monitoring.ReconnectOnFailure = ReconnectOnFailure;

        profile.Port = Int(Port, nameof(Port), profile.Port, issues);
        profile.ServerAliveInterval = Int(ServerAliveInterval, nameof(ServerAliveInterval), profile.ServerAliveInterval, issues);
        profile.ServerAliveCountMax = Int(ServerAliveCountMax, nameof(ServerAliveCountMax), profile.ServerAliveCountMax, issues);
        profile.StartupTimeoutSeconds = Int(StartupTimeoutSeconds, nameof(StartupTimeoutSeconds), profile.StartupTimeoutSeconds, issues);
        profile.Reconnect.MaxDelaySeconds = Int(MaxDelaySeconds, nameof(MaxDelaySeconds), profile.Reconnect.MaxDelaySeconds, issues);
        profile.Reconnect.HealthyResetSeconds = Int(HealthyResetSeconds, nameof(HealthyResetSeconds), profile.Reconnect.HealthyResetSeconds, issues);
        profile.Reconnect.JitterPercent = Int(JitterPercent, nameof(JitterPercent), profile.Reconnect.JitterPercent, issues);
        profile.Monitoring.TargetPort = Int(HealthPort, nameof(HealthPort), profile.Monitoring.TargetPort, issues);
        profile.Monitoring.IntervalSeconds = Int(HealthInterval, nameof(HealthInterval), profile.Monitoring.IntervalSeconds, issues);
        profile.Monitoring.TimeoutSeconds = Int(HealthTimeout, nameof(HealthTimeout), profile.Monitoring.TimeoutSeconds, issues);
        profile.Monitoring.FailureThreshold = Int(FailureThreshold, nameof(FailureThreshold), profile.Monitoring.FailureThreshold, issues);
        profile.Monitoring.ReconnectAfterFailures = Int(ReconnectAfterFailures, nameof(ReconnectAfterFailures), profile.Monitoring.ReconnectAfterFailures, issues);
        profile.Reconnect.DelaysSeconds = IntList(ReconnectDelays, nameof(ReconnectDelays), profile.Reconnect.DelaysSeconds, issues);

        var notifications = new NotificationConfig
        {
            NotifyOnFailure = NotifyOnFailure,
            NotifyOnRecovery = NotifyOnRecovery,
            MinimumOutageSeconds = Int(MinimumOutageSeconds, nameof(MinimumOutageSeconds), new NotificationConfig().MinimumOutageSeconds, issues),
        };
        var analytics = new AnalyticsConfig { StoreHistory = StoreHistory, RetentionDays = RetentionDays, DatabasePath = DatabasePath.Trim() };
        var ui = new InterfaceConfig
        {
            Language = LanguageCodes.IsValidSetting(Language) ? Language.ToLowerInvariant() : LanguageCodes.Auto,
            StartMinimized = StartMinimized,
            CloseToTray = CloseToTray,
            MinimizeToTray = MinimizeToTray,
            StartWithWindows = StartWithWindows,
            ConnectOnLaunch = ConnectOnLaunch,
        };

        // Only validate what parsed: a value that could not be read already has its own message.
        var unparsed = issues.Select(i => i.Field).ToHashSet();
        foreach (var issue in ProfileValidator.Validate(profile, fileExists).Issues)
        {
            AddMapped(issues, issue, unparsed);
        }

        foreach (var issue in ProfileValidator.ValidateAnalytics(analytics, directoryExists).Issues)
        {
            AddMapped(issues, issue, unparsed);
        }

        foreach (var issue in ProfileValidator.ValidateNotifications(notifications).Issues)
        {
            AddMapped(issues, issue, unparsed);
        }

        if (repository is not null && !issues.Any(i => i.Field == nameof(ProfileName)))
        {
            foreach (var issue in repository.ValidateName(ProfileName, repository.Find(original.Id)).Issues)
            {
                AddMapped(issues, issue, unparsed);
            }
        }

        return new SettingsBuildResult
        {
            Issues = issues
                .GroupBy(i => (i.Field, i.Issue.Code))
                .Select(g => g.First())
                .ToList(),
            Profile = profile,
            Analytics = analytics,
            Interface = ui,
            Notifications = notifications,
            LogVerbosity = LogVerbosity,
        };
    }

    private static void AddMapped(List<SettingsIssue> issues, ValidationIssue issue, HashSet<string> unparsedFields)
    {
        var field = FieldMap.TryGetValue(issue.Field, out var mapped) ? mapped : issue.Field;
        if (unparsedFields.Contains(field))
        {
            return; // "not a number" is more useful than the range message for the stand-in value
        }

        issues.Add(new SettingsIssue(field, issue));
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Int(string text, string field, int fallback, List<SettingsIssue> issues)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        issues.Add(new SettingsIssue(field, new ValidationIssue(IssueSeverity.Error, field, "number.invalid", "Enter a whole number.")));
        return fallback;
    }

    private static List<int> IntList(string text, string field, List<int> fallback, List<SettingsIssue> issues)
    {
        var result = new List<int>();
        foreach (var part in (text ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                issues.Add(new SettingsIssue(field, new ValidationIssue(IssueSeverity.Error, field, "list.invalid", "Enter a comma-separated list of whole numbers.")));
                return fallback;
            }

            result.Add(value);
        }

        return result;
    }
}
