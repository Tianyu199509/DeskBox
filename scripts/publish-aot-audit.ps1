[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [switch]$RequireCleanAnalysis,

    [string]$DotNetPath
)

$ErrorActionPreference = "Stop"

if ($Platform -ne "x64") {
    throw "This audit currently supports only x64 as the runtime gate. Use publish-arm64-aot-static-audit.ps1 for the stage 7A ARM64 static gate."
}

$auditStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$auditProfileVersion = 59

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$updaterProject = Join-Path $repoRoot "src\DeskBox.Updater\DeskBox.Updater.csproj"
$msvcEnvironmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
if (-not (Test-Path -LiteralPath $msvcEnvironmentScript -PathType Leaf)) {
    throw "The explicit MSVC environment helper is missing: '$msvcEnvironmentScript'."
}
. $msvcEnvironmentScript
$msvcToolchain = Get-DeskBoxMsvcEnvironment -Platform x64
$dotnet = if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $resolvedDotNet = [System.IO.Path]::GetFullPath($DotNetPath)
    if (-not (Test-Path -LiteralPath $resolvedDotNet -PathType Leaf)) {
        throw "The explicitly selected dotnet host does not exist: '$resolvedDotNet'."
    }

    $resolvedDotNet
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$runtimeIdentifier = "win-x64"
$expectedMachine = 0x8664
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts\aot-audit"))
$runRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot $runtimeIdentifier))
$buildArtifactsDir = Join-Path $runRoot "build"
$publishDir = Join-Path $runRoot "publish"
$symbolsDir = Join-Path $runRoot "symbols"
$rustIntermediateDir = Join-Path $runRoot "rust-staging"
$rustCargoTargetDir = Join-Path $runRoot "rust-target"
$qjsIntermediateDir = Join-Path $runRoot "qjs-staging"
$qjsObjDir = Join-Path $runRoot "qjs-obj"
$logPath = Join-Path $runRoot "publish.log"
$summaryPath = Join-Path $runRoot "summary.json"
$rustNativeEnabled = $true
$qjsNativeEnabled = $true
$jsonSerializerIsReflectionEnabledByDefault = $false

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    $requiredPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $normalizedCandidate.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify an AOT audit path outside '$normalizedRoot': '$normalizedCandidate'"
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

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
            throw "'$Path' does not contain a valid PE signature."
        }

        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-TextSha256 {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        $hash = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-WorkingTreeSnapshot {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 can promote benign native stderr (for example
        # Git's LF/CRLF notice) to a terminating NativeCommandError when the
        # script-wide preference is Stop. Capture exit codes explicitly.
        $ErrorActionPreference = "Continue"
        $gitCommitOutput = @(& git -C $repoRoot rev-parse HEAD 2>$null)
        $gitCommitExitCode = $LASTEXITCODE
        $gitStatusEntries = @(& git -C $repoRoot -c core.quotepath=false status --porcelain=v1 --untracked-files=all 2>$null)
        $gitStatusExitCode = $LASTEXITCODE
        $trackedDiff = @(& git -C $repoRoot diff --binary --no-ext-diff HEAD -- 2>$null) -join "`n"
        $gitDiffExitCode = $LASTEXITCODE
        $untrackedFiles = @(& git -C $repoRoot -c core.quotepath=false ls-files --others --exclude-standard 2>$null | Sort-Object)
        $gitUntrackedExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($gitStatusExitCode -ne 0 -or $gitDiffExitCode -ne 0 -or $gitUntrackedExitCode -ne 0) {
        throw "Failed to capture the Git working-tree state for the AOT audit."
    }

    $gitCommit = if ($gitCommitExitCode -eq 0) {
        ($gitCommitOutput -join "").Trim()
    }
    else {
        $null
    }

    $gitDirty = $gitStatusEntries.Count -gt 0
    $untrackedManifest = @(
        foreach ($relativePath in $untrackedFiles) {
            $fullPath = Join-Path $repoRoot $relativePath
            if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
                "$relativePath`t$((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash)"
            }
        }
    ) -join "`n"

    [PSCustomObject]@{
        GitCommit = $gitCommit
        GitDirty = $gitDirty
        GitStatusEntries = $gitStatusEntries
        WorkingTreeFingerprint = Get-TextSha256 -Value (
            $trackedDiff + "`n--UNTRACKED--`n" + $untrackedManifest)
    }
}

function Get-DumpBinPath {
    param(
        [string]$PreferredPath
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and
        (Test-Path -LiteralPath $PreferredPath -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($PreferredPath)
    }

    $dumpBinCommand = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($null -ne $dumpBinCommand) {
        return $dumpBinCommand.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vsWhere -PathType Leaf) {
            $vsWhereArguments = @(
                "-latest",
                "-products", "*",
                "-find", "VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe"
            )
            $candidates = @(& $vsWhere @vsWhereArguments 2>$null)
            foreach ($candidate in $candidates) {
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return [System.IO.Path]::GetFullPath($candidate)
                }
            }
        }
    }

    throw "Unable to locate dumpbin.exe for the native dependency inventory."
}

