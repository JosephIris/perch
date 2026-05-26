; Inno Setup script for cmux-win
; Compile from the repo root with:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\cmux.iss /DAppVersion=0.1.0
; The CI workflow passes AppVersion based on the build run.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6F2A9D54-3CE0-4C2B-9B7D-CMUX-WIN-APP}}
AppName=cmux
AppVersion={#AppVersion}
AppVerName=cmux {#AppVersion}
AppPublisher=JosephIris
AppPublisherURL=https://github.com/JosephIris/cmux-win
AppSupportURL=https://github.com/JosephIris/cmux-win/issues
DefaultDirName={userappdata}\cmux
DefaultGroupName=cmux
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\publish\installer
OutputBaseFilename=cmux-Setup-{#AppVersion}
SetupIconFile=..\src\CmuxWin\Assets\cmux.ico
UninstallDisplayIcon={app}\CmuxWin.exe
UninstallDisplayName=cmux
Compression=lzma2/ultra
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=force

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\cmux-win\CmuxWin.exe"; DestDir: "{app}"; Flags: ignoreversion
; cmux CLI + claude.cmd hook wrapper. App.xaml.cs prepends {app}\tools to
; PATH at startup so panes can run `cmux ...` and so claude.cmd intercepts
; `claude` to inject Claude Code's --settings hooks JSON. Without these
; two files the agent IPC layer is dead code (hooks never fire).
Source: "..\publish\cmux-win\tools\cmux.exe";   DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\publish\cmux-win\tools\claude.cmd"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{group}\cmux";              Filename: "{app}\CmuxWin.exe"
Name: "{group}\Uninstall cmux";    Filename: "{uninstallexe}"
Name: "{userdesktop}\cmux";        Filename: "{app}\CmuxWin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CmuxWin.exe"; Description: "Launch cmux"; Flags: nowait postinstall skipifsilent
