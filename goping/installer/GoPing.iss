; GoPing Messenger installer
; Build with Inno Setup 6: ISCC.exe GoPing.iss
; Recommended: run build-installer.ps1, which publishes the app and compiles this script.

#define AppName "GoPing"
#define AppVersion "1.0.0"
#define AppPublisher "GoPing"
#define AppExeName "GoPing.exe"
#define AppDescription "GoPing - a local messenger for you and your friends, with login via GitHub or a server token!"

[Setup]
AppId={{A7F4D9E2-6C1B-4D8A-9F3E-2B7C5D1A8E40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
AppUpdatesURL=https://github.com/
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
; Publish output produced by build-installer.ps1
SourceDir=publish\win-x64
OutputDir=..\output
OutputBaseFilename=GoPing-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Clean old files from previous installs to avoid stale assemblies/assets.
Type: filesandordirs; Name: "{app}\*"

[Code]
function InitializeSetup(): Boolean;
begin
  // WebView2 Runtime is required by GoPing (used for calls/voice recorder).
  // Most Windows 10/11 systems already have it installed.
  Result := True;
end;
