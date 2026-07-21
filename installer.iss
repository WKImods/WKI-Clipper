; WKI Clipper — Inno Setup installer script
; Build with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
; Prerequisite: run dotnet publish first (see build.ps1)

#define AppName      "WKI Clipper"
#define AppVersion   "0.1.2"
#define AppPublisher "WKI"
#define AppExeName   "WKI_Clipper.exe"
#define AppId        "{B5F3D2A1-8C4E-4F9B-A2D6-1E5C8A3F7D2C}"

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}
; Per-user install — no admin needed.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=no
OutputDir=installer_output
OutputBaseFilename=WKI_Clipper_Setup_{#AppVersion}
SetupIconFile=Resources\Icons\app_icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Replay-Clipper + Screen-Recorder fuer Windows 11 (AMD/NVIDIA/Intel)
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknuepfung erstellen"; GroupDescription: "Verknuepfungen:"; Flags: checkedonce
Name: "autostart"; Description: "Mit Windows starten"; GroupDescription: "Optionen:"; Flags: unchecked

[Files]
; Hauptprogramm (self-contained single-file .NET 8 publish output)
Source: "publish\WKI_Clipper.exe"; DestDir: "{app}"; Flags: ignoreversion
; WPF native DLLs (required beside single-file exe)
Source: "publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion
; Gebundeltes FFmpeg (Gyan full build, ~214 MB, hat AMF/NVENC/QSV)
Source: "publish\Assets\ffmpeg\ffmpeg.exe"; DestDir: "{app}\Assets\ffmpeg"; Flags: ignoreversion
; App-Icon (fuer Tray-Fallback und Shortcuts)
Source: "Resources\Icons\app_icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                Filename: "{app}\{#AppExeName}";  IconFilename: "{app}\app_icon.ico"
Name: "{group}\{#AppName} deinstallieren"; Filename: "{uninstallexe}";        IconFilename: "{app}\app_icon.ico"
Name: "{userdesktop}\{#AppName}";          Filename: "{app}\{#AppExeName}";  IconFilename: "{app}\app_icon.ico"; Tasks: desktopicon

[Registry]
; Autostart-Eintrag wenn Task gewaehlt — Deinstaller raeumt ihn weg
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "WKI_Clipper"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart
; Falls Autostart NICHT gewaehlt: trotzdem alten Eintrag entfernen (sauberer Re-Install)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueName: "WKI_Clipper"; Flags: deletevalue uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{#AppName} jetzt starten"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User-Daten BLEIBEN beim Uninstall (Clips, Settings) — bewusst.
; Nur Tray-Cache und log entfernen wenn nichts mehr da ist.
Type: filesandordirs; Name: "{localappdata}\WKI_Clipper\buffer"
Type: files;          Name: "{localappdata}\WKI_Clipper\tray.ico"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
