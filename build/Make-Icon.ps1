<#
.SYNOPSIS
  Renders the OverShell application icon and packs it into src/OverShell.App/Assets/OverShell.ico.

.DESCRIPTION
  The mark: a dark rounded square (the chrome colour), an accent-blue prompt chevron and
  underscore - a terminal - and a green state dot in the corner - the overseer. Drawn
  with WPF geometry at every size (16, 20, 24, 32, 40, 48, 64, 128, 256) rather than
  scaled from one bitmap, so the small sizes stay crisp; the .ico stores the 256 px frame
  as PNG (Vista+ convention) and the rest as 32-bit BGRA bitmaps.

  Committed so the icon is reproducible from the palette's colours; run after changing
  them. Needs only PowerShell 7 with WPF (Windows).
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\src\OverShell.App\Assets\OverShell.ico'),
    [string] $PreviewPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# From Theme/Palette.xaml: Surface.Chrome, Accent.Base, State.Done, Text.Primary.
$chrome = [Windows.Media.Color]::FromRgb(0x17, 0x1A, 0x20)
$edge = [Windows.Media.Color]::FromRgb(0x2A, 0x2F, 0x3A)
$accent = [Windows.Media.Color]::FromRgb(0x4C, 0x8D, 0xFF)
$done = [Windows.Media.Color]::FromRgb(0x3D, 0xD6, 0x8C)

function Draw-Mark([int] $size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $s = $size / 16.0   # the design grid is 16 units

    # Background: rounded square with a one-unit lighter edge, inset so the corners breathe.
    $radius = 3.2 * $s
    $rect = [Windows.Rect]::new(0.5 * $s, 0.5 * $s, 15 * $s, 15 * $s)
    $dc.DrawRoundedRectangle([Windows.Media.SolidColorBrush]::new($chrome), [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new($edge), [Math]::Max(1, 0.6 * $s)), $rect, $radius, $radius)

    # Prompt chevron ">" : a stroked polyline, round caps so it reads at 16 px.
    $stroke = [Math]::Max(1.5, 1.9 * $s)
    $pen = [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new($accent), $stroke)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $chevron = [Windows.Media.StreamGeometry]::new()
    $ctx = $chevron.Open()
    $ctx.BeginFigure([Windows.Point]::new(4.2 * $s, 4.6 * $s), $false, $false)
    $ctx.LineTo([Windows.Point]::new(7.6 * $s, 8.0 * $s), $true, $true)
    $ctx.LineTo([Windows.Point]::new(4.2 * $s, 11.4 * $s), $true, $true)
    $ctx.Close(); $chevron.Freeze()
    $dc.DrawGeometry($null, $pen, $chevron)

    # Underscore "_" : the cursor, slightly dimmer than the chevron.
    $underscore = [Windows.Media.Pen]::new([Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromArgb(0xE0, $accent.R, $accent.G, $accent.B)), $stroke)
    $underscore.StartLineCap = 'Round'; $underscore.EndLineCap = 'Round'
    $dc.DrawLine($underscore, [Windows.Point]::new(8.6 * $s, 11.4 * $s), [Windows.Point]::new(12.2 * $s, 11.4 * $s))

    # State dot: the overseer's "done" green, top-right, with a chrome-coloured ring so it sits on the edge.
    $dotR = 2.1 * $s
    $dotCenter = [Windows.Point]::new(12.3 * $s, 3.9 * $s)
    $dc.DrawEllipse([Windows.Media.SolidColorBrush]::new($chrome), $null, $dotCenter, $dotR + [Math]::Max(1, 0.8 * $s), $dotR + [Math]::Max(1, 0.8 * $s))
    $dc.DrawEllipse([Windows.Media.SolidColorBrush]::new($done), $null, $dotCenter, $dotR, $dotR)

    $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Encode-Png([Windows.Media.Imaging.BitmapSource] $bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new(); $encoder.Save($ms); return ,$ms.ToArray()
}

function Encode-Bmp32([Windows.Media.Imaging.BitmapSource] $bitmap) {
    # ICO "BMP" frame: BITMAPINFOHEADER with doubled height, bottom-up BGRA pixels, then a 1-bpp AND mask (all zero: alpha rules).
    $w = $bitmap.PixelWidth; $h = $bitmap.PixelHeight
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $w * 4; $pixels = [byte[]]::new($stride * $h); $converted.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $ms = [IO.MemoryStream]::new(); $bw = [IO.BinaryWriter]::new($ms)
    $bw.Write([int32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]0); $bw.Write([int32]($stride * $h + $maskStride * $h)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $stride) }
    $bw.Write([byte[]]::new($maskStride * $h))
    $bw.Flush(); return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bmp = Draw-Mark $size
    [byte[]] $data = if ($size -ge 256) { Encode-Png $bmp } else { Encode-Bmp32 $bmp }
    [pscustomobject]@{ Size = $size; Data = $data; Bitmap = $bmp }
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
$ms = [IO.MemoryStream]::new(); $bw = [IO.BinaryWriter]::new($ms)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]$f.Data.Length); $bw.Write([int32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f.Data) }
$bw.Flush(); [IO.File]::WriteAllBytes($Output, $ms.ToArray())
"wrote $Output ($([int]($ms.Length / 1KB)) KB, sizes $($sizes -join ', '))"

if ($PreviewPng) {
    # A contact sheet of every size on the chrome colour, for a look.
    $panel = [Windows.Controls.StackPanel]::new(); $panel.Orientation = 'Horizontal'; $panel.Background = [Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(0x30, 0x34, 0x3C))
    foreach ($f in $frames) { $img = [Windows.Controls.Image]::new(); $img.Source = $f.Bitmap; $img.Width = $f.Size; $img.Height = $f.Size; $img.Margin = '10'; $img.VerticalAlignment = 'Bottom'; [Windows.Media.RenderOptions]::SetBitmapScalingMode($img, 'NearestNeighbor'); $panel.Children.Add($img) | Out-Null }
    $panel.Measure([Windows.Size]::new(4000, 400)); $panel.Arrange([Windows.Rect]::new(0, 0, $panel.DesiredSize.Width, $panel.DesiredSize.Height)); $panel.UpdateLayout()
    $rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$panel.ActualWidth, [int]$panel.ActualHeight, 96, 96, [Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($panel)
    [IO.File]::WriteAllBytes($PreviewPng, (Encode-Png $rtb)); "preview $PreviewPng"
}
