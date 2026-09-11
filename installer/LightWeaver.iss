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
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
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

[InstallDelete]
; Old-version binaries whose files may have been renamed/removed between releases —
; the publish output is self-contained, so a clean {app} avoids stale assemblies.
Type: filesandordirs; Name: "{app}\*"