function Get-PeImports {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$DumpBinPath
    )

    $dumpOutput = @(& $DumpBinPath /nologo /dependents $Path 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin failed while reading imports from '$Path'."
    }

    $imports = @(
        foreach ($line in $dumpOutput) {
            $match = [regex]::Match(
                [string]$line,
                "^\s*([A-Za-z0-9_.-]+\.dll)\s*$",
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                $match.Groups[1].Value.ToLowerInvariant()
            }
        }
    ) | Sort-Object -Unique
    # A single unique import (the statically linked QuickJS module imports only
    # KERNEL32.dll) comes back as a scalar string; force an array so the Count
    # check holds under Set-StrictMode.
    $imports = @($imports)

    if ($imports.Count -eq 0) {
        throw "No PE imports were found for '$Path'."
    }

    return $imports
}

Assert-PathInsideRoot -Root $artifactRoot -Candidate $runRoot
$sourceSnapshotBefore = Get-WorkingTreeSnapshot
$dumpBinPath = Get-DumpBinPath -PreferredPath (
    Join-Path $msvcToolchain.LinkerDirectory "dumpbin.exe")

if (Test-Path -LiteralPath $runRoot) {
    Remove-Item -LiteralPath $runRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $symbolsDir -Force | Out-Null

$publishArguments = @(
    "publish",
    $project,
    "--configuration", "Release",
    "--output", $publishDir,
    "--artifacts-path", $buildArtifactsDir,
    "--no-restore",
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:DeskBoxDistribution=Direct",
    "-p:DeskBoxAotAudit=true",
    "-p:DeskBoxAotSmokeHarness=true",
    "-p:JsonSerializerIsReflectionEnabledByDefault=$($jsonSerializerIsReflectionEnabledByDefault.ToString().ToLowerInvariant())",
    "-p:DeskBoxRustNative=$($rustNativeEnabled.ToString().ToLowerInvariant())",
    "-p:DeskBoxRustNativeIntermediateDir=$rustIntermediateDir",
    "-p:DeskBoxRustNativeCargoTargetDir=$rustCargoTargetDir",
    "-p:DeskBoxQjsNative=$($qjsNativeEnabled.ToString().ToLowerInvariant())",
    "-p:DeskBoxQjsIntermediateDir=$qjsIntermediateDir",
    "-p:DeskBoxQjsObjDir=$qjsObjDir",
    "-p:IlcUseEnvironmentalTools=true",
    "-p:SelfContained=true",
    "-p:WindowsAppSDKSelfContained=false",
    "-p:PublishSingleFile=false",
    "-v:minimal"
)

$previousCliLanguage = [Environment]::GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "Process")
$previousNoLogo = [Environment]::GetEnvironmentVariable("DOTNET_NOLOGO", "Process")
$msvcEnvironmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $msvcToolchain
try {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US", "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", "1", "Process")

    foreach ($restoreProject in @($project, $updaterProject)) {
        $restoreArguments = @(
            "restore",
            $restoreProject,
            "--artifacts-path", $buildArtifactsDir,
            "-p:Platform=$Platform",
            "-p:RuntimeIdentifier=$runtimeIdentifier",
            "-p:DeskBoxAotAudit=true",
            "-p:DeskBoxAotSmokeHarness=true",
            "-p:JsonSerializerIsReflectionEnabledByDefault=$($jsonSerializerIsReflectionEnabledByDefault.ToString().ToLowerInvariant())",
            "-p:PublishAot=true",
            "-p:IlcUseEnvironmentalTools=true",
            "-p:SelfContained=true",
            "-p:WindowsAppSDKSelfContained=false",
            "-v:minimal"
        )

        & $dotnet @restoreArguments 2>&1 | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) {
            throw "Restore failed for '$restoreProject' with exit code $LASTEXITCODE. See '$logPath'."
        }
    }

    & $dotnet @publishArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    $publishExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", $previousCliLanguage, "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", $previousNoLogo, "Process")
    Exit-DeskBoxMsvcEnvironment -State $msvcEnvironmentState
}

