; ORBIT installer — Inno Setup script.
;
; Per-user install (no admin/UAC prompt) to keep beta-tester friction low. Safe because ORBIT
; itself never writes inside its own install folder at runtime: the SQLite library DB lives at
; %APPDATA%\ORBIT\library.db (Data/AppDbContext.cs) and config.ini falls back to the same
; %APPDATA%\ORBIT folder whenever no config.ini is already sitting next to the exe (which a
; fresh publish output never ships) — see Configuration/ConfigManager.cs's GetDefaultConfigPath.
;
; Version is passed in from Tools/release-alpha.ps1 via /DMyAppVersion=x.y.z-tag so this file
; never needs manual edits per release. Falls back to a placeholder if compiled standalone.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#define MyAppName "ORBIT"
#define MyAppPublisher "MeshDigital"
#define MyAppURL "https://github.com/MeshDigital/Orbit-pure"
#define MyAppExeName "ORBIT.exe"

; Fixed AppId so repeat installs upgrade in place instead of side-by-side. Generated once for
; this project — do not regenerate, or every future installer will look like a different app.
#define MyAppId "{{A6C9F3E2-8B1D-4E7A-9F5C-2D6B1A9E4C77}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Per-user install — no UAC prompt, matches this app's own data-location conventions.
DefaultDirName={localappdata}\Programs\{#MyAppName}
PrivilegesRequired=lowest
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=ORBIT-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Everything dotnet publish produced, recursively — ORBIT.exe, appsettings.json, Data/, Tools/,
; LatoFont/, onnx runtime natives, icons — all of it, preserving folder structure exactly.
Source: "..\artifacts\alpha\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Uninstall removes only the installed program files — deliberately leaves %APPDATA%\ORBIT
; (library DB, config, downloaded playlists metadata) untouched so a reinstall doesn't lose data.
Type: filesandordirs; Name: "{app}"
