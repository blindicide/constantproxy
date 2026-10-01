using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Desktop;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Logging;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Presentation;
using ConstantProxy.Core.Ssh;
using ConstantProxy.Core.Traffic;
using ConstantProxy.Core.Validation;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Logging;

namespace ConstantProxy.App.ViewModels;

/// <summary>
/// Presentation logic for the main window. It never touches processes or sockets: all of that lives behind
/// <see cref="ConnectionManager"/> (SPEC §60).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConnectionManager manager;
    private readonly TrafficSamplingService traffic;
    private readonly AnalyticsRecorder? recorder;
    private readonly StartupManager? startup;
    private readonly ConfigurationService configuration;
    private readonly AppConfig config;
    private readonly AppLog log;
    private readonly LocalizationService loc;
    private readonly ProfileRepository profiles;
    private readonly IConfirmDialog confirm;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;

    private ConnectionState state = ConnectionState.Disconnected;
    private string sessionDuration = "00:00:00";
    private FailureInfo? currentFailure;
    private string validationText = string.Empty;
    private string logText = string.Empty;
    private int reconnectCount;
    private string latencyText = string.Empty;
    private string languageSetting;
    private bool closing;

    private string profileName = string.Empty;
    private string settingsNote = string.Empty;
    private IReadOnlyList<LocalizedOption<Guid>> profileOptions = Array.Empty<LocalizedOption<Guid>>();
    private string host = string.Empty;
    private string bindAddress = string.Empty;
    private string port = string.Empty;
    private string sshExecutable = string.Empty;
    private bool ipv4Only;
    private string serverAliveInterval = string.Empty;
    private string serverAliveCountMax = string.Empty;
    private string additionalArguments = string.Empty;
    private bool autoReconnect;
    private bool healthEnabled;
    private string healthHost = string.Empty;
    private string healthPort = string.Empty;
    private bool measureTraffic;
    private bool startMinimized;
    private bool closeToTray;
    private bool minimizeToTray;
    private bool startWithWindows;
    private bool connectOnLaunch;
    private bool notifyOnFailure;
    private bool notifyOnRecovery;
    private string minimumOutageSeconds;
    private int graphWindowSeconds = 60;
    private IReadOnlyList<TrafficPoint> graphPoints = Array.Empty<TrafficPoint>();
    private string downloadRateText = string.Empty;
    private string uploadRateText = string.Empty;
    private string downloadTotalText = string.Empty;
    private string uploadTotalText = string.Empty;
    private string peakText = string.Empty;
    private string averageText = string.Empty;
    private IReadOnlyList<LocalizedOption<string>> languageOptions = Array.Empty<LocalizedOption<string>>();

    public MainViewModel(ConnectionManager manager, TrafficSamplingService traffic, AnalyticsRecorder? recorder, StatisticsViewModel statistics, StartupManager? startup, LocalizationService loc, IConfirmDialog confirm, ConfigurationService configuration, AppConfig config, AppLog log, Dispatcher dispatcher)
    {
        this.manager = manager;
        this.traffic = traffic;
        this.recorder = recorder;
        this.startup = startup;
        this.loc = loc;
        this.confirm = confirm;
        Statistics = statistics;
        this.configuration = configuration;
        this.config = config;
        this.log = log;
        this.dispatcher = dispatcher;

        profiles = new ProfileRepository(config);
        LoadEditor(profiles.Active);
        startMinimized = config.Interface.StartMinimized;
        closeToTray = config.Interface.CloseToTray;
        minimizeToTray = config.Interface.MinimizeToTray;
        startWithWindows = config.Interface.StartWithWindows;
        connectOnLaunch = config.Interface.ConnectOnLaunch;
        notifyOnFailure = config.Notifications.NotifyOnFailure;
        notifyOnRecovery = config.Notifications.NotifyOnRecovery;
        minimumOutageSeconds = config.Notifications.MinimumOutageSeconds.ToString(CultureInfo.InvariantCulture);
        languageSetting = config.Interface.Language;
        languageOptions = BuildLanguageOptions();
        downloadRateText = uploadRateText = downloadTotalText = uploadTotalText = loc.Get("traffic.unavailable");
        latencyText = loc.Get("stats.none");
        profileOptions = BuildProfileOptions();

        NewProfileCommand = new RelayCommand(() => { AddProfile(); return Task.CompletedTask; }, () => CanEditProfiles);
        CloneProfileCommand = new RelayCommand(() => { CloneProfile(); return Task.CompletedTask; }, () => CanEditProfiles);
        DeleteProfileCommand = new RelayCommand(() => { DeleteProfile(); return Task.CompletedTask; }, () => CanEditProfiles && profiles.Profiles.Count > 1);
        ConnectCommand = new RelayCommand(ConnectAsync, () => State is ConnectionState.Disconnected or ConnectionState.Failed);
        DisconnectCommand = new RelayCommand(manager.DisconnectAsync, () => State != ConnectionState.Disconnected);
        ReconnectCommand = new RelayCommand(async () => { await manager.ReconnectNowAsync(); }, () => State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting);

        manager.StateChanged += change => dispatcher.BeginInvoke(() => OnStateChanged(change));
        manager.HealthChecked += _ => dispatcher.BeginInvoke(RefreshSession);
        traffic.Sampled += _ => dispatcher.BeginInvoke(RefreshTraffic);
        log.EntryAdded += entry => dispatcher.BeginInvoke(() => AppendLog(entry));
        loc.LanguageChanged += () => dispatcher.BeginInvoke(OnLanguageChanged);
        foreach (var entry in log.Snapshot())
        {
            AppendLogLine(entry);
        }

        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshSession();
    }

    public StatisticsViewModel Statistics { get; }

    public RelayCommand NewProfileCommand { get; }

    public RelayCommand CloneProfileCommand { get; }

    public RelayCommand DeleteProfileCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand ReconnectCommand { get; }

    public string VersionText => $"v{Core.VersionInfo.Version}";

    public ConnectionState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(StateGlyph));
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(CanEditProfiles));
                NewProfileCommand.RaiseCanExecuteChanged();
                CloneProfileCommand.RaiseCanExecuteChanged();
                DeleteProfileCommand.RaiseCanExecuteChanged();
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ReconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StateText => LocalizedText.State(loc, State);

    public string StateGlyph => State switch
    {
        ConnectionState.Connected => "●",
        ConnectionState.Degraded => "▲",
        ConnectionState.Failed => "✖",
        ConnectionState.Disconnected => "○",
        _ => "◌",
    };

    /// <summary>Colour is never the only signal: the glyph and text label always accompany it (SPEC §27).</summary>
    public Brush StateBrush => State switch
    {
        ConnectionState.Connected => Brushes.SeaGreen,
        ConnectionState.Starting or ConnectionState.Connecting => Brushes.SteelBlue,
        ConnectionState.Degraded => Brushes.Goldenrod,
        ConnectionState.Reconnecting or ConnectionState.Stopping => Brushes.DarkOrange,
        ConnectionState.Failed => Brushes.Firebrick,
        _ => Brushes.Gray,
    };

    public string SessionDuration
    {
        get => sessionDuration;
        private set => SetProperty(ref sessionDuration, value);
    }

    /// <summary>One line explaining the current state; recomputed from the state so a language switch updates it.</summary>
    public string StatusDetail => State switch
    {
        ConnectionState.Failed => currentFailure is { } failure ? LocalizedText.Failure(loc, failure) : string.Empty,
        ConnectionState.Reconnecting => currentFailure is { } failure ? LocalizedText.Failure(loc, failure) : loc.Get("status.reconnecting"),
        ConnectionState.Degraded => loc.Get("status.degraded"),
        _ => string.Empty,
    };

    public string ValidationText
    {
        get => validationText;
        private set => SetProperty(ref validationText, value);
    }

    public string LogText
    {
        get => logText;
        private set => SetProperty(ref logText, value);
    }

    public int ReconnectCount
    {
        get => reconnectCount;
        private set => SetProperty(ref reconnectCount, value);
    }

    public string DownloadRateText { get => downloadRateText; private set => SetProperty(ref downloadRateText, value); }

    public string UploadRateText { get => uploadRateText; private set => SetProperty(ref uploadRateText, value); }

    public string DownloadTotalText { get => downloadTotalText; private set => SetProperty(ref downloadTotalText, value); }

    public string UploadTotalText { get => uploadTotalText; private set => SetProperty(ref uploadTotalText, value); }

    public string PeakText { get => peakText; private set => SetProperty(ref peakText, value); }

    public string AverageText { get => averageText; private set => SetProperty(ref averageText, value); }

    public IReadOnlyList<TrafficPoint> GraphPoints { get => graphPoints; private set => SetProperty(ref graphPoints, value); }

    public int GraphWindowSeconds
    {
        get => graphWindowSeconds;
        set
        {
            if (SetProperty(ref graphWindowSeconds, value))
            {
                RefreshTraffic();
            }
        }
    }

    public IReadOnlyList<int> GraphWindows { get; } = new[] { 60, 300, 3600 };

    public bool MeasureTraffic { get => measureTraffic; set => SetProperty(ref measureTraffic, value); }

    public bool StartMinimized { get => startMinimized; set => SetProperty(ref startMinimized, value); }

    public bool CloseToTray { get => closeToTray; set => SetProperty(ref closeToTray, value); }

    public bool MinimizeToTray { get => minimizeToTray; set => SetProperty(ref minimizeToTray, value); }

    public bool StartWithWindows { get => startWithWindows; set => SetProperty(ref startWithWindows, value); }

    public bool ConnectOnLaunch { get => connectOnLaunch; set => SetProperty(ref connectOnLaunch, value); }

    public bool NotifyOnFailure { get => notifyOnFailure; set => SetProperty(ref notifyOnFailure, value); }

    public bool NotifyOnRecovery { get => notifyOnRecovery; set => SetProperty(ref notifyOnRecovery, value); }

    public string MinimumOutageSeconds { get => minimumOutageSeconds; set => SetProperty(ref minimumOutageSeconds, value); }

    public AppConfig Config => config;

    public IReadOnlyList<Profile> Profiles => profiles.Profiles;

    public Profile ActiveProfile => profiles.Active;

    /// <summary>Profiles can only be switched, added or removed while no tunnel is running (SPEC §64).</summary>
    public bool CanEditProfiles => State is ConnectionState.Disconnected or ConnectionState.Failed;

    public IReadOnlyList<LocalizedOption<Guid>> ProfileOptions { get => profileOptions; private set => SetProperty(ref profileOptions, value); }

    public string ProfileName { get => profileName; set => SetProperty(ref profileName, value); }

    /// <summary>Informational line under the settings (for example: changes apply on the next connect).</summary>
    public string SettingsNote { get => settingsNote; private set => SetProperty(ref settingsNote, value); }

    public Guid SelectedProfileId
    {
        get => profiles.Active.Id;
        set
        {
            if (value != Guid.Empty && value != profiles.Active.Id)
            {
                SwitchProfile(value);
            }
        }
    }

    /// <summary>Raised on the UI thread after profiles were added, removed, renamed or the selection changed.</summary>
    public event Action? ProfilesChanged;

    public IReadOnlyList<LocalizedOption<string>> LanguageOptions { get => languageOptions; private set => SetProperty(ref languageOptions, value); }

    /// <summary><c>auto</c>, <c>en</c> or <c>ru</c>. Applied and saved immediately; no restart needed (SPEC §30).</summary>
    public string LanguageSetting
    {
        get => languageSetting;
        set
        {
            if (string.IsNullOrEmpty(value) || !LanguageCodes.IsValidSetting(value) || !SetProperty(ref languageSetting, value))
            {
                return; // a combo box briefly reports null while its items are being replaced
            }

            config.Interface.Language = value;
            loc.SetLanguage(LanguageCodes.Resolve(value, CultureInfo.CurrentUICulture));
            try
            {
                configuration.Save(config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error("config", "Could not save the language setting.", ex);
            }
        }
    }

    public ConnectionState CurrentState => State;

    /// <summary>Raised on the UI thread whenever the connection state changes (used by the tray).</summary>
    public event Action<ConnectionState>? StateChangedForTray;

    public RelayCommand SaveCommand => saveCommand ??= new RelayCommand(() => { ApplyAndSave(); return Task.CompletedTask; });

    private RelayCommand? saveCommand;

    public Task ConnectFromTrayAsync() => ConnectAsync();

    public Task DisconnectFromTrayAsync() => manager.DisconnectAsync();

    public Task ReconnectFromTrayAsync() => manager.ReconnectNowAsync();

    /// <summary>Connects at startup when configured and a target exists.</summary>
    public Task ConnectOnLaunchAsync() => string.IsNullOrWhiteSpace(Host) ? Task.CompletedTask : ConnectAsync();

    public string LatencyText
    {
        get => latencyText;
        private set => SetProperty(ref latencyText, value);
    }

    /// <summary>Technical details of the last failure, shown in an expandable section (SPEC §34).</summary>
    public string FailureDetails =>
        State is ConnectionState.Failed or ConnectionState.Reconnecting && currentFailure is { } failure
            ? LocalizedText.FailureDetails(loc, failure)
            : string.Empty;

    public bool HasFailureDetails => !string.IsNullOrWhiteSpace(FailureDetails);

    public LocalizationService Localization => loc;

    public string Endpoint => string.IsNullOrWhiteSpace(Host) ? loc.Get("stats.none") : SshArgumentBuilder.FormatEndpoint(BindAddress, int.TryParse(Port, out var p) ? p : 0);

    public string Host { get => host; set { if (SetProperty(ref host, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string BindAddress { get => bindAddress; set { if (SetProperty(ref bindAddress, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string Port { get => port; set { if (SetProperty(ref port, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string SshExecutable { get => sshExecutable; set => SetProperty(ref sshExecutable, value); }

    public bool IPv4Only { get => ipv4Only; set => SetProperty(ref ipv4Only, value); }

    public string ServerAliveInterval { get => serverAliveInterval; set => SetProperty(ref serverAliveInterval, value); }

    public string ServerAliveCountMax { get => serverAliveCountMax; set => SetProperty(ref serverAliveCountMax, value); }

    public string AdditionalArguments { get => additionalArguments; set => SetProperty(ref additionalArguments, value); }

    public bool AutoReconnect { get => autoReconnect; set => SetProperty(ref autoReconnect, value); }

    public bool HealthEnabled { get => healthEnabled; set => SetProperty(ref healthEnabled, value); }

    public string HealthHost { get => healthHost; set => SetProperty(ref healthHost, value); }

    public string HealthPort { get => healthPort; set => SetProperty(ref healthPort, value); }

    /// <summary>First half of shutdown: stops the UI-thread timers. Must run on the UI thread.</summary>
    public void StopUiActivity()
    {
        closing = true;
        timer.Stop();
        Statistics.Stop();
    }

    /// <summary>
    /// Second half of shutdown: stops the tunnel (terminating only our own ssh) and flushes analytics. Touches no UI
    /// objects, so it is safe to run on a worker thread when Windows is ending the session.
    /// </summary>
    public async Task ShutdownCoreAsync()
    {
        log.Info("app", "Application shutting down");
        recorder?.MarkShuttingDown();
        await manager.DisposeAsync().ConfigureAwait(false);
        await traffic.StopAsync().ConfigureAwait(false);
        if (recorder is not null)
        {
            recorder.RecordApplicationEvent(ConnectionEventType.ApplicationExit);
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ShutdownAsync()
    {
        StopUiActivity();
        await ShutdownCoreAsync();
    }

    private async Task ConnectAsync()
    {
        if (!ApplyAndSave())
        {
            return;
        }

        await manager.ConnectAsync(profiles.Active);
    }

    private bool ApplyAndSave() => ApplyEditor(strict: true);

    /// <summary>
    /// Copies the edited fields into the active profile and persists. A strict apply (Save, Connect) refuses invalid
    /// input and reports why; a lenient one (switching profiles) keeps whatever parses so no typing is ever lost.
    /// </summary>
    private bool ApplyEditor(bool strict)
    {
        var p = profiles.Active;
        var errors = new List<string>();

        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            errors.Add(loc.Get("settings.number.port"));
            parsedPort = p.Port;
        }

        if (!int.TryParse(ServerAliveInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            errors.Add(loc.Get("settings.number.alive"));
            interval = p.ServerAliveInterval;
        }

        if (!int.TryParse(ServerAliveCountMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var countMax))
        {
            errors.Add(loc.Get("settings.number.count"));
            countMax = p.ServerAliveCountMax;
        }

        if (!int.TryParse(HealthPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var healthPortValue))
        {
            errors.Add(loc.Get("settings.number.healthPort"));
            healthPortValue = p.Monitoring.TargetPort;
        }

        var nameCheck = profiles.ValidateName(ProfileName, p);
        errors.AddRange(nameCheck.Errors.Select(i => LocalizedText.Issue(loc, i)));

        var candidate = p.Clone();
        candidate.Name = nameCheck.IsValid ? ProfileName.Trim() : p.Name;
        candidate.Host = Host.Trim();
        candidate.BindAddress = BindAddress.Trim();
        candidate.Port = parsedPort;
        candidate.SshExecutable = SshExecutable.Trim();
        candidate.IPv4Only = IPv4Only;
        candidate.ServerAliveInterval = interval;
        candidate.ServerAliveCountMax = countMax;
        candidate.AdditionalArguments = CommandLineSplitter.Split(AdditionalArguments);
        candidate.Reconnect.Enabled = AutoReconnect;
        candidate.Monitoring.Enabled = HealthEnabled;
        candidate.Monitoring.TargetHost = HealthHost.Trim();
        candidate.Monitoring.TargetPort = healthPortValue;
        candidate.TrafficMode = MeasureTraffic ? TrafficMode.Bridge : TrafficMode.Off;

        var result = ProfileValidator.Validate(candidate);
        errors.AddRange(result.Errors.Select(i => LocalizedText.Issue(loc, i)));
        var builder = new StringBuilder();

        if (!int.TryParse(MinimumOutageSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outageSeconds))
        {
            errors.Add(loc.Get("settings.number.outage"));
            outageSeconds = config.Notifications.MinimumOutageSeconds;
        }
        else
        {
            var notificationCheck = ProfileValidator.ValidateNotifications(new NotificationConfig { MinimumOutageSeconds = outageSeconds });
            errors.AddRange(notificationCheck.Errors.Select(i => LocalizedText.Issue(loc, i)));
        }

        builder.Clear();
        foreach (var line in errors.Distinct())
        {
            builder.AppendLine(line);
        }

        foreach (var warning in result.Warnings)
        {
            builder.AppendLine(loc.Format("settings.warning", LocalizedText.Issue(loc, warning)));
        }

        ValidationText = strict ? builder.ToString().TrimEnd() : string.Empty;
        if (strict && errors.Count > 0)
        {
            return false;
        }

        if (strict)
        {
            ApplyGlobalSettings(outageSeconds);
        }

        var index = config.Profiles.FindIndex(x => x.Id == p.Id);
        config.Profiles[index] = candidate;
        SettingsNote = strict && !CanEditProfiles ? loc.Get("settings.appliesNextConnect") : string.Empty;
        SaveConfiguration();
        if (!string.Equals(p.Name, candidate.Name, StringComparison.Ordinal))
        {
            NotifyProfilesChanged();
        }

        return true;
    }

    private void ApplyGlobalSettings(int outageSeconds)
    {
        config.Interface.StartMinimized = StartMinimized;
        config.Interface.CloseToTray = CloseToTray;
        config.Interface.MinimizeToTray = MinimizeToTray;
        config.Interface.StartWithWindows = StartWithWindows;
        config.Interface.ConnectOnLaunch = ConnectOnLaunch;
        config.Notifications.NotifyOnFailure = NotifyOnFailure;
        config.Notifications.NotifyOnRecovery = NotifyOnRecovery;
        config.Notifications.MinimumOutageSeconds = outageSeconds;
        ApplyStartupRegistration();
    }

    private void SaveConfiguration()
    {
        try
        {
            configuration.Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("config", "Could not save the configuration.", ex);
            ValidationText += (ValidationText.Length > 0 ? Environment.NewLine : string.Empty) + loc.Format("dialog.saveFailed", ex.Message);
        }
    }

    // ---- profiles (SPEC §7, §48) ----

    private void LoadEditor(Profile p)
    {
        profileName = p.Name;
        host = p.Host;
        bindAddress = p.BindAddress;
        port = p.Port.ToString(CultureInfo.InvariantCulture);
        sshExecutable = p.SshExecutable;
        ipv4Only = p.IPv4Only;
        serverAliveInterval = p.ServerAliveInterval.ToString(CultureInfo.InvariantCulture);
        serverAliveCountMax = p.ServerAliveCountMax.ToString(CultureInfo.InvariantCulture);
        additionalArguments = CommandLineSplitter.Join(p.AdditionalArguments);
        autoReconnect = p.Reconnect.Enabled;
        healthEnabled = p.Monitoring.Enabled;
        healthHost = p.Monitoring.TargetHost;
        healthPort = p.Monitoring.TargetPort.ToString(CultureInfo.InvariantCulture);
        measureTraffic = p.TrafficMode == TrafficMode.Bridge;
        OnPropertyChanged(string.Empty);
    }

    private IReadOnlyList<LocalizedOption<Guid>> BuildProfileOptions() =>
        profiles.Profiles.Select(p => new LocalizedOption<Guid>(p.Id, p.Name)).ToArray();

    private void NotifyProfilesChanged()
    {
        ProfileOptions = BuildProfileOptions();
        OnPropertyChanged(nameof(SelectedProfileId));
        OnPropertyChanged(nameof(Endpoint));
        DeleteProfileCommand.RaiseCanExecuteChanged();
        Statistics.OnProfileChanged();
        ProfilesChanged?.Invoke();
    }

    private void SwitchProfile(Guid id)
    {
        if (!CanEditProfiles)
        {
            SettingsNote = loc.Get("tooltip.profileLocked");
            OnPropertyChanged(nameof(SelectedProfileId)); // snap the selector back
            return;
        }

        ApplyEditor(strict: false);
        try
        {
            profiles.SetActive(id);
        }
        catch (ProfileException ex)
        {
            ValidationText = LocalizedText.Issue(loc, new ValidationIssue(IssueSeverity.Error, nameof(Profile), ex.Code, ex.Message));
            return;
        }

        LoadEditor(profiles.Active);
        ValidationText = string.Empty;
        SettingsNote = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    /// <summary>Selects a profile from the tray menu.</summary>
    public void SelectProfileFromTray(Guid id) => SwitchProfile(id);

    private void AddProfile()
    {
        ApplyEditor(strict: false);
        var created = profiles.Add(null, loc.Get("profile.defaultName"));
        profiles.SetActive(created.Id);
        LoadEditor(created);
        ValidationText = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    private void CloneProfile()
    {
        ApplyEditor(strict: false);
        var copy = profiles.Clone(profiles.Active.Id, loc.Get("profile.copyName"));
        profiles.SetActive(copy.Id);
        LoadEditor(copy);
        ValidationText = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    private void DeleteProfile()
    {
        var doomed = profiles.Active;
        if (!confirm.Confirm(loc.Format("dialog.deleteProfile", doomed.Name)))
        {
            return;
        }

        try
        {
            profiles.Delete(doomed.Id);
        }
        catch (ProfileException ex)
        {
            ValidationText = LocalizedText.Issue(loc, new ValidationIssue(IssueSeverity.Error, nameof(Profile), ex.Code, ex.Message));
            return;
        }

        LoadEditor(profiles.Active);
        ValidationText = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    private void ApplyStartupRegistration()
    {
        if (startup is null)
        {
            return;
        }

        try
        {
            startup.Apply(config.Interface.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or IOException)
        {
            log.Warn("app", "Could not update the Windows startup entry.", ex);
            ValidationText += (ValidationText.Length > 0 ? Environment.NewLine : string.Empty) + loc.Format("dialog.startupFailed", ex.Message);
        }
    }

    private void OnStateChanged(StateChange change)
    {
        if (closing)
        {
            return;
        }

        if (change.New is ConnectionState.Failed or ConnectionState.Reconnecting)
        {
            currentFailure = change.Failure ?? currentFailure;
        }
        else if (change.New is ConnectionState.Connected or ConnectionState.Disconnected or ConnectionState.Starting)
        {
            currentFailure = null;
        }

        State = change.New;
        StateChangedForTray?.Invoke(change.New);
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(FailureDetails));
        OnPropertyChanged(nameof(HasFailureDetails));

        if (change.New is ConnectionState.Disconnected)
        {
            timer.Stop();
            SessionDuration = LocalizedText.Duration(loc, TimeSpan.Zero);
        }
        else if (!timer.IsEnabled)
        {
            timer.Start(); // the 1 Hz timer only runs while a session exists
        }

        if (change.Old is ConnectionState.Disconnected or ConnectionState.Failed && change.New == ConnectionState.Starting)
        {
            traffic.StartSession();
        }
        else if (change.New is ConnectionState.Disconnected or ConnectionState.Failed)
        {
            _ = StopSamplingAsync();
        }

        RefreshSession();
    }

    private async Task StopSamplingAsync()
    {
        await traffic.StopAsync();
        await dispatcher.BeginInvoke(RefreshTraffic);
    }

    private void RefreshTraffic()
    {
        var s = traffic.Statistics;
        if (!s.IsAvailable)
        {
            DownloadRateText = UploadRateText = DownloadTotalText = UploadTotalText = loc.Get("traffic.unavailable");
            PeakText = AverageText = string.Empty;
            GraphPoints = Array.Empty<TrafficPoint>();
            return;
        }

        DownloadRateText = LocalizedText.Rate(loc, s.CurrentDownloadRate ?? 0);
        UploadRateText = LocalizedText.Rate(loc, s.CurrentUploadRate ?? 0);
        DownloadTotalText = LocalizedText.Bytes(loc, s.SessionDownloadBytes ?? 0);
        UploadTotalText = LocalizedText.Bytes(loc, s.SessionUploadBytes ?? 0);
        PeakText = loc.Format("traffic.peak", LocalizedText.Rate(loc, s.PeakDownloadRate), LocalizedText.Rate(loc, s.PeakUploadRate));
        AverageText = loc.Format("traffic.average", LocalizedText.Rate(loc, s.AverageDownloadRate), LocalizedText.Rate(loc, s.AverageUploadRate));
        GraphPoints = s.Recent(GraphWindowSeconds + 1);
    }

    private IReadOnlyList<LocalizedOption<string>> BuildLanguageOptions() => new[]
    {
        new LocalizedOption<string>(LanguageCodes.Auto, loc.Get("language.auto")),
        new LocalizedOption<string>(LanguageCodes.English, loc.Get("language.en")),
        new LocalizedOption<string>(LanguageCodes.Russian, loc.Get("language.ru")),
    };

    /// <summary>Re-renders everything that was composed in the previous language (SPEC §30: no restart needed).</summary>
    private void OnLanguageChanged()
    {
        LanguageOptions = BuildLanguageOptions();
        RefreshTraffic();
        RefreshSession();
        if (State == ConnectionState.Disconnected)
        {
            SessionDuration = LocalizedText.Duration(loc, TimeSpan.Zero);
        }

        Statistics.OnLanguageChanged();
        OnPropertyChanged(string.Empty); // every bound text is recomposed
    }

    private void RefreshSession()
    {
        ReconnectCount = manager.ReconnectCount;
        LatencyText = State is ConnectionState.Connected or ConnectionState.Degraded && manager.LastHealth is { Success: true, Latency: { } latency }
            ? loc.Format("label.latency.value", latency.TotalMilliseconds.ToString("0", loc.Culture))
            : loc.Get("stats.none");
        var started = manager.SessionStartedUtc;
        if (started is not null)
        {
            SessionDuration = LocalizedText.Duration(loc, DateTimeOffset.UtcNow - started.Value);
        }
    }

    private void AppendLog(LogEntry entry)
    {
        AppendLogLine(entry);
    }

    private void AppendLogLine(LogEntry entry)
    {
        var text = LogText + AppLog.Format(entry) + Environment.NewLine;
        LogText = text.Length > 60_000 ? text[^40_000..] : text;
    }
}

/// <summary>A combo-box entry whose label is localized but whose value is stable.</summary>
public sealed record LocalizedOption<T>(T Value, string Label);
