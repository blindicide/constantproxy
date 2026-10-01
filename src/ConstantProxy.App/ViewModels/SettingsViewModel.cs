using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Settings;
using ConstantProxy.Core.Validation;
using ConstantProxy.Infrastructure.Ssh;
using Microsoft.Win32;

namespace ConstantProxy.App.ViewModels;

/// <summary>
/// The settings dialog (SPEC §28). Edits a <see cref="SettingsDraft"/>, re-validates on every keystroke and shows each
/// problem next to its field (SPEC §49); the real configuration is only touched when the user presses OK.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDataErrorInfo
{
    private readonly ILocalizer loc;
    private readonly Profile original;
    private readonly ProfileRepository repository;
    private readonly string dataFolder;
    private SettingsBuildResult result;

    public SettingsViewModel(SettingsDraft draft, Profile original, ProfileRepository repository, ILocalizer loc, string dataFolder)
    {
        Draft = draft;
        this.original = original;
        this.repository = repository;
        this.loc = loc;
        this.dataFolder = dataFolder;
        result = draft.Build(original, repository, File.Exists, Directory.Exists);

        OkCommand = new RelayCommand(() => { Accept(); return Task.CompletedTask; }, () => result.IsValid);
        CancelCommand = new RelayCommand(() => { CloseRequested?.Invoke(false); return Task.CompletedTask; });
        BrowseSshCommand = new RelayCommand(() => { BrowseSsh(); return Task.CompletedTask; });
        BrowseDatabaseCommand = new RelayCommand(() => { BrowseDatabase(); return Task.CompletedTask; });
        OpenDataFolderCommand = new RelayCommand(() => { OpenDataFolder(); return Task.CompletedTask; });

        RetentionOptions = SettingsDraft.RetentionChoices.Select(d => new LocalizedOption<int>(d, loc.Get($"retention.{d}"))).ToArray();
        LanguageOptions = new[]
        {
            new LocalizedOption<string>(LanguageCodes.Auto, loc.Get("language.auto")),
            new LocalizedOption<string>(LanguageCodes.English, loc.Get("language.en")),
            new LocalizedOption<string>(LanguageCodes.Russian, loc.Get("language.ru")),
        };
        TrafficModeOptions = new[]
        {
            new LocalizedOption<TrafficMode>(TrafficMode.Bridge, loc.Get("traffic.mode.bridge")),
            new LocalizedOption<TrafficMode>(TrafficMode.Off, loc.Get("traffic.mode.off")),
        };
        LogVerbosityOptions = new[]
        {
            new LocalizedOption<LogVerbosity>(LogVerbosity.Normal, loc.Get("log.normal")),
            new LocalizedOption<LogVerbosity>(LogVerbosity.Verbose, loc.Get("log.verbose")),
        };
        DetectedSsh = DetectSsh();
    }

    /// <summary>Raised with true when the dialog should close accepting the changes, false when cancelled.</summary>
    public event Action<bool>? CloseRequested;

    public SettingsDraft Draft { get; }

    public SettingsBuildResult Result => result;

    public ICommand OkCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand BrowseSshCommand { get; }

    public ICommand BrowseDatabaseCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public IReadOnlyList<LocalizedOption<int>> RetentionOptions { get; }

    public IReadOnlyList<LocalizedOption<string>> LanguageOptions { get; }

    public IReadOnlyList<LocalizedOption<TrafficMode>> TrafficModeOptions { get; }

    public IReadOnlyList<LocalizedOption<LogVerbosity>> LogVerbosityOptions { get; }

    public string DetectedSsh { get; private set; }

    public bool IsValid => result.IsValid;

    /// <summary>Messages for all current errors, listed under the form so nothing is hidden on another tab.</summary>
    public string ErrorSummary => result.IsValid
        ? string.Empty
        : loc.Get("settings.fixErrors") + Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Select(e => "• " + LocalizedText.Issue(loc, e.Issue)).Distinct());

    public string WarningSummary => string.Join(Environment.NewLine, result.Warnings.Select(w => loc.Format("settings.warning", LocalizedText.Issue(loc, w.Issue))).Distinct());

    /// <summary>The read-only generated command (SPEC §75); the target is shown because the user is editing it.</summary>
    public string CommandPreview => SshCommandPreview.Build(result.Profile, redactTarget: false, ResolvedSsh());

    public string this[string columnName]
    {
        get
        {
            var messages = result.Issues
                .Where(i => i.Field == columnName && i.Issue.Severity == IssueSeverity.Error)
                .Select(i => LocalizedText.Issue(loc, i.Issue))
                .Distinct();
            return string.Join(" ", messages);
        }
    }

    public string Error => string.Empty;

    // ---- Connection ----
    public string ProfileName { get => Draft.ProfileName; set => Edit(() => Draft.ProfileName = value, nameof(ProfileName)); }

    public string Host { get => Draft.Host; set => Edit(() => Draft.Host = value, nameof(Host)); }

    public string BindAddress { get => Draft.BindAddress; set => Edit(() => Draft.BindAddress = value, nameof(BindAddress)); }

    public string Port { get => Draft.Port; set => Edit(() => Draft.Port = value, nameof(Port)); }

    public string SshExecutable { get => Draft.SshExecutable; set => Edit(() => Draft.SshExecutable = value, nameof(SshExecutable)); }

    public bool IPv4Only { get => Draft.IPv4Only; set => Edit(() => Draft.IPv4Only = value, nameof(IPv4Only)); }

    public string ServerAliveInterval { get => Draft.ServerAliveInterval; set => Edit(() => Draft.ServerAliveInterval = value, nameof(ServerAliveInterval)); }

    public string ServerAliveCountMax { get => Draft.ServerAliveCountMax; set => Edit(() => Draft.ServerAliveCountMax = value, nameof(ServerAliveCountMax)); }

    public bool ExitOnForwardFailure { get => Draft.ExitOnForwardFailure; set => Edit(() => Draft.ExitOnForwardFailure = value, nameof(ExitOnForwardFailure)); }

    public string StartupTimeoutSeconds { get => Draft.StartupTimeoutSeconds; set => Edit(() => Draft.StartupTimeoutSeconds = value, nameof(StartupTimeoutSeconds)); }

    // ---- Reconnect ----
    public bool AutoReconnect { get => Draft.AutoReconnect; set => Edit(() => Draft.AutoReconnect = value, nameof(AutoReconnect)); }

    public string ReconnectDelays { get => Draft.ReconnectDelays; set => Edit(() => Draft.ReconnectDelays = value, nameof(ReconnectDelays)); }

    public string MaxDelaySeconds { get => Draft.MaxDelaySeconds; set => Edit(() => Draft.MaxDelaySeconds = value, nameof(MaxDelaySeconds)); }

    public string HealthyResetSeconds { get => Draft.HealthyResetSeconds; set => Edit(() => Draft.HealthyResetSeconds = value, nameof(HealthyResetSeconds)); }

    public string JitterPercent { get => Draft.JitterPercent; set => Edit(() => Draft.JitterPercent = value, nameof(JitterPercent)); }

    // ---- Monitoring ----
    public bool HealthEnabled { get => Draft.HealthEnabled; set => Edit(() => Draft.HealthEnabled = value, nameof(HealthEnabled)); }

    public string HealthHost { get => Draft.HealthHost; set => Edit(() => Draft.HealthHost = value, nameof(HealthHost)); }

    public string HealthPort { get => Draft.HealthPort; set => Edit(() => Draft.HealthPort = value, nameof(HealthPort)); }

    public string HealthInterval { get => Draft.HealthInterval; set => Edit(() => Draft.HealthInterval = value, nameof(HealthInterval)); }

    public string HealthTimeout { get => Draft.HealthTimeout; set => Edit(() => Draft.HealthTimeout = value, nameof(HealthTimeout)); }

    public string FailureThreshold { get => Draft.FailureThreshold; set => Edit(() => Draft.FailureThreshold = value, nameof(FailureThreshold)); }

    public bool ReconnectOnFailure { get => Draft.ReconnectOnFailure; set => Edit(() => Draft.ReconnectOnFailure = value, nameof(ReconnectOnFailure)); }

    public string ReconnectAfterFailures { get => Draft.ReconnectAfterFailures; set => Edit(() => Draft.ReconnectAfterFailures = value, nameof(ReconnectAfterFailures)); }

    // ---- Analytics ----
    public bool StoreHistory { get => Draft.StoreHistory; set => Edit(() => Draft.StoreHistory = value, nameof(StoreHistory)); }

    public int RetentionDays { get => Draft.RetentionDays; set => Edit(() => Draft.RetentionDays = value, nameof(RetentionDays)); }

    public string DatabasePath { get => Draft.DatabasePath; set => Edit(() => Draft.DatabasePath = value, nameof(DatabasePath)); }

    // ---- Interface ----
    public string Language { get => Draft.Language; set { if (!string.IsNullOrEmpty(value)) { Edit(() => Draft.Language = value, nameof(Language)); } } }

    public bool StartMinimized { get => Draft.StartMinimized; set => Edit(() => Draft.StartMinimized = value, nameof(StartMinimized)); }

    public bool CloseToTray { get => Draft.CloseToTray; set => Edit(() => Draft.CloseToTray = value, nameof(CloseToTray)); }

    public bool MinimizeToTray { get => Draft.MinimizeToTray; set => Edit(() => Draft.MinimizeToTray = value, nameof(MinimizeToTray)); }

    public bool StartWithWindows { get => Draft.StartWithWindows; set => Edit(() => Draft.StartWithWindows = value, nameof(StartWithWindows)); }

    public bool ConnectOnLaunch { get => Draft.ConnectOnLaunch; set => Edit(() => Draft.ConnectOnLaunch = value, nameof(ConnectOnLaunch)); }

    // ---- Notifications ----
    public bool NotifyOnFailure { get => Draft.NotifyOnFailure; set => Edit(() => Draft.NotifyOnFailure = value, nameof(NotifyOnFailure)); }

    public bool NotifyOnRecovery { get => Draft.NotifyOnRecovery; set => Edit(() => Draft.NotifyOnRecovery = value, nameof(NotifyOnRecovery)); }

    public string MinimumOutageSeconds { get => Draft.MinimumOutageSeconds; set => Edit(() => Draft.MinimumOutageSeconds = value, nameof(MinimumOutageSeconds)); }

    // ---- Advanced ----
    public string AdditionalArguments { get => Draft.AdditionalArguments; set => Edit(() => Draft.AdditionalArguments = value, nameof(AdditionalArguments)); }

    public bool BatchMode { get => Draft.BatchMode; set => Edit(() => Draft.BatchMode = value, nameof(BatchMode)); }

    public TrafficMode TrafficMode { get => Draft.TrafficMode; set => Edit(() => Draft.TrafficMode = value, nameof(TrafficMode)); }

    public LogVerbosity LogVerbosity { get => Draft.LogVerbosity; set => Edit(() => Draft.LogVerbosity = value, nameof(LogVerbosity)); }

    /// <summary>The validated result to apply after the dialog was accepted.</summary>
    public SettingsBuildResult Accepted => result;

    private void Edit(Action change, string property)
    {
        change();
        OnPropertyChanged(property);
        Revalidate();
    }

    private void Revalidate()
    {
        result = Draft.Build(original, repository, File.Exists, Directory.Exists);
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ErrorSummary));
        OnPropertyChanged(nameof(WarningSummary));
        OnPropertyChanged(nameof(CommandPreview));
        OnPropertyChanged(string.Empty); // refreshes every field's error indicator
        ((RelayCommand)OkCommand).RaiseCanExecuteChanged();
    }

    private void Accept()
    {
        if (result.IsValid)
        {
            CloseRequested?.Invoke(true);
        }
    }

    private string? ResolvedSsh() => new SshLocator().Resolve(Draft.SshExecutable);

    private string DetectSsh()
    {
        var found = new SshLocator().Resolve(string.Empty);
        return found is null ? loc.Get("settings.sshNotDetected") : loc.Format("settings.sshDetected", found);
    }

    private void BrowseSsh()
    {
        var dialog = new OpenFileDialog { Filter = loc.Get("settings.sshFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            SshExecutable = dialog.FileName;
        }
    }

    private void BrowseDatabase()
    {
        var dialog = new SaveFileDialog { Filter = loc.Get("settings.dbFilter"), OverwritePrompt = false, FileName = "constantproxy.db" };
        if (dialog.ShowDialog() == true)
        {
            DatabasePath = dialog.FileName;
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(dataFolder) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Nothing sensible to do here; the Statistics tab reports the same failure to the user.
        }
    }
}
