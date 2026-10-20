; WKI Clipper — Inno Setup installer script
; Build with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
; Prerequisite: run dotnet publish first (see build.ps1)

#define AppName      "WKI Clipper"
#define AppVersion   "0.16.0"
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
; No Start-menu folder page: its Browse dialog only lists Start-menu folders and was mistaken for the
; install-location picker. The Start-menu group is still created, named by DefaultGroupName.
DisableProgramGroupPage=yes
; The destination page is always shown (Inno's default "auto" hides it on updates), pre-filled with the
; current location (UsePreviousAppDir), so the install can be moved to any drive by running Setup again.
; Inno's Browse button opens the classic Browse-For-Folder tree (no setting switches it to the modern
; picker). It is the shell folder tree, where drives can be picked: the Inno docs' own AppendDefaultDirName
; example is "Z:\" becoming "Z:\{#AppName}". Typing a path into the box works for any drive too. The
; Start-menu page's Browse only offers Start-menu folders - which is why that page is switched off above.
DisableDirPage=no
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
; Hint on the destination page (placed by InitializeWizard).
english.DirHint=You can install to any drive: type the full path (for example D:\Programs\WKI Clipper) or click "Browse". Your clips and settings are stored separately and are kept.
german.DirHint=Du kannst jedes Laufwerk wählen: Pfad eintippen (z. B. D:\Programme\WKI Clipper) oder „Durchsuchen“ klicken. Clips und Einstellungen liegen getrennt und bleiben erhalten.
english.DirWarning=Do not choose a folder that already contains other files (such as your clips) - pick a new or empty folder.
german.DirWarning=Wähle keinen Ordner, in dem schon andere Dateien (z. B. deine Clips) liegen – am besten einen neuen oder leeren Ordner.
; Moving an existing installation by choosing a different folder.
english.DirNested=The new folder and the current installation folder must not be inside one another.%n%nCurrent: %1%nNew: %2%n%nPlease choose a different folder.
german.DirNested=Der neue Ordner darf nicht innerhalb des bisherigen Installationsordners liegen – und umgekehrt.%n%nBisher: %1%nNeu: %2%n%nBitte wähle einen anderen Ordner.
english.DirIsDriveRoot=Please choose a folder, not the drive itself (for example D:\Programs\WKI Clipper).
german.DirIsDriveRoot=Bitte wähle einen Ordner und nicht das Laufwerk selbst (z. B. D:\Programme\WKI Clipper).
english.MoveNote=Move: the existing installation in %1 is removed once the new one is installed. Your clips and settings are kept.
german.MoveNote=Verschieben: Die bestehende Installation in %1 wird entfernt, sobald die neue installiert ist. Clips und Einstellungen bleiben erhalten.
english.OldCopyLeft=The previous installation in %1 could not be removed completely. You can delete that folder yourself; your clips and settings are untouched.
german.OldCopyLeft=Die bisherige Installation in %1 konnte nicht vollständig entfernt werden. Du kannst den Ordner selbst löschen; Clips und Einstellungen sind unberührt.

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
const
  // Per-user install: the uninstall entry lives in HKCU.
  UninstKey      = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  RunKey         = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';
  AutostartName  = 'WKI_Clipper';
  AppExe         = '{#AppExeName}';

var
  WasInstalled: Boolean;       // an uninstall entry exists
  PrevDir: String;             // folder of that installation, no trailing backslash ('' on a first install)
  MovingInstall: Boolean;      // set at the start of the install: the new folder differs from PrevDir

function InitializeSetup(): Boolean;
var
  S: String;
begin
  Result := True;
  WasInstalled := RegKeyExists(HKCU, UninstKey);
  PrevDir := '';
  if WasInstalled then
  begin
    S := '';
    if not RegQueryStringValue(HKCU, UninstKey, 'InstallLocation', S) then S := '';
    if Trim(S) = '' then
      if not RegQueryStringValue(HKCU, UninstKey, 'Inno Setup: App Path', S) then S := '';
    S := Trim(S);
    if S <> '' then PrevDir := RemoveBackslashUnlessRoot(S);
  end;
end;

// Already installed. Evaluated once at startup: the install itself writes the uninstall entry, which must not turn a
// first install into an update halfway through (the autostart task is checked against this).
function IsUpdate(): Boolean;
begin
  Result := WasInstalled;
end;

function SamePath(const A, B: String): Boolean;
begin
  Result := CompareText(AddBackslash(A), AddBackslash(B)) = 0;
end;

// True when Inner is Outer or lies below it.
function PathInside(const Inner, Outer: String): Boolean;
var
  O: String;
begin
  O := AddBackslash(Outer);
  Result := CompareText(Copy(AddBackslash(Inner), 1, Length(O)), O) = 0;
end;

// '' = the folder is fine, otherwise the message to show. Nested old/new folders would make the move
// delete files that belong to the new location (or leave the old tree inside it), so they are refused.
function CheckTargetDir(const Dir: String): String;
var
  N: String;
begin
  Result := '';
  if Trim(Dir) = '' then exit;
  N := RemoveBackslashUnlessRoot(ExpandFileName(Dir));
  if Length(N) <= 3 then
    Result := CustomMessage('DirIsDriveRoot')
  else if (PrevDir <> '') and not SamePath(N, PrevDir) and (PathInside(N, PrevDir) or PathInside(PrevDir, N)) then
    Result := FmtMessage(CustomMessage('DirNested'), [PrevDir, N]);
end;

function IsMove(): Boolean;
begin
  Result := (PrevDir <> '') and not SamePath(PrevDir, ExpandConstant('{app}'));
end;

// The Restart Manager check also has to see the app running from the OLD folder: its exe cannot be deleted
// while it runs, and a move does not touch that exe through [Files].
procedure RegisterExtraCloseApplicationsResources();
begin
  if PrevDir <> '' then
    RegisterExtraCloseApplicationsResource(False, AddBackslash(PrevDir) + AppExe);
end;

procedure InitializeWizard();
var
  Hint: TNewStaticText;
begin
  // Destination page: say that every drive is allowed. Placed below the path box and its Browse button.
  Hint := TNewStaticText.Create(WizardForm);
  Hint.Parent := WizardForm.SelectDirPage;
  Hint.AutoSize := False;
  Hint.WordWrap := True;
  Hint.Left := WizardForm.SelectDirBrowseLabel.Left;
  Hint.Width := WizardForm.SelectDirBrowseLabel.Width;
  Hint.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(16);
  Hint.Height := WizardForm.DiskSpaceLabel.Top - Hint.Top - ScaleY(6);
  Hint.Caption := CustomMessage('DirHint') + #13#10#13#10 + CustomMessage('DirWarning');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Err: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Err := CheckTargetDir(WizardDirValue);
    if Err <> '' then
    begin
      Log('Destination refused: ' + WizardDirValue);
      SuppressibleMsgBox(Err, mbError, MB_OK, IDOK);
      Result := False;   // on a silent install this ends Setup
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckTargetDir(ExpandConstant('{app}'));   // same check once more, for /DIR= and edits made after the page
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if IsMove() then
    Result := Result + NewLine + NewLine + FmtMessage(CustomMessage('MoveNote'), [PrevDir]);
  if MemoGroupInfo <> '' then Result := Result + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

// Moving is transactional: the new copy is installed FIRST and the old one is only taken away at ssPostInstall,
// once everything has worked. A cancel or a failure before that leaves the old installation untouched (Setup's own
// rollback undoes just what the new one wrote), with its shortcuts, uninstall entry and autostart still in place.
//
// The old uninstaller is deliberately NOT run. It deletes by name what the new installation shares with it: the
// uninstall entry ({AppId}_is1 - Setup has just rewritten it to point at the new folder), the Start-menu shortcuts
// and the autostart values. So the old program files are deleted directly - only the ones Setup itself put there.
// Whatever else sits in that folder stays, which is why there is no DelTree: RemoveDir only removes empty folders.

// The autostart entry follows the exe, but only our own: a value the user pointed somewhere else stays as it is.
// Windows' own on/off switch (StartupApproved) was never touched, so it keeps its state.
procedure RepointAutostart();
var
  Data: String;
begin
  if RegQueryStringValue(HKCU, RunKey, AutostartName, Data) and
     (CompareText(RemoveQuotes(Trim(Data)), AddBackslash(PrevDir) + AppExe) = 0) then
  begin
    Data := '"' + AddBackslash(ExpandConstant('{app}')) + AppExe + '"';
    RegWriteStringValue(HKCU, RunKey, AutostartName, Data);
    Log('Move: autostart entry now points at ' + Data);
  end;
end;

// The old desktop shortcut keeps its name. With the task ticked [Icons] has just rewritten it; with the task off it
// would be left pointing at the exe that is about to go, so it is removed - but only if it points at that exe.
procedure RemoveStaleDesktopShortcut();
var
  Lnk, Target: String;
  Shell, Link: Variant;
begin
  if WizardIsTaskSelected('desktopicon') then exit;
  Lnk := ExpandConstant('{userdesktop}\{#AppName}.lnk');
  if not FileExists(Lnk) then exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Lnk);
    Target := Link.TargetPath;
  except
    Log('Move: could not read the desktop shortcut: ' + GetExceptionMessage);
    exit;
  end;
  if CompareText(Target, AddBackslash(PrevDir) + AppExe) = 0 then
  begin
    DeleteFile(Lnk);
    Log('Move: removed the desktop shortcut of the previous installation');
  end;
