using System.Windows;
using ConstantProxy.App.ViewModels;
using ConstantProxy.App.Views;

namespace ConstantProxy.App.Services;

public sealed class WpfWindowService : IWindowService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public bool ShowSettings(SettingsViewModel viewModel)
    {
        var window = new SettingsWindow { DataContext = viewModel, Owner = Owner };
        viewModel.CloseRequested += accepted =>
        {
            window.DialogResult = accepted;
        };
        return window.ShowDialog() == true;
    }

    public void ShowDiagnostics(DiagnosticsViewModel viewModel)
    {
        var window = new DiagnosticsWindow { DataContext = viewModel, Owner = Owner };
        window.ShowDialog();
    }

    public void ShowAbout()
    {
        new AboutWindow { Owner = Owner }.ShowDialog();
    }

    public FirstRunChoice ShowFirstRun(FirstRunViewModel viewModel)
    {
        var window = new FirstRunWindow { DataContext = viewModel, Owner = Owner };
        viewModel.CloseRequested += choice =>
        {
            viewModel.Choice = choice;
            window.DialogResult = choice == FirstRunChoice.Skip ? false : true;
        };
        window.ShowDialog();
        return viewModel.Choice;
    }
}
