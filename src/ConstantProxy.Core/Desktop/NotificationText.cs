namespace ConstantProxy.Core.Desktop;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record NotificationContent(string Title, string Message, NotificationSeverity Severity);

/// <summary>Composes notification text (English until the localization layer takes over in v0.6.0).</summary>
public static class NotificationText
{
    public static NotificationContent Compose(AppNotification n) => n.Kind switch
    {
        NotificationKind.ConnectionLost => new NotificationContent(
            VersionInfo.ProductName, "Proxy connection lost." + Environment.NewLine + "Reconnecting...", NotificationSeverity.Warning),
        NotificationKind.ConnectionFailed => new NotificationContent(
            VersionInfo.ProductName, n.Failure?.Message ?? "The proxy connection failed.", NotificationSeverity.Error),
        _ => new NotificationContent(
            VersionInfo.ProductName,
            "Proxy connection restored." + (n.Downtime is { } d ? Environment.NewLine + "Downtime: " + FormatDowntime(d) + "." : string.Empty),
            NotificationSeverity.Info),
    };

    /// <summary>"18 seconds", "1 min 5 s", "2 h 3 min".</summary>
    public static string FormatDowntime(TimeSpan downtime)
    {
        var total = (long)Math.Max(Math.Round(downtime.TotalSeconds), 0);
        if (total < 60)
        {
            return total == 1 ? "1 second" : $"{total} seconds";
        }

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0
            ? $"{hours} h {minutes} min"
            : seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} s";
    }
}
