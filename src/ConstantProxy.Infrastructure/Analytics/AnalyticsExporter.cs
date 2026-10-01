using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConstantProxy.Infrastructure.Analytics;

public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>Exports sessions, traffic and connection events (SPEC §66). Everything stays local; nothing is uploaded.</summary>
public sealed class AnalyticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IAnalyticsStore store;

    public AnalyticsExporter(IAnalyticsStore store)
    {
        this.store = store;
    }

    /// <summary>Writes <c>sessions</c>, <c>traffic</c> and <c>connection-events</c> files into <paramref name="directory"/>; returns their paths.</summary>
    public IReadOnlyList<string> Export(string directory, ExportFormat format, Guid? profileId, DateTimeOffset from, DateTimeOffset to, string prefix = "constantproxy")
    {
        Directory.CreateDirectory(directory);
        var extension = format == ExportFormat.Csv ? "csv" : "json";
        var files = new List<string>();

        var sessions = store.GetSessions(profileId, from, to);
        files.Add(Write(directory, $"{prefix}-sessions.{extension}", format, SessionsCsv(sessions), sessions));

        var traffic = store.GetTrafficMinutes(profileId, from, to);
        files.Add(Write(directory, $"{prefix}-traffic.{extension}", format, TrafficCsv(traffic), traffic));

        var events = store.GetEvents(profileId, from, to);
        files.Add(Write(directory, $"{prefix}-connection-events.{extension}", format, EventsCsv(events), events));
        return files;
    }

    public static string SessionsCsv(IEnumerable<SessionRecord> sessions)
    {
        var sb = new StringBuilder(CsvFormatter.Line(new object[]
        {
            "session_id", "profile_id", "start_utc", "end_utc", "end_reason", "duration_seconds", "connected_seconds", "disconnected_seconds",
            "reconnects", "failures", "health_failures", "uploaded_bytes", "downloaded_bytes",
            "avg_upload_bytes_per_s", "avg_download_bytes_per_s", "peak_upload_bytes_per_s", "peak_download_bytes_per_s",
        }));
        foreach (var s in sessions)
        {
            var duration = s.Duration?.TotalSeconds;
            sb.Append(CsvFormatter.Line(new object?[]
            {
                s.Id, s.ProfileId, s.StartUtc, s.EndUtc, s.EndReason?.ToString(), duration is null ? null : Math.Round(duration.Value, 3), Math.Round(s.ConnectedSeconds, 3),
                duration is null ? null : Math.Round(Math.Max(duration.Value - s.ConnectedSeconds, 0), 3),
                s.ReconnectCount, s.FailureCount, s.HealthFailures, s.UploadedBytes, s.DownloadedBytes,
                Math.Round(s.AverageUploadRate, 2), Math.Round(s.AverageDownloadRate, 2), Math.Round(s.PeakUploadRate, 2), Math.Round(s.PeakDownloadRate, 2),
            }));
        }

        return sb.ToString();
    }

    public static string TrafficCsv(IEnumerable<TrafficMinute> minutes)
    {
        var sb = new StringBuilder(CsvFormatter.Line(new object[]
        {
            "session_id", "minute_utc", "uploaded_bytes", "downloaded_bytes", "peak_upload_bytes_per_s", "peak_download_bytes_per_s",
            "health_checks", "health_failures", "avg_latency_ms", "max_latency_ms",
        }));
        foreach (var m in minutes)
        {
            sb.Append(CsvFormatter.Line(new object?[]
            {
                m.SessionId, m.MinuteUtc, m.UploadBytes, m.DownloadBytes, Math.Round(m.PeakUploadRate, 2), Math.Round(m.PeakDownloadRate, 2),
                m.HealthChecks, m.HealthFailures, m.AverageLatencyMs is { } l ? Math.Round(l, 2) : null, m.LatencyCount == 0 ? null : Math.Round(m.LatencyMaxMs, 2),
            }));
        }

        return sb.ToString();
    }

    public static string EventsCsv(IEnumerable<EventRecord> events)
    {
        var sb = new StringBuilder(CsvFormatter.Line(new object[] { "event_id", "session_id", "time_utc", "type", "detail" }));
        foreach (var e in events)
        {
            sb.Append(CsvFormatter.Line(new object?[] { e.Id, e.SessionId, e.TimeUtc, e.Type, e.Detail }));
        }

        return sb.ToString();
    }

    private static string Write<T>(string directory, string name, ExportFormat format, string csv, IReadOnlyList<T> rows)
    {
        var path = Path.Combine(directory, name);
        var content = format == ExportFormat.Csv ? csv : JsonSerializer.Serialize(rows, JsonOptions);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ExportFormat.Csv)); // BOM helps Excel read UTF-8 CSV
        return path;
    }
}
