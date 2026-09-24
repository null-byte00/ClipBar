Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size, [bool]$recording) {
    $ss = 4
    $S = $size * $ss
    $big = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $margin = [Math]::Round($S * 0.03)
    if ($size -le 24) { $margin = 0 }
    $bw = $S - 2 * $margin
    $radius = $bw * 0.22
    $rect = New-RoundedPath $margin $margin $bw $bw $radius
    if ($recording) {
        $c1 = [System.Drawing.Color]::FromArgb(255, 240, 30, 50)
        $c2 = [System.Drawing.Color]::FromArgb(255, 196, 12, 30)
    } else {
        $c1 = [System.Drawing.Color]::FromArgb(255, 48, 48, 48)
        $c2 = [System.Drawing.Color]::FromArgb(255, 24, 24, 24)
    }
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $S), $c1, $c2)
    $g.FillPath($lg, $rect)
    $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(56, 255, 255, 255)), ([float]($S * 0.012))
    $inner = New-RoundedPath ($margin + $borderPen.Width / 2) ($margin + $borderPen.Width / 2) ($bw - $borderPen.Width) ($bw - $borderPen.Width) ($radius - $borderPen.Width / 2)
    $g.DrawPath($borderPen, $inner)

    $cx = $S / 2.0; $cy = $S / 2.0

    if ($recording) {
        $r = $S * 0.24
        $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), ([float]($S * 0.05))
        $g.DrawEllipse($ring, [float]($cx - $r * 1.45), [float]($cy - $r * 1.45), [float]($r * 2.9), [float]($r * 2.9))
        $g.FillEllipse([System.Drawing.Brushes]::White, [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2))
    } else {
        $R = $S * 0.30
        $stroke = [Math]::Max($ss * 1.7, $S * 0.095)
        if ($size -le 16) { $R = $S * 0.31; $stroke = $ss * 1.9 }
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 245, 245, 245)), ([float]$stroke)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($pen, [float]($cx - $R), [float]($cy - $R), [float]($R * 2), [float]($R * 2), -52, 284)
        $endAngle = [Math]::PI * 232.0 / 180.0
        $ex = $cx + $R * [Math]::Cos($endAngle); $ey = $cy + $R * [Math]::Sin($endAngle)
        $dx = [Math]::Sin($endAngle); $dy = -[Math]::Cos($endAngle)
        $nx = -$dy; $ny = $dx
        $L = $stroke * 1.9; $W = $stroke * 1.25; $back = $stroke * 0.15
        $tip = New-Object System.Drawing.PointF ([float]($ex + $dx * $L)), ([float]($ey + $dy * $L))
        $b1 = New-Object System.Drawing.PointF ([float]($ex + $nx * $W - $dx * $back)), ([float]($ey + $ny * $W - $dy * $back))
        $b2 = New-Object System.Drawing.PointF ([float]($ex - $nx * $W - $dx * $back)), ([float]($ey - $ny * $W - $dy * $back))
        $g.FillPolygon([System.Drawing.Brushes]::WhiteSmoke, [System.Drawing.PointF[]]@($tip, $b1, $b2))

        $r = $S * 0.13
        if ($size -le 16) { $r = $S * 0.15 }
        $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 17, 35))
        $g.FillEllipse($red, [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2))
        $hl = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
        $g.FillEllipse($hl, [float]($cx - $r * 0.55), [float]($cy - $r * 0.75), [float]($r * 0.9), [float]($r * 0.6))
    }
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g2.DrawImage($big, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $g2.Dispose(); $big.Dispose()
    return $small
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $maskStride = ((($w + 31) -shr 5) -shl 2)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]0)
    $bw.Write([int32]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $w * 4)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $bw.Write((New-Object byte[] ($maskStride * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

function Write-Ico([string]$path, [bool]$recording) {
    $sizes = 16, 24, 32, 48, 256
    $entries = @()
    foreach ($s in $sizes) {
        $bmp = Draw-Icon $s $recording
        [byte[]]$bytes = if ($s -ge 256) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
        $entries += ,@{ Size = $s; Bytes = $bytes }
        $bmp.Dispose()
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int32]$e.Bytes.Length); $bw.Write([int32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { [byte[]]$eb = $e.Bytes; $bw.Write($eb, 0, $eb.Length) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    "wrote $path ($($ms.Length) bytes)"
}

Write-Ico (Join-Path $out 'clipbar.ico') $false
Write-Ico (Join-Path $out 'clipbar-rec.ico') $true
$b = Draw-Icon 256 $false; $b.Save((Join-Path $out 'clipbar.png'), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
$b = Draw-Icon 256 $true;  $b.Save((Join-Path $out 'clipbar-rec.png'), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
$sheet = New-Object System.Drawing.Bitmap 420, 120
$g = [System.Drawing.Graphics]::FromImage($sheet); $g.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
$x = 10
foreach ($s in 16, 24, 32, 48) { $b = Draw-Icon $s $false; $g.DrawImageUnscaled($b, $x, 60 - [int]($s / 2)); $b.Dispose(); $x += $s + 14 }
$x += 20
foreach ($s in 16, 24, 32, 48) { $b = Draw-Icon $s $true; $g.DrawImageUnscaled($b, $x, 60 - [int]($s / 2)); $b.Dispose(); $x += $s + 14 }
$g.Dispose()
$sheet.Save((Join-Path $env:TEMP 'clipbar-icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
"preview: $env:TEMP\clipbar-icon-preview.png"
