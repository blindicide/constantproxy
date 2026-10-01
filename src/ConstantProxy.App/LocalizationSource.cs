using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using ConstantProxy.Core.Localization;

namespace ConstantProxy.App;

/// <summary>
/// Bridges <see cref="LocalizationService"/> to XAML. Bindings to its indexer refresh when the language changes, so
/// every label updates immediately (SPEC §30). This is the only singleton: XAML needs a static access point.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService Service { get; private set; } = new();

    public string this[string key] => Service.Get(key);

    public void Attach(LocalizationService service)
    {
        Service.LanguageChanged -= OnChanged;
        Service = service;
        Service.LanguageChanged += OnChanged;
        Raise();
    }

    private void OnChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Raise();
        }
        else
        {
            dispatcher.BeginInvoke(Raise);
        }
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>XAML usage: <c>Content="{loc:Loc button.connect}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
