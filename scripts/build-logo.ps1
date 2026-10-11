$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
[xml]$source = Get-Content -LiteralPath (Join-Path $assets 'logo.svg')
$shape = $source.svg.path.d
if (!$shape) { throw 'Logo path is missing' }
$svg = '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 100 100"><rect width="100" height="100" rx="21" fill="#222226"/><path transform="translate(0 5)" fill="#f3f3f5" fill-rule="evenodd" d="' + $shape + '"/></svg>'
[IO.File]::WriteAllText((Join-Path $root 'web\icon.svg'),$svg)
$path = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Alternate)
$parts = [regex]::Matches($shape,'[MHQVZ]|-?\d+(?:\.\d+)?') | ForEach-Object Value
$i = 0; [single]$x=0; [single]$y=0; [single]$sx=0; [single]$sy=0
while ($i -lt $parts.Count) {
    $command = $parts[$i++]
    switch ($command) {
        M { $x=[single]$parts[$i++]; $y=[single]$parts[$i++]; $sx=$x; $sy=$y; $path.StartFigure() }
        H { $nx=[single]$parts[$i++]; $path.AddLine($x,$y,$nx,$y); $x=$nx }
        V { $ny=[single]$parts[$i++]; $path.AddLine($x,$y,$x,$ny); $y=$ny }
        Q {
            $cx=[single]$parts[$i++]; $cy=[single]$parts[$i++]; $nx=[single]$parts[$i++]; $ny=[single]$parts[$i++]
            $path.AddBezier($x,$y,[single]($x+2/3*($cx-$x)),[single]($y+2/3*($cy-$y)),[single]($nx+2/3*($cx-$nx)),[single]($ny+2/3*($cy-$ny)),$nx,$ny)
            $x=$nx; $y=$ny
        }
        Z { $path.CloseFigure(); $x=$sx; $y=$sy }
        default { throw 'Unsupported logo command' }
    }
}
$tile = [Drawing.Drawing2D.GraphicsPath]::new()
$tile.AddArc(0,0,42,42,180,90); $tile.AddArc(58,0,42,42,270,90); $tile.AddArc(58,58,42,42,0,90); $tile.AddArc(0,58,42,42,90,90); $tile.CloseFigure()
$background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#222226'))
$white = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f3f3f5'))
$sizes = @(16,20,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size/100,$size/100)
    $graphics.FillPath($background,$tile); $graphics.TranslateTransform(0,5); $graphics.FillPath($white,$path)
    if ($size -eq 256) { $bitmap.Save((Join-Path $assets 'logo-preview.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $memory = [IO.MemoryStream]::new(); $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $images += ,$memory.ToArray()
    $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'parallexity.ico')))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($n=0; $n -lt $sizes.Count; $n++) {
    $dimension = if ($sizes[$n] -eq 256) { 0 } else { $sizes[$n] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$n].Length); $writer.Write([uint32]$offset)
    $offset += $images[$n].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $path.Dispose(); $tile.Dispose(); $background.Dispose(); $white.Dispose()