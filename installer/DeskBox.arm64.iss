; DeskBox ARM64 安装脚本
; 构建命令：
; dotnet publish ..\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o ..\artifacts\publish\DeskBox\arm64 -v:minimal

#define MyAppName "DeskBox"
#define MyAppVersion "1.3.2"
#define MyAppVersionInfo "1.3.2.0"
#define MyAppPublisher "朱天雨"
#define MyAppExeName "DeskBox.exe"
#define MyAppOutputBaseName "DeskBox_Setup"
#ifndef MyAppReleaseDir
#define MyAppReleaseDir "..\artifacts\publish\DeskBox\arm64"
#endif

[Setup]
AppId={{5E052824-3456-427E-9759-3BCAE078A1D3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=安装包会按需检测并下载 .NET 10 Runtime 和 Windows App Runtime 2.2。
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\Assets\deskbox.ico
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
UsePreviousAppDir=no
UsePreviousPrivileges=no
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
OutputDir=..\Output
OutputBaseFilename={#MyAppOutputBaseName}_{#MyAppVersion}_arm64
SetupIconFile=..\src\DeskBox\Assets\deskbox.ico
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoProductVersion={#MyAppVersionInfo}
VersionInfoTextVersion={#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "Languages\English.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "Languages\Japanese.isl"
Name: "german"; MessagesFile: "Languages\German.isl"
Name: "brazilianportuguese"; MessagesFile: "Languages\BrazilianPortuguese.isl"

[CustomMessages]
chinesesimplified.DependencyDownloadTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyDownloadSubtitle=正在下载缺少的运行时依赖。
chinesesimplified.DependencyInstallTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyInstallSubtitle=正在安装缺少的运行时依赖。
chinesesimplified.DownloadingDotNet=正在下载 .NET 10 Runtime ARM64...
chinesesimplified.DownloadingWinAppRuntime=正在下载 Windows App Runtime 2.2 ARM64...
chinesesimplified.InstallingDependency=正在安装 %1...%n这可能需要几分钟，请勿关闭此窗口。
chinesesimplified.NeedsRestart=运行时依赖已安装，但 Windows 需要重启。请重启电脑后重新运行 DeskBox 安装程序。
english.DependencyDownloadTitle=Preparing DeskBox runtime
english.DependencyDownloadSubtitle=Downloading missing runtime dependencies.
english.DependencyInstallTitle=Preparing DeskBox runtime
english.DependencyInstallSubtitle=Installing missing runtime dependencies.
english.DownloadingDotNet=Downloading .NET 10 Runtime ARM64...
english.DownloadingWinAppRuntime=Downloading Windows App Runtime 2.2 ARM64...
english.InstallingDependency=Installing %1...%nThis may take a few minutes. Please do not close this window.
english.NeedsRestart=Runtime dependencies were installed, but Windows needs to restart. Restart your PC, then run DeskBox setup again.
japanese.ConfirmStorageTitle=DeskBox の保存フォルダにまだファイルがあります
japanese.ConfirmStorageBody=現在、フォルダ %1 個とファイル %2 個が含まれています。
japanese.ConfirmStorageFooter=DeskBox をアンインストールしても、このフォルダと中のユーザーファイルは削除されません。%nこれらのファイルの場所をご確認ください。続行しますか？
japanese.ConfirmRemoveAppData=DeskBox のアプリデータも削除しますか？%n%n設定、ウィジェットのレイアウト、クイックキャプチャの画像キャッシュ、ログが含まれます：%1%n%n「いいえ」を選ぶとデータは保持され、後で DeskBox を再インストールしても利用できます。
japanese.FolderItem=[フォルダ]
japanese.FileItem=[ファイル]
japanese.MoreItems=...ほかに %1 件あります（非表示）
japanese.DependencyDownloadTitle=DeskBox の実行環境を準備しています
japanese.DependencyDownloadSubtitle=不足しているランタイム依存関係をダウンロードしています。
japanese.DependencyInstallTitle=DeskBox の実行環境を準備しています
japanese.DependencyInstallSubtitle=不足しているランタイム依存関係をインストールしています。
japanese.DownloadingDotNet=.NET 10 Runtime ARM64 をダウンロードしています...
japanese.DownloadingWinAppRuntime=Windows App Runtime 2.2 ARM64 をダウンロードしています...
japanese.InstallingDependency=%1 をインストールしています...%n数分かかる場合があります。このウィンドウを閉じないでください。
japanese.NeedsRestart=ランタイム依存関係はインストールされましたが、Windows の再起動が必要です。PC を再起動してから DeskBox セットアップを再度実行してください。
german.ConfirmStorageTitle=Der DeskBox-Speicherordner enthält noch Dateien
german.ConfirmStorageBody=Er enthält derzeit %1 Ordner und %2 Datei(en).
german.ConfirmStorageFooter=Das Deinstallieren von DeskBox löscht diesen Ordner und die darin enthaltenen Benutzerdateien nicht.%nBitte bestätigen Sie, dass Sie wissen, wo diese Dateien liegen. Deinstallation fortsetzen?
german.ConfirmRemoveAppData=Auch die DeskBox-Anwendungsdaten entfernen?%n%nDiese umfassen Einstellungen, Widget-Layouts, den Bildcache der Schnellerfassung und Protokolle:%1%n%nMit „Nein“ bleiben diese Daten erhalten und können bei einer späteren Neuinstallation von DeskBox weiterverwendet werden.
german.FolderItem=[Ordner]
german.FileItem=[Datei]
german.MoreItems=...und %1 weitere Einträge werden nicht angezeigt
german.DependencyDownloadTitle=DeskBox-Laufzeitumgebung wird vorbereitet
german.DependencyDownloadSubtitle=Fehlende Laufzeitabhängigkeiten werden heruntergeladen.
german.DependencyInstallTitle=DeskBox-Laufzeitumgebung wird vorbereitet
german.DependencyInstallSubtitle=Fehlende Laufzeitabhängigkeiten werden installiert.
german.DownloadingDotNet=.NET 10 Runtime ARM64 wird heruntergeladen...
german.DownloadingWinAppRuntime=Windows App Runtime 2.2 ARM64 wird heruntergeladen...
german.InstallingDependency=%1 wird installiert...%nDies kann einige Minuten dauern. Bitte schließen Sie dieses Fenster nicht.
german.NeedsRestart=Die Laufzeitabhängigkeiten wurden installiert, aber Windows muss neu gestartet werden. Starten Sie den PC neu und führen Sie das DeskBox-Setup erneut aus.
brazilianportuguese.ConfirmStorageTitle=A pasta de armazenamento do DeskBox ainda contém arquivos
brazilianportuguese.ConfirmStorageBody=Ela contém atualmente %1 pasta(s) e %2 arquivo(s).
brazilianportuguese.ConfirmStorageFooter=Desinstalar o DeskBox não excluirá esta pasta nem nenhum arquivo de usuário dentro dela.%nConfirme que você sabe onde esses arquivos estão. Continuar a desinstalação?
brazilianportuguese.ConfirmRemoveAppData=Também remover os dados do aplicativo DeskBox?%n%nIsso inclui configurações, layouts de widgets, cache de imagens da captura rápida e registros:%1%n%nEscolher „Não” mantém esses dados, que poderão ser usados se você reinstalar o DeskBox mais tarde.
brazilianportuguese.FolderItem=[Pasta]
brazilianportuguese.FileItem=[Arquivo]
brazilianportuguese.MoreItems=...e mais %1 itens não exibidos
brazilianportuguese.DependencyDownloadTitle=Preparando o ambiente de execução do DeskBox
brazilianportuguese.DependencyDownloadSubtitle=Baixando as dependências de runtime ausentes.
brazilianportuguese.DependencyInstallTitle=Preparando o ambiente de execução do DeskBox
brazilianportuguese.DependencyInstallSubtitle=Instalando as dependências de runtime ausentes.
brazilianportuguese.DownloadingDotNet=Baixando o .NET 10 Runtime ARM64...
brazilianportuguese.DownloadingWinAppRuntime=Baixando o Windows App Runtime 2.2 ARM64...
brazilianportuguese.InstallingDependency=Instalando %1...%nIsso pode levar alguns minutos. Não feche esta janela.
brazilianportuguese.NeedsRestart=As dependências de runtime foram instaladas, mas o Windows precisa reiniciar. Reinicie o PC e execute o instalador do DeskBox novamente.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{userdesktop}\{#MyAppName}.lnk"; Tasks: desktopicon
Type: filesandordirs; Name: "{app}\Microsoft.WindowsAppRuntime"
Type: files; Name: "{app}\Microsoft.WinUI.dll"
Type: files; Name: "{app}\Microsoft.Windows.SDK.NET.dll"
Type: files; Name: "{app}\DirectML.dll"
Type: files; Name: "{app}\onnxruntime.dll"
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
#include "DeskBox.Dependencies.arm64.iss"
#include "DeskBox.Uninstall.iss"

[Registry]
; Record the language chosen at install time so the DeskBox app can
; default to it on first run (when the user has not manually
; changed the in-app language). Read by LocalizationService.
Root: HKCU; Subkey: "Software\DeskBox"; ValueType: string; ValueName: "InstallLanguage"; ValueData: "{code:InstallLanguageCode}"; Flags: uninsdeletevalue

[Code]
function InstallLanguageCode(Value: string): string;
begin
  if ActiveLanguage = 'japanese' then Result := 'ja-JP'
  else if ActiveLanguage = 'german' then Result := 'de-DE'
  else if ActiveLanguage = 'brazilianportuguese' then Result := 'pt-BR'
  else if ActiveLanguage = 'chinesesimplified' then Result := 'zh-CN'
  else Result := 'en-US';
end;
