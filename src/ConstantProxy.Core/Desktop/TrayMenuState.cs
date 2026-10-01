namespace ConstantProxy.Core.Desktop;

public enum TrayIconKind
{
    Disconnected,
    Connecting,
    Connected,
    Degraded,
    Reconnecting,
    Failed,
}

/// <summary>
/// What the notification-area menu and icon should show for a connection state (SPEC §31). Pure so the rules are
/// testable; the WinForms <c>NotifyIcon</c> only applies the result.
/// </summary>
public sealed record TrayMenuState(bool CanConnect, bool CanDisconnect, bool CanReconnect, TrayIconKind Icon, bool CanSwitchProfile)
{
    /// <summary>NotifyIcon.Text throws above this many characters.</summary>
    public const int MaxTooltipLength = 63;

    public static TrayMenuState For(ConnectionState state) => new(
        CanConnect: state is ConnectionState.Disconnected or ConnectionState.Failed,
        CanDisconnect: state is not (ConnectionState.Disconnected or ConnectionState.Stopping),
        CanReconnect: state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting,
        // The active profile can only change while no tunnel is running (SPEC §64: profile changes during a connection).
        CanSwitchProfile: state is ConnectionState.Disconnected or ConnectionState.Failed,
        Icon: state switch
        {
            ConnectionState.Connected => TrayIconKind.Connected,
            ConnectionState.Degraded => TrayIconKind.Degraded,
            ConnectionState.Starting or ConnectionState.Connecting => TrayIconKind.Connecting,
            ConnectionState.Reconnecting or ConnectionState.Stopping => TrayIconKind.Reconnecting,
            ConnectionState.Failed => TrayIconKind.Failed,
            _ => TrayIconKind.Disconnected,
        });

    /// <summary>Truncates tooltip text to the Windows limit, ending with an ellipsis when shortened.</summary>
    public static string FitTooltip(string text)
    {
        if (text.Length <= MaxTooltipLength)
        {
            return text;
        }

        return text[..(MaxTooltipLength - 1)] + "…";
    }
}
