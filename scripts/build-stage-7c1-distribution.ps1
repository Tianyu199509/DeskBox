[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("x64", "ARM64")]
    [string]$Platform,

    [string]$OutputDirectory = "",

    [string]$DotNetPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$configuration = "Release"
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$platformSegment = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
$expectedMachine = if ($Platform -eq "ARM64") { 0xAA64 } else { 0x8664 }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot "stage7c1-distribution\$runtimeIdentifier"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a Stage 7C1 path outside '$normalizedRoot': '$normalizedCandidate'."
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a PE image."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' does not contain a PE signature."
        }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-InnoCompilerPath {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "ISCC.exe was not found. Stage 7C1 uses the Inno Setup installation provided by the GitHub Windows runner image."
}

Assert-PathInsideRoot -Root $artifactRoot -Candidate $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$directAuditScript = if ($Platform -eq "ARM64") {
    Join-Path $PSScriptRoot "publish-arm64-aot-static-audit.ps1"
}
else {
    Join-Path $PSScriptRoot "publish-aot-audit.ps1"
}
$directAuditArguments = @()
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $directAuditArguments += @("-DotNetPath", [System.IO.Path]::GetFullPath($DotNetPath))
}

Write-Host "Running the retained Direct Native AOT audit for $Platform..." -ForegroundColor Cyan
& $directAuditScript @directAuditArguments | Out-Host

$directAuditRoot = if ($Platform -eq "ARM64") {
    Join-Path $artifactRoot "aot-arm64-static-audit\win-arm64"
}
else {
    Join-Path $artifactRoot "aot-audit\win-x64"
}
$directAuditSummaryPath = Join-Path $directAuditRoot "summary.json"
if (-not (Test-Path -LiteralPath $directAuditSummaryPath -PathType Leaf)) {
    throw "The retained Direct AOT smoke audit did not produce its expected summary."
}
Copy-Item -LiteralPath $directAuditSummaryPath `
    -Destination (Join-Path $OutputDirectory "direct-aot-audit-summary.json") -Force

$retailPublishScript = Join-Path $PSScriptRoot "publish-aot-retail.ps1"
$retailPublishArguments = @{
    Platform = $Platform
}
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $retailPublishArguments.DotNetPath = [System.IO.Path]::GetFullPath($DotNetPath)
}
Write-Host "Publishing the smoke-free Direct Native AOT retail payload for $Platform..." `
    -ForegroundColor Cyan
& $retailPublishScript @retailPublishArguments | Out-Host

$directRetailRoot = Join-Path $artifactRoot "aot-retail\$runtimeIdentifier"
$directPublishDirectory = Join-Path $directRetailRoot "publish"
$directRetailSummaryPath = Join-Path $directRetailRoot "summary.json"
if (-not (Test-Path -LiteralPath $directRetailSummaryPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $directPublishDirectory -PathType Container)) {
    throw "The Direct AOT retail publish did not produce its expected summary and publish directory."
}
$directRetailSummary = Get-Content -LiteralPath $directRetailSummaryPath -Raw |
    ConvertFrom-Json
if ($directRetailSummary.productProfile -ne "retail" -or
    [bool]$directRetailSummary.smokeHarnessEnabled) {
    throw "The Direct installer payload is not a smoke-free retail AOT build."
}
Copy-Item -LiteralPath $directRetailSummaryPath `
    -Destination (Join-Path $OutputDirectory "direct-aot-retail-summary.json") -Force

$directFiles = @(
    Get-ChildItem -LiteralPath $directPublishDirectory -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($directPublishDirectory.Length + 1).Replace('\', '/')
        } |
        Sort-Object
)
$directFiles | Set-Content -LiteralPath (Join-Path $OutputDirectory "direct-publish-files.txt") `
    -Encoding utf8

$directRequiredFiles = @(
    "DeskBox.exe",
    "DeskBox.Updater.exe",
    "DeskBox.ThumbnailProxy.exe",
    "deskbox_native.dll",
    "EverythingSdk.dll",
    "ThirdParty/Everything/LICENSE.txt",
    "DeskBox.pri"
)
$missingDirectFiles = @($directRequiredFiles | Where-Object { $directFiles -notcontains $_ })
$directForbiddenPatterns = @(
    '\.pdb$',
    '(^|/)DeskBox\.dll$',
    '(^|/)DeskBox\.deps\.json$',
    '(^|/)DeskBox\.runtimeconfig\.json$',
    '(^|/)(?:coreclr|clrjit|hostfxr|hostpolicy)\.dll$',
    '(^|/)Assets/Store/',
    'store-assets-html'
)
$forbiddenDirectFiles = @(
    @(
        foreach ($directFile in $directFiles) {
            foreach ($pattern in $directForbiddenPatterns) {
                if ($directFile -match $pattern) {
                    $directFile
                    break
                }
            }
        }
    ) | Sort-Object -Unique
)
if ($missingDirectFiles.Count -gt 0) {
    throw "The Direct AOT publish is missing required files: $($missingDirectFiles -join ', ')."
}
if ($forbiddenDirectFiles.Count -gt 0) {
    throw "The Direct AOT publish contains forbidden files: $($forbiddenDirectFiles -join ', ')."
}

