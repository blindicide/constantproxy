using System.Globalization;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Connection;
using ConstantProxy.Core.Logging;
using ConstantProxy.Core.Models;
using ConstantProxy.Core.Presentation;
using ConstantProxy.Core.Traffic;
using ConstantProxy.Infrastructure.Analytics;
using ConstantProxy.Infrastructure.Config;
using Microsoft.Data.Sqlite;

namespace ConstantProxy.App.ViewModels;

public enum HistoryMetric
{
    Traffic,
    Latency,
    Availability,
}

public sealed record HistoryRange(string Label, TimeSpan Span);

/// <summary>Chooses where an export goes; implemented with WPF dialogs in the app and replaceable in tests.</summary>
public interface IExportDialog
{
    /// <summary>Returns the chosen file path (its extension selects CSV or JSON), or null when cancelled.</summary>
    string? PickExportPath();
}

/// <summary>Statistics and history tab. All database reads run off the UI thread (SPEC §62, §67).</summary>
public sealed class StatisticsViewModel : ObservableObject
{
    private readonly IAnalyticsStore? store;
    private readonly AppPaths paths;
    private readonly AppConfig config;
    private readonly ConnectionManager manager;
    private readonly IExportDialog exportDialog;
    private readonly IAppLog log;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly AnalyticsSummaryService? summaryService;

    private string summaryText = string.Empty;
    private string statusMessage = string.Empty;
    private HistoryMetric metric = HistoryMetric.Traffic;
    private HistoryRange range;
    private IReadOnlyList<GraphSeries> series = Array.Empty<GraphSeries>();
    private bool active;
    private int refreshing;

