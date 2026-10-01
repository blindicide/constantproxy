namespace ConstantProxy.Core.Analytics;

/// <summary>
/// Calendar windows for "today / this week / this month" statistics (SPEC §18). Boundaries are computed in the
/// user's local time zone and converted back to UTC, so DST changes and travel never corrupt history (SPEC §79).
/// </summary>
public static class TimeWindows
{
    public static (DateTimeOffset From, DateTimeOffset To) Today(DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        (LocalMidnightUtc(nowUtc, zone, 0), nowUtc);

    /// <summary>Calendar week starting on Monday.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) ThisWeek(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return (LocalMidnightUtc(nowUtc, zone, -daysSinceMonday), nowUtc);
    }

    public static (DateTimeOffset From, DateTimeOffset To) ThisMonth(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        return (LocalMidnightUtc(nowUtc, zone, 1 - local.Day), nowUtc);
    }

    /// <summary>A rolling window ending now (for example the last 7 days).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Last(DateTimeOffset nowUtc, TimeSpan span) => (nowUtc - span, nowUtc);

    private static DateTimeOffset LocalMidnightUtc(DateTimeOffset nowUtc, TimeZoneInfo zone, int dayOffset)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var date = local.Date.AddDays(dayOffset);
        var unspecified = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        // Midnight can be skipped or repeated on DST days; resolve to the first valid instant at or after it.
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(unspecified) ? zone.GetAmbiguousTimeOffsets(unspecified).Max() : zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