$deskBoxMachine = Get-PeMachine -Path (Join-Path $directPublishDirectory "DeskBox.exe")
$updaterMachine = Get-PeMachine -Path (Join-Path $directPublishDirectory "DeskBox.Updater.exe")
$thumbnailProxyMachine = Get-PeMachine -Path (
    Join-Path $directPublishDirectory "DeskBox.ThumbnailProxy.exe")
$everythingSdkMachine = Get-PeMachine -Path (
    Join-Path $directPublishDirectory "EverythingSdk.dll")
if ($deskBoxMachine -ne $expectedMachine -or
    $updaterMachine -ne $expectedMachine -or
    $thumbnailProxyMachine -ne $expectedMachine -or
    $everythingSdkMachine -ne $expectedMachine) {
    throw "The Direct AOT executable architecture does not match $Platform."
}

. (Join-Path $PSScriptRoot "native-pe-contract.ps1")
$nativeExports = @(
    "deskbox_native_abi_version",
    "deskbox_native_capabilities",
    "deskbox_shortcut_read_v2",
    "deskbox_shortcut_resolve_no_ui_v2",
    "deskbox_shortcut_write_v2",
    "deskbox_shortcut_resolve_with_ui_v2",
    "deskbox_music_volume_v1",
    "deskbox_explorer_shell_launch_v1",
    "deskbox_quick_access_v1",
    "deskbox_recycle_bin_v1"
)
$nativeContract = Get-DeskBoxNativePeContract `
    -Path (Join-Path $directPublishDirectory "deskbox_native.dll") `
    -ExpectedPlatform $Platform `
    -RequiredExports $nativeExports
$vcImports = @(
    @($nativeContract.ImportedModules) |
        Where-Object { $_ -match '^(?:VCRUNTIME|MSVCP|ucrtbase)' } |
        Sort-Object -Unique
)
if ($vcImports.Count -gt 0) {
    throw "Static Rust modules unexpectedly import VC runtime DLLs: $($vcImports -join ', ')."
}

$installerOutputDirectory = Join-Path $OutputDirectory "direct-installer"
New-Item -ItemType Directory -Path $installerOutputDirectory -Force | Out-Null
$installerScript = if ($Platform -eq "ARM64") {
    Join-Path $repoRoot "installer\DeskBox.arm64.iss"
}
else {
    Join-Path $repoRoot "installer\DeskBox.iss"
}
$installerScriptText = Get-Content -LiteralPath $installerScript -Raw
$installerBaseNameMatch = [regex]::Match(
    $installerScriptText,
    '(?m)^\s*#define\s+MyAppOutputBaseName\s+"([^"]+)"\s*$')
$installerVersionMatch = [regex]::Match(
    $installerScriptText,
    '(?m)^\s*#define\s+MyAppVersion\s+"([^"]+)"\s*$')
if (-not $installerBaseNameMatch.Success -or -not $installerVersionMatch.Success) {
    throw "The Inno script does not expose MyAppOutputBaseName and MyAppVersion definitions."
}
# Keep the DeskBox_Setup_<version>_<arch>.exe shape: every released updater
# (1.4.3 and later) only accepts assets and manifest URLs ending in
# "_x64.exe"/"_arm64.exe", and the distribution workflow publishes this exact
# name to GitHub Releases and stable.json. Flavor suffixes break that contract.
$installerOutputBaseName = "{0}_{1}_{2}" -f `
    $installerBaseNameMatch.Groups[1].Value,
    $installerVersionMatch.Groups[1].Value,
    $platformSegment
$innoCompiler = Get-InnoCompilerPath
$innoLogPath = Join-Path $OutputDirectory "inno-compile.log"
$innoArguments = @(
    "/Qp",
    "/DDeskBoxNativeAot=1",
    "/DMyAppReleaseDir=$directPublishDirectory",
    "/F$installerOutputBaseName",
    "/O$installerOutputDirectory",
    $installerScript
)
Write-Host "Compiling the Direct Native AOT installer with '$innoCompiler'..." -ForegroundColor Cyan
& $innoCompiler @innoArguments 2>&1 | Tee-Object -FilePath $innoLogPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed for $Platform (exit $LASTEXITCODE). See '$innoLogPath'."
}

$installer = Get-ChildItem -LiteralPath $installerOutputDirectory -Filter *.exe -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $installer -or $installer.Name -ne "$installerOutputBaseName.exe") {
    throw "The Native AOT Inno installer was not produced with the expected name '$installerOutputBaseName.exe'."
}

$storeOutputDirectory = Join-Path $OutputDirectory "store"
$storeBuildArguments = @{
    Configuration = $configuration
    Platform = $Platform
    NativeAot = $true
    PackageBuildMode = "StoreUpload"
    OutputDir = $storeOutputDirectory
}
Write-Host "Building the Store Native AOT MSIX/upload for $Platform..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build-store-msix.ps1") @storeBuildArguments | Out-Host

