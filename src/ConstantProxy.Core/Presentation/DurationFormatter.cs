using System.Globalization;

namespace ConstantProxy.Core.Presentation;

public static class DurationFormatter
{
    /// <summary>Formats a duration as <c>HH:mm:ss</c>, or <c>Nd HH:mm:ss</c> beyond a day. Negative values clamp to zero.</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var time = $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        return duration.Days > 0 ? string.Create(CultureInfo.InvariantCulture, $"{duration.Days}d {time}") : time;
    }
}
