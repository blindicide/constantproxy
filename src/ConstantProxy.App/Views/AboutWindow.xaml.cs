using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ConstantProxy.Core;

namespace ConstantProxy.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var loc = LocalizationSource.Instance.Service;
        VersionText.Text = loc.Format("about.version", VersionInfo.Version);
        LicenseText.Text = loc.Format("about.license", VersionInfo.License);
        if (Uri.TryCreate(VersionInfo.RepositoryUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            RepositoryLink.NavigateUri = uri;
            RepositoryLink.ToolTip = uri.ToString();
        }
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Only the HTTPS repository address from build metadata is ever opened.
        if (e.Uri is { Scheme: "https" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // no default browser configured; the address is visible in the tooltip
            }
        }

        e.Handled = true;
    }
}
