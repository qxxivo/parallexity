$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
$bitmap = [Drawing.Bitmap]::new(256,256)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.ScaleTransform(256/144,256/144)
$background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#222226'))
$white = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f3f3f5'))
$pen = [Drawing.Pen]::new($white,[single]3.2)
$pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
function RoundRectangle([single]$x,[single]$y,[single]$w,[single]$h,[single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new(); $d = $radius * 2
    $path.AddArc($x,$y,$d,$d,180,90); $path.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $path.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $path.AddArc($x,$y+$h-$d,$d,$d,90,90); $path.CloseFigure()
    return ,$path
}
$tile = RoundRectangle 0 0 144 144 30
$graphics.FillPath($background,$tile); $graphics.TranslateTransform(4,4)
$monitor = RoundRectangle 10 27 50 31 3; $graphics.DrawPath($pen,$monitor)
$graphics.DrawLine($pen,35,58,35,71); $graphics.DrawLine($pen,23,71,47,71)
$graphics.DrawLine($pen,6,116,116,6); $graphics.DrawLine($pen,20,130,130,20)
$phone = RoundRectangle 82 75 24 44 4; $graphics.DrawPath($pen,$phone)
$graphics.DrawLine($pen,90,81,98,81); $graphics.FillEllipse($white,[single]92.4,[single]111.4,[single]3.2,[single]3.2)
$bitmap.Save((Join-Path $assets 'logo-preview.png'),[Drawing.Imaging.ImageFormat]::Png)
$png = [IO.MemoryStream]::new(); $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png); $bytes = $png.ToArray()
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'parallexity.ico')))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]22); $writer.Write($bytes)
$writer.Dispose(); $png.Dispose(); $tile.Dispose(); $monitor.Dispose(); $phone.Dispose(); $pen.Dispose(); $white.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()