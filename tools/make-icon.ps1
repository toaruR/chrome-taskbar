param([string]$OutIco, [string]$PreviewDir)
Add-Type -AssemblyName System.Drawing

function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Render([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $n / 256.0
    $g.ScaleTransform($s, $s)

    # Body: soft pastel rounded square (mochi)
    $body = RR 8 8 240 240 64
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 8), (New-Object System.Drawing.PointF 0, 248), (C '#B9A7FF'), (C '#FF9EC7'))
    $g.FillPath($br, $body)
    if ($n -ge 32) {
        # glossy highlight
        $hl = RR 34 22 120 34 17
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))), $hl)
    }

    $simple = $n -lt 32
    if (-not $simple) {
        # Face
        $eye = New-Object System.Drawing.SolidBrush (C '#3B2A55')
        $g.FillEllipse($eye, 70, 82, 30, 38); $g.FillEllipse($eye, 156, 82, 30, 38)
        $w = [System.Drawing.Brushes]::White
        $g.FillEllipse($w, 78, 88, 11, 12); $g.FillEllipse($w, 164, 88, 11, 12)
        $blush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 110, 160))
        $g.FillEllipse($blush, 44, 120, 36, 20); $g.FillEllipse($blush, 176, 120, 36, 20)
        $pen = New-Object System.Drawing.Pen (C '#3B2A55'), 8
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, 112, 104, 16, 16, 0, 180); $g.DrawArc($pen, 128, 104, 16, 16, 0, 180)
    }

    # Taskbar strip with three window buttons
    if ($simple) { $by = 120; $bh = 96 } else { $by = 160; $bh = 64 }
    $bar = RR 28 $by 200 $bh ([Math]::Min(26, $bh / 2))
    $g.FillPath([System.Drawing.Brushes]::White, $bar)
    $cols = '#FF6FA5', '#FFC94D', '#5ED6B4'
    $pad = 14; $bw = (200 - 2 * $pad - 2 * 10) / 3.0
    for ($i = 0; $i -lt 3; $i++) {
        $x = 28 + $pad + $i * ($bw + 10)
        $btn = RR $x ($by + $pad) $bw ($bh - 2 * $pad) ([Math]::Min(12, ($bh - 2 * $pad) / 2))
        $g.FillPath((New-Object System.Drawing.SolidBrush (C $cols[$i])), $btn)
    }
    if ($simple) {
        $eye = New-Object System.Drawing.SolidBrush (C '#3B2A55')
        $g.FillEllipse($eye, 76, 52, 30, 40); $g.FillEllipse($eye, 150, 52, 30, 40)
    }
    $g.Dispose(); $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($n in $sizes) {
    $b = Render $n
    $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $b.Save((Join-Path $PreviewDir "icon-$n.png")) }
    $pngs += , $ms.ToArray(); $b.Dispose()
}

# ICO container with PNG-encoded entries
$fs = [System.IO.File]::Create($OutIco); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $dim = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
