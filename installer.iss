; PatchDiff installer — Inno Setup 6
; Built by the release workflow: iscc installer.iss /DAppVersion=x.y.z
;
; Installs to Program Files by default and lets the user pick any folder on the
; directory page. The app's own data (snapshots, revisions, reports, settings)
; lives in %LOCALAPPDATA%\PatchDiff and is never touched by an install, upgrade,
; or uninstall.

#define AppName "PatchDiff"
#define AppExe "PatchDiff.exe"

[Setup]
AppId={{B4DE5F6B-0DAC-4B81-A4CA-6C6003D6932B}
AppVersion={#AppVersion}
AppName={#AppName}
AppPublisher=PatchDiff
; {autopf} resolves to "C:\Program Files" (or the x86 equivalent) on Windows.
DefaultDirName={autopf}\{#AppName}
; Always show the directory page so the install location can be changed.
DisableDirPage=no
; Reuse the folder chosen during a previous install when upgrading.
UsePreviousAppDir=yes
; Program Files needs elevation; the updater runs this installer, so updates
; elevate the same way instead of writing into a protected folder.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=PatchDiff-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
; Silent upgrades must not stop on a "close the running app?" prompt.
CloseApplicationsFilter=*.exe,*.dll

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional tasks:"

[Files]
Source: "publish\win-x64\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; No skipifsilent: the updater runs this installer silently and the app must come
; back up afterwards. --show forces the window even if the user normally starts in
; the tray, so a fresh install visibly opens the app.
Filename: "{app}\{#AppExe}"; Parameters: "--show"; Description: "Launch {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
; runtime-extracted files the app may have created next to the exe
Type: filesandordirs; Name: "{app}\runtimes"
