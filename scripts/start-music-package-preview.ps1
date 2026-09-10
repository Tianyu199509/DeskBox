[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [string]$DataRoot, [switch]$RunSmoke, [switch]$KeepVisual)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $repoRoot 'src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build the canonical Debug host first: $exe" }
if (-not $DataRoot) { $DataRoot = Join-Path $repoRoot ('.artifacts\music-preview\' + [Guid]::NewGuid().ToString('N')) }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (Test-Path -LiteralPath $DataRoot) { throw "Use a fresh preview data root: $DataRoot" }
$data = Join-Path $DataRoot 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$settings = [ordered]@{
    schemaVersion = 10; hasCompletedOnboarding = $true; completedOnboardingVersion = 1
    hasResolvedInitialFileWidgetSetup = $true; language = 'zh-CN'; autoStart = $false
    globalHotkeyEnabled = $false; searchHotkeyEnabled = $false; desktopDoubleClickEnabled = $false
    automaticBackupEnabled = $false; desktopAutoOrganizationEnabled = $false
    featureWidgetEnabledStates = @{ Music = $true; Glance = $false; Weather = $false; Todo = $false; QuickCapture = $false; Search = $false }
    widgets = @(@{ id = 'music-native-preview'; name = 'Music package preview'; isDefaultTitle = $false; widgetKind = 'Music'; x = 160; y = 160; width = 360; height = 320; isVisible = $true; isDisabled = $false; metadata = @{} })
}
$settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8NoBOM
$info = [Diagnostics.ProcessStartInfo]::new($exe)
$info.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.Environment['DESKBOX_DEV_DATA_ROOT'] = $DataRoot
$info.Environment['DESKBOX_DEV_NATIVE_MUSIC'] = [IO.Path]::GetFullPath($PackageDirectory)
$info.Environment['DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV'] = '1'
$info.Environment['DESKBOX_VERBOSE_LOG'] = '1'
if ($RunSmoke) { $info.Environment['DESKBOX_DEV_MUSIC_SMOKE'] = '1' }
if ($KeepVisual) { $info.Environment['DESKBOX_DEV_MUSIC_VISUAL'] = '1' }
$process = [Diagnostics.Process]::Start($info)
[ordered]@{ processId = $process.Id; executable = $exe; dataRoot = $DataRoot; packageDirectory = $PackageDirectory } | ConvertTo-Json