if ($publishExitCode -ne 0) {
    throw "Native AOT publish failed with exit code $publishExitCode. See '$logPath'."
}

$rustAbiVersion = $null
$rustCapabilities = $null
$rustRequiredExports = @()
$rustStagingSha256 = $null
$rustPublishSha256 = $null
$rustPublishMatchesStaging = $null
if ($rustNativeEnabled) {
    $rustBuildScript = Join-Path $repoRoot "scripts\build-rust-native.ps1"
    $rustValidation = & $rustBuildScript `
        -Platform x64 `
        -Configuration Release `
        -OutputDirectory $publishDir `
        -ValidateOnly
    $rustAbiVersion = $rustValidation.AbiVersion
    $rustCapabilities = $rustValidation.Capabilities
    $rustRequiredExports = @($rustValidation.RequiredExports)

    $stagedRustDll = Join-Path $rustIntermediateDir "deskbox_native.dll"
    $publishedRustDll = Join-Path $publishDir "deskbox_native.dll"
    if (-not (Test-Path -LiteralPath $stagedRustDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publishedRustDll -PathType Leaf)) {
        throw "The isolated staging or published Rust native module is missing."
    }

    $rustStagingSha256 = (Get-FileHash -LiteralPath $stagedRustDll -Algorithm SHA256).Hash
    $rustPublishSha256 = (Get-FileHash -LiteralPath $publishedRustDll -Algorithm SHA256).Hash
    $rustPublishMatchesStaging = [string]::Equals(
        $rustStagingSha256,
        $rustPublishSha256,
        [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $rustPublishMatchesStaging) {
        throw "The published Rust native module does not match this audit run's isolated staging output."
    }
}

$qjsAbiVersion = $null
$qjsRequiredExports = @()
$qjsStagingSha256 = $null
$qjsPublishSha256 = $null
$qjsPublishMatchesStaging = $null
if ($qjsNativeEnabled) {
    $qjsBuildScript = Join-Path $repoRoot "scripts\build-qjs-native.ps1"
    $qjsValidation = & $qjsBuildScript `
        -Platform x64 `
        -Configuration Release `
        -CrtLinkage Static `
        -OutputDirectory $publishDir `
        -ValidateOnly
    $qjsAbiVersion = $qjsValidation.AbiVersion
    $qjsRequiredExports = @($qjsValidation.RequiredExports)

    $stagedQjsDll = Join-Path $qjsIntermediateDir "deskbox_qjs.dll"
    $publishedQjsDll = Join-Path $publishDir "deskbox_qjs.dll"
    if (-not (Test-Path -LiteralPath $stagedQjsDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publishedQjsDll -PathType Leaf)) {
        throw "The isolated staging or published QuickJS native module is missing."
    }

    $qjsStagingSha256 = (Get-FileHash -LiteralPath $stagedQjsDll -Algorithm SHA256).Hash
    $qjsPublishSha256 = (Get-FileHash -LiteralPath $publishedQjsDll -Algorithm SHA256).Hash
    $qjsPublishMatchesStaging = [string]::Equals(
        $qjsStagingSha256,
        $qjsPublishSha256,
        [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $qjsPublishMatchesStaging) {
        throw "The published QuickJS native module does not match this audit run's isolated staging output."
    }
}

$pdbFiles = @(Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -Recurse)
foreach ($pdb in $pdbFiles) {
    Assert-PathInsideRoot -Root $publishDir -Candidate $pdb.FullName
    $normalizedPublishDir = [System.IO.Path]::GetFullPath($publishDir).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedPdbPath = [System.IO.Path]::GetFullPath($pdb.FullName)
    $relativePath = $normalizedPdbPath.Substring($normalizedPublishDir.Length + 1)
    $symbolDestination = Join-Path $symbolsDir $relativePath
    $symbolParent = Split-Path -Parent $symbolDestination
    New-Item -ItemType Directory -Path $symbolParent -Force | Out-Null
    Move-Item -LiteralPath $pdb.FullName -Destination $symbolDestination -Force
}

$requiredFiles = @(
    "DeskBox.exe",
    "DeskBox.Updater.exe",
    "DeskBox.ThumbnailProxy.exe",
    "DeskBox.pri"
)
if ($rustNativeEnabled) {
    $requiredFiles += "deskbox_native.dll"
}
if ($qjsNativeEnabled) {
    $requiredFiles += "deskbox_qjs.dll"
}

foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $publishDir $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "AOT audit output is missing '$requiredFile'."
    }
}

$publishedNativeModules = @(
    Get-ChildItem -LiteralPath $publishDir -Filter "deskbox_native.dll" -File -Recurse
)
if ($rustNativeEnabled) {
    $expectedRustDllPath = [System.IO.Path]::GetFullPath(
        (Join-Path $publishDir "deskbox_native.dll"))
    if ($publishedNativeModules.Count -ne 1 -or
        -not [string]::Equals(
            $publishedNativeModules[0].FullName,
            $expectedRustDllPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The x64 AOT publish must contain exactly one root-level deskbox_native.dll."
    }
}
elseif ($publishedNativeModules.Count -ne 0) {
    throw "A non-x64 AOT publish must not contain the x64 deskbox_native.dll."
}

$publishedThumbnailProxies = @(
    Get-ChildItem -LiteralPath $publishDir -Filter "DeskBox.ThumbnailProxy.exe" -File -Recurse
)
$expectedThumbnailProxyPath = [System.IO.Path]::GetFullPath(
    (Join-Path $publishDir "DeskBox.ThumbnailProxy.exe"))
if ($publishedThumbnailProxies.Count -ne 1 -or
    -not [string]::Equals(
        $publishedThumbnailProxies[0].FullName,
        $expectedThumbnailProxyPath,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The AOT publish must contain exactly one root-level DeskBox.ThumbnailProxy.exe."
}

$publishedQjsModules = @(
    Get-ChildItem -LiteralPath $publishDir -Filter "deskbox_qjs.dll" -File -Recurse
)
if ($qjsNativeEnabled) {
    $expectedQjsDllPath = [System.IO.Path]::GetFullPath(
        (Join-Path $publishDir "deskbox_qjs.dll"))
    if ($publishedQjsModules.Count -ne 1 -or
        -not [string]::Equals(
            $publishedQjsModules[0].FullName,
            $expectedQjsDllPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The AOT publish must contain exactly one root-level deskbox_qjs.dll."
    }
}

$forbiddenFiles = @(
    "coreclr.dll",
    "clrjit.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll",
    "DeskBox.dll",
    "DeskBox.deps.json",
    "DeskBox.runtimeconfig.json",
    "DeskBox.Updater.dll",
    "DeskBox.Updater.deps.json",
    "DeskBox.Updater.runtimeconfig.json"
)

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
$forbiddenMatches = @($publishedFiles | Where-Object { $_.Name -in $forbiddenFiles })
if ($forbiddenMatches.Count -gt 0) {
    $details = $forbiddenMatches.FullName -join [Environment]::NewLine
    throw "AOT audit output still contains managed runtime or application files:`n$details"
}

$publishedPdbFiles = @($publishedFiles | Where-Object Extension -eq ".pdb")
if ($publishedPdbFiles.Count -gt 0) {
    throw "AOT publish directory still contains PDB files after symbol separation."
}

$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsDir -Filter "*.pdb" -File -Recurse)
$requiredSymbolFiles = @(
    "DeskBox.pdb",
    "DeskBox.Updater.pdb",
    "DeskBox.ThumbnailProxy.pdb"
)
if ($rustNativeEnabled) {
    $requiredSymbolFiles += "deskbox_native.pdb"
}
if ($qjsNativeEnabled) {
    $requiredSymbolFiles += "deskbox_qjs.pdb"
}

