<#
.SYNOPSIS
    Pobiera i instaluje najnowsze wydanie FencesVE.

.DESCRIPTION
    Jedna komenda w terminalu Windows:

        irm https://raw.githubusercontent.com/vyltrixecho/FencesVE/main/install.ps1 | iex

    Skrypt pyta GitHuba o najnowsze wydanie, pobiera instalator, sprawdza jego sume
    kontrolna SHA-256 i uruchamia instalacje. Nie wymaga uprawnien administratora -
    FencesVE instaluje sie w katalogu uzytkownika.

    Z parametrami (potok nie przekazuje argumentow, wiec przez scriptblock):

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/vyltrixecho/FencesVE/main/install.ps1))) -Silent

.PARAMETER Silent
    Instalacja bez okien kreatora.

.PARAMETER DownloadOnly
    Tylko pobiera i sprawdza plik, nie uruchamia instalacji. Zwraca sciezke do pliku.

.PARAMETER Version
    Konkretny tag wydania, np. 'v0.4.0'. Domyslnie najnowsze.
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$DownloadOnly,
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$Repozytorium = 'vyltrixecho/FencesVE'
$WymaganyRuntime = 'Microsoft.WindowsDesktop.App 10.'

# Windows PowerShell 5.1 domyslnie probuje starych protokolow i odbija sie od GitHuba.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Krok { param([string]$Tekst) Write-Host "==> $Tekst" -ForegroundColor Cyan }
function Uwaga { param([string]$Tekst) Write-Host "    $Tekst" -ForegroundColor Yellow }
function Dobrze { param([string]$Tekst) Write-Host "    $Tekst" -ForegroundColor Green }

# ---- sprawdzenia wstepne ---------------------------------------------------

if ([Environment]::Is64BitOperatingSystem -eq $false) {
    throw 'FencesVE jest budowany tylko dla x64.'
}

Krok 'Sprawdzam srodowisko uruchomieniowe .NET'
$maRuntime = $false
try {
    $maRuntime = @(& dotnet --list-runtimes 2>$null | Where-Object { $_ -like "$WymaganyRuntime*" }).Count -gt 0
}
catch {
    # Brak polecenia dotnet - traktujemy jak brak srodowiska.
}

if ($maRuntime) {
    Dobrze 'Jest .NET Desktop Runtime 10'
}
else {
    Uwaga 'Nie widze .NET Desktop Runtime 10 - FencesVE bez niego nie wystartuje.'
    Uwaga 'Zainstalujesz go poleceniem:'
    Uwaga '    winget install Microsoft.DotNet.DesktopRuntime.10'
    Uwaga 'Instalacja FencesVE leci dalej.'
}

# ---- wydanie ---------------------------------------------------------------

$adres = if ($Version) {
    "https://api.github.com/repos/$Repozytorium/releases/tags/$Version"
} else {
    "https://api.github.com/repos/$Repozytorium/releases/latest"
}

Krok 'Pytam GitHuba o wydanie'
$wydanie = Invoke-RestMethod -Uri $adres -Headers @{ 'User-Agent' = 'FencesVE-Installer' }
Dobrze "$($wydanie.name) ($($wydanie.tag_name))"

$instalator = $wydanie.assets | Where-Object { $_.name -like '*setup.exe' } | Select-Object -First 1
if (-not $instalator) { throw "Wydanie $($wydanie.tag_name) nie ma zalacznika z instalatorem." }

# ---- pobieranie ------------------------------------------------------------

$katalog = Join-Path ([IO.Path]::GetTempPath()) "FencesVE-$($wydanie.tag_name)"
New-Item -ItemType Directory -Force -Path $katalog | Out-Null
$plik = Join-Path $katalog $instalator.name

Krok "Pobieram $($instalator.name) ($([math]::Round($instalator.size / 1MB, 2)) MB)"
$postep = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'   # Pasek postepu potrafi spowolnic pobieranie kilkukrotnie.
try {
    Invoke-WebRequest -Uri $instalator.browser_download_url -OutFile $plik -UseBasicParsing
}
finally {
    $ProgressPreference = $postep
}

# ---- suma kontrolna --------------------------------------------------------

$sumaAsset = $wydanie.assets | Where-Object { $_.name -like '*.sha256' } | Select-Object -First 1

if ($sumaAsset) {
    Krok 'Sprawdzam sume kontrolna'

    # GitHub oddaje zalaczniki jako application/octet-stream, wiec w Windows PowerShell
    # .Content jest tablica bajtow, a nie tekstem - bez tej zamiany "suma" bylaby
    # kodem pierwszego znaku.
    $odpowiedz = Invoke-WebRequest -Uri $sumaAsset.browser_download_url -UseBasicParsing
    $tresc = if ($odpowiedz.Content -is [byte[]]) {
        [Text.Encoding]::ASCII.GetString($odpowiedz.Content)
    } else {
        [string]$odpowiedz.Content
    }

    $oczekiwana = ($tresc -split '\s+')[0].Trim().ToLower()
    $rzeczywista = (Get-FileHash $plik -Algorithm SHA256).Hash.ToLower()

    if ($oczekiwana -ne $rzeczywista) {
        Remove-Item $plik -Force -ErrorAction SilentlyContinue
        throw "Suma kontrolna sie nie zgadza.`n  oczekiwano: $oczekiwana`n  otrzymano:  $rzeczywista"
    }

    Dobrze "SHA-256 zgodna: $rzeczywista"
}
else {
    Uwaga 'Wydanie nie ma pliku .sha256 - pomijam weryfikacje.'
}

if ($DownloadOnly) {
    Krok 'Gotowe (tylko pobranie)'
    return $plik
}

# ---- instalacja ------------------------------------------------------------

Krok 'Uruchamiam instalator'
$argumenty = if ($Silent) { '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' } else { @() }
$proces = Start-Process -FilePath $plik -ArgumentList $argumenty -PassThru -Wait

if ($proces.ExitCode -ne 0) {
    throw "Instalator zakonczyl sie kodem $($proces.ExitCode)."
}

Remove-Item $katalog -Recurse -Force -ErrorAction SilentlyContinue

$zainstalowany = Join-Path $env:LOCALAPPDATA 'Programs\FencesVE\FencesVE.exe'
if (Test-Path $zainstalowany) {
    Dobrze "Zainstalowano: $zainstalowany"
    Dobrze "Wersja: $((Get-Item $zainstalowany).VersionInfo.FileVersion)"
}
else {
    Uwaga 'Instalator zakonczyl sie bez bledu, ale nie widze pliku w domyslnym miejscu.'
}

Krok 'Gotowe'
