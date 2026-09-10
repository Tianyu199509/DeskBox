# Independent NativeAOT Weather payload. Defaults to the public DEVELOPMENT key.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [string]$OutputDir,
    [string]$SigningKeyDirectory,
    [string]$Version,
    [switch]$Smoke
)
$ErrorActionPreference = 'Stop'
$weatherRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
if (-not $OutputDir) {
    $OutputDir = Join-Path $weatherRepo ('.artifacts\weather-package\' + $rid + '\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw "Choose a fresh output directory: $OutputDir" }
New-Item -ItemType Directory -Path $OutputDir | Out-Null
if (-not $SigningKeyDirectory) { $SigningKeyDirectory = Join-Path $weatherRepo 'spikes\keys' }
. (Join-Path $weatherRepo 'scripts\rust-arm64-msvc-environment.ps1')
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$savedEnvironment = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    $project = Join-Path $weatherRepo 'src\DeskBox.WeatherPackage\DeskBox.WeatherPackage.csproj'
    $publishDir = Join-Path $OutputDir 'publish'
    $packageDir = Join-Path $OutputDir 'package'
    $smokeValue = $Smoke.IsPresent.ToString().ToLowerInvariant()
    & dotnet restore $project -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) { throw 'Weather AOT restore failed' }
    & dotnet publish $project --no-restore -c Release -p:Platform=$Platform -p:RuntimeIdentifier=$rid `
        -p:PublishAot=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
        -p:EnableWeatherPackageSmoke=$smokeValue -p:IlcUseEnvironmentalTools=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Weather AOT publish failed' }
    New-Item -ItemType Directory -Path $packageDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir 'DeskBox.WeatherPackage.dll') -Destination (Join-Path $packageDir 'package.dll')
    Copy-Item -LiteralPath (Join-Path $publishDir 'Rendering\weather.xaml') -Destination $packageDir
    Copy-Item -LiteralPath (Join-Path $publishDir 'strings') -Destination $packageDir -Recurse
    $manifest = Get-Content -LiteralPath (Join-Path $weatherRepo 'src\DeskBox.WeatherPackage\manifest.json') -Raw | ConvertFrom-Json
    if (-not $Version) { $Version = '0.1.' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
    $manifest.version = $Version
    $manifest.entry.architecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $packageDir 'manifest.json') -Encoding utf8NoBOM
    & node (Join-Path $weatherRepo 'scripts\spike\build-package.mjs') $packageDir $SigningKeyDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Weather signing failed' }
    & node (Join-Path $weatherRepo 'scripts\spike\validate-package.mjs') $packageDir
    if ($LASTEXITCODE -ne 0) { throw 'Weather package verification failed' }
    Write-Output "WEATHER_PACKAGE=$packageDir"
    Get-FileHash -LiteralPath (Join-Path $packageDir 'package.dll') -Algorithm SHA256
}
finally { Exit-DeskBoxMsvcEnvironment -State $savedEnvironment }
