; ============================================================
; LongStitch installer script (Inno Setup, built by build.ps1 -Installer)
;
; Design notes (see docs/构建与发布.md for the Chinese write-up):
; - PrivilegesRequired=lowest: installs per-user, no admin prompt, no UAC.
; - DefaultDirName={localappdata}\Programs\LongStitch: the modern per-user
;   convention (same shape as VS Code). The dir page stays enabled, so the
;   user may pick any drive - data follows the install dir because the app
;   detects a "data" folder next to the exe.
; - The empty data folder is created by [Dirs]; all preferences, recent list
;   and logs land inside it.
; - Uninstaller deliberately does NOT delete {app}\data: it holds user data.
; - Keep this file ASCII-only (no Chinese literals): ISCC treats a BOM-less
;   file as ANSI. All user-visible Chinese text comes from the language file.
; ============================================================

#define MyAppName "LongStitch"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "liulifeili"

[Setup]
AppId={{9C6B4E12-3D47-4F5A-9B21-58A0C7D2E1F4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\LongStitch
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\out
OutputBaseFilename=LongStitch-installer-v{#MyAppVersion}
SetupIconFile=..\assets\Icon.ico
UninstallDisplayName={#MyAppName}
; Run the app maximized only if it does that by itself; start normally.
ChangesAssociations=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Dirs]
; The data folder is the on-disk switch the app looks for (DataRoot.cs):
; when it exists next to the exe, preferences / recent list / logs are
; stored inside it instead of %APPDATA% / %LOCALAPPDATA%.
Name: "{app}\data"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppName}.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
