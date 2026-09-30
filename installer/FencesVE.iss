; Instalator FencesVE (Inno Setup 6)
; Buduj przez build-installer.ps1 - ten skrypt oczekuje, ze publish juz sie odbyl.

#define AppName      "FencesVE"
#define AppPublisher "FencesVE"
#define AppExeName   "FencesVE.exe"
#define AppUrl       "https://github.com/vyltrixecho/FencesVE"
; Poprzednia nazwa programu - tylko do sprzatania starej instalacji.
#define LegacyName   "OpenFences"

; Wersje podaje build-installer.ps1 (/DAppVersion=...), tu jest tylko awaryjna wartosc.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define PublishDir "..\src\FencesVE\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
; AppId zostaje po poprzedniej nazwie - dzieki temu instalator FencesVE
; aktualizuje stara instalacje zamiast stawiac druga obok.
AppId={{8F3A1C74-2D5B-4E96-9A1F-7C0B6E5D3A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; ---------------------------------------------------------------------------
; Instalacja dla biezacego uzytkownika, bez UAC.
; To nie jest wygoda, tylko wymog: wbudowany aktualizator podmienia wlasny .exe
; w miejscu. W katalogu Program Files wymagaloby to za kazdym razem uprawnien
; administratora, wiec auto-aktualizacja przestalaby dzialac.
; Przy PrivilegesRequired=lowest {autopf} wskazuje na {localappdata}\Programs.
; ---------------------------------------------------------------------------
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
; Stara instalacja siedziala w katalogu pod poprzednia nazwa - nowa idzie pod wlasna nazwe,
; a stary katalog sprzata sekcja [InstallDelete].
UsePreviousAppDir=no
UsePreviousGroup=no

OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\FencesVE\Assets\FencesVE.ico

; Aplikacja siedzi w zasobniku - bez tego podmiana pliku by sie nie udala.
AppMutex=FencesVE.SingleInstance,{#LegacyName}.SingleInstance
CloseApplications=yes

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Uruchamiaj FencesVE razem z Windows"

[Files]
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[InstallDelete]
; Pozostalosci po wersji sprzed zmiany nazwy. Dane uzytkownika z %APPDATA%
; przenosi sama aplikacja przy pierwszym starcie.
Type: files; Name: "{autopf}\{#LegacyName}\{#LegacyName}.exe"
Type: files; Name: "{autopf}\{#LegacyName}\README.md"
Type: files; Name: "{autopf}\{#LegacyName}\unins*.*"
Type: dirifempty; Name: "{autopf}\{#LegacyName}"
Type: filesandordirs; Name: "{autoprograms}\{#LegacyName}"
Type: files; Name: "{autodesktop}\{#LegacyName}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} - ustawienia"; Filename: "{app}\{#AppExeName}"; Parameters: "--settings"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Autostart - tylko gdy uzytkownik zaznaczyl zadanie.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FencesVE"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

; Klucze menu pulpitu wypelnia sama aplikacja przy starcie. Zakladamy je puste
; tylko po to, zeby deinstalator mial co posprzatac.
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\FencesVE.NewFence"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\FencesVE.Settings"; Flags: uninsdeletekey

; Wpisy wersji sprzed zmiany nazwy - wskazuja na plik, ktorego juz nie ma.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "{#LegacyName}"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\{#LegacyName}.NewFence"; ValueType: none; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\{#LegacyName}.Settings"; ValueType: none; Flags: deletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[Code]

// Restart Manager nie zawsze domknie aplikacje bez glownego okna,
// a zablokowany .exe wywrocilby instalacje w polowie.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Druga nazwa to wersja sprzed zmiany nazwy, jej plik usuwa [InstallDelete].
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExeName} /IM {#LegacyName}.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ConfigDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExeName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(700);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Wpis autostartu mogla zalozyc sama aplikacja, a nie instalator.
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'FencesVE');

    ConfigDir := ExpandConstant('{userappdata}\FencesVE');
    if DirExists(ConfigDir) then
    begin
      if SuppressibleMsgBox(
           'Usunac takze ustawienia i uklad fence''ow?' + #13#10 + #13#10 + ConfigDir,
           mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(ConfigDir, True, True, True);
    end;
  end;
end;
