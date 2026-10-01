using System.Runtime.InteropServices;
using System.Windows;
using ConstantProxy.App.ViewModels;
using ConstantProxy.Core;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Logging;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Logging;
using ConstantProxy.Infrastructure.Ssh;

namespace ConstantProxy.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

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

        log.MinimumSeverity = load.Config.LogVerbosity == Core.Models.LogVerbosity.Verbose ? LogSeverity.Debug : LogSeverity.Information;

        var manager = new ConnectionManager(
            new SshProcessLauncher(),
            new ProcessAliveStartupVerifier(SystemClock.Instance, TimeSpan.FromSeconds(2)),
            SystemClock.Instance,
            log);

        var viewModel = new MainViewModel(manager, configService, load.Config, log, Dispatcher);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }
}
