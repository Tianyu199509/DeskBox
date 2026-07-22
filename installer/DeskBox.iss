; DeskBox 安装脚本
; 构建命令：
; dotnet publish ..\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o ..\artifacts\publish\DeskBox\x64 -v:minimal

#define MyAppName "DeskBox"
#define MyAppVersion "1.3.1"
#define MyAppVersionInfo "1.3.1.0"
#define MyAppPublisher "朱天雨"
#define MyAppExeName "DeskBox.exe"
#define MyAppOutputBaseName "DeskBox_Setup"
#ifndef MyAppReleaseDir
#define MyAppReleaseDir "..\\Output\\perf-final-v085"
#endif

[Setup]
; AppId 用于唯一标识同一个应用。
AppId={{5E052824-3456-427E-9759-3BCAE078A1D3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=安装包会按需检测并下载 .NET 10 Runtime 和 Windows App Runtime 2.2。
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\Assets\deskbox.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
UsePreviousAppDir=no
UsePreviousPrivileges=no
; DeskBox is a tray-first WinUI app with multiple top-level windows. Restart
; Manager cannot always close the whole process through a single window, so
; allow Setup to terminate DeskBox after the normal close attempt times out.
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
OutputDir=..\Output
OutputBaseFilename={#MyAppOutputBaseName}_{#MyAppVersion}_x64
SetupIconFile=..\src\DeskBox\Assets\deskbox.ico
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoProductVersion={#MyAppVersionInfo}
VersionInfoTextVersion={#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{userdesktop}\{#MyAppName}.lnk"; Tasks: desktopicon
Type: filesandordirs; Name: "{app}\Microsoft.WindowsAppRuntime"
Type: files; Name: "{app}\Microsoft.WinUI.dll"
Type: files; Name: "{app}\Microsoft.Windows.SDK.NET.dll"
Type: files; Name: "{app}\DirectML.dll"
Type: files; Name: "{app}\onnxruntime.dll"
; Remove legacy startup shortcut from previous versions that created it via Inno Setup.
Type: files; Name: "{userstartup}\{#MyAppName}.lnk"

[Files]
Source: "{#MyAppReleaseDir}\*"; DestDir: "{app}"; Excludes: "DeskBox.Updater.*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppReleaseDir}\DeskBox.Updater.*"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

#include "DeskBox.Migration.iss"
#include "DeskBox.Dependencies.iss"
#include "DeskBox.Uninstall.iss"
