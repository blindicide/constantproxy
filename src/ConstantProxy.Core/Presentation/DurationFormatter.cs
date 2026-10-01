using System.Globalization;

namespace ConstantProxy.Core.Presentation;

public static class DurationFormatter
{
    /// <summary>Formats a duration as <c>HH:mm:ss</c>, or <c>Nd HH:mm:ss</c> beyond a day. Negative values clamp to zero.</summary>
    public static string Format(TimeSpan duration)
    {
        duration = Clamp(duration);
        var time = Time(duration);
        return duration.Days > 0 ? string.Create(CultureInfo.InvariantCulture, $"{duration.Days}d {time}") : time;
    }

    /// <summary>The <c>HH:mm:ss</c> part only (hours wrap at 24; use <see cref="Days"/> for the rest).</summary>
    public static string Time(TimeSpan duration)
    {
        duration = Clamp(duration);
        return $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public static int Days(TimeSpan duration) => Clamp(duration).Days;

    private static TimeSpan Clamp(TimeSpan duration) => duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
}
