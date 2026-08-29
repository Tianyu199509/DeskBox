; DeskBox 安装脚本
; 零售安装包由 scripts\build-stage-7c1-distribution.ps1 产出（Full Native AOT 载荷）。
; 载荷必须由 ..\scripts\publish-aot-retail.ps1 -Platform x64 生成，
; 以便同时生成 DeskBox.InstallManifest.txt。
; 手动编译安装器示例：
; ISCC /DDeskBoxNativeAot=1 /DDeskBoxBundledRuntime=1 /DMyAppReleaseDir=<publish 目录> DeskBox.iss

#define MyAppName "DeskBox"
#define MyAppVersion "1.4.8"
#define MyAppVersionInfo "1.4.8.0"
#define MyAppPublisher "朱天雨"
#define MyAppExeName "DeskBox.exe"
#define MyAppOutputBaseName "DeskBox_Setup"
#define MyAppRuntimeArchitecture "x64"
#ifndef MyAppPackageSuffix
#define MyAppPackageSuffix ""
#endif
#ifndef DeskBoxBundledRuntime
#define DeskBoxBundledRuntime 0
#endif
#ifndef DeskBoxNativeAot
#define DeskBoxNativeAot 0
#endif
#ifndef MyAppReleaseDir
#define MyAppReleaseDir "..\artifacts\publish\DeskBox\x64"
#endif

