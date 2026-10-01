using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ConstantProxy.App.ViewModels;

namespace ConstantProxy.App;

public partial class MainWindow : Window
{
    private bool shutdownDone;

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

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (shutdownDone || DataContext is not MainViewModel viewModel)
        {
            base.OnClosing(e);
            return;
        }

        // Stop ssh (only our own child) before the window goes away; do not block the UI thread while waiting.
        e.Cancel = true;
        await viewModel.ShutdownAsync();
        shutdownDone = true;
        Close();
    }
}
