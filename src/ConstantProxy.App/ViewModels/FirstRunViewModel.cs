using System.Windows.Input;
using ConstantProxy.App.Services;
using ConstantProxy.Core.Localization;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Settings;
using ConstantProxy.Core.Validation;
using ConstantProxy.Infrastructure.Ssh;
using Microsoft.Win32;

namespace ConstantProxy.App.ViewModels;

/// <summary>The minimal setup shown on first launch (SPEC §83): target, port and ssh location, nothing else.</summary>
public sealed class FirstRunViewModel : ObservableObject
{
    private readonly ILocalizer loc;
    private readonly Profile profile;
    private readonly AppConfig config;
    private string host = string.Empty;
    private string port;
    private string sshExecutable = string.Empty;
    private string errors = string.Empty;

    public FirstRunViewModel(AppConfig config, ILocalizer loc)
    {
        this.config = config;
        this.loc = loc;
        profile = config.ActiveProfile;
        port = profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var detected = new SshLocator().Resolve(string.Empty);
        DetectedText = detected is null ? loc.Get("settings.sshNotDetected") : loc.Format("settings.sshDetected", detected);

        ConnectCommand = new RelayCommand(() => { Finish(FirstRunChoice.Connect); return Task.CompletedTask; });
        MoreCommand = new RelayCommand(() => { Finish(FirstRunChoice.MoreSettings); return Task.CompletedTask; });
        SkipCommand = new RelayCommand(() => { CloseRequested?.Invoke(FirstRunChoice.Skip); return Task.CompletedTask; });
        BrowseCommand = new RelayCommand(() => { Browse(); return Task.CompletedTask; });
    }

    public event Action<FirstRunChoice>? CloseRequested;

    public FirstRunChoice Choice { get; set; } = FirstRunChoice.Skip;

    public ICommand ConnectCommand { get; }

    public ICommand MoreCommand { get; }

    public ICommand SkipCommand { get; }

    public ICommand BrowseCommand { get; }

    public string DetectedText { get; }

    public string Host { get => host; set => SetProperty(ref host, value); }

    public string Port { get => port; set => SetProperty(ref port, value); }

    public string SshExecutable { get => sshExecutable; set => SetProperty(ref sshExecutable, value); }

    public string Errors { get => errors; private set => SetProperty(ref errors, value); }

    /// <summary>The validated settings, available after <see cref="FirstRunChoice.Connect"/> or <see cref="FirstRunChoice.MoreSettings"/>.</summary>
    public SettingsBuildResult? Result { get; private set; }

    /// <summary>What the user typed, as a settings draft (used to prefill the full settings dialog).</summary>
    public SettingsDraft? Draft { get; private set; }

    private void Finish(FirstRunChoice choice)
    {
        var draft = SettingsDraft.From(config, profile);
        draft.Host = Host;
        draft.Port = Port;
        draft.SshExecutable = SshExecutable;
        var result = draft.Build(profile, new ProfileRepository(config), System.IO.File.Exists, System.IO.Directory.Exists);

        // "More settings" is allowed with incomplete input: the full dialog will point at what is missing.
        if (!result.IsValid && choice == FirstRunChoice.Connect)
        {
            Errors = string.Join(Environment.NewLine, result.Errors.Select(e => LocalizedText.Issue(loc, e.Issue)).Distinct());
            return;
        }

        Result = result;
        Draft = draft;
        CloseRequested?.Invoke(choice);
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Filter = loc.Get("settings.sshFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            SshExecutable = dialog.FileName;
        }
    }
}
