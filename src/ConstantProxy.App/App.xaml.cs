using System.Runtime.InteropServices;
using System.Windows;
using ConstantProxy.App.ViewModels;
using ConstantProxy.Core;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Models;
using Microsoft.Data.Sqlite;
using ConstantProxy.Infrastructure.Analytics;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Logging;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Logging;
using ConstantProxy.Core.Traffic;
using ConstantProxy.Infrastructure.Network;
using ConstantProxy.Infrastructure.Traffic;
using ConstantProxy.Infrastructure.Ssh;

namespace ConstantProxy.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Opens the analytics database. Analytics problems never stop the application: on failure history is simply
    /// unavailable and the user is told why.
    /// </summary>
    private static (IAnalyticsStore? Store, string? Notice) OpenAnalytics(AppPaths paths, AppConfig config, IAppLog log)
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
                ? $"The analytics database was damaged and has been set aside:\n\n{result.BackupPath}\n\nA new empty database is in use."
                : null;
            return (result.Store, notice);
        }
        catch (DatabaseTooNewException ex)
        {
            log.Error("analytics", ex.Message, ex);
            return (null, ex.Message + "\n\nHistory is unavailable in this session.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.Error("analytics", "The analytics database could not be opened.", ex);
            return (null, "The analytics database could not be opened:\n\n" + ex.Message + "\n\nHistory is unavailable in this session.");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1); // write to the parent console when started from a terminal
            Console.WriteLine($"{VersionInfo.ProductName} {VersionInfo.Version}");
            Shutdown(0);
            return;
        }

        var paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        var log = new AppLog(paths.LogFile);
        log.Info("app", $"{VersionInfo.ProductName} {VersionInfo.Version} starting");

        var configService = new ConfigurationService(paths.ConfigFile, log);
        var load = configService.Load();
        if (load.Status == ConfigLoadStatus.RecoveredFromCorruption)
        {
            MessageBox.Show(
                $"The configuration file could not be read and was set aside.\n\n{load.BackupPath}\n\nDefault settings are in use.",
                VersionInfo.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        log.MinimumSeverity = load.Config.LogVerbosity == LogVerbosity.Verbose ? LogSeverity.Debug : LogSeverity.Information;

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

        var (store, notice) = OpenAnalytics(paths, load.Config, log);
        AnalyticsRecorder? recorder = null;
        if (store is not null)
        {
            recorder = new AnalyticsRecorder(store, SystemClock.Instance, log, new AnalyticsSettings { StoreHistory = true, RetentionDays = load.Config.Analytics.RetentionDays });
            recorder.Attach(manager, sampling);
            recorder.Start();
            recorder.RecordApplicationEvent(ConnectionEventType.ApplicationStarted, VersionInfo.Version);
        }

        if (notice is not null)
        {
            MessageBox.Show(notice, VersionInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var statistics = new StatisticsViewModel(store, paths, load.Config, manager, new WpfExportDialog(), log, Dispatcher);
        var viewModel = new MainViewModel(manager, sampling, recorder, statistics, configService, load.Config, log, Dispatcher);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }
}
