using System.Drawing.Drawing2D;

namespace PhoneRemote;

internal sealed class RoundedButton : Button
{
    internal RoundedButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = new GraphicsPath(); const int d = 16;
        shape.AddArc(1,1,d,d,180,90); shape.AddArc(Width-d-2,1,d,d,270,90);
        shape.AddArc(Width-d-2,Height-d-2,d,d,0,90); shape.AddArc(1,Height-d-2,d,d,90,90); shape.CloseFigure();
        using var fill = new SolidBrush(Enabled ? BackColor : Color.FromArgb(32,32,36));
        using var border = new Pen(Focused ? Color.FromArgb(170,170,180) : Color.FromArgb(65,65,72));
        e.Graphics.FillPath(fill,shape); e.Graphics.DrawPath(border,shape);
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled ? ForeColor : Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    internal ToggleSwitch() { DoubleBuffered = true; Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var x = Width - 45; var y = (Height - 24) / 2;
        using var shape = new GraphicsPath();
        shape.AddArc(x,y,24,24,90,180); shape.AddArc(x+20,y,24,24,270,180); shape.CloseFigure();
        using var track = new SolidBrush(Checked ? Color.FromArgb(146,146,159) : Color.FromArgb(58,58,65));
        using var knob = new SolidBrush(Enabled ? Color.FromArgb(239,239,244) : Color.FromArgb(116,116,125));
        e.Graphics.FillPath(track,shape); e.Graphics.FillEllipse(knob,x+(Checked ? 23 : 3),y+3,18,18);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,0,Width-52,Height),
            Enabled ? ForeColor : Color.FromArgb(140,140,150),TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if(Focused) ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(0,2,Width-52,Height-4));
    }
}
