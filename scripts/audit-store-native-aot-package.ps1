[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsixPath,

    [Parameter(Mandatory)]
    [ValidateSet("x64", "ARM64")]
    [string]$ExpectedPlatform,

    [string]$AppxSymPath = "",

    [string]$ExpectedPublishDirectory = "",

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$resolvedMsix = [System.IO.Path]::GetFullPath($MsixPath)
$platformSegment = if ($ExpectedPlatform -eq "ARM64") { "arm64" } else { "x64" }
$expectedMachine = if ($ExpectedPlatform -eq "ARM64") { 0xAA64 } else { 0x8664 }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot "stage7c1-store-audit\$platformSegment"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a Store audit path outside '$normalizedRoot': '$normalizedCandidate'."
    }
}

function Get-MakeAppxPath {
    $command = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $processArchitecture =
        [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $preferredArchitecture = if (
        $processArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        "arm64"
    }
    else {
        "x64"
    }

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $kitsBin -Filter makeappx.exe -Recurse -File `
            -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Directory.Name -ieq $preferredArchitecture -or
            ($preferredArchitecture -eq "arm64" -and $_.Directory.Name -ieq "x64")
        } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "MakeAppx.exe was not found in PATH or the installed Windows SDK."
    }

    return $candidate.FullName
}

function Get-PeFacts {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x100 -or [System.BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) {
        throw "'$Path' is not a valid PE image."
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 0x108 -gt $bytes.Length -or
        [System.BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "'$Path' does not contain a valid PE signature."
    }

    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $optionalOffset = $peOffset + 24
    $optionalMagic = [System.BitConverter]::ToUInt16($bytes, $optionalOffset)
    $dataDirectoryOffset = if ($optionalMagic -eq 0x20B) {
        $optionalOffset + 112
    }
    elseif ($optionalMagic -eq 0x10B) {
        $optionalOffset + 96
    }
    else {
        throw "'$Path' has an unsupported PE optional-header magic."
    }

    $clrDirectoryOffset = $dataDirectoryOffset + (14 * 8)
    $clrRva = [System.BitConverter]::ToUInt32($bytes, $clrDirectoryOffset)
    $clrSize = [System.BitConverter]::ToUInt32($bytes, $clrDirectoryOffset + 4)
    [pscustomobject]@{
        Path = [System.IO.Path]::GetFullPath($Path)
        Length = $bytes.LongLength
        Machine = $machine
        MachineHex = "0x$($machine.ToString('X4'))"
        HasClrHeader = $clrRva -ne 0 -or $clrSize -ne 0
        ClrRva = $clrRva
        ClrSize = $clrSize
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

if (-not (Test-Path -LiteralPath $resolvedMsix -PathType Leaf)) {
    throw "The MSIX package does not exist: '$resolvedMsix'."
}
Assert-PathInsideRoot -Root $artifactRoot -Candidate $OutputDirectory
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$layoutDirectory = Join-Path $OutputDirectory "package-layout"
Assert-PathInsideRoot -Root $OutputDirectory -Candidate $layoutDirectory
if (Test-Path -LiteralPath $layoutDirectory) {
    Remove-Item -LiteralPath $layoutDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $layoutDirectory | Out-Null

$makeAppx = Get-MakeAppxPath
& $makeAppx unpack /p $resolvedMsix /d $layoutDirectory /o | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed to unpack '$resolvedMsix' (exit $LASTEXITCODE)."
}

$fileEntries = @(
    Get-ChildItem -LiteralPath $layoutDirectory -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($layoutDirectory.Length + 1).Replace('\', '/')
        } |
        Sort-Object
)
$fileEntries | Set-Content -LiteralPath (Join-Path $OutputDirectory "package-files.txt") `
    -Encoding utf8

$manifestPath = Join-Path $layoutDirectory "AppxManifest.xml"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The unpacked package does not contain AppxManifest.xml."
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $OutputDirectory "AppxManifest.xml") -Force

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace("f", $manifest.DocumentElement.NamespaceURI)
$identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaceManager)
$application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespaceManager)
$frameworkDependency = $manifest.SelectSingleNode(
    "/f:Package/f:Dependencies/f:PackageDependency[@Name='Microsoft.WindowsAppRuntime.2']",
    $namespaceManager)

$failures = [System.Collections.Generic.List[string]]::new()
function Add-AuditFailure {
    param([Parameter(Mandatory)][string]$Message)
    $failures.Add($Message)
}

