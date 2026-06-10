; Inno Setup Script for DeskBox

#define MyAppName "DeskBox"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Wingezi"
#define MyAppExeName "DeskBox.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application.
AppId={{5E052824-3456-427E-9759-3BCAE078A1D3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; To auto-start the app on Windows boot
PrivilegesRequired=lowest
OutputDir=..\Output
OutputBaseFilename=DeskBox_Installer
SetupIconFile=..\src\DeskBox\Assets\deskbox.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start DeskBox automatically when Windows starts"; GroupDescription: "System Integration"

[Files]
Source: "..\src\DeskBox\bin\Release\net8.0-windows10.0.22621.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Auto-start registry key (HKCU)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
