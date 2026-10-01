using System.Runtime.InteropServices;
using System.Windows;
using ConstantProxy.App.ViewModels;
using ConstantProxy.Core;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Connection;
using System.Globalization;
using ConstantProxy.Core.Desktop;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Logging;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Traffic;
using ConstantProxy.Infrastructure.Analytics;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Desktop;
using ConstantProxy.Infrastructure.Logging;
using ConstantProxy.Infrastructure.Network;
using ConstantProxy.Infrastructure.Ssh;
using ConstantProxy.Infrastructure.Traffic;
using Microsoft.Data.Sqlite;

namespace ConstantProxy.App;

public partial class App : Application
{
    private SingleInstanceGuard? instanceGuard;
    private TrayController? tray;
    private NotificationService? notifications;
    private MainViewModel? viewModel;
    private MainWindow? window;
    private AppLog? log;
    private LocalizationService? loc;
    private bool exiting;
    private bool hintShown;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>True once the application is really shutting down (as opposed to hiding to the tray).</summary>
    public bool IsExiting => exiting;

    /// <summary>
    /// Opens the analytics database. Analytics problems never stop the application: on failure history is simply
    /// unavailable and the user is told why.
    /// </summary>
    private static (IAnalyticsStore? Store, string? Notice) OpenAnalytics(AppPaths paths, AppConfig config, IAppLog log, ILocalizer loc)
    {
        if (!config.Analytics.StoreHistory)
        {
            return (null, null);
        }

        var path = string.IsNullOrWhiteSpace(config.Analytics.DatabasePath) ? paths.DatabaseFile : config.Analytics.DatabasePath.Trim();
        try
        {
            var result = SqliteAnalyticsStore.Open(path, log);
            var notice = result.Status == StoreOpenStatus.RecoveredFromCorruption
                ? loc.Format("dialog.analyticsDamaged", result.BackupPath ?? string.Empty)
                : null;
            return (result.Store, notice);
        }
        catch (DatabaseTooNewException ex)
        {
            log.Error("analytics", ex.Message, ex);
            return (null, loc.Format("dialog.analyticsTooNew", ex.DatabaseVersion, ex.SupportedVersion));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.Error("analytics", "The analytics database could not be opened.", ex);
            return (null, loc.Format("dialog.analyticsOpenFailed", ex.Message));
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Closing the main window may only hide it; exiting is always an explicit decision (tray menu or Exit).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1); // write to the parent console when started from a terminal
            Console.WriteLine($"{VersionInfo.ProductName} {VersionInfo.Version}");
            Shutdown(0);
            return;
        }

        var instanceName = SingleInstanceGuard.NameFor(Environment.UserName);
        instanceGuard = SingleInstanceGuard.TryAcquire(instanceName);
        if (instanceGuard is null)
        {
            // Another instance owns the tunnel: ask it to show itself instead of starting a duplicate.
            SingleInstanceGuard.TryActivateExisting(instanceName, TimeSpan.FromSeconds(3));
            Shutdown(0);
            return;
        }

        instanceGuard.StartListening(() => Dispatcher.BeginInvoke(() => ShowMainWindow()));

        var paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        log = new AppLog(paths.LogFile);
        var startedByWindows = e.Args.Contains(StartupManager.StartupArgument, StringComparer.OrdinalIgnoreCase);
        log.Info("app", $"{VersionInfo.ProductName} {VersionInfo.Version} starting" + (startedByWindows ? " (started with Windows)" : string.Empty));

        var configService = new ConfigurationService(paths.ConfigFile, log);
        var load = configService.Load();
        var config = load.Config;

        // Windows UI language on first launch, a manual choice afterwards (SPEC §30).
        loc = new LocalizationService(LanguageCodes.Resolve(config.Interface.Language, CultureInfo.CurrentUICulture));
        LocalizationSource.Instance.Attach(loc);

