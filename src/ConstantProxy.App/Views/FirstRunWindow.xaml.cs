using System.Windows;

namespace ConstantProxy.App.Views;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => HostBox.Focus();
    }
}
