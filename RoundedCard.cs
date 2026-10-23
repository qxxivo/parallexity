using System.Drawing.Drawing2D;

namespace PhoneRemote;

internal sealed class RoundedCard : Panel
{
    internal RoundedCard() { DoubleBuffered = true; BackColor = DesktopTheme.Card; ResizeRedraw = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? DesktopTheme.Background);
        if (Width < 32 || Height < 32) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = DesktopTheme.Shape(new RectangleF(0,0,Width-1,Height-1),DesktopTheme.CardRadius*DeviceDpi/96f);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, shape);
    }
}
