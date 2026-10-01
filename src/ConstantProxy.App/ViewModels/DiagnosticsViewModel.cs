using System.Windows;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Diagnostics;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Settings;
using ConstantProxy.Core.Ssh;
using ConstantProxy.Infrastructure;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Ssh;

namespace ConstantProxy.App.ViewModels;

/// <summary>The diagnostics view (SPEC §65, §75): a copyable report plus the generated ssh command.</summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly ConnectionManager manager;
    private readonly AppConfig config;
    private readonly AppPaths paths;
    private readonly LocalizationService loc;
    private string? sshVersion;
    private bool includePrivate;
    private string reportText = string.Empty;
    private string statusText = string.Empty;

    public DiagnosticsViewModel(ConnectionManager manager, AppConfig config, AppPaths paths, LocalizationService loc)
    {
        this.manager = manager;
        this.config = config;
        this.paths = paths;
        this.loc = loc;
        CopyCommand = new RelayCommand(() => { Copy(); return Task.CompletedTask; });
        RefreshCommand = new RelayCommand(RefreshAsync);
        RebuildReport();
    }

    public RelayCommand CopyCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public string ReportText { get => reportText; private set => SetProperty(ref reportText, value); }

    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

    public bool IncludePrivate
    {
        get => includePrivate;
        set
        {
            if (SetProperty(ref includePrivate, value))
            {
                RebuildReport();
            }
        }
    }

    /// <summary>Looks up the OpenSSH version off the UI thread and refreshes the report when it arrives.</summary>
    public async Task RefreshAsync()
    {
        StatusText = loc.Get("diag.checking");
        var path = new SshLocator().Resolve(config.ActiveProfile.SshExecutable);
        sshVersion = path is null ? null : await SshVersionProbe.QueryAsync(path, TimeSpan.FromSeconds(5));
        StatusText = string.Empty;
        RebuildReport();
    }

    private void RebuildReport()
    {
        var profile = manager.ActiveProfile ?? config.ActiveProfile;
        var resolved = new SshLocator().Resolve(profile.SshExecutable);
        var info = new DiagnosticsInfo
        {
            AppVersion = Core.VersionInfo.Version,
            OsVersion = EnvironmentInfo.OsVersion,
            RuntimeVersion = EnvironmentInfo.RuntimeVersion,
            ConfiguredSsh = profile.SshExecutable,
            ResolvedSshPath = resolved,
            SshVersion = sshVersion,
            ProfileName = profile.Name,
            SshTarget = profile.Host,
            SshPid = manager.CurrentProcess is { ExitTimeUtc: null } running ? running.Pid : null,
            State = manager.State,
            SocksEndpoint = SshArgumentBuilder.FormatEndpoint(profile.BindAddress, profile.Port),
            SessionStartUtc = manager.SessionStartedUtc,
            ReconnectCount = manager.ReconnectCount,
            TrafficMode = profile.TrafficMode.ToString(),
            DatabasePath = string.IsNullOrWhiteSpace(config.Analytics.DatabasePath) ? paths.DatabaseFile : config.Analytics.DatabasePath,
            LogPath = paths.LogsDirectory,
            ConfigPath = paths.ConfigFile,
            Language = loc.Language,
            LastFailure = manager.LastFailure?.Code,
            SshCommand = SshCommandPreview.Build(profile, redactTarget: !includePrivate, resolved),
        };
        ReportText = DiagnosticsReport.Format(info, includePrivate);
    }

    private void Copy()
    {
        try
        {
            Clipboard.SetText(ReportText);
            StatusText = loc.Get("diag.copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be locked by another program for a moment; the text stays selectable in the window.
            StatusText = string.Empty;
        }
    }
}