foreach ($requiredSymbolFile in $requiredSymbolFiles) {
    if (-not ($symbolFiles | Where-Object Name -eq $requiredSymbolFile)) {
        throw "AOT audit symbols are missing '$requiredSymbolFile'."
    }
}

$peFiles = @(
    (Join-Path $publishDir "DeskBox.exe"),
    (Join-Path $publishDir "DeskBox.Updater.exe"),
    (Join-Path $publishDir "DeskBox.ThumbnailProxy.exe")
)
if ($rustNativeEnabled) {
    $peFiles += (Join-Path $publishDir "deskbox_native.dll")
}
if ($qjsNativeEnabled) {
    $peFiles += (Join-Path $publishDir "deskbox_qjs.dll")
}

$peResults = foreach ($peFile in $peFiles) {
    $machine = Get-PeMachine -Path $peFile
    if ($machine -ne $expectedMachine) {
        throw "Unexpected PE machine 0x$($machine.ToString('X4')) for '$peFile'; expected 0x$($expectedMachine.ToString('X4'))."
    }

    $imports = @(Get-PeImports -Path $peFile -DumpBinPath $dumpBinPath)

    [ordered]@{
        file = [System.IO.Path]::GetFileName($peFile)
        machine = "0x$($machine.ToString('X4'))"
        bytes = (Get-Item -LiteralPath $peFile).Length
        sha256 = (Get-FileHash -LiteralPath $peFile -Algorithm SHA256).Hash
        imports = @($imports)
    }
}

