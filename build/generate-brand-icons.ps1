<#
.SYNOPSIS
Renders crisp PNG assets and a multi-size PNG-compressed Windows .ico directly from
assets/branding/shima-cradled-island.svg using WPF vector rasterization (zero System.Drawing).
#>
[CmdletBinding()]
param(
    [string]$SvgPath,

    [string]$SparseAssetsDir,

    [string]$IcoOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SvgPath)) {
    $SvgPath = Join-Path $repoRoot 'assets\branding\shima-cradled-island.svg'
}
if ([string]::IsNullOrWhiteSpace($SparseAssetsDir)) {
    $SparseAssetsDir = Join-Path $repoRoot 'build\sparse-msix\Assets'
}
if ([string]::IsNullOrWhiteSpace($IcoOutputPath)) {
    $IcoOutputPath = Join-Path $repoRoot 'src\Shima.App\Assets\Shima.ico'
}

if (-not (Test-Path $SvgPath)) {
    throw "Source SVG not found at '$SvgPath'."
}

[xml]$svgDoc = Get-Content -Path $SvgPath -Raw -Encoding UTF8
$svgRoot = $svgDoc.DocumentElement
if ($null -eq $svgRoot -or $svgRoot.LocalName -ne 'svg') {
    throw "Invalid SVG root element in '$SvgPath'."
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Parse-Double {
    param([string]$Value, [double]$Default = 0.0)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Default
    }
    return [double]::Parse($Value.Trim(), $invariant)
}

function Parse-Brush {
    param([string]$Fill)
    if ([string]::IsNullOrWhiteSpace($Fill) -or $Fill.Trim().Equals('none', [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    $color = [System.Windows.Media.ColorConverter]::ConvertFromString($Fill.Trim())
    $brush = New-Object System.Windows.Media.SolidColorBrush($color)
    $brush.Freeze()
    return $brush
}

function Parse-SvgTransform {
    param([string]$TransformAttr)
    if ([string]::IsNullOrWhiteSpace($TransformAttr)) {
        return $null
    }

    $matches = [regex]::Matches($TransformAttr, '([a-zA-Z]+)\s*\(([^)]+)\)')
    if ($matches.Count -eq 0) {
        return $null
    }

    # SVG transform lists apply right-to-left; WPF TransformGroup applies forward (index 0 first).
    $group = New-Object System.Windows.Media.TransformGroup
    for ($i = $matches.Count - 1; $i -ge 0; $i--) {
        $fn = $matches[$i].Groups[1].Value.ToLowerInvariant()
        $nums = @([regex]::Matches($matches[$i].Groups[2].Value, '[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?') |
            ForEach-Object { [double]::Parse($_.Value, $invariant) })

        switch ($fn) {
            'translate' {
                $tx = if ($nums.Count -ge 1) { $nums[0] } else { 0.0 }
                $ty = if ($nums.Count -ge 2) { $nums[1] } else { 0.0 }
                $group.Children.Add((New-Object System.Windows.Media.TranslateTransform($tx, $ty)))
            }
            'scale' {
                $sx = if ($nums.Count -ge 1) { $nums[0] } else { 1.0 }
                $sy = if ($nums.Count -ge 2) { $nums[1] } else { $sx }
                $group.Children.Add((New-Object System.Windows.Media.ScaleTransform($sx, $sy)))
            }
            'rotate' {
                $angle = if ($nums.Count -ge 1) { $nums[0] } else { 0.0 }
                $cx = if ($nums.Count -ge 3) { $nums[1] } else { 0.0 }
                $cy = if ($nums.Count -ge 3) { $nums[2] } else { 0.0 }
                $group.Children.Add((New-Object System.Windows.Media.RotateTransform($angle, $cx, $cy)))
            }
            default {
                throw "Unsupported SVG transform function '$fn' in '$TransformAttr'."
            }
        }
    }

    $group.Freeze()
    return $group
}

function Render-SvgNode {
    param(
        [System.Xml.XmlElement]$Node,
        [System.Windows.Media.DrawingContext]$Dc
    )

    $transform = Parse-SvgTransform -TransformAttr $Node.GetAttribute('transform')
    if ($null -ne $transform) {
        $Dc.PushTransform($transform)
    }

    try {
        switch ($Node.LocalName.ToLowerInvariant()) {
            'rect' {
                $x = Parse-Double $Node.GetAttribute('x') 0.0
                $y = Parse-Double $Node.GetAttribute('y') 0.0
                $w = Parse-Double $Node.GetAttribute('width') 0.0
                $h = Parse-Double $Node.GetAttribute('height') 0.0
                $rx = Parse-Double $Node.GetAttribute('rx') 0.0
                $ry = Parse-Double $Node.GetAttribute('ry') $rx
                $brush = Parse-Brush $Node.GetAttribute('fill')
                if ($null -ne $brush -and $w -gt 0 -and $h -gt 0) {
                    $rect = New-Object System.Windows.Rect($x, $y, $w, $h)
                    $Dc.DrawRoundedRectangle($brush, $null, $rect, $rx, $ry)
                }
            }
            'path' {
                $d = $Node.GetAttribute('d')
                $brush = Parse-Brush $Node.GetAttribute('fill')
                if ($null -ne $brush -and -not [string]::IsNullOrWhiteSpace($d)) {
                    $geom = [System.Windows.Media.Geometry]::Parse($d)
                    $geom.Freeze()
                    $Dc.DrawGeometry($brush, $null, $geom)
                }
            }
            'g' {
                foreach ($child in $Node.ChildNodes) {
                    if ($child -is [System.Xml.XmlElement]) {
                        Render-SvgNode -Node $child -Dc $Dc
                    }
                }
            }
            'svg' {
                foreach ($child in $Node.ChildNodes) {
                    if ($child -is [System.Xml.XmlElement]) {
                        Render-SvgNode -Node $child -Dc $Dc
                    }
                }
            }
        }
    }
    finally {
        if ($null -ne $transform) {
            $Dc.Pop()
        }
    }
}

$vbAttr = $svgRoot.GetAttribute('viewBox')
$vbParts = @([regex]::Matches($vbAttr, '[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?') |
    ForEach-Object { [double]::Parse($_.Value, $invariant) })
if ($vbParts.Count -ne 4) {
    throw "Expected 4-part viewBox in '$SvgPath', found '$vbAttr'."
}

$vbX = $vbParts[0]
$vbY = $vbParts[1]
$vbW = $vbParts[2]
$vbH = $vbParts[3]

function Render-SvgToPngBytes {
    param([int]$SizePx)

    $visual = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)

    $dc = $visual.RenderOpen()
    try {
        # Clip cleanly to the exact output square and map SVG viewBox into [0, SizePx] x [0, SizePx].
        $clip = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(0, 0, $SizePx, $SizePx)))
        $clip.Freeze()
        $dc.PushClip($clip)

        $vbTransform = New-Object System.Windows.Media.TransformGroup
        $vbTransform.Children.Add((New-Object System.Windows.Media.TranslateTransform(-$vbX, -$vbY)))
        $vbTransform.Children.Add((New-Object System.Windows.Media.ScaleTransform(($SizePx / $vbW), ($SizePx / $vbH))))
        $vbTransform.Freeze()
        $dc.PushTransform($vbTransform)

        Render-SvgNode -Node $svgRoot -Dc $dc

        $dc.Pop()
        $dc.Pop()
    }
    finally {
        $dc.Close()
    }

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $SizePx,
        $SizePx,
        96.0,
        96.0,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $rtb.Freeze()

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))

    $ms = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($ms)
        return ,$ms.ToArray()
    }
    finally {
        $ms.Dispose()
    }
}

