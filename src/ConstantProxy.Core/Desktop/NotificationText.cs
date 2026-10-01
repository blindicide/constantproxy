namespace ConstantProxy.Core.Desktop;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record NotificationContent(string Title, string Message, NotificationSeverity Severity);

/// <summary>Composes localized notification text (SPEC §33).</summary>
public static class NotificationText
{
    public static NotificationContent Compose(AppNotification n, ILocalizer l) => n.Kind switch
    {
        NotificationKind.ConnectionLost => new NotificationContent(
            VersionInfo.ProductName, l.Get("notify.lost").Replace("\n", Environment.NewLine), NotificationSeverity.Warning),
        NotificationKind.ConnectionFailed => new NotificationContent(
            VersionInfo.ProductName, n.Failure is { } failure ? LocalizedText.Failure(l, failure).Replace("\n", Environment.NewLine) : l.Get("notify.failed"), NotificationSeverity.Error),
        _ => new NotificationContent(
            VersionInfo.ProductName,
            l.Get("notify.restored") + (n.Downtime is { } d ? Environment.NewLine + l.Format("notify.downtime", LocalizedText.Downtime(l, d)) : string.Empty),
            NotificationSeverity.Info),
    };
}
