using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ConstantProxy.App.ViewModels;

namespace ConstantProxy.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to the tab control itself, not to combo boxes inside it that raise the same routed event.
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.Statistics.SetActive(ReferenceEquals(MainTabs.SelectedItem, StatisticsTab));
    }

    /// <summary>Opens the settings dialog (used by the tray menu).</summary>
    public void ShowSettings()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OpenSettings();
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized
            && Application.Current is App { IsExiting: false } app
            && DataContext is MainViewModel viewModel
            && viewModel.Config.Interface.MinimizeToTray)
        {
            app.HideToTray();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is not App app || app.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true; // the window is only hidden unless the user chose to exit
        if (DataContext is MainViewModel viewModel && viewModel.Config.Interface.CloseToTray)
        {
            app.HideToTray();
        }
        else
        {
            _ = app.RequestExitAsync();
        }
    }
}