function Write-MultiSizeIco {
    param(
        [string]$Path,
        [int[]]$Sizes
    )

    $parentDir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parentDir)) {
        New-Item -ItemType Directory -Force -Path $parentDir | Out-Null
    }

    $frames = New-Object System.Collections.Generic.List[object]
    foreach ($size in $Sizes) {
        [byte[]]$pngBytes = Render-SvgToPngBytes -SizePx $size
        $frames.Add([pscustomobject]@{
            Size  = $size
            Bytes = $pngBytes
        })
    }

    $ms = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($ms)
    try {
        # ICONDIR header (6 bytes)
        $writer.Write([uint16]0)               # idReserved
        $writer.Write([uint16]1)               # idType = 1 (ICON)
        $writer.Write([uint16]$frames.Count)   # idCount

        # ICONDIRENTRY table (16 bytes per frame)
        [uint32]$offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            [byte[]]$payload = $frame.Bytes
            [byte]$dim = if ($frame.Size -ge 256) { 0 } else { [byte]$frame.Size }
            $writer.Write($dim)                    # bWidth (0 means 256)
            $writer.Write($dim)                    # bHeight (0 means 256)
            $writer.Write([byte]0)                 # bColorCount
            $writer.Write([byte]0)                 # bReserved
            $writer.Write([uint16]1)               # wPlanes
            $writer.Write([uint16]32)              # wBitCount (32-bit RGBA)
            $writer.Write([uint32]$payload.Length) # dwBytesInRes
            $writer.Write($offset)                 # dwImageOffset
            $offset += [uint32]$payload.Length
        }

        # PNG payloads
        foreach ($frame in $frames) {
            [byte[]]$payload = $frame.Bytes
            $writer.Write($payload)
        }

        $writer.Flush()
        [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    }
    finally {
        $writer.Dispose()
        $ms.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $SparseAssetsDir | Out-Null

$sparseOutputs = [ordered]@{
    'StoreLogo.png'         = 50
    'Square44x44Logo.png'   = 44
    'Square150x150Logo.png' = 150
}

foreach ($entry in $sparseOutputs.GetEnumerator()) {
    $outPath = Join-Path $SparseAssetsDir $entry.Key
    [byte[]]$png = Render-SvgToPngBytes -SizePx $entry.Value
    [System.IO.File]::WriteAllBytes($outPath, $png)
    Write-Host "Generated $outPath ($($entry.Value)x$($entry.Value), $($png.Length) bytes)"
}

$icoSizes = @(16, 24, 32, 48, 64, 256)
Write-MultiSizeIco -Path $IcoOutputPath -Sizes $icoSizes
$icoLen = (Get-Item $IcoOutputPath).Length
Write-Host "Generated $IcoOutputPath ($($icoSizes -join ', ') px PNG frames, $icoLen bytes)"
