using System.Drawing.Drawing2D;

namespace PhoneRemote;

internal sealed class RoundedButton : Button
{
    private bool hovered, pressed;
    internal RoundedButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; DoubleBuffered = true; ResizeRedraw = true; UseVisualStyleBackColor = false; }
    protected override void OnMouseEnter(EventArgs e) { hovered=true;Invalidate();base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered=pressed=false;Invalidate();base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed=e.Button==MouseButtons.Left;Invalidate();base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed=false;Invalidate();base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate();base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate();base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = DesktopTheme.Shape(new RectangleF(2,2,Width-5,Height-5),DesktopTheme.ControlRadius*DeviceDpi/96f);
        var color = !Enabled ? DesktopTheme.Card : pressed ? ControlPaint.Dark(BackColor,.12f) : hovered ? ControlPaint.Light(BackColor,.08f) : BackColor;
        using var fill = new SolidBrush(color);
        e.Graphics.FillPath(fill,shape);
        if(Focused&&ShowFocusCues){using var border=new Pen(DesktopTheme.Text,2*DeviceDpi/96f);e.Graphics.DrawPath(border,shape);}
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
        var scale=DeviceDpi/96f;var trackWidth=44*scale;var trackHeight=26*scale;
        var x = Width - trackWidth; var y = (Height - trackHeight) / 2;
        using var shape = DesktopTheme.Shape(new RectangleF(x,y,trackWidth,trackHeight),trackHeight/2);
        using var track = new SolidBrush(Checked ? DesktopTheme.Text : DesktopTheme.Surface);
        using var knob = new SolidBrush(!Enabled ? DesktopTheme.Muted : Checked ? DesktopTheme.Surface : DesktopTheme.Text);
        e.Graphics.FillPath(track,shape); e.Graphics.FillEllipse(knob,x+(Checked ? 21 : 3)*scale,y+3*scale,20*scale,20*scale);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,0,Math.Max(0,Width-(int)(52*scale)),Height),
            Enabled ? ForeColor : Color.FromArgb(140,140,150),TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(0,2,Math.Max(0,Width-(int)(52*scale)),Height-4));
    }
}
