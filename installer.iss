; WKI Clipper — Inno Setup installer script
; Build with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
; Prerequisite: run dotnet publish first (see build.ps1)

#define AppName      "WKI Clipper"
#define AppVersion   "0.15.4"
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
VersionInfoDescription=Replay clipper + screen recorder for Windows 11 (AMD/NVIDIA/Intel)
CloseApplications=yes
RestartApplications=no

[Languages]
; Bilingual installer — auto-selects from the Windows UI language.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.AutostartTask=Start with Windows
german.AutostartTask=Mit Windows starten
english.OptionsGroup=Options:
german.OptionsGroup=Optionen:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
; Offered on the first install only: afterwards the app owns this choice (Settings → About),
; and an update must not switch autostart back on that the user turned off in the app.
Name: "autostart";   Description: "{cm:AutostartTask}";     GroupDescription: "{cm:OptionsGroup}";   Flags: unchecked; Check: not IsUpdate

[Files]
; Main program (self-contained single-file .NET 8 publish output)
Source: "publish\WKI_Clipper.exe"; DestDir: "{app}"; Flags: ignoreversion
; WPF native DLLs — only present when publishing WITHOUT
; IncludeNativeLibrariesForSelfExtract (otherwise they live inside the exe).
Source: "publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Bundled FFmpeg (Gyan full build, ~214 MB, has AMF/NVENC/QSV)
Source: "publish\Assets\ffmpeg\ffmpeg.exe"; DestDir: "{app}\Assets\ffmpeg"; Flags: ignoreversion
; Starter crosshairs seeded into the library on first run
Source: "publish\Assets\DefaultCrosshairs\*.png"; DestDir: "{app}\Assets\DefaultCrosshairs"; Flags: ignoreversion skipifsourcedoesntexist
; App icon (tray fallback and shortcuts)
Source: "Resources\Icons\app_icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                          Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app_icon.ico"
Name: "{group}\{cm:UninstallProgram,{#AppName}}";    Filename: "{uninstallexe}";       IconFilename: "{app}\app_icon.ico"
Name: "{userdesktop}\{#AppName}";                    Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app_icon.ico"; Tasks: desktopicon

[Registry]
; "Start with Windows" picked: the Run entry, plus any earlier "off" from Windows' own startup
; switch (Settings → Apps → Autostart) removed — with that switch off, Windows skips the entry.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "WKI_Clipper"; ValueData: """{app}\{#AppExeName}"""; Tasks: autostart
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; \
  ValueName: "WKI_Clipper"; Flags: deletevalue; Tasks: autostart
; Autostart can also be switched on in the app. Installs and updates leave that choice alone
; (an unconditional delete here wiped it on every update); only the uninstaller removes both.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueName: "WKI_Clipper"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; \
  ValueName: "WKI_Clipper"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data is KEPT on uninstall (clips, settings) — by design.
; Only remove the buffer cache and generated tray icon.
Type: filesandordirs; Name: "{localappdata}\WKI_Clipper\buffer"
Type: files;          Name: "{localappdata}\WKI_Clipper\tray.ico"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

// Already installed (per-user install: the uninstall entry lives in HKCU).
function IsUpdate(): Boolean;
begin
  Result := RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1');
end;

