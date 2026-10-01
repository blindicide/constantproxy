using ConstantProxy.Core.Presentation;

namespace ConstantProxy.Core.Localization;

/// <summary>Turns codes and models from the lower layers into localized text.</summary>
public static class LocalizedText
{
    public static string State(ILocalizer l, ConnectionState state) => l.Get($"state.{state}");

    /// <summary>Localized failure message, falling back to the English text for codes without a translation.</summary>
    public static string Failure(ILocalizer l, FailureInfo failure)
    {
        var key = failure.Code.StartsWith("config.", StringComparison.Ordinal)
            ? "validation." + failure.Code["config.".Length..]
            : "failure." + failure.Code;
        return l.Has(key) ? l.Format(key, failure.Arguments.Cast<object>().ToArray()) : failure.Message;
    }

    /// <summary>Technical details for the expandable "Details" section: exit code first, then ssh output.</summary>
    public static string FailureDetails(ILocalizer l, FailureInfo failure)
    {
        var parts = new List<string>();
        if (failure.ExitCode is { } code)
        {
            parts.Add(l.Format("failure.exitcode", code));
        }

        if (!string.IsNullOrWhiteSpace(failure.Details))
        {
            parts.Add(failure.Details);
        }

        return string.Join(Environment.NewLine, parts);
    }

    public static string Issue(ILocalizer l, ValidationIssue issue)
    {
        var key = "validation." + issue.Code;
        return l.Has(key) ? l.Get(key) : issue.Message;
    }

    public static TrafficUnitLabels UnitLabels(ILocalizer l) => new(
        new[] { l.Get("unit.rate.b"), l.Get("unit.rate.kb"), l.Get("unit.rate.mb"), l.Get("unit.rate.gb") },
        new[] { l.Get("unit.size.b"), l.Get("unit.size.kb"), l.Get("unit.size.mb"), l.Get("unit.size.gb"), l.Get("unit.size.tb") });

    public static string Rate(ILocalizer l, double bytesPerSecond) =>
        TrafficFormatter.FormatRate(bytesPerSecond, l.Culture, UnitLabels(l));

    public static string Bytes(ILocalizer l, long bytes) =>
        TrafficFormatter.FormatBytes(bytes, l.Culture, UnitLabels(l));

    /// <summary><c>HH:mm:ss</c>, with a localized day count in front beyond 24 hours.</summary>
    public static string Duration(ILocalizer l, TimeSpan duration)
    {
        var time = DurationFormatter.Time(duration);
        var days = DurationFormatter.Days(duration);
        return days > 0 ? l.Format("unit.day", days, time) : time;
    }

    /// <summary>"18 seconds", "1 min 5 s", "2 h 3 min" in the current language.</summary>
    public static string Downtime(ILocalizer l, TimeSpan downtime)
    {
        var total = (long)Math.Max(Math.Round(downtime.TotalSeconds), 0);
        if (total < 60)
        {
            return l.Plural("duration.second", total);
        }

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        if (hours > 0)
        {
            return $"{l.Format("duration.hour", hours)} {l.Format("duration.min", minutes)}";
        }

        return seconds == 0
            ? l.Format("duration.min", minutes)
            : $"{l.Format("duration.min", minutes)} {l.Format("duration.sec", seconds)}";
    }

    public static string Percent(ILocalizer l, double? value) =>
        value is { } v ? l.Format("stats.percent", v.ToString("0.00", l.Culture)) : l.Get("stats.none");
}
