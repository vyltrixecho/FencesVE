<#
.SYNOPSIS
    Generuje wielorozmiarowa ikone FencesVE (.ico).

.DESCRIPTION
    Rysuje koncept "Kafle": dwa zachodzace na siebie zaokraglone panele,
    tylny polprzezroczysty, kazdy wypelniony wlasnym gradientem grafitowym.

    Kazdy rozmiar jest rysowany w czterokrotnym powiekszeniu i dopiero potem
    zmniejszany dwuszescienne - rysowanie wprost w 16 px daje poszarpane krawedzie.

    Wynik: src\FencesVE\Assets\FencesVE.ico (PNG w srodku, Vista+).

.EXAMPLE
    .\tools\make-icon.ps1
#>

[CmdletBinding()]
param(
    [string]$OutPath,
    [int[]]$Sizes = @(256, 64, 48, 40, 32, 24, 20, 16)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# $PSScriptRoot nie jest jeszcze ustawiony podczas obliczania wartosci domyslnych param().
if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot '..\src\FencesVE\Assets\FencesVE.ico'
}

# Paleta "Grafit" z propozycji ikon.
$GradFrom = [System.Drawing.Color]::FromArgb(255, 0x9F, 0xB3, 0xC6)
$GradTo   = [System.Drawing.Color]::FromArgb(255, 0x33, 0x42, 0x4F)

# Geometria w ukladzie 256x256 (jak w viewBox z propozycji).
$BackRect  = @{ X = 24.0; Y = 40.0;  W = 136.0; H = 100.0; R = 22.0; Alpha = 0.42 }
$FrontRect = @{ X = 96.0; Y = 116.0; W = 136.0; H = 100.0; R = 22.0; Alpha = 1.00 }

function New-RoundedPath {
    param([double]$X, [double]$Y, [double]$W, [double]$H, [double]$R)

    $d = $R * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([float]$X,          [float]$Y,          [float]$d, [float]$d, 180, 90)
    $path.AddArc([float]($X+$W-$d),  [float]$Y,          [float]$d, [float]$d, 270, 90)
    $path.AddArc([float]($X+$W-$d),  [float]($Y+$H-$d),  [float]$d, [float]$d,   0, 90)
    $path.AddArc([float]$X,          [float]($Y+$H-$d),  [float]$d, [float]$d,  90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon {
    param([System.Drawing.Graphics]$G, [double]$Scale)

    $G.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $G.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    foreach ($r in @($BackRect, $FrontRect)) {
        $x = $r.X * $Scale; $y = $r.Y * $Scale
        $w = $r.W * $Scale; $h = $r.H * $Scale
        $rad = $r.R * $Scale

        $a = [int][Math]::Round(255 * $r.Alpha)
        $from = [System.Drawing.Color]::FromArgb($a, $GradFrom.R, $GradFrom.G, $GradFrom.B)
        $to   = [System.Drawing.Color]::FromArgb($a, $GradTo.R,   $GradTo.G,   $GradTo.B)

        # Gradient liczony wzgledem wlasnego prostokata - tak samo jak
        # objectBoundingBox w SVG, z ktorego pochodzi projekt.
        $rect = New-Object System.Drawing.RectangleF([float]$x, [float]$y, [float]$w, [float]$h)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect, $from, $to, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)

        $path = New-RoundedPath -X $x -Y $y -W $w -H $h -R $rad
        $G.FillPath($brush, $path)

        $path.Dispose()
        $brush.Dispose()
    }
}

function New-SizedPng {
    param([int]$Size)

    $ss = 4
    $big = New-Object System.Drawing.Bitmap(($Size * $ss), ($Size * $ss),
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Icon -G $g -Scale ($Size * $ss / 256.0)
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap($Size, $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gs = [System.Drawing.Graphics]::FromImage($small)
    $gs.Clear([System.Drawing.Color]::Transparent)
    $gs.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gs.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gs.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $gs.DrawImage($big, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    $gs.Dispose()
    $big.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()

    return $ms.ToArray()
}

# ---- sklejanie pliku .ico ----------------------------------------------------

$images = @{}
foreach ($size in $Sizes) {
    $images[$size] = New-SizedPng -Size $size
    Write-Host ("  {0,3} px -> {1,6:N0} B" -f $size, $images[$size].Length) -ForegroundColor DarkGray
}

$outDir = Split-Path -Parent $OutPath
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)

try {
    # ICONDIR
    $bw.Write([uint16]0)               # zarezerwowane
    $bw.Write([uint16]1)               # typ: ikona
    $bw.Write([uint16]$Sizes.Count)

    # Dane obrazow zaczynaja sie za katalogiem.
    $offset = 6 + (16 * $Sizes.Count)

    foreach ($size in $Sizes) {
        $bytes = $images[$size]
        $dim = if ($size -ge 256) { 0 } else { $size }   # 0 oznacza 256

        $bw.Write([byte]$dim)          # szerokosc
        $bw.Write([byte]$dim)          # wysokosc
        $bw.Write([byte]0)             # liczba kolorow palety
        $bw.Write([byte]0)             # zarezerwowane
        $bw.Write([uint16]1)           # plaszczyzny
        $bw.Write([uint16]32)          # bitow na piksel
        $bw.Write([uint32]$bytes.Length)
        $bw.Write([uint32]$offset)

        $offset += $bytes.Length
    }

    foreach ($size in $Sizes) {
        # Jawne przeciazenie Write(byte[], int, int) - przy samym Write($tablica)
        # PowerShell potrafi wybrac Write(byte) i zapisac jeden bajt zamiast calosci.
        [byte[]]$bytes = $images[$size]
        $bw.Write($bytes, 0, $bytes.Length)
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

$final = Get-Item $OutPath
Write-Host ("Zapisano {0}  ({1:N0} B, {2} rozmiarow)" -f $final.FullName, $final.Length, $Sizes.Count) -ForegroundColor Green
