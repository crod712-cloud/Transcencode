#define MyAppName "Transcencode"
#define MyAppVersion "0.2.9"
#define MyAppPublisher "Transcencode"
#define MyAppExeName "Transcencode.exe"

[Setup]
AppId={{D32108EA-8553-4EB1-8D5B-CC6A876FA929}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/crod712-cloud/Transcencode
DefaultDirName={localappdata}\Programs\Transcencode
DefaultGroupName=Transcencode
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Transcencode-Setup-x64-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallDisplayName=Transcencode
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion=0.2.9.0
VersionInfoCompany=Transcencode
VersionInfoDescription=Transcencode — Analyze. Encode. Verify.
VersionInfoProductName=Transcencode
VersionInfoProductVersion={#MyAppVersion}

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Transcencode"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Transcencode"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Transcencode"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
