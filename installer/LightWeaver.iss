; LightWeaver installer — per-user (no UAC), upgrade-in-place.
; Build via installer.ps1 (which publishes first and passes /DMyAppVersion).
; App data (%LOCALAPPDATA%\LightWeaver: credentials, settings, window state, image
; cache) is never touched by install, update, or uninstall — updates keep the user
; logged in with all preferences. The app itself migrates the pre-rename
; %LOCALAPPDATA%\LightWeever folder on first start.

#define MyAppName "LightWeaver"
#define MyAppExeName "LightWeaver.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
; Stable AppId: every future version installs over the same entry (true update).
AppId={{7A0E693C-50D5-45A6-9151-C67A6A590B2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher=Oss Alali
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Per-user: installs to %LOCALAPPDATA%\Programs\LightWeaver, no admin prompt.
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=LightWeaver-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 (build 19041), the app's TargetPlatformMinVersion.
MinVersion=10.0.19041
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\LightWeaver.WinUI\Assets\LightWeaver.ico
; Update flow: prompt Windows Restart Manager to close a running LightWeaver.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Update flow: the app passes /RELAUNCH when the user chose Install, so a silent update ends with
; the new version running. A silent install on a normal exit omits it and leaves the app closed.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: RelaunchRequested

[InstallDelete]
; Old-version binaries whose files may have been renamed/removed between releases —
; the publish output is self-contained, so a clean {app} avoids stale assemblies.
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
; Written by [Code] after the files are in, so the uninstaller's file list does not know it.
Type: files; Name: "{app}\previous-version.txt"

[Code]
// Revert support (v2 and later): the version this setup replaces goes to {app}\previous-version.txt,
// so Settings > Updates can offer to go back to it. Reinstalling the same version keeps the record.
// Read at ssInstall, before [InstallDelete] clears {app}; written back after the files are in.
var
  PreviousVersion: String;

function InitializeSetup(): Boolean;
begin
  PreviousVersion := '';
  RegQueryStringValue(HKA, ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1'),
    'DisplayVersion', PreviousVersion);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Kept: AnsiString;
begin
  if (CurStep = ssInstall) and (PreviousVersion = '{#MyAppVersion}') then
  begin
    if LoadStringFromFile(ExpandConstant('{app}\previous-version.txt'), Kept) then
      PreviousVersion := Trim(String(Kept))
    else
      PreviousVersion := '';
  end;
  if (CurStep = ssPostInstall) and (PreviousVersion <> '') then
    SaveStringToFile(ExpandConstant('{app}\previous-version.txt'), PreviousVersion, False);
end;

// Silent only: an interactive install already offers the launch checkbox on its finish page.
function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  if not WizardSilent then Exit;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;