$logLines = @(Get-Content -LiteralPath $logPath)
$warningCodeRegex = [regex]::new(
    "\b(?:IL|CS|MSB|WMC|MVVMTK|CsWinRT|NETSDK|SYSLIB)\d+\b",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$warningMatches = @(
    foreach ($line in $logLines) {
        foreach ($match in $warningCodeRegex.Matches($line)) {
            $match.Value
        }
    }
)
$warningCodes = @($warningMatches | Sort-Object -Unique)
$allowedWarningCodes = @(
    "CS0108",
    "CS0169",
    "CS0414",
    "CS8601",
    "CS8602",
    "WMC1510"
)
$unexpectedWarningCodes = @(
    $warningCodes | Where-Object { $allowedWarningCodes -notcontains $_ }
)
$warningCodeCounts = [ordered]@{}
foreach ($group in @($warningMatches | Group-Object | Sort-Object Name)) {
    $warningCodeCounts[$group.Name] = $group.Count
}
$targetedWarningCounts = [ordered]@{
    MVVMTK0045 = @($warningMatches | Where-Object { $_ -ieq "MVVMTK0045" }).Count
    CsWinRT1028 = @($warningMatches | Where-Object { $_ -ieq "CsWinRT1028" }).Count
}
$maximumWmc1510Count = 763
$actualWmc1510Count = @($warningMatches | Where-Object { $_ -ieq "WMC1510" }).Count
$alwaysThrowMessages = @(
    $logLines |
        Where-Object { $_ -match "will always throw" } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$shortcutAlwaysThrowMessages = @(
    $alwaysThrowMessages |
        Where-Object {
            $_ -match "ShortcutHelper\+ShellLink" -or
            $_ -match "DragDropPermissionService\+ShellLink"
        }
)
$musicVolumeAlwaysThrowMessages = @(
    $alwaysThrowMessages |
        Where-Object { $_ -match "MusicVolumeService" }
)
$explorerShellAlwaysThrowMessages = @(
    $alwaysThrowMessages |
        Where-Object { $_ -match "ExplorerShellLaunch" }
)
$quickAccessAlwaysThrowMessages = @(
    $alwaysThrowMessages |
        Where-Object { $_ -match "ExplorerQuickAccess|QuickAccessNative" }
)
$expectedRemainingAlwaysThrowTypes = @()
$missingExpectedAlwaysThrowTypes = @(
    foreach ($typeName in $expectedRemainingAlwaysThrowTypes) {
        if (-not ($alwaysThrowMessages | Where-Object { $_.Contains($typeName) })) {
            $typeName
        }
    }
)
$unexpectedAlwaysThrowMessages = @(
    foreach ($message in $alwaysThrowMessages) {
        $matchesExpectedType = $false
        foreach ($typeName in $expectedRemainingAlwaysThrowTypes) {
            if ($message.Contains($typeName)) {
                $matchesExpectedType = $true
                break
            }
        }

        if (-not $matchesExpectedType) {
            $message
        }
    }
)

$sourceSnapshotAfter = Get-WorkingTreeSnapshot
$workingTreeFingerprintBefore = $sourceSnapshotBefore.WorkingTreeFingerprint
$workingTreeFingerprintAfter = $sourceSnapshotAfter.WorkingTreeFingerprint
$sourceStableDuringAudit =
    [string]::Equals(
        $sourceSnapshotBefore.GitCommit,
        $sourceSnapshotAfter.GitCommit,
        [System.StringComparison]::Ordinal) -and
    [string]::Equals(
        $workingTreeFingerprintBefore,
        $workingTreeFingerprintAfter,
        [System.StringComparison]::Ordinal)

$dotnetSdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Failed to query the selected dotnet SDK version from '$dotnet'."
}

$rustcVersion = $null
$cargoVersion = $null
$rustLockedPackages = @()
if ($rustNativeEnabled) {
    Push-Location $repoRoot
    try {
        $rustcVersion = (& rustc --version).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to query the repository Rust compiler version."
        }

        $cargoVersion = (& cargo --version).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to query the repository Cargo version."
        }

        $cargoMetadataLines = @(& cargo metadata `
                --manifest-path (Join-Path $repoRoot "native\Cargo.toml") `
                --locked `
                --format-version 1)
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to query the locked Rust dependency graph."
        }

        $cargoMetadata = ($cargoMetadataLines -join "`n") | ConvertFrom-Json
        $resolvedFeaturesById = @{}
        foreach ($node in $cargoMetadata.resolve.nodes) {
            $resolvedFeaturesById[[string]$node.id] = @($node.features | Sort-Object)
        }

        $rustLockedPackages = @(
            $cargoMetadata.packages |
                Sort-Object name, version |
                ForEach-Object {
                    [ordered]@{
                        name = $_.name
                        version = $_.version
                        source = $_.source
                        features = @($resolvedFeaturesById[[string]$_.id])
                    }
                }
        )
    }
    finally {
        Pop-Location
    }
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsDir -File -Recurse)
$nativeDependencies = @(
    $peResults |
        ForEach-Object { $_.imports } |
        Sort-Object -Unique
)
$auditStopwatch.Stop()
$summary = [ordered]@{
    schemaVersion = 55
    auditProfileVersion = $auditProfileVersion
    productProfile = "smoke-audit"
    smokeHarnessEnabled = $true
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    durationMilliseconds = $auditStopwatch.ElapsedMilliseconds
    gitCommit = $sourceSnapshotBefore.GitCommit
    gitDirty = $sourceSnapshotBefore.GitDirty
    workingTreeFingerprint = $workingTreeFingerprintBefore
    workingTreeFingerprintBefore = $workingTreeFingerprintBefore
    workingTreeFingerprintAfter = $workingTreeFingerprintAfter
    sourceStableDuringAudit = $sourceStableDuringAudit
    gitStatusEntries = $sourceSnapshotBefore.GitStatusEntries
    gitStatusEntriesAfter = $sourceSnapshotAfter.GitStatusEntries
    dotnetHost = $dotnet
    dotnetSdkVersion = $dotnetSdkVersion
    configuration = "Release"
    platform = $Platform
    runtimeIdentifier = $runtimeIdentifier
    jsonSerializer = [ordered]@{
        reflectionEnabledByDefault = $jsonSerializerIsReflectionEnabledByDefault
    }
    buildArtifactsDirectory = $buildArtifactsDir
    rustIntermediateDirectory = $rustIntermediateDir
    rustCargoTargetDirectory = $rustCargoTargetDir
    qjsIntermediateDirectory = $qjsIntermediateDir
    qjsObjDirectory = $qjsObjDir
    publishDirectory = $publishDir
    symbolsDirectory = $symbolsDir
    publishFileCount = $publishedFiles.Count
    publishBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
    symbolFileCount = $symbolFiles.Count
    symbolBytes = ($symbolFiles | Measure-Object -Property Length -Sum).Sum
    warningCodes = $warningCodes
    warningCodeCounts = $warningCodeCounts
    targetedWarningCounts = $targetedWarningCounts
    alwaysThrowMessages = $alwaysThrowMessages
    analysisContract = [ordered]@{
        allowedWarningCodes = $allowedWarningCodes
        unexpectedWarningCodes = $unexpectedWarningCodes
        maximumWmc1510Count = $maximumWmc1510Count
        actualWmc1510Count = $actualWmc1510Count
        shortcutAlwaysThrowMessages = $shortcutAlwaysThrowMessages
        musicVolumeAlwaysThrowMessages = $musicVolumeAlwaysThrowMessages
        explorerShellAlwaysThrowMessages = $explorerShellAlwaysThrowMessages
        quickAccessAlwaysThrowMessages = $quickAccessAlwaysThrowMessages
        expectedRemainingAlwaysThrowTypes = $expectedRemainingAlwaysThrowTypes
        missingExpectedAlwaysThrowTypes = $missingExpectedAlwaysThrowTypes
        unexpectedAlwaysThrowMessages = $unexpectedAlwaysThrowMessages
    }
    peImportTool = $dumpBinPath
    nativeDependencies = $nativeDependencies
    peImages = @($peResults)
    rustNative = [ordered]@{
        enabled = $rustNativeEnabled
        abiVersion = $rustAbiVersion
        capabilities = $rustCapabilities
        requiredExports = @($rustRequiredExports)
        dllName = if ($rustNativeEnabled) { "deskbox_native.dll" } else { $null }
        stagingSha256 = $rustStagingSha256
        publishSha256 = $rustPublishSha256
        publishMatchesStaging = $rustPublishMatchesStaging
        rustcVersion = $rustcVersion
        cargoVersion = $cargoVersion
        lockedPackageCount = $rustLockedPackages.Count
        lockedPackages = @($rustLockedPackages)
        shortcutBackendPolicy = [ordered]@{
            jitDefault = "rust"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
        }
        musicVolumeBackendPolicy = [ordered]@{
            jitDefault = "rust"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
        }
        explorerShellBackendPolicy = [ordered]@{
            jitDefault = "rust"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            productFallback = "Process.Start/SHOpenWithDialog"
        }
        quickAccessBackendPolicy = [ordered]@{
            jitDefault = "rust"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            asynchronousApartment = "dedicated STA"
        }
        recycleBinBackendPolicy = [ordered]@{
            productDelete = "C# IFileOperation (FOFX_RECYCLEONDELETE)"
            nativeAotExactQueryAndRestore = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            exactIdentity = "original parent plus item name"
            restoreRequiresExactlyOneMatch = $true
        }
    }
    qjsNative = [ordered]@{
        enabled = $qjsNativeEnabled
        abiVersion = $qjsAbiVersion
        crtLinkage = "Static"
        requiredExports = @($qjsRequiredExports)
        dllName = if ($qjsNativeEnabled) { "deskbox_qjs.dll" } else { $null }
        stagingSha256 = $qjsStagingSha256
        publishSha256 = $qjsPublishSha256
        publishMatchesStaging = $qjsPublishMatchesStaging
    }
}