    public StatisticsViewModel(IAnalyticsStore? store, AppPaths paths, AppConfig config, ConnectionManager manager, IExportDialog exportDialog, IAppLog log, Dispatcher dispatcher)
    {
        this.store = store;
        this.paths = paths;
        this.config = config;
        this.manager = manager;
        this.exportDialog = exportDialog;
        this.log = log;
        this.dispatcher = dispatcher;
        summaryService = store is null ? null : new AnalyticsSummaryService(store);

        Ranges = new[]
        {
            new HistoryRange("1 hour", TimeSpan.FromHours(1)),
            new HistoryRange("24 hours", TimeSpan.FromHours(24)),
            new HistoryRange("7 days", TimeSpan.FromDays(7)),
            new HistoryRange("30 days", TimeSpan.FromDays(30)),
        };
        range = Ranges[1];

        RefreshCommand = new RelayCommand(RefreshAsync);
        ExportCommand = new RelayCommand(ExportAsync, () => store is not null);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolderAsync);

        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) => await RefreshAsync();
        SummaryText = store is null ? "History is turned off or the database could not be opened." : "Open this tab to load statistics.";
    }

    public IReadOnlyList<HistoryRange> Ranges { get; }

    public IReadOnlyList<HistoryMetric> Metrics { get; } = Enum.GetValues<HistoryMetric>();

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public bool IsAvailable => store is not null;

    public string SummaryText { get => summaryText; private set => SetProperty(ref summaryText, value); }

    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }

    public IReadOnlyList<GraphSeries> Series { get => series; private set => SetProperty(ref series, value); }

    public GraphUnit GraphUnit => Metric switch
    {
        HistoryMetric.Traffic => GraphUnit.Rate,
        HistoryMetric.Latency => GraphUnit.Milliseconds,
        _ => GraphUnit.Percent,
    };

    public HistoryMetric Metric
    {
        get => metric;
        set
        {
            if (SetProperty(ref metric, value))
            {
                OnPropertyChanged(nameof(GraphUnit));
                _ = RefreshAsync();
            }
        }
    }

    public HistoryRange Range
    {
        get => range;
        set
        {
            if (SetProperty(ref range, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>The statistics tab being visible turns on a slow refresh; otherwise nothing runs.</summary>
    public void SetActive(bool isActive)
    {
        active = isActive;
        if (isActive)
        {
            timer.Start();
            _ = RefreshAsync();
        }
        else
        {
            timer.Stop();
        }
    }

    public void Stop() => timer.Stop();

    private async Task RefreshAsync()
    {
        if (store is null || summaryService is null || Interlocked.Exchange(ref refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var profileId = config.ActiveProfile.Id;
            var sessionStart = manager.SessionStartedUtc;
            var selectedMetric = metric;
            var selectedRange = range;
            var (summary, history) = await Task.Run(() => Load(profileId, sessionStart, selectedMetric, selectedRange));
            await dispatcher.InvokeAsync(() =>
            {
                SummaryText = FormatSummary(summary);
                Series = history;
                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.Error("analytics", "Could not load statistics.", ex);
            StatusMessage = "Statistics could not be loaded: " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    private (AnalyticsSummary Summary, IReadOnlyList<GraphSeries> Series) Load(Guid profileId, DateTimeOffset? sessionStart, HistoryMetric selectedMetric, HistoryRange selectedRange)
    {
        var now = DateTimeOffset.UtcNow;
        var summary = summaryService!.Compute(profileId, now, TimeZoneInfo.Local, sessionStart);

        var to = now.AddMinutes(1);
        var from = MinuteAggregator.FloorToMinute(now - selectedRange.Span);
        var bucket = HistorySeries.BucketFor(selectedRange.Span);
        IReadOnlyList<GraphSeries> built;
        switch (selectedMetric)
        {
            case HistoryMetric.Traffic:
                var minutes = store!.GetTrafficMinutes(profileId, from, to);
                built = new[]
                {
                    new GraphSeries("Download", Brushes.SeaGreen, false, HistorySeries.Rate(minutes, from, to, bucket, HistorySeries.TrafficDirection.Download)),
                    new GraphSeries("Upload", Brushes.DarkOrange, true, HistorySeries.Rate(minutes, from, to, bucket, HistorySeries.TrafficDirection.Upload)),
                };
                break;
            case HistoryMetric.Latency:
                built = new[] { new GraphSeries("Latency", Brushes.SteelBlue, false, HistorySeries.Latency(store!.GetTrafficMinutes(profileId, from, to), from, to, bucket)) };
                break;
            default:
                built = new[] { new GraphSeries("Availability", Brushes.SeaGreen, false, HistorySeries.Availability(store!.GetIntervals(profileId, from, to), from, to, bucket, now)) };
                break;
        }

        return (summary, built);
    }

    private static string FormatSummary(AnalyticsSummary s)
    {
        var culture = CultureInfo.CurrentUICulture;
        static string Pct(double? v) => v is { } p ? $"{p:0.00}%" : "-";
        static string Traffic(TrafficTotals t, CultureInfo c) => $"↓ {TrafficFormatter.FormatBytes(t.DownloadBytes, c)}   ↑ {TrafficFormatter.FormatBytes(t.UploadBytes, c)}";

        return string.Join(Environment.NewLine,
            "Uptime",
            $"  Session {Pct(s.UptimeSession)}   Today {Pct(s.UptimeToday)}   7 days {Pct(s.Uptime7Days)}   30 days {Pct(s.Uptime30Days)}",
            string.Empty,
            "Traffic",
            $"  Today        {Traffic(s.TrafficToday, culture)}",
            $"  This week    {Traffic(s.TrafficThisWeek, culture)}",
            $"  This month   {Traffic(s.TrafficThisMonth, culture)}",
            $"  Stored total {Traffic(s.TrafficAllTime, culture)}",
            string.Empty,
            "Connection",
            $"  Sessions {s.SessionCount}   Runtime {DurationFormatter.Format(s.TotalRuntime)}   Connected {DurationFormatter.Format(s.TotalConnected)}",
            $"  Reconnects {s.TotalReconnects}   Failures {s.TotalFailures}   Longest connection {DurationFormatter.Format(s.LongestContinuousConnection)}");
    }

    private async Task ExportAsync()
    {
        var path = exportDialog.PickExportPath();
        if (path is null || store is null)
        {
            return;
        }

        var format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Json : ExportFormat.Csv;
        var directory = Path.GetDirectoryName(path) ?? ".";
        var prefix = Path.GetFileNameWithoutExtension(path);
        var profileId = config.ActiveProfile.Id;
        try
        {
            var files = await Task.Run(() => new AnalyticsExporter(store).Export(directory, format, profileId, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), prefix));
            StatusMessage = $"Exported {files.Count} files to {directory}";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.Error("analytics", "Export failed.", ex);
            StatusMessage = "Export failed: " + ex.Message;
        }
    }

    private Task OpenDataFolderAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo(paths.Root) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warn("app", "Could not open the data folder.", ex);
            StatusMessage = "The data folder could not be opened: " + ex.Message;
        }

        return Task.CompletedTask;
    }
}