if ($null -eq $identity) {
    Add-AuditFailure "The package identity is missing."
}
else {
    if ($identity.Name -ne "D1FC332A.DeskBoxWidgets") {
        Add-AuditFailure "Unexpected package identity name '$($identity.Name)'."
    }
    if ($identity.Publisher -ne "CN=3B75AA4A-2433-4F71-9CC1-B644B26F474A") {
        Add-AuditFailure "Unexpected package publisher '$($identity.Publisher)'."
    }
    if ($identity.ProcessorArchitecture -ine $platformSegment) {
        Add-AuditFailure "Package architecture '$($identity.ProcessorArchitecture)' does not match '$platformSegment'."
    }
}
if ($null -eq $application -or $application.Executable -ne "DeskBox.exe") {
    Add-AuditFailure "The package application does not target DeskBox.exe."
}
if ($null -eq $frameworkDependency -or
    [version]$frameworkDependency.MinVersion -lt [version]"2.4.0.0") {
    Add-AuditFailure "The Microsoft.WindowsAppRuntime.2 framework dependency is missing or too old."
}

$requiredFiles = @(
    "DeskBox.exe",
    "DeskBox.ThumbnailProxy.exe",
    "deskbox_native.dll",
    "EverythingSdk.dll",
    "ThirdParty/Everything/LICENSE.txt",
    "resources.pri",
    "Assets/Store/StoreLogo.png",
    "Assets/Store/Square44x44Logo.png",
    "Assets/Store/Square150x150Logo.png",
    "Assets/Store/Wide310x150Logo.png",
    "Assets/Store/SplashScreen.png"
)
$missingRequiredFiles = @($requiredFiles | Where-Object { $fileEntries -notcontains $_ })
foreach ($missingFile in $missingRequiredFiles) {
    Add-AuditFailure "Required Store payload is missing: '$missingFile'."
}

$forbiddenPatterns = @(
    '(^|/)DeskBox\.dll$',
    '(^|/)DeskBox\.deps\.json$',
    '(^|/)DeskBox\.runtimeconfig\.json$',
    '(^|/)DeskBox\.Abstractions\.dll$',
    '(^|/)DeskBox\.Updater(?:\.|/|$)',
    '(^|/)deskbox_search_core(?:\.|$)',
    '\.pdb$',
    '(^|/)(?:coreclr|clrjit|hostfxr|hostpolicy)\.dll$',
    'donation-wechat',
    'wechat-qrcode',
    'Assets/Support/',
    'store-assets-html'
)
$forbiddenFiles = @(
    @(
        foreach ($fileEntry in $fileEntries) {
            foreach ($pattern in $forbiddenPatterns) {
                if ($fileEntry -match $pattern) {
                    $fileEntry
                    break
                }
            }
        }
    ) | Sort-Object -Unique
)
foreach ($forbiddenFile in $forbiddenFiles) {
    Add-AuditFailure "Forbidden Store AOT payload is present: '$forbiddenFile'."
}

$deskBoxExePath = Join-Path $layoutDirectory "DeskBox.exe"
$thumbnailProxyPath = Join-Path $layoutDirectory "DeskBox.ThumbnailProxy.exe"
$nativeDllPath = Join-Path $layoutDirectory "deskbox_native.dll"
$everythingSdkPath = Join-Path $layoutDirectory "EverythingSdk.dll"
$deskBoxPe = $null
$thumbnailProxyPe = $null
$nativeContract = $null
if (Test-Path -LiteralPath $deskBoxExePath -PathType Leaf) {
    $deskBoxPe = Get-PeFacts -Path $deskBoxExePath
    if ($deskBoxPe.Machine -ne $expectedMachine) {
        Add-AuditFailure "DeskBox.exe machine '$($deskBoxPe.MachineHex)' does not match $ExpectedPlatform."
    }
    if ($deskBoxPe.HasClrHeader) {
        Add-AuditFailure "DeskBox.exe still contains a CLR header instead of a Native AOT image."
    }
    if ($deskBoxPe.Length -lt 10MB) {
        Add-AuditFailure "DeskBox.exe is unexpectedly small for the current Native AOT product image."
    }
}

if (Test-Path -LiteralPath $thumbnailProxyPath -PathType Leaf) {
    $thumbnailProxyPe = Get-PeFacts -Path $thumbnailProxyPath
    if ($thumbnailProxyPe.Machine -ne $expectedMachine) {
        Add-AuditFailure "DeskBox.ThumbnailProxy.exe machine '$($thumbnailProxyPe.MachineHex)' does not match $ExpectedPlatform."
    }
    if ($thumbnailProxyPe.HasClrHeader) {
        Add-AuditFailure "DeskBox.ThumbnailProxy.exe unexpectedly contains a CLR header."
    }
}