$summaryJson = $summary | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    $summaryPath,
    $summaryJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

# Analysis-gate failures are collected instead of fail-fast so a single regression
# (for example a CsWinRT1028 red) cannot mask later findings; build, publish, and
# structural failures above still abort immediately because later analysis cannot run.
$auditFailures = [System.Collections.Generic.List[string]]::new()

if (-not $sourceStableDuringAudit) {
    $auditFailures.Add("The repository changed while the AOT audit was running. The output is not a trusted source snapshot; see '$summaryPath'.")
}

if ($warningCodes.Count -gt 0) {
    Write-Warning "AOT analysis warnings remain: $($warningCodes -join ', ')"
}

if ($alwaysThrowMessages.Count -gt 0) {
    Write-Warning "ILC reported $($alwaysThrowMessages.Count) method(s) that will always throw."
}

if ($targetedWarningCounts.MVVMTK0045 -ne 0 -or
    $targetedWarningCounts.CsWinRT1028 -ne 0) {
    $auditFailures.Add("AOT compatibility regression detected in migrated MVVM or CsWinRT ABI declarations. See '$summaryPath'.")
}

if ($actualWmc1510Count -gt $maximumWmc1510Count) {
    $auditFailures.Add("WMC1510 warning count exceeded ratchet: maximum=$maximumWmc1510Count actual=$actualWmc1510Count. See '$summaryPath'.")
}