        if (load.Status == ConfigLoadStatus.RecoveredFromCorruption)
        {
            MessageBox.Show(
                loc.Format("dialog.configCorrupt", load.BackupPath ?? string.Empty),
                VersionInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        log.MinimumSeverity = config.LogVerbosity == LogVerbosity.Verbose ? LogSeverity.Debug : LogSeverity.Information;

        var bridge = new BridgeTrafficMonitor(log);
        var manager = new ConnectionManager(
            new SshProcessLauncher(),
            new ListenerStartupVerifier(SystemClock.Instance),
            SystemClock.Instance,
            log,
            portProbe: new TcpPortProbe(),
            socksProbe: new Socks5Probe(),
            trafficMonitor: bridge);
        var sampling = new TrafficSamplingService(bridge);

        var (store, notice) = OpenAnalytics(paths, config, log, loc);
        AnalyticsRecorder? recorder = null;
        if (store is not null)
        {
            recorder = new AnalyticsRecorder(store, SystemClock.Instance, log, new AnalyticsSettings { StoreHistory = true, RetentionDays = config.Analytics.RetentionDays });
            recorder.Attach(manager, sampling);
            recorder.Start();
            recorder.RecordApplicationEvent(ConnectionEventType.ApplicationStarted, VersionInfo.Version);
        }

        if (notice is not null)
        {
            MessageBox.Show(notice, VersionInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StartupManager? startup = null;
        if (Environment.ProcessPath is { Length: > 0 } exePath)
        {
            startup = new StartupManager(new WindowsStartupRegistry(), exePath);
            RepairStartupEntry(startup, log);
        }

        var statistics = new StatisticsViewModel(store, paths, config, manager, new WpfExportDialog(), loc, log, Dispatcher);
        viewModel = new MainViewModel(manager, sampling, recorder, statistics, startup, loc, configService, config, log, Dispatcher);

        window = new MainWindow { DataContext = viewModel };
        MainWindow = window;

        tray = new TrayController(
            BuildTrayTexts(),
            new TrayActions(
                Open: () => ShowMainWindow(),
                Connect: () => _ = viewModel.ConnectFromTrayAsync(),
                Disconnect: () => _ = viewModel.DisconnectFromTrayAsync(),
                Reconnect: () => _ = viewModel.ReconnectFromTrayAsync(),
                Settings: () => ShowMainWindow(openSettings: true),
                Exit: () => _ = RequestExitAsync()));
        viewModel.StateChangedForTray += _ => RefreshTray();
        loc.LanguageChanged += () => Dispatcher.BeginInvoke(() =>
        {
            tray?.SetTexts(BuildTrayTexts());
            RefreshTray();
        });

        RefreshTray();

        notifications = new NotificationService(() => config.Notifications, SystemClock.Instance, n => Dispatcher.BeginInvoke(() => ShowNotification(n)));
        notifications.Attach(manager);

        SessionEnding += OnSessionEnding;

        if (!config.Interface.StartMinimized)
        {
            window.Show();
        }

        if (config.Interface.ConnectOnLaunch)
        {
            _ = viewModel.ConnectOnLaunchAsync();
        }
    }

    private TrayTexts BuildTrayTexts() => new(
        loc!.Get("tray.open"), loc.Get("tray.connect"), loc.Get("tray.disconnect"),
        loc.Get("tray.reconnect"), loc.Get("tray.settings"), loc.Get("tray.exit"));

    private void RefreshTray()
    {
        if (viewModel is null || tray is null || loc is null)
        {
            return;
        }

        tray.Update(viewModel.CurrentState, loc.Format("tray.tooltip", LocalizedText.State(loc, viewModel.CurrentState)));
    }

    /// <summary>Brings the main window to the foreground, restoring it from the tray or a minimized state.</summary>
    public void ShowMainWindow() => ShowMainWindow(openSettings: false);

    public void ShowMainWindow(bool openSettings)
    {
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true; // Windows only lets a foreground-eligible window take focus; toggling Topmost is the usual nudge
        window.Topmost = false;
        window.Focus();
        if (openSettings)
        {
            window.ShowSettings();
        }
    }

    /// <summary>Hides the window into the notification area, telling the user once where the application went.</summary>
    public void HideToTray()
    {
        window?.Hide();
        if (!hintShown && tray is not null)
        {
            hintShown = true;
            tray.ShowBalloon(VersionInfo.ProductName, loc?.Get("tray.hint") ?? string.Empty, System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>The one real exit path: stop the tunnel (only our own ssh), flush analytics, then end the process.</summary>
    public async Task RequestExitAsync()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        try
        {
            if (viewModel is not null)
            {
                await viewModel.ShutdownAsync();
            }
        }
        finally
        {
            ReleaseDesktopResources();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseDesktopResources();
        base.OnExit(e);
    }

    private void ReleaseDesktopResources()
    {
        notifications?.Dispose();
        notifications = null;
        tray?.Dispose();
        tray = null;
        instanceGuard?.Dispose();
        instanceGuard = null;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down: stop our ssh now, without waiting on the UI thread.
        if (exiting || viewModel is null)
        {
            return;
        }

        exiting = true;
        viewModel.StopUiActivity();
        try
        {
            Task.Run(viewModel.ShutdownCoreAsync).Wait(TimeSpan.FromSeconds(8));
        }
        catch (AggregateException ex)
        {
            log?.Error("app", "Shutdown during session end failed.", ex);
        }
    }

    private void ShowNotification(AppNotification notification)
    {
        if (tray is null)
        {
            return;
        }

        var content = NotificationText.Compose(notification, loc!);
        var icon = content.Severity switch
        {
            NotificationSeverity.Error => System.Windows.Forms.ToolTipIcon.Error,
            NotificationSeverity.Warning => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Info,
        };
        tray.ShowBalloon(content.Title, content.Message, icon);
    }

    private static void RepairStartupEntry(StartupManager startup, IAppLog log)
    {
        try
        {
            if (startup.RepairIfNeeded())
            {
                log.Info("app", "The Windows startup entry was updated to point at this executable.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            log.Warn("app", "Could not check the Windows startup entry.", ex);
        }
    }
}
