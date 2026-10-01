using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Logging;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Presentation;
using ConstantProxy.Core.Ssh;
using ConstantProxy.Core.Validation;
using ConstantProxy.Infrastructure.Config;
using ConstantProxy.Infrastructure.Logging;

namespace ConstantProxy.App.ViewModels;

/// <summary>
/// Presentation logic for the main window. It never touches processes or sockets: all of that lives behind
/// <see cref="ConnectionManager"/> (SPEC §60).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConnectionManager manager;
    private readonly ConfigurationService configuration;
    private readonly AppConfig config;
    private readonly AppLog log;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;

    private ConnectionState state = ConnectionState.Disconnected;
    private string sessionDuration = DurationFormatter.Format(TimeSpan.Zero);
    private string statusDetail = string.Empty;
    private string validationText = string.Empty;
    private string logText = string.Empty;
    private int reconnectCount;
    private string latencyText = "-";
    private string failureDetails = string.Empty;
    private bool closing;

    private string host;
    private string bindAddress;
    private string port;
    private string sshExecutable;
    private bool ipv4Only;
    private string serverAliveInterval;
    private string serverAliveCountMax;
    private string additionalArguments;
    private bool autoReconnect;
    private bool healthEnabled;
    private string healthHost;
    private string healthPort;

    public MainViewModel(ConnectionManager manager, ConfigurationService configuration, AppConfig config, AppLog log, Dispatcher dispatcher)
    {
        this.manager = manager;
        this.configuration = configuration;
        this.config = config;
        this.log = log;
        this.dispatcher = dispatcher;

        var p = config.ActiveProfile;
        host = p.Host;
        bindAddress = p.BindAddress;
        port = p.Port.ToString(CultureInfo.InvariantCulture);
        sshExecutable = p.SshExecutable;
        ipv4Only = p.IPv4Only;
        serverAliveInterval = p.ServerAliveInterval.ToString(CultureInfo.InvariantCulture);
        serverAliveCountMax = p.ServerAliveCountMax.ToString(CultureInfo.InvariantCulture);
        additionalArguments = CommandLineSplitter.Join(p.AdditionalArguments);
        autoReconnect = p.Reconnect.Enabled;
        healthEnabled = p.Monitoring.Enabled;
        healthHost = p.Monitoring.TargetHost;
        healthPort = p.Monitoring.TargetPort.ToString(CultureInfo.InvariantCulture);

        ConnectCommand = new RelayCommand(ConnectAsync, () => State is ConnectionState.Disconnected or ConnectionState.Failed);
        DisconnectCommand = new RelayCommand(manager.DisconnectAsync, () => State != ConnectionState.Disconnected);
        ReconnectCommand = new RelayCommand(async () => { await manager.ReconnectNowAsync(); }, () => State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting);

        manager.StateChanged += change => dispatcher.BeginInvoke(() => OnStateChanged(change));
        manager.HealthChecked += _ => dispatcher.BeginInvoke(RefreshSession);
        log.EntryAdded += entry => dispatcher.BeginInvoke(() => AppendLog(entry));
        foreach (var entry in log.Snapshot())
        {
            AppendLogLine(entry);
        }

        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshSession();
    }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand ReconnectCommand { get; }

    public string VersionText => $"v{Core.VersionInfo.Version}";

    public ConnectionState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(StateGlyph));
                OnPropertyChanged(nameof(Endpoint));
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ReconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // Phase 6 moves these literals into localization resources.
    public string StateText => State.ToString();

    public string StateGlyph => State switch
    {
        ConnectionState.Connected => "●",
        ConnectionState.Degraded => "▲",
        ConnectionState.Failed => "✖",
        ConnectionState.Disconnected => "○",
        _ => "◌",
    };

    /// <summary>Colour is never the only signal: the glyph and text label always accompany it (SPEC §27).</summary>
    public Brush StateBrush => State switch
    {
        ConnectionState.Connected => Brushes.SeaGreen,
        ConnectionState.Starting or ConnectionState.Connecting => Brushes.SteelBlue,
        ConnectionState.Degraded => Brushes.Goldenrod,
        ConnectionState.Reconnecting or ConnectionState.Stopping => Brushes.DarkOrange,
        ConnectionState.Failed => Brushes.Firebrick,
        _ => Brushes.Gray,
    };

    public string SessionDuration
    {
        get => sessionDuration;
        private set => SetProperty(ref sessionDuration, value);
    }

    public string StatusDetail
    {
        get => statusDetail;
        private set => SetProperty(ref statusDetail, value);
    }

    public string ValidationText
    {
        get => validationText;
        private set => SetProperty(ref validationText, value);
    }

    public string LogText
    {
        get => logText;
        private set => SetProperty(ref logText, value);
    }

    public int ReconnectCount
    {
        get => reconnectCount;
        private set => SetProperty(ref reconnectCount, value);
    }

    public string LatencyText
    {
        get => latencyText;
        private set => SetProperty(ref latencyText, value);
    }

    /// <summary>Technical details of the last failure, shown in an expandable section (SPEC §34).</summary>
    public string FailureDetails
    {
        get => failureDetails;
        private set
        {
            if (SetProperty(ref failureDetails, value))
            {
                OnPropertyChanged(nameof(HasFailureDetails));
            }
        }
    }

    public bool HasFailureDetails => !string.IsNullOrWhiteSpace(FailureDetails);

    public string Endpoint => string.IsNullOrWhiteSpace(Host) ? "-" : SshArgumentBuilder.FormatEndpoint(BindAddress, int.TryParse(Port, out var p) ? p : 0);

    public string Host { get => host; set { if (SetProperty(ref host, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string BindAddress { get => bindAddress; set { if (SetProperty(ref bindAddress, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string Port { get => port; set { if (SetProperty(ref port, value)) { OnPropertyChanged(nameof(Endpoint)); } } }

    public string SshExecutable { get => sshExecutable; set => SetProperty(ref sshExecutable, value); }

    public bool IPv4Only { get => ipv4Only; set => SetProperty(ref ipv4Only, value); }

    public string ServerAliveInterval { get => serverAliveInterval; set => SetProperty(ref serverAliveInterval, value); }

    public string ServerAliveCountMax { get => serverAliveCountMax; set => SetProperty(ref serverAliveCountMax, value); }

    public string AdditionalArguments { get => additionalArguments; set => SetProperty(ref additionalArguments, value); }

    public bool AutoReconnect { get => autoReconnect; set => SetProperty(ref autoReconnect, value); }

    public bool HealthEnabled { get => healthEnabled; set => SetProperty(ref healthEnabled, value); }

    public string HealthHost { get => healthHost; set => SetProperty(ref healthHost, value); }

    public string HealthPort { get => healthPort; set => SetProperty(ref healthPort, value); }

    /// <summary>Called when the window is closing: stop the tunnel (killing only our own ssh) before exiting.</summary>
    public async Task ShutdownAsync()
    {
        closing = true;
        timer.Stop();
        log.Info("app", "Application shutting down");
        await manager.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        if (!ApplyAndSave())
        {
            return;
        }

        await manager.ConnectAsync(config.ActiveProfile);
    }

    /// <summary>Copies the edited fields into the profile, validates, and persists. Returns false when invalid.</summary>
    private bool ApplyAndSave()
    {
        var p = config.ActiveProfile;
        var errors = new List<string>();

        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            errors.Add("Port must be a number between 1 and 65535.");
            parsedPort = p.Port;
        }

        if (!int.TryParse(ServerAliveInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            errors.Add("ServerAliveInterval must be a number.");
            interval = p.ServerAliveInterval;
        }

        if (!int.TryParse(ServerAliveCountMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var countMax))
        {
            errors.Add("ServerAliveCountMax must be a number.");
            countMax = p.ServerAliveCountMax;
        }

        if (!int.TryParse(HealthPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var healthPortValue))
        {
            errors.Add("Health-check port must be a number.");
            healthPortValue = p.Monitoring.TargetPort;
        }

        var candidate = p.Clone();
        candidate.Host = Host.Trim();
        candidate.BindAddress = BindAddress.Trim();
        candidate.Port = parsedPort;
        candidate.SshExecutable = SshExecutable.Trim();
        candidate.IPv4Only = IPv4Only;
        candidate.ServerAliveInterval = interval;
        candidate.ServerAliveCountMax = countMax;
        candidate.AdditionalArguments = CommandLineSplitter.Split(AdditionalArguments);
        candidate.Reconnect.Enabled = AutoReconnect;
        candidate.Monitoring.Enabled = HealthEnabled;
        candidate.Monitoring.TargetHost = HealthHost.Trim();
        candidate.Monitoring.TargetPort = healthPortValue;

        var result = ProfileValidator.Validate(candidate);
        errors.AddRange(result.Errors.Select(i => i.Message));
        var builder = new StringBuilder();
        foreach (var line in errors.Distinct())
        {
            builder.AppendLine(line);
        }

        foreach (var warning in result.Warnings)
        {
            builder.AppendLine("Warning: " + warning.Message);
        }

        ValidationText = builder.ToString().TrimEnd();
        if (errors.Count > 0)
        {
            return false;
        }

        var index = config.Profiles.FindIndex(x => x.Id == p.Id);
        config.Profiles[index] = candidate;
        try
        {
            configuration.Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("config", "Could not save the configuration.", ex);
            ValidationText += (ValidationText.Length > 0 ? Environment.NewLine : string.Empty) + "The configuration could not be saved: " + ex.Message;
        }

        return true;
    }

    private void OnStateChanged(StateChange change)
    {
        if (closing)
        {
            return;
        }

        State = change.New;
        StatusDetail = change.New switch
        {
            ConnectionState.Failed => change.Failure?.Message ?? string.Empty,
            ConnectionState.Reconnecting => change.Failure?.Message ?? "Reconnecting…",
            ConnectionState.Degraded => "Health checks are failing; the SOCKS proxy may not be passing traffic.",
            ConnectionState.Connected or ConnectionState.Disconnected => string.Empty,
            _ => StatusDetail,
        };
        FailureDetails = change.New is ConnectionState.Failed or ConnectionState.Reconnecting ? change.Failure?.Details ?? string.Empty : string.Empty;

        if (change.New is ConnectionState.Disconnected)
        {
            timer.Stop();
            SessionDuration = DurationFormatter.Format(TimeSpan.Zero);
        }
        else if (!timer.IsEnabled)
        {
            timer.Start(); // the 1 Hz timer only runs while a session exists
        }

        RefreshSession();
    }

    private void RefreshSession()
    {
        ReconnectCount = manager.ReconnectCount;
        LatencyText = State is ConnectionState.Connected or ConnectionState.Degraded && manager.LastHealth is { Success: true, Latency: { } latency }
            ? $"{latency.TotalMilliseconds:0} ms"
            : "-";
        var started = manager.SessionStartedUtc;
        if (started is not null)
        {
            SessionDuration = DurationFormatter.Format(DateTimeOffset.UtcNow - started.Value);
        }
    }

    private void AppendLog(LogEntry entry)
    {
        AppendLogLine(entry);
    }

    private void AppendLogLine(LogEntry entry)
    {
        var text = LogText + AppLog.Format(entry) + Environment.NewLine;
        LogText = text.Length > 60_000 ? text[^40_000..] : text;
    }
}
