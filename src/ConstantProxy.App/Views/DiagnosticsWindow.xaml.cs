using System.Windows;
using ConstantProxy.App.ViewModels;

namespace ConstantProxy.App.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel viewModel)
        {
            await viewModel.RefreshAsync(); // the OpenSSH version lookup runs off the UI thread
        }
    }
}
