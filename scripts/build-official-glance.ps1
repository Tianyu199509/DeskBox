# Build the official Glance native package from the D3 project (src/DeskBox.GlancePackage).
[CmdletBinding()]
param(
    [ValidateSet("x64")][string]$Platform = "x64",
    [string]$OutputDir
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $repoRoot "scripts\rust-arm64-msvc-environment.ps1")
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot ".artifacts\official-glance-package" }
    Remove-Item -LiteralPath $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    $project = Join-Path $repoRoot "src\DeskBox.GlancePackage\DeskBox.GlancePackage.csproj"
    $publishDir = Join-Path $OutputDir ".publish"
    & dotnet restore $project -p:Platform=$Platform -p:RuntimeIdentifier="win-$Platform" -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }
    & dotnet publish $project --no-restore -c Release -p:Platform=$Platform -p:RuntimeIdentifier="win-$Platform" `
        -p:PublishAot=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=false `
        -p:IlcUseEnvironmentalTools=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "AOT publish failed" }

    $packageDir = Join-Path $OutputDir "package"
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

    # Required payload files - fail fast if any is missing.
    $dllName = "DeskBox.GlancePackage.dll"
    foreach ($requiredFile in @($dllName, "Rendering\glance.xaml")) {
        $source = Join-Path $publishDir $requiredFile
        if (-not (Test-Path $source)) { throw "required payload missing: $requiredFile" }
    }
    $manifestSource = Join-Path $repoRoot "spikes\glance-native\OfficialPackage\manifest.json"
    if (-not (Test-Path $manifestSource)) { throw "required payload missing: manifest.json" }
    Copy-Item (Join-Path $publishDir $dllName) (Join-Path $packageDir "package.dll")
    Copy-Item (Join-Path $publishDir "Rendering\glance.xaml") $packageDir
    if (Test-Path (Join-Path $publishDir "strings")) {
        Copy-Item (Join-Path $publishDir "strings") $packageDir -Recurse
    }
    Copy-Item (Join-Path $repoRoot "spikes\glance-native\OfficialPackage\manifest.json") $packageDir

    # Sign.
    $keysDir = Join-Path $repoRoot "spikes\keys"
    & node (Join-Path $repoRoot "scripts\spike\build-package.mjs") $packageDir $keysDir
    if ($LASTEXITCODE -ne 0) { throw "integrity + signature generation failed" }
    & node (Join-Path $repoRoot "scripts\spike\validate-package.mjs") $packageDir
    if ($LASTEXITCODE -ne 0) { throw "package validation failed" }

    Write-Output "package assembled: $packageDir"
    Get-ChildItem $packageDir -File | ForEach-Object {
        Write-Output ("  {0}  {1:N0} bytes" -f $_.Name, $_.Length)
    }
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}
