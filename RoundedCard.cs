using System.Drawing.Drawing2D;

namespace PhoneRemote;

internal sealed class RoundedCard : Panel
{
    internal RoundedCard() { DoubleBuffered = true; BackColor = Color.FromArgb(28, 28, 31); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(18, 18, 21));
        if (Width < 32 || Height < 32) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = new GraphicsPath();
        const int d = 28;
        shape.AddArc(1, 1, d, d, 180, 90); shape.AddArc(Width-d-2, 1, d, d, 270, 90);
        shape.AddArc(Width-d-2, Height-d-2, d, d, 0, 90); shape.AddArc(1, Height-d-2, d, d, 90, 90); shape.CloseFigure();
        using var fill = new SolidBrush(BackColor); using var border = new Pen(Color.FromArgb(49, 49, 54));
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}
