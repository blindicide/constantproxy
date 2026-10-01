using ConstantProxy.App.ViewModels;
using ConstantProxy.Core.Settings;

namespace ConstantProxy.App.Services;

/// <summary>Opens the secondary dialogs. An interface so view models never create windows themselves.</summary>
public interface IWindowService
{
    /// <summary>Shows the settings dialog; returns true when the user accepted valid changes.</summary>
    bool ShowSettings(SettingsViewModel viewModel);

    void ShowDiagnostics(DiagnosticsViewModel viewModel);

    void ShowAbout();

    /// <summary>Shows the first-run setup; returns true when the user chose Connect.</summary>
    FirstRunChoice ShowFirstRun(FirstRunViewModel viewModel);
}

public enum FirstRunChoice
{
    Skip,
    Connect,
    MoreSettings,
}
