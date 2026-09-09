# Build the official Glance native package: publish AOT DLL, assemble the
# installable directory (manifest + integrity + signature + DLL + XAML), and
# install it through the B1 PluginPackageManager pipeline.
[CmdletBinding()]
param(
    [ValidateSet("x64")][string]$Platform = "x64",
    [string]$OutputDir
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $repoRoot "scripts\rust-arm64-msvc-environment.ps1")
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot ".artifacts\official-glance-package" }
    Remove-Item -LiteralPath $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    # 1. Publish the package project as AOT with the DLL named package.dll.
    $project = Join-Path $repoRoot "spikes\glance-native\Package\Glance.NativePackage.csproj"
    $publishDir = Join-Path $OutputDir ".publish"
    & dotnet restore $project -p:Platform=$Platform -p:RuntimeIdentifier="win-$Platform" -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }
    & dotnet publish $project --no-restore -c Release -p:Platform=$Platform -p:RuntimeIdentifier="win-$Platform" `
        -p:PublishAot=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
        -p:IlcUseEnvironmentalTools=true -p:GlancePackageVersion=3 -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "AOT publish failed" }

    # 2. Assemble the installable directory.
    $packageDir = Join-Path $OutputDir "package"
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    $sourceDll = Join-Path $publishDir "DeskBox.Glance.NativePackage.dll"
    if (-not (Test-Path $sourceDll)) { throw "AOT DLL not found: $sourceDll" }
    # Required payload files - fail fast if any is missing (audit round 13 §30).
    foreach ($requiredFile in @("DeskBox.Glance.NativePackage.dll", "glance-real.xaml")) {
        $source = Join-Path $publishDir $requiredFile
        if (-not (Test-Path $source)) { throw "required payload missing from AOT publish: $requiredFile" }
    }
    Copy-Item (Join-Path $publishDir "DeskBox.Glance.NativePackage.dll") (Join-Path $packageDir "package.dll")
    Copy-Item (Join-Path $publishDir "glance-real.xaml") $packageDir
    # Optional XAML files (probe variants; may be absent without breaking the package).
    foreach ($optionalFile in @("calendar.xaml", "full-glance.xaml")) {
        $source = Join-Path $publishDir $optionalFile
        if (Test-Path $source) { Copy-Item $source $packageDir }
    }
    Copy-Item (Join-Path $repoRoot "spikes\glance-native\OfficialPackage\manifest.json") $packageDir

    # 3. Generate integrity manifest and signature using the Node tooling.
    $keysDir = Join-Path $repoRoot "spikes\keys"
    & node (Join-Path $repoRoot "scripts\spike\build-package.mjs") $packageDir $keysDir
    if ($LASTEXITCODE -ne 0) { throw "integrity + signature generation failed" }

    # 4. Validate the assembled package.
    & node (Join-Path $repoRoot "scripts\spike\validate-package.mjs") $packageDir
    if ($LASTEXITCODE -ne 0) { throw "package validation failed" }

    Write-Output "package assembled: $packageDir"
    Get-ChildItem $packageDir -File | ForEach-Object {
        Write-Output ("  {0}  {1:N0} bytes" -f $_.Name, $_.Length)
    }
    Write-Output "output root: $OutputDir"
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}
