# SPDX-License-Identifier: MPL-2.0
param([string]$Destination = (Join-Path $PSScriptRoot '..\assets\branding'), [string]$Preview = (Join-Path $PSScriptRoot '..\artifacts\branding\app-icon-preview.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$Destination = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($Destination) | Out-Null
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Preview))) | Out-Null
$svg = @'
<!-- SPDX-License-Identifier: MPL-2.0 -->
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs><linearGradient id="blue" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#2897ff"/><stop offset="1" stop-color="#1555d8"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="56" fill="url(#blue)"/>
  <rect x="9" y="9" width="238" height="238" rx="55" fill="none" stroke="white" stroke-opacity=".22" stroke-width="2"/>
  <path fill="white" d="M110 103 203 196 185 214 161 190 148 203 130 185 143 172 101 130Z"/>
  <path fill="white" fill-rule="evenodd" d="M128 92a36 36 0 1 1-72 0 36 36 0 0 1 72 0M106 92a14 14 0 1 1-28 0 14 14 0 0 1 28 0"/>
  <circle cx="184" cy="70" r="29" fill="#20bd7b" stroke="white" stroke-opacity=".9" stroke-width="3"/>
  <path d="m171 70 9 9 16-18" fill="none" stroke="white" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $Destination 'app-icon.svg'), ($svg + "`n"), [System.Text.UTF8Encoding]::new($false))
function New-RoundPath([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}
$master = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
try {
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(4, 4)
    $tile = New-RoundPath 8 8 240 240 56
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(8, 8), [System.Drawing.Point]::new(248, 248), [System.Drawing.ColorTranslator]::FromHtml('#2897ff'), [System.Drawing.ColorTranslator]::FromHtml('#1555d8'))
    $border = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(56, 255, 255, 255), 2)
    $inner = New-RoundPath 9 9 238 238 55
    try { $g.FillPath($gradient, $tile); $g.DrawPath($border, $inner) } finally { $tile.Dispose(); $inner.Dispose(); $gradient.Dispose(); $border.Dispose() }
    $stem = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(110,103), [System.Drawing.PointF]::new(203,196), [System.Drawing.PointF]::new(185,214), [System.Drawing.PointF]::new(161,190), [System.Drawing.PointF]::new(148,203), [System.Drawing.PointF]::new(130,185), [System.Drawing.PointF]::new(143,172), [System.Drawing.PointF]::new(101,130))
    $g.FillPolygon([System.Drawing.Brushes]::White, $stem)
    $ring = [System.Drawing.Drawing2D.GraphicsPath]::new([System.Drawing.Drawing2D.FillMode]::Alternate)
    try { $ring.AddEllipse(56,56,72,72); $ring.AddEllipse(78,78,28,28); $g.FillPath([System.Drawing.Brushes]::White,$ring) } finally { $ring.Dispose() }
    $green = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#20bd7b'))
    $badge = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230,255,255,255),3)
    $check = [System.Drawing.Pen]::new([System.Drawing.Color]::White,6)
    $check.StartCap = $check.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $check.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    try {
        $g.FillEllipse($green,155,41,58,58); $g.DrawEllipse($badge,155,41,58,58)
        $g.DrawLines($check,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(171,70),[System.Drawing.PointF]::new(180,79),[System.Drawing.PointF]::new(196,61)))
    } finally { $green.Dispose(); $badge.Dispose(); $check.Dispose() }
} finally { $g.Dispose() }
try {
    $master.Save([System.IO.Path]::GetFullPath($Preview), [System.Drawing.Imaging.ImageFormat]::Png)
    $sizes = @(16,20,24,28,32,36,40,48,56,64,72,80,96,128,256)
    $frames = @()
    foreach ($size in $sizes) {
        $frame = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $render = [System.Drawing.Graphics]::FromImage($frame)
        $buffer = [System.IO.MemoryStream]::new()
        try {
            $render.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $render.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $render.DrawImage($master,[System.Drawing.Rectangle]::new(0,0,$size,$size))
            $frame.Save($buffer,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,$buffer.ToArray()
        } finally { $render.Dispose(); $frame.Dispose(); $buffer.Dispose() }
    }
    $file = [System.IO.File]::Create((Join-Path $Destination 'app-icon.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index=0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($bytes in $frames) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose() }
} finally { $master.Dispose() }
Get-Item -LiteralPath (Join-Path $Destination 'app-icon.ico'), $Preview | Select-Object Name,Length
