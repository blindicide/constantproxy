using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using ConstantProxy.App.Services;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Desktop;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Logging;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Settings;
using ConstantProxy.Core.Ssh;
using ConstantProxy.Core.Traffic;
using ConstantProxy.Core.Validation;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Logging;

namespace ConstantProxy.App.ViewModels;

/// <summary>
/// Presentation logic for the main window. It never touches processes or sockets: all of that lives behind
/// <see cref="ConnectionManager"/> (SPEC §60). Configuration is edited in the settings dialog, never inline.
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
    private readonly IWindowService windows;
    private readonly AppPaths paths;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;

    private ConnectionState state = ConnectionState.Disconnected;
    private FailureInfo? currentFailure;
    private string sessionDuration = "00:00:00";
    private string notice = string.Empty;
    private string logText = string.Empty;
    private int reconnectCount;
    private string latencyText = string.Empty;
    private bool closing;
    private int graphWindowSeconds = 60;
    private IReadOnlyList<TrafficPoint> graphPoints = Array.Empty<TrafficPoint>();
    private string downloadRateText = string.Empty;
    private string uploadRateText = string.Empty;
    private string downloadTotalText = string.Empty;
    private string uploadTotalText = string.Empty;
    private string peakText = string.Empty;
    private string averageText = string.Empty;
    private IReadOnlyList<LocalizedOption<Guid>> profileOptions = Array.Empty<LocalizedOption<Guid>>();

    public MainViewModel(
        ConnectionManager manager,
        TrafficSamplingService traffic,
        AnalyticsRecorder? recorder,
        StatisticsViewModel statistics,
        StartupManager? startup,
        LocalizationService loc,
        IConfirmDialog confirm,
        IWindowService windows,
        AppPaths paths,
        ConfigurationService configuration,
        AppConfig config,
        AppLog log,
        Dispatcher dispatcher)
    {
        this.manager = manager;
        this.traffic = traffic;
        this.recorder = recorder;
        this.startup = startup;
        this.loc = loc;
        this.confirm = confirm;
        this.windows = windows;
        this.paths = paths;
        Statistics = statistics;
        this.configuration = configuration;
        this.config = config;
        this.log = log;
        this.dispatcher = dispatcher;
        profiles = new ProfileRepository(config);

        downloadRateText = uploadRateText = downloadTotalText = uploadTotalText = loc.Get("traffic.unavailable");
        latencyText = loc.Get("stats.none");
        profileOptions = BuildProfileOptions();

        NewProfileCommand = new RelayCommand(() => { AddProfile(); return Task.CompletedTask; }, () => CanEditProfiles);
        CloneProfileCommand = new RelayCommand(() => { CloneProfile(); return Task.CompletedTask; }, () => CanEditProfiles);
        DeleteProfileCommand = new RelayCommand(() => { DeleteProfile(); return Task.CompletedTask; }, () => CanEditProfiles && profiles.Profiles.Count > 1);
        SettingsCommand = new RelayCommand(() => { OpenSettings(); return Task.CompletedTask; });
        DiagnosticsCommand = new RelayCommand(() => { windows.ShowDiagnostics(new DiagnosticsViewModel(manager, config, paths, loc)); return Task.CompletedTask; });
        AboutCommand = new RelayCommand(() => { windows.ShowAbout(); return Task.CompletedTask; });
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

    public RelayCommand SettingsCommand { get; }

    public RelayCommand DiagnosticsCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand ReconnectCommand { get; }

    public string VersionText => $"v{Core.VersionInfo.Version}";

    public AppConfig Config => config;

    public LocalizationService Localization => loc;

    public IReadOnlyList<Profile> Profiles => profiles.Profiles;

    public Profile ActiveProfile => profiles.Active;

    public ConnectionState CurrentState => State;

    /// <summary>Profiles can only be switched, added or removed while no tunnel is running (SPEC §64).</summary>
    public bool CanEditProfiles => State is ConnectionState.Disconnected or ConnectionState.Failed;

    public IReadOnlyList<LocalizedOption<Guid>> ProfileOptions { get => profileOptions; private set => SetProperty(ref profileOptions, value); }

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

    /// <summary>Raised on the UI thread whenever the connection state changes (used by the tray).</summary>
    public event Action<ConnectionState>? StateChangedForTray;

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

    public string SessionDuration { get => sessionDuration; private set => SetProperty(ref sessionDuration, value); }

    /// <summary>One line explaining the current state; recomputed from the state so a language switch updates it.</summary>
    public string StatusDetail => State switch
    {
        ConnectionState.Failed => currentFailure is { } failure ? LocalizedText.Failure(loc, failure) : string.Empty,
        ConnectionState.Reconnecting => currentFailure is { } failure ? LocalizedText.Failure(loc, failure) : loc.Get("status.reconnecting"),
        ConnectionState.Degraded => loc.Get("status.degraded"),
        ConnectionState.Disconnected when string.IsNullOrWhiteSpace(profiles.Active.Host) => loc.Get("status.noTarget"),
        _ => string.Empty,
    };

    /// <summary>Technical details of the last failure, shown in an expandable section (SPEC §34).</summary>
    public string FailureDetails =>
        State is ConnectionState.Failed or ConnectionState.Reconnecting && currentFailure is { } failure
            ? LocalizedText.FailureDetails(loc, failure)
            : string.Empty;

    public bool HasFailureDetails => !string.IsNullOrWhiteSpace(FailureDetails);

    /// <summary>Message about the last profile or settings operation (errors, or "applies on next connect").</summary>
    public string Notice { get => notice; private set => SetProperty(ref notice, value); }

    public string LogText { get => logText; private set => SetProperty(ref logText, value); }

    public int ReconnectCount { get => reconnectCount; private set => SetProperty(ref reconnectCount, value); }

    public string LatencyText { get => latencyText; private set => SetProperty(ref latencyText, value); }

    public string Endpoint => string.IsNullOrWhiteSpace(profiles.Active.Host) ? loc.Get("stats.none") : SshArgumentBuilder.FormatEndpoint(profiles.Active.BindAddress, profiles.Active.Port);

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

    // ---- tray and startup entry points ----

    public Task ConnectFromTrayAsync() => ConnectAsync();

    public Task DisconnectFromTrayAsync() => manager.DisconnectAsync();

    public Task ReconnectFromTrayAsync() => manager.ReconnectNowAsync();

    /// <summary>Selects a profile from the tray menu.</summary>
    public void SelectProfileFromTray(Guid id) => SwitchProfile(id);

    /// <summary>Connects at startup when configured and a target exists.</summary>
    public Task ConnectOnLaunchAsync() => string.IsNullOrWhiteSpace(profiles.Active.Host) ? Task.CompletedTask : ConnectAsync();

    /// <summary>Opens the settings dialog (SPEC §28), optionally prefilled (the first-run flow passes what was typed).</summary>
    public void OpenSettings(SettingsDraft? prefill = null)
    {
        var active = profiles.Active;
        var draft = prefill ?? SettingsDraft.From(config, active);
        var viewModel = new SettingsViewModel(draft, active, profiles, loc, paths.Root);
        if (windows.ShowSettings(viewModel))
        {
            ApplySettings(viewModel.Accepted);
        }
    }

    /// <summary>Shows the minimal first-run setup (SPEC §83) and acts on the choice.</summary>
    public void RunFirstRun()
    {
        var viewModel = new FirstRunViewModel(config, loc);
        switch (windows.ShowFirstRun(viewModel))
        {
            case FirstRunChoice.Connect when viewModel.Result is { IsValid: true } result:
                ApplySettings(result);
                _ = ConnectAsync();
                break;
            case FirstRunChoice.MoreSettings:
                OpenSettings(viewModel.Draft);
                SaveConfiguration(); // the setup has been seen; do not show it again on the next launch
                break;
            default:
                SaveConfiguration();
                break;
        }
    }

    // ---- shutdown ----

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

    // ---- connection ----

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(profiles.Active.Host))
        {
            OpenSettings(); // nothing to connect to yet: let the user enter a target first
            if (string.IsNullOrWhiteSpace(profiles.Active.Host))
            {
                return;
            }
        }

        await manager.ConnectAsync(profiles.Active);
    }

    // ---- settings ----

    /// <summary>Applies a validated settings result: profile, interface, notifications, analytics, logging, language, startup entry.</summary>
    private void ApplySettings(SettingsBuildResult result)
    {
        var index = config.Profiles.FindIndex(p => p.Id == result.Profile.Id);
        config.Profiles[index] = result.Profile;
        config.Interface = result.Interface;
        config.Notifications = result.Notifications;
        config.Analytics = result.Analytics;
        config.LogVerbosity = result.LogVerbosity;
        log.MinimumSeverity = result.LogVerbosity == LogVerbosity.Verbose ? LogSeverity.Debug : LogSeverity.Information;
        ApplyStartupRegistration();
        SaveConfiguration();
        Notice = CanEditProfiles ? string.Empty : loc.Get("settings.appliesNextConnect");

        // May raise LanguageChanged, which re-renders every text.
        loc.SetLanguage(LanguageCodes.Resolve(config.Interface.Language, CultureInfo.CurrentUICulture));
        NotifyProfilesChanged();
        OnPropertyChanged(string.Empty);
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
            Notice = loc.Format("dialog.saveFailed", ex.Message);
        }
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
            Notice = loc.Format("dialog.startupFailed", ex.Message);
        }
    }

    // ---- profiles (SPEC §7, §48) ----

    private IReadOnlyList<LocalizedOption<Guid>> BuildProfileOptions() =>
        profiles.Profiles.Select(p => new LocalizedOption<Guid>(p.Id, p.Name)).ToArray();

    private void NotifyProfilesChanged()
    {
        ProfileOptions = BuildProfileOptions();
        OnPropertyChanged(nameof(SelectedProfileId));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(StatusDetail));
        DeleteProfileCommand.RaiseCanExecuteChanged();
        Statistics.OnProfileChanged();
        ProfilesChanged?.Invoke();
    }

    private void SwitchProfile(Guid id)
    {
        if (!CanEditProfiles)
        {
            Notice = loc.Get("tooltip.profileLocked");
            OnPropertyChanged(nameof(SelectedProfileId)); // snap the selector back
            return;
        }

        try
        {
            profiles.SetActive(id);
        }
        catch (ProfileException ex)
        {
            Notice = ProfileError(ex);
            return;
        }

        Notice = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    private void AddProfile()
    {
        var created = profiles.Add(null, loc.Get("profile.defaultName"));
        profiles.SetActive(created.Id);
        Notice = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
        OpenSettings(); // a new profile has no target yet
    }

    private void CloneProfile()
    {
        var copy = profiles.Clone(profiles.Active.Id, loc.Get("profile.copyName"));
        profiles.SetActive(copy.Id);
        Notice = string.Empty;
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
            Notice = ProfileError(ex);
            return;
        }

        Notice = string.Empty;
        SaveConfiguration();
        NotifyProfilesChanged();
    }

    private string ProfileError(ProfileException ex) =>
        LocalizedText.Issue(loc, new ValidationIssue(IssueSeverity.Error, nameof(Profile), ex.Code, ex.Message));

    // ---- state, traffic and log rendering ----

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

    /// <summary>Re-renders everything that was composed in the previous language (SPEC §30: no restart needed).</summary>
    private void OnLanguageChanged()
    {
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

    private void AppendLog(LogEntry entry) => AppendLogLine(entry);

    private void AppendLogLine(LogEntry entry)
    {
        var text = LogText + AppLog.Format(entry) + Environment.NewLine;
        LogText = text.Length > 60_000 ? text[^40_000..] : text;
    }
}

/// <summary>A combo-box entry whose label is localized but whose value is stable.</summary>
public sealed record LocalizedOption<T>(T Value, string Label);
