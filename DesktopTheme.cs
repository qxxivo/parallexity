using System.Drawing.Drawing2D;

namespace PhoneRemote;

internal static class DesktopTheme
{
    internal static readonly Color Background = Color.FromArgb(0, 0, 0);
    internal static readonly Color Card = Color.FromArgb(28, 28, 30);
    internal static readonly Color Surface = Color.FromArgb(44, 44, 46);
    internal static readonly Color Text = Color.White;
    internal static readonly Color Muted = Color.FromArgb(174, 174, 184);
    internal const int CardRadius = 24, ControlRadius = 16;

    internal static GraphicsPath Shape(RectangleF box, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        if (d <= 0) return path;
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right-d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right-d, box.Bottom-d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom-d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
