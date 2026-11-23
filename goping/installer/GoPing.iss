; ---------------------------------------------------------------------------
; GoPing Messenger — native Inno Setup installer
;
; Build with:  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
; Manual:      ISCC.exe /dAppVersion=1.0.2 GoPing.iss
; ---------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "1.0.2"
#endif

#define AppName "GoPing"
#define AppPublisher "GoPing"
#define AppExeName "GoPing.exe"
#define AppRepo "https://github.com/RealBibus/GoPing"

[Setup]
AppId={{A7F4D9E2-6C1B-4D8A-9F3E-2B7C5D1A8E40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppRepo}
AppSupportURL={#AppRepo}/issues
AppUpdatesURL={#AppRepo}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no

; Published app files are produced by build-installer.ps1.
OutputDir=output
OutputBaseFilename=GoPing-Setup-v{#AppVersion}-win-x64

; Branding
SetupIconFile=Resources\goping.ico
WizardImageFile=Resources\wizard-image.bmp
WizardSmallImageFile=Resources\wizard-small-image.bmp
WizardStyle=modern

; Per-user install, no administrator rights required.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

Compression=lzma2/max
SolidCompression=yes
UninstallDisplayName=GoPing Messenger
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
CloseApplicationsFilter=GoPing.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=GoPing Setup
SetupWindowTitle=GoPing Setup v{#AppVersion}
WelcomeLabel1=Welcome to the GoPing Setup Wizard
WelcomeLabel2=GoPing {#AppVersion} will be installed for your user account.%n%nYour chats and settings in the GoPing data folder are kept.%n%nIt is recommended to close all other applications before continuing.
FinishedHeadingLabel=GoPing is installed
FinishedLabel=GoPing {#AppVersion} is ready to use. Launch it from the Start menu or the desktop shortcut.
ClickFinish=Click Finish to exit GoPing Setup.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch GoPing"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Clean stale files from a previous install so upgraded builds stay consistent.
Type: filesandordirs; Name: "{app}\*"

[Code]
function InitializeSetup(): Boolean;
begin
  // GoPing uses WebView2 for calls and voice messages. Most Windows 10/11
  // systems already ship the runtime; keep the install simple.
  Result := True;
end;
