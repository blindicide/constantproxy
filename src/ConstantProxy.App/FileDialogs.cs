using ConstantProxy.App.ViewModels;
using Microsoft.Win32;

namespace ConstantProxy.App;

public sealed class WpfExportDialog : IExportDialog
{
    public string? PickExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationSource.Instance.Service.Get("export.title"),
            FileName = "constantproxy-export.csv",
            Filter = LocalizationSource.Instance.Service.Get("export.filter"),
            AddExtension = true,
            DefaultExt = ".csv",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
