; Inno Setup script for perch
; Compile from the repo root with:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\perch.iss /DAppVersion=0.1.0
; The CI workflow passes AppVersion based on the build run.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6F2A9D54-3CE0-4C2B-9B7D-PERCH-WIN-APP}}
AppName=Perch
AppVersion={#AppVersion}
AppVerName=Perch {#AppVersion}
AppPublisher=JosephIris
AppPublisherURL=https://github.com/JosephIris/perch
AppSupportURL=https://github.com/JosephIris/perch/issues
DefaultDirName={userappdata}\perch
DefaultGroupName=Perch
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\publish\installer
OutputBaseFilename=Perch-Setup-{#AppVersion}
SetupIconFile=..\src\Perch\Assets\perch.ico
UninstallDisplayIcon={app}\Perch.exe
UninstallDisplayName=Perch
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
Source: "..\publish\perch\Perch.exe"; DestDir: "{app}"; Flags: ignoreversion
; Web bundle (xterm.js + chrome). MainWindow.xaml.cs maps {app}\wwwroot
; to https://perch.local/ via SetVirtualHostNameToFolderMapping; without
; these files WebView2 shows the "Web bundle not found" fallback page.
Source: "..\publish\perch\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs createallsubdirs
; perch CLI + claude.cmd hook wrapper. App.xaml.cs prepends {app}\tools to
; PATH at startup so panes can run `perch ...` and so claude.cmd intercepts
; `claude` to inject Claude Code's --settings hooks JSON. Without these
; two files the agent IPC layer is dead code (hooks never fire).
Source: "..\publish\perch\tools\perch.exe";   DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\publish\perch\tools\claude.cmd"; DestDir: "{app}\tools"; Flags: ignoreversion
; Microsoft Edge WebView2 Runtime evergreen bootstrapper (~150KB). Downloaded
; by CI into publish/. Runs only when the runtime isn't already present
; (see NeedsWebView2 in [Code]). Without this, perch launches into a blank
; page on machines without WebView2 — Win11 ships it; older Win10 may not.
Source: "..\publish\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{group}\Perch";              Filename: "{app}\Perch.exe"
Name: "{group}\Uninstall Perch";    Filename: "{uninstallexe}"
Name: "{userdesktop}\Perch";        Filename: "{app}\Perch.exe"; Tasks: desktopicon

[Run]
; Install WebView2 Runtime first if missing — the app can't run without it.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; Flags: waituntilterminated; Check: NeedsWebView2; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."
Filename: "{app}\Perch.exe"; Description: "Launch Perch"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsWebView2: Boolean;
var Pv: string;
begin
  // WebView2 Runtime registers itself under EdgeUpdate\Clients. Check both
  // 64-bit + 32-bit views since the registrar varies by OS bitness.
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) then
    Result := (Pv = '') or (Pv = '0.0.0.0')
  else if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) then
    Result := (Pv = '') or (Pv = '0.0.0.0')
  else if RegQueryStringValue(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) then
    Result := (Pv = '') or (Pv = '0.0.0.0')
  else
    Result := True;
end;