if (Test-Path -LiteralPath $everythingSdkPath -PathType Leaf) {
    $everythingSdkPe = Get-PeFacts -Path $everythingSdkPath
    if ($everythingSdkPe.Machine -ne $expectedMachine) {
        Add-AuditFailure "EverythingSdk.dll machine '$($everythingSdkPe.MachineHex)' does not match $ExpectedPlatform."
    }
}
else {
    Add-AuditFailure "EverythingSdk.dll is missing from the Store layout."
}

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
if (Test-Path -LiteralPath $nativeDllPath -PathType Leaf) {
    . (Join-Path $PSScriptRoot "native-pe-contract.ps1")
    $nativeContract = Get-DeskBoxNativePeContract `
        -Path $nativeDllPath `
        -ExpectedPlatform $ExpectedPlatform `
        -RequiredExports $nativeExports
    $vcRuntimeImports = @(
        $nativeContract.ImportedModules |
            Where-Object { $_ -match '^(?:VCRUNTIME|MSVCP|ucrtbase)' }
    )
    if ($vcRuntimeImports.Count -gt 0) {
        Add-AuditFailure "The static Rust Store module imports VC runtime DLLs: $($vcRuntimeImports -join ', ')."
    }
}

$publishHashMatch = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedPublishDirectory)) {
    $resolvedPublishDirectory = [System.IO.Path]::GetFullPath($ExpectedPublishDirectory)
    $publishedExe = Join-Path $resolvedPublishDirectory "DeskBox.exe"
    $publishedThumbnailProxy = Join-Path $resolvedPublishDirectory "DeskBox.ThumbnailProxy.exe"
    $publishedNative = Join-Path $resolvedPublishDirectory "deskbox_native.dll"
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publishedThumbnailProxy -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publishedNative -PathType Leaf) -or
        -not (Test-Path -LiteralPath $deskBoxExePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $thumbnailProxyPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf)) {
        Add-AuditFailure "The expected Store AOT publish directory is incomplete: '$resolvedPublishDirectory'."
        $publishHashMatch = $false
    }
    else {
        $publishHashMatch =
            (Get-FileSha256 -Path $publishedExe) -eq (Get-FileSha256 -Path $deskBoxExePath) -and
            (Get-FileSha256 -Path $publishedThumbnailProxy) -eq (Get-FileSha256 -Path $thumbnailProxyPath) -and
            (Get-FileSha256 -Path $publishedNative) -eq (Get-FileSha256 -Path $nativeDllPath)
        if (-not $publishHashMatch) {
            Add-AuditFailure "The Store package executable, thumbnail proxy, or Rust module differs from the audited publish output."
        }
    }
}

$symbolEntries = @()
if (-not [string]::IsNullOrWhiteSpace($AppxSymPath)) {
    $resolvedAppxSym = [System.IO.Path]::GetFullPath($AppxSymPath)
    if (-not (Test-Path -LiteralPath $resolvedAppxSym -PathType Leaf)) {
        Add-AuditFailure "The appxsym package does not exist: '$resolvedAppxSym'."
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedAppxSym)
        try {
            $symbolEntries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        }
        finally {
            $archive.Dispose()
        }
        foreach ($requiredSymbol in @(
                "DeskBox.pdb",
                "DeskBox.ThumbnailProxy.pdb",
                "deskbox_native.pdb")) {
            if ($symbolEntries -notcontains $requiredSymbol) {
                Add-AuditFailure "Required Native AOT symbol is missing from appxsym: '$requiredSymbol'."
            }
        }
    }
}

$summary = [ordered]@{
    schemaVersion = 1
    status = if ($failures.Count -eq 0) { "passed" } else { "failed" }
    platform = $ExpectedPlatform
    runtimeIdentifier = if ($ExpectedPlatform -eq "ARM64") { "win-arm64" } else { "win-x64" }
    msixPath = $resolvedMsix
    msixBytes = (Get-Item -LiteralPath $resolvedMsix).Length
    msixSha256 = Get-FileSha256 -Path $resolvedMsix
    makeAppxPath = $makeAppx
    fileCount = $fileEntries.Count
    identity = if ($null -eq $identity) { $null } else {
        [ordered]@{
            name = $identity.Name
            publisher = $identity.Publisher
            version = $identity.Version
            processorArchitecture = $identity.ProcessorArchitecture
        }
    }
    windowsAppRuntime = if ($null -eq $frameworkDependency) { $null } else {
        [ordered]@{
            name = $frameworkDependency.Name
            minimumVersion = $frameworkDependency.MinVersion
            publisher = $frameworkDependency.Publisher
        }
    }
    nativeAotExecutable = $deskBoxPe
    thumbnailProxy = $thumbnailProxyPe
    rustNative = $nativeContract
    publishPayloadHashesMatch = $publishHashMatch
    requiredFiles = $requiredFiles
    missingRequiredFiles = $missingRequiredFiles
    forbiddenFiles = $forbiddenFiles
    symbolEntries = $symbolEntries
    failures = @($failures)
    signingAndWackExecuted = $false
    installationExecuted = $false
    storeFlightExecuted = $false
}
$summaryPath = Join-Path $OutputDirectory "summary.json"
[System.IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

if ($failures.Count -gt 0) {
    throw "Store Native AOT package audit failed: $($failures -join ' ') See '$summaryPath'."
}

[pscustomobject]@{
    Status = "passed"
    Summary = $summaryPath
    Msix = $resolvedMsix
    Platform = $ExpectedPlatform
    Files = $fileEntries.Count
}