[Setup]
; AppId 用于唯一标识同一个应用。
AppId={{5E052824-3456-427E-9759-3BCAE078A1D3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
#if DeskBoxBundledRuntime
AppComments=Native AOT build with private Windows App Runtime components for offline installation.
#elif DeskBoxNativeAot
AppComments=Native AOT build; Windows App Runtime is installed when missing.
#else
AppComments={cm:RuntimeDependencyComment}
#endif
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\Assets\deskbox.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19044
DefaultDirName={code:GetDefaultInstallDir}
DisableProgramGroupPage=yes
DisableDirPage=no
; New installs recommend the machine-wide Program Files location while still
; allowing an unelevated current-user install. Registered upgrades retain their
; previous install mode and path.
PrivilegesRequired=admin
; "commandline" lets DeskBox.Updater pin the install scope with
; /CURRENTUSER or /ALLUSERS so its post-install consistency check cannot
; disagree with the scope the installer actually used.
PrivilegesRequiredOverridesAllowed=dialog commandline
; Upgrade paths are resolved and locked by DeskBox.Installation.iss before the
; directory page is shown.
; Directory reuse is handled exclusively by DeskBox.Installation.iss. Leaving
; Inno's previous-directory fallback enabled could resurrect a stale uninstall
; record after the detector intentionally classified the run as a first install.
UsePreviousAppDir=no
UsePreviousPrivileges=yes
; DeskBox is a tray-first WinUI app with multiple top-level windows. Restart
; Manager cannot always close the whole process through a single window, so
; allow Setup to terminate DeskBox after the normal close attempt times out.
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
OutputDir=..\Output
#if DeskBoxBundledRuntime
OutputBaseFilename={#MyAppOutputBaseName}_{#MyAppVersion}_x64
#else
OutputBaseFilename={#MyAppOutputBaseName}_{#MyAppVersion}_x64{#MyAppPackageSuffix}
#endif
SetupIconFile=..\src\DeskBox\Assets\deskbox.ico
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoProductVersion={#MyAppVersionInfo}
VersionInfoTextVersion={#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
; Let the user pick the installer language; the dialog pre-selects the
; language detected from the system locale. English is listed first so any
; locale that is neither Chinese nor English falls back to English.
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "Languages\English.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "Languages\ChineseTraditional.isl"
Name: "japanese"; MessagesFile: "Languages\Japanese.isl"
Name: "german"; MessagesFile: "Languages\German.isl"
Name: "brazilianportuguese"; MessagesFile: "Languages\BrazilianPortuguese.isl"
Name: "hindi"; MessagesFile: "Languages\Hindi.islu"
Name: "spanish"; MessagesFile: "Languages\Spanish.isl"
Name: "french"; MessagesFile: "Languages\French.isl"
Name: "arabic"; MessagesFile: "Languages\Arabic.isl"
Name: "bengali"; MessagesFile: "Languages\Bengali.islu"
Name: "russian"; MessagesFile: "Languages\Russian.isl"

[Messages]
#include "DeskBox.HindiBengaliMessages.iss"

[CustomMessages]
chinesesimplified.MultipleInstallationsTitle=检测到多个 DeskBox 安装
chinesesimplified.MultipleInstallationsBody=检测到以下多个 DeskBox 程序目录：%n%n%1
chinesesimplified.MultipleInstallationsFooter=为避免升级后产生新的 DeskBox，请先在系统设置中卸载多余版本，然后重新运行安装程序。
chinesesimplified.UpgradeDirectoryMismatch=检测到这是升级操作，但安装目录与已安装版本不一致。%n%n原安装目录：%1%n本次指定目录：%2%n%n升级必须使用原安装目录，请取消本次安装后重新运行。
english.MultipleInstallationsTitle=Multiple DeskBox installations detected
english.MultipleInstallationsBody=The following DeskBox installation directories were found:%n%n%1
english.MultipleInstallationsFooter=To prevent another copy from being created, uninstall the extra version from Windows Settings, then run Setup again.
english.UpgradeDirectoryMismatch=This is an upgrade, but the requested installation directory does not match the existing installation.%n%nExisting directory: %1%nRequested directory: %2%n%nAn upgrade must use the existing directory. Cancel Setup and run it again.
japanese.MultipleInstallationsTitle=複数の DeskBox インストールが検出されました
japanese.MultipleInstallationsBody=次の DeskBox インストール フォルダーが見つかりました：%n%n%1
japanese.MultipleInstallationsFooter=新しいコピーの作成を防ぐため、Windows の設定から余分なバージョンをアンインストールしてから、セットアップを再実行してください。
japanese.UpgradeDirectoryMismatch=アップグレードですが、指定されたインストール フォルダーが既存のインストールと一致しません。%n%n既存のフォルダー：%1%n指定されたフォルダー：%2%n%nアップグレードでは既存のフォルダーを使用する必要があります。セットアップをキャンセルして再実行してください。
german.MultipleInstallationsTitle=Mehrere DeskBox-Installationen erkannt
german.MultipleInstallationsBody=Die folgenden DeskBox-Installationsordner wurden gefunden:%n%n%1
german.MultipleInstallationsFooter=Um eine weitere Kopie zu verhindern, deinstallieren Sie die zusätzliche Version in den Windows-Einstellungen und führen Sie Setup erneut aus.
german.UpgradeDirectoryMismatch=Dies ist ein Upgrade, aber der angeforderte Installationsordner stimmt nicht mit der vorhandenen Installation überein.%n%nVorhandener Ordner: %1%nAngeforderter Ordner: %2%n%nEin Upgrade muss den vorhandenen Ordner verwenden. Brechen Sie Setup ab und führen Sie es erneut aus.
brazilianportuguese.MultipleInstallationsTitle=Várias instalações do DeskBox detectadas
brazilianportuguese.MultipleInstallationsBody=As seguintes pastas de instalação do DeskBox foram encontradas:%n%n%1
brazilianportuguese.MultipleInstallationsFooter=Para evitar a criação de outra cópia, desinstale a versão extra nas Configurações do Windows e execute a instalação novamente.
brazilianportuguese.UpgradeDirectoryMismatch=Esta é uma atualização, mas a pasta de instalação solicitada não corresponde à instalação existente.%n%nPasta existente: %1%nPasta solicitada: %2%n%nA atualização deve usar a pasta existente. Cancele a instalação e execute-a novamente.
chinesesimplified.ConfirmStorageTitle=检测到 DeskBox 收纳目录中仍有内容
chinesesimplified.ConfirmStorageBody=当前包含 %1 个文件夹、%2 个文件。
chinesesimplified.ConfirmStorageFooter=卸载 DeskBox 不会删除这个目录，也不会删除里面的用户文件。%n请确认你已经知道这些文件的位置。是否继续卸载？
chinesesimplified.AppDataChoiceTitle=选择卸载后保留的内容
chinesesimplified.ConfirmRemoveAppData=DeskBox 应用数据位于：%n%1%n%n其中包含设置、格子布局、待办、随记、缓存、日志、更新文件和恢复快照。%n%n收纳路径中的文件不会被删除。
chinesesimplified.KeepAppDataButton=保留应用数据%n保留设置和内容，重新安装后可继续使用
chinesesimplified.RemoveAppDataButton=彻底删除应用数据%n永久删除以上应用数据和恢复快照
chinesesimplified.AppDataCleanupFailed=部分应用数据未能删除，请手动检查：%n%1
chinesesimplified.FolderItem=[文件夹]
chinesesimplified.FileItem=[文件]
chinesesimplified.MoreItems=...还有 %1 项未显示
chinesesimplified.DependencyDownloadTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyDownloadSubtitle=正在下载缺少的运行时依赖。
chinesesimplified.DependencyInstallTitle=正在准备 DeskBox 运行环境
chinesesimplified.DependencyInstallSubtitle=正在安装缺少的运行时依赖。
chinesesimplified.DownloadingDotNet=正在下载 .NET 10 Runtime x64...
chinesesimplified.DownloadingWinAppRuntime=正在下载 Windows App Runtime 2.4 x64...
chinesesimplified.InstallingDependency=正在安装 %1...%n这可能需要几分钟，请勿关闭此窗口。
chinesesimplified.NeedsRestart=运行时依赖已安装，但 Windows 需要重启。请重启电脑后重新运行 DeskBox 安装程序。
chinesesimplified.DependencyVerificationFailed=依赖安装完成后，系统仍未识别到所需运行环境。DeskBox 尚未安装。请先安装稳定版 .NET 10 Runtime 和 Windows App Runtime 2.4，然后重新运行安装程序。
english.ConfirmStorageTitle=DeskBox storage folder still contains files
english.ConfirmStorageBody=It currently has %1 folder(s) and %2 file(s).
english.ConfirmStorageFooter=Uninstalling DeskBox will not delete this folder or any user files inside it.%nPlease confirm you know where these files are. Continue uninstalling?
english.AppDataChoiceTitle=Choose what to keep after uninstalling
english.ConfirmRemoveAppData=DeskBox application data is stored in:%n%1%n%nIt includes settings, widget layouts, todos, quick notes, caches, logs, update files, and recovery snapshots.%n%nFiles in the managed storage path will not be deleted.
english.KeepAppDataButton=Keep application data%nKeep settings and content for a future reinstall
english.RemoveAppDataButton=Permanently delete application data%nDelete the application data and recovery snapshots listed above
english.AppDataCleanupFailed=Some application data could not be removed. Check these locations manually:%n%1
english.FolderItem=[Folder]
english.FileItem=[File]
english.MoreItems=...and %1 more items not shown
english.DependencyDownloadTitle=Preparing DeskBox runtime
english.DependencyDownloadSubtitle=Downloading missing runtime dependencies.
english.DependencyInstallTitle=Preparing DeskBox runtime
english.DependencyInstallSubtitle=Installing missing runtime dependencies.
english.DownloadingDotNet=Downloading .NET 10 Runtime x64...
english.DownloadingWinAppRuntime=Downloading Windows App Runtime 2.4 x64...
english.InstallingDependency=Installing %1...%nThis may take a few minutes. Please do not close this window.
english.NeedsRestart=Runtime dependencies were installed, but Windows needs to restart. Restart your PC, then run DeskBox setup again.
english.DependencyVerificationFailed=The required runtime was still not detected after dependency installation. DeskBox has not been installed. Install the stable .NET 10 Runtime and Windows App Runtime 2.4, then run setup again.

japanese.ConfirmStorageTitle=DeskBox の保存フォルダにまだファイルがあります
japanese.ConfirmStorageBody=現在、フォルダ %1 個とファイル %2 個が含まれています。
japanese.ConfirmStorageFooter=DeskBox をアンインストールしても、このフォルダと中のユーザーファイルは削除されません。%nこれらのファイルの場所をご確認ください。続行しますか？
japanese.AppDataChoiceTitle=アンインストール後に残す内容を選択
japanese.ConfirmRemoveAppData=DeskBox のアプリデータは次の場所に保存されています：%n%1%n%n設定、ウィジェットのレイアウト、ToDo、クイックメモ、キャッシュ、ログ、更新ファイル、復元スナップショットが含まれます。%n%n収納先にあるファイルは削除されません。
japanese.KeepAppDataButton=アプリデータを保持%n再インストール時に設定と内容を引き継ぎます
japanese.RemoveAppDataButton=アプリデータを完全に削除%n上記のアプリデータと復元スナップショットを削除します
japanese.AppDataCleanupFailed=一部のアプリデータを削除できませんでした。次の場所を手動で確認してください：%n%1
japanese.FolderItem=[フォルダ]
japanese.FileItem=[ファイル]
japanese.MoreItems=...ほかに %1 件あります（非表示）
japanese.DependencyDownloadTitle=DeskBox の実行環境を準備しています
japanese.DependencyDownloadSubtitle=不足しているランタイム依存関係をダウンロードしています。
japanese.DependencyInstallTitle=DeskBox の実行環境を準備しています
japanese.DependencyInstallSubtitle=不足しているランタイム依存関係をインストールしています。
japanese.DownloadingDotNet=.NET 10 Runtime x64 をダウンロードしています...
japanese.DownloadingWinAppRuntime=Windows App Runtime 2.4 x64 をダウンロードしています...
japanese.InstallingDependency=%1 をインストールしています...%n数分かかる場合があります。このウィンドウを閉じないでください。
japanese.NeedsRestart=ランタイム依存関係はインストールされましたが、Windows の再起動が必要です。PC を再起動してから DeskBox セットアップを再度実行してください。
japanese.DependencyVerificationFailed=依存関係のインストール後も、必要なランタイムを確認できませんでした。DeskBox はまだインストールされていません。安定版の .NET 10 Runtime と Windows App Runtime 2.4 をインストールしてから、セットアップを再実行してください。

german.ConfirmStorageTitle=Der DeskBox-Speicherordner enthält noch Dateien
german.ConfirmStorageBody=Er enthält derzeit %1 Ordner und %2 Datei(en).
german.ConfirmStorageFooter=Das Deinstallieren von DeskBox löscht diesen Ordner und die darin enthaltenen Benutzerdateien nicht.%nBitte bestätigen Sie, dass Sie wissen, wo diese Dateien liegen. Deinstallation fortsetzen?
german.AppDataChoiceTitle=Auswählen, was nach der Deinstallation erhalten bleibt
german.ConfirmRemoveAppData=DeskBox-Anwendungsdaten befinden sich hier:%n%1%n%nDazu gehören Einstellungen, Widget-Layouts, Aufgaben, Kurznotizen, Caches, Protokolle, Updatedateien und Wiederherstellungspunkte.%n%nDateien im Ablagepfad werden nicht gelöscht.
german.KeepAppDataButton=Anwendungsdaten behalten%nEinstellungen und Inhalte für eine spätere Neuinstallation behalten
german.RemoveAppDataButton=Anwendungsdaten dauerhaft löschen%nDie oben genannten Daten und Wiederherstellungspunkte löschen
german.AppDataCleanupFailed=Einige Anwendungsdaten konnten nicht entfernt werden. Prüfen Sie diese Orte manuell:%n%1
german.FolderItem=[Ordner]
german.FileItem=[Datei]
german.MoreItems=...und %1 weitere Einträge werden nicht angezeigt
german.DependencyDownloadTitle=DeskBox-Laufzeitumgebung wird vorbereitet
german.DependencyDownloadSubtitle=Fehlende Laufzeitabhängigkeiten werden heruntergeladen.
german.DependencyInstallTitle=DeskBox-Laufzeitumgebung wird vorbereitet
german.DependencyInstallSubtitle=Fehlende Laufzeitabhängigkeiten werden installiert.
german.DownloadingDotNet=.NET 10 Runtime x64 wird heruntergeladen...
german.DownloadingWinAppRuntime=Windows App Runtime 2.4 x64 wird heruntergeladen...
german.InstallingDependency=%1 wird installiert...%nDies kann einige Minuten dauern. Bitte schließen Sie dieses Fenster nicht.
german.NeedsRestart=Die Laufzeitabhängigkeiten wurden installiert, aber Windows muss neu gestartet werden. Starten Sie den PC neu und führen Sie das DeskBox-Setup erneut aus.
german.DependencyVerificationFailed=Die erforderliche Laufzeit wurde nach der Installation der Abhängigkeiten weiterhin nicht erkannt. DeskBox wurde noch nicht installiert. Installieren Sie die stabile .NET 10 Runtime und Windows App Runtime 2.4, und starten Sie Setup erneut.

brazilianportuguese.ConfirmStorageTitle=A pasta de armazenamento do DeskBox ainda contém arquivos
brazilianportuguese.ConfirmStorageBody=Ela contém atualmente %1 pasta(s) e %2 arquivo(s).
brazilianportuguese.ConfirmStorageFooter=Desinstalar o DeskBox não excluirá esta pasta nem nenhum arquivo de usuário dentro dela.%nConfirme que você sabe onde esses arquivos estão. Continuar a desinstalação?
brazilianportuguese.AppDataChoiceTitle=Escolha o que manter após a desinstalação
brazilianportuguese.ConfirmRemoveAppData=Os dados do aplicativo DeskBox ficam em:%n%1%n%nEles incluem configurações, layouts de widgets, tarefas, notas rápidas, caches, registros, arquivos de atualização e pontos de recuperação.%n%nOs arquivos no caminho de armazenamento não serão excluídos.
brazilianportuguese.KeepAppDataButton=Manter dados do aplicativo%nManter configurações e conteúdo para uma futura reinstalação
brazilianportuguese.RemoveAppDataButton=Excluir dados do aplicativo permanentemente%nExcluir os dados e pontos de recuperação listados acima
brazilianportuguese.AppDataCleanupFailed=Alguns dados do aplicativo não puderam ser removidos. Verifique estes locais manualmente:%n%1
brazilianportuguese.FolderItem=[Pasta]
brazilianportuguese.FileItem=[Arquivo]
brazilianportuguese.MoreItems=...e mais %1 itens não exibidos
brazilianportuguese.DependencyDownloadTitle=Preparando o ambiente de execução do DeskBox
brazilianportuguese.DependencyDownloadSubtitle=Baixando as dependências de runtime ausentes.
brazilianportuguese.DependencyInstallTitle=Preparando o ambiente de execução do DeskBox
brazilianportuguese.DependencyInstallSubtitle=Instalando as dependências de runtime ausentes.
brazilianportuguese.DownloadingDotNet=Baixando o .NET 10 Runtime x64...
brazilianportuguese.DownloadingWinAppRuntime=Baixando o Windows App Runtime 2.4 x64...
brazilianportuguese.InstallingDependency=Instalando %1...%nIsso pode levar alguns minutos. Não feche esta janela.
brazilianportuguese.NeedsRestart=As dependências de runtime foram instaladas, mas o Windows precisa reiniciar. Reinicie o PC e execute o instalador do DeskBox novamente.
brazilianportuguese.DependencyVerificationFailed=O ambiente de execução necessário ainda não foi detectado após a instalação das dependências. O DeskBox ainda não foi instalado. Instale o .NET 10 Runtime estável e o Windows App Runtime 2.4 e execute o instalador novamente.

#include "DeskBox.NewLanguageCustomMessages.iss"
#include "DeskBox.UninstallCustomMessages.iss"
#include "DeskBox.DependencyCustomMessages.iss"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{autodesktop}\{#MyAppName}.lnk"; Tasks: desktopicon
; Remove legacy startup shortcut from previous versions that created it via Inno Setup.
Type: files; Name: "{autostartup}\{#MyAppName}.lnk"

[Files]
#if DeskBoxBundledRuntime
Source: "..\scripts\cleanup-deskbox-install.ps1"; Flags: dontcopy
Source: "DeskBox.LegacyBundledRuntimeFiles.txt"; Flags: dontcopy
Source: "{#MyAppReleaseDir}\DeskBox.InstallManifest.txt"; DestDir: "{tmp}"; DestName: "DeskBox.InstallManifest.current.txt"; Flags: ignoreversion deleteafterinstall
Source: "{#MyAppReleaseDir}\*"; DestDir: "{app}"; Excludes: "DeskBox.Updater.*,deskbox_native.dll,deskbox_native.pdb,DeskBox.InstallManifest.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppReleaseDir}\deskbox_native.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppReleaseDir}\DeskBox.Updater.*"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppReleaseDir}\DeskBox.InstallManifest.txt"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: CleanupDeskBoxInstall
#else
Source: "{#MyAppReleaseDir}\*"; DestDir: "{app}"; Excludes: "DeskBox.Updater.*,deskbox_native.dll,deskbox_native.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppReleaseDir}\deskbox_native.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppReleaseDir}\DeskBox.Updater.*"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\deskbox.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

#include "DeskBox.Installation.iss"
#include "DeskBox.Migration.iss"
#include "DeskBox.Dependencies.iss"
#include "DeskBox.Uninstall.iss"

[Registry]
; HKA maps installation metadata to HKLM for all-users installs and HKCU for
; current-user installs. The per-user language preference is handled below.
Root: HKA; Subkey: "Software\DeskBox\DirectInstall"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\DeskBox\DirectInstall"; ValueType: string; ValueName: "InstallVersion"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\DeskBox\DirectInstall"; ValueType: string; ValueName: "InstallScope"; ValueData: "{code:GetInstallScopeName}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Code]
function InstallLanguageCode(Value: string): string;
begin
  if ActiveLanguage = 'japanese' then Result := 'ja-JP'
  else if ActiveLanguage = 'german' then Result := 'de-DE'
  else if ActiveLanguage = 'brazilianportuguese' then Result := 'pt-BR'
  else if ActiveLanguage = 'hindi' then Result := 'hi-IN'
  else if ActiveLanguage = 'spanish' then Result := 'es-ES'
  else if ActiveLanguage = 'french' then Result := 'fr-FR'
  else if ActiveLanguage = 'arabic' then Result := 'ar-SA'
  else if ActiveLanguage = 'bengali' then Result := 'bn-BD'
  else if ActiveLanguage = 'russian' then Result := 'ru-RU'
  else if ActiveLanguage = 'chinesesimplified' then Result := 'zh-CN'
  else if ActiveLanguage = 'chinesetraditional' then Result := 'zh-TW'
  else Result := 'en-US';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Install language is a per-user preference. Never write it while running in
  // administrative install mode because HKCU may belong to an elevation
  // account rather than the person who launched Setup.
  if (CurStep = ssPostInstall) and (not IsAdminInstallMode) then
    RegWriteStringValue(
      HKEY_CURRENT_USER,
      'Software\DeskBox',
      'InstallLanguage',
      InstallLanguageCode(''));
end;
