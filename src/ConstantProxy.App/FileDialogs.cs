using ConstantProxy.App.ViewModels;
using Microsoft.Win32;

namespace ConstantProxy.App;

public sealed class WpfExportDialog : IExportDialog
{
    public string? PickExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export analytics",
            FileName = "constantproxy-export.csv",
            Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".csv",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
