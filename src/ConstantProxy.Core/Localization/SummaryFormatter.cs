namespace ConstantProxy.Core.Localization;

/// <summary>Formats the statistics summary as text in the current language.</summary>
public static class SummaryFormatter
{
    public static string Format(AnalyticsSummary s, ILocalizer l)
    {
        string Pair(TrafficTotals t) => l.Format("stats.traffic.pair", LocalizedText.Bytes(l, t.DownloadBytes), LocalizedText.Bytes(l, t.UploadBytes));

        return string.Join(Environment.NewLine,
            l.Get("stats.uptime"),
            l.Format("stats.uptime.line", LocalizedText.Percent(l, s.UptimeSession), LocalizedText.Percent(l, s.UptimeToday), LocalizedText.Percent(l, s.Uptime7Days), LocalizedText.Percent(l, s.Uptime30Days)),
            string.Empty,
            l.Get("stats.traffic"),
            l.Format("stats.traffic.today", Pair(s.TrafficToday)),
            l.Format("stats.traffic.week", Pair(s.TrafficThisWeek)),
            l.Format("stats.traffic.month", Pair(s.TrafficThisMonth)),
            l.Format("stats.traffic.total", Pair(s.TrafficAllTime)),
            string.Empty,
            l.Get("stats.connection"),
            l.Format("stats.connection.line1", s.SessionCount, LocalizedText.Duration(l, s.TotalRuntime), LocalizedText.Duration(l, s.TotalConnected)),
            l.Format("stats.connection.line2", s.TotalReconnects, s.TotalFailures, LocalizedText.Duration(l, s.LongestContinuousConnection)));
    }
}
