[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")][string]$Platform = "x64",
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = "0.1.0"
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot (".artifacts\music-native\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Choose a new output directory; existing artifacts are preserved: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$rid = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
. (Join-Path $repoRoot "scripts\rust-arm64-msvc-environment.ps1")
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    $project = Join-Path $repoRoot "src\DeskBox.MusicPackage\DeskBox.MusicPackage.csproj"
    $publishDir = Join-Path $OutputDirectory "publish"
    & dotnet restore $project -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:PublishAot=true -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Music AOT restore failed." }
    & dotnet publish $project --no-restore -c Release -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:PublishAot=true -p:NativeLib=Shared -p:SelfContained=true -p:WindowsAppSDKSelfContained=false -p:IlcUseEnvironmentalTools=true -o $publishDir -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Music AOT publish failed." }
    $packageDir = Join-Path $OutputDirectory "package"
    New-Item -ItemType Directory -Path $packageDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir "DeskBox.MusicPackage.dll") -Destination (Join-Path $packageDir "package.dll")
    Copy-Item -LiteralPath (Join-Path $publishDir "Rendering\music.xaml") -Destination (Join-Path $packageDir "music.xaml")
    Copy-Item -LiteralPath (Join-Path $publishDir "strings") -Destination $packageDir -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageDir
    . (Join-Path $repoRoot "scripts\native-pe-contract.ps1")
    $peContract = Get-DeskBoxNativePeContract `
        -Path (Join-Path $packageDir "package.dll") `
        -ExpectedPlatform $Platform `
        -RequiredExports @(
            "deskbox_package_get_abi_version",
            "deskbox_package_activate",
            "deskbox_widget_create",
            "deskbox_widget_destroy",
            "deskbox_widget_event",
            "deskbox_package_shutdown")
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src\DeskBox.MusicPackage\manifest.json") | ConvertFrom-Json
    $manifest.version = $Version
    $manifest.entry.architecture = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $packageDir "manifest.json") -Encoding utf8NoBOM
    # Explicit DEVELOPMENT signing. Never use the public spike key for retail trust.
    & node (Join-Path $repoRoot "scripts\spike\build-package.mjs") $packageDir (Join-Path $repoRoot "spikes\keys")
    if ($LASTEXITCODE -ne 0) { throw "Music development signing failed." }
    & node (Join-Path $repoRoot "scripts\spike\validate-package.mjs") $packageDir
    if ($LASTEXITCODE -ne 0) { throw "Music package validation failed." }
    $evidence = [ordered]@{
        packageId = "deskbox.music"; version = $Version; platform = $Platform
        packageDirectory = $packageDir
        moduleSha256 = (Get-FileHash -LiteralPath (Join-Path $packageDir "package.dll") -Algorithm SHA256).Hash
        signingPolicy = "Development"
        peMachine = $peContract.MachineHex
        exports = $peContract.RequiredExports
        hostDependency = "deskbox_native.dll ABI 2, music-volume-v1 capability 32"
    }
    $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "build-result.json") -Encoding utf8NoBOM
    $evidence | ConvertTo-Json
}
finally { Exit-DeskBoxMsvcEnvironment -State $environmentState }
