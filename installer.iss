; PatchDiff installer — Inno Setup 6
; Built by the release workflow: iscc installer.iss /DAppVersion=x.y.z
; Per-user install (no admin): the folder must stay user-writable because the
; app's self-updater swaps files in the install folder on update.

#define AppName "PatchDiff"
#define AppExe "PatchDiff.exe"

[Setup]
AppId={{B4DE5F6B-0DAC-4B81-A4CA-6C6003D6932B}
AppVersion={#AppVersion}
AppName={#AppName}
AppPublisher=PatchDiff
DefaultDirName={localappdata}\Programs\{#AppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=PatchDiff-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional tasks:"

[Files]
Source: "dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; runtime-extracted files the app may have created next to the exe
Type: filesandordirs; Name: "{app}\runtimes"
