using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ConstantProxy.Core.Desktop;

namespace ConstantProxy.App;

/// <summary>
/// Draws the notification-area icons at runtime so no binary assets are needed per state. Each state has its own
/// shape as well as its own colour, so the state never depends on colour alone (SPEC §27).
/// </summary>
public static class TrayIconFactory
{
    private const int Size = 32;

    public static Icon Create(TrayIconKind kind)
    {
        using var bitmap = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, kind);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone(); // the clone owns its own handle; the temporary one is destroyed below
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void Draw(Graphics g, TrayIconKind kind)
    {
        var bounds = new Rectangle(2, 2, Size - 4, Size - 4);
        using var white = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case TrayIconKind.Connected:
                Fill(g, bounds, Color.SeaGreen);
                g.DrawLines(white, new[] { new Point(9, 17), new Point(14, 22), new Point(23, 11) });
                break;
            case TrayIconKind.Connecting:
                Fill(g, bounds, Color.SteelBlue);
                using (var dots = new SolidBrush(Color.White))
                {
                    foreach (var x in new[] { 8, 14, 20 })
                    {
                        g.FillEllipse(dots, x, 14, 5, 5);
                    }
                }

                break;
            case TrayIconKind.Reconnecting:
                Fill(g, bounds, Color.DarkOrange);
                g.DrawArc(white, 9, 9, 14, 14, 40, 270);
                using (var tip = new SolidBrush(Color.White))
                {
                    g.FillPolygon(tip, new[] { new Point(20, 5), new Point(27, 12), new Point(17, 13) });
                }

                break;
            case TrayIconKind.Degraded:
                using (var triangle = new SolidBrush(Color.Goldenrod))
                {
                    g.FillPolygon(triangle, new[] { new Point(16, 2), new Point(30, 29), new Point(2, 29) });
                }

                using (var mark = new SolidBrush(Color.White))
                {
                    g.FillRectangle(mark, 14, 11, 4, 10);
                    g.FillEllipse(mark, 14, 23, 4, 4);
                }

                break;
            case TrayIconKind.Failed:
                Fill(g, bounds, Color.Firebrick);
                g.DrawLine(white, 10, 10, 22, 22);
                g.DrawLine(white, 22, 10, 10, 22);
                break;
            default:
                using (var ring = new Pen(Color.Gray, 3.5f))
                {
                    g.DrawEllipse(ring, 5, 5, Size - 11, Size - 11);
                }

                break;
        }
    }

    private static void Fill(Graphics g, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, bounds);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
