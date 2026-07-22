; DeskBox 安装脚本
; 构建命令：
; dotnet publish ..\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o ..\artifacts\publish\DeskBox\x64 -v:minimal

#define MyAppName "DeskBox"
#define MyAppVersion "1.3.2"
#define MyAppVersionInfo "1.3.2.0"
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
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "Languages\English.isl"

[CustomMessages]
chinesesimplified.ConfirmStorageTitle=检测到 DeskBox 收纳目录中仍有内容
chinesesimplified.ConfirmStorageBody=当前包含 %1 个文件夹、%2 个文件。
chinesesimplified.ConfirmStorageFooter=卸载 DeskBox 不会删除这个目录，也不会删除里面的用户文件。%n请确认你已经知道这些文件的位置。是否继续卸载？
chinesesimplified.ConfirmRemoveAppData=是否同时删除 DeskBox 应用数据？%n%n这些数据包含设置、格子布局、随记图片缓存和日志：%1%n%n选择"否"会保留这些数据，之后重新安装 DeskBox 时仍可继续使用。
chinesesimplified.FolderItem=[文件夹]
chinesesimplified.FileItem=[文件]
chinesesimplified.MoreItems=...还有 %1 项未显示
chinesesimplified.DependencyDownloadTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyDownloadSubtitle=正在下载缺少的运行时依赖。
chinesesimplified.DependencyInstallTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyInstallSubtitle=正在安装缺少的运行时依赖。
chinesesimplified.DownloadingDotNet=正在下载 .NET 10 Runtime x64...
chinesesimplified.DownloadingWinAppRuntime=正在下载 Windows App Runtime 2.2 x64...
chinesesimplified.InstallingDependency=正在安装 %1...%n这可能需要几分钟，请勿关闭此窗口。
chinesesimplified.NeedsRestart=运行时依赖已安装，但 Windows 需要重启。请重启电脑后重新运行 DeskBox 安装程序。
english.ConfirmStorageTitle=DeskBox storage folder still contains files
english.ConfirmStorageBody=It currently has %1 folder(s) and %2 file(s).
english.ConfirmStorageFooter=Uninstalling DeskBox will not delete this folder or any user files inside it.%nPlease confirm you know where these files are. Continue uninstalling?
english.ConfirmRemoveAppData=Also remove DeskBox application data?%n%nThis includes settings, widget layouts, quick-capture image cache and logs:%1%n%nChoosing "No" will keep this data so it can be used if you reinstall DeskBox later.
english.FolderItem=[Folder]
english.FileItem=[File]
english.MoreItems=...and %1 more items not shown
english.DependencyDownloadTitle=Preparing DeskBox runtime
english.DependencyDownloadSubtitle=Downloading missing runtime dependencies.
english.DependencyInstallTitle=Preparing DeskBox runtime
english.DependencyInstallSubtitle=Installing missing runtime dependencies.
english.DownloadingDotNet=Downloading .NET 10 Runtime x64...
english.DownloadingWinAppRuntime=Downloading Windows App Runtime 2.2 x64...
english.InstallingDependency=Installing %1...%nThis may take a few minutes. Please do not close this window.
english.NeedsRestart=Runtime dependencies were installed, but Windows needs to restart. Restart your PC, then run DeskBox setup again.

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
Source: "{#MyAppReleaseDir}\DeskBox.Updater.*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

#include "DeskBox.Migration.iss"
#include "DeskBox.Dependencies.iss"
#include "DeskBox.Uninstall.iss"
