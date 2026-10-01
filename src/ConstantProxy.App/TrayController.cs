using System.Drawing;
using System.Windows.Forms;
using ConstantProxy.Core.Desktop;
using ConstantProxy.Core.Models;

namespace ConstantProxy.App;

/// <summary>User-visible tray strings; supplied by the localization layer (SPEC §29).</summary>
public sealed record TrayTexts(string Open, string Connect, string Disconnect, string Reconnect, string Settings, string Exit);

public sealed record TrayActions(Action Open, Action Connect, Action Disconnect, Action Reconnect, Action Settings, Action Exit);

/// <summary>
/// The notification-area icon and its menu (SPEC §31). It only reflects state and forwards user choices to
/// <see cref="TrayActions"/>; it never touches processes itself.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Dictionary<TrayIconKind, Icon> icons = new();
    private readonly ToolStripMenuItem open;
    private readonly ToolStripMenuItem settingsItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly ToolStripMenuItem connect;
    private readonly ToolStripMenuItem disconnect;
    private readonly ToolStripMenuItem reconnect;
    private readonly ContextMenuStrip menu = new();

    public TrayController(TrayTexts texts, TrayActions actions)
    {
        open = new ToolStripMenuItem(texts.Open) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        open.Click += (_, _) => actions.Open();
        connect = new ToolStripMenuItem(texts.Connect);
        connect.Click += (_, _) => actions.Connect();
        disconnect = new ToolStripMenuItem(texts.Disconnect);
        disconnect.Click += (_, _) => actions.Disconnect();
        reconnect = new ToolStripMenuItem(texts.Reconnect);
        reconnect.Click += (_, _) => actions.Reconnect();
        settingsItem = new ToolStripMenuItem(texts.Settings);
        settingsItem.Click += (_, _) => actions.Settings();
        exitItem = new ToolStripMenuItem(texts.Exit);
        exitItem.Click += (_, _) => actions.Exit();

        menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), connect, disconnect, reconnect, new ToolStripSeparator(), settingsItem, new ToolStripSeparator(), exitItem });

        icon = new NotifyIcon
        {
            Icon = IconFor(TrayIconKind.Disconnected),
            Text = "constantproxy",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => actions.Open();
        Update(ConnectionState.Disconnected, "constantproxy");
    }

    /// <summary>Re-labels the menu after a language change.</summary>
    public void SetTexts(TrayTexts texts)
    {
        open.Text = texts.Open;
        connect.Text = texts.Connect;
        disconnect.Text = texts.Disconnect;
        reconnect.Text = texts.Reconnect;
        settingsItem.Text = texts.Settings;
        exitItem.Text = texts.Exit;
    }

    /// <summary>Applies the menu state, icon and tooltip for <paramref name="state"/>.</summary>
    public void Update(ConnectionState state, string tooltip)
    {
        var model = TrayMenuState.For(state);
        connect.Enabled = model.CanConnect;
        disconnect.Enabled = model.CanDisconnect;
        reconnect.Enabled = model.CanReconnect;
        icon.Icon = IconFor(model.Icon);
        icon.Text = TrayMenuState.FitTooltip(tooltip);
    }

    public void ShowBalloon(string title, string text, ToolTipIcon kind)
    {
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        foreach (var cached in icons.Values)
        {
            cached.Dispose();
        }

        icons.Clear();
    }

    private Icon IconFor(TrayIconKind kind)
    {
        if (!icons.TryGetValue(kind, out var cached))
        {
            cached = TrayIconFactory.Create(kind);
            icons[kind] = cached;
        }

        return cached;
    }
}