$msix = Get-ChildItem -LiteralPath $storeOutputDirectory -Filter *.msix -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$appxSym = Get-ChildItem -LiteralPath $storeOutputDirectory -Filter *.appxsym -Recurse -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$msixUpload = Get-ChildItem -LiteralPath $storeOutputDirectory -Filter *.msixupload -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $msix -or $null -eq $appxSym -or $null -eq $msixUpload) {
    throw "The StoreUpload build did not produce an MSIX, appxsym, and msixupload."
}

$storePublishDirectory = Join-Path $repoRoot (
    "src\DeskBox\bin\$Platform\$configuration\net10.0-windows10.0.22621.0\$runtimeIdentifier\publish")
$storeAuditDirectory = Join-Path $OutputDirectory "store-audit"
& (Join-Path $PSScriptRoot "audit-store-native-aot-package.ps1") `
    -MsixPath $msix.FullName `
    -AppxSymPath $appxSym.FullName `
    -ExpectedPlatform $Platform `
    -ExpectedPublishDirectory $storePublishDirectory `
    -OutputDirectory $storeAuditDirectory | Out-Host
$storeAuditSummary = Get-Content -LiteralPath (Join-Path $storeAuditDirectory "summary.json") `
    -Raw | ConvertFrom-Json
if ($storeAuditSummary.status -ne "passed") {
    throw "The Store Native AOT package audit did not pass."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$uploadArchive = [System.IO.Compression.ZipFile]::OpenRead($msixUpload.FullName)
try {
    $uploadEntries = @($uploadArchive.Entries | ForEach-Object FullName | Sort-Object)
}
finally {
    $uploadArchive.Dispose()
}
if (@($uploadEntries | Where-Object { $_ -like '*.msix' }).Count -ne 1 -or
    @($uploadEntries | Where-Object { $_ -like '*.appxsym' }).Count -ne 1) {
    throw "The msixupload must contain exactly one architecture MSIX and one appxsym."
}

$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer.FullName
$summary = [ordered]@{
    schemaVersion = 1
    status = "passed"
    platform = $Platform
    runtimeIdentifier = $runtimeIdentifier
    host = [ordered]@{
        osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        githubActions = $env:GITHUB_ACTIONS -eq "true"
        runnerImage = $env:ImageOS
    }
    direct = [ordered]@{
        retainedAuditSummary = $directAuditSummaryPath
        retailPublishSummary = $directRetailSummaryPath
        smokeHarnessEnabled = $false
        publishDirectory = $directPublishDirectory
        fileCount = $directFiles.Count
        requiredFiles = $directRequiredFiles
        missingRequiredFiles = $missingDirectFiles
        forbiddenFiles = $forbiddenDirectFiles
        executableMachine = "0x$($deskBoxMachine.ToString('X4'))"
        updaterMachine = "0x$($updaterMachine.ToString('X4'))"
        thumbnailProxyMachine = "0x$($thumbnailProxyMachine.ToString('X4'))"
        rustNative = $nativeContract
        crtLinkage = "Static"
        vcRuntimeImports = $vcImports
        installer = [ordered]@{
            path = $installer.FullName
            bytes = $installer.Length
            sha256 = Get-FileSha256 -Path $installer.FullName
            signatureStatus = $installerSignature.Status.ToString()
            nativeAotDefine = $true
            dotNetRuntimeDependencySkipped = $true
            windowsAppRuntimeDependencyRetained = $true
        }
    }
    store = [ordered]@{
        packageAuditSummary = (Join-Path $storeAuditDirectory "summary.json")
        msix = $msix.FullName
        msixBytes = $msix.Length
        msixSha256 = Get-FileSha256 -Path $msix.FullName
        appxSym = $appxSym.FullName
        appxSymBytes = $appxSym.Length
        msixUpload = $msixUpload.FullName
        msixUploadBytes = $msixUpload.Length
        msixUploadSha256 = Get-FileSha256 -Path $msixUpload.FullName
        uploadEntries = $uploadEntries
        rustNativePackaged = $true
        updaterPackaged = $false
    }
    evidenceBoundary = [ordered]@{
        hostedRunnerExecution = $env:GITHUB_ACTIONS -eq "true"
        physicalUserDeviceExecuted = $false
        interactiveDesktopExecuted = $false
        installerInstallationExecuted = $false
        msixInstallationExecuted = $false
        signingExecuted = $false
        wackExecuted = $false
        inPlaceUpgradeExecuted = $false
        storeFlightExecuted = $false
    }
}
$summaryPath = Join-Path $OutputDirectory "summary.json"
[System.IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Status = "passed"
    Platform = $Platform
    Summary = $summaryPath
    Installer = $installer.FullName
    Msix = $msix.FullName
    MsixUpload = $msixUpload.FullName
}
