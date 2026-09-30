<#
.SYNOPSIS
    Wycina banner Vyltrix Echo z materialu zrodlowego do stopki okna ustawien.

.DESCRIPTION
    Logotyp nie jest rysowany przez skrypt (to cudza grafika, nie koncept do
    odtworzenia) - skrypt tylko kadruje go z materialu zrodlowego zawsze tak samo,
    zeby kadr dalo sie powtorzyc po podmianie zrodla.

    Zrodlem jest reklama 1024x807, bo lockup jest w niej okolo poltora raza wiekszy
    niz w osobnym pliku logo (384x256) - napis "VYLTRIX ECHO" ma tam realne piksele
    zamiast powiekszonej papki.

    Granice kadru sa wpisane na sztywno, bo zostaly zmierzone profilem jasnosci
    wiersz po wierszu. Automatyczne progowanie jasnosci tu nie dziala: dolna
    krawedz heksagonu schodzi gradientem w ciemna czerwien, wypada ponizej kazdego
    sensownego progu i kadr scinal sześciokatowi spod. Przy zmianie zrodla trzeba
    zmierzyc granice od nowa (-Measure).

    Wynik: src\FencesVE\Assets\VyltrixEcho.png

.PARAMETER Measure
    Nie zapisuje pliku, tylko wypisuje profil jasnosci wierszy i kolumn w obszarze
    poszukiwan - od tego zaczyna sie dobieranie granic dla nowego zrodla.

.PARAMETER Source
    Plik zrodlowy z lockupem. Material marki nie lezy w repozytorium - sciezke podaje sie
    przy wywolaniu albo ustawia w zmiennej srodowiskowej VYLTRIX_BANNER_SOURCE.

.EXAMPLE
    .\tools\make-banner.ps1 -Source 'D:\brand\reklama.png'

.EXAMPLE
    .\tools\make-banner.ps1 -Source 'D:\brand\reklama.png' -Measure
#>

[CmdletBinding()]
param(
    [string]$Source = $env:VYLTRIX_BANNER_SOURCE,
    [string]$OutPath,
    [switch]$Measure
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $Source) {
    throw 'Podaj -Source ze sciezka do materialu zrodlowego albo ustaw VYLTRIX_BANNER_SOURCE.'
}

# $PSScriptRoot nie jest jeszcze ustawiony podczas obliczania wartosci domyslnych param().
if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot '..\src\FencesVE\Assets\VyltrixEcho.png'
}

# Zmierzone granice lockupu w materiale zrodlowym: tresc siega y 48..143, x 54..347.
# Do tego rowny margines, zeby heksagon nie dotykal ramki kontrolki.
$Crop = @{ X0 = 42; Y0 = 38; X1 = 359; Y1 = 153 }

# Obszar, w ktorym w ogole szukamy logotypu przy -Measure: naglowek reklamy
# zaczyna sie nizej, a iskry sa po prawej - jedno i drugie zaburzyloby pomiar.
$Search = @{ X0 = 10; Y0 = 10; X1 = 420; Y1 = 172 }

if (-not (Test-Path $Source)) {
    throw "Nie znaleziono materialu zrodlowego: $Source"
}

$bitmap = New-Object System.Drawing.Bitmap $Source

try {
    if ($Measure) {
        function Get-MaxLuminance {
            param([int]$Fixed, [int]$From, [int]$To, [switch]$AlongRow)

            $max = 0
            for ($i = $From; $i -le $To; $i++) {
                $pixel = if ($AlongRow) { $bitmap.GetPixel($i, $Fixed) } else { $bitmap.GetPixel($Fixed, $i) }
                $lum = [int](0.299 * $pixel.R + 0.587 * $pixel.G + 0.114 * $pixel.B)
                if ($lum -gt $max) { $max = $lum }
            }
            return $max
        }

        'wiersze (y: najjasniejszy piksel)'
        for ($y = $Search.Y0; $y -le $Search.Y1; $y++) {
            $max = Get-MaxLuminance -Fixed $y -From $Search.X0 -To $Search.X1 -AlongRow
            if ($max -gt 12) { "  y=$y  max=$max" }
        }

        'kolumny (x: najjasniejszy piksel)'
        for ($x = $Search.X0; $x -le $Search.X1; $x++) {
            $max = Get-MaxLuminance -Fixed $x -From $Search.Y0 -To $Search.Y1
            if ($max -gt 12) { "  x=$x  max=$max" }
        }

        return
    }

    $rect = New-Object System.Drawing.Rectangle `
        $Crop.X0, $Crop.Y0, ($Crop.X1 - $Crop.X0 + 1), ($Crop.Y1 - $Crop.Y0 + 1)

    $crop = $bitmap.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $resolved = [System.IO.Path]::GetFullPath($OutPath)
        $crop.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("{0}  ({1} x {2})" -f $resolved, $crop.Width, $crop.Height) -ForegroundColor Green
    }
    finally {
        $crop.Dispose()
    }
}
finally {
    $bitmap.Dispose()
}
