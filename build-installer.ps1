<#
.SYNOPSIS
    Buduje FencesVE w trybie Release i pakuje go w instalator .exe.

.DESCRIPTION
    Publikuje aplikacje jako pojedynczy plik, odczytuje wersje z gotowego .exe,
    a nastepnie uruchamia kompilator Inno Setup. Wynik ladzie w katalogu dist\.

.EXAMPLE
    .\build-installer.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$project    = Join-Path $root 'src\FencesVE\FencesVE.csproj'
$publishDir = Join-Path $root 'src\FencesVE\bin\Release\net10.0-windows\win-x64\publish'
$exePath    = Join-Path $publishDir 'FencesVE.exe'
$issPath    = Join-Path $root 'installer\FencesVE.iss'
$distDir    = Join-Path $root 'dist'

function Find-Iscc {
    # winget potrafi zainstalowac Inno Setup w profilu uzytkownika zamiast w Program Files.
    $bases = @(
        "${env:ProgramFiles(x86)}",
        "$env:ProgramFiles",
        "$env:LOCALAPPDATA\Programs"
    )

    $candidates = foreach ($base in $bases) {
        foreach ($version in @('Inno Setup 6', 'Inno Setup 7')) {
            Join-Path $base "$version\ISCC.exe"
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw "Nie znaleziono kompilatora Inno Setup (ISCC.exe). Zainstaluj: winget install JRSoftware.InnoSetup"
}

# Publish nie nadpisze pliku, ktory trzyma dzialajaca aplikacja -
# GenerateBundle konczy sie wtedy bledem dostepu i zostawia uszkodzony .exe.
$running = Get-Process FencesVE -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Zamykam dzialajaca instancje FencesVE...' -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

Write-Host '[1/3] Publikuje aplikacje...' -ForegroundColor Cyan
& dotnet publish $project -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakonczyl sie bledem ($LASTEXITCODE)." }
if (-not (Test-Path $exePath)) { throw "Brak pliku $exePath." }

$version = (Get-Item $exePath).VersionInfo.FileVersion
if (-not $version) { throw 'Nie udalo sie odczytac wersji z pliku .exe.' }

# Inno chce wersji w formacie x.y.z(.w) - FileVersion daje juz cztery czlony.
Write-Host "      wersja: $version" -ForegroundColor DarkGray

Write-Host '[2/3] Skladam instalator...' -ForegroundColor Cyan
$iscc = Find-Iscc
Write-Host "      ISCC: $iscc" -ForegroundColor DarkGray

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

& $iscc "/DAppVersion=$version" $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup zakonczyl sie bledem ($LASTEXITCODE)." }

Write-Host '[3/3] Gotowe.' -ForegroundColor Green
Get-ChildItem $distDir -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object {
        Write-Host ("      {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
    }