end;

// Deletes the files of one extension in Dir. The extension is compared exactly: a mask like '*.dll' also
// matches longer extensions through the old 8.3 names.
procedure DeleteOldFiles(const Dir, Mask, Ext: String);
var
  R: TFindRec;
begin
  if FindFirst(AddBackslash(Dir) + Mask, R) then
  try
    repeat
      if ((R.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and (CompareText(ExtractFileExt(R.Name), Ext) = 0) then
        if not DeleteFile(AddBackslash(Dir) + R.Name) then
          Log('Move: could not delete ' + AddBackslash(Dir) + R.Name);
    until not FindNext(R);
  finally
    FindClose(R);
  end;
end;

// The program files of the old installation: exactly what [Files] and the uninstaller put there.
procedure RemoveOldProgramFiles();
var
  D: String;
begin
  D := AddBackslash(PrevDir);
  Log('Move: removing the previous installation in ' + PrevDir);
  DeleteFile(D + AppExe);
  DeleteFile(D + 'app_icon.ico');
  DeleteFile(D + 'unins000.exe');
  DeleteFile(D + 'unins000.dat');
  DeleteFile(D + 'unins000.msg');
  DeleteOldFiles(D, '*.dll', '.dll');
  DeleteFile(D + 'Assets\ffmpeg\ffmpeg.exe');
  DeleteOldFiles(D + 'Assets\DefaultCrosshairs', '*.png', '.png');
  // Empty folders only (RemoveDir fails on anything with files in it): the user's own files keep their folders.
  RemoveDir(D + 'Assets\DefaultCrosshairs');
  RemoveDir(D + 'Assets\ffmpeg');
  RemoveDir(D + 'Assets');
  RemoveDir(PrevDir);

  if FileExists(D + AppExe) then
  begin
    Log('Previous install not removed completely: ' + PrevDir);
    SuppressibleMsgBox(FmtMessage(CustomMessage('OldCopyLeft'), [PrevDir]), mbInformation, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    MovingInstall := IsMove()
  else if (CurStep = ssPostInstall) and MovingInstall then
  begin
    // Setup shut the old app down (Restart Manager) before it copied anything, so its exe can go now.
    RepointAutostart();
    RemoveStaleDesktopShortcut();
    RemoveOldProgramFiles();
  end;
end;

// The app records the buffer folder it really used (HKCU\Software\WKI_Clipper\BufferFolder). A 30 minute
// replay buffer is gigabytes, and the folder may be anywhere the user put it - the uninstaller cleans up
// exactly the files the buffer wrote there. Never DelTree a user-chosen folder.
const
  BufferKey = 'Software\WKI_Clipper';

function IsOwnBufferFile(const Name: String): Boolean;
var
  L: String;
  I, Us: Integer;
begin
  L := Lowercase(Name);
  Result := (L = '_concat_list.txt') or (L = '_chapters.txt') or (L = '_gif_src.mp4') or (L = '_gif_list.txt');
  if Result then exit;
  // seg_<digits>_<digits>.mp4
  if (Length(L) < 11) or (Copy(L, 1, 4) <> 'seg_') or (Copy(L, Length(L) - 3, 4) <> '.mp4') then exit;
  L := Copy(L, 5, Length(L) - 8);
  Us := Pos('_', L);
  if (Us < 2) or (Us = Length(L)) then exit;
  for I := 1 to Length(L) do
    if (I <> Us) and ((L[I] < '0') or (L[I] > '9')) then exit;
  Result := True;
end;

procedure CleanBufferFolder(const Dir: String);
var
  R: TFindRec;
begin
  if (Length(Dir) <= 3) or not DirExists(Dir) then exit;   // never a drive root
  if FindFirst(AddBackslash(Dir) + '*', R) then
  try
    repeat
      if ((R.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and IsOwnBufferFile(R.Name) then
        DeleteFile(AddBackslash(Dir) + R.Name);
    until not FindNext(R);
  finally
    FindClose(R);
  end;
  RemoveDir(Dir);   // only succeeds when empty
end;

// Old buffer folders whose cleanup never finished (the app writes them to PendingCleanup, '|' between the entries -
// '|' cannot occur in a Windows path).
procedure CleanPendingFolders(const List: String);
var
  Rest: String;
  P: Integer;
begin
  Rest := List;
  while Length(Rest) > 0 do
  begin
    P := Pos('|', Rest);
    if P = 0 then
    begin
      CleanBufferFolder(Rest);
      Rest := '';
    end
    else
    begin
      if P > 1 then CleanBufferFolder(Copy(Rest, 1, P - 1));
      Delete(Rest, 1, P);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Pending: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, BufferKey, 'BufferFolder', Dir) then
      CleanBufferFolder(Dir);
    if RegQueryStringValue(HKCU, BufferKey, 'PendingCleanup', Pending) then
      CleanPendingFolders(Pending);
    RegDeleteKeyIncludingSubkeys(HKCU, BufferKey);
  end;
end;