if ($unexpectedWarningCodes.Count -gt 0) {
    $auditFailures.Add("The Stage 5B-4C3B2B2B AOT warning set expanded: $($unexpectedWarningCodes -join ', '). See '$summaryPath'.")
}

if ($shortcutAlwaysThrowMessages.Count -gt 0) {
    $auditFailures.Add("Legacy shortcut COM constructors remain reachable in Native AOT. See '$summaryPath'.")
}

if ($musicVolumeAlwaysThrowMessages.Count -gt 0) {
    $auditFailures.Add("Legacy music-volume COM constructors remain reachable in Native AOT. See '$summaryPath'.")
}

if ($explorerShellAlwaysThrowMessages.Count -gt 0) {
    $auditFailures.Add("Legacy Explorer-shell dynamic COM remains reachable in Native AOT. See '$summaryPath'.")
}

if ($quickAccessAlwaysThrowMessages.Count -gt 0) {
    $auditFailures.Add("Legacy Quick Access dynamic COM remains reachable in Native AOT. See '$summaryPath'.")
}

if ($missingExpectedAlwaysThrowTypes.Count -gt 0 -or
    $unexpectedAlwaysThrowMessages.Count -gt 0) {
    $auditFailures.Add("The Stage 5B-4A remaining always-throw contract changed. See '$summaryPath'.")
}

