$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$dest = Join-Path $PSScriptRoot '..\LcdWidget\Assets'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
# Shared vector geometry drives both the editable SVG and every raster size.
$shapes = @(
    @('#111A24', 'M12,2 L52,2 62,12 62,52 52,62 12,62 2,52 2,12 Z'),
    @('#263C4C', 'M13,5 L51,5 59,13 59,51 51,59 13,59 5,51 5,13 Z'),
    @('#101920', 'M14,8 L50,8 56,14 56,50 50,56 14,56 8,50 8,14 Z'),
    @('#75DDE6', 'M12,2 L29,2 29,6 14,6 6,14 6,29 2,29 2,12 Z'),
    @('#75DDE6', 'M62,35 L62,52 52,62 35,62 35,58 50,58 58,50 58,35 Z'),
    @('#738B95', 'M21,11 L25,11 25,17 21,17 Z M30,11 L34,11 34,17 30,17 Z M39,11 L43,11 43,17 39,17 Z M21,47 L25,47 25,53 21,53 Z M30,47 L34,47 34,53 30,53 Z M39,47 L43,47 43,53 39,53 Z M11,21 L17,21 17,25 11,25 Z M11,30 L17,30 17,34 11,34 Z M11,39 L17,39 17,43 11,43 Z M47,21 L53,21 53,25 47,25 Z M47,30 L53,30 53,34 47,34 Z M47,39 L53,39 53,43 47,43 Z'),
    @('#75DDE6', 'M20,16 L44,16 48,20 48,44 44,48 20,48 16,44 16,20 Z'),
    @('#070E15', 'M22,20 L42,20 44,22 44,42 42,44 22,44 20,42 20,22 Z'),
    @('#DDE7DE', 'M21,32 L26,32 30,23 34,34 37,29 43,29 43,33 39,33 33,42 29,32 28,36 21,36 Z'),
    @('#B6EE75', 'M45,8 L50,8 56,14 56,19 52,19 52,16 48,12 45,12 Z')
)
$svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">' + "`n"
foreach ($shape in $shapes) { $svg += '<path fill="' + $shape[0] + '" d="' + $shape[1] + '"/>' + "`n" }
$svg += '</svg>'
[IO.File]::WriteAllText((Join-Path $dest 'CorePanel.svg'), $svg)
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256,512)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($size/64.0),($size/64.0)))
    foreach ($shape in $shapes) {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($shape[0])
        $dc.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($shape[1]))
    }
    $dc.Pop(); $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new(); $encoder.Save($stream)
    $bytes = $stream.ToArray(); $stream.Dispose()
    [IO.File]::WriteAllBytes((Join-Path $dest "CorePanel-$size.png"),$bytes)
    if ($size -le 256) { $frames += @{Size=$size;Bytes=$bytes} }
}
$stream = [IO.File]::Create((Join-Path $dest 'CorePanel.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output 'Generated CorePanel.svg, PNG sizes 16-512, and nine-frame ICO.'
