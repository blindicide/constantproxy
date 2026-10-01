using System.ComponentModel;
using System.Windows;
using ConstantProxy.App.ViewModels;

namespace ConstantProxy.App;

public partial class MainWindow : Window
{
    private bool shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
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
