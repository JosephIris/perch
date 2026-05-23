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

[Icons]
Name: "{group}\cmux";              Filename: "{app}\CmuxWin.exe"
Name: "{group}\Uninstall cmux";    Filename: "{uninstallexe}"
Name: "{userdesktop}\cmux";        Filename: "{app}\CmuxWin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CmuxWin.exe"; Description: "Launch cmux"; Flags: nowait postinstall skipifsilent
