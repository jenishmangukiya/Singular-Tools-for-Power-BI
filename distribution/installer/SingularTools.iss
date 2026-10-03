; Singular Tools installer (Inno Setup 6).
;
; One script builds all three architecture installers; the build script
; (build-installers.ps1) passes the per-arch values with /D defines:
;   ArchName             x64 | x86 | arm64   (output name + display)
;   ArchitecturesAllowed allowed arch list   (x64compatible / x86compatible / arm64)
;   AppSourceDir         the published app folder to package
;   OutputDir            where the Setup .exe is written
;   AppVersion           version shown by the wizard
;   IconData             base64 PNG data URI for the Power BI ribbon icon
;
; The app installs per-user (no admin) to %LOCALAPPDATA%\SingularTools, matching
; distribution\register-external-tool.ps1. Registration with Power BI Desktop's
; External Tools tab is attempted during install; that folder is under Program
; Files (x86)\Common Files and needs admin, so when it is not writable the
; installed register-pbi-tool.cmd elevates on demand.

#ifndef ArchName
  #define ArchName "x64"
#endif
#ifndef ArchitecturesAllowed
  #define ArchitecturesAllowed "x64compatible"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef IconData
  #define IconData ""
#endif

[Setup]
AppId={{8F2C1E64-6B1A-4B7E-9E1C-5C9D3A2E7B41}
AppName=Singular Tools
AppVersion={#AppVersion}
AppPublisher=Singular Tools
DefaultDirName={localappdata}\SingularTools
DefaultGroupName=Singular Tools
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#ArchitecturesAllowed}
OutputDir={#OutputDir}
OutputBaseFilename=SingularTools-{#AppVersion}-{#ArchName}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#AppSourceDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SingularTools.App.exe
UninstallDisplayName=Singular Tools
; Unsigned for now; SmartScreen will warn until a code-signing cert is added.
SignedUninstaller=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "registerpbi"; Description: "Register with &Power BI Desktop (External Tools ribbon)"; GroupDescription: "Power BI integration:"; Flags: checkedonce

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "register-pbi-tool.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SingularTools.pbitool.json"; DestDir: "{app}"; DestName: "SingularTools.pbitool.template.json"; Flags: ignoreversion

[Icons]
Name: "{group}\Singular Tools"; Filename: "{app}\SingularTools.App.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Singular Tools"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Singular Tools"; Filename: "{app}\SingularTools.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Register during install only when the user kept the task. The helper writes the
; JSON to the per-user app folder and, if Power BI's folder is writable, there too;
; a failure is non-fatal and can be retried by running the command manually.
Filename: "{app}\register-pbi-tool.cmd"; Parameters: "-Quiet"; StatusMsg: "Registering with Power BI Desktop..."; Tasks: registerpbi; Flags: runhidden
Filename: "{app}\SingularTools.App.exe"; Description: "Launch Singular Tools"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\register-pbi-tool.cmd"; Parameters: "-Unregister -Quiet"; Flags: runhidden; RunOnceId: "UnregisterPbiTool"

[Code]
{ ---------------------------------------------------------------------------
  Registration writes two files: SingularTools.pbitool.json in the app folder
  (always writable) and, when permission allows, a copy in Power BI's External
  Tools folder. The JSON path must point at the installed exe, so it is written
  here rather than shipped verbatim.
  --------------------------------------------------------------------------- }

const
  PbiExternalToolsSubDir = 'Common Files\Microsoft Shared\Power BI Desktop\External Tools';

function PbiExternalToolsDir(): string;
begin
  Result := AddBackslash(ExpandConstant('{commonpf32}')) + PbiExternalToolsSubDir;
end;

function DirectoryExistsWritable(const Dir: string): Boolean;
begin
  { A cheap writability probe: try to create and delete a temp file. }
  Result := DirExists(Dir) and SaveStringToFile(Dir + '\__wtest.tmp', '', False);
  if Result then
    DeleteFile(Dir + '\__wtest.tmp');
end;

procedure WriteRegistrationFile();
var
  ExePath: string;
  Json: string;
begin
  ExePath := ExpandConstant('{app}\SingularTools.App.exe');
  StringChangeEx(ExePath, '\', '\\', True);
  Json :=
    '{' + #13#10 +
    '  "version": "1.0",' + #13#10 +
    '  "name": "Singular Tools",' + #13#10 +
    '  "description": "Utilities for Power BI Desktop authors",' + #13#10 +
    '  "path": "' + ExePath + '",' + #13#10 +
    '  "arguments": "\"%server%\" \"%database%\"",' + #13#10 +
    '  "iconData": "{#IconData}"' + #13#10 +
    '}';

  SaveStringToFile(ExpandConstant('{app}\SingularTools.pbitool.json'), Json, False);

  if DirectoryExistsWritable(PbiExternalToolsDir()) then
    SaveStringToFile(PbiExternalToolsDir() + '\SingularTools.pbitool.json', Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteRegistrationFile();
end;
