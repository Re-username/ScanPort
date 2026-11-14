# Generates app.ico (radar-style) for PortScanner via System.Drawing.
# Renders multiple sizes (16..256) into one ICO so every Explorer view is crisp.
param(
    [Parameter(Mandatory = $true)][string]$IcoOut,
    [string]$PngPreviewOut
)

Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$S) {
    $k = $S / 256.0
    $c = $S / 2.0
    $bmp = New-Object System.Drawing.Bitmap $S, $S
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    # rounded-square clip
    $r = [int](56 * $k)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($S - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($S - 2 * $r, $S - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $S - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)

    # background: dark navy gradient
    $rect = New-Object System.Drawing.Rectangle 0, 0, $S, $S
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 22, 32, 46)),
        ([System.Drawing.Color]::FromArgb(255, 11, 15, 22)), 45.0
    $g.FillRectangle($bg, $rect)

    $faintPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 88, 166, 255)), ([Math]::Max(1, 2 * $k))
    $g.DrawLine($faintPen, 0, $c, $S, $c)
    $g.DrawLine($faintPen, $c, 0, $c, $S)

    # radar rings
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(190, 88, 166, 255)), ([Math]::Max(1.5, 5 * $k))
    foreach ($rad in 38.0, 68.0, 98.0) {
        $rr = $rad * $k
        $g.DrawEllipse($ringPen, ($c - $rr), ($c - $rr), (2 * $rr), (2 * $rr))
    }

    # sweep wedge
    $R = 98 * $k
    $piePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $piePath.AddPie(($c - $R), ($c - $R), (2 * $R), (2 * $R), -55, 55)
    $wedge = New-Object System.Drawing.Drawing2D.PathGradientBrush $piePath
    $wedge.CenterPoint = New-Object System.Drawing.PointF $c, $c
    $wedge.CenterColor = [System.Drawing.Color]::FromArgb(170, 88, 166, 255)
    $wedge.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 88, 166, 255))
    $g.FillPath($wedge, $piePath)

    # leading edge
    $ang = -55.0 * [Math]::PI / 180.0
    $ex = $c + ($R - 2) * [Math]::Cos($ang)
    $ey = $c + ($R - 2) * [Math]::Sin($ang)
    $edgePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 170, 208, 255)), ([Math]::Max(1.5, 7 * $k))
    $g.DrawLine($edgePen, $c, $c, $ex, $ey)

    # center dot
    $cd = [Math]::Max(2, 9 * $k)
    $cdBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 219, 233, 255))
    $g.FillEllipse($cdBrush, ($c - $cd), ($c - $cd), (2 * $cd), (2 * $cd))

    # open ports (green dots)
    $dot = [Math]::Max(2, 8 * $k)
    $okBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 185, 80))
    $okRing  = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 160, 235, 175)), ([Math]::Max(1, 3 * $k))
    foreach ($pt in @(@(183, 98), @(96, 172), @(150, 196))) {
        $px = [double]$pt[0] * $k; $py = [double]$pt[1] * $k
        $g.FillEllipse($okBrush, ($px - $dot), ($py - $dot), (2 * $dot), (2 * $dot))
        $g.DrawEllipse($okRing, ($px - $dot), ($py - $dot), (2 * $dot), (2 * $dot))
    }

    $g.Dispose()
    return $bmp
}

function Png-Bytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# --- build ICO with all sizes ---
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $images[$s] = Png-Bytes $bmp
    if ($s -eq 256 -and $PngPreviewOut) { $bmp.Save($PngPreviewOut, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ms
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bytes = $images[$s]
    $dim = if ($s -ge 256) { [Byte]0 } else { [Byte]$s }
    $bw.Write($dim); $bw.Write($dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($s in $sizes) { $bw.Write([byte[]]$images[$s]) }
[IO.File]::WriteAllBytes($IcoOut, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host ("Icon written: {0} ({1} KB, sizes: {2})" -f $IcoOut, [math]::Round((Get-Item $IcoOut).Length / 1KB, 1), ($sizes -join ','))
