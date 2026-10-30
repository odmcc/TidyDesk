; Builds TidyDesk-Setup.exe (optional installer). The portable TidyDesk.exe stays the main download.
; Usage: ISCC.exe /DAppVersion=3.0.0 installer\TidyDesk.iss   (run scripts\publish.ps1 first)
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{A24AD7C8-FB3D-4BF1-B2B4-5F9582BCD775}
AppName=TidyDesk
AppVersion={#AppVersion}
AppPublisher=odmcc
AppPublisherURL=https://github.com/odmcc/TidyDesk
DefaultDirName={autopf}\TidyDesk
DefaultGroupName=TidyDesk
OutputDir=..\dist
OutputBaseFilename=TidyDesk-Setup
SetupIconFile=..\src\TidyDesk\Assets\app.ico
UninstallDisplayIcon={app}\TidyDesk.exe
; Per-user install by default (no admin prompt); the wizard lets the user pick all users instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\dist\TidyDesk.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\TidyDesk"; Filename: "{app}\TidyDesk.exe"
Name: "{autodesktop}\TidyDesk"; Filename: "{app}\TidyDesk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TidyDesk.exe"; Description: "{cm:LaunchProgram,TidyDesk}"; Flags: nowait postinstall skipifsilent