if ($RequireCleanAnalysis.IsPresent -and
    ($warningCodes.Count -gt 0 -or $alwaysThrowMessages.Count -gt 0)) {
    $auditFailures.Add("AOT publish passed structural validation, but analysis is not clean. See '$summaryPath'.")
}

# Every analysis gate has now run; report all findings at once and fail once.
if ($auditFailures.Count -gt 0) {
    $summary['auditFindings'] = @($auditFailures)
    $summaryJson = $summary | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText(
        $summaryPath,
        $summaryJson + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    foreach ($auditFailure in $auditFailures) {
        $failureStage = "analysis"
        if ($auditFailure -match '^(?:The )?Stage (?<stage>[0-9][0-9A-Z-]*)') {
            $failureStage = $Matches['stage']
        }

        Write-Host "FAIL [$failureStage] $auditFailure"
    }

    Write-Host "=== AUDIT FAILED: $($auditFailures.Count) findings ==="
    exit 1
}

[PSCustomObject]@{
    PublishDirectory = $publishDir
    SymbolsDirectory = $symbolsDir
    Summary = $summaryPath
    RuntimeIdentifier = $runtimeIdentifier
    PublishFiles = $publishedFiles.Count
    PublishMiB = [Math]::Round($summary.publishBytes / 1MB, 1)
    SymbolFiles = $symbolFiles.Count
    SymbolMiB = [Math]::Round($summary.symbolBytes / 1MB, 1)
    WarningCodes = $warningCodes -join ", "
    AlwaysThrowCount = $alwaysThrowMessages.Count
    RustAbiVersion = $rustAbiVersion
    RustCapabilities = $rustCapabilities
    RustRequiredExports = $rustRequiredExports -join ", "
}
