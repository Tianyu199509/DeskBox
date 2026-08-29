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
$auditProfileVersion = 58

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
$logPath = Join-Path $runRoot "publish.log"
$summaryPath = Join-Path $runRoot "summary.json"
$rustNativeEnabled = $true
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
$stage4D1ATargetFiles = @(
    "Win32Helper.cs",
    "MarkdownDocumentView.cs",
    "SearchPopupWindow.xaml.cs"
)
$stage4D1AWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                @($stage4D1ATargetFiles | Where-Object {
                    $line -match ([regex]::Escape($_) + "\(")
                }).Count -gt 0
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D1BTargetFiles = @(
    "QuickCaptureSurfaceContent.xaml.cs",
    "Localized.cs"
)
$stage4D1BWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                @($stage4D1BTargetFiles | Where-Object {
                    $line -match ([regex]::Escape($_) + "\(")
                }).Count -gt 0
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D2RemovedSourceFiles = @(
    "src\DeskBox\Helpers\FileOperationHelper.cs"
)
$stage4D2UnexpectedExistingSourceFiles = @(
    $stage4D2RemovedSourceFiles |
        Where-Object { Test-Path -LiteralPath (Join-Path $repoRoot $_) }
)
$stage4D2FileOperationWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                ($line -match "FileOperationHelper|IFileOperation|IShellItem")
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D3ASourceFiles = @(
    "src\DeskBox\Helpers\NativeDropTarget.cs",
    "src\DeskBox\Helpers\NativeDropComDataReader.cs"
)
$stage4D3ALegacyRcwPatterns = @(
    "COMIDataObject",
    "Marshal.GetObjectForIUnknown",
    "Marshal.GetIUnknownForObject",
    "Marshal.ReleaseComObject",
    "(IStream)"
)
$stage4D3ALegacyRcwSourceMatches = @(
    foreach ($relativePath in $stage4D3ASourceFiles) {
        $fullPath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            "{0}:<missing>" -f $relativePath
            continue
        }

        $lineNumber = 0
        foreach ($sourceLine in Get-Content -LiteralPath $fullPath) {
            $lineNumber++
            foreach ($pattern in $stage4D3ALegacyRcwPatterns) {
                if ($sourceLine.IndexOf($pattern, [StringComparison]::Ordinal) -ge 0) {
                    "{0}:{1}:{2}" -f $relativePath, $lineNumber, $pattern
                }
            }
        }
    }
)
$stage4D3ADataReaderWarningMessages = @(
    $logLines |
        Where-Object {
            $warningCodeRegex.IsMatch($_) -and
                $_ -match ([regex]::Escape("NativeDropComDataReader.cs") + "\(")
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D3ARemainingDropTargetWarningMessages = @(
    $logLines |
        Where-Object {
            $warningCodeRegex.IsMatch($_) -and
                $_ -match ([regex]::Escape("NativeDropTarget.cs") + "\(")
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D3AUnexpectedDropTargetWarningMessages = @(
    $stage4D3ARemainingDropTargetWarningMessages |
        Where-Object {
            $_ -notmatch "IL2050" -or
                $_ -notmatch "RegisterDragDrop"
        }
)
$stage4D3BSourceFiles = @(
    "src\DeskBox\Helpers\NativeDropTarget.cs",
    "src\DeskBox\Helpers\NativeDropTargetComInterop.cs"
)
$stage4D3BLegacyRegistrationPatterns = @(
    "[ComImport",
    "[ComVisible",
    "interface IDropTarget",
    "RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget)",
    "Marshal.GetIUnknownForObject",
    "Marshal.GetComInterfaceForObject"
)
$stage4D3BLegacyRegistrationSourceMatches = @(
    foreach ($relativePath in $stage4D3BSourceFiles) {
        $fullPath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            "{0}:<missing>" -f $relativePath
            continue
        }

        $lineNumber = 0
        foreach ($sourceLine in Get-Content -LiteralPath $fullPath) {
            $lineNumber++
            foreach ($pattern in $stage4D3BLegacyRegistrationPatterns) {
                if ($sourceLine.IndexOf($pattern, [StringComparison]::Ordinal) -ge 0) {
                    "{0}:{1}:{2}" -f $relativePath, $lineNumber, $pattern
                }
            }
        }
    }
)
$stage4D3BRequiredGeneratedComPatterns = @(
    "[GeneratedComInterface",
    "ComInterfaceOptions.ManagedObjectWrapper",
    "partial interface INativeDropTarget",
    "[GeneratedComClass]",
    "partial class NativeDropTargetComObject : INativeDropTarget",
    "[LibraryImport(`"ole32.dll`")]",
    "RegisterDragDrop(nint hwnd, nint dropTarget)",
    "ComInterfaceMarshaller<INativeDropTarget>.ConvertToUnmanaged",
    "ComInterfaceMarshaller<INativeDropTarget>.Free"
)
$stage4D3BInteropSourcePath = Join-Path $repoRoot (
    "src\DeskBox\Helpers\NativeDropTargetComInterop.cs")
$stage4D3BInteropSource = if (
    Test-Path -LiteralPath $stage4D3BInteropSourcePath -PathType Leaf) {
    Get-Content -LiteralPath $stage4D3BInteropSourcePath -Raw
}
else {
    ""
}
$stage4D3BMissingGeneratedComPatterns = @(
    $stage4D3BRequiredGeneratedComPatterns |
        Where-Object {
            $stage4D3BInteropSource.IndexOf(
                $_,
                [StringComparison]::Ordinal) -lt 0
        }
)
$stage4D3BWarningMessages = @(
    $logLines |
        Where-Object {
            $warningCodeRegex.IsMatch($_) -and
                (
                    $_ -match "NativeDropTarget(?:ComInterop)?\.cs\(" -or
                    $_ -match "INativeDropTarget|NativeDropTargetComObject"
                )
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D3BIl2050WarningMessages = @(
    $logLines |
        Where-Object { $_ -match "\bIL2050\b" } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D4ASourceFiles = @(
    "src\DeskBox\Helpers\ExplorerShellLaunchService.cs",
    "src\DeskBox\Helpers\ExplorerShellLaunchNativeBackend.cs"
)
$stage4D4AWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                (
                    $line -match "ExplorerShellLaunch(?:Service|NativeBackend)\.cs\(" -or
                    $line -match "ExplorerShellLaunchService|ExplorerShellLaunchNativeBackend"
                )
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D4BSourceFiles = @(
    "src\DeskBox\Helpers\ExplorerQuickAccessHelper.cs",
    "src\DeskBox\Helpers\QuickAccessNativeBackend.cs"
)
$stage4D4BWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                (
                    $line -match "(?:ExplorerQuickAccessHelper|QuickAccessNativeBackend)\.cs\(" -or
                    $line -match "ExplorerQuickAccessHelper|QuickAccessNativeBackend"
                )
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4D5SourceFiles = @(
    "src\DeskBox\App.Tray.cs"
)
$stage4D5LegacyReflectionPatterns = @(
    "System.Reflection.BindingFlags",
    "GetProperty(`"ContextMenuFlyout`"",
    "GetProperty(`"TrayIcon`"",
    "GetProperty(`"WindowHandle`"",
    "GetProperty(`"Id`""
)
$stage4D5RequiredPublicPatterns = @(
    "_trayIcon.TrayIcon",
    "trayIcon.WindowHandle",
    "trayIcon.Id",
    "SecondWindowContextMenuOpened +=",
    "VisualTreeHelper.GetParent",
    "VisualTreeHelper.GetOpenPopupsForXamlRoot"
)
$stage4D5SourcePath = Join-Path $repoRoot $stage4D5SourceFiles[0]
$stage4D5Source = Get-Content -LiteralPath $stage4D5SourcePath -Raw
$stage4D5LegacyReflectionSourceMatches = @(
    foreach ($pattern in $stage4D5LegacyReflectionPatterns) {
        if ($stage4D5Source.IndexOf($pattern, [StringComparison]::Ordinal) -ge 0) {
            "$($stage4D5SourceFiles[0])::$pattern"
        }
    }
)
$stage4D5MissingPublicPatterns = @(
    $stage4D5RequiredPublicPatterns |
        Where-Object {
            $stage4D5Source.IndexOf($_, [StringComparison]::Ordinal) -lt 0
        }
)
$stage4D5WarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                (
                    $line -match "App\.Tray\.cs\(" -or
                    $line -match "DeskBox\.App\.(?:TryGetTrayIconIdentity|ApplySecondWindowTrayPresenterSettings|ConfigureOwningPopup)"
                )
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E0SourceFiles = @(
    "src\DeskBox\Controls\WidgetContents\SearchWidgetContent.xaml",
    "src\DeskBox\Controls\WidgetContents\SearchWidgetContent.xaml.cs",
    "src\DeskBox\Models\SearchModels.cs"
)
$stage4E0XamlPath = Join-Path $repoRoot $stage4E0SourceFiles[0]
$stage4E0CodeBehindPath = Join-Path $repoRoot $stage4E0SourceFiles[1]
$stage4E0ModelPath = Join-Path $repoRoot $stage4E0SourceFiles[2]
$stage4E0Xaml = Get-Content -LiteralPath $stage4E0XamlPath -Raw
$stage4E0CodeBehind = Get-Content -LiteralPath $stage4E0CodeBehindPath -Raw
$stage4E0Model = Get-Content -LiteralPath $stage4E0ModelPath -Raw
$stage4E0LegacyOneWayPatterns = @(
    "{x:Bind Query, Mode=OneWay}",
    "{x:Bind DeleteLabel, Mode=OneWay}"
)
$stage4E0LegacyOneWaySourceMatches = @(
    foreach ($pattern in $stage4E0LegacyOneWayPatterns) {
        if ($stage4E0Xaml.IndexOf($pattern, [StringComparison]::Ordinal) -ge 0) {
            "$($stage4E0SourceFiles[0])::$pattern"
        }
    }
)
$stage4E0RequiredOneTimeBindings = @(
    [PSCustomObject]@{
        pattern = "{x:Bind Query, Mode=OneTime}"
        expectedCount = 4
    },
    [PSCustomObject]@{
        pattern = "{x:Bind DeleteLabel, Mode=OneTime}"
        expectedCount = 2
    }
)
$stage4E0MissingOneTimeBindings = @(
    foreach ($binding in $stage4E0RequiredOneTimeBindings) {
        $actualCount = [regex]::Matches(
            $stage4E0Xaml,
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E0RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E0SourceFiles[2]
        source = $stage4E0Model
        pattern = "public required string Query { get; init; }"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E0SourceFiles[2]
        source = $stage4E0Model
        pattern = "public required string DeleteLabel { get; init; }"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E0SourceFiles[1]
        source = $stage4E0CodeBehind
        pattern = "_recentQueries.Clear();"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E0SourceFiles[1]
        source = $stage4E0CodeBehind
        pattern = "_recentQueries.Add(new SearchHistoryEntry"
    }
)
$stage4E0MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E0RequiredBehaviorPatterns) {
        if ($contract.source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E0Wmc1506WarningMessages = @(
    $logLines |
        Where-Object { $_ -match "\bWMC1506\b" } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E0SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "SearchWidgetContent\.xaml(?:\.cs)?\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E1SourceFiles = @(
    "src\DeskBox\Controls\PinStateIcon.xaml",
    "src\DeskBox\Controls\PinStateIcon.xaml.cs",
    "src\DeskBox\Controls\MarkdownSourceEditor.xaml",
    "src\DeskBox\Controls\MarkdownSourceEditor.xaml.cs",
    "src\DeskBox\Controls\DesktopOrganizationTaskView.xaml",
    "src\DeskBox\Controls\DesktopOrganizationTaskView.xaml.cs",
    "src\DeskBox\Views\SettingsSections\DesktopOrganizationSettingsSection.xaml",
    "src\DeskBox\Views\SettingsSections\DesktopOrganizationSettingsSection.xaml.cs"
)
$stage4E1Sources = [ordered]@{}
foreach ($sourceFile in $stage4E1SourceFiles) {
    $stage4E1Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage4E1LegacyBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[0]
        pattern = "{Binding Foreground, ElementName=Root}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{Binding EditorFontSize, ElementName=Root}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{Binding IsReadOnly, ElementName=Root}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{Binding PlaceholderText, ElementName=Root}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[4]
        pattern = 'ToolTipService.ToolTip="{Binding Text, RelativeSource={RelativeSource Self}}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[6]
        pattern = 'ToolTipService.ToolTip="{Binding Text, RelativeSource={RelativeSource Self}}"'
    }
)
$stage4E1LegacyBindingSourceMatches = @(
    foreach ($contract in $stage4E1LegacyBindingContracts) {
        if ($stage4E1Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E1RequiredCompiledBindings = @(
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[0]
        pattern = "{x:Bind Foreground, Mode=OneWay}"
        expectedCount = 2
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{x:Bind EditorFontSize, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{x:Bind IsReadOnly, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[2]
        pattern = "{x:Bind PlaceholderText, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[4]
        pattern = 'ToolTipService.ToolTip="{x:Bind StoragePathText.Text, Mode=OneWay}"'
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[6]
        pattern = 'ToolTipService.ToolTip="{x:Bind RuleDetailPath.Text, Mode=OneWay}"'
        expectedCount = 1
    }
)
$stage4E1MissingCompiledBindings = @(
    foreach ($binding in $stage4E1RequiredCompiledBindings) {
        $actualCount = [regex]::Matches(
            $stage4E1Sources[$binding.sourceFile],
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.sourceFile)::$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E1RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[1]
        pattern = "IsPinnedProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[3]
        pattern = "EditorFontSizeProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[3]
        pattern = "IsReadOnlyProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[3]
        pattern = "PlaceholderTextProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[5]
        pattern = "StoragePathText.Text ="
    },
    [PSCustomObject]@{
        sourceFile = $stage4E1SourceFiles[7]
        pattern = "RuleDetailPath.Text ="
    }
)
$stage4E1MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E1RequiredBehaviorPatterns) {
        if ($stage4E1Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E1DeferredBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = "src\DeskBox\App.xaml"
        pattern = 'Value="{Binding SegmentHeight}"'
    },
    [PSCustomObject]@{
        sourceFile = "src\DeskBox\App.xaml"
        pattern = 'Value="{Binding SegmentTextSize}"'
    },
    [PSCustomObject]@{
        sourceFile = "src\DeskBox\Views\ContentWidgetWindow.xaml"
        pattern = 'OverlayTitle="{Binding DisplayName}"'
    }
)
$stage4E1MissingDeferredBindings = @(
    foreach ($contract in $stage4E1DeferredBindingContracts) {
        $source = Get-Content -LiteralPath (Join-Path $repoRoot $contract.sourceFile) -Raw
        if ($source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:PinStateIcon|MarkdownSourceEditor|DesktopOrganizationTaskView|DesktopOrganizationSettingsSection)\.xaml\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E1MaximumWmc1510Count = 1258
$stage4E1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage4E2SourceFiles = @(
    "src\DeskBox\Controls\WidgetContents\MusicTransportIcon.xaml",
    "src\DeskBox\Controls\WidgetContents\MusicTransportIcon.xaml.cs",
    "src\DeskBox\Controls\WidgetInlineEditor.xaml",
    "src\DeskBox\Controls\WidgetInlineEditor.xaml.cs"
)
$stage4E2Sources = [ordered]@{}
foreach ($sourceFile in $stage4E2SourceFiles) {
    $stage4E2Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage4E2LegacyBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[0]
        pattern = "{Binding Foreground, ElementName=Root}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding TitleFontSize, ElementName=InlineEditorRoot}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding Title, ElementName=InlineEditorRoot}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding EditorFontSize, ElementName=InlineEditorRoot}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding Text, ElementName=InlineEditorRoot, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding CancelText, ElementName=InlineEditorRoot}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding CommandFontSize, ElementName=InlineEditorRoot}"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{Binding SaveText, ElementName=InlineEditorRoot}"
    }
)
$stage4E2LegacyBindingSourceMatches = @(
    foreach ($contract in $stage4E2LegacyBindingContracts) {
        if ($stage4E2Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E2RequiredCompiledBindings = @(
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[0]
        pattern = "{x:Bind Foreground, Mode=OneWay}"
        expectedCount = 7
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind TitleFontSize, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind Title, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind EditorFontSize, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind Text, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind CancelText, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind CommandFontSize, Mode=OneWay}"
        expectedCount = 2
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[2]
        pattern = "{x:Bind SaveText, Mode=OneWay}"
        expectedCount = 1
    }
)
$stage4E2MissingCompiledBindings = @(
    foreach ($binding in $stage4E2RequiredCompiledBindings) {
        $actualCount = [regex]::Matches(
            $stage4E2Sources[$binding.sourceFile],
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.sourceFile)::$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E2RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[1]
        pattern = "KindProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[1]
        pattern = "OnKindChanged"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[1]
        pattern = "ApplyKind();"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[3]
        pattern = "TextProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[3]
        pattern = "SaveRequested?.Invoke(this, e);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[3]
        pattern = "CancelRequested?.Invoke(this, e);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[3]
        pattern = "EditorKeyDown?.Invoke(this, e);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E2SourceFiles[3]
        pattern = "PreviewKeyDownEvent"
    }
)
$stage4E2MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E2RequiredBehaviorPatterns) {
        if ($stage4E2Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E2DeferredBindingContracts = $stage4E1DeferredBindingContracts
$stage4E2MissingDeferredBindings = @(
    foreach ($contract in $stage4E2DeferredBindingContracts) {
        $source = Get-Content -LiteralPath (Join-Path $repoRoot $contract.sourceFile) -Raw
        if ($source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E2SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:MusicTransportIcon|WidgetInlineEditor)\.xaml\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E2MaximumWmc1510Count = 1243
$stage4E2ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage4E3SourceFiles = @(
    "src\DeskBox\Controls\AttachmentTileStrip.xaml",
    "src\DeskBox\Controls\AttachmentTileStrip.xaml.cs",
    "src\DeskBox\ViewModels\TodoAttachmentViewModel.cs",
    "src\DeskBox\Views\SearchPopupWindow.xaml",
    "src\DeskBox\Views\SearchPopupWindow.xaml.cs",
    "src\DeskBox\Models\SearchModels.cs"
)
$stage4E3Sources = [ordered]@{}
foreach ($sourceFile in $stage4E3SourceFiles) {
    $stage4E3Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage4E3LegacyBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'AutomationProperties.Name="{Binding DisplayName}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'ToolTipService.ToolTip="{Binding DisplayName}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'Glyph="{Binding Glyph}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'Visibility="{Binding FileIconVisibility}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'Source="{Binding Thumbnail}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'Visibility="{Binding ThumbnailVisibility}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'Text="{Binding DisplayName}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Glyph="{Binding Glyph}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Text="{Binding DisplayName}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Text="{Binding Count}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Source="{Binding Icon}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Text="{Binding AppDisplayName}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'Text="{Binding Title}"'
    }
)
$stage4E3LegacyBindingSourceMatches = @(
    foreach ($contract in $stage4E3LegacyBindingContracts) {
        if ($stage4E3Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E3RequiredCompiledBindings = @(
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = "{x:Bind DisplayName, Mode=OneWay}"
        expectedCount = 3
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = "{x:Bind Glyph, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = "{x:Bind FileIconVisibility, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = "{x:Bind Thumbnail, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = "{x:Bind ThumbnailVisibility, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind Glyph, Mode=OneTime}"
        expectedCount = 0
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind DisplayName, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind Count, Mode=OneWay}"
        expectedCount = 0
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind Icon, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind AppDisplayName, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = "{x:Bind Title, Mode=OneTime}"
        expectedCount = 2
    }
)
$stage4E3MissingCompiledBindings = @(
    foreach ($binding in $stage4E3RequiredCompiledBindings) {
        $actualCount = [regex]::Matches(
            $stage4E3Sources[$binding.sourceFile],
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.sourceFile)::$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E3RequiredDataTypes = @(
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[0]
        pattern = 'x:DataType="viewModels:TodoAttachmentViewModel"'
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'x:DataType="models:SearchTabItem"'
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'x:DataType="models:SearchResultItem"'
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[3]
        pattern = 'x:DataType="models:SearchRecommendationItem"'
        expectedCount = 2
    }
)
$stage4E3MissingDataTypes = @(
    foreach ($dataType in $stage4E3RequiredDataTypes) {
        $actualCount = [regex]::Matches(
            $stage4E3Sources[$dataType.sourceFile],
            [regex]::Escape($dataType.pattern)).Count
        if ($actualCount -ne $dataType.expectedCount) {
            "$($dataType.sourceFile)::$($dataType.pattern) expected=$($dataType.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E3RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[1]
        pattern = "await EnsureThumbnailAsync(args.NewValue);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[1]
        pattern = "OpenRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[1]
        pattern = "RemoveRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[2]
        pattern = "public sealed class TodoAttachmentViewModel : ObservableObject"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[2]
        pattern = "OnPropertyChanged(nameof(ThumbnailVisibility));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[2]
        pattern = "OnPropertyChanged(nameof(FileIconVisibility));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[4]
        pattern = "RecommendedAppsRepeater.ElementPrepared += OnRecommendedAppsElementPrepared;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[4]
        pattern = "private void RefreshRecommendedAppIcons()"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[4]
        pattern = "image.Source = item.Icon;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[5]
        pattern = "public sealed class SearchTabItem : INotifyPropertyChanged"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[5]
        pattern = "PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E3SourceFiles[5]
        pattern = "public ImageSource? Icon { get; set; }"
    }
)
$stage4E3MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E3RequiredBehaviorPatterns) {
        if ($stage4E3Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E3DeferredBindingContracts = @($stage4E2DeferredBindingContracts)
$stage4E3MissingDeferredBindings = @(
    foreach ($contract in $stage4E3DeferredBindingContracts) {
        $source = Get-Content -LiteralPath (Join-Path $repoRoot $contract.sourceFile) -Raw
        if ($source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E3SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:AttachmentTileStrip|SearchPopupWindow)\.xaml\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E3MaximumWmc1510Count = 1241
$stage4E3ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage4E4SourceFiles = @(
    "src\DeskBox\Views\SettingsSections\FileWidgetSettingsSection.xaml",
    "src\DeskBox\Views\SettingsSections\FileWidgetSettingsSection.xaml.cs",
    "src\DeskBox\Views\SettingsWindow.xaml.cs",
    "src\DeskBox\ViewModels\SettingsViewModel.FileStackOptions.cs",
    "src\DeskBox\ViewModels\SettingsViewModel.FeatureOptions.cs",
    "src\DeskBox\ViewModels\SettingsViewModel.SelectionOptions.cs",
    "src\DeskBox\Controls\SettingsComboBox.cs"
)
$stage4E4Sources = [ordered]@{}
foreach ($sourceFile in $stage4E4SourceFiles) {
    $stage4E4Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage4E4LegacyBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = 'Text="{Binding FileStackSettingsSummaryText}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = 'ItemsSource="{Binding AvailableFileStackModeOptions}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = 'controls:SettingsComboBox.Value="{Binding SelectedFileStackMode, Mode=TwoWay}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = 'ItemsSource="{Binding AvailableFileWidgetFolderOpenBehaviorOptions}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = 'controls:SettingsComboBox.Value="{Binding SelectedFileWidgetFolderOpenBehavior, Mode=TwoWay}"'
    }
)
$stage4E4LegacyBindingSourceMatches = @(
    foreach ($contract in $stage4E4LegacyBindingContracts) {
        if ($stage4E4Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E4RequiredCompiledBindings = @(
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = "{x:Bind ViewModel.FileStackSettingsSummaryText, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = "{x:Bind ViewModel.FileStacksEnabled, Mode=TwoWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = "{x:Bind ViewModel.AvailableFileWidgetFolderOpenBehaviorOptionItems, Mode=OneWay}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[0]
        pattern = "{x:Bind ViewModel.SelectedFileWidgetFolderOpenBehavior, Mode=TwoWay}"
        expectedCount = 1
    }
)
$stage4E4MissingCompiledBindings = @(
    foreach ($binding in $stage4E4RequiredCompiledBindings) {
        $actualCount = [regex]::Matches(
            $stage4E4Sources[$binding.sourceFile],
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.sourceFile)::$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E4RequiredViewModelBridgePatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "public static readonly DependencyProperty ViewModelProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "DependencyProperty.Register("
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "nameof(ViewModel)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "typeof(SettingsViewModel)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "new PropertyMetadata(null)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "public SettingsViewModel? ViewModel"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "get => (SettingsViewModel?)GetValue(ViewModelProperty);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[1]
        pattern = "set => SetValue(ViewModelProperty, value);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[2]
        pattern = "SettingsRoot.DataContext = ViewModel;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[2]
        pattern = "AppearanceDetailSection.ViewModel = ViewModel;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[2]
        pattern = "AppearanceDetailSection.ViewModel = null;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[2]
        pattern = "ViewModel.Dispose();"
    }
)
$stage4E4MissingViewModelBridgePatterns = @(
    foreach ($contract in $stage4E4RequiredViewModelBridgePatterns) {
        if ($stage4E4Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E4SettingsWindowSource = $stage4E4Sources[$stage4E4SourceFiles[2]]
$stage4E4RootDataContextIndex = $stage4E4SettingsWindowSource.IndexOf(
    "SettingsRoot.DataContext = ViewModel;",
    [StringComparison]::Ordinal)
$stage4E4BridgeAssignmentIndex = $stage4E4SettingsWindowSource.IndexOf(
    "AppearanceDetailSection.ViewModel = ViewModel;",
    [StringComparison]::Ordinal)
$stage4E4BridgeClearIndex = $stage4E4SettingsWindowSource.IndexOf(
    "AppearanceDetailSection.ViewModel = null;",
    [StringComparison]::Ordinal)
$stage4E4ViewModelDisposeIndex = $stage4E4SettingsWindowSource.IndexOf(
    "ViewModel.Dispose();",
    [StringComparison]::Ordinal)
$stage4E4ViewModelBridgeOrderValid =
    $stage4E4RootDataContextIndex -ge 0 -and
    $stage4E4BridgeAssignmentIndex -gt $stage4E4RootDataContextIndex -and
    $stage4E4BridgeClearIndex -ge 0 -and
    $stage4E4BridgeClearIndex -lt $stage4E4ViewModelDisposeIndex
$stage4E4UnexpectedManualBridgePatterns = @(
    foreach ($pattern in @("OnViewModelChanged", "Bindings.Update()")) {
        if ($stage4E4Sources[$stage4E4SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage4E4SourceFiles[1])::$pattern"
        }
    }
)
$stage4E4RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[3]
        pattern = "OnPropertyChanged(nameof(FileStackSettingsSummaryText));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[3]
        pattern = "OnPropertyChanged(nameof(FileStackAutoStacking));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[3]
        pattern = "SetProperty(ref _fileStacksEnabled, value)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[4]
        pattern = "public string SelectedFileWidgetFolderOpenBehavior"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[4]
        pattern = "_settingsService.Settings.FileWidgetFolderOpenBehavior = normalized;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[5]
        pattern = "OnPropertyChanged(nameof(AvailableFileWidgetFolderOpenBehaviorOptions));"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[6]
        pattern = "ItemsControl.ItemsSourceProperty"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[6]
        pattern = "_comboBox.SelectionChanged += OnSelectionChanged;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[6]
        pattern = "SetValue(_comboBox, option.Value);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E4SourceFiles[6]
        pattern = "ApplyValueToSelection();"
    }
)
$stage4E4MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E4RequiredBehaviorPatterns) {
        if ($stage4E4Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E4DeferredBindingContracts = @($stage4E3DeferredBindingContracts)
$stage4E4MissingDeferredBindings = @(
    foreach ($contract in $stage4E4DeferredBindingContracts) {
        $source = Get-Content -LiteralPath (Join-Path $repoRoot $contract.sourceFile) -Raw
        if ($source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E4SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:FileWidgetSettingsSection\.xaml(?:\.cs)?|SettingsWindow\.xaml\.cs|SettingsViewModel\.(?:FileStackOptions|FeatureOptions|SelectionOptions)\.cs|SettingsComboBox\.cs)\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E4MaximumWmc1510Count = 1241
$stage4E4ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage4E5SourceFiles = @(
    "src\DeskBox\Controls\SearchResultRowControl.xaml",
    "src\DeskBox\Controls\SearchResultRowControl.xaml.cs",
    "src\DeskBox\Views\SearchPopupWindow.xaml",
    "src\DeskBox\Views\SearchPopupWindow.xaml.cs",
    "src\DeskBox\Models\SearchModels.cs",
    "src\DeskBox\ViewModels\SearchPopupViewModel.cs",
    "src\DeskBox\Services\FileMetaService.cs"
)
$stage4E5Sources = [ordered]@{}
foreach ($sourceFile in $stage4E5SourceFiles) {
    $stage4E5Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage4E5LegacyBindingContracts = @(
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'AutomationProperties.Name="{Binding Title}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Glyph="{Binding DisplayGlyph}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Source="{Binding Icon}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Text="{Binding Title}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Text="{Binding Subtitle}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Text="{Binding TypeDisplay}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Text="{Binding SizeDisplay}"'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = 'Text="{Binding DateDisplay}"'
    }
)
$stage4E5LegacyBindingSourceMatches = @(
    foreach ($contract in $stage4E5LegacyBindingContracts) {
        if ($stage4E5Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E5RequiredCompiledBindings = @(
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.Title, Mode=OneTime}"
        expectedCount = 2
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.DisplayGlyph, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.Icon, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.Subtitle, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.TypeDisplay, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.SizeDisplay, Mode=OneTime}"
        expectedCount = 1
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[0]
        pattern = "{x:Bind Item.DateDisplay, Mode=OneTime}"
        expectedCount = 1
    }
)
$stage4E5MissingCompiledBindings = @(
    foreach ($binding in $stage4E5RequiredCompiledBindings) {
        $actualCount = [regex]::Matches(
            $stage4E5Sources[$binding.sourceFile],
            [regex]::Escape($binding.pattern)).Count
        if ($actualCount -ne $binding.expectedCount) {
            "$($binding.sourceFile)::$($binding.pattern) expected=$($binding.expectedCount) actual=$actualCount"
        }
    }
)
$stage4E5RequiredItemBridgePatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[2]
        pattern = '<controls:SearchResultRowControl/>'
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "internal SearchResultItem? Item { get; private set; }"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "internal void PrepareItem(SearchResultItem? item)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "Item = item;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "Bindings.Update();"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "? _viewModel.CurrentResults[args.Index]"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "row.PrepareItem(preparedItem);"
    }
)
$stage4E5MissingItemBridgePatterns = @(
    foreach ($contract in $stage4E5RequiredItemBridgePatterns) {
        if ($stage4E5Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E5UnexpectedPublicItemBridgePatterns = @(
    foreach ($pattern in @(
            "public SearchResultItem? Item",
            "DependencyProperty ItemProperty")) {
        if ($stage4E5Sources[$stage4E5SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage4E5SourceFiles[1])::$pattern"
        }
    }
)
$stage4E5PopupSource = $stage4E5Sources[$stage4E5SourceFiles[3]]
$stage4E5PrepareIndex = $stage4E5PopupSource.IndexOf(
    "row.PrepareItem(preparedItem);",
    [StringComparison]::Ordinal)
$stage4E5RefreshIndex = $stage4E5PopupSource.IndexOf(
    "row.RefreshIconVisuals();",
    $stage4E5PrepareIndex + 1,
    [StringComparison]::Ordinal)
$stage4E5ItemRefreshOrderValid =
    $stage4E5PrepareIndex -ge 0 -and
    $stage4E5RefreshIndex -gt $stage4E5PrepareIndex
$stage4E5RequiredBehaviorPatterns = @(
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "public void RefreshIconVisuals()"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "FileIcon.Source = item?.Icon;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "GlyphBlock.Visibility = hasIcon"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "SizeText.Text = item?.SizeDisplay;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[1]
        pattern = "DateText.Text = item?.DateDisplay;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "ResultsRepeater.ElementPrepared += OnResultsElementPrepared;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "row.Item is { IconResolved: false } item"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "await _viewModel.EnsureResultMetadataAsync(item);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "if (ReferenceEquals(row.Item, item))"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "ReferenceEquals(row.Item, selected)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "row.IsMultiSelected = row.Item is { } rowItem"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "FindRowByDataContext(ResultsRepeater, selected)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "ResultsRepeater.ElementPrepared -= OnResultsElementPrepared;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[3]
        pattern = "ResultsRepeater.ItemsSource = null;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[5]
        pattern = "item.TypeDisplay = GetTypeDisplay(item);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[5]
        pattern = "public Task EnsureResultMetadataAsync(SearchResultItem item)"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[6]
        pattern = "item.IconResolved = true;"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[6]
        pattern = "item.SizeDisplay = FormatSize(fileInfo.Length);"
    },
    [PSCustomObject]@{
        sourceFile = $stage4E5SourceFiles[6]
        pattern = "item.DateDisplay = FormatDate(fileInfo.CreationTime);"
    }
)
$stage4E5MissingBehaviorPatterns = @(
    foreach ($contract in $stage4E5RequiredBehaviorPatterns) {
        if ($stage4E5Sources[$contract.sourceFile].IndexOf(
                $contract.pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E5ModelSource = $stage4E5Sources[$stage4E5SourceFiles[4]]
$stage4E5ModelStart = $stage4E5ModelSource.IndexOf(
    "public sealed class SearchResultItem",
    [StringComparison]::Ordinal)
$stage4E5ModelEnd = $stage4E5ModelSource.IndexOf(
    "public sealed class SearchResultGroup",
    $stage4E5ModelStart + 1,
    [StringComparison]::Ordinal)
$stage4E5ItemModelSource = if ($stage4E5ModelStart -ge 0 -and $stage4E5ModelEnd -gt $stage4E5ModelStart) {
    $stage4E5ModelSource.Substring(
        $stage4E5ModelStart,
        $stage4E5ModelEnd - $stage4E5ModelStart)
}
else {
    ""
}
$stage4E5MissingRequiredModelPatterns = @(
    foreach ($pattern in @(
            "public required SearchResultKind Kind { get; init; }",
            "public required string Title { get; init; }",
            "public ImageSource? Icon { get; set; }",
            "public string? SizeDisplay { get; set; }",
            "public string? DateDisplay { get; set; }")) {
        if ($stage4E5ItemModelSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage4E5SourceFiles[4])::$pattern"
        }
    }
)
$stage4E5UnexpectedObservableModelPatterns = @(
    foreach ($pattern in @(
            "INotifyPropertyChanged",
            "PropertyChangedEventHandler",
            "OnPropertyChanged")) {
        if ($stage4E5ItemModelSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage4E5SourceFiles[4])::$pattern"
        }
    }
)
$stage4E5UnexpectedDataContextOverridePatterns = @(
    foreach ($pattern in @("ResultsRepeater.DataContext =")) {
        if ($stage4E5PopupSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage4E5SourceFiles[3])::$pattern"
        }
    }
)
$stage4E5UnhookIndex = $stage4E5PopupSource.IndexOf(
    "ResultsRepeater.ElementPrepared -= OnResultsElementPrepared;",
    [StringComparison]::Ordinal)
$stage4E5ClearIndex = $stage4E5PopupSource.IndexOf(
    "ResultsRepeater.ItemsSource = null;",
    [StringComparison]::Ordinal)
$stage4E5DisposeIndex = $stage4E5PopupSource.IndexOf(
    "_viewModel.Dispose();",
    [StringComparison]::Ordinal)
$stage4E5LifecycleOrderValid =
    $stage4E5UnhookIndex -ge 0 -and
    $stage4E5ClearIndex -gt $stage4E5UnhookIndex -and
    $stage4E5DisposeIndex -gt $stage4E5ClearIndex
$stage4E5DeferredBindingContracts = @($stage4E2DeferredBindingContracts)
$stage4E5MissingDeferredBindings = @(
    foreach ($contract in $stage4E5DeferredBindingContracts) {
        $source = Get-Content -LiteralPath (Join-Path $repoRoot $contract.sourceFile) -Raw
        if ($source.IndexOf($contract.pattern, [StringComparison]::Ordinal) -lt 0) {
            "$($contract.sourceFile)::$($contract.pattern)"
        }
    }
)
$stage4E5SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:SearchResultRowControl\.xaml(?:\.cs)?|SearchPopupWindow\.xaml(?:\.cs)?|SearchModels\.cs|SearchPopupViewModel\.cs|FileMetaService\.cs)\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage4E5ExpectedWmc1510Count = 1241
$stage4E5ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5ASourceFiles = @(
    "src/DeskBox/Services/DeskBoxDataPathService.cs",
    "scripts/start-aot-preview.ps1"
)
$stage5ASources = [ordered]@{}
foreach ($sourceFile in $stage5ASourceFiles) {
    $stage5ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5ARequiredDataPathPatterns = @(
    'AotPreviewRootEnvironmentVariable = "DESKBOX_AOT_PREVIEW_DATA_ROOT"',
    'Current { get; } = new(ResolveConfiguredRoot())',
    '#if DEBUG',
    'Environment.GetEnvironmentVariable(DevelopmentRootEnvironmentVariable)',
    '#elif DESKBOX_NATIVE_AOT',
    'Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable)',
    '#else',
    'return null;'
)
$stage5AMissingDataPathPatterns = @(
    foreach ($pattern in $stage5ARequiredDataPathPatterns) {
        if ($stage5ASources[$stage5ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5ASourceFiles[0])::$pattern"
        }
    }
)
$stage5ARequiredLauncherPatterns = @(
    '$RequiredAuditProfileVersion = 58',
    '$RequiredSummarySchemaVersion = 55',
    'Test-PathEqualOrInside',
    'Get-DirectoryStateFingerprint',
    'Get-AotPreviewProcesses',
    'Refusing to start Native AOT preview with the production data root',
    'DESKBOX_AOT_PREVIEW_DATA_ROOT',
    'DESKBOX_DEV_DATA_ROOT',
    'sourceStableDuringAudit',
    'rustNative.publishSha256',
    'rustNative.publishMatchesStaging',
    'ExpectExistingInstance',
    'ExistingInstanceActivated',
    'productionDataFingerprintBefore',
    '$records.Sort([System.StringComparer]::Ordinal)',
    'path-upper-length-lastwriteutc-v1-ordinal',
    'session.json'
)
$stage5AMissingLauncherPatterns = @(
    foreach ($pattern in $stage5ARequiredLauncherPatterns) {
        if ($stage5ASources[$stage5ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5ASourceFiles[1])::$pattern"
        }
    }
)
$stage5AUnsafeLauncherPatterns = @(
    foreach ($pattern in @(
            'UseProductionData',
            'ExecutablePath.StartsWith($repoRootPath')) {
        if ($stage5ASources[$stage5ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5ASourceFiles[1])::$pattern"
        }
    }
)
$stage5ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "DeskBoxDataPathService\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5AExpectedWmc1510Count = 1241
$stage5AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B1SourceFiles = @(
    "src/DeskBox/App.AotShortcutSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "scripts/run-aot-shortcut-smoke.ps1"
)
$stage5B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B1SourceFiles) {
    $stage5B1Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B1RequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'IsDevelopmentRoot',
    'RefusedNonPreviewRoot',
    'aot-shortcut-smoke',
    'DragDropPermissionService.CreateOrUpdateShortcut(',
    'ShortcutHelper.CreateOrUpdateFolderShortcut(',
    'ShortcutHelper.ReadStoredMetadata(',
    'ShortcutHelper.Resolve(',
    'ShortcutHelper.ResolveBrokenShortcutWithShellUi(',
    'File.Move(targetPath, replacementPath)',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'ShortcutNativeModule.Default',
    'RuntimeFeature.IsDynamicCodeSupported',
    'WindowNative.GetWindowHandle(_trayWindow)',
    'AwaitingShellUi',
    'result.json',
    'AotShortcutSmokeJsonContext.Default.AotShortcutSmokeResult'
)
$stage5B1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B1RequiredRunnerPatterns) {
        if ($stage5B1Sources[$stage5B1SourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B1SourceFiles[0])::$pattern"
        }
    }
)
$stage5B1RequiredLaunchPatterns = @(
    'Log("OnLaunched completed successfully");',
    'StartAotShortcutSmokeIfRequested();'
)
$stage5B1MissingLaunchPatterns = @(
    foreach ($pattern in $stage5B1RequiredLaunchPatterns) {
        if ($stage5B1Sources[$stage5B1SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B1SourceFiles[1])::$pattern"
        }
    }
)
$stage5B1LaunchCompletedIndex = $stage5B1Sources[$stage5B1SourceFiles[1]].IndexOf(
    $stage5B1RequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B1LaunchSmokeIndex = $stage5B1Sources[$stage5B1SourceFiles[1]].IndexOf(
    $stage5B1RequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B1LaunchOrderValid =
    $stage5B1LaunchCompletedIndex -ge 0 -and
    $stage5B1LaunchSmokeIndex -gt $stage5B1LaunchCompletedIndex
$stage5B1RequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'previousShortcutSmoke',
    'previousShellSmoke',
    'previousMutationSmoke',
    'previousMusicReadSmoke',
    'start-aot-preview.ps1',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'executableSha256',
    'rustNativeSha256',
    'ModuleSha256',
    'ModulePath',
    'AwaitingShellUi',
    'Completed',
    'Stop-ExactPreviewProcess',
    'Production data changed',
    'session.json'
)
$stage5B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B1RequiredSmokeScriptPatterns) {
        if ($stage5B1Sources[$stage5B1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B1UnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'File.WriteAllText(replacementPath',
            'Directory.Delete(dataPaths.RootPath')) {
        if ($stage5B1Sources[$stage5B1SourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B1SourceFiles[0])::$pattern"
        }
    }
)
$stage5B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotShortcutSmoke|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B1ExpectedWmc1510Count = 1241
$stage5B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B2ASourceFiles = @(
    "src/DeskBox/App.AotShellSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Helpers/ExplorerShellLaunchService.cs",
    "src/DeskBox/Helpers/ExplorerQuickAccessHelper.cs",
    "scripts/run-aot-shell-smoke.ps1"
)
$stage5B2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B2ASourceFiles) {
    $stage5B2ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B2ARequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_SHELL_SMOKE',
    'ExplorerQuickAccessReadOnly',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'IsDevelopmentRoot',
    'RefusedNonPreviewRoot',
    'aot-shell-smoke',
    'ExplorerShellLaunchService.TryOpen(',
    'explorer-launch-probe.cmd',
    'explorer-launch-marker.txt',
    'ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(',
    'QuickAccessNativeBackend.Invoke(',
    'QuickAccessNativeOperation.QueryPinState',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'ShortcutNativeModule.Default',
    'RuntimeFeature.IsDynamicCodeSupported',
    'AotShellSmokeJsonContext.Default.AotShellSmokeResult'
)
$stage5B2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B2ARequiredRunnerPatterns) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B2ARequiredLaunchPatterns = @(
    'Log("OnLaunched completed successfully");',
    'StartAotShellSmokeIfRequested();'
)
$stage5B2AMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B2ARequiredLaunchPatterns) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2ASourceFiles[1])::$pattern"
        }
    }
)
$stage5B2ALaunchCompletedIndex = $stage5B2ASources[$stage5B2ASourceFiles[1]].IndexOf(
    $stage5B2ARequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B2ALaunchSmokeIndex = $stage5B2ASources[$stage5B2ASourceFiles[1]].IndexOf(
    $stage5B2ARequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B2ALaunchOrderValid =
    $stage5B2ALaunchCompletedIndex -ge 0 -and
    $stage5B2ALaunchSmokeIndex -gt $stage5B2ALaunchCompletedIndex
$stage5B2ARequiredServicePatterns = @(
    'out ExplorerShellLaunchNativeCallResult? nativeResult',
    'nativeResult = ExplorerShellLaunchNativeBackend.TryOpen('
)
$stage5B2AMissingServicePatterns = @(
    foreach ($pattern in $stage5B2ARequiredServicePatterns) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2ASourceFiles[2])::$pattern"
        }
    }
)
$stage5B2ARequiredQuickAccessPatterns = @(
    'GetQuickAccessPinStateAsync(string folderPath)',
    'QuickAccessNativeOperation.QueryPinState'
)
$stage5B2AMissingQuickAccessPatterns = @(
    foreach ($pattern in $stage5B2ARequiredQuickAccessPatterns) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2ASourceFiles[3])::$pattern"
        }
    }
)
$stage5B2ARequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'previousShellSmoke',
    'previousShortcutSmoke',
    'previousMutationSmoke',
    'previousMusicReadSmoke',
    'start-aot-preview.ps1',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'executableSha256',
    'rustNativeSha256',
    'ModuleSha256',
    'ModulePath',
    'ExplorerMarkerExists',
    'QuickAccessStateBefore',
    'QuickAccessStateAfter',
    'QuickAccessNativeState',
    'Completed',
    'Stop-ExactPreviewProcess',
    'Production data changed',
    'session.json'
)
$stage5B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B2ARequiredSmokeScriptPatterns) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2ASourceFiles[4])::$pattern"
        }
    }
)
$stage5B2AUnsafeMutationPatterns = @(
    foreach ($pattern in @(
            'TryPinFolderToQuickAccess',
            'TryUnpinFolderFromQuickAccess',
            'QuickAccessNativeOperation.Pin,',
            'QuickAccessNativeOperation.Unpin')) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B2ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B2AUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'Directory.Delete(dataPaths.RootPath')) {
        if ($stage5B2ASources[$stage5B2ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B2ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotShellSmoke|ExplorerShellLaunchService|ExplorerQuickAccessHelper|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B2AExpectedWmc1510Count = 1241
$stage5B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B2BSourceFiles = @(
    "src/DeskBox/App.AotQuickAccessMutationSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Helpers/ExplorerQuickAccessHelper.cs",
    "scripts/run-aot-quick-access-mutation-smoke.ps1"
)
$stage5B2BSources = [ordered]@{}
foreach ($sourceFile in $stage5B2BSourceFiles) {
    $stage5B2BSources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B2BRequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'AotQuickAccessMutationScenario.PinUnpin',
    'AotQuickAccessMutationScenario.PinThenFail',
    'AotQuickAccessMutationScenario.PinThenAwaitExternalCompensation',
    'AotQuickAccessMutationScenario.CompensateUnpin',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'IsDevelopmentRoot',
    'RefusedNonPreviewRoot',
    'aot-quick-access-mutation-smoke',
    'mutation-target',
    'ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(targetFolder)',
    'ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(targetFolder)',
    'ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(targetFolder)',
    'QuickAccessNativeOperation.QueryPinState',
    'RunCompensatingUnpinAsync(targetFolder, result)',
    'intentional-after-pin',
    'AwaitingExternalCompensation',
    'Task.Delay(Timeout.InfiniteTimeSpan)',
    'finally',
    'mutation-pinned-public',
    'mutation-pinned-native',
    'mutation-unpinned-public',
    'mutation-unpinned-native',
    'cleanup-final-not-pinned',
    'cleanup-native-not-pinned',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'ShortcutNativeModule.Default',
    'RuntimeFeature.IsDynamicCodeSupported',
    'AotQuickAccessMutationSmokeJsonContext.Default.AotQuickAccessMutationSmokeResult'
)
$stage5B2BMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B2BRequiredRunnerPatterns) {
        if ($stage5B2BSources[$stage5B2BSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2BSourceFiles[0])::$pattern"
        }
    }
)
$stage5B2BRequiredLaunchPatterns = @(
    'Log("OnLaunched completed successfully");',
    'StartAotQuickAccessMutationSmokeIfRequested();'
)
$stage5B2BMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B2BRequiredLaunchPatterns) {
        if ($stage5B2BSources[$stage5B2BSourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2BSourceFiles[1])::$pattern"
        }
    }
)
$stage5B2BLaunchCompletedIndex = $stage5B2BSources[$stage5B2BSourceFiles[1]].IndexOf(
    $stage5B2BRequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B2BLaunchSmokeIndex = $stage5B2BSources[$stage5B2BSourceFiles[1]].IndexOf(
    $stage5B2BRequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B2BLaunchOrderValid =
    $stage5B2BLaunchCompletedIndex -ge 0 -and
    $stage5B2BLaunchSmokeIndex -gt $stage5B2BLaunchCompletedIndex
$stage5B2BRequiredQuickAccessPatterns = @(
    'GetQuickAccessPinStateAsync(string folderPath)',
    'TryPinFolderToQuickAccessAsync(string folderPath)',
    'TryUnpinFolderFromQuickAccessAsync(string folderPath)',
    'QuickAccessNativeOperation.Pin',
    'QuickAccessNativeOperation.Unpin'
)
$stage5B2BMissingQuickAccessPatterns = @(
    foreach ($pattern in $stage5B2BRequiredQuickAccessPatterns) {
        if ($stage5B2BSources[$stage5B2BSourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2BSourceFiles[2])::$pattern"
        }
    }
)
$stage5B2BRequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'previousMutationSmoke',
    'previousShortcutSmoke',
    'previousShellSmoke',
    'previousMusicReadSmoke',
    'start-aot-preview.ps1',
    'Invoke-MutationScenario',
    'CompensateUnpin',
    'PinUnpin',
    'PinThenFail',
    'PinThenAwaitExternalCompensation',
    'preflight',
    'in-process-failure',
    'forced-termination',
    'recovery',
    'postflight',
    'Assert-InProcessFailureResult',
    'Assert-ForcedTerminationResult',
    '-RequireInitiallyPinned',
    'Stop-ExactPreviewProcess',
    'CleanupSucceeded',
    'FinalPublicState',
    'FinalNativeState',
    'PinnedPublicState',
    'PinnedNativeState',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'Production data changed',
    'executableSha256',
    'rustNativeSha256',
    'session.json'
)
$stage5B2BMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B2BRequiredSmokeScriptPatterns) {
        if ($stage5B2BSources[$stage5B2BSourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B2BSourceFiles[3])::$pattern"
        }
    }
)
$stage5B2BUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'Directory.Delete(dataPaths.RootPath)',
            'Directory.Delete(targetFolder',
            'QuickAccessNativeOperation.Pin,',
            'QuickAccessNativeOperation.Unpin')) {
        if ($stage5B2BSources[$stage5B2BSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B2BSourceFiles[0])::$pattern"
        }
    }
)
$stage5B2BSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotQuickAccessMutationSmoke|ExplorerQuickAccessHelper|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B2BExpectedWmc1510Count = 1241
$stage5B2BActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B3ASourceFiles = @(
    "src/DeskBox/App.AotMusicVolumeReadSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Services/MusicVolumeService.cs",
    "src/DeskBox/Helpers/MusicVolumeNativeBackend.cs",
    "scripts/run-aot-music-volume-read-smoke.ps1"
)
$stage5B3ASources = [ordered]@{}
foreach ($sourceFile in $stage5B3ASourceFiles) {
    $stage5B3ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B3ARequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'AotMusicVolumeReadSmokeScenario.SystemAndSnapshotReadOnly',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'IsDevelopmentRoot',
    'RefusedNonPreviewRoot',
    'aot-music-volume-read-smoke',
    'new MusicVolumeService()',
    'GetSystemMasterVolumeAsync()',
    'GetVolumeAsync(',
    'MusicVolumeNativeBackend.GetSystemVolume()',
    'MusicVolumeNativeBackend.GetSnapshot(',
    'nativeSystem.Success',
    'nativeSnapshot.Success',
    'default-audio-endpoint',
    'NativeSystemVolumeBefore',
    'NativeSystemVolumeAfter',
    'system-volume-unchanged',
    'AttemptedPhases',
    'OperationHResult',
    'DeviceHResult',
    'SystemHResult',
    'SessionHResult',
    'MatchKind',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'ShortcutNativeModule.Default',
    'RuntimeFeature.IsDynamicCodeSupported',
    'AotMusicVolumeReadSmokeJsonContext.Default.AotMusicVolumeReadSmokeResult'
)
$stage5B3AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B3ARequiredRunnerPatterns) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B3ARequiredLaunchPatterns = @(
    'Log("OnLaunched completed successfully");',
    'StartAotMusicVolumeReadSmokeIfRequested();'
)
$stage5B3AMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B3ARequiredLaunchPatterns) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3ASourceFiles[1])::$pattern"
        }
    }
)
$stage5B3ALaunchCompletedIndex = $stage5B3ASources[$stage5B3ASourceFiles[1]].IndexOf(
    $stage5B3ARequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B3ALaunchSmokeIndex = $stage5B3ASources[$stage5B3ASourceFiles[1]].IndexOf(
    $stage5B3ARequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B3ALaunchOrderValid =
    $stage5B3ALaunchCompletedIndex -ge 0 -and
    $stage5B3ALaunchSmokeIndex -gt $stage5B3ALaunchCompletedIndex
$stage5B3ARequiredProductPatterns = @(
    'GetSystemMasterVolumeAsync()',
    'GetVolumeAsync(string sourceAppUserModelId, string sourceDisplayName)',
    'MusicVolumeNativeBackend.GetSystemVolume()',
    'MusicVolumeNativeBackend.GetSnapshot('
)
$stage5B3AMissingProductPatterns = @(
    foreach ($pattern in $stage5B3ARequiredProductPatterns) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3ASourceFiles[2])::$pattern"
        }
    }
)
$stage5B3ARequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'DESKBOX_MUSIC_VOLUME_BACKEND',
    'previousMusicReadSmoke',
    'previousShortcutSmoke',
    'previousShellSmoke',
    'previousMutationSmoke',
    'previousMusicBackend',
    'start-aot-preview.ps1',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'executableSha256',
    'rustNativeSha256',
    'nativeSystemVolumeBefore',
    'nativeSystemVolumeAfter',
    'nativeSnapshotAttemptedPhases',
    'Stop-ExactPreviewProcess',
    'Production data changed',
    'session.json'
)
$stage5B3AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B3ARequiredSmokeScriptPatterns) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3ASourceFiles[4])::$pattern"
        }
    }
)
$stage5B3AUnsafeMutationPatterns = @(
    foreach ($pattern in @(
            'TrySetSystemMasterVolumeAsync',
            'TrySetSessionVolumeAsync',
            'MusicVolumeNativeBackend.SetSystemVolume',
            'MusicVolumeNativeBackend.SetSessionVolume')) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B3AUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'Directory.Delete(dataPaths.RootPath')) {
        if ($stage5B3ASources[$stage5B3ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B3ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotMusicVolumeReadSmoke|MusicVolumeService|MusicVolumeNativeBackend|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B3AExpectedWmc1510Count = 1241
$stage5B3AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B3BSourceFiles = @(
    "src/DeskBox/App.AotMusicVolumeMutationSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Services/MusicVolumeService.cs",
    "src/DeskBox/Helpers/MusicVolumeNativeBackend.cs",
    "scripts/run-aot-music-volume-mutation-smoke.ps1"
)
$stage5B3BSources = [ordered]@{}
foreach ($sourceFile in $stage5B3BSourceFiles) {
    $stage5B3BSources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B3BRequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE',
    'AotMusicVolumeMutationSmokeScenario.ChangeRestore',
    'AotMusicVolumeMutationSmokeScenario.ChangeThenFail',
    'AotMusicVolumeMutationSmokeScenario.ChangeThenAwaitExternalRecovery',
    'AotMusicVolumeMutationSmokeScenario.RecoverOriginal',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'IsDevelopmentRoot',
    'RefusedNonPreviewRoot',
    'aot-music-volume-mutation-smoke',
    'recovery-intent.json',
    'PersistAndReadBackMusicVolumeRecoveryIntent',
    'ReadMusicVolumeMutationJson(',
    'WriteMusicVolumeMutationJsonAtomically(',
    'new MusicVolumeService()',
    'TrySetSystemMasterVolumeAsync',
    'MusicVolumeNativeBackend.GetSystemVolume()',
    'AwaitingExternalRecovery',
    'Timeout.InfiniteTimeSpan',
    'intentional-after-system-volume-change',
    'RestoreOriginalMusicVolumeAsync',
    'recovery-original-verified',
    'File.Delete(recoveryIntentPath)',
    'RecoveryIntentPreserved',
    'OperationHResult',
    'AttemptedPhases',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'RuntimeFeature.IsDynamicCodeSupported',
    'AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeMutationSmokeResult',
    'AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent'
)
$stage5B3BMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B3BRequiredRunnerPatterns) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3BSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3BRequiredLaunchPatterns = @(
    'Log("OnLaunched completed successfully");',
    'StartAotMusicVolumeMutationSmokeIfRequested();'
)
$stage5B3BMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B3BRequiredLaunchPatterns) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3BSourceFiles[1])::$pattern"
        }
    }
)
$stage5B3BLaunchCompletedIndex = $stage5B3BSources[$stage5B3BSourceFiles[1]].IndexOf(
    $stage5B3BRequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B3BLaunchSmokeIndex = $stage5B3BSources[$stage5B3BSourceFiles[1]].IndexOf(
    $stage5B3BRequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B3BLaunchOrderValid =
    $stage5B3BLaunchCompletedIndex -ge 0 -and
    $stage5B3BLaunchSmokeIndex -gt $stage5B3BLaunchCompletedIndex
$stage5B3BRequiredProductPatterns = @(
    'TrySetSystemMasterVolumeAsync(double volume)',
    'TrySetSystemMasterVolumeCore(volume)',
    'MusicVolumeNativeBackend.SetSystemVolume(volume)'
)
$stage5B3BMissingProductPatterns = @(
    foreach ($pattern in $stage5B3BRequiredProductPatterns) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3BSourceFiles[2])::$pattern"
        }
    }
)
$stage5B3BRequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'DESKBOX_MUSIC_VOLUME_BACKEND',
    'previousMusicMutationSmoke',
    'previousMusicReadSmoke',
    'previousShortcutSmoke',
    'previousShellSmoke',
    'previousMutationSmoke',
    'previousMusicBackend',
    'ChangeRestore',
    'ChangeThenFail',
    'ChangeThenAwaitExternalRecovery',
    'RecoverOriginal',
    'recovery-intent.json',
    'preflight',
    'in-process-failure',
    'forced-termination',
    'recovery',
    'postflight',
    'Stop-ExactPreviewProcess',
    'Get-ExactPreviewProcesses',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'executableSha256',
    'rustNativeSha256',
    'Original system volume',
    'session.json'
)
$stage5B3BMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B3BRequiredSmokeScriptPatterns) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3BSourceFiles[4])::$pattern"
        }
    }
)
$stage5B3BUnsafeMutationPatterns = @(
    foreach ($pattern in @(
            'MusicVolumeNativeBackend.SetSystemVolume',
            'TrySetSessionVolumeAsync',
            'MusicVolumeNativeBackend.SetSessionVolume')) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3BSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3BUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'Directory.Delete(dataPaths.RootPath')) {
        if ($stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3BSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3BPersistIndex = $stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
    'PersistAndReadBackMusicVolumeRecoveryIntent(',
    [StringComparison]::Ordinal)
$stage5B3BProbeSetterIndex = $stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
    'TrySetSystemMasterVolumeAsync',
    $stage5B3BPersistIndex,
    [StringComparison]::Ordinal)
$stage5B3BRecoveryVerifiedIndex = $stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
    'recovery-original-verified',
    [StringComparison]::Ordinal)
$stage5B3BIntentDeleteIndex = $stage5B3BSources[$stage5B3BSourceFiles[0]].IndexOf(
    'File.Delete(recoveryIntentPath)',
    [StringComparison]::Ordinal)
$stage5B3BRecoveryOrderValid =
    $stage5B3BPersistIndex -ge 0 -and
    $stage5B3BProbeSetterIndex -gt $stage5B3BPersistIndex -and
    $stage5B3BRecoveryVerifiedIndex -gt $stage5B3BProbeSetterIndex -and
    $stage5B3BIntentDeleteIndex -gt $stage5B3BRecoveryVerifiedIndex
$stage5B3BSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotMusicVolumeMutationSmoke|MusicVolumeService|MusicVolumeNativeBackend|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B3BExpectedWmc1510Count = 1241
$stage5B3BActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B3CSourceFiles = @(
    "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Services/MusicVolumeService.cs",
    "src/DeskBox/Helpers/MusicVolumeNativeBackend.cs",
    "scripts/run-aot-music-volume-session-mutation-smoke.ps1",
    "native/deskbox-audio-session-fixture/src/main.rs",
    "native/deskbox-audio-session-fixture/Cargo.toml"
)
$stage5B3CSources = [ordered]@{}
foreach ($sourceFile in $stage5B3CSourceFiles) {
    $stage5B3CSources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B3CRequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_SESSION_FIXTURE_PID',
    'deskbox-audio-session-fixture',
    'RefusedUntrustedFixture',
    'AotMusicVolumeSessionMutationSmokeScenario.ReadMatchedSession',
    'AotMusicVolumeSessionMutationSmokeScenario.ChangeRestore',
    'AotMusicVolumeSessionMutationSmokeScenario.ChangeThenFail',
    'AotMusicVolumeSessionMutationSmokeScenario.ChangeThenAwaitExternalRecovery',
    'AotMusicVolumeSessionMutationSmokeScenario.RecoverOriginal',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'RefusedNonPreviewRoot',
    'aot-music-volume-session-mutation-smoke',
    'session-recovery-intent.json',
    'PersistAndReadBackMusicVolumeSessionRecoveryIntent',
    'ReadMusicVolumeSessionMutationJson(',
    'WriteMusicVolumeSessionMutationJsonAtomically(',
    'new MusicVolumeService()',
    'GetVolumeAsync(',
    'TrySetSessionVolumeAsync(',
    'MusicVolumeNativeBackend.GetSnapshot(',
    'ExpectedSessionMatchKind',
    'AwaitingExternalRecovery',
    'Timeout.InfiniteTimeSpan',
    'intentional-after-session-volume-change',
    'RestoreOriginalMusicVolumeSessionAsync',
    'recovery-original-session-verified',
    'session-disappeared-intent-preserved',
    'system-volume-unchanged',
    'File.Delete(recoveryIntentPath)',
    'RecoveryIntentPreserved',
    'OperationHResult',
    'AttemptedPhases',
    'MatchKind',
    'ShortcutNativeBackend.CaptureDiagnosticState()',
    'RuntimeFeature.IsDynamicCodeSupported',
    'AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionMutationSmokeResult',
    'AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent'
)
$stage5B3CMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B3CRequiredRunnerPatterns) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3CSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3CRequiredLaunchPatterns = @(
    'StartAotMusicVolumeMutationSmokeIfRequested();',
    'StartAotMusicVolumeSessionMutationSmokeIfRequested();'
)
$stage5B3CMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B3CRequiredLaunchPatterns) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3CSourceFiles[1])::$pattern"
        }
    }
)
$stage5B3CSystemSmokeIndex = $stage5B3CSources[$stage5B3CSourceFiles[1]].IndexOf(
    $stage5B3CRequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B3CSessionSmokeIndex = $stage5B3CSources[$stage5B3CSourceFiles[1]].IndexOf(
    $stage5B3CRequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B3CLaunchOrderValid =
    $stage5B3CSystemSmokeIndex -ge 0 -and
    $stage5B3CSessionSmokeIndex -gt $stage5B3CSystemSmokeIndex
$stage5B3CRequiredProductPatterns = @(
    'GetVolumeAsync(string sourceAppUserModelId, string sourceDisplayName)',
    'TrySetSessionVolumeAsync(string sourceAppUserModelId, string sourceDisplayName, double volume)',
    'MusicVolumeNativeBackend.GetSnapshot(',
    'MusicVolumeNativeBackend.SetSessionVolume('
)
$stage5B3CMissingProductPatterns = @(
    foreach ($pattern in $stage5B3CRequiredProductPatterns) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3CSourceFiles[2])::$pattern"
        }
    }
)
$stage5B3CRequiredFixturePatterns = @(
    'deskbox-audio-session-fixture',
    'PlaySoundW',
    'SND_ASYNC',
    'SND_LOOP',
    'SND_FILENAME',
    'write_silent_wave',
    'wave.resize((44 + data_length) as usize, 0)',
    '--parent-pid',
    'PROCESS_SYNCHRONIZE',
    'WaitForSingleObject',
    '--ready',
    '--stop'
)
$stage5B3CMissingFixturePatterns = @(
    foreach ($pattern in $stage5B3CRequiredFixturePatterns) {
        $fixtureSource = if ($pattern -eq 'deskbox-audio-session-fixture') {
            $stage5B3CSources[$stage5B3CSourceFiles[6]]
        }
        else {
            $stage5B3CSources[$stage5B3CSourceFiles[5]]
        }
        if ($fixtureSource.IndexOf($pattern, [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B3CRequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE',
    'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
    'DESKBOX_AOT_SHORTCUT_SMOKE',
    'DESKBOX_AOT_SHELL_SMOKE',
    'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
    'previousMusicSessionMutationSmoke',
    'previousMusicMutationSmoke',
    'previousMusicReadSmoke',
    'previousShortcutSmoke',
    'previousShellSmoke',
    'previousMutationSmoke',
    'deskbox-audio-session-fixture.exe',
    '--package deskbox-audio-session-fixture',
    '-WindowStyle Hidden',
    'fixtureProcess.Id',
    'ReadMatchedSession',
    'ChangeRestore',
    'ChangeThenFail',
    'ChangeThenAwaitExternalRecovery',
    'RecoverOriginal',
    'session-recovery-intent.json',
    'preflight',
    'in-process-failure',
    'forced-termination',
    'recovery',
    'postflight',
    'Stop-ExactPreviewProcess',
    'Stop-ExactFixtureProcess',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'executableSha256',
    'rustNativeSha256',
    'fixtureExecutableSha256',
    'systemVolume',
    'session.json'
)
$stage5B3CMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B3CRequiredSmokeScriptPatterns) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B3CSourceFiles[4])::$pattern"
        }
    }
)
$stage5B3CUnsafeMutationPatterns = @(
    foreach ($pattern in @(
            'MusicVolumeNativeBackend.SetSessionVolume',
            'TrySetSystemMasterVolumeAsync',
            'MusicVolumeNativeBackend.SetSystemVolume')) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3CSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3CUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'Path.GetTempPath(',
            'UseProductionData',
            'Directory.Delete(dataPaths.RootPath')) {
        if ($stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B3CSourceFiles[0])::$pattern"
        }
    }
)
$stage5B3CUnsafeFixtureScriptPatterns = @(
    foreach ($pattern in @(
            'Stop-Process -Name',
            'Get-Process | Stop-Process',
            'SND_ALIAS')) {
        $combinedFixtureAndScript =
            $stage5B3CSources[$stage5B3CSourceFiles[4]] +
            $stage5B3CSources[$stage5B3CSourceFiles[5]]
        if ($combinedFixtureAndScript.IndexOf(
                $pattern,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "fixture-or-script::$pattern"
        }
    }
)
$stage5B3CPersistIndex = $stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
    'PersistAndReadBackMusicVolumeSessionRecoveryIntent(',
    [StringComparison]::Ordinal)
$stage5B3CProbeSetterIndex = if ($stage5B3CPersistIndex -ge 0) {
    $stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
        'TrySetSessionVolumeAsync(',
        $stage5B3CPersistIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$stage5B3CRecoveryVerifiedIndex = $stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
    'recovery-original-session-verified',
    [StringComparison]::Ordinal)
$stage5B3CIntentDeleteIndex = $stage5B3CSources[$stage5B3CSourceFiles[0]].IndexOf(
    'File.Delete(recoveryIntentPath)',
    [StringComparison]::Ordinal)
$stage5B3CRecoveryOrderValid =
    $stage5B3CPersistIndex -ge 0 -and
    $stage5B3CProbeSetterIndex -gt $stage5B3CPersistIndex -and
    $stage5B3CRecoveryVerifiedIndex -gt $stage5B3CProbeSetterIndex -and
    $stage5B3CIntentDeleteIndex -gt $stage5B3CRecoveryVerifiedIndex
$stage5B3CSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotMusicVolumeSessionMutationSmoke|MusicVolumeService|MusicVolumeNativeBackend|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B3CExpectedWmc1510Count = 1241
$stage5B3CActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Views/SettingsWindow.AotSmoke.cs",
    "src/DeskBox/Views/SearchPopupWindow.AotSmoke.cs",
    "src/DeskBox/Services/LocalizationService.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/start-aot-preview.ps1",
    "src/DeskBox/Views/SettingsWindow.Navigation.cs"
)
$stage5B4ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4ASourceFiles) {
    $stage5B4ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4ARequiredRunnerPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_MANAGED_UI_SMOKE',
    'BasicReadOnly',
    'DeskBoxDataPathService.AotPreviewRootEnvironmentVariable',
    'RefusedNonPreviewRoot',
    'aot-managed-ui-smoke',
    'aot-5b4a-file',
    'aot-5b4a-search',
    '_trayIcon.TrayIcon.WindowHandle',
    'WidgetManager.CreateDiagnosticsSnapshot()',
    'WidgetKind.File',
    'WidgetKind.Search',
    'LocalizationService.CaptureAotSmokeResourceDiagnostics()',
    'ShowSettings(sectionTag)',
    'CaptureAotSmokeSnapshot()',
    'Search.Action.OpenSettings',
    'OpenSearchPopupWithQuery(searchQuery)',
    'WaitForManagedUiSearchAsync',
    'ExerciseAotReadOnlyControls()',
    'SearchCompleted',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult',
    'RuntimeFeature.IsDynamicCodeSupported'
)
$stage5B4AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4ARequiredRunnerPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B4ARequiredLaunchPatterns = @(
    'StartAotMusicVolumeSessionMutationSmokeIfRequested();',
    'StartAotManagedUiSmokeIfRequested();'
)
$stage5B4AMissingLaunchPatterns = @(
    foreach ($pattern in $stage5B4ARequiredLaunchPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[1])::$pattern"
        }
    }
)
$stage5B4ANativeBoundarySmokeIndex = $stage5B4ASources[$stage5B4ASourceFiles[1]].IndexOf(
    $stage5B4ARequiredLaunchPatterns[0],
    [StringComparison]::Ordinal)
$stage5B4AManagedUiSmokeIndex = $stage5B4ASources[$stage5B4ASourceFiles[1]].IndexOf(
    $stage5B4ARequiredLaunchPatterns[1],
    [StringComparison]::Ordinal)
$stage5B4ALaunchOrderValid =
    $stage5B4ANativeBoundarySmokeIndex -ge 0 -and
    $stage5B4AManagedUiSmokeIndex -gt $stage5B4ANativeBoundarySmokeIndex
$stage5B4ARequiredSettingsPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'WindowNative.GetWindowHandle(this)',
    '_appWindow.IsVisible',
    'SettingsRoot.XamlRoot',
    '_currentSettingsSection',
    '_settingsSectionElements',
    'SettingsNavigationView.SelectedItem'
)
$stage5B4AMissingSettingsPatterns = @(
    foreach ($pattern in $stage5B4ARequiredSettingsPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[2])::$pattern"
        }
    }
)
$stage5B4ARequiredSettingsNavigationPatterns = @(
    'SettingsSearchBox.ItemsSource = null;',
    'SettingsSearchBox.IsSuggestionListOpen = false;'
)
$stage5B4AMissingSettingsNavigationPatterns = @(
    foreach ($pattern in $stage5B4ARequiredSettingsNavigationPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[7]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[7])::$pattern"
        }
    }
)
$stage5B4AUnsafeSettingsNavigationPatterns = @(
    foreach ($pattern in @(
            'SettingsSearchBox.ItemsSource = Array.Empty<SettingsSearchResult>();')) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[7]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B4ASourceFiles[7])::$pattern"
        }
    }
)
$stage5B4ARequiredSearchPatterns = @(
    'ResultFilterComboBox.SelectedItem',
    '"All"',
    '"FilesAndFolders"',
    '"Apps"',
    '"Images"',
    '"Documents"',
    '"DeskBox"',
    'ResultFilterBar.Visibility',
    'SortHeaderRow.Visibility',
    'ActionId == "open-settings"'
)
$stage5B4AMissingSearchPatterns = @(
    foreach ($pattern in $stage5B4ARequiredSearchPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[3])::$pattern"
        }
    }
)
$stage5B4ASortHandlerCountViolations = @(
    foreach ($handler in @(
            'SortNameHeader_Click(',
            'SortSizeHeader_Click(',
            'SortDateHeader_Click(',
            'SortTypeHeader_Click(')) {
        $actualCount = [regex]::Matches(
            $stage5B4ASources[$stage5B4ASourceFiles[3]],
            [regex]::Escape($handler)).Count
        if ($actualCount -ne 2) {
            "$handler expected=2 actual=$actualCount"
        }
    }
)
$stage5B4ARequiredLocalePatterns = @(
    'CaptureAotSmokeResourceDiagnostics',
    '"zh-CN"',
    '"zh-TW"',
    '"en-US"',
    '"ja-JP"',
    '"de-DE"',
    '"pt-BR"',
    '"hi-IN"',
    '"es-ES"',
    '"fr-FR"',
    '"ar-SA"',
    '"bn-BD"',
    '"ru-RU"',
    'Window.Settings.Title',
    'Search.Action.OpenSettings'
)
$stage5B4AMissingLocalePatterns = @(
    foreach ($pattern in $stage5B4ARequiredLocalePatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[4])::$pattern"
        }
    }
)
$stage5B4ARequiredSmokeScriptPatterns = @(
    'DESKBOX_AOT_MANAGED_UI_SMOKE',
    'BasicReadOnly',
    'aot-5b4a-file',
    'aot-5b4a-search',
    'HasCompletedOnboarding',
    'FeatureWidgetEnabledStates',
    'SearchSaveHistory',
    'Get-DirectoryStateFingerprint',
    'productionDataFingerprintBefore',
    'Stop-ExactPreviewProcess',
    'settingsSections',
    'filterTransitions',
    'sortTransitions',
    'session.json',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4ARequiredSmokeScriptPatterns) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[5]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4ASourceFiles[5])::$pattern"
        }
    }
)
$stage5B4ASmokeScriptFiles = @(
    'scripts/run-aot-shortcut-smoke.ps1',
    'scripts/run-aot-shell-smoke.ps1',
    'scripts/run-aot-quick-access-mutation-smoke.ps1',
    'scripts/run-aot-music-volume-read-smoke.ps1',
    'scripts/run-aot-music-volume-mutation-smoke.ps1',
    'scripts/run-aot-music-volume-session-mutation-smoke.ps1',
    'scripts/run-aot-managed-ui-smoke.ps1'
)
$stage5B4AMissingSmokeOptInIsolation = @(
    foreach ($smokeScriptFile in $stage5B4ASmokeScriptFiles) {
        $smokeScriptSource = Get-Content -LiteralPath (Join-Path $repoRoot $smokeScriptFile) -Raw
        foreach ($pattern in @(
                'DESKBOX_AOT_SHORTCUT_SMOKE',
                'DESKBOX_AOT_SHELL_SMOKE',
                'DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE',
                'DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE',
                'DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE',
                'DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE',
                'DESKBOX_AOT_MANAGED_UI_SMOKE',
                'previousManagedUiSmoke')) {
            if ($smokeScriptSource.IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -lt 0) {
                "$($smokeScriptFile)::$pattern"
            }
        }
    }
)
$stage5B4AUnsafeRunnerPatterns = @(
    foreach ($pattern in @(
            'CreateWidgetOfKindAsync',
            'CreateManagedWidgetAsync',
            'SettingsService.Save',
            'RecordQuery',
            'OpenSelected',
            'SetLanguage(')) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B4ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B4AUnsafeSmokeScriptPatterns = @(
    foreach ($pattern in @(
            'UseProductionData',
            'Stop-Process -Name',
            'Get-Process | Stop-Process')) {
        if ($stage5B4ASources[$stage5B4ASourceFiles[5]].IndexOf(
                $pattern,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "$($stage5B4ASourceFiles[5])::$pattern"
        }
    }
)
$stage5B4AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4ASources[$stage5B4ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotManagedUiSmoke|SettingsWindow\.AotSmoke|SearchPopupWindow\.AotSmoke|LocalizationService|App\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4AExpectedWmc1510Count = 1241
$stage5B4AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs",
    "src/DeskBox/Views/SettingsWindow.Navigation.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/start-aot-preview.ps1",
    "src/DeskBox/Views/SettingsWindow.xaml.cs",
    "src/DeskBox/Views/SettingsWindow.Maintenance.cs",
    "src/DeskBox/ViewModels/FileStackCustomRuleEditor.cs",
    "src/DeskBox/Views/SettingsWindow.xaml",
    "src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs",
    "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml",
    "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml.cs",
    "src/DeskBox/Models/SettingsOption.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.CapsuleOptions.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.GroupNavigation.cs",
    "src/DeskBox/Models/WeatherData.cs",
    "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml",
    "src/DeskBox/ViewModels/SettingsViewModel.FileStackOptions.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.SelectionOptions.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.WeatherOptions.cs",
    "src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs"
)
$stage5B4B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4B1SourceFiles) {
    $stage5B4B1Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B1RequiredRunnerPatterns = @(
    'DeepSettingsReadOnly',
    'CaptureAotManagedUiDeepSettingsAsync',
    'ExerciseAotDeepReadOnlySettingsAsync',
    'AotManagedUiDeepSettingsEvidence',
    'SearchSuggestions',
    'PageTransitions',
    'FileStackRuleCount',
    'BackupSnapshotCount',
    'DeepSettingsCompleted',
    'result.DeepSettings.PageTransitions.Count == 24',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredRunnerPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[0])::$pattern"
        }
    }
)
$stage5B4B1RequiredSettingsPatterns = @(
    'ExerciseAotDeepReadOnlySettingsAsync',
    'Settings.DataBackup.Title',
    'UpdateSettingsSearchSuggestions(searchQuery)',
    'SettingsSearchBox.ItemsSource',
    'ActivateSettingsSearchResult',
    'NavigateToSettingsSection(sectionTag)',
    'SettingsNavigationView.SelectedItem',
    'SettingsBreadcrumbBar.ItemsSource',
    'NavigateFromSettingsBreadcrumbItem',
    'BreadcrumbParentReturned',
    'WaitForAotFileStackRuleProjectionAsync',
    'WaitForAotBackupSnapshotProjectionAsync',
    'DeepSettings route begin',
    'DeepSettings route completed'
)
$stage5B4B1MissingSettingsPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredSettingsPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[1])::$pattern"
        }
    }
)
$stage5B4B1RequiredNavigationPatterns = @(
    'ActivateSettingsSearchResult(result, sender)',
    'NavigateToSettingsSection(result.SectionTag)',
    'ScheduleSettingsSearchTarget(result)',
    'NavigateFromSettingsBreadcrumbItem(item)',
    'matches.Cast<object>().ToArray()',
    'SettingsBreadcrumbBar.ItemsSource = new object[]'
)
$stage5B4B1MissingNavigationPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredNavigationPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B1RequiredProjectionPatterns = @(
    '[WinRT.GeneratedBindableCustomProperty]',
    'private sealed partial record SettingsBreadcrumbItem',
    'private sealed partial record SettingsSearchResult',
    'private sealed partial record BackupSnapshotListItem',
    'CapsuleModeSection.ViewModel = ViewModel'
)
$stage5B4B1MissingProjectionPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredProjectionPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[5]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[5])::$pattern"
        }
    }
)
$stage5B4B1RequiredInventoryPatterns = @(
    'BackupSnapshotsList.ItemsSource = rows.Cast<object>().ToArray()',
    'BackupSnapshotsList.ItemsSource = null'
)
$stage5B4B1MissingInventoryPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredInventoryPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[6]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[6])::$pattern"
        }
    }
)
$stage5B4B1RequiredBindableTypePatterns = @(
    [ordered]@{
        file = $stage5B4B1SourceFiles[7]
        patterns = @(
            '[WinRT.GeneratedBindableCustomProperty]',
            'public partial class FileStackCustomRuleEditor')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[9]
        patterns = @(
            '#if DESKBOX_NATIVE_AOT',
            '[WinRT.GeneratedBindableCustomProperty([',
            'nameof(SelectedWidgetCapsuleBarPlacement)',
            'public partial class SettingsViewModel')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[12]
        patterns = @(
            '[WinRT.GeneratedBindableCustomProperty]',
            'public sealed partial class SettingsOption')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[13]
        patterns = @(
            '[WinRT.GeneratedBindableCustomProperty]',
            'public sealed partial record CapsuleOverrideSettingsItem')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[14]
        patterns = @(
            '[WinRT.GeneratedBindableCustomProperty]',
            'public sealed partial record WidgetGroupSettingsItem',
            'public sealed partial record WidgetGroupMemberSettingsItem')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[15]
        patterns = @(
            '[WinRT.GeneratedBindableCustomProperty]',
            'public sealed partial class WeatherCitySearchResult')
    }
)
$stage5B4B1MissingBindableTypePatterns = @(
    foreach ($entry in $stage5B4B1RequiredBindableTypePatterns) {
        foreach ($pattern in $entry.patterns) {
            if ($stage5B4B1Sources[$entry.file].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -lt 0) {
                "$($entry.file)::$pattern"
            }
        }
    }
)
$stage5B4B1ExpectedBindableViewModelPropertyCount = 305
$stage5B4B1ActualBindableViewModelPropertyCount = [regex]::Matches(
    $stage5B4B1Sources[$stage5B4B1SourceFiles[9]],
    [regex]::Escape('nameof(')).Count
$stage5B4B1UnsafeBindableViewModelPatterns = @(
    foreach ($pattern in @(
            'nameof(ResetAllCapsuleOverridesCommand)',
            'nameof(ResetCapsuleWidthOverridesCommand)',
            'nameof(ResetDisplayWidgetChromeOverridesCommand)',
            'nameof(ResetInteractiveWidgetChromeOverridesCommand)',
            'nameof(SaveCommand)')) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[9]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            $pattern
        }
    }
)
$stage5B4B1RequiredFileStackXamlPatterns = @(
    'ItemsSource="{x:Bind ViewModel.FileStackCustomRules, Mode=OneWay}"'
)
$stage5B4B1RequiredCommandXamlPatterns = @(
    'Command="{x:Bind ViewModel.ResetDisplayWidgetChromeOverridesCommand, Mode=OneWay}"',
    'Command="{x:Bind ViewModel.ResetInteractiveWidgetChromeOverridesCommand, Mode=OneWay}"',
    'Command="{x:Bind ViewModel.ResetAllCapsuleOverridesCommand, Mode=OneWay}"'
)
$stage5B4B1MissingCommandXamlPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredCommandXamlPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[8]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[8])::$pattern"
        }
    }
)
$stage5B4B1RequiredCapsuleCommandXamlPatterns = @(
    'Command="{x:Bind ViewModel.ResetCapsuleWidthOverridesCommand, Mode=OneWay}"'
)
$stage5B4B1MissingCapsuleCommandXamlPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredCapsuleCommandXamlPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[10]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[10])::$pattern"
        }
    }
)
$stage5B4B1RequiredCapsuleCodeBehindPatterns = @(
    'DependencyProperty ViewModelProperty',
    'public SettingsViewModel? ViewModel'
)
$stage5B4B1MissingCapsuleCodeBehindPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredCapsuleCodeBehindPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[11]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[11])::$pattern"
        }
    }
)
$stage5B4B1MissingFileStackXamlPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredFileStackXamlPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[8]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[8])::$pattern"
        }
    }
)
$stage5B4B1RequiredFileWidgetProjectionPatterns = @(
    [ordered]@{
        file = $stage5B4B1SourceFiles[16]
        patterns = @(
            'IsOn="{x:Bind ViewModel.FileStacksEnabled, Mode=TwoWay}"',
            'ItemsSource="{x:Bind ViewModel.AvailableFileWidgetFolderOpenBehaviorOptionItems, Mode=OneWay}"')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[17]
        patterns = @(
            'public bool FileStacksEnabled',
            'SetProperty(ref _fileStacksEnabled, value)',
            '_settingsService.Settings.FileStacksEnabled = value;')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[18]
        patterns = @(
            'public object[] AvailableFileWidgetFolderOpenBehaviorOptionItems',
            'AvailableFileWidgetFolderOpenBehaviorOptions.Cast<object>().ToArray()')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[19]
        patterns = @(
            'OnPropertyChanged(nameof(AvailableFileWidgetFolderOpenBehaviorOptionItems))')
    }
)
$stage5B4B1MissingFileWidgetProjectionPatterns = @(
    foreach ($entry in $stage5B4B1RequiredFileWidgetProjectionPatterns) {
        foreach ($pattern in $entry.patterns) {
            if ($stage5B4B1Sources[$entry.file].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -lt 0) {
                "$($entry.file)::$pattern"
            }
        }
    }
)
$stage5B4B1RequiredWeatherProjectionPatterns = @(
    [ordered]@{
        file = $stage5B4B1SourceFiles[20]
        patterns = @(
            'ObservableCollection<WeatherCitySearchResult> WeatherCitySuggestions',
            'public object[] WeatherCitySuggestionItems',
            'WeatherCitySuggestions.Cast<object>().ToArray()',
            'RefreshWeatherCitySuggestionItems()',
            'WeatherCitySuggestions.Add(')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[21]
        patterns = @(
            'WeatherCitySuggestions[0]')
    },
    [ordered]@{
        file = $stage5B4B1SourceFiles[8]
        patterns = @(
            'ItemsSource="{Binding WeatherCitySuggestionItems}"')
    }
)
$stage5B4B1MissingWeatherProjectionPatterns = @(
    foreach ($entry in $stage5B4B1RequiredWeatherProjectionPatterns) {
        foreach ($pattern in $entry.patterns) {
            if ($stage5B4B1Sources[$entry.file].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -lt 0) {
                "$($entry.file)::$pattern"
            }
        }
    }
)
$stage5B4B1RoutePatterns = @(
    '"AppearanceDetail"',
    '"CapsuleMode"',
    '"WidgetGroups"',
    '"FileDisplaySettings"',
    '"ManagedStorage"',
    '"FileStackSettings"',
    '"DesktopOrganizationSettings"',
    '"QuickCaptureSettings"',
    '"TodoSettings"',
    '"MusicSettings"',
    '"WeatherSettings"',
    '"GlanceSettings"',
    '"SearchSettings"',
    '"AppearanceMaterialSettings"',
    '"AppearanceDensitySettings"',
    '"AppearanceWindowSettings"',
    '"AppearanceAnimationSettings"',
    '"CapsuleBehaviorSettings"',
    '"CapsuleArrangementSettings"',
    '"CapsuleAnimationSettings"',
    '"CapsuleOverridesSettings"',
    '"BackupRestoreSettings"',
    '"DataHealthSettings"',
    '"CompatibilityDiagnosticsSettings"'
)
$stage5B4B1MissingRoutePatterns = @(
    foreach ($pattern in $stage5B4B1RoutePatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[1])::$pattern"
        }
    }
)
$stage5B4B1RequiredSmokeScriptPatterns = @(
    '"DeepSettingsReadOnly"',
    '"GlancePersistenceRestart"',
    'deep-settings-read-only',
    'productionDataFingerprintBefore',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json',
    'deepSettings',
    'pageTransitions',
    'searchSuggestions',
    'aot-5b4b1-design',
    'fileStackRuleCount',
    'backupSnapshotCount',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    '[DataBackup] Snapshot inventory failed:',
    'DeepSettingsPageCount',
    'DeepSettingsSuggestionCount'
)
$stage5B4B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B1RequiredSmokeScriptPatterns) {
        if ($stage5B4B1Sources[$stage5B4B1SourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B1SourceFiles[3])::$pattern"
        }
    }
)
$stage5B4B1UnsafeMutationPatterns = @(
    foreach ($sourceFile in $stage5B4B1SourceFiles[0..3]) {
        foreach ($pattern in @(
                'SettingsService.Save',
                'CreateWidget',
                'DeleteWidget',
                'DeepSettingsReadOnlyMutation',
                'Stop-Process -Name',
                'UseProductionData')) {
            if ($stage5B4B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4B1Sources[$stage5B4B1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotManagedUiSmoke|SettingsWindow\.(?:AotDeepSmoke|Navigation|Maintenance|HotkeyAndAppearance)|SettingsWindow\.xaml|FileStackCustomRuleEditor|SettingsViewModel\.(?:AotBindableProperties|CapsuleOptions|GroupNavigation|FileStackOptions|FeatureOptions|SelectionOptions|WeatherOptions)|(?:CapsuleMode|FileWidget)SettingsSection\.xaml|SettingsOption|WeatherData)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B1ExpectedWmc1510Count = 1241
$stage5B4B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotPersistenceSmoke.cs",
    "src/DeskBox/Views/WidgetWindowBase.AotPersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/start-aot-preview.ps1",
    "src/DeskBox/ViewModels/SettingsViewModel.AppearanceOptions.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.AppearanceCallbacks.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.PreferenceCallbacks.cs",
    "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs",
    "src/DeskBox/Services/SettingsService.cs"
)
$stage5B4B2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2ASourceFiles) {
    $stage5B4B2ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2ARequiredRunnerPatterns = @(
    'SettingsWidgetPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE',
    'AotManagedUiPersistenceMutatePhase',
    'AotManagedUiPersistenceVerifyRestorePhase',
    'AotManagedUiPersistencePostflightPhase',
    'CaptureAotManagedUiPersistenceAsync',
    'settingsWindow.ViewModel',
    'ShowFileExtensions',
    'FileNameLineCount',
    'TextSize',
    'SelectedTrayIconStyle',
    'FlushPendingSaveAsync(',
    'SettingsPersistenceFlushed',
    'ShutdownApplicationAsync()',
    'AotManagedUiPersistenceEvidence',
    'AotManagedUiPersistenceStateEvidence',
    'AotManagedUiPersistenceWidgetEvidence',
    'NormalShutdownRequested',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2ARequiredRunnerPatterns) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[0]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2ASourceFiles[0])::$pattern"
        }
    }
)
$stage5B4B2ARequiredManagerPatterns = @(
    'aot-5b4a-file',
    '_fileWidgets.TryGetValue',
    'ViewModel.ToggleViewMode()',
    'SetWidgetPositionLocked',
    'SetWidgetSizeLocked',
    'ApplyAotPersistenceSmokeBounds',
    'CaptureAotPersistenceWidgetSnapshot',
    'Aot5B4B2ABaselineX',
    'ViewModelName',
    'ViewModelViewMode',
    'WindowHandle',
    'ActualBounds'
)
$stage5B4B2AMissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2ARequiredManagerPatterns) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2ASourceFiles[1])::$pattern"
        }
    }
)
$stage5B4B2ARequiredBoundsPatterns = @(
    'GetActualWindowBounds()',
    'DisplayArea.GetFromRect',
    'MoveWindowWithoutPersisting',
    'CapturePositionAnchor',
    'UpdateConfigBoundsFromPhysical',
    'persist: true'
)
$stage5B4B2AMissingBoundsPatterns = @(
    foreach ($pattern in $stage5B4B2ARequiredBoundsPatterns) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2ASourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2ARequiredSmokeScriptPatterns = @(
    '"SettingsWidgetPersistenceRestart",',
    'DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE',
    'Invoke-PersistencePhase',
    'Wait-NaturalPreviewExit',
    'Assert-PersistenceStateEqual',
    '$mutate.persistence.after',
    '$verifyRestore.persistence.before',
    '$postflight.persistence.before',
    'normalShutdownRequested',
    'previewProcessesAfter',
    'previewRootCleaned',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    'archivedPersistenceSessionPath',
    'sessionPath = $archivedPersistenceSessionPath',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2ARequiredSmokeScriptPatterns) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2ASourceFiles[3])::$pattern"
        }
    }
)
$stage5B4B2ARequiredLauncherPatterns = @(
    '[switch]$AllowEarlyExit',
    '$AllowEarlyExit.IsPresent',
    'running = $startedProcessStillRunning'
)
$stage5B4B2AMissingLauncherPatterns = @(
    foreach ($pattern in $stage5B4B2ARequiredLauncherPatterns) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2ASourceFiles[4])::$pattern"
        }
    }
)
$stage5B4B2AForbiddenScopePatterns = @(
    foreach ($sourceFile in $stage5B4B2ASourceFiles[0..3]) {
        foreach ($pattern in @(
                'QuickCaptureStore',
                'TodoWidgetStore',
                'GlanceWidgetStore',
                'Stop-Process -Name',
                'UseProductionData')) {
            if ($stage5B4B2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
    foreach ($sourceFile in $stage5B4B2ASourceFiles[0..2]) {
        if ($stage5B4B2ASources[$sourceFile].IndexOf(
                'WeatherService',
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "$($sourceFile)::WeatherService"
        }
    }
    foreach ($pattern in @(
            'ShortcutHelper',
            'CreateWidget',
            'RemoveWidget')) {
        if ($stage5B4B2ASources[$stage5B4B2ASourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            "$($stage5B4B2ASourceFiles[1])::$pattern"
        }
    }
)
$stage5B4B2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2ASources[$stage5B4B2ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.AotManagedUiSmoke|WidgetManager\.AotPersistenceSmoke|WidgetWindowBase\.AotPersistenceSmoke|SettingsViewModel\.(?:AppearanceOptions|AppearanceCallbacks|PreferenceCallbacks)|WidgetViewModel\.Operations|SettingsService)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2AExpectedWmc1510Count = 1241
$stage5B4B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2B1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotQuickCapturePersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.AotPersistenceSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotQuickCapturePersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs",
    "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs",
    "src/DeskBox/Services/QuickCaptureService.cs",
    "src/DeskBox/Services/QuickCaptureStore.cs",
    "src/DeskBox/Services/AttachmentStorageService.cs"
)
$stage5B4B2B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2B1SourceFiles) {
    $stage5B4B2B1Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2B1RunnerSource =
    $stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[0]] +
    $stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[1]]
$stage5B4B2B1RequiredRunnerPatterns = @(
    'QuickCapturePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE',
    'AotManagedUiQuickCaptureMutatePhase',
    'AotManagedUiQuickCaptureVerifyDeletePhase',
    'AotManagedUiQuickCapturePostflightPhase',
    'quick-capture-persistence-restart',
    'CaptureAotManagedUiQuickCapturePersistenceAsync',
    'DeskBoxDataPathService.Current',
    'AotManagedUiQuickCapturePersistenceEvidence',
    'AotManagedUiQuickCaptureStateEvidence',
    'AotManagedUiQuickCaptureItemEvidence',
    'AotManagedUiQuickCaptureAttachmentEvidence',
    'AfterExplicitFlush',
    'AfterAttachmentDelete',
    'ManagedAttachmentFileCount',
    'SurfaceItemCount',
    'DetailItemId',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2B1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2B1RequiredRunnerPatterns) {
        if ($stage5B4B2B1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2B1RequiredSurfacePatterns = @(
    'OpenNewDetailAsync',
    'SetDetailEditorText',
    'MarkDetailDirty',
    'HasNewDetailContent',
    'FlushPendingDetailSaveAsync',
    'ScheduleDetailAutoSave',
    '_detailEditRevision',
    '_detailSavedRevision',
    'WaitForAotQuickCaptureAutoSaveAsync',
    'ViewModel.AddAttachmentsAsync',
    'ForceManagedCopy: true',
    'ViewModel.DeleteAttachmentAsync',
    'DeleteQuickCaptureItemAsync',
    'File.Exists',
    'PendingSaveFlushed',
    'AutoSaveObserved'
)
$stage5B4B2B1MissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2B1RequiredSurfacePatterns) {
        if ($stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2B1RequiredProductSurfacePatterns = @(
    'DetailAutoSaveDelayMs = 600',
    'attachments.Cast<object>().ToArray()'
)
$stage5B4B2B1MissingProductSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2B1RequiredProductSurfacePatterns) {
        if ($stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[5]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B1SourceFiles[5])::$pattern"
        }
    }
)
$stage5B4B2B1RequiredManagerPatterns = @(
    'aot-5b4b2b1-quick-capture',
    '_contentWidgets.TryGetValue',
    'window.ContentReadyTask',
    'window.CurrentContent is QuickCaptureSurfaceContent',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'Visible'
)
$stage5B4B2B1MissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2B1RequiredManagerPatterns) {
        if ($stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B1SourceFiles[3])::$pattern"
        }
    }
)
$stage5B4B2B1RequiredSmokeScriptPatterns = @(
    'QuickCapturePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE',
    'Invoke-QuickCapturePersistencePhase',
    'Mutate',
    'VerifyDelete',
    'Postflight',
    'Wait-NaturalPreviewExit',
    'Assert-QuickCaptureStateEqual',
    '$mutate.quickCapturePersistence.after',
    '$verifyDelete.quickCapturePersistence.before',
    '$verifyDelete.quickCapturePersistence.after',
    '$postflight.quickCapturePersistence.before',
    'managedAttachmentRelativePaths',
    'Get-FileSha256',
    'quick-capture-attachment.txt',
    'quickCaptureNaturalExit',
    'quickCapturePreviewProcessesAfter',
    'previewRootCleaned',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    'archivedQuickCaptureSessionPath',
    'sessionPath = $archivedQuickCaptureSessionPath',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2B1RequiredSmokeScriptPatterns) {
        if ($stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B1SourceFiles[4])::$pattern"
        }
    }
)
$stage5B4B2B1ForbiddenScopePatterns = @(
    foreach ($sourceFile in $stage5B4B2B1SourceFiles[1..3]) {
        foreach ($pattern in @(
                'new QuickCaptureStore(',
                'TodoWidgetStore',
                'GlanceWidgetStore',
                'WeatherService',
                'FolderPicker',
                'FileOpenPicker',
                'ShortcutHelper',
                'Launcher.Launch',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.Delete')) {
            if ($stage5B4B2B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
    if ($stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[2]].IndexOf(
            'DetailAutoSaveTimer_Tick(',
            [StringComparison]::Ordinal) -ge 0) {
        "$($stage5B4B2B1SourceFiles[2])::DetailAutoSaveTimer_Tick("
    }
)
$stage5B4B2B1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2B1Sources[$stage5B4B2B1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.Aot(?:ManagedUi|QuickCapturePersistence)Smoke|QuickCaptureSurfaceContent\.(?:AotPersistenceSmoke|xaml)|WidgetManager\.AotQuickCapturePersistenceSmoke|QuickCaptureWidgetViewModel\.Operations|QuickCaptureService|QuickCaptureStore|AttachmentStorageService)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2B1ExpectedWmc1510Count = 1241
$stage5B4B2B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2B2ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotTodoPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs",
    "src/DeskBox/ViewModels/TodoWidgetViewModel.ItemOperations.cs",
    "src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs",
    "src/DeskBox/ViewModels/TodoWidgetViewModel.EditingAndUndo.cs",
    "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs",
    "src/DeskBox/Services/TodoWidgetStore.cs"
)
$stage5B4B2B2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2B2ASourceFiles) {
    $stage5B4B2B2ASources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2B2ARunnerSource =
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[0]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[1]]
$stage5B4B2B2ARequiredRunnerPatterns = @(
    'TodoPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_PHASE',
    'AotManagedUiTodoMutatePhase',
    'AotManagedUiTodoVerifyDeletePhase',
    'AotManagedUiTodoPostflightPhase',
    'todo-persistence-restart',
    'CaptureAotManagedUiTodoPersistenceAsync',
    'DeskBoxDataPathService.Current',
    'AotManagedUiTodoPersistenceEvidence',
    'AotManagedUiTodoStateEvidence',
    'AotManagedUiTodoItemEvidence',
    'AfterExplicitSave',
    'StoreFileExists',
    'SurfaceItemCount',
    'DetailItemId',
    'StepCount',
    'AttachmentCount',
    'HasDueDate',
    'HasRecurrence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2B2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2B2ARequiredRunnerPatterns) {
        if ($stage5B4B2B2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2B2ARequiredSurfacePatterns = @(
    'OpenAddEditorAsync',
    'DetailTitleTextBox.Text',
    'ViewModel.FinalizeDetailAsync',
    'SaveDetailEditorsAsync',
    'AotTodoInitialTitle',
    'AotTodoPersistedTitle',
    'BeginNotesEditingAsync',
    'DetailNotesEditor.Text',
    'ScheduleNotesAutoSave',
    '_notesAutosaveTimer.IsEnabled',
    '_notesOriginalText',
    '_notesSaveGate.CurrentCount',
    'WaitForAotTodoAutoSaveAsync',
    'SaveActiveNotesAsync(keepEditing: false)',
    'SetCompletedWithFeedbackAsync',
    'DeleteItemAsync',
    'OpenDetailItemAsync',
    'ExplicitNotesSaved',
    'CompletionRoundTripObserved'
)
$stage5B4B2B2AMissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2B2ARequiredSurfacePatterns) {
        if ($stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2ASourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2B2ARequiredProductPatterns = @(
    'Interval = TimeSpan.FromMilliseconds(600)',
    'private void ScheduleNotesAutoSave()',
    'private async Task<bool> SaveActiveNotesAsync(bool keepEditing)',
    'public async Task<bool> UpdateNotesAsync(string itemId, string? notes)',
    'public async Task<bool> SetCompletedAsync(string itemId, bool isCompleted)',
    'public async Task<bool> DeleteItemAsync(string itemId)',
    '[WinRT.GeneratedBindableCustomProperty]',
    'partial class TodoWidgetViewModel',
    'partial class TodoItemViewModel',
    'Math.Max(3, data.Version)'
)
$stage5B4B2B2AProductSource =
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[5]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[7]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[8]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[9]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[10]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[11]] +
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[12]]
$stage5B4B2B2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4B2B2ARequiredProductPatterns) {
        if ($stage5B4B2B2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4B2B2ARequiredManagerPatterns = @(
    'aot-5b4b2b2a-todo',
    '_contentWidgets.TryGetValue',
    'window.ContentReadyTask',
    'window.CurrentContent is TodoWidgetContentAdapter',
    'adapter.View is TodoWidgetContent',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'Visible'
)
$stage5B4B2B2AMissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2B2ARequiredManagerPatterns) {
        if ($stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2ASourceFiles[3])::$pattern"
        }
    }
)
$stage5B4B2B2ARequiredSmokeScriptPatterns = @(
    'TodoPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_PHASE',
    'Invoke-TodoPersistencePhase',
    'Mutate',
    'VerifyDelete',
    'Postflight',
    'Wait-NaturalPreviewExit',
    'Assert-TodoStateEqual',
    '$mutate.todoPersistence.after',
    '$verifyDelete.todoPersistence.before',
    '$verifyDelete.todoPersistence.after',
    '$postflight.todoPersistence.before',
    'afterExplicitSave',
    'final-todo.json',
    'todoNaturalExit',
    'todoPreviewProcessesAfter',
    'previewRootCleaned',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    'archivedTodoSessionPath',
    'sessionPath = $archivedTodoSessionPath',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2B2ARequiredSmokeScriptPatterns) {
        if ($stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[4]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2ASourceFiles[4])::$pattern"
        }
    }
)
$stage5B4B2B2AForbiddenScopePatterns = @(
    foreach ($sourceFile in $stage5B4B2B2ASourceFiles[1..3]) {
        foreach ($pattern in @(
                'TodoWidgetStore.SaveAsync',
                'AddStepAsync',
                'SetStepCompletedAsync',
                'AddAttachmentAsync',
                'DeleteAttachmentAsync',
                'SetDueDate',
                'SetRecurrence',
                'GlanceWidgetStore',
                'WeatherService',
                'FolderPicker',
                'FileOpenPicker',
                'ShortcutHelper',
                'Launcher.Launch',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.Delete')) {
            if ($stage5B4B2B2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
    if ($stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[2]].IndexOf(
            'NotesAutosaveTimer_Tick(',
            [StringComparison]::Ordinal) -ge 0) {
        "$($stage5B4B2B2ASourceFiles[2])::NotesAutosaveTimer_Tick("
    }
    if ($stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[2]].IndexOf(
            'new TodoItem',
            [StringComparison]::Ordinal) -ge 0) {
        "$($stage5B4B2B2ASourceFiles[2])::new TodoItem"
    }
)
$stage5B4B2B2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2B2ASources[$stage5B4B2B2ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.Aot(?:ManagedUi|TodoPersistence)Smoke|TodoWidgetContent\.(?:AotPersistenceSmoke|DetailNotesAndSteps|EditingAndUndo|xaml)|WidgetManager\.AotTodoPersistenceSmoke|TodoWidgetViewModel\.(?:ItemOperations|DetailAndAttachments|EditingAndUndo)|TodoViewModels\.AotBindableProperties|TodoWidgetStore)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2B2AExpectedWmc1510Count = 1241
$stage5B4B2B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2B2B1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotTodoStepsPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotStepsPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml",
    "src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs",
    "src/DeskBox/ViewModels/TodoStepViewModel.cs",
    "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs",
    "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "src/DeskBox/App.AotTodoPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs",
    "src/DeskBox/Services/TodoWidgetStore.cs",
    "src/DeskBox/ViewModels/TodoItemViewModel.cs"
)
$stage5B4B2B2B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2B2B1SourceFiles) {
    $stage5B4B2B2B1Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2B2B1RunnerSource =
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[0]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[1]]
$stage5B4B2B2B1RequiredRunnerPatterns = @(
    'TodoStepsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE',
    'AotManagedUiTodoStepsMutatePhase',
    'AotManagedUiTodoStepsVerifyDeletePhase',
    'AotManagedUiTodoStepsPostflightPhase',
    'todo-steps-persistence-restart',
    'aot-5b4b2b2b1-todo-steps',
    'CaptureAotManagedUiTodoStepsPersistenceAsync',
    'await surface.WaitForAotTodoStepProjectionAsync',
    'AotManagedUiTodoStepsPersistenceEvidence',
    'InitialStepUiProjected',
    'StepTextEditObserved',
    'StepCompletionRoundTripObserved',
    'AfterStepMutation',
    'AfterStepDelete',
    'RequireAotManagedUiTodoStepPopulated',
    'RequireAotManagedUiTodoTaskWithoutSteps',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2B2B1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2B2B1RequiredRunnerPatterns) {
        if ($stage5B4B2B2B1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2B2B1RequiredSurfacePatterns = @(
    'OpenAddEditorAsync',
    'DetailTitleTextBox.Text',
    'ViewModel.FinalizeDetailAsync',
    'DetailNewStepTextBox.Text',
    'AddDetailStepAsync',
    'DetailStepsItemsControl.ContainerFromIndex',
    'FindAotTodoStepDescendant<TextBox>',
    'FindAotTodoStepDescendant<CheckBox>',
    'FindAotTodoStepDescendant<Button>',
    'SaveDetailStepTextAsync',
    'SetDetailStepCompletedAsync',
    'DeleteDetailStepAsync',
    'WaitForAotTodoStepRowAsync',
    'WaitForAotTodoStepProjectionAsync',
    'AotTodoStepUiSnapshot',
    'AotTodoStepRowControls',
    'AotTodoPersistedStepText'
)
$stage5B4B2B2B1MissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2B2B1RequiredSurfacePatterns) {
        if ($stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2B2B1ProductSource =
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[3]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[4]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[5]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[6]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[7]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[10]] +
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[13]]
$stage5B4B2B2B1RequiredProductPatterns = @(
    'x:Name="DetailStepsItemsControl"',
    'ItemsSource="{Binding StepItemsSource}"',
    'x:Name="DetailStepCheckBox"',
    'x:Name="DetailStepTextBox"',
    'x:Name="DetailDeleteStepButton"',
    'await SetDetailStepCompletedAsync(checkBox)',
    'await SaveDetailStepTextAsync(textBox)',
    'await DeleteDetailStepAsync(element)',
    'public async Task<TodoStepViewModel?> AddStepAsync',
    'public async Task<bool> SetStepCompletedAsync',
    'public async Task<bool> UpdateStepTextAsync',
    'public async Task<bool> DeleteStepAsync',
    'public object[] StepItemsSource',
    'Steps.Cast<object>().ToArray()',
    'OnPropertyChanged(nameof(StepItemsSource))',
    'CaptureAotManagedUiTodoStateAsync(',
    'string widgetId',
    'new TodoWidgetStore(widgetId)',
    'public sealed partial class TodoStepViewModel',
    'partial class TodoStepViewModel',
    '[WinRT.GeneratedBindableCustomProperty]'
)
$stage5B4B2B2B1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4B2B2B1RequiredProductPatterns) {
        if ($stage5B4B2B2B1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4B2B2B1GeneratedBindableCount = [regex]::Matches(
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[7]],
    [regex]::Escape('[WinRT.GeneratedBindableCustomProperty]')).Count
$stage5B4B2B2B1RequiredManagerPatterns = @(
    'aot-5b4b2b2a-todo',
    'aot-5b4b2b2b1-todo-steps',
    'AotTodoStepsPersistenceOwnedWidgetId',
    '_contentWidgets.TryGetValue',
    'window.ContentReadyTask',
    'window.CurrentContent is TodoWidgetContentAdapter',
    'adapter.View is TodoWidgetContent',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'Visible'
)
$stage5B4B2B2B1MissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2B2B1RequiredManagerPatterns) {
        if ($stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[8]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B1SourceFiles[8])::$pattern"
        }
    }
)
$stage5B4B2B2B1RequiredSmokeScriptPatterns = @(
    'TodoStepsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE',
    'Invoke-TodoStepsPersistencePhase',
    '$mutate.todoStepsPersistence.after',
    '$verifyDelete.todoStepsPersistence.before',
    '$verifyDelete.todoStepsPersistence.afterStepMutation',
    '$verifyDelete.todoStepsPersistence.afterStepDelete',
    '$postflight.todoStepsPersistence.before',
    'todoStepsNaturalExit',
    'todoStepsPreviewProcessesAfter',
    'Sort-Object -Unique',
    'final-todo.json',
    'archivedTodoStepsSessionPath',
    'sessionPath = $archivedTodoStepsSessionPath',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2B2B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2B2B1RequiredSmokeScriptPatterns) {
        if ($stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[9]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B1SourceFiles[9])::$pattern"
        }
    }
)
$stage5B4B2B2B1ForbiddenScopePatterns = @(
    foreach ($sourceFile in $stage5B4B2B2B1SourceFiles[1..2] + $stage5B4B2B2B1SourceFiles[8]) {
        foreach ($pattern in @(
                'TodoWidgetStore.SaveAsync',
                'new TodoStep',
                'AddAttachmentAsync',
                'AddAttachmentPathAsync',
                'DeleteAttachmentAsync',
                'AttachmentStorageService',
                'SetDueDate',
                'SetRecurrence',
                'GlanceWidgetStore',
                'WeatherService',
                'FolderPicker',
                'FileOpenPicker',
                'ShortcutHelper',
                'Launcher.Launch',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.Delete')) {
            if ($stage5B4B2B2B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B2B2B1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2B2B1Sources[$stage5B4B2B2B1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2B2B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.Aot(?:ManagedUi|TodoStepsPersistence|TodoPersistence)Smoke|TodoWidgetContent\.(?:AotStepsPersistenceSmoke|AotPersistenceSmoke|DetailNotesAndSteps)|WidgetManager\.AotTodoPersistenceSmoke|TodoWidgetViewModel\.DetailAndAttachments|TodoItemViewModel|TodoStepViewModel|TodoViewModels\.AotBindableProperties|TodoWidgetStore)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2B2B1ExpectedWmc1510Count = 1241
$stage5B4B2B2B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2B2B2SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotTodoAttachmentsPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotAttachmentsPersistenceSmoke.cs",
    "src/DeskBox/Controls/AttachmentTileStrip.AotSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Attachments.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml",
    "src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs",
    "src/DeskBox/ViewModels/TodoItemViewModel.cs",
    "src/DeskBox/Services/AttachmentStorageService.cs",
    "src/DeskBox/ViewModels/TodoAttachmentViewModel.cs",
    "src/DeskBox/Controls/AttachmentTileStrip.xaml",
    "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs",
    "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "src/DeskBox/App.AotTodoPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs",
    "src/DeskBox/Services/TodoWidgetStore.cs",
    "src/DeskBox/Models/TodoAttachment.cs"
)
$stage5B4B2B2B2Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2B2B2SourceFiles) {
    $stage5B4B2B2B2Sources[$sourceFile] = Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2B2B2RunnerSource =
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[0]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[1]]
$stage5B4B2B2B2RequiredRunnerPatterns = @(
    'TodoAttachmentsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE',
    'AotManagedUiTodoAttachmentsMutatePhase',
    'AotManagedUiTodoAttachmentsVerifyDeletePhase',
    'AotManagedUiTodoAttachmentsPostflightPhase',
    'todo-attachments-persistence-restart',
    'aot-5b4b2b2b2-todo-attachments',
    'bool isTodoAttachmentsPersistence',
    '? AotManagedUiTodoAttachmentsWidgetId',
    'CaptureAotManagedUiTodoAttachmentsPersistenceAsync',
    'await surface.WaitForAotTodoAttachmentProjectionAsync',
    'AotManagedUiTodoAttachmentsPersistenceEvidence',
    'InitialAttachmentUiProjected',
    'RestartAttachmentUiProjected',
    'ManagedAttachmentDeleted',
    'ManagedAttachmentPath',
    'AfterAttachmentDelete',
    'RequireAotManagedUiTodoAttachmentPopulated',
    'RequireAotManagedUiTodoTaskWithoutAttachments',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2B2B2MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredRunnerPatterns) {
        if ($stage5B4B2B2B2RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2B2B2RequiredSurfacePatterns = @(
    'OpenAddEditorAsync',
    'DetailTitleTextBox.Text',
    'ViewModel.FinalizeDetailAsync',
    'ViewModel.AddAttachmentPathAsync',
    'copyToManagedStorageOverride: true',
    'DetailAttachmentStrip.WaitForAotAttachmentTileAsync',
    'WaitForAotTodoAttachmentProjectionAsync',
    'DeleteAotTodoManagedAttachmentAsync',
    'DeleteDetailAttachmentAsync(tile.Attachment)',
    'WaitForAotAttachmentTileEmptyAsync',
    'AotTodoAttachmentMutationResult',
    'AotTodoAttachmentDeleteResult'
)
$stage5B4B2B2B2MissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredSurfacePatterns) {
        if ($stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B2SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2B2B2RequiredTilePatterns = @(
    'AttachmentItems.ContainerFromIndex',
    'FindAotAttachmentDataContext',
    'FindAotAttachmentDescendant<TextBlock>',
    'FindAotAttachmentDescendant<FontIcon>',
    'RemoveAttachmentButton',
    'AutomationProperties.GetName',
    'WaitForAotAttachmentTileAsync',
    'WaitForAotAttachmentTileEmptyAsync',
    'AotAttachmentTileSnapshot',
    'AotAttachmentTileObservation'
)
$stage5B4B2B2B2MissingTilePatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredTilePatterns) {
        if ($stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[3]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B2SourceFiles[3])::$pattern"
        }
    }
)
$stage5B4B2B2B2ProductSource =
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[4]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[5]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[6]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[7]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[8]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[9]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[10]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[11]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[14]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[15]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[16]] +
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[17]]
$stage5B4B2B2B2RequiredProductPatterns = @(
    'x:Name="DetailAttachmentStrip"',
    'ItemsSource="{Binding AttachmentItemsSource}"',
    'public object[] AttachmentItemsSource',
    'Attachments.Cast<object>().ToArray()',
    'OnPropertyChanged(nameof(AttachmentItemsSource))',
    'await DeleteDetailAttachmentAsync(e.Attachment)',
    'ViewModel.DeleteAttachmentAsync',
    'public async Task<TodoAttachmentViewModel?> AddAttachmentPathAsync',
    'copyToManagedStorageOverride',
    'AttachmentStorageService.ImportPathAsync',
    'public async Task<bool> DeleteAttachmentAsync',
    'await SaveAsync();',
    'File.Delete(attachment.FilePath)',
    'File.Copy(normalizedSourcePath, destinationPath',
    'x:DataType="viewModels:TodoAttachmentViewModel"',
    '{x:Bind DisplayName, Mode=OneWay}',
    '{x:Bind Glyph, Mode=OneWay}',
    'List<AotManagedUiTodoAttachmentEvidence> Attachments',
    'ManagedAttachmentRelativePaths',
    'AttachmentUiContainerRealized',
    'CaptureAotManagedUiTodoStateAsync(',
    'string widgetId',
    'new TodoWidgetStore(widgetId)',
    '[WinRT.GeneratedBindableCustomProperty]'
)
$stage5B4B2B2B2MissingProductPatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredProductPatterns) {
        if ($stage5B4B2B2B2ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4B2B2B2GeneratedBindableCount = [regex]::Matches(
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[11]],
    [regex]::Escape('[WinRT.GeneratedBindableCustomProperty]')).Count
$stage5B4B2B2B2RequiredManagerPatterns = @(
    'aot-5b4b2b2a-todo',
    'aot-5b4b2b2b1-todo-steps',
    'aot-5b4b2b2b2-todo-attachments',
    'AotTodoAttachmentsPersistenceOwnedWidgetId',
    '_contentWidgets.TryGetValue',
    'window.ContentReadyTask',
    'window.CurrentContent is TodoWidgetContentAdapter',
    'adapter.View is TodoWidgetContent',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'Visible'
)
$stage5B4B2B2B2MissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredManagerPatterns) {
        if ($stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[12]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B2SourceFiles[12])::$pattern"
        }
    }
)
$stage5B4B2B2B2RequiredSmokeScriptPatterns = @(
    'TodoAttachmentsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE',
    'Invoke-TodoAttachmentsPersistencePhase',
    '$mutate.todoAttachmentsPersistence.after',
    '$verifyDelete.todoAttachmentsPersistence.before',
    '$verifyDelete.todoAttachmentsPersistence.afterAttachmentDelete',
    '$postflight.todoAttachmentsPersistence.before',
    'todo-managed-attachment.txt',
    'Get-FileSha256',
    'fixtureSha256',
    'managedAttachmentSha256',
    'managedAttachmentPath',
    'managedAttachmentRelativePaths',
    '$managedFilesAfterDelete = @(',
    'todoAttachmentsNaturalExit',
    'todoAttachmentsPreviewProcessesAfter',
    'Sort-Object -Unique',
    'final-todo.json',
    'archivedTodoAttachmentsSessionPath',
    'sessionPath = $archivedTodoAttachmentsSessionPath',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Unhandled exception:',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2B2B2MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2B2B2RequiredSmokeScriptPatterns) {
        if ($stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[13]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2B2B2SourceFiles[13])::$pattern"
        }
    }
)
$stage5B4B2B2B2ForbiddenScopePatterns = @(
    foreach ($sourceFile in $stage5B4B2B2B2SourceFiles[1..3] + $stage5B4B2B2B2SourceFiles[12]) {
        foreach ($pattern in @(
                'TodoWidgetStore.SaveAsync',
                'new TodoAttachment',
                'SetDueDate',
                'SetRecurrence',
                'GlanceWidgetStore',
                'WeatherService',
                'FolderPicker',
                'FileOpenPicker',
                'ShortcutHelper',
                'Launcher.Launch',
                'NativeBackend',
                'LibraryImport',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize')) {
            if ($stage5B4B2B2B2Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B2B2B2JsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2B2B2Sources[$stage5B4B2B2B2SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2B2B2SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -match "(?:App\.Aot(?:ManagedUi|TodoAttachmentsPersistence|TodoPersistence)Smoke|TodoWidgetContent\.(?:AotAttachmentsPersistenceSmoke|AotPersistenceSmoke|Attachments)|AttachmentTileStrip(?:\.AotSmoke)?|WidgetManager\.AotTodoPersistenceSmoke|TodoWidgetViewModel\.DetailAndAttachments|TodoItemViewModel|TodoAttachmentViewModel|TodoViewModels\.AotBindableProperties|AttachmentStorageService|TodoWidgetStore|TodoAttachment)\.(?:cs|xaml)\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2B2B2ExpectedWmc1510Count = 1241
$stage5B4B2B2B2ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2C1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotGlancePersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.AotPersistenceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml",
    "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs",
    "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs",
    "src/DeskBox/ViewModels/GlanceWidgetViewModel.AotBindableProperties.cs",
    "src/DeskBox/ViewModels/GlanceWidgetViewModel.AotPersistenceSmoke.cs",
    "src/DeskBox/Services/GlanceWidgetSettingsPolicy.cs",
    "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs",
    "src/DeskBox/Services/GlanceWidgetStore.cs",
    "src/DeskBox/Services/GlanceImageService.cs",
    "src/DeskBox/Services/WidgetManager.AotGlancePersistenceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "src/DeskBox/Models/GlanceWidgetData.cs"
)
$stage5B4B2C1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2C1SourceFiles) {
    $stage5B4B2C1Sources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2C1RunnerSource =
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[0]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[1]]
$stage5B4B2C1RequiredRunnerPatterns = @(
    'GlancePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE',
    'DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE',
    'AotManagedUiGlanceMutatePhase',
    'AotManagedUiGlanceVerifyRestorePhase',
    'AotManagedUiGlancePostflightPhase',
    'glance-persistence-restart',
    'aot-5b4b2c1-glance',
    'bool isGlancePersistence',
    '? AotManagedUiGlanceWidgetId',
    '? WidgetKind.Glance',
    'CaptureAotManagedUiGlancePersistenceAsync',
    'ApplyAotGlanceMutationAsync',
    'RestoreAotGlanceBaselineAsync',
    'SetLocalImageFilesAsync',
    'SetDisplayElementAsync',
    'SetLayoutAsync',
    'SetPhotoPlaybackAsync',
    'AotManagedUiGlancePersistenceEvidence',
    'state.Surface.ActiveImageUri',
    'GlanceMutationApplied',
    'GlanceBaselineVerified',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2C1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2C1RequiredRunnerPatterns) {
        if ($stage5B4B2C1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2C1RequiredSurfacePatterns = @(
    'WaitForAotGlanceSurfaceAsync',
    'CaptureAotGlanceSurface',
    '_decodedImagePath',
    'BackgroundA.Background is not null',
    'BackgroundB.Background is not null',
    'active.Background as ImageBrush',
    'ActiveBackgroundIsImageBrush',
    'UriSource?.LocalPath',
    'ImmersiveLayoutRoot.Visibility',
    'CenteredLayoutRoot.Visibility',
    'EditorialLayoutRoot.Visibility',
    'CalendarLayoutRoot.Visibility',
    'ReadabilityLayer.Visibility',
    'ActionLayer.Visibility',
    'DataContextMatchesViewModel'
)
$stage5B4B2C1MissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2C1RequiredSurfacePatterns) {
        if ($stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2C1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4B2C1ProductSource =
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[3]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[4]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[5]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[6]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[8]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[9]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[10]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[11]] +
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[14]]
$stage5B4B2C1RequiredProductPatterns = @(
    'public static void SetLocalImageFiles',
    'GlanceWidgetSettingsPolicy.SetLocalImageFiles(',
    'GlanceWidgetSettingsPolicy.ClearLocalSource(',
    'public Task SetLocalImageFilesAsync',
    'public Task SetPhotoPlaybackAsync',
    'GlanceWidgetSettingsPolicy.SetPhotoPlayback(',
    'return _store.UpdateAsync',
    '[JsonSerializable(',
    'typeof(GlanceWidgetData)',
    'GlancePreferencesJsonContext.Default.Preferences',
    'GlanceBackgroundSource.LocalFiles',
    'CreateLocalImages',
    'File.Exists',
    'new BitmapImage',
    'DecodePixelType.Physical',
    'x:Name="ImmersiveLayoutRoot"',
    'x:Name="CenteredLayoutRoot"',
    'x:Name="EditorialLayoutRoot"',
    'x:Name="CalendarLayoutRoot"',
    'x:Name="ReadabilityLayer"',
    'x:Name="ActionLayer"',
    '[WinRT.GeneratedBindableCustomProperty([',
    'public sealed partial class GlanceWidgetViewModel'
)
$stage5B4B2C1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4B2C1RequiredProductPatterns) {
        if ($stage5B4B2C1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4B2C1GeneratedBindableCount = [regex]::Matches(
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[6]],
    [regex]::Escape('[WinRT.GeneratedBindableCustomProperty')).Count
$stage5B4B2C1BindablePropertyCount = [regex]::Matches(
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[6]],
    'nameof\(').Count
$stage5B4B2C1RequiredManagerPatterns = @(
    'AotGlancePersistenceOwnedWidgetId',
    'aot-5b4b2c1-glance',
    '_contentWidgets.TryGetValue',
    'window.ContentReadyTask',
    'window.CurrentContent is GlanceWidgetContentAdapter',
    'adapter.View is GlanceWidgetContent',
    'adapter.ViewModel',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'Visible'
)
$stage5B4B2C1MissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2C1RequiredManagerPatterns) {
        if ($stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[12]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2C1SourceFiles[12])::$pattern"
        }
    }
)
$stage5B4B2C1RequiredSmokeScriptPatterns = @(
    'GlancePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE',
    'DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE',
    'Invoke-GlancePersistencePhase',
    'glance-local.png',
    '[System.IO.File]::WriteAllBytes',
    'glance\widgets',
    'aot-5b4b2c1-glance.json',
    '$mutate.glancePersistence.after',
    '$verifyRestore.glancePersistence.before',
    '$postflight.glancePersistence.before',
    'Assert-GlanceStateEqual',
    'Assert-GlanceEvidenceState',
    'surface.activeImageUri',
    'surface.immersiveLayoutVisible',
    'surface.calendarLayoutVisible',
    'Get-FileSha256',
    'fixtureSha256Before',
    'fixtureSha256After',
    'glanceNaturalExit',
    'Sort-Object -Unique',
    'phaseExecutableHashes',
    'final-glance.json',
    'archivedGlanceSessionPath',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'runtimeFailureLogLines',
    'Image decode failed',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2C1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2C1RequiredSmokeScriptPatterns) {
        if ($stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[13]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2C1SourceFiles[13])::$pattern"
        }
    }
)
$stage5B4B2C1ForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4B2C1SourceFiles[1],
            $stage5B4B2C1SourceFiles[2],
            $stage5B4B2C1SourceFiles[7],
            $stage5B4B2C1SourceFiles[12])) {
        foreach ($pattern in @(
                'FileOpenPicker',
                'FolderPicker',
                'RefreshOnline',
                'HttpClient',
                'Launcher.Launch',
                'NativeBackend',
                'LibraryImport',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.WriteAllBytes',
                'File.WriteAllText')) {
            if ($stage5B4B2C1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B2C1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2C1Sources[$stage5B4B2C1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2C1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.Aot(?:ManagedUi|GlancePersistence)Smoke|GlanceWidgetContent(?:\.AotPersistenceSmoke)?|GlanceWidgetViewModel(?:\.AotBindableProperties|\.AotPersistenceSmoke)?|GlanceWidgetSettingsPolicy|GlanceWidgetSettingsSection\.xaml|GlanceWidgetStore|GlanceImageService|WidgetManager\.AotGlancePersistenceSmoke|GlanceWidgetData)\.(?:cs|xaml)\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2C1ExpectedWmc1510Count = 1241
$stage5B4B2C1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2C2ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs",
    "src/DeskBox/Services/WeatherSettingsPolicy.cs",
    "src/DeskBox/ViewModels/SettingsViewModel.WeatherOptions.cs",
    "src/DeskBox/Services/WeatherWidgetViewModeSettings.cs",
    "src/DeskBox/Services/WidgetManager.AotWeatherSettingsPersistenceSmoke.cs",
    "src/DeskBox/ViewModels/WeatherWidgetViewModel.RefreshAndLayout.cs",
    "scripts/run-aot-managed-ui-smoke.ps1"
)
$stage5B4B2C2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2C2ASourceFiles) {
    $stage5B4B2C2ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2C2ARunnerSource =
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[0]] +
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[1]]
$stage5B4B2C2ARequiredRunnerPatterns = @(
    'WeatherSettingsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE',
    'AotManagedUiWeatherSettingsMutatePhase',
    'AotManagedUiWeatherSettingsVerifyRestorePhase',
    'AotManagedUiWeatherSettingsPostflightPhase',
    'weather-settings-persistence-restart',
    'aot-5b4b2c2a-weather',
    'bool isWeatherSettingsPersistence',
    '? AotManagedUiWeatherSettingsWidgetId',
    '? WidgetKind.Weather',
    'manager.LoadedSurfaceCount == 1',
    'host.WidgetKind != WidgetKind.Weather',
    'CaptureAotManagedUiWeatherSettingsPersistenceAsync',
    'ApplyAotWeatherSettingsState',
    'WeatherSettingsPolicy.TrySetManualLocation',
    'manager.ApplyAotWeatherSettingsViewMode',
    'WeatherSettingsHostSuppressed',
    'WeatherSettingsPersistenceFlushed',
    'AotManagedUiWeatherSettingsPersistenceEvidence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2C2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2C2ARequiredRunnerPatterns) {
        if ($stage5B4B2C2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2C2APolicySource =
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[2]] +
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[3]]
$stage5B4B2C2ARequiredPolicyPatterns = @(
    'TrySetManualLocation',
    'double.IsFinite',
    'latitude is < -90 or > 90',
    'longitude is < -180 or > 180',
    'SetTemperatureUnit',
    'SetWindSpeedUnit',
    'SetDefaultView',
    'SetSkin',
    'SetRefreshInterval',
    'SetDisplayOption',
    'WeatherSettingsPolicy.TrySetManualLocation',
    'WeatherSettingsPolicy.SetTemperatureUnit',
    'WeatherSettingsPolicy.SetWindSpeedUnit',
    'WeatherSettingsPolicy.SetDefaultView',
    'WeatherSettingsPolicy.SetSkin',
    'WeatherSettingsPolicy.SetRefreshInterval',
    'WeatherSettingsPolicy.SetDisplayOption'
)
$stage5B4B2C2AMissingPolicyPatterns = @(
    foreach ($pattern in $stage5B4B2C2ARequiredPolicyPatterns) {
        if ($stage5B4B2C2APolicySource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "policy::$pattern"
        }
    }
)
$stage5B4B2C2AManagerSource =
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[4]] +
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[5]] +
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[6]]
$stage5B4B2C2ARequiredManagerPatterns = @(
    'Weather.ViewMode',
    'DayValue',
    'WeekValue',
    'WeatherWidgetViewModeSettings.SetWeekView',
    'WeatherWidgetViewModeSettings.TryGetWeekView',
    '_settingsService.UpdateWidget',
    'AotWeatherSettingsOwnedWidgetId',
    'GetLoadedDesktopWindows',
    'FeatureWidgetSettings.IsEnabled',
    'host?.WindowHandle',
    'host?.WindowContentRoot?.XamlRoot'
)
$stage5B4B2C2AMissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2C2ARequiredManagerPatterns) {
        if ($stage5B4B2C2AManagerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "manager::$pattern"
        }
    }
)
$stage5B4B2C2ARequiredSmokeScriptPatterns = @(
    'WeatherSettingsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE',
    'Invoke-WeatherSettingsPersistencePhase',
    'Assert-WeatherSettingsStateEqual',
    'Assert-WeatherSettingsEvidenceState',
    '"Weather.ViewMode" = "Day"',
    '$mutate.weatherSettingsPersistence.after',
    '$verifyRestore.weatherSettingsPersistence.before',
    '$postflight.weatherSettingsPersistence.before',
    'weatherSettingsNaturalExit',
    '$processIds | Sort-Object -Unique',
    'phaseExecutableHashes',
    'runtimeWeatherInitializationLines',
    '[WeatherService]',
    '[WeatherWidgetViewModel]',
    'final-settings.json',
    'archivedWeatherSessionPath',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'weatherSettingsPreviewProcessesAfter',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2C2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2C2ARequiredSmokeScriptPatterns) {
        if ($stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[7]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2C2ASourceFiles[7])::$pattern"
        }
    }
)
$stage5B4B2C2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4B2C2ASourceFiles[1],
            $stage5B4B2C2ASourceFiles[2],
            $stage5B4B2C2ASourceFiles[5])) {
        foreach ($pattern in @(
                'WeatherService',
                'WeatherWidgetViewModel',
                'WeatherWidgetContent',
                'HttpClient',
                'WindowsLocationHelper',
                'CitySearchService',
                'InitializeAsync',
                'RefreshAsync',
                'FileOpenPicker',
                'FolderPicker',
                'NativeBackend',
                'LibraryImport',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.WriteAllText')) {
            if ($stage5B4B2C2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B2C2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2C2ASources[$stage5B4B2C2ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2C2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.Aot(?:ManagedUi|WeatherSettingsPersistence)Smoke|WeatherSettingsPolicy|SettingsViewModel\.WeatherOptions|WeatherWidgetViewModeSettings|WidgetManager\.AotWeatherSettingsPersistenceSmoke|WeatherWidgetViewModel\.RefreshAndLayout)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2C2AExpectedWmc1510Count = 1241
$stage5B4B2C2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4B2C2BSourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotWeatherSurfacePersistenceSmoke.cs",
    "src/DeskBox/Services/AotWeatherSurfaceFixture.cs",
    "src/DeskBox/Services/WeatherService.cs",
    "src/DeskBox/Services/WeatherWidgetContentProvider.cs",
    "src/DeskBox/Controls/WidgetContents/WeatherWidgetContentAdapter.cs",
    "src/DeskBox/ViewModels/WeatherWidgetViewModel.cs",
    "src/DeskBox/ViewModels/WeatherWidgetViewModel.DataProcessing.cs",
    "src/DeskBox/ViewModels/WeatherViewModels.AotBindableProperties.cs",
    "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml",
    "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml.cs",
    "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.AotSurfaceSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotWeatherSurfaceSmoke.cs",
    "src/DeskBox/Services/WeatherWidgetViewModeSettings.cs",
    "src/DeskBox/Services/WeatherSettingsPolicy.cs",
    "scripts/run-aot-managed-ui-smoke.ps1"
)
$stage5B4B2C2BSources = [ordered]@{}
foreach ($sourceFile in $stage5B4B2C2BSourceFiles) {
    $stage5B4B2C2BSources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4B2C2BRunnerSource =
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[0]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[1]]
$stage5B4B2C2BRequiredRunnerPatterns = @(
    'WeatherSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE',
    'AotManagedUiWeatherSurfaceMutatePhase',
    'AotManagedUiWeatherSurfaceVerifyRestorePhase',
    'AotManagedUiWeatherSurfacePostflightPhase',
    'weather-surface-persistence-restart',
    'aot-5b4b2c2b-weather',
    'bool isWeatherSurfacePersistence',
    '? AotManagedUiWeatherSurfaceWidgetId',
    'CaptureAotManagedUiWeatherSurfacePersistenceAsync',
    'GetAotWeatherSurfaceHostAsync',
    'WeatherSurfaceHostReady',
    'WeatherSurfacePersistenceFlushed',
    'AotManagedUiWeatherSurfacePersistenceEvidence',
    'AotManagedUiWeatherSurfaceEvidence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4B2C2BMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4B2C2BRequiredRunnerPatterns) {
        if ($stage5B4B2C2BRunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4B2C2BFixtureSource =
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[2]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[3]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[4]]
$stage5B4B2C2BRequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'WeatherSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_SMOKE',
    'DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE',
    'aot-5b4b2c2b-weather',
    'Shanghai AOT Surface',
    'TryCreateService',
    'new WeatherService(CreateData)',
    '_aotWeatherDataFactory',
    'WeatherData fixture = _aotWeatherDataFactory',
    '[AotWeatherSurfaceFixture] Served deterministic WeatherData request',
    'WeatherCode = 61',
    'Humidity = 64',
    'WindSpeed = 18',
    'Pressure = 1012',
    'TemperatureMax = [24, 23, 26, 25, 8, 18, 20]',
    'weatherService = AotWeatherSurfaceFixture.TryCreateService(config)'
)
$stage5B4B2C2BMissingFixturePatterns = @(
    foreach ($pattern in $stage5B4B2C2BRequiredFixturePatterns) {
        if ($stage5B4B2C2BFixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4B2C2BSurfaceSource =
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[6]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[7]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[8]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[9]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[10]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[11]]
$stage5B4B2C2BRequiredSurfacePatterns = @(
    'DailyForecastItemsSource => DailyForecast.Cast<object>().ToArray()',
    'HourlyForecastItemsSource => HourlyForecast.Cast<object>().ToArray()',
    'OnPropertyChanged(nameof(DailyForecastItemsSource))',
    'OnPropertyChanged(nameof(HourlyForecastItemsSource))',
    'nameof(DailyForecastItemsSource)',
    'nameof(HourlyForecastItemsSource)',
    'ItemsSource="{Binding HourlyForecastItemsSource}"',
    'ItemsSource="{Binding DailyForecastItemsSource}"',
    'x:Name="ExpandedHourlyItems"',
    'x:Name="ExpandedDailyItems"',
    'x:Name="CompactTemperatureText"',
    'x:Name="CompactWindText"',
    'x:Name="ExpandedUvMetric"',
    'x:Name="ExpandedPressureMetric"',
    'x:Name="HourlyTemperatureText"',
    'x:Name="DailyMaxText"',
    'WaitForAotWeatherSurfaceAsync',
    'WaitForAotWeatherCompactSurfaceAsync',
    'SetAotWeatherSurfaceViewModeAsync',
    'WeatherViewSegmented.SelectedIndex',
    'ContainerFromIndex(0)',
    'HourlyTemplateTextProjected',
    'DailyTemplateTextProjected',
    'AotWeatherCompactSurfaceSnapshot',
    'UvMetricVisible',
    'PressureMetricVisible',
    'DataContextMatchesViewModel',
    'LoadingOverlayHidden'
)
$stage5B4B2C2BMissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4B2C2BRequiredSurfacePatterns) {
        if ($stage5B4B2C2BSurfaceSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "surface::$pattern"
        }
    }
)
$stage5B4B2C2BBindableAttributeCount = [regex]::Matches(
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[8]],
    [regex]::Escape('[WinRT.GeneratedBindableCustomProperty(')).Count
$stage5B4B2C2BManagerSource =
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[5]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[12]] +
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[13]]
$stage5B4B2C2BRequiredManagerPatterns = @(
    'WeatherService? weatherService = null',
    'WeatherService? weatherService = null,',
    'GetAotWeatherSurfaceHostAsync',
    '_contentWidgets.TryGetValue',
    'ContentReadyTask',
    'WeatherWidgetContentAdapter adapter',
    'adapter.View is WeatherWidgetContent surface',
    'window.WindowHandle',
    'window.WindowContentRoot?.XamlRoot',
    'CaptureAotWeatherCompactSurfaceAsync',
    'CaptureAotPersistenceSmokeBounds',
    'ApplyAotPersistenceSmokeBounds',
    'compactLogicalWidth = 205',
    'TryGetWeekView('
)
$stage5B4B2C2BMissingManagerPatterns = @(
    foreach ($pattern in $stage5B4B2C2BRequiredManagerPatterns) {
        if ($stage5B4B2C2BManagerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "manager::$pattern"
        }
    }
)
$stage5B4B2C2BRequiredSmokeScriptPatterns = @(
    'WeatherSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE',
    'Invoke-WeatherSurfacePersistencePhase',
    'Assert-WeatherSurfaceStateEqual',
    'Assert-WeatherSurfaceEvidenceState',
    '$State.compactSurface',
    '$surface.uvMetricVisible',
    '$surface.pressureMetricVisible',
    '"Weather.ViewMode" = "Day"',
    '$mutate.weatherSurfacePersistence.after',
    '$verifyRestore.weatherSurfacePersistence.before',
    '$postflight.weatherSurfacePersistence.before',
    'weatherSurfaceNaturalExit',
    '$processIds | Sort-Object -Unique',
    'phaseExecutableHashes',
    'runtimeFixtureLogLines',
    '[AotWeatherSurfaceFixture] Served deterministic WeatherData request',
    'runtimeNetworkLogLines',
    '[WindowsLocation]',
    '[WeatherService]',
    'final-settings.json',
    'archivedWeatherSurfaceSessionPath',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'weatherSurfacePreviewProcessesAfter',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4B2C2BMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4B2C2BRequiredSmokeScriptPatterns) {
        if ($stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[15]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4B2C2BSourceFiles[15])::$pattern"
        }
    }
)
$stage5B4B2C2BForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4B2C2BSourceFiles[1],
            $stage5B4B2C2BSourceFiles[2],
            $stage5B4B2C2BSourceFiles[11],
            $stage5B4B2C2BSourceFiles[12])) {
        foreach ($pattern in @(
                'HttpClient',
                'WindowsLocationHelper',
                'CitySearchService',
                'FetchFromSourceAsync',
                'SearchCityAsync',
                'ResolveCityAsync',
                'FileOpenPicker',
                'FolderPicker',
                'NativeBackend',
                'LibraryImport',
                'CreateWidget',
                'RemoveWidget',
                'JsonSerializer.Deserialize',
                'File.WriteAllBytes',
                'File.WriteAllText')) {
            if ($stage5B4B2C2BSources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4B2C2BJsonSerializeCallCount = [regex]::Matches(
    $stage5B4B2C2BSources[$stage5B4B2C2BSourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4B2C2BSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.Aot(?:ManagedUi|WeatherSurfacePersistence)Smoke|AotWeatherSurfaceFixture|WeatherService|WeatherWidgetContent(?:\.AotSurfaceSmoke)?|WeatherWidgetContentAdapter|WeatherWidgetContentProvider|WeatherViewModels\.AotBindableProperties|WeatherWidgetViewModel(?:\.DataProcessing)?|WidgetManager\.AotWeatherSurfaceSmoke)\.(?:cs|xaml)\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4B2C2BExpectedWmc1510Count = 1241
$stage5B4B2C2BActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs",
    "src/DeskBox/Services/AotLocalFileSurfaceFixture.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotLocalFileSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "src/DeskBox/Controls/FileItemSurface.AotBindableProperties.cs",
    "src/DeskBox/Models/WidgetItem.AotBindableProperties.cs",
    "src/DeskBox/ViewModels/WidgetViewModel.AotBindableProperties.cs",
    "scripts/run-aot-managed-ui-smoke.ps1"
)
$stage5B4C1ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1ASourceFiles) {
    $stage5B4C1ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1ARunnerSource =
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[0]] +
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[1]]
$stage5B4C1ARequiredRunnerPatterns = @(
    'LocalFileSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE',
    'AotManagedUiLocalFileMutatePhase',
    'AotManagedUiLocalFileVerifyRestorePhase',
    'AotManagedUiLocalFilePostflightPhase',
    'local-file-surface-persistence-restart',
    'aot-5b4c1a-file',
    'bool isLocalFilePersistence',
    '? AotManagedUiLocalFileWidgetId',
    'CaptureAotManagedUiLocalFilePersistenceAsync',
    'LocalFileSurfaceHostReady',
    'LocalFileOwnedRootVerified',
    'LocalFilePersistenceFlushed',
    'AotManagedUiLocalFilePersistenceEvidence',
    'AotManagedUiLocalFileDiskEntryEvidence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4C1AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1ARequiredRunnerPatterns) {
        if ($stage5B4C1ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1AFixtureSource =
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[2]]
$stage5B4C1ARequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'LocalFileSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE',
    'aot-5b4c1a-file',
    'local-file-surface',
    'widget-root',
    'sources',
    'baseline.txt',
    'nested.txt',
    'copy-source.txt',
    'move-source.txt',
    'copied-renamed.txt',
    'watcher-created.txt',
    'IsPathEqualOrInside(dataPaths.RootPath'
)
$stage5B4C1AMissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1ARequiredFixturePatterns) {
        if ($stage5B4C1AFixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1ASurfaceSource =
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[1]] +
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[3]] +
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[4]]
$stage5B4C1ARequiredSurfacePatterns = @(
    'GetAotLocalFileSurfaceHostAsync',
    '_fileWidgets.TryGetValue',
    'ContentReadyTask',
    'WindowHandle',
    'WindowContentRoot?.XamlRoot',
    'WaitForAotLocalFileSurfaceAsync',
    'GetActiveItemsView',
    'ContainerFromItem',
    'FileItemSurface',
    'ItemNameText.Text',
    'DataContextMatches',
    'ProjectedItemCount',
    'itemsInExpectedOrder',
    'item.IsFolder == string.Equals(',
    'NavigateIntoFolderAsync',
    'NavigateUpAsync',
    'ImportPathsAsync',
    'moveWhenMapped: false',
    'moveWhenMapped: true',
    'useShellProgress: false',
    'RenameItemAsync',
    'catch (IOException ex)',
    'File.WriteAllTextAsync',
    'SHA256.HashData(stream)',
    'SearchOption.AllDirectories',
    'LocalFileWatcherObservedExternalCreate'
)
$stage5B4C1AMissingSurfacePatterns = @(
    foreach ($pattern in $stage5B4C1ARequiredSurfacePatterns) {
        if ($stage5B4C1ASurfaceSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "surface::$pattern"
        }
    }
)
$stage5B4C1ABindableAttributeCount =
    [regex]::Matches(
        $stage5B4C1ASources[$stage5B4C1ASourceFiles[5]],
        [regex]::Escape('[WinRT.GeneratedBindableCustomProperty(')).Count +
    [regex]::Matches(
        $stage5B4C1ASources[$stage5B4C1ASourceFiles[6]],
        [regex]::Escape('[WinRT.GeneratedBindableCustomProperty(')).Count +
    [regex]::Matches(
        $stage5B4C1ASources[$stage5B4C1ASourceFiles[7]],
        [regex]::Escape('[WinRT.GeneratedBindableCustomProperty(')).Count
$stage5B4C1ARequiredBindablePatterns = @(
    'public sealed partial class FileItemSurface',
    'nameof(IconLayoutVisibility)',
    'nameof(ListLayoutVisibility)',
    'nameof(SurfaceHorizontalAlignment)',
    'nameof(SurfaceMargin)',
    'nameof(SurfaceMaxWidth)',
    'nameof(SurfacePadding)',
    'public partial class WidgetItem',
    'nameof(FallbackIconVisibility)',
    'nameof(FullPath)',
    'nameof(Icon)',
    'nameof(IconVisibility)',
    'nameof(Name)',
    'nameof(SecondaryInfo)',
    'public partial class WidgetViewModel',
    'nameof(CurrentFolderDisplayName)',
    'nameof(FolderNavigationVisibility)',
    'nameof(VisibleItems)',
    'nameof(IconViewVisibility)',
    'nameof(ListViewVisibility)'
)
$stage5B4C1ABindableSource =
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[5]] +
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[6]] +
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[7]]
$stage5B4C1AMissingBindablePatterns = @(
    foreach ($pattern in $stage5B4C1ARequiredBindablePatterns) {
        if ($stage5B4C1ABindableSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "bindable::$pattern"
        }
    }
)
$stage5B4C1ARequiredSmokeScriptPatterns = @(
    'LocalFileSurfacePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE',
    'Invoke-LocalFilePersistencePhase',
    'Get-LocalFileFixtureState',
    'Assert-LocalFileDiskState',
    'Assert-LocalFileEvidenceState',
    'Assert-LocalFileStateEqual',
    'mutate-independent-disk',
    'verify-restore-independent-disk',
    'postflight-independent-disk',
    '$mutate.localFilePersistence.after',
    '$verifyRestore.localFilePersistence.before',
    '$postflight.localFilePersistence.before',
    'localFileNaturalExit',
    '$processIds | Sort-Object -Unique',
    'phaseExecutableHashes',
    'runtimeDeferredPathLogLines',
    '[bool]$item.isFolder -ne ([string]$item.name -ceq "nested")',
    'final-fixture',
    'disk-states.json',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'localFilePreviewProcessesAfter',
    'previewRootCleaned',
    'Stop-ExactPreviewProcess',
    '.deskbox-aot-managed-ui-owned.json'
)
$stage5B4C1AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1ARequiredSmokeScriptPatterns) {
        if ($stage5B4C1ASources[$stage5B4C1ASourceFiles[8]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4C1ASourceFiles[8])::$pattern"
        }
    }
)
$stage5B4C1AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1ASourceFiles[1],
            $stage5B4C1ASourceFiles[2],
            $stage5B4C1ASourceFiles[3],
            $stage5B4C1ASourceFiles[4])) {
        foreach ($pattern in @(
                'StorageFile',
                'StorageFolder',
                'FileOpenPicker',
                'FolderPicker',
                'DataPackage',
                'NativeDrop',
                'DeleteEntryToRecycleBin',
                'RegisterHotKey',
                'HttpClient',
                'WeatherService',
                'MusicVolume',
                'NativeBackend',
                'LibraryImport',
                'JsonSerializer.Deserialize',
                'RefreshFolderContentsAsync',
                'useShellProgress: true')) {
            if ($sourceFile -eq $stage5B4C1ASourceFiles[4] -and
                $pattern -eq 'NativeDrop') {
                # Stage 5B-4C1C2A now owns the native-drop host exposed by the
                # shared local-file surface manager. All other 4C1A deferred
                # paths remain forbidden and C1C2A applies its own narrow gate.
                continue
            }

            if ($stage5B4C1ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1ASources[$stage5B4C1ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.Aot(?:ManagedUi|LocalFilePersistence)Smoke|AotLocalFileSurfaceFixture|FileItemSurface\.AotBindableProperties|FileSurfaceContent\.AotLocalFileSmoke|WidgetItem\.AotBindableProperties|WidgetManager\.AotLocalFileSurfaceSmoke|WidgetViewModel\.AotBindableProperties)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1AExpectedWmc1510Count = 1241
$stage5B4C1AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1B1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotRecycleBinSmoke.cs",
    "src/DeskBox/Services/AotRecycleBinFixture.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotRecycleBinSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "src/DeskBox/Helpers/RecycleBinNativeBackend.cs",
    "src/DeskBox/Controls/FileItemMenuBuilder.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs",
    "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs",
    "src/DeskBox/Services/FileService.cs",
    "native/deskbox-native/src/lib.rs",
    "native/deskbox-native/src/recycle_bin.rs",
    "native/include/deskbox_native.h",
    "scripts/build-rust-native.ps1",
    "scripts/run-aot-managed-ui-smoke.ps1"
)
$stage5B4C1B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1B1SourceFiles) {
    $stage5B4C1B1Sources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1B1RunnerSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[0]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[1]]
$stage5B4C1B1RequiredRunnerPatterns = @(
    'RecycleBinMenuPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE',
    'AotManagedUiRecycleBinMutatePhase',
    'AotManagedUiRecycleBinVerifyRestorePhase',
    'AotManagedUiRecycleBinPostflightPhase',
    'AotManagedUiRecycleBinCompensatePhase',
    'aot-5b4c1b1-file',
    'CaptureAotManagedUiRecycleBinAsync',
    'AotManagedUiRecycleBinEvidence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4C1B1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredRunnerPatterns) {
        if ($stage5B4C1B1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1B1FixtureSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[2]]
$stage5B4C1B1RequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'RecycleBinMenuPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE',
    'DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_RUN_ID',
    'aot-5b4c1b1-file',
    'recycle-bin-menu',
    'widget-root',
    'IsValidRunId(runId)',
    'value is { Length: 32 }',
    "character is >= '0' and <= '9'",
    ">= 'a' and <= 'f'",
    'single-{runId}',
    'multi-file-{runId}',
    'multi-folder-{runId}',
    'payload-{runId}',
    'IsPathEqualOrInside'
)
$stage5B4C1B1MissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredFixturePatterns) {
        if ($stage5B4C1B1FixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1B1ProductSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[6]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[7]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[8]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[9]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[10]]
$stage5B4C1B1RequiredProductPatterns = @(
    'CreateItemFlyout',
    'CreateMultiSelectionFlyout',
    'Widget.MoveToRecycleBin',
    'await actions.DeleteItemsAsync(actions.GetSelectedItems())',
    'DeleteItemsAsync,',
    'bool permanently = false',
    '_fileService.DeleteEntryAsync(item.Path, recycle)',
    'DeleteEntryToRecycleBin(normalizedPath)',
    'SHFileOperation(ref operation)',
    'FofAllowUndo'
)
$stage5B4C1B1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredProductPatterns) {
        if ($stage5B4C1B1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C1B1MenuSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[3]]
$stage5B4C1B1RequiredMenuPatterns = @(
    'CreateItemFlyout(selectedItems[0])',
    'CreateMultiSelectionFlyout()',
    'MenuFlyoutItemAutomationPeer',
    'PatternInterface.Invoke',
    'IInvokeProvider',
    'invokeProvider.Invoke()',
    'FeedbackRequested += OnFeedbackRequested',
    'file-delete',
    'AotRecycleBinMenuInvocationSnapshot'
)
$stage5B4C1B1MissingMenuPatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredMenuPatterns) {
        if ($stage5B4C1B1MenuSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "menu::$pattern"
        }
    }
)
$stage5B4C1B1NativeSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[5]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[11]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[12]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[13]] +
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[14]]
$stage5B4C1B1RequiredNativePatterns = @(
    'DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1',
    'DESKBOX_RECYCLE_BIN_REQUEST_V1_SIZE_64',
    'DESKBOX_RECYCLE_BIN_RESULT_V1_SIZE_64',
    'deskbox_recycle_bin_v1',
    'assert_eq!(deskbox_native_capabilities(), 511);',
    'RecycleBinCapability = 1UL << 8',
    'NativeLibrary.TryGetExport',
    'result.Reserved5 != 0',
    'const RECYCLE_BIN_CSIDL: i32 = 10',
    'System.Recycle.DeletedFrom',
    'GetFullPathNameW',
    'CompareStringOrdinal',
    'let mut restore_item: Option<FolderItem> = None',
    'if result.matched_count != 1',
    'const RESTORE_VERB: &str = "undelete"',
    'item.InvokeVerb(&verb)',
    'expected 511'
)
$stage5B4C1B1MissingNativePatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredNativePatterns) {
        if ($stage5B4C1B1NativeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "native::$pattern"
        }
    }
)
$stage5B4C1B1RustSource =
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[12]]
$stage5B4C1B1RestoreInvokeAfterEnumeration =
    $stage5B4C1B1RustSource.IndexOf(
        'result.enumerate_hresult = DESKBOX_NATIVE_S_OK',
        [StringComparison]::Ordinal) -ge 0 -and
    $stage5B4C1B1RustSource.IndexOf(
        'item.InvokeVerb(&verb)',
        [StringComparison]::Ordinal) -gt
        $stage5B4C1B1RustSource.IndexOf(
            'result.enumerate_hresult = DESKBOX_NATIVE_S_OK',
            [StringComparison]::Ordinal)
$stage5B4C1B1RequiredScenarioPatterns = @(
    'InvokeAotRecycleBinMenuDeleteAsync',
    'WaitForAotLocalFileSurfaceAsync',
    'RecycleBinSingleMenuDeleteCompleted',
    'RecycleBinMultiMenuDeleteCompleted',
    'case "VerifyRestore"',
    'case "Postflight"',
    'case "Compensate"',
    'RecycleBinNativeOperation.Query',
    'RecycleBinNativeOperation.Restore',
    'query.MatchedCount == (exists ? 0U : 1U)',
    'restore.MatchedCount == 1',
    'restore.RestoredCount == 1',
    'SHA256.HashData(stream)',
    'RecycleBinCompensationCompleted'
)
$stage5B4C1B1MissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredScenarioPatterns) {
        if ($stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[1]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C1B1RequiredSmokeScriptPatterns = @(
    'RecycleBinMenuPersistenceRestart',
    '[Guid]::NewGuid().ToString("N")',
    'recycle-preview-$recycleBinRunId',
    '$DataRoot-Recovery',
    'Refusing to replace an existing Recycle Bin preview root',
    'Refusing to replace an existing Recycle Bin recovery root',
    'Invoke-RecycleBinPhase',
    '-Phase "Mutate"',
    '-Phase "VerifyRestore"',
    '-Phase "Postflight"',
    '-Phase "Compensate"',
    'mutate-independent-disk',
    'verify-restore-independent-disk',
    'postflight-independent-disk',
    'compensation-independent-disk',
    '$processIds | Sort-Object -Unique',
    '$phaseExecutableHashes | Sort-Object -Unique',
    'foreach ($property in @("relativePath", "length", "sha256"))',
    'productionDataFingerprintBefore',
    '$recycleSafetyVerified = $true',
    'owned preview/recovery roots and run ID',
    'were preserved for recovery',
    'Refusing to clean an unowned Recycle Bin preview root',
    'Refusing to clean an unowned Recycle Bin recovery root',
    'Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force',
    'Remove-Item -LiteralPath $resolvedRecoveryRoot -Recurse -Force',
    'recoveryRootCleaned',
    'final-fixture',
    'disk-states.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C1B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1B1RequiredSmokeScriptPatterns) {
        if ($stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[15]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "$($stage5B4C1B1SourceFiles[15])::$pattern"
        }
    }
)
$stage5B4C1B1ForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1B1SourceFiles[1],
            $stage5B4C1B1SourceFiles[2],
            $stage5B4C1B1SourceFiles[3],
            $stage5B4C1B1SourceFiles[12])) {
        foreach ($pattern in @(
                'FileOpenPicker',
                'FolderPicker',
                'NativeDrop',
                'IFileOperation',
                'MoveEntriesWithShellProgress',
                'useShellProgress: true',
                'ShowFileProperties',
                'SHEmptyRecycleBin',
                '$Recycle.Bin')) {
            if ($stage5B4C1B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1B1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1B1Sources[$stage5B4C1B1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotRecycleBinSmoke|AotRecycleBinFixture|FileSurfaceContent\.AotRecycleBinSmoke|RecycleBinNativeBackend)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1B1ExpectedWmc1510Count = 1241
$stage5B4C1B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1B2ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotShellMoveSmoke.cs",
    "src/DeskBox/Services/AotShellMoveFixture.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotShellMoveSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "src/DeskBox/Controls/FileItemMenuBuilder.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs",
    "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs",
    "src/DeskBox/Services/OrganizerService.cs",
    "src/DeskBox/Services/FileService.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/run-aot-shell-move-persistence-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C1B2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1B2ASourceFiles) {
    $stage5B4C1B2ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1B2ARunnerSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[0]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[1]]
$stage5B4C1B2ARequiredRunnerPatterns = @(
    'ShellMovePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE',
    'AotManagedUiShellMoveMutatePhase',
    'AotManagedUiShellMoveVerifyRestorePhase',
    'AotManagedUiShellMovePostflightPhase',
    'AotManagedUiShellMoveCompensatePhase',
    'aot-5b4c1b2a-file',
    'CaptureAotManagedUiShellMoveAsync',
    'AotManagedUiShellMoveEvidence',
    'NormalShutdownRequested',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4C1B2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredRunnerPatterns) {
        if ($stage5B4C1B2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1B2AFixtureSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[2]]
$stage5B4C1B2ARequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'ShellMovePersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE',
    'DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_RUN_ID',
    'aot-5b4c1b2a-file',
    'widget-root',
    'desktop-root',
    'value is { Length: 32 }',
    "character is >= '0' and <= '9'",
    ">= 'a' and <= 'f'",
    'TryGetOwnedDesktopPath',
    'GetRecoveryProbeDelay',
    'TimeSpan.FromMilliseconds(150)',
    'executeRealShellMove()',
    'case PartialMode',
    'case CancelMode',
    'case LateMode',
    'SimulatedOperationsAborted = true',
    'Thread.Sleep(800)',
    'unsupported owned selection shape'
)
$stage5B4C1B2AMissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredFixturePatterns) {
        if ($stage5B4C1B2AFixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1B2AProductSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[5]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[6]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[7]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[8]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[9]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[10]]
$stage5B4C1B2ARequiredProductPatterns = @(
    'Widget.MoveBackToDesktop',
    'await actions.MoveItemsBackToDesktopAsync(',
    'ownerWindowHandle: _hostWindowHandle',
    'IntPtr ownerWindowHandle = default',
    'GetDefaultDesktopPath',
    'AotShellMoveFixture.TryGetOwnedDesktopPath',
    'ExecuteTransferPlanAsync(',
    'MoveEntriesWithShellProgress(',
    'WindowHandle = ownerWindowHandle',
    'SHFileOperation(ref fileOperation)',
    'AotShellMoveFixture.TryExecute',
    'AotShellMoveFixture.ReturnedOutcome',
    'AotShellMoveFixture.RecoveredPendingOutcome',
    'AotShellMoveFixture.ExtendedWaitOutcome',
    'ObserveLateShellMoveCompletionAsync'
)
$stage5B4C1B2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredProductPatterns) {
        if ($stage5B4C1B2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C1B2AMenuSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[3]]
$stage5B4C1B2ARequiredMenuPatterns = @(
    'CreateItemFlyout(selectedItems[0])',
    'CreateMultiSelectionFlyout()',
    'Widget.MoveBackToDesktop',
    '_hostWindowHandle.ToInt64()',
    'MenuFlyoutItemAutomationPeer',
    'PatternInterface.Invoke',
    'IInvokeProvider',
    'invokeProvider.Invoke()',
    'FeedbackRequested += OnFeedbackRequested',
    'file-move-desktop',
    'AotShellMoveMenuInvocationSnapshot'
)
$stage5B4C1B2AMissingMenuPatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredMenuPatterns) {
        if ($stage5B4C1B2AMenuSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "menu::$pattern"
        }
    }
)
$stage5B4C1B2AScenarioSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[1]]
$stage5B4C1B2ARequiredScenarioPatterns = @(
    'InvokeAotShellMoveBackToDesktopAsync',
    'WaitForAotLocalFileSurfaceAsync',
    'ShellMoveMenuMatrixCompleted',
    'LateTaskPendingWhenProductReturned',
    'RecoveredPendingOutcome',
    '[1, 0, 1, 1]',
    'case "VerifyRestore"',
    'case "Postflight"',
    'case "Compensate"',
    'RecentOrganizationHistory.Clear()',
    'ShellMoveFilesRestoredByHarness',
    'ShellMoveCompensationCompleted',
    'SHA256.HashData(stream)'
)
$stage5B4C1B2AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredScenarioPatterns) {
        if ($stage5B4C1B2AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C1B2ASmokeSource =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[11]] +
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[12]]
$stage5B4C1B2ARequiredSmokeScriptPatterns = @(
    'run-aot-shell-move-persistence-smoke.ps1',
    'ShellMovePersistenceRestart',
    '[Guid]::NewGuid().ToString("N")',
    'shell-move-preview-$runId',
    '$DataRoot-Recovery',
    'Refusing to replace an existing Shell move preview root',
    'Refusing to replace an existing Shell move recovery root',
    'Invoke-ShellMovePhase',
    '-Phase "Mutate"',
    '-Phase "VerifyRestore"',
    '-Phase "Postflight"',
    '-Phase "Compensate"',
    'mutate-independent-disk',
    'verify-restore-independent-hashes',
    'postflight-independent-hashes',
    'compensation-independent-hashes',
    '$processIds | Sort-Object -Unique',
    '$phaseExecutableHashes | Sort-Object -Unique',
    'productionDataFingerprintBefore',
    '$safetyVerified = $true',
    'owned preview/recovery roots and run ID',
    'were preserved for recovery',
    'Refusing to clean an unowned Shell move',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'recoveryRootCleaned',
    'final-fixture',
    'disk-states.json',
    'Pending shell move call eventually returned',
    'Stop-ExactPreviewProcess'
)
$stage5B4C1B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1B2ARequiredSmokeScriptPatterns) {
        if ($stage5B4C1B2ASmokeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C1B2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1B2ASourceFiles[1],
            $stage5B4C1B2ASourceFiles[2],
            $stage5B4C1B2ASourceFiles[3])) {
        foreach ($pattern in @(
                'FileOpenPicker',
                'FolderPicker',
                'NativeDrop',
                'ShowFileProperties',
                'IFileOperation',
                'LibraryImport')) {
            if ($stage5B4C1B2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1B2ARustAbiUnchanged =
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[13]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[13]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C1B2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1B2ASources[$stage5B4C1B2ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotShellMoveSmoke|AotShellMoveFixture|FileSurfaceContent\.AotShellMoveSmoke|OrganizerService|FileService|WidgetViewModel\.Operations)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1B2AExpectedWmc1510Count = 1241
$stage5B4C1B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1B2BSourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotFilePropertiesSmoke.cs",
    "src/DeskBox/Services/AotFilePropertiesFixture.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotFilePropertiesSmoke.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "src/DeskBox/Controls/FileItemMenuBuilder.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs",
    "src/DeskBox/Helpers/ShellContextMenuHelper.cs",
    "src/DeskBox/Helpers/Win32Helper.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/run-aot-file-properties-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C1B2BSources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1B2BSourceFiles) {
    $stage5B4C1B2BSources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1B2BRunnerSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[0]] +
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[1]]
$stage5B4C1B2BRequiredRunnerPatterns = @(
    'FilePropertiesReadOnly',
    'file-properties-read-only',
    'aot-5b4c1b2b-file',
    'CaptureAotManagedUiFilePropertiesAsync',
    'AotManagedUiFilePropertiesEvidence',
    'FileProperties = scenario == AotManagedUiFilePropertiesScenario',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult'
)
$stage5B4C1B2BMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredRunnerPatterns) {
        if ($stage5B4C1B2BRunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1B2BFixtureSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[2]]
$stage5B4C1B2BRequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'FilePropertiesReadOnly',
    'DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID',
    'aot-5b4c1b2b-file',
    'file-properties',
    'widget-root',
    'value is { Length: 32 }',
    "character is >= '0' and <= '9'",
    ">= 'a' and <= 'f'",
    'TryBeginInvocation',
    'ownerWindowHandle == IntPtr.Zero',
    'permits exactly one product invocation',
    'RecordInvocationResult',
    'CaptureVisibleTopLevelWindowHandles',
    'ObserveAndCloseOwnedDialogAsync',
    'Win32Helper.GW_OWNER',
    'Win32Helper.GA_ROOTOWNER',
    'CaptureObservedWindow',
    'AotFilePropertiesObservedWindowSnapshot',
    '"#32770"',
    'WmClose',
    'CountVisibleMatchingDialogs'
)
$stage5B4C1B2BMissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredFixturePatterns) {
        if ($stage5B4C1B2BFixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1B2BProductSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[5]] +
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[6]] +
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[7]] +
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[8]]
$stage5B4C1B2BRequiredProductPatterns = @(
    '"Common.Properties"',
    'actions.ShowProperties(item)',
    'ShellContextMenuHelper.ShowProperties(',
    '_hostWindowHandle,',
    'item.Path',
    'SHObjectProperties(',
    'SHOP_FILEPATH',
    'AotFilePropertiesFixture.TryBeginInvocation',
    'AotFilePropertiesFixture.RecordInvocationResult',
    'public const uint GW_OWNER = 4'
)
$stage5B4C1B2BMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredProductPatterns) {
        if ($stage5B4C1B2BProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C1B2BMenuSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[3]]
$stage5B4C1B2BRequiredMenuPatterns = @(
    'CreateItemFlyout(target)',
    'Common.Properties',
    '_hostWindowHandle == IntPtr.Zero',
    'MenuFlyoutItemAutomationPeer',
    'PatternInterface.Invoke',
    'IInvokeProvider',
    'CaptureVisibleTopLevelWindowHandles',
    'ObserveAndCloseOwnedDialogAsync',
    'invokeProvider.Invoke()',
    'WaitForInvocationResultAsync',
    'CountVisibleMatchingDialogs',
    'AotFilePropertiesMenuInvocationSnapshot'
)
$stage5B4C1B2BMissingMenuPatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredMenuPatterns) {
        if ($stage5B4C1B2BMenuSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "menu::$pattern"
        }
    }
)
$stage5B4C1B2BScenarioSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[1]]
$stage5B4C1B2BRequiredScenarioPatterns = @(
    'GetAotLocalFileSurfaceHostAsync(',
    'WaitForAotLocalFileSurfaceAsync(',
    'InvokeAotFilePropertiesAsync(',
    'FilePropertiesOwnedBaselineVerified',
    'FilePropertiesMenuInvoked',
    'FilePropertiesInvocationVerified',
    'Invocation.OwnerWindowHandle == host.WindowHandle',
    'FilePropertiesDialogObserved',
    'ExpectedOwner.IsWindow',
    'DirectOwner.IsWindow',
    'RootOwner.IsWindow',
    'FilePropertiesDialogClosed',
    'RemainingMatchingDialogCount == 0',
    'FilePropertiesPostflightVerified',
    'SHA256.HashData(stream)'
)
$stage5B4C1B2BMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredScenarioPatterns) {
        if ($stage5B4C1B2BScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C1B2BSmokeSource =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[9]] +
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[10]]
$stage5B4C1B2BRequiredSmokeScriptPatterns = @(
    'run-aot-file-properties-smoke.ps1',
    'FilePropertiesReadOnly',
    '[Guid]::NewGuid().ToString("N")',
    'file-properties-preview-$runId',
    'profile 49 / schema 46',
    'Refusing to replace an existing file Properties preview root',
    'Refusing to replace an existing file Properties recovery root',
    'properties-$runId.txt',
    'aot-5b4c1b2b-file',
    '-AllowEarlyExit',
    'Wait-NaturalPreviewExit',
    'productionDataFingerprintBefore',
    'targetSha256Before',
    'directOwnerWindowHandle',
    'windowDestroyedAfterClose',
    'FilePropertiesDialogObserved',
    'runtimeFailureLogLines',
    'Refusing to clean an unowned file Properties preview root',
    'ownedRecoveryRootCleaned',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'The exact owned preview/recovery',
    'file-properties-session.json',
    'fixture-state.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C1B2BMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1B2BRequiredSmokeScriptPatterns) {
        if ($stage5B4C1B2BSmokeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C1B2BForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1B2BSourceFiles[1],
            $stage5B4C1B2BSourceFiles[2],
            $stage5B4C1B2BSourceFiles[3],
            $stage5B4C1B2BSourceFiles[10])) {
        foreach ($pattern in @(
                'FileOpenPicker',
                'FolderPicker',
                'NativeDrop',
                'IFileOperation',
                'SHFileOperation',
                'RecycleBinNativeBackend',
                'deskbox_native_')) {
            if ($stage5B4C1B2BSources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1B2BRustAbiUnchanged =
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[11]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[11]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C1B2BJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1B2BSources[$stage5B4C1B2BSourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1B2BSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotFilePropertiesSmoke|AotFilePropertiesFixture|FileSurfaceContent\.AotFilePropertiesSmoke|ShellContextMenuHelper|FileSurfaceContent\.SelectionAndMenus|Win32Helper)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1B2BExpectedWmc1510Count = 1241
$stage5B4C1B2BActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1C1SourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotPickerClipboardSmoke.cs",
    "src/DeskBox/Services/AotPickerClipboardFixture.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotPickerClipboardSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs",
    "src/DeskBox/Services/FileOpenPickerService.cs",
    "src/DeskBox/Services/FileService.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotLocalFileSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/run-aot-picker-clipboard-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C1C1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1C1SourceFiles) {
    $stage5B4C1C1Sources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1C1RunnerSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[0]] +
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[9]]
$stage5B4C1C1RequiredRunnerPatterns = @(
    'PickerClipboardStorageItemsPersistenceRestart',
    'picker-clipboard-storage-items-persistence-restart',
    'aot-5b4c1c1-file',
    'DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE',
    'CaptureAotManagedUiPickerClipboardAsync',
    'PickerClipboard = pickerClipboardPhase is null',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'run-aot-picker-clipboard-smoke.ps1'
)
$stage5B4C1C1MissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredRunnerPatterns) {
        if ($stage5B4C1C1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1C1ProductSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[4]] +
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[5]]
$stage5B4C1C1RequiredProductPatterns = @(
    'new FileOpenPicker(ownerWindowId)',
    'GetWindowIdFromWindow(',
    'SuggestedStartLocation = PickerLocationId.Desktop',
    'picker.SuggestedFolder = normalizedFolder',
    'await picker.PickMultipleFilesAsync()',
    'ValidateOwnerWindowHandle(ownerHwnd)',
    'FileOpenPickerService.PickFilesAsync(',
    '_hostWindowHandle,',
    'PasteDataPackageAsync(',
    'includeShellFileDropFallback'
)
$stage5B4C1C1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredProductPatterns) {
        if ($stage5B4C1C1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C1C1FixtureSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[2]]
$stage5B4C1C1RequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'PickerClipboardStorageItemsPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_RUN_ID',
    'aot-5b4c1c1-file',
    'runId is not { Length: 32 }',
    "character is not (>= '0' and <= '9')",
    "not (>= 'a' and <= 'f')",
    'dataPaths.IsDevelopmentRoot',
    'CaptureVisibleTopLevelWindowHandles',
    'ObservePickerDialogAsync',
    'Win32Helper.GW_OWNER',
    'Win32Helper.GA_ROOTOWNER',
    'OwnerChainContainsExpected',
    'IsSamePickerWindow(candidate)',
    'windowThreadId == candidate.WindowThreadId',
    'processId == candidate.ProcessId',
    'WindowDestroyedAfterAction',
    '"#32770"'
)
$stage5B4C1C1MissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredFixturePatterns) {
        if ($stage5B4C1C1FixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1C1ProbeSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[3]]
$stage5B4C1C1RequiredProbePatterns = @(
    'InvokeAotFilePickerAsync(',
    'PickAndImportFilesAsync(suggestedFolder)',
    'ObservePickerDialogAsync(',
    'ImportAotClipboardStorageItemsAsync(',
    'GetStorageItemsAsync(normalizedPaths)',
    'package.SetStorageItems(storageItems)',
    'DataPackageView view = package.GetView()',
    'view.Contains(StandardDataFormats.StorageItems)',
    'await view.GetStorageItemsAsync()',
    'includeShellFileDropFallback: false',
    'GlobalClipboardUntouched: true'
)
$stage5B4C1C1MissingProbePatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredProbePatterns) {
        if ($stage5B4C1C1ProbeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "probe::$pattern"
        }
    }
)
$stage5B4C1C1ScenarioSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[1]]
$stage5B4C1C1RequiredScenarioPatterns = @(
    'WaitForAotLocalFileSurfaceAsync(',
    'InteractionState = "CancelPending"',
    'InteractionState = "SelectionPending"',
    'PickerCancelNoChangeVerified',
    'PickerSelectionImported',
    'ClipboardStorageItemsImported',
    'PickerClipboardRestartMutationVerified',
    'PickerClipboardPostflightVerified',
    'OwnerChainContainsExpected',
    'GlobalClipboardUntouched',
    'SHA256.HashData(stream)'
)
$stage5B4C1C1MissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredScenarioPatterns) {
        if ($stage5B4C1C1ScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C1C1SmokeSource =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[9]] +
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[10]]
$stage5B4C1C1RequiredSmokeScriptPatterns = @(
    'run-aot-picker-clipboard-smoke.ps1',
    'PickerClipboardStorageItemsPersistenceRestart',
    '[Guid]::NewGuid().ToString("N")',
    'profile 50 / schema 47',
    'UIAutomationClient',
    'CancelPending',
    'SelectionPending',
    'ValuePattern',
    'InvokePattern',
    'FindVisibleDialog',
    'AutomationElement]::FromHandle',
    'Get-AutomationElementsById',
    'BM_CLICK',
    'WM_SETTEXT',
    'windowHandle',
    'GlobalClipboardUntouched',
    'Invoke-PickerClipboardPhase',
    '"Mutate"',
    '"VerifyRestore"',
    '"Postflight"',
    '$dataDirectory = Join-Path $DataRoot "data"',
    '$settingsPath = Join-Path $dataDirectory "settings.json"',
    'schemaVersion = 5',
    'hasResolvedInitialFileWidgetSetup = $true',
    'featureWidgetEnabledStates',
    'productionDataFingerprintBefore',
    'Refusing to clean an unowned picker/StorageItems',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'picker-clipboard-session.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C1C1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1C1RequiredSmokeScriptPatterns) {
        if ($stage5B4C1C1SmokeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C1C1ForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1C1SourceFiles[1],
            $stage5B4C1C1SourceFiles[2],
            $stage5B4C1C1SourceFiles[3],
            $stage5B4C1C1SourceFiles[10])) {
        foreach ($pattern in @(
                'NativeDrop',
                'IDropTarget',
                'IFileOperation',
                'deskbox_native_',
                'Clipboard.SetContent',
                'Clipboard.GetContent')) {
            if ($stage5B4C1C1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1C1RustAbiUnchanged =
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[11]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[11]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C1C1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1C1Sources[$stage5B4C1C1SourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1C1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotPickerClipboardSmoke|AotPickerClipboardFixture|FileSurfaceContent\.AotPickerClipboardSmoke|FileOpenPickerService|FileSurfaceContent\.xaml|FileService)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1C1ExpectedWmc1510Count = 1241
$stage5B4C1C1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C1C2ASourceFiles = @(
    "src/DeskBox/App.AotManagedUiSmoke.cs",
    "src/DeskBox/App.AotNativeDropSmoke.cs",
    "src/DeskBox/Services/AotNativeDropFixture.cs",
    "src/DeskBox/Views/ContentWidgetWindow.AotNativeDropSmoke.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotNativeDropSmoke.cs",
    "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml",
    "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ImportProgress.cs",
    "src/DeskBox/Helpers/NativeDropTarget.cs",
    "src/DeskBox/Helpers/NativeDropComDataReader.cs",
    "src/DeskBox/Helpers/NativeDropTargetComInterop.cs",
    "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "scripts/run-aot-native-drop-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C1C2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4C1C2ASourceFiles) {
    $stage5B4C1C2ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C1C2ARunnerSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[0]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[13]]
$stage5B4C1C2ARequiredRunnerPatterns = @(
    'NativeDropPersistenceRestart',
    'native-drop-persistence-restart',
    'DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE',
    'AotManagedUiNativeDropWidgetId',
    'bool isNativeDrop',
    '? AotManagedUiNativeDropWidgetId',
    'CaptureAotManagedUiNativeDropAsync',
    'NativeDrop = nativeDropPhase is null',
    'public AotManagedUiNativeDropEvidence? NativeDrop',
    'NormalShutdownRequested = true',
    'AotManagedUiNativeDropScenario)',
    'ShutdownApplicationAsync()',
    'run-aot-native-drop-smoke.ps1'
)
$stage5B4C1C2AMissingRunnerPatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredRunnerPatterns) {
        if ($stage5B4C1C2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "runner::$pattern"
        }
    }
)
$stage5B4C1C2AProductSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[5]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[6]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[9]]
$stage5B4C1C2ARequiredProductPatterns = @(
    'DragEnterEvent +=',
    'DragOverEvent +=',
    'DragLeaveEvent +=',
    'ObserveNativeFileDragPointer(',
    'file.ObserveNativeDragPointer(',
    'file.ClearDragSessionVisualState()',
    'copyWhenMapped',
    'NativeDropEffectPolicy.ResolveFeedbackEffect(',
    'DropEvent?.Invoke(',
    'internal bool IsRegistered => _registered',
    'HasActiveChildDropTargetVisual',
    'IsScreenPointInsideElement(Root, screenX, screenY)',
    'TransformToVisual(null)',
    'This path only clears stale state and never',
    'copyWhenMapped switch'
)
$stage5B4C1C2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredProductPatterns) {
        if ($stage5B4C1C2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C1C2AFixtureSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[2]]
$stage5B4C1C2ARequiredFixturePatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'NativeDropPersistenceRestart',
    'DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE',
    'DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_RUN_ID',
    'aot-5b4c1c2a-file',
    'runId is not { Length: 32 }',
    "character is not (>= '0' and <= '9')",
    "not (>= 'a' and <= 'f')",
    'dataPaths.IsDevelopmentRoot',
    'configuredPreviewRoot',
    'IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot)'
)
$stage5B4C1C2AMissingFixturePatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredFixturePatterns) {
        if ($stage5B4C1C2AFixtureSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "fixture::$pattern"
        }
    }
)
$stage5B4C1C2AProbeSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[3]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[4]]
$stage5B4C1C2ARequiredProbePatterns = @(
    'AcquireAotSmokeInterfacePointer()',
    'delegate* unmanaged[Stdcall]',
    'AotNativeHDropDataObject',
    'UnmanagedCallersOnly',
    'NativeFormatEtc*',
    'FileDropClipboardFormat = 15',
    'GlobalAlloc(',
    'Marshal.WriteInt32(locked, 16, 1)',
    'InvokeAotNativeDragLeaveCallback()',
    'PrimeAotNativeFolderHighlight(',
    'CaptureAotNativeFolderHighlightState(',
    'GetAotNativeFolderVisualState(',
    'thickness.Left >= 0.5',
    'borderBrush.Color.A > 0',
    'CaptureAotNativeDropProgress()',
    'Canvas.GetZIndex(ImportProgressCard)',
    'background is AcrylicBrush'
)
$stage5B4C1C2AMissingProbePatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredProbePatterns) {
        if ($stage5B4C1C2AProbeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "probe::$pattern"
        }
    }
)
$stage5B4C1C2AScenarioSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[1]]
$stage5B4C1C2ARequiredScenarioPatterns = @(
    'ProgrammaticGeneratedCcwHDrop',
    'PhysicalExplorerMouseVerified = false',
    'NativeDropScreenPointClearedStaleFolderHighlight',
    'NativeDropLeaveClearedFolderHighlight',
    'NativeDropCopyMoveSemanticsVerified',
    'OleCallbackReleasedBeforeProgress',
    'ProgressCardVisibleAboveDragVisual',
    'BackgroundIsAcrylicBrush',
    'CanvasZIndex >= 1000',
    'TranslationZ >= 64',
    'NativeDropRestartMutationVerified',
    'NativeDropPostflightVerified',
    'SHA256.HashData(stream)'
)
$stage5B4C1C2AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredScenarioPatterns) {
        if ($stage5B4C1C2AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C1C2AVisualSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[7]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[8]]
$stage5B4C1C2ARequiredVisualPatterns = @(
    'x:Name="ImportProgressCard"',
    'Canvas.ZIndex="1000"',
    'Translation="0,0,64"',
    'SystemControlAcrylicElementBrush',
    'ImportCardShowDelay',
    'TimeSpan.FromMilliseconds(120)',
    'ImportProgressCard.Visibility = Visibility.Visible'
)
$stage5B4C1C2AMissingVisualPatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredVisualPatterns) {
        if ($stage5B4C1C2AVisualSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "visual::$pattern"
        }
    }
)
$stage5B4C1C2ASmokeSource =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[13]] +
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[14]]
$stage5B4C1C2ARequiredSmokeScriptPatterns = @(
    'run-aot-native-drop-smoke.ps1',
    'NativeDropPersistenceRestart',
    '[Guid]::NewGuid().ToString("N")',
    'profile 56 / schema 53',
    '$largeFileLength = 384MB',
    'ProgrammaticGeneratedCcwHDrop',
    'physicalExplorerMouseVerified = $false',
    'Invoke-NativeDropPhase',
    '"Mutate"',
    '"VerifyRestore"',
    '"Postflight"',
    'copyImport.duringImport.cardVisible',
    'backgroundIsAcrylicBrush',
    'canvasZIndex',
    'translationZ',
    'nativePointerClear.highlightActiveAfter',
    'nativeLeaveClear.highlightActiveAfter',
    'productionDataFingerprintBefore',
    'Refusing to clean an unowned native-drop root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'native-drop-session.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C1C2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C1C2ARequiredSmokeScriptPatterns) {
        if ($stage5B4C1C2ASmokeSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C1C2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C1C2ASourceFiles[1],
            $stage5B4C1C2ASourceFiles[2],
            $stage5B4C1C2ASourceFiles[3],
            $stage5B4C1C2ASourceFiles[4],
            $stage5B4C1C2ASourceFiles[14])) {
        foreach ($pattern in @(
                'Clipboard.SetContent',
                'Clipboard.GetContent',
                'SendInput',
                'mouse_event',
                'Process.Start("explorer',
                'deskbox_native_')) {
            if ($stage5B4C1C2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C1C2ARustAbiUnchanged =
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[15]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[15]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C1C2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C1C2ASources[$stage5B4C1C2ASourceFiles[0]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C1C2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotNativeDropSmoke|AotNativeDropFixture|ContentWidgetWindow\.AotNativeDropSmoke|FileSurfaceContent\.AotNativeDropSmoke|ContentWidgetWindow\.NativeDragDrop|NativeDropTarget|NativeDropComDataReader|NativeDropTargetComInterop)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C1C2AExpectedWmc1510Count = 1241
$stage5B4C1C2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C2ASourceFiles = @(
    "src/DeskBox/App.AotHotkeySmoke.cs",
    "src/DeskBox/Helpers/Win32Helper.AotHotkeySmoke.cs",
    "src/DeskBox/Services/GlobalHotkeyService.cs",
    "src/DeskBox/Services/SearchHotkeyService.cs",
    "src/DeskBox/Services/ReservedHotkeyHookService.cs",
    "src/DeskBox/Services/WinSpaceHotkeyStateMachine.cs",
    "src/DeskBox/App.xaml.cs",
    "scripts/run-aot-hotkey-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C2ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4C2ASourceFiles) {
    $stage5B4C2ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C2AScenarioSource =
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[0]]
$stage5B4C2ARequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_HOTKEY_SMOKE',
    'DESKBOX_AOT_HOTKEY_PHASE',
    'DESKBOX_AOT_HOTKEY_RUN_ID',
    'RegistrationLifecycle',
    'Guid.TryParseExact(runId, "N"',
    'dataPaths.IsDevelopmentRoot',
    'RuntimeFeature.IsDynamicCodeSupported',
    'SyntheticSendInputForRegisterHotKeyOnly',
    'PhysicalStandardKeyboardVerified = false',
    'PhysicalWinSpaceVerified = false',
    'PhysicalRecorderVerified = false',
    'GlobalConflictRolledBack',
    'SearchConflictRolledBack',
    'ReservedHookSyntheticTriggerAttempted = false',
    'release-startup-reregistered',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()'
)
$stage5B4C2AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C2ARequiredScenarioPatterns) {
        if ($stage5B4C2AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C2AHelperSource =
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[1]]
$stage5B4C2ARequiredHelperPatterns = @(
    'TrySendTaggedKeyChord(',
    'SendInput(',
    'KEYEVENTF_KEYUP',
    'A partial SendInput must not leave a test key logically pressed',
    'TrySendKeyboardEvent('
)
$stage5B4C2AMissingHelperPatterns = @(
    foreach ($pattern in $stage5B4C2ARequiredHelperPatterns) {
        if ($stage5B4C2AHelperSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "helper::$pattern"
        }
    }
)
$stage5B4C2AProductSource =
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[2]] +
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[3]] +
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[4]] +
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[5]]
$stage5B4C2ARequiredProductPatterns = @(
    'public long ReceivedCount',
    'public long InvocationCount',
    'public long DispatchFailureCount',
    'Interlocked.Increment(ref _receivedSequence)',
    'Interlocked.Increment(ref _invocationSequence)',
    'Interlocked.Increment(ref _dispatchFailureSequence)',
    'settings.SearchHotkeyModifiers = previousModifiers',
    'settings.SearchHotkeyKey = previousVirtualKey',
    'Rollback registration failed previousGesture=',
    'LLKHF_INJECTED',
    'TrySendTaggedKeyPress(',
    '_lifecycleGeneration',
    'public uint ReservedHookThreadId',
    'public int ReservedHookLastErrorCode'
)
$stage5B4C2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C2ARequiredProductPatterns) {
        if ($stage5B4C2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C2ARunnerSource =
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[6]] +
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[7]]
$stage5B4C2ARequiredSmokeScriptPatterns = @(
    'StartAotHotkeySmokeIfRequested();',
    'profile 56 / schema 53',
    'Invoke-HotkeyPhase',
    '-Phase "Primary"',
    '-Phase "Release"',
    'processIdsDistinct',
    'executableHashesMatch',
    'Wait-NaturalPreviewExit',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'SyntheticSendInputForRegisterHotKeyOnly',
    'physicalStandardKeyboardVerified = $false',
    'physicalWinSpaceVerified = $false',
    'physicalRecorderVerified = $false',
    'reservedHookSyntheticTriggerAttempted = $false',
    'Refusing to clean an unowned hotkey root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'hotkey-session.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C2ARequiredSmokeScriptPatterns) {
        if ($stage5B4C2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C2ASourceFiles[0],
            $stage5B4C2ASourceFiles[1],
            $stage5B4C2ASourceFiles[7])) {
        foreach ($pattern in @(
                'PhysicalStandardKeyboardVerified = true',
                'PhysicalWinSpaceVerified = true',
                'PhysicalRecorderVerified = true',
                'ReservedHookSyntheticTriggerAttempted = true',
                'deskbox_native_')) {
            if ($stage5B4C2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C2ARustAbiUnchanged =
    $stage5B4C2ASources[$stage5B4C2ASourceFiles[8]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C2ASources[$stage5B4C2ASourceFiles[8]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C2AScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotHotkeySmoke|Win32Helper\.AotHotkeySmoke|GlobalHotkeyService|SearchHotkeyService|ReservedHotkeyHookService|WinSpaceHotkeyStateMachine)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C2AExpectedWmc1510Count = 1241
$stage5B4C2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3ASourceFiles = @(
    "src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Services/TodoReminderService.cs",
    "src/DeskBox/Services/TodoRecurrenceService.cs",
    "src/DeskBox/Services/TodoWidgetStore.cs",
    "src/DeskBox/Services/SettingsService.cs",
    "src/DeskBox/Services/LocalizationService.cs",
    "src/DeskBox/Models/TodoItem.cs",
    "src/DeskBox/Models/TodoRecurrence.cs",
    "src/DeskBox/Models/TodoReminderOptions.cs",
    "src/DeskBox/Models/TodoWidgetData.cs",
    "scripts/run-aot-todo-recurrence-reminder-smoke.ps1",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "native/deskbox-native/src/lib.rs"
)
$stage5B4C3ASources = [ordered]@{}
foreach ($sourceFile in $stage5B4C3ASourceFiles) {
    $stage5B4C3ASources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C3AScenarioSource =
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[0]]
$stage5B4C3ARequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE',
    'DESKBOX_AOT_TODO_RECURRENCE_REMINDER_PHASE',
    'DESKBOX_AOT_TODO_RECURRENCE_REMINDER_RUN_ID',
    'DeterministicStateMatrix',
    'Guid.TryParseExact(runId, "N"',
    'dataPaths.IsDevelopmentRoot',
    'RuntimeFeature.IsDynamicCodeSupported',
    'CapturedCallbackOnly',
    'SystemNotificationAttempted = false',
    'new SettingsService(settingsRoot)',
    'new TodoWidgetStore(widgetsRoot, widgetId)',
    'dispatcherQueue: null',
    '() => currentClock',
    'SeedAndSnooze',
    'SnoozeAndComplete',
    'NextOccurrence',
    'Restore',
    'Postflight',
    'initial-due-candidates-exact',
    'snooze-deadline-fired-once',
    'next-occurrence-generated',
    'next-reminder-fired-once',
    'restart-dismissal-persisted',
    'cleanup-postflight-empty',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'AotTodoRecurrenceReminderJsonContext.Default'
)
$stage5B4C3AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3ARequiredScenarioPatterns) {
        if ($stage5B4C3AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3AProductSource =
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[2]] +
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[3]] +
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[4]]
$stage5B4C3ARequiredProductPatterns = @(
    'Func<string, TodoWidgetStore> storeFactory',
    'Func<DateTimeOffset> clock',
    'public async Task<int> CheckNowAsync(DateTimeOffset now)',
    'public async Task<bool> SnoozeAsync(',
    'item.SnoozedUntil = snoozedUntil',
    'item.ReminderDismissedForDueDate = item.DueDate',
    'public async Task<bool> CompleteAsync(',
    'TodoRecurrenceService.TryCreateNextOccurrence',
    'item.GeneratedNextItemId = nextItem.Id',
    'ReminderLastNotifiedAt = null',
    'ReminderDismissedForDueDate = null',
    'SnoozedUntil = null',
    'SnoozeLastNotifiedAt = null',
    'internal TodoWidgetStore(string widgetsDataRoot, string widgetId)',
    'public Task ClearAsync()'
)
$stage5B4C3AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3ARequiredProductPatterns) {
        if ($stage5B4C3AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3ARunnerSource =
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[1]] +
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[11]] +
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[12]]
$stage5B4C3ARequiredSmokeScriptPatterns = @(
    'StartAotTodoRecurrenceReminderSmokeIfRequested();',
    'TodoRecurrenceReminderPersistenceRestart',
    'run-aot-todo-recurrence-reminder-smoke.ps1',
    'profile 56 / schema 53',
    '[Guid]::NewGuid().ToString("N")',
    'Invoke-TodoRecurrenceReminderPhase',
    '"SeedAndSnooze"',
    '"SnoozeAndComplete"',
    '"NextOccurrence"',
    '"Restore"',
    '"Postflight"',
    'processIdsDistinct',
    'executableHashesMatch',
    'Wait-NaturalPreviewExit',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'systemNotificationAttempted = $false',
    '[TodoReminder] Native notification shown',
    '[TodoReminder] Tray notification fallback shown',
    'Refusing to clean an unowned Todo recurrence/reminder root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'todo-session.json',
    'Stop-ExactPreviewProcess'
)
$stage5B4C3AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3ARequiredSmokeScriptPatterns) {
        if ($stage5B4C3ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3ASourceFiles[0],
            $stage5B4C3ASourceFiles[11])) {
        foreach ($pattern in @(
                'ShowTodoReminderNotification(',
                'NativeAppNotification',
                'AppNotificationManager',
                'ToastNotification',
                'deskbox_native_')) {
            if ($stage5B4C3ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C3ARustAbiUnchanged =
    $stage5B4C3ASources[$stage5B4C3ASourceFiles[13]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3ASources[$stage5B4C3ASourceFiles[13]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3AScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoRecurrenceReminderSmoke|TodoReminderService|TodoRecurrenceService|TodoWidgetStore|TodoItem|TodoRecurrence|TodoReminderOptions|TodoWidgetData)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3AExpectedWmc1510Count = 1241
$stage5B4C3AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3B1SourceFiles = @(
    "src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs",
    "src/DeskBox/App.xaml.cs",
    "src/DeskBox/Services/NativeAppNotificationService.cs",
    "src/DeskBox/Package.appxmanifest",
    "scripts/run-aot-todo-notification-smoke.ps1",
    "scripts/run-aot-managed-ui-smoke.ps1",
    "native/deskbox-native/src/lib.rs",
    "src/DeskBox/DeskBox.csproj"
)
$stage5B4C3B1Sources = [ordered]@{}
foreach ($sourceFile in $stage5B4C3B1SourceFiles) {
    $stage5B4C3B1Sources[$sourceFile] =
        Get-Content -LiteralPath (Join-Path $repoRoot $sourceFile) -Raw
}
$stage5B4C3B1ScenarioSource =
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[0]]
$stage5B4C3B1RequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_NOTIFICATION_SMOKE',
    'DESKBOX_AOT_TODO_NOTIFICATION_PHASE',
    'DESKBOX_AOT_TODO_NOTIFICATION_RUN_ID',
    'RealDisplayAndCleanup',
    'Guid.TryParseExact(runId, "N"',
    'dataPaths.IsDevelopmentRoot',
    'RuntimeFeature.IsDynamicCodeSupported',
    'ShowAndInspect',
    'Cleanup',
    'Postflight',
    'AppNotificationManager.Default.Setting',
    'AppNotificationSetting.Enabled',
    'TryShowNativeTodoReminderNotification(',
    'new NativeAppNotificationOptions(result.SingleTag, result.Group)',
    'new NativeAppNotificationOptions(result.AggregateTag, result.Group)',
    'XDocument.Parse(snapshot.Payload',
    'ParseAotTodoNotificationArguments(launch)',
    "['&', ';']",
    'single-payload-actions-and-snooze-options-exact',
    'aggregate-payload-has-no-actions',
    'cross-process-history-reloaded',
    'single-tag-group-cleanup-exact',
    'aggregate-tag-group-cleanup-exact',
    'new-process-postflight-empty',
    'SystemNotificationAttempted = true',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'AotTodoNotificationJsonContext.Default'
)
$stage5B4C3B1MissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3B1RequiredScenarioPatterns) {
        if ($stage5B4C3B1ScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3B1ProductSource =
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[1]] +
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[2]] +
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[3]]
$stage5B4C3B1RequiredProductPatterns = @(
    'private bool TryShowNativeTodoReminderNotification(',
    'notification.Count == 1',
    'TodoReminderActionComplete',
    'TodoReminderActionSnooze',
    'TodoReminderSnooze10Minutes',
    'TodoReminderSnooze30Minutes',
    'TodoReminderSnooze1Hour',
    'TodoReminderSnoozeTomorrow',
    'builder.SetGroup(options.Group)',
    'builder.SetTag(options.Tag)',
    'AppNotificationManager.Default.Register()',
    'AppNotificationManager.Default.GetAllAsync()',
    'AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group)',
    'AppNotificationManager.Default.Unregister()',
    'public bool IsRegistered => _isRegistered;',
    'public bool Unregister()',
    'windows.toastNotificationActivation',
    '----AppNotificationActivated:'
)
$stage5B4C3B1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3B1RequiredProductPatterns) {
        if ($stage5B4C3B1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3B1RunnerSource =
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[1]] +
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[4]] +
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[5]] +
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[7]]
$stage5B4C3B1RequiredSmokeScriptPatterns = @(
    'StartAotTodoNotificationLifecycleSmokeIfRequested();',
    'TodoNotificationDisplayCleanup',
    'run-aot-todo-notification-smoke.ps1',
    'profile 56 / schema 53',
    '[Guid]::NewGuid().ToString("N")',
    'Invoke-TodoNotificationPhase',
    '"ShowAndInspect"',
    '"Cleanup"',
    '"Postflight"',
    'processIdsDistinct',
    'executableHashesMatch',
    'Wait-NaturalPreviewExit',
    'realSystemNotificationsShown = 2',
    'exactTagGroupCleanup = $true',
    'activationObserved = $false',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    '[Notification] Native notification activated',
    'Refusing to clean an unowned Todo notification root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'notification-session.json',
    'Stop-ExactPreviewProcess',
    'Native AOT stage 5B-4C3B2B1'
)
$stage5B4C3B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3B1RequiredSmokeScriptPatterns) {
        if ($stage5B4C3B1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3B1ForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3B1SourceFiles[0],
            $stage5B4C3B1SourceFiles[4])) {
        foreach ($pattern in @(
                'CompleteTodoReminderFromNotificationAsync(',
                'SnoozeTodoReminderFromNotificationAsync(',
                'AppInstance.GetCurrent()',
                'RedirectActivation',
                'RemoveAllAsync',
                'RemoveByGroupAsync',
                'deskbox_native_')) {
            if ($stage5B4C3B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
    foreach ($pattern in @('RemoveAllAsync', 'RemoveByGroupAsync')) {
        if ($stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[2]].IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -ge 0) {
            "$($stage5B4C3B1SourceFiles[2])::$pattern"
        }
    }
)
$stage5B4C3B1RustAbiUnchanged =
    $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[6]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3B1Sources[$stage5B4C3B1SourceFiles[6]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3B1JsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B1ScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoNotificationLifecycleSmoke|NativeAppNotificationService)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3B1ExpectedWmc1510Count = 1241
$stage5B4C3B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3B2ASourceFiles = @(
    "src\DeskBox\App.AotTodoNotificationActivationSmoke.cs",
    "src\DeskBox\App.xaml.cs",
    "src\DeskBox\Services\TodoNotificationActivationRouter.cs",
    "src\DeskBox\Services\TodoReminderService.cs",
    "src\DeskBox\Services\TodoWidgetStore.cs",
    "scripts\run-aot-todo-notification-activation-smoke.ps1",
    "scripts\run-aot-managed-ui-smoke.ps1",
    "native\deskbox-native\src\lib.rs",
    "src\DeskBox\DeskBox.csproj"
)
$stage5B4C3B2ASources = @{}
foreach ($sourceFile in $stage5B4C3B2ASourceFiles) {
    $sourcePath = Join-Path $repoRoot $sourceFile
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Stage 5B-4C3B2A source is missing: '$sourcePath'."
    }

    $stage5B4C3B2ASources[$sourceFile] =
        Get-Content -LiteralPath $sourcePath -Raw
}
$stage5B4C3B2AScenarioSource =
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[0]]
$stage5B4C3B2ARequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_SMOKE',
    'DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_PHASE',
    'DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_RUN_ID',
    'DeterministicActionRouting',
    'Guid.TryParseExact(runId, "N"',
    'dataPaths.IsDevelopmentRoot',
    'RuntimeFeature.IsDynamicCodeSupported',
    'RouteAndPersist',
    'VerifyAndClear',
    'Postflight',
    'ParseNotificationArguments(rawArguments)',
    'TodoNotificationActivationRouter.RouteAsync(',
    'AotTodoNotificationActivationClock',
    'AotTodoNotificationActivationTimeZone',
    'semicolon-body-open-routed',
    'ampersand-grammar-compatible',
    'complete-action-idempotent',
    'snooze-10m-persisted-and-idempotent',
    'snooze-30m-persisted-and-idempotent',
    'snooze-1h-persisted-and-idempotent',
    'snooze-tomorrow-persisted-and-idempotent',
    'legacy-snooze10-compatible',
    'invalid-inputs-rejected-without-mutation',
    'cross-process-action-state-reloaded',
    'activation-store-cleared',
    'postflight-empty-and-stable',
    'route-matrix-complete',
    'SystemNotificationAttempted = false',
    'ExternalActivationAttempted = false',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'AotTodoNotificationActivationJsonContext.Default'
)
$stage5B4C3B2AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3B2ARequiredScenarioPatterns) {
        if ($stage5B4C3B2AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3B2AProductSource =
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[1]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[2]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[3]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[4]]
$stage5B4C3B2ARequiredProductPatterns = @(
    'arguments.Split(',
    "['&', ';']",
    'RouteTodoNotificationActivationAsync(',
    'internal static class TodoNotificationActivationRouter',
    'ActionComplete = "complete"',
    'ActionSnooze = "snooze"',
    'LegacyActionSnooze10 = "snooze10"',
    'SnoozeUntilAsync(',
    'CompleteAsync(',
    'DispositionRejectedUnsupportedSnooze',
    'GetTomorrowAtNine(',
    'TimeZoneInfo',
    'showTargetAsync(',
    'refreshAsync(',
    'showSnoozeConfirmationAsync('
)
$stage5B4C3B2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3B2ARequiredProductPatterns) {
        if ($stage5B4C3B2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3B2ARunnerSource =
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[1]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[5]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[6]] +
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[8]]
$stage5B4C3B2ARequiredSmokeScriptPatterns = @(
    'StartAotTodoNotificationActivationSmokeIfRequested();',
    'TodoNotificationActionRouting',
    'run-aot-todo-notification-activation-smoke.ps1',
    'profile 56 / schema 53',
    '[Guid]::NewGuid().ToString("N")',
    'Invoke-TodoNotificationActivationPhase',
    '"RouteAndPersist"',
    '"VerifyAndClear"',
    '"Postflight"',
    'processIdsDistinct',
    'executableHashesMatch',
    'Wait-NaturalPreviewExit',
    'routeAndPersistRoutes = 18',
    'verifyAndClearRoutes = 2',
    'systemNotificationAttempted = $false',
    'externalActivationAttempted = $false',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'Refusing to replace an existing or unowned Todo notification activation root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'activation-session.json',
    'Stop-ExactPreviewProcess',
    'Native AOT stage 5B-4C3B2B1'
)
$stage5B4C3B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3B2ARequiredSmokeScriptPatterns) {
        if ($stage5B4C3B2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3B2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3B2ASourceFiles[0],
            $stage5B4C3B2ASourceFiles[2],
            $stage5B4C3B2ASourceFiles[5])) {
        foreach ($pattern in @(
                'AppNotificationManager',
                'TryShowNativeTodoReminderNotification(',
                'StorePendingNativeNotificationActivationArguments(',
                'TakePendingNativeNotificationActivationArguments(',
                'AppInstance.GetCurrent()',
                'RedirectActivation',
                'RemoveAllAsync',
                'RemoveByGroupAsync',
                'deskbox_native_')) {
            if ($stage5B4C3B2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C3B2ARustAbiUnchanged =
    $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[7]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3B2ASources[$stage5B4C3B2ASourceFiles[7]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3B2AJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2AScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoNotificationActivationSmoke|TodoNotificationActivationRouter)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3B2AExpectedWmc1510Count = 1241
$stage5B4C3B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3B2B1SourceFiles = @(
    "src\DeskBox\App.AotTodoNotificationForwardingSmoke.cs",
    "src\DeskBox\App.xaml.cs",
    "src\DeskBox\Services\NativeNotificationActivationEnvelopeStore.cs",
    "src\DeskBox\Services\NativeAppNotificationService.cs",
    "src\DeskBox\Services\TodoNotificationActivationRouter.cs",
    "src\DeskBox\Services\TodoReminderService.cs",
    "scripts\run-aot-todo-notification-forwarding-smoke.ps1",
    "scripts\run-aot-managed-ui-smoke.ps1",
    "scripts\start-aot-preview.ps1",
    "native\deskbox-native\src\lib.rs",
    "src\DeskBox\DeskBox.csproj"
)
$stage5B4C3B2B1Sources = @{}
foreach ($sourceFile in $stage5B4C3B2B1SourceFiles) {
    $sourcePath = Join-Path $repoRoot $sourceFile
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Stage 5B-4C3B2B1 source is missing: '$sourcePath'."
    }

    $stage5B4C3B2B1Sources[$sourceFile] =
        Get-Content -LiteralPath $sourcePath -Raw
}
$stage5B4C3B2B1ScenarioSource =
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[0]]
$stage5B4C3B2B1RequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_SMOKE',
    'DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_PHASE',
    'DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_RUN_ID',
    'EnvelopeAndSingleInstance',
    'SeedColdStart',
    'ColdStartConsume',
    'PrimaryAwait',
    'SecondaryForward',
    'Postflight',
    'TryGetAotTodoNotificationForwardingActivation()',
    'TryGetAotTodoNotificationForwardingClock()',
    'ShouldSuppressAotTodoNotificationForwardingSystemNotification()',
    'OnPendingNativeNotificationActivationConsumed(',
    'OnPendingNativeNotificationActivationRejected(',
    'atomic-store-duplicate-and-corrupt-seeded',
    'cold-start-drain-preserved-user-input',
    'cold-start-mutation-persisted',
    'live-second-instance-forwarding-persisted',
    'postflight-state-reloaded-and-spool-empty',
    'fixture-store-cleared',
    'SystemNotificationAttempted = false',
    'ExternalWindowsActivationAttempted = false',
    'NormalShutdownRequested = true',
    'ShutdownApplicationAsync()',
    'AotTodoNotificationForwardingJsonContext.Default'
)
$stage5B4C3B2B1MissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3B2B1RequiredScenarioPatterns) {
        if ($stage5B4C3B2B1ScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3B2B1ProductSource =
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[1]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[2]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[3]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[4]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[5]]
$stage5B4C3B2B1RequiredProductPatterns = @(
    'NativeAppNotificationActivation? nativeNotificationActivation',
    'PendingNativeNotificationActivationStore.Store(nativeNotificationActivation)',
    'CompleteExternalActivationInitializationAsync()',
    'DrainPendingNativeNotificationActivations()',
    'Forwarded activation drain yielded after',
    'PendingNativeNotificationActivationStore.HasPendingActivation',
    'new NativeAppNotificationActivation(',
    'envelope.ActivationSource',
    'envelope.CreatedAtUtc',
    'envelope.SourceProcessId',
    'envelope.EnvelopeId',
    'NativeNotificationActivationEnvelopeTakeDisposition.Rejected',
    'StartAotTodoNotificationForwardingSmokeIfRequested();',
    'internal sealed class NativeNotificationActivationEnvelopeStore',
    'pending-notification-activations',
    'pending-notification-activation.txt',
    'NativeNotificationActivationEnvelopeJsonContext.Default.Envelope',
    'File.Move(tempPath, finalPath, overwrite: false)',
    'NativeNotificationActivationEnvelopeWriteDisposition.Duplicate',
    'MaxEnvelopeBytes',
    'MaxUserInputEntries',
    'HasPendingActivation',
    'RecoverAbandonedClaims();',
    'Process.GetProcessById(processId)',
    'IsLegacyArgumentsOnly',
    'IReadOnlyDictionary<string, string> UserInput'
)
$stage5B4C3B2B1MissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3B2B1RequiredProductPatterns) {
        if ($stage5B4C3B2B1ProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3B2B1RunnerSource =
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[1]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[6]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[7]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[8]] +
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[10]]
$stage5B4C3B2B1RequiredSmokeScriptPatterns = @(
    'TodoNotificationEnvelopeForwarding',
    'run-aot-todo-notification-forwarding-smoke.ps1',
    '$requiredAuditProfileVersion = 58',
    '$requiredSummarySchemaVersion = 55',
    '-NoStop',
    '-ExpectExistingInstance',
    'ExistingInstanceActivated',
    'Wait-NaturalPreviewExit',
    'processIdsDistinct',
    'Count -eq 5',
    'typedUserInputPreserved = $true',
    'corruptEnvelopeRejected = $true',
    'duplicateEnvelopeRejected = $true',
    'coldStartDrainVerified = $true',
    'realSecondaryProcessForwardingVerified = $true',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'Refusing to clean an unowned Todo notification forwarding root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'forwarding-session.json',
    'Stop-ExactPreviewProcess',
    'Native AOT stage 5B-4C3B2B1'
)
$stage5B4C3B2B1MissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3B2B1RequiredSmokeScriptPatterns) {
        if ($stage5B4C3B2B1RunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3B2B1ForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3B2B1SourceFiles[0],
            $stage5B4C3B2B1SourceFiles[2],
            $stage5B4C3B2B1SourceFiles[6])) {
        foreach ($pattern in @(
                'AppNotificationManager',
                'StorePendingNativeNotificationActivationArguments(',
                'TakePendingNativeNotificationActivationArguments(',
                'RedirectActivation',
                'RemoveAllAsync',
                'RemoveByGroupAsync',
                'deskbox_native_')) {
            if ($stage5B4C3B2B1Sources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C3B2B1RustAbiUnchanged =
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[9]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[9]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3B2B1ScenarioJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2B1ScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2B1StoreJsonCallCount = [regex]::Matches(
    $stage5B4C3B2B1Sources[$stage5B4C3B2B1SourceFiles[2]],
    'JsonSerializer\.(?:Serialize|Deserialize)\b').Count
$stage5B4C3B2B1SourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoNotificationForwardingSmoke|NativeNotificationActivationEnvelopeStore)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3B2B1ExpectedWmc1510Count = 1241
$stage5B4C3B2B1ActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3B2B2ASourceFiles = @(
    "src\DeskBox\App.AotTodoNotificationSurfaceSmoke.cs",
    "src\DeskBox\App.AotManagedUiSmoke.cs",
    "src\DeskBox\App.xaml.cs",
    "src\DeskBox\Services\TodoNotificationActivationRouter.cs",
    "src\DeskBox\Services\WidgetManager.FeatureWidgets.cs",
    "src\DeskBox\Controls\WidgetContents\TodoWidgetContent.xaml.cs",
    "scripts\run-aot-todo-notification-surface-smoke.ps1",
    "scripts\run-aot-managed-ui-smoke.ps1",
    "scripts\start-aot-preview.ps1",
    "native\deskbox-native\src\lib.rs",
    "src\DeskBox\DeskBox.csproj"
)
$stage5B4C3B2B2ASources = @{}
foreach ($sourceFile in $stage5B4C3B2B2ASourceFiles) {
    $sourcePath = Join-Path $repoRoot $sourceFile
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Stage 5B-4C3B2B2A source is missing: '$sourcePath'."
    }

    $stage5B4C3B2B2ASources[$sourceFile] =
        Get-Content -LiteralPath $sourcePath -Raw
}
$stage5B4C3B2B2AScenarioSource =
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[0]]
$stage5B4C3B2B2ARequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_NOTIFICATION_SURFACE_SMOKE',
    'TodoNotificationSurfaceRouting',
    'Stage = "5B-4C3B2B2A"',
    'RouteTodoNotificationActivationAsync(',
    'CaptureAotTodoNotificationSurfaceHostAsync(',
    'body-visible-item-located',
    'complete-visible-refresh-proved',
    'snooze-user-input-visible-refresh-proved',
    'SystemNotificationAttempted = false',
    'ExternalWindowsActivationAttempted = false',
    'UserClickVerified = false',
    'controlled-input-not-mislabeled-as-real-click',
    'WriteAotManagedUiResult(resultPath, result)',
    'ShutdownApplicationAsync()'
)
$stage5B4C3B2B2AMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2ARequiredScenarioPatterns) {
        if ($stage5B4C3B2B2AScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3B2B2AProductSource =
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[2]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[3]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[4]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[5]]
$stage5B4C3B2B2ARequiredProductPatterns = @(
    'Task<TodoNotificationActivationRouteResult?> RouteTodoNotificationActivationAsync(',
    'DispositionTargetUnavailable',
    'bool TargetPresented',
    'bool RefreshCompleted',
    'Task<TodoReminderTargetPresentationResult> ShowTodoReminderTargetAsync(',
    'await window.ContentReadyTask;',
    'WaitForTodoReminderSurfaceLoadedAsync',
    'WaitForTodoReminderSurfaceCommitAsync',
    'CompositionTarget.Rendering += rendering;',
    'TargetPresented: targetPresented',
    'public bool RevealReminderItem(',
    'await adapter.RefreshAsync();',
    'targetPresented={result.TargetPresented}',
    'refreshCompleted={result.RefreshCompleted}'
)
$stage5B4C3B2B2AMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2ARequiredProductPatterns) {
        if ($stage5B4C3B2B2AProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3B2B2ARunnerSource =
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[0]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[1]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[2]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[6]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[7]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[8]] +
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[10]]
$stage5B4C3B2B2ARequiredSmokeScriptPatterns = @(
    'TodoNotificationSurfaceRouting',
    'run-aot-todo-notification-surface-smoke.ps1',
    '$requiredAuditProfileVersion = 58',
    '$requiredSummarySchemaVersion = 55',
    '-AllowEarlyExit',
    '-StartupWaitSeconds 1',
    'Wait-NaturalPreviewExit',
    'ProcessCount = 1',
    'NaturalExitCount = 1',
    'Production data changed during the Todo notification surface smoke',
    'Refusing to clean an unowned Todo notification surface root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'surface-session.json',
    'StartAotTodoNotificationSurfaceSmokeIfRequested();',
    'Native AOT stage 5B-4C3B2B2A'
)
$stage5B4C3B2B2AMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2ARequiredSmokeScriptPatterns) {
        if ($stage5B4C3B2B2ARunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3B2B2AForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3B2B2ASourceFiles[0],
            $stage5B4C3B2B2ASourceFiles[6])) {
        foreach ($pattern in @(
                'AppNotificationManager',
                'TryShowNativeTodoReminderNotification(',
                'UserClickVerified = true',
                'RemoveAllAsync',
                'RemoveByGroupAsync',
                'deskbox_native_')) {
            if ($stage5B4C3B2B2ASources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C3B2B2ARustAbiUnchanged =
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[9]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[9]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3B2B2AScenarioJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2B2AScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2B2AManagedUiJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2B2ASources[$stage5B4C3B2B2ASourceFiles[1]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2B2ASourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoNotificationSurfaceSmoke|App\.xaml|TodoNotificationActivationRouter|WidgetManager\.FeatureWidgets|TodoWidgetContent\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3B2B2AExpectedWmc1510Count = 1241
$stage5B4C3B2B2AActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
$stage5B4C3B2B2BSourceFiles = @(
    "src\DeskBox\App.AotTodoNotificationUserClickSmoke.cs",
    "src\DeskBox\App.AotManagedUiSmoke.cs",
    "src\DeskBox\App.xaml.cs",
    "src\DeskBox\Services\NativeAppNotificationService.cs",
    "src\DeskBox\Services\NativeNotificationActivationEnvelopeStore.cs",
    "src\DeskBox\Services\TodoNotificationActivationRouter.cs",
    "src\DeskBox\Controls\WidgetContents\TodoWidgetContent.xaml.cs",
    "scripts\run-aot-todo-notification-user-click-smoke.ps1",
    "scripts\start-aot-preview.ps1",
    "native\deskbox-native\src\lib.rs",
    "src\DeskBox\DeskBox.csproj"
)
$stage5B4C3B2B2BSources = @{}
foreach ($sourceFile in $stage5B4C3B2B2BSourceFiles) {
    $sourcePath = Join-Path $repoRoot $sourceFile
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Stage 5B-4C3B2B2B source is missing: '$sourcePath'."
    }

    $stage5B4C3B2B2BSources[$sourceFile] =
        Get-Content -LiteralPath $sourcePath -Raw
}
$stage5B4C3B2B2BScenarioSource =
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[0]]
$stage5B4C3B2B2BRequiredScenarioPatterns = @(
    '#if DESKBOX_NATIVE_AOT',
    'DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_SMOKE',
    'RealWindowsNotificationUserClick',
    'RunningMatrix',
    'ColdSeed',
    'ColdConsume',
    'Postflight',
    'Stage = "5B-4C3B2B2B"',
    'OnNativeNotificationActivationObserved(',
    'OnTodoNotificationActivationRouteObserved(',
    'NativeAppNotificationActivationSource.NotificationInvokedEvent or',
    'NativeAppNotificationActivationSource.CurrentAppInstance',
    'TryShowNativeTodoReminderNotification(',
    'TimeSpan.FromMinutes(10)',
    'matchingRoutes.Count == 1',
    '{caseName}-real-todo-surface-state-exact',
    'cold-start-user-click-and-surface-verified',
    'SystemNotificationAttempted = true',
    'ExternalWindowsActivationObserved = true',
    'UserClickVerified = true',
    'RemoveByTagAndGroupAsync(',
    'ShutdownApplicationAsync()'
)
$stage5B4C3B2B2BMissingScenarioPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2BRequiredScenarioPatterns) {
        if ($stage5B4C3B2B2BScenarioSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "scenario::$pattern"
        }
    }
)
$stage5B4C3B2B2BProductSource =
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[2]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[3]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[4]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[5]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[6]]
$stage5B4C3B2B2BRequiredProductPatterns = @(
    'public enum NativeAppNotificationActivationSource',
    'NotificationInvokedEvent = 1',
    'CurrentAppInstance = 2',
    'DateTimeOffset CapturedAtUtc = default',
    'int SourceProcessId = 0',
    'string? EnvelopeId = null',
    'NativeAppNotificationActivationSource.NotificationInvokedEvent',
    'NativeAppNotificationActivationSource.CurrentAppInstance',
    'ActivationSource = activation.Source',
    'CreatedAtUtc = activation.CapturedAtUtc == default',
    'OnNativeNotificationActivationObserved(activation);',
    'OnTodoNotificationActivationRouteObserved(activation, result);',
    'envelope.ActivationSource',
    'envelope.CreatedAtUtc',
    'envelope.SourceProcessId',
    'envelope.EnvelopeId',
    'StartAotTodoNotificationUserClickSmokeIfRequested();'
)
$stage5B4C3B2B2BMissingProductPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2BRequiredProductPatterns) {
        if ($stage5B4C3B2B2BProductSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "product::$pattern"
        }
    }
)
$stage5B4C3B2B2BRunnerSource =
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[0]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[1]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[2]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[7]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[8]] +
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[10]]
$stage5B4C3B2B2BRequiredSmokeScriptPatterns = @(
    'RealWindowsNotificationUserClick',
    '$requiredAuditProfileVersion = 58',
    '$requiredSummarySchemaVersion = 55',
    '[switch]$IncludeColdStart',
    '-AllowEarlyExit',
    'Wait-InteractiveResult',
    'Wait-NaturalPreviewExit',
    'NotificationInvokedEvent',
    'CurrentAppInstance',
    'activationCount',
    'routeCount',
    'userInput.todoSnooze',
    'Set-ColdActivationUserEnvironment',
    'Restore-ColdActivationUserEnvironment',
    'productionDataFingerprintBefore',
    'productionDataFingerprintAfter',
    'Refusing to replace an existing, production, or unowned click root',
    'Remove-Item -LiteralPath $resolvedRoot -Recurse -Force',
    'runningUserClicksVerified = $true',
    'coldStartUserClickVerified = if ($IncludeColdStart)',
    'previewRootCleaned = $previewRootCleaned',
    'stage = "5B-4C3B2B2B"',
    'Native AOT stage 5B-4C3B2B2B'
)
$stage5B4C3B2B2BMissingSmokeScriptPatterns = @(
    foreach ($pattern in $stage5B4C3B2B2BRequiredSmokeScriptPatterns) {
        if ($stage5B4C3B2B2BRunnerSource.IndexOf(
                $pattern,
                [StringComparison]::Ordinal) -lt 0) {
            "smoke::$pattern"
        }
    }
)
$stage5B4C3B2B2BForbiddenScopePatterns = @(
    foreach ($sourceFile in @(
            $stage5B4C3B2B2BSourceFiles[0],
            $stage5B4C3B2B2BSourceFiles[7])) {
        foreach ($pattern in @(
                'AppNotificationManager',
                'SendInput',
                'mouse_event',
                'System.Windows.Automation',
                'UIAutomationClient',
                'RemoveAllAsync',
                'RemoveByGroupAsync',
                'deskbox_native_')) {
            if ($stage5B4C3B2B2BSources[$sourceFile].IndexOf(
                    $pattern,
                    [StringComparison]::Ordinal) -ge 0) {
                "$($sourceFile)::$pattern"
            }
        }
    }
)
$stage5B4C3B2B2BRustAbiUnchanged =
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[9]].Contains(
        'assert_eq!(deskbox_native_capabilities(), 511);') -and
    [regex]::Matches(
        $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[9]],
        [regex]::Escape('#[unsafe(no_mangle)]')).Count -eq 10
$stage5B4C3B2B2BScenarioJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2B2BScenarioSource,
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2B2BManagedUiJsonSerializeCallCount = [regex]::Matches(
    $stage5B4C3B2B2BSources[$stage5B4C3B2B2BSourceFiles[1]],
    [regex]::Escape('JsonSerializer.Serialize(')).Count
$stage5B4C3B2B2BSourceWarningMessages = @(
    $logLines |
        Where-Object {
            $line = $_
            $warningCodeRegex.IsMatch($line) -and
                $line -notmatch "warning WMC1510:" -and
                $line -match "(?:App\.AotTodoNotificationUserClickSmoke|App\.xaml|NativeAppNotificationService|NativeNotificationActivationEnvelopeStore|TodoNotificationActivationRouter|TodoWidgetContent\.xaml)\.cs\("
        } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)
$stage5B4C3B2B2BExpectedWmc1510Count = 1241
$stage5B4C3B2B2BActualWmc1510Count = @(
    $warningMatches | Where-Object { $_ -ieq "WMC1510" }
).Count
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
        stage4D1ATargetFiles = $stage4D1ATargetFiles
        stage4D1AWarningMessages = $stage4D1AWarningMessages
        stage4D1BTargetFiles = $stage4D1BTargetFiles
        stage4D1BWarningMessages = $stage4D1BWarningMessages
        stage4D2RemovedSourceFiles = $stage4D2RemovedSourceFiles
        stage4D2UnexpectedExistingSourceFiles = $stage4D2UnexpectedExistingSourceFiles
        stage4D2FileOperationWarningMessages = $stage4D2FileOperationWarningMessages
        stage4D3ASourceFiles = $stage4D3ASourceFiles
        stage4D3ALegacyRcwPatterns = $stage4D3ALegacyRcwPatterns
        stage4D3ALegacyRcwSourceMatches = $stage4D3ALegacyRcwSourceMatches
        stage4D3ADataReaderWarningMessages = $stage4D3ADataReaderWarningMessages
        stage4D3ARemainingDropTargetWarningMessages = $stage4D3ARemainingDropTargetWarningMessages
        stage4D3AUnexpectedDropTargetWarningMessages = $stage4D3AUnexpectedDropTargetWarningMessages
        stage4D3BSourceFiles = $stage4D3BSourceFiles
        stage4D3BLegacyRegistrationPatterns = $stage4D3BLegacyRegistrationPatterns
        stage4D3BLegacyRegistrationSourceMatches = $stage4D3BLegacyRegistrationSourceMatches
        stage4D3BRequiredGeneratedComPatterns = $stage4D3BRequiredGeneratedComPatterns
        stage4D3BMissingGeneratedComPatterns = $stage4D3BMissingGeneratedComPatterns
        stage4D3BWarningMessages = $stage4D3BWarningMessages
        stage4D3BIl2050WarningMessages = $stage4D3BIl2050WarningMessages
        stage4D4ASourceFiles = $stage4D4ASourceFiles
        stage4D4AWarningMessages = $stage4D4AWarningMessages
        stage4D4BSourceFiles = $stage4D4BSourceFiles
        stage4D4BWarningMessages = $stage4D4BWarningMessages
        stage4D5SourceFiles = $stage4D5SourceFiles
        stage4D5LegacyReflectionPatterns = $stage4D5LegacyReflectionPatterns
        stage4D5LegacyReflectionSourceMatches = $stage4D5LegacyReflectionSourceMatches
        stage4D5RequiredPublicPatterns = $stage4D5RequiredPublicPatterns
        stage4D5MissingPublicPatterns = $stage4D5MissingPublicPatterns
        stage4D5WarningMessages = $stage4D5WarningMessages
        stage4E0SourceFiles = $stage4E0SourceFiles
        stage4E0LegacyOneWayPatterns = $stage4E0LegacyOneWayPatterns
        stage4E0LegacyOneWaySourceMatches = $stage4E0LegacyOneWaySourceMatches
        stage4E0RequiredOneTimeBindings = $stage4E0RequiredOneTimeBindings
        stage4E0MissingOneTimeBindings = $stage4E0MissingOneTimeBindings
        stage4E0RequiredBehaviorPatterns = @(
            $stage4E0RequiredBehaviorPatterns |
                ForEach-Object {
                    [PSCustomObject]@{
                        sourceFile = $_.sourceFile
                        pattern = $_.pattern
                    }
                }
        )
        stage4E0MissingBehaviorPatterns = $stage4E0MissingBehaviorPatterns
        stage4E0Wmc1506WarningMessages = $stage4E0Wmc1506WarningMessages
        stage4E0SourceWarningMessages = $stage4E0SourceWarningMessages
        stage4E1SourceFiles = $stage4E1SourceFiles
        stage4E1LegacyBindingContracts = $stage4E1LegacyBindingContracts
        stage4E1LegacyBindingSourceMatches = $stage4E1LegacyBindingSourceMatches
        stage4E1RequiredCompiledBindings = $stage4E1RequiredCompiledBindings
        stage4E1MissingCompiledBindings = $stage4E1MissingCompiledBindings
        stage4E1RequiredBehaviorPatterns = $stage4E1RequiredBehaviorPatterns
        stage4E1MissingBehaviorPatterns = $stage4E1MissingBehaviorPatterns
        stage4E1DeferredBindingContracts = $stage4E1DeferredBindingContracts
        stage4E1MissingDeferredBindings = $stage4E1MissingDeferredBindings
        stage4E1SourceWarningMessages = $stage4E1SourceWarningMessages
        stage4E1MaximumWmc1510Count = $stage4E1MaximumWmc1510Count
        stage4E1ActualWmc1510Count = $stage4E1ActualWmc1510Count
        stage4E2SourceFiles = $stage4E2SourceFiles
        stage4E2LegacyBindingContracts = $stage4E2LegacyBindingContracts
        stage4E2LegacyBindingSourceMatches = $stage4E2LegacyBindingSourceMatches
        stage4E2RequiredCompiledBindings = $stage4E2RequiredCompiledBindings
        stage4E2MissingCompiledBindings = $stage4E2MissingCompiledBindings
        stage4E2RequiredBehaviorPatterns = $stage4E2RequiredBehaviorPatterns
        stage4E2MissingBehaviorPatterns = $stage4E2MissingBehaviorPatterns
        stage4E2DeferredBindingContracts = $stage4E2DeferredBindingContracts
        stage4E2MissingDeferredBindings = $stage4E2MissingDeferredBindings
        stage4E2SourceWarningMessages = $stage4E2SourceWarningMessages
        stage4E2MaximumWmc1510Count = $stage4E2MaximumWmc1510Count
        stage4E2ActualWmc1510Count = $stage4E2ActualWmc1510Count
        stage4E3SourceFiles = $stage4E3SourceFiles
        stage4E3LegacyBindingContracts = $stage4E3LegacyBindingContracts
        stage4E3LegacyBindingSourceMatches = $stage4E3LegacyBindingSourceMatches
        stage4E3RequiredCompiledBindings = $stage4E3RequiredCompiledBindings
        stage4E3MissingCompiledBindings = $stage4E3MissingCompiledBindings
        stage4E3RequiredDataTypes = $stage4E3RequiredDataTypes
        stage4E3MissingDataTypes = $stage4E3MissingDataTypes
        stage4E3RequiredBehaviorPatterns = $stage4E3RequiredBehaviorPatterns
        stage4E3MissingBehaviorPatterns = $stage4E3MissingBehaviorPatterns
        stage4E3DeferredBindingContracts = $stage4E3DeferredBindingContracts
        stage4E3MissingDeferredBindings = $stage4E3MissingDeferredBindings
        stage4E3SourceWarningMessages = $stage4E3SourceWarningMessages
        stage4E3MaximumWmc1510Count = $stage4E3MaximumWmc1510Count
        stage4E3ActualWmc1510Count = $stage4E3ActualWmc1510Count
        stage4E4SourceFiles = $stage4E4SourceFiles
        stage4E4LegacyBindingContracts = $stage4E4LegacyBindingContracts
        stage4E4LegacyBindingSourceMatches = $stage4E4LegacyBindingSourceMatches
        stage4E4RequiredCompiledBindings = $stage4E4RequiredCompiledBindings
        stage4E4MissingCompiledBindings = $stage4E4MissingCompiledBindings
        stage4E4RequiredViewModelBridgePatterns = $stage4E4RequiredViewModelBridgePatterns
        stage4E4MissingViewModelBridgePatterns = $stage4E4MissingViewModelBridgePatterns
        stage4E4ViewModelBridgeOrderValid = $stage4E4ViewModelBridgeOrderValid
        stage4E4UnexpectedManualBridgePatterns = $stage4E4UnexpectedManualBridgePatterns
        stage4E4RequiredBehaviorPatterns = $stage4E4RequiredBehaviorPatterns
        stage4E4MissingBehaviorPatterns = $stage4E4MissingBehaviorPatterns
        stage4E4DeferredBindingContracts = $stage4E4DeferredBindingContracts
        stage4E4MissingDeferredBindings = $stage4E4MissingDeferredBindings
        stage4E4SourceWarningMessages = $stage4E4SourceWarningMessages
        stage4E4MaximumWmc1510Count = $stage4E4MaximumWmc1510Count
        stage4E4ActualWmc1510Count = $stage4E4ActualWmc1510Count
        stage4E5SourceFiles = $stage4E5SourceFiles
        stage4E5LegacyBindingContracts = $stage4E5LegacyBindingContracts
        stage4E5LegacyBindingSourceMatches = $stage4E5LegacyBindingSourceMatches
        stage4E5RequiredCompiledBindings = $stage4E5RequiredCompiledBindings
        stage4E5MissingCompiledBindings = $stage4E5MissingCompiledBindings
        stage4E5RequiredItemBridgePatterns = $stage4E5RequiredItemBridgePatterns
        stage4E5MissingItemBridgePatterns = $stage4E5MissingItemBridgePatterns
        stage4E5UnexpectedPublicItemBridgePatterns = $stage4E5UnexpectedPublicItemBridgePatterns
        stage4E5ItemRefreshOrderValid = $stage4E5ItemRefreshOrderValid
        stage4E5RequiredBehaviorPatterns = $stage4E5RequiredBehaviorPatterns
        stage4E5MissingBehaviorPatterns = $stage4E5MissingBehaviorPatterns
        stage4E5MissingRequiredModelPatterns = $stage4E5MissingRequiredModelPatterns
        stage4E5UnexpectedObservableModelPatterns = $stage4E5UnexpectedObservableModelPatterns
        stage4E5UnexpectedDataContextOverridePatterns = $stage4E5UnexpectedDataContextOverridePatterns
        stage4E5LifecycleOrderValid = $stage4E5LifecycleOrderValid
        stage4E5DeferredBindingContracts = $stage4E5DeferredBindingContracts
        stage4E5MissingDeferredBindings = $stage4E5MissingDeferredBindings
        stage4E5SourceWarningMessages = $stage4E5SourceWarningMessages
        stage4E5ExpectedWmc1510Count = $stage4E5ExpectedWmc1510Count
        stage4E5ActualWmc1510Count = $stage4E5ActualWmc1510Count
        stage5ASourceFiles = $stage5ASourceFiles
        stage5ARequiredDataPathPatterns = $stage5ARequiredDataPathPatterns
        stage5AMissingDataPathPatterns = $stage5AMissingDataPathPatterns
        stage5ARequiredLauncherPatterns = $stage5ARequiredLauncherPatterns
        stage5AMissingLauncherPatterns = $stage5AMissingLauncherPatterns
        stage5AUnsafeLauncherPatterns = $stage5AUnsafeLauncherPatterns
        stage5ASourceWarningMessages = $stage5ASourceWarningMessages
        stage5AExpectedWmc1510Count = $stage5AExpectedWmc1510Count
        stage5AActualWmc1510Count = $stage5AActualWmc1510Count
        stage5B1SourceFiles = $stage5B1SourceFiles
        stage5B1RequiredRunnerPatterns = $stage5B1RequiredRunnerPatterns
        stage5B1MissingRunnerPatterns = $stage5B1MissingRunnerPatterns
        stage5B1RequiredLaunchPatterns = $stage5B1RequiredLaunchPatterns
        stage5B1MissingLaunchPatterns = $stage5B1MissingLaunchPatterns
        stage5B1LaunchOrderValid = $stage5B1LaunchOrderValid
        stage5B1RequiredSmokeScriptPatterns = $stage5B1RequiredSmokeScriptPatterns
        stage5B1MissingSmokeScriptPatterns = $stage5B1MissingSmokeScriptPatterns
        stage5B1UnsafeRunnerPatterns = $stage5B1UnsafeRunnerPatterns
        stage5B1SourceWarningMessages = $stage5B1SourceWarningMessages
        stage5B1ExpectedWmc1510Count = $stage5B1ExpectedWmc1510Count
        stage5B1ActualWmc1510Count = $stage5B1ActualWmc1510Count
        stage5B2ASourceFiles = $stage5B2ASourceFiles
        stage5B2ARequiredRunnerPatterns = $stage5B2ARequiredRunnerPatterns
        stage5B2AMissingRunnerPatterns = $stage5B2AMissingRunnerPatterns
        stage5B2ARequiredLaunchPatterns = $stage5B2ARequiredLaunchPatterns
        stage5B2AMissingLaunchPatterns = $stage5B2AMissingLaunchPatterns
        stage5B2ALaunchOrderValid = $stage5B2ALaunchOrderValid
        stage5B2ARequiredServicePatterns = $stage5B2ARequiredServicePatterns
        stage5B2AMissingServicePatterns = $stage5B2AMissingServicePatterns
        stage5B2ARequiredQuickAccessPatterns = $stage5B2ARequiredQuickAccessPatterns
        stage5B2AMissingQuickAccessPatterns = $stage5B2AMissingQuickAccessPatterns
        stage5B2ARequiredSmokeScriptPatterns = $stage5B2ARequiredSmokeScriptPatterns
        stage5B2AMissingSmokeScriptPatterns = $stage5B2AMissingSmokeScriptPatterns
        stage5B2AUnsafeMutationPatterns = $stage5B2AUnsafeMutationPatterns
        stage5B2AUnsafeRunnerPatterns = $stage5B2AUnsafeRunnerPatterns
        stage5B2ASourceWarningMessages = $stage5B2ASourceWarningMessages
        stage5B2AExpectedWmc1510Count = $stage5B2AExpectedWmc1510Count
        stage5B2AActualWmc1510Count = $stage5B2AActualWmc1510Count
        stage5B2BSourceFiles = $stage5B2BSourceFiles
        stage5B2BRequiredRunnerPatterns = $stage5B2BRequiredRunnerPatterns
        stage5B2BMissingRunnerPatterns = $stage5B2BMissingRunnerPatterns
        stage5B2BRequiredLaunchPatterns = $stage5B2BRequiredLaunchPatterns
        stage5B2BMissingLaunchPatterns = $stage5B2BMissingLaunchPatterns
        stage5B2BLaunchOrderValid = $stage5B2BLaunchOrderValid
        stage5B2BRequiredQuickAccessPatterns = $stage5B2BRequiredQuickAccessPatterns
        stage5B2BMissingQuickAccessPatterns = $stage5B2BMissingQuickAccessPatterns
        stage5B2BRequiredSmokeScriptPatterns = $stage5B2BRequiredSmokeScriptPatterns
        stage5B2BMissingSmokeScriptPatterns = $stage5B2BMissingSmokeScriptPatterns
        stage5B2BUnsafeRunnerPatterns = $stage5B2BUnsafeRunnerPatterns
        stage5B2BSourceWarningMessages = $stage5B2BSourceWarningMessages
        stage5B2BExpectedWmc1510Count = $stage5B2BExpectedWmc1510Count
        stage5B2BActualWmc1510Count = $stage5B2BActualWmc1510Count
        stage5B3ASourceFiles = $stage5B3ASourceFiles
        stage5B3ARequiredRunnerPatterns = $stage5B3ARequiredRunnerPatterns
        stage5B3AMissingRunnerPatterns = $stage5B3AMissingRunnerPatterns
        stage5B3ARequiredLaunchPatterns = $stage5B3ARequiredLaunchPatterns
        stage5B3AMissingLaunchPatterns = $stage5B3AMissingLaunchPatterns
        stage5B3ALaunchOrderValid = $stage5B3ALaunchOrderValid
        stage5B3ARequiredProductPatterns = $stage5B3ARequiredProductPatterns
        stage5B3AMissingProductPatterns = $stage5B3AMissingProductPatterns
        stage5B3ARequiredSmokeScriptPatterns = $stage5B3ARequiredSmokeScriptPatterns
        stage5B3AMissingSmokeScriptPatterns = $stage5B3AMissingSmokeScriptPatterns
        stage5B3AUnsafeMutationPatterns = $stage5B3AUnsafeMutationPatterns
        stage5B3AUnsafeRunnerPatterns = $stage5B3AUnsafeRunnerPatterns
        stage5B3ASourceWarningMessages = $stage5B3ASourceWarningMessages
        stage5B3AExpectedWmc1510Count = $stage5B3AExpectedWmc1510Count
        stage5B3AActualWmc1510Count = $stage5B3AActualWmc1510Count
        stage5B3BSourceFiles = $stage5B3BSourceFiles
        stage5B3BRequiredRunnerPatterns = $stage5B3BRequiredRunnerPatterns
        stage5B3BMissingRunnerPatterns = $stage5B3BMissingRunnerPatterns
        stage5B3BRequiredLaunchPatterns = $stage5B3BRequiredLaunchPatterns
        stage5B3BMissingLaunchPatterns = $stage5B3BMissingLaunchPatterns
        stage5B3BLaunchOrderValid = $stage5B3BLaunchOrderValid
        stage5B3BRequiredProductPatterns = $stage5B3BRequiredProductPatterns
        stage5B3BMissingProductPatterns = $stage5B3BMissingProductPatterns
        stage5B3BRequiredSmokeScriptPatterns = $stage5B3BRequiredSmokeScriptPatterns
        stage5B3BMissingSmokeScriptPatterns = $stage5B3BMissingSmokeScriptPatterns
        stage5B3BUnsafeMutationPatterns = $stage5B3BUnsafeMutationPatterns
        stage5B3BUnsafeRunnerPatterns = $stage5B3BUnsafeRunnerPatterns
        stage5B3BRecoveryOrderValid = $stage5B3BRecoveryOrderValid
        stage5B3BSourceWarningMessages = $stage5B3BSourceWarningMessages
        stage5B3BExpectedWmc1510Count = $stage5B3BExpectedWmc1510Count
        stage5B3BActualWmc1510Count = $stage5B3BActualWmc1510Count
        stage5B3CSourceFiles = $stage5B3CSourceFiles
        stage5B3CRequiredRunnerPatterns = $stage5B3CRequiredRunnerPatterns
        stage5B3CMissingRunnerPatterns = $stage5B3CMissingRunnerPatterns
        stage5B3CRequiredLaunchPatterns = $stage5B3CRequiredLaunchPatterns
        stage5B3CMissingLaunchPatterns = $stage5B3CMissingLaunchPatterns
        stage5B3CLaunchOrderValid = $stage5B3CLaunchOrderValid
        stage5B3CRequiredProductPatterns = $stage5B3CRequiredProductPatterns
        stage5B3CMissingProductPatterns = $stage5B3CMissingProductPatterns
        stage5B3CRequiredFixturePatterns = $stage5B3CRequiredFixturePatterns
        stage5B3CMissingFixturePatterns = $stage5B3CMissingFixturePatterns
        stage5B3CRequiredSmokeScriptPatterns = $stage5B3CRequiredSmokeScriptPatterns
        stage5B3CMissingSmokeScriptPatterns = $stage5B3CMissingSmokeScriptPatterns
        stage5B3CUnsafeMutationPatterns = $stage5B3CUnsafeMutationPatterns
        stage5B3CUnsafeRunnerPatterns = $stage5B3CUnsafeRunnerPatterns
        stage5B3CUnsafeFixtureScriptPatterns = $stage5B3CUnsafeFixtureScriptPatterns
        stage5B3CRecoveryOrderValid = $stage5B3CRecoveryOrderValid
        stage5B3CSourceWarningMessages = $stage5B3CSourceWarningMessages
        stage5B3CExpectedWmc1510Count = $stage5B3CExpectedWmc1510Count
        stage5B3CActualWmc1510Count = $stage5B3CActualWmc1510Count
        stage5B4ASourceFiles = $stage5B4ASourceFiles
        stage5B4ARequiredRunnerPatterns = $stage5B4ARequiredRunnerPatterns
        stage5B4AMissingRunnerPatterns = $stage5B4AMissingRunnerPatterns
        stage5B4ARequiredLaunchPatterns = $stage5B4ARequiredLaunchPatterns
        stage5B4AMissingLaunchPatterns = $stage5B4AMissingLaunchPatterns
        stage5B4ALaunchOrderValid = $stage5B4ALaunchOrderValid
        stage5B4ARequiredSettingsPatterns = $stage5B4ARequiredSettingsPatterns
        stage5B4AMissingSettingsPatterns = $stage5B4AMissingSettingsPatterns
        stage5B4ARequiredSettingsNavigationPatterns = $stage5B4ARequiredSettingsNavigationPatterns
        stage5B4AMissingSettingsNavigationPatterns = $stage5B4AMissingSettingsNavigationPatterns
        stage5B4AUnsafeSettingsNavigationPatterns = $stage5B4AUnsafeSettingsNavigationPatterns
        stage5B4ARequiredSearchPatterns = $stage5B4ARequiredSearchPatterns
        stage5B4AMissingSearchPatterns = $stage5B4AMissingSearchPatterns
        stage5B4ASortHandlerCountViolations = $stage5B4ASortHandlerCountViolations
        stage5B4ARequiredLocalePatterns = $stage5B4ARequiredLocalePatterns
        stage5B4AMissingLocalePatterns = $stage5B4AMissingLocalePatterns
        stage5B4ARequiredSmokeScriptPatterns = $stage5B4ARequiredSmokeScriptPatterns
        stage5B4AMissingSmokeScriptPatterns = $stage5B4AMissingSmokeScriptPatterns
        stage5B4ASmokeScriptFiles = $stage5B4ASmokeScriptFiles
        stage5B4AMissingSmokeOptInIsolation = $stage5B4AMissingSmokeOptInIsolation
        stage5B4AUnsafeRunnerPatterns = $stage5B4AUnsafeRunnerPatterns
        stage5B4AUnsafeSmokeScriptPatterns = $stage5B4AUnsafeSmokeScriptPatterns
        stage5B4AJsonSerializeCallCount = $stage5B4AJsonSerializeCallCount
        stage5B4ASourceWarningMessages = $stage5B4ASourceWarningMessages
        stage5B4AExpectedWmc1510Count = $stage5B4AExpectedWmc1510Count
        stage5B4AActualWmc1510Count = $stage5B4AActualWmc1510Count
        stage5B4B1SourceFiles = $stage5B4B1SourceFiles
        stage5B4B1RequiredRunnerPatterns = $stage5B4B1RequiredRunnerPatterns
        stage5B4B1MissingRunnerPatterns = $stage5B4B1MissingRunnerPatterns
        stage5B4B1RequiredSettingsPatterns = $stage5B4B1RequiredSettingsPatterns
        stage5B4B1MissingSettingsPatterns = $stage5B4B1MissingSettingsPatterns
        stage5B4B1RequiredNavigationPatterns = $stage5B4B1RequiredNavigationPatterns
        stage5B4B1MissingNavigationPatterns = $stage5B4B1MissingNavigationPatterns
        stage5B4B1RequiredProjectionPatterns = $stage5B4B1RequiredProjectionPatterns
        stage5B4B1MissingProjectionPatterns = $stage5B4B1MissingProjectionPatterns
        stage5B4B1RequiredInventoryPatterns = $stage5B4B1RequiredInventoryPatterns
        stage5B4B1MissingInventoryPatterns = $stage5B4B1MissingInventoryPatterns
        stage5B4B1RequiredBindableTypePatterns = $stage5B4B1RequiredBindableTypePatterns
        stage5B4B1MissingBindableTypePatterns = $stage5B4B1MissingBindableTypePatterns
        stage5B4B1ExpectedBindableViewModelPropertyCount = $stage5B4B1ExpectedBindableViewModelPropertyCount
        stage5B4B1ActualBindableViewModelPropertyCount = $stage5B4B1ActualBindableViewModelPropertyCount
        stage5B4B1UnsafeBindableViewModelPatterns = $stage5B4B1UnsafeBindableViewModelPatterns
        stage5B4B1RequiredFileStackXamlPatterns = $stage5B4B1RequiredFileStackXamlPatterns
        stage5B4B1MissingFileStackXamlPatterns = $stage5B4B1MissingFileStackXamlPatterns
        stage5B4B1RequiredFileWidgetProjectionPatterns = $stage5B4B1RequiredFileWidgetProjectionPatterns
        stage5B4B1MissingFileWidgetProjectionPatterns = $stage5B4B1MissingFileWidgetProjectionPatterns
        stage5B4B1RequiredWeatherProjectionPatterns = $stage5B4B1RequiredWeatherProjectionPatterns
        stage5B4B1MissingWeatherProjectionPatterns = $stage5B4B1MissingWeatherProjectionPatterns
        stage5B4B1RequiredCommandXamlPatterns = $stage5B4B1RequiredCommandXamlPatterns
        stage5B4B1MissingCommandXamlPatterns = $stage5B4B1MissingCommandXamlPatterns
        stage5B4B1RequiredCapsuleCommandXamlPatterns = $stage5B4B1RequiredCapsuleCommandXamlPatterns
        stage5B4B1MissingCapsuleCommandXamlPatterns = $stage5B4B1MissingCapsuleCommandXamlPatterns
        stage5B4B1RequiredCapsuleCodeBehindPatterns = $stage5B4B1RequiredCapsuleCodeBehindPatterns
        stage5B4B1MissingCapsuleCodeBehindPatterns = $stage5B4B1MissingCapsuleCodeBehindPatterns
        stage5B4B1RoutePatterns = $stage5B4B1RoutePatterns
        stage5B4B1MissingRoutePatterns = $stage5B4B1MissingRoutePatterns
        stage5B4B1RequiredSmokeScriptPatterns = $stage5B4B1RequiredSmokeScriptPatterns
        stage5B4B1MissingSmokeScriptPatterns = $stage5B4B1MissingSmokeScriptPatterns
        stage5B4B1UnsafeMutationPatterns = $stage5B4B1UnsafeMutationPatterns
        stage5B4B1JsonSerializeCallCount = $stage5B4B1JsonSerializeCallCount
        stage5B4B1SourceWarningMessages = $stage5B4B1SourceWarningMessages
        stage5B4B1ExpectedWmc1510Count = $stage5B4B1ExpectedWmc1510Count
        stage5B4B1ActualWmc1510Count = $stage5B4B1ActualWmc1510Count
        stage5B4B2ASourceFiles = $stage5B4B2ASourceFiles
        stage5B4B2ARequiredRunnerPatterns = $stage5B4B2ARequiredRunnerPatterns
        stage5B4B2AMissingRunnerPatterns = $stage5B4B2AMissingRunnerPatterns
        stage5B4B2ARequiredManagerPatterns = $stage5B4B2ARequiredManagerPatterns
        stage5B4B2AMissingManagerPatterns = $stage5B4B2AMissingManagerPatterns
        stage5B4B2ARequiredBoundsPatterns = $stage5B4B2ARequiredBoundsPatterns
        stage5B4B2AMissingBoundsPatterns = $stage5B4B2AMissingBoundsPatterns
        stage5B4B2ARequiredSmokeScriptPatterns = $stage5B4B2ARequiredSmokeScriptPatterns
        stage5B4B2AMissingSmokeScriptPatterns = $stage5B4B2AMissingSmokeScriptPatterns
        stage5B4B2ARequiredLauncherPatterns = $stage5B4B2ARequiredLauncherPatterns
        stage5B4B2AMissingLauncherPatterns = $stage5B4B2AMissingLauncherPatterns
        stage5B4B2AForbiddenScopePatterns = $stage5B4B2AForbiddenScopePatterns
        stage5B4B2AJsonSerializeCallCount = $stage5B4B2AJsonSerializeCallCount
        stage5B4B2ASourceWarningMessages = $stage5B4B2ASourceWarningMessages
        stage5B4B2AExpectedWmc1510Count = $stage5B4B2AExpectedWmc1510Count
        stage5B4B2AActualWmc1510Count = $stage5B4B2AActualWmc1510Count
        stage5B4B2B1SourceFiles = $stage5B4B2B1SourceFiles
        stage5B4B2B1RequiredRunnerPatterns = $stage5B4B2B1RequiredRunnerPatterns
        stage5B4B2B1MissingRunnerPatterns = $stage5B4B2B1MissingRunnerPatterns
        stage5B4B2B1RequiredSurfacePatterns = $stage5B4B2B1RequiredSurfacePatterns
        stage5B4B2B1MissingSurfacePatterns = $stage5B4B2B1MissingSurfacePatterns
        stage5B4B2B1RequiredProductSurfacePatterns = $stage5B4B2B1RequiredProductSurfacePatterns
        stage5B4B2B1MissingProductSurfacePatterns = $stage5B4B2B1MissingProductSurfacePatterns
        stage5B4B2B1RequiredManagerPatterns = $stage5B4B2B1RequiredManagerPatterns
        stage5B4B2B1MissingManagerPatterns = $stage5B4B2B1MissingManagerPatterns
        stage5B4B2B1RequiredSmokeScriptPatterns = $stage5B4B2B1RequiredSmokeScriptPatterns
        stage5B4B2B1MissingSmokeScriptPatterns = $stage5B4B2B1MissingSmokeScriptPatterns
        stage5B4B2B1ForbiddenScopePatterns = $stage5B4B2B1ForbiddenScopePatterns
        stage5B4B2B1JsonSerializeCallCount = $stage5B4B2B1JsonSerializeCallCount
        stage5B4B2B1SourceWarningMessages = $stage5B4B2B1SourceWarningMessages
        stage5B4B2B1ExpectedWmc1510Count = $stage5B4B2B1ExpectedWmc1510Count
        stage5B4B2B1ActualWmc1510Count = $stage5B4B2B1ActualWmc1510Count
        stage5B4B2B2ASourceFiles = $stage5B4B2B2ASourceFiles
        stage5B4B2B2ARequiredRunnerPatterns = $stage5B4B2B2ARequiredRunnerPatterns
        stage5B4B2B2AMissingRunnerPatterns = $stage5B4B2B2AMissingRunnerPatterns
        stage5B4B2B2ARequiredSurfacePatterns = $stage5B4B2B2ARequiredSurfacePatterns
        stage5B4B2B2AMissingSurfacePatterns = $stage5B4B2B2AMissingSurfacePatterns
        stage5B4B2B2ARequiredProductPatterns = $stage5B4B2B2ARequiredProductPatterns
        stage5B4B2B2AMissingProductPatterns = $stage5B4B2B2AMissingProductPatterns
        stage5B4B2B2ARequiredManagerPatterns = $stage5B4B2B2ARequiredManagerPatterns
        stage5B4B2B2AMissingManagerPatterns = $stage5B4B2B2AMissingManagerPatterns
        stage5B4B2B2ARequiredSmokeScriptPatterns = $stage5B4B2B2ARequiredSmokeScriptPatterns
        stage5B4B2B2AMissingSmokeScriptPatterns = $stage5B4B2B2AMissingSmokeScriptPatterns
        stage5B4B2B2AForbiddenScopePatterns = $stage5B4B2B2AForbiddenScopePatterns
        stage5B4B2B2AJsonSerializeCallCount = $stage5B4B2B2AJsonSerializeCallCount
        stage5B4B2B2ASourceWarningMessages = $stage5B4B2B2ASourceWarningMessages
        stage5B4B2B2AExpectedWmc1510Count = $stage5B4B2B2AExpectedWmc1510Count
        stage5B4B2B2AActualWmc1510Count = $stage5B4B2B2AActualWmc1510Count
        stage5B4B2B2B1SourceFiles = $stage5B4B2B2B1SourceFiles
        stage5B4B2B2B1RequiredRunnerPatterns = $stage5B4B2B2B1RequiredRunnerPatterns
        stage5B4B2B2B1MissingRunnerPatterns = $stage5B4B2B2B1MissingRunnerPatterns
        stage5B4B2B2B1RequiredSurfacePatterns = $stage5B4B2B2B1RequiredSurfacePatterns
        stage5B4B2B2B1MissingSurfacePatterns = $stage5B4B2B2B1MissingSurfacePatterns
        stage5B4B2B2B1RequiredProductPatterns = $stage5B4B2B2B1RequiredProductPatterns
        stage5B4B2B2B1MissingProductPatterns = $stage5B4B2B2B1MissingProductPatterns
        stage5B4B2B2B1GeneratedBindableCount = $stage5B4B2B2B1GeneratedBindableCount
        stage5B4B2B2B1RequiredManagerPatterns = $stage5B4B2B2B1RequiredManagerPatterns
        stage5B4B2B2B1MissingManagerPatterns = $stage5B4B2B2B1MissingManagerPatterns
        stage5B4B2B2B1RequiredSmokeScriptPatterns = $stage5B4B2B2B1RequiredSmokeScriptPatterns
        stage5B4B2B2B1MissingSmokeScriptPatterns = $stage5B4B2B2B1MissingSmokeScriptPatterns
        stage5B4B2B2B1ForbiddenScopePatterns = $stage5B4B2B2B1ForbiddenScopePatterns
        stage5B4B2B2B1JsonSerializeCallCount = $stage5B4B2B2B1JsonSerializeCallCount
        stage5B4B2B2B1SourceWarningMessages = $stage5B4B2B2B1SourceWarningMessages
        stage5B4B2B2B1ExpectedWmc1510Count = $stage5B4B2B2B1ExpectedWmc1510Count
        stage5B4B2B2B1ActualWmc1510Count = $stage5B4B2B2B1ActualWmc1510Count
        stage5B4B2B2B2SourceFiles = $stage5B4B2B2B2SourceFiles
        stage5B4B2B2B2RequiredRunnerPatterns = $stage5B4B2B2B2RequiredRunnerPatterns
        stage5B4B2B2B2MissingRunnerPatterns = $stage5B4B2B2B2MissingRunnerPatterns
        stage5B4B2B2B2RequiredSurfacePatterns = $stage5B4B2B2B2RequiredSurfacePatterns
        stage5B4B2B2B2MissingSurfacePatterns = $stage5B4B2B2B2MissingSurfacePatterns
        stage5B4B2B2B2RequiredTilePatterns = $stage5B4B2B2B2RequiredTilePatterns
        stage5B4B2B2B2MissingTilePatterns = $stage5B4B2B2B2MissingTilePatterns
        stage5B4B2B2B2RequiredProductPatterns = $stage5B4B2B2B2RequiredProductPatterns
        stage5B4B2B2B2MissingProductPatterns = $stage5B4B2B2B2MissingProductPatterns
        stage5B4B2B2B2GeneratedBindableCount = $stage5B4B2B2B2GeneratedBindableCount
        stage5B4B2B2B2RequiredManagerPatterns = $stage5B4B2B2B2RequiredManagerPatterns
        stage5B4B2B2B2MissingManagerPatterns = $stage5B4B2B2B2MissingManagerPatterns
        stage5B4B2B2B2RequiredSmokeScriptPatterns = $stage5B4B2B2B2RequiredSmokeScriptPatterns
        stage5B4B2B2B2MissingSmokeScriptPatterns = $stage5B4B2B2B2MissingSmokeScriptPatterns
        stage5B4B2B2B2ForbiddenScopePatterns = $stage5B4B2B2B2ForbiddenScopePatterns
        stage5B4B2B2B2JsonSerializeCallCount = $stage5B4B2B2B2JsonSerializeCallCount
        stage5B4B2B2B2SourceWarningMessages = $stage5B4B2B2B2SourceWarningMessages
        stage5B4B2B2B2ExpectedWmc1510Count = $stage5B4B2B2B2ExpectedWmc1510Count
        stage5B4B2B2B2ActualWmc1510Count = $stage5B4B2B2B2ActualWmc1510Count
        stage5B4B2C1SourceFiles = $stage5B4B2C1SourceFiles
        stage5B4B2C1RequiredRunnerPatterns = $stage5B4B2C1RequiredRunnerPatterns
        stage5B4B2C1MissingRunnerPatterns = $stage5B4B2C1MissingRunnerPatterns
        stage5B4B2C1RequiredSurfacePatterns = $stage5B4B2C1RequiredSurfacePatterns
        stage5B4B2C1MissingSurfacePatterns = $stage5B4B2C1MissingSurfacePatterns
        stage5B4B2C1RequiredProductPatterns = $stage5B4B2C1RequiredProductPatterns
        stage5B4B2C1MissingProductPatterns = $stage5B4B2C1MissingProductPatterns
        stage5B4B2C1GeneratedBindableCount = $stage5B4B2C1GeneratedBindableCount
        stage5B4B2C1BindablePropertyCount = $stage5B4B2C1BindablePropertyCount
        stage5B4B2C1RequiredManagerPatterns = $stage5B4B2C1RequiredManagerPatterns
        stage5B4B2C1MissingManagerPatterns = $stage5B4B2C1MissingManagerPatterns
        stage5B4B2C1RequiredSmokeScriptPatterns = $stage5B4B2C1RequiredSmokeScriptPatterns
        stage5B4B2C1MissingSmokeScriptPatterns = $stage5B4B2C1MissingSmokeScriptPatterns
        stage5B4B2C1ForbiddenScopePatterns = $stage5B4B2C1ForbiddenScopePatterns
        stage5B4B2C1JsonSerializeCallCount = $stage5B4B2C1JsonSerializeCallCount
        stage5B4B2C1SourceWarningMessages = $stage5B4B2C1SourceWarningMessages
        stage5B4B2C1ExpectedWmc1510Count = $stage5B4B2C1ExpectedWmc1510Count
        stage5B4B2C1ActualWmc1510Count = $stage5B4B2C1ActualWmc1510Count
        stage5B4B2C2ASourceFiles = $stage5B4B2C2ASourceFiles
        stage5B4B2C2ARequiredRunnerPatterns = $stage5B4B2C2ARequiredRunnerPatterns
        stage5B4B2C2AMissingRunnerPatterns = $stage5B4B2C2AMissingRunnerPatterns
        stage5B4B2C2ARequiredPolicyPatterns = $stage5B4B2C2ARequiredPolicyPatterns
        stage5B4B2C2AMissingPolicyPatterns = $stage5B4B2C2AMissingPolicyPatterns
        stage5B4B2C2ARequiredManagerPatterns = $stage5B4B2C2ARequiredManagerPatterns
        stage5B4B2C2AMissingManagerPatterns = $stage5B4B2C2AMissingManagerPatterns
        stage5B4B2C2ARequiredSmokeScriptPatterns = $stage5B4B2C2ARequiredSmokeScriptPatterns
        stage5B4B2C2AMissingSmokeScriptPatterns = $stage5B4B2C2AMissingSmokeScriptPatterns
        stage5B4B2C2AForbiddenScopePatterns = $stage5B4B2C2AForbiddenScopePatterns
        stage5B4B2C2AJsonSerializeCallCount = $stage5B4B2C2AJsonSerializeCallCount
        stage5B4B2C2ASourceWarningMessages = $stage5B4B2C2ASourceWarningMessages
        stage5B4B2C2AExpectedWmc1510Count = $stage5B4B2C2AExpectedWmc1510Count
        stage5B4B2C2AActualWmc1510Count = $stage5B4B2C2AActualWmc1510Count
        stage5B4B2C2BSourceFiles = $stage5B4B2C2BSourceFiles
        stage5B4B2C2BRequiredRunnerPatterns = $stage5B4B2C2BRequiredRunnerPatterns
        stage5B4B2C2BMissingRunnerPatterns = $stage5B4B2C2BMissingRunnerPatterns
        stage5B4B2C2BRequiredFixturePatterns = $stage5B4B2C2BRequiredFixturePatterns
        stage5B4B2C2BMissingFixturePatterns = $stage5B4B2C2BMissingFixturePatterns
        stage5B4B2C2BRequiredSurfacePatterns = $stage5B4B2C2BRequiredSurfacePatterns
        stage5B4B2C2BMissingSurfacePatterns = $stage5B4B2C2BMissingSurfacePatterns
        stage5B4B2C2BBindableAttributeCount = $stage5B4B2C2BBindableAttributeCount
        stage5B4B2C2BRequiredManagerPatterns = $stage5B4B2C2BRequiredManagerPatterns
        stage5B4B2C2BMissingManagerPatterns = $stage5B4B2C2BMissingManagerPatterns
        stage5B4B2C2BRequiredSmokeScriptPatterns = $stage5B4B2C2BRequiredSmokeScriptPatterns
        stage5B4B2C2BMissingSmokeScriptPatterns = $stage5B4B2C2BMissingSmokeScriptPatterns
        stage5B4B2C2BForbiddenScopePatterns = $stage5B4B2C2BForbiddenScopePatterns
        stage5B4B2C2BJsonSerializeCallCount = $stage5B4B2C2BJsonSerializeCallCount
        stage5B4B2C2BSourceWarningMessages = $stage5B4B2C2BSourceWarningMessages
        stage5B4B2C2BExpectedWmc1510Count = $stage5B4B2C2BExpectedWmc1510Count
        stage5B4B2C2BActualWmc1510Count = $stage5B4B2C2BActualWmc1510Count
        stage5B4C1ASourceFiles = $stage5B4C1ASourceFiles
        stage5B4C1ARequiredRunnerPatterns = $stage5B4C1ARequiredRunnerPatterns
        stage5B4C1AMissingRunnerPatterns = $stage5B4C1AMissingRunnerPatterns
        stage5B4C1ARequiredFixturePatterns = $stage5B4C1ARequiredFixturePatterns
        stage5B4C1AMissingFixturePatterns = $stage5B4C1AMissingFixturePatterns
        stage5B4C1ARequiredSurfacePatterns = $stage5B4C1ARequiredSurfacePatterns
        stage5B4C1AMissingSurfacePatterns = $stage5B4C1AMissingSurfacePatterns
        stage5B4C1ABindableAttributeCount = $stage5B4C1ABindableAttributeCount
        stage5B4C1ARequiredBindablePatterns = $stage5B4C1ARequiredBindablePatterns
        stage5B4C1AMissingBindablePatterns = $stage5B4C1AMissingBindablePatterns
        stage5B4C1ARequiredSmokeScriptPatterns = $stage5B4C1ARequiredSmokeScriptPatterns
        stage5B4C1AMissingSmokeScriptPatterns = $stage5B4C1AMissingSmokeScriptPatterns
        stage5B4C1AForbiddenScopePatterns = $stage5B4C1AForbiddenScopePatterns
        stage5B4C1AJsonSerializeCallCount = $stage5B4C1AJsonSerializeCallCount
        stage5B4C1ASourceWarningMessages = $stage5B4C1ASourceWarningMessages
        stage5B4C1AExpectedWmc1510Count = $stage5B4C1AExpectedWmc1510Count
        stage5B4C1AActualWmc1510Count = $stage5B4C1AActualWmc1510Count
        stage5B4C1B1SourceFiles = $stage5B4C1B1SourceFiles
        stage5B4C1B1RequiredRunnerPatterns = $stage5B4C1B1RequiredRunnerPatterns
        stage5B4C1B1MissingRunnerPatterns = $stage5B4C1B1MissingRunnerPatterns
        stage5B4C1B1RequiredFixturePatterns = $stage5B4C1B1RequiredFixturePatterns
        stage5B4C1B1MissingFixturePatterns = $stage5B4C1B1MissingFixturePatterns
        stage5B4C1B1RequiredProductPatterns = $stage5B4C1B1RequiredProductPatterns
        stage5B4C1B1MissingProductPatterns = $stage5B4C1B1MissingProductPatterns
        stage5B4C1B1RequiredMenuPatterns = $stage5B4C1B1RequiredMenuPatterns
        stage5B4C1B1MissingMenuPatterns = $stage5B4C1B1MissingMenuPatterns
        stage5B4C1B1RequiredNativePatterns = $stage5B4C1B1RequiredNativePatterns
        stage5B4C1B1MissingNativePatterns = $stage5B4C1B1MissingNativePatterns
        stage5B4C1B1RestoreInvokeAfterEnumeration = $stage5B4C1B1RestoreInvokeAfterEnumeration
        stage5B4C1B1RequiredScenarioPatterns = $stage5B4C1B1RequiredScenarioPatterns
        stage5B4C1B1MissingScenarioPatterns = $stage5B4C1B1MissingScenarioPatterns
        stage5B4C1B1RequiredSmokeScriptPatterns = $stage5B4C1B1RequiredSmokeScriptPatterns
        stage5B4C1B1MissingSmokeScriptPatterns = $stage5B4C1B1MissingSmokeScriptPatterns
        stage5B4C1B1ForbiddenScopePatterns = $stage5B4C1B1ForbiddenScopePatterns
        stage5B4C1B1JsonSerializeCallCount = $stage5B4C1B1JsonSerializeCallCount
        stage5B4C1B1SourceWarningMessages = $stage5B4C1B1SourceWarningMessages
        stage5B4C1B1ExpectedWmc1510Count = $stage5B4C1B1ExpectedWmc1510Count
        stage5B4C1B1ActualWmc1510Count = $stage5B4C1B1ActualWmc1510Count
        stage5B4C1B2ASourceFiles = $stage5B4C1B2ASourceFiles
        stage5B4C1B2ARequiredRunnerPatterns = $stage5B4C1B2ARequiredRunnerPatterns
        stage5B4C1B2AMissingRunnerPatterns = $stage5B4C1B2AMissingRunnerPatterns
        stage5B4C1B2ARequiredFixturePatterns = $stage5B4C1B2ARequiredFixturePatterns
        stage5B4C1B2AMissingFixturePatterns = $stage5B4C1B2AMissingFixturePatterns
        stage5B4C1B2ARequiredProductPatterns = $stage5B4C1B2ARequiredProductPatterns
        stage5B4C1B2AMissingProductPatterns = $stage5B4C1B2AMissingProductPatterns
        stage5B4C1B2ARequiredMenuPatterns = $stage5B4C1B2ARequiredMenuPatterns
        stage5B4C1B2AMissingMenuPatterns = $stage5B4C1B2AMissingMenuPatterns
        stage5B4C1B2ARequiredScenarioPatterns = $stage5B4C1B2ARequiredScenarioPatterns
        stage5B4C1B2AMissingScenarioPatterns = $stage5B4C1B2AMissingScenarioPatterns
        stage5B4C1B2ARequiredSmokeScriptPatterns = $stage5B4C1B2ARequiredSmokeScriptPatterns
        stage5B4C1B2AMissingSmokeScriptPatterns = $stage5B4C1B2AMissingSmokeScriptPatterns
        stage5B4C1B2AForbiddenScopePatterns = $stage5B4C1B2AForbiddenScopePatterns
        stage5B4C1B2ARustAbiUnchanged = $stage5B4C1B2ARustAbiUnchanged
        stage5B4C1B2AJsonSerializeCallCount = $stage5B4C1B2AJsonSerializeCallCount
        stage5B4C1B2ASourceWarningMessages = $stage5B4C1B2ASourceWarningMessages
        stage5B4C1B2AExpectedWmc1510Count = $stage5B4C1B2AExpectedWmc1510Count
        stage5B4C1B2AActualWmc1510Count = $stage5B4C1B2AActualWmc1510Count
        stage5B4C1B2BSourceFiles = $stage5B4C1B2BSourceFiles
        stage5B4C1B2BRequiredRunnerPatterns = $stage5B4C1B2BRequiredRunnerPatterns
        stage5B4C1B2BMissingRunnerPatterns = $stage5B4C1B2BMissingRunnerPatterns
        stage5B4C1B2BRequiredFixturePatterns = $stage5B4C1B2BRequiredFixturePatterns
        stage5B4C1B2BMissingFixturePatterns = $stage5B4C1B2BMissingFixturePatterns
        stage5B4C1B2BRequiredProductPatterns = $stage5B4C1B2BRequiredProductPatterns
        stage5B4C1B2BMissingProductPatterns = $stage5B4C1B2BMissingProductPatterns
        stage5B4C1B2BRequiredMenuPatterns = $stage5B4C1B2BRequiredMenuPatterns
        stage5B4C1B2BMissingMenuPatterns = $stage5B4C1B2BMissingMenuPatterns
        stage5B4C1B2BRequiredScenarioPatterns = $stage5B4C1B2BRequiredScenarioPatterns
        stage5B4C1B2BMissingScenarioPatterns = $stage5B4C1B2BMissingScenarioPatterns
        stage5B4C1B2BRequiredSmokeScriptPatterns = $stage5B4C1B2BRequiredSmokeScriptPatterns
        stage5B4C1B2BMissingSmokeScriptPatterns = $stage5B4C1B2BMissingSmokeScriptPatterns
        stage5B4C1B2BForbiddenScopePatterns = $stage5B4C1B2BForbiddenScopePatterns
        stage5B4C1B2BRustAbiUnchanged = $stage5B4C1B2BRustAbiUnchanged
        stage5B4C1B2BJsonSerializeCallCount = $stage5B4C1B2BJsonSerializeCallCount
        stage5B4C1B2BSourceWarningMessages = $stage5B4C1B2BSourceWarningMessages
        stage5B4C1B2BExpectedWmc1510Count = $stage5B4C1B2BExpectedWmc1510Count
        stage5B4C1B2BActualWmc1510Count = $stage5B4C1B2BActualWmc1510Count
        stage5B4C1C1SourceFiles = $stage5B4C1C1SourceFiles
        stage5B4C1C1RequiredRunnerPatterns = $stage5B4C1C1RequiredRunnerPatterns
        stage5B4C1C1MissingRunnerPatterns = $stage5B4C1C1MissingRunnerPatterns
        stage5B4C1C1RequiredProductPatterns = $stage5B4C1C1RequiredProductPatterns
        stage5B4C1C1MissingProductPatterns = $stage5B4C1C1MissingProductPatterns
        stage5B4C1C1RequiredFixturePatterns = $stage5B4C1C1RequiredFixturePatterns
        stage5B4C1C1MissingFixturePatterns = $stage5B4C1C1MissingFixturePatterns
        stage5B4C1C1RequiredProbePatterns = $stage5B4C1C1RequiredProbePatterns
        stage5B4C1C1MissingProbePatterns = $stage5B4C1C1MissingProbePatterns
        stage5B4C1C1RequiredScenarioPatterns = $stage5B4C1C1RequiredScenarioPatterns
        stage5B4C1C1MissingScenarioPatterns = $stage5B4C1C1MissingScenarioPatterns
        stage5B4C1C1RequiredSmokeScriptPatterns = $stage5B4C1C1RequiredSmokeScriptPatterns
        stage5B4C1C1MissingSmokeScriptPatterns = $stage5B4C1C1MissingSmokeScriptPatterns
        stage5B4C1C1ForbiddenScopePatterns = $stage5B4C1C1ForbiddenScopePatterns
        stage5B4C1C1RustAbiUnchanged = $stage5B4C1C1RustAbiUnchanged
        stage5B4C1C1JsonSerializeCallCount = $stage5B4C1C1JsonSerializeCallCount
        stage5B4C1C1SourceWarningMessages = $stage5B4C1C1SourceWarningMessages
        stage5B4C1C1ExpectedWmc1510Count = $stage5B4C1C1ExpectedWmc1510Count
        stage5B4C1C1ActualWmc1510Count = $stage5B4C1C1ActualWmc1510Count
        stage5B4C1C2ASourceFiles = $stage5B4C1C2ASourceFiles
        stage5B4C1C2ARequiredRunnerPatterns = $stage5B4C1C2ARequiredRunnerPatterns
        stage5B4C1C2AMissingRunnerPatterns = $stage5B4C1C2AMissingRunnerPatterns
        stage5B4C1C2ARequiredProductPatterns = $stage5B4C1C2ARequiredProductPatterns
        stage5B4C1C2AMissingProductPatterns = $stage5B4C1C2AMissingProductPatterns
        stage5B4C1C2ARequiredFixturePatterns = $stage5B4C1C2ARequiredFixturePatterns
        stage5B4C1C2AMissingFixturePatterns = $stage5B4C1C2AMissingFixturePatterns
        stage5B4C1C2ARequiredProbePatterns = $stage5B4C1C2ARequiredProbePatterns
        stage5B4C1C2AMissingProbePatterns = $stage5B4C1C2AMissingProbePatterns
        stage5B4C1C2ARequiredScenarioPatterns = $stage5B4C1C2ARequiredScenarioPatterns
        stage5B4C1C2AMissingScenarioPatterns = $stage5B4C1C2AMissingScenarioPatterns
        stage5B4C1C2ARequiredVisualPatterns = $stage5B4C1C2ARequiredVisualPatterns
        stage5B4C1C2AMissingVisualPatterns = $stage5B4C1C2AMissingVisualPatterns
        stage5B4C1C2ARequiredSmokeScriptPatterns = $stage5B4C1C2ARequiredSmokeScriptPatterns
        stage5B4C1C2AMissingSmokeScriptPatterns = $stage5B4C1C2AMissingSmokeScriptPatterns
        stage5B4C1C2AForbiddenScopePatterns = $stage5B4C1C2AForbiddenScopePatterns
        stage5B4C1C2ARustAbiUnchanged = $stage5B4C1C2ARustAbiUnchanged
        stage5B4C1C2AJsonSerializeCallCount = $stage5B4C1C2AJsonSerializeCallCount
        stage5B4C1C2ASourceWarningMessages = $stage5B4C1C2ASourceWarningMessages
        stage5B4C1C2AExpectedWmc1510Count = $stage5B4C1C2AExpectedWmc1510Count
        stage5B4C1C2AActualWmc1510Count = $stage5B4C1C2AActualWmc1510Count
        stage5B4C2ASourceFiles = $stage5B4C2ASourceFiles
        stage5B4C2ARequiredScenarioPatterns = $stage5B4C2ARequiredScenarioPatterns
        stage5B4C2AMissingScenarioPatterns = $stage5B4C2AMissingScenarioPatterns
        stage5B4C2ARequiredHelperPatterns = $stage5B4C2ARequiredHelperPatterns
        stage5B4C2AMissingHelperPatterns = $stage5B4C2AMissingHelperPatterns
        stage5B4C2ARequiredProductPatterns = $stage5B4C2ARequiredProductPatterns
        stage5B4C2AMissingProductPatterns = $stage5B4C2AMissingProductPatterns
        stage5B4C2ARequiredSmokeScriptPatterns = $stage5B4C2ARequiredSmokeScriptPatterns
        stage5B4C2AMissingSmokeScriptPatterns = $stage5B4C2AMissingSmokeScriptPatterns
        stage5B4C2AForbiddenScopePatterns = $stage5B4C2AForbiddenScopePatterns
        stage5B4C2ARustAbiUnchanged = $stage5B4C2ARustAbiUnchanged
        stage5B4C2AJsonSerializeCallCount = $stage5B4C2AJsonSerializeCallCount
        stage5B4C2ASourceWarningMessages = $stage5B4C2ASourceWarningMessages
        stage5B4C2AExpectedWmc1510Count = $stage5B4C2AExpectedWmc1510Count
        stage5B4C2AActualWmc1510Count = $stage5B4C2AActualWmc1510Count
        stage5B4C3ASourceFiles = $stage5B4C3ASourceFiles
        stage5B4C3ARequiredScenarioPatterns = $stage5B4C3ARequiredScenarioPatterns
        stage5B4C3AMissingScenarioPatterns = $stage5B4C3AMissingScenarioPatterns
        stage5B4C3ARequiredProductPatterns = $stage5B4C3ARequiredProductPatterns
        stage5B4C3AMissingProductPatterns = $stage5B4C3AMissingProductPatterns
        stage5B4C3ARequiredSmokeScriptPatterns = $stage5B4C3ARequiredSmokeScriptPatterns
        stage5B4C3AMissingSmokeScriptPatterns = $stage5B4C3AMissingSmokeScriptPatterns
        stage5B4C3AForbiddenScopePatterns = $stage5B4C3AForbiddenScopePatterns
        stage5B4C3ARustAbiUnchanged = $stage5B4C3ARustAbiUnchanged
        stage5B4C3AJsonSerializeCallCount = $stage5B4C3AJsonSerializeCallCount
        stage5B4C3ASourceWarningMessages = $stage5B4C3ASourceWarningMessages
        stage5B4C3AExpectedWmc1510Count = $stage5B4C3AExpectedWmc1510Count
        stage5B4C3AActualWmc1510Count = $stage5B4C3AActualWmc1510Count
        stage5B4C3B1SourceFiles = $stage5B4C3B1SourceFiles
        stage5B4C3B1RequiredScenarioPatterns = $stage5B4C3B1RequiredScenarioPatterns
        stage5B4C3B1MissingScenarioPatterns = $stage5B4C3B1MissingScenarioPatterns
        stage5B4C3B1RequiredProductPatterns = $stage5B4C3B1RequiredProductPatterns
        stage5B4C3B1MissingProductPatterns = $stage5B4C3B1MissingProductPatterns
        stage5B4C3B1RequiredSmokeScriptPatterns = $stage5B4C3B1RequiredSmokeScriptPatterns
        stage5B4C3B1MissingSmokeScriptPatterns = $stage5B4C3B1MissingSmokeScriptPatterns
        stage5B4C3B1ForbiddenScopePatterns = $stage5B4C3B1ForbiddenScopePatterns
        stage5B4C3B1RustAbiUnchanged = $stage5B4C3B1RustAbiUnchanged
        stage5B4C3B1JsonSerializeCallCount = $stage5B4C3B1JsonSerializeCallCount
        stage5B4C3B1SourceWarningMessages = $stage5B4C3B1SourceWarningMessages
        stage5B4C3B1ExpectedWmc1510Count = $stage5B4C3B1ExpectedWmc1510Count
        stage5B4C3B1ActualWmc1510Count = $stage5B4C3B1ActualWmc1510Count
        stage5B4C3B2ASourceFiles = $stage5B4C3B2ASourceFiles
        stage5B4C3B2ARequiredScenarioPatterns = $stage5B4C3B2ARequiredScenarioPatterns
        stage5B4C3B2AMissingScenarioPatterns = $stage5B4C3B2AMissingScenarioPatterns
        stage5B4C3B2ARequiredProductPatterns = $stage5B4C3B2ARequiredProductPatterns
        stage5B4C3B2AMissingProductPatterns = $stage5B4C3B2AMissingProductPatterns
        stage5B4C3B2ARequiredSmokeScriptPatterns = $stage5B4C3B2ARequiredSmokeScriptPatterns
        stage5B4C3B2AMissingSmokeScriptPatterns = $stage5B4C3B2AMissingSmokeScriptPatterns
        stage5B4C3B2AForbiddenScopePatterns = $stage5B4C3B2AForbiddenScopePatterns
        stage5B4C3B2ARustAbiUnchanged = $stage5B4C3B2ARustAbiUnchanged
        stage5B4C3B2AJsonSerializeCallCount = $stage5B4C3B2AJsonSerializeCallCount
        stage5B4C3B2ASourceWarningMessages = $stage5B4C3B2ASourceWarningMessages
        stage5B4C3B2AExpectedWmc1510Count = $stage5B4C3B2AExpectedWmc1510Count
        stage5B4C3B2AActualWmc1510Count = $stage5B4C3B2AActualWmc1510Count
        stage5B4C3B2B1SourceFiles = $stage5B4C3B2B1SourceFiles
        stage5B4C3B2B1RequiredScenarioPatterns = $stage5B4C3B2B1RequiredScenarioPatterns
        stage5B4C3B2B1MissingScenarioPatterns = $stage5B4C3B2B1MissingScenarioPatterns
        stage5B4C3B2B1RequiredProductPatterns = $stage5B4C3B2B1RequiredProductPatterns
        stage5B4C3B2B1MissingProductPatterns = $stage5B4C3B2B1MissingProductPatterns
        stage5B4C3B2B1RequiredSmokeScriptPatterns = $stage5B4C3B2B1RequiredSmokeScriptPatterns
        stage5B4C3B2B1MissingSmokeScriptPatterns = $stage5B4C3B2B1MissingSmokeScriptPatterns
        stage5B4C3B2B1ForbiddenScopePatterns = $stage5B4C3B2B1ForbiddenScopePatterns
        stage5B4C3B2B1RustAbiUnchanged = $stage5B4C3B2B1RustAbiUnchanged
        stage5B4C3B2B1ScenarioJsonSerializeCallCount = $stage5B4C3B2B1ScenarioJsonSerializeCallCount
        stage5B4C3B2B1StoreJsonCallCount = $stage5B4C3B2B1StoreJsonCallCount
        stage5B4C3B2B1SourceWarningMessages = $stage5B4C3B2B1SourceWarningMessages
        stage5B4C3B2B1ExpectedWmc1510Count = $stage5B4C3B2B1ExpectedWmc1510Count
        stage5B4C3B2B1ActualWmc1510Count = $stage5B4C3B2B1ActualWmc1510Count
        stage5B4C3B2B2ASourceFiles = $stage5B4C3B2B2ASourceFiles
        stage5B4C3B2B2ARequiredScenarioPatterns = $stage5B4C3B2B2ARequiredScenarioPatterns
        stage5B4C3B2B2AMissingScenarioPatterns = $stage5B4C3B2B2AMissingScenarioPatterns
        stage5B4C3B2B2ARequiredProductPatterns = $stage5B4C3B2B2ARequiredProductPatterns
        stage5B4C3B2B2AMissingProductPatterns = $stage5B4C3B2B2AMissingProductPatterns
        stage5B4C3B2B2ARequiredSmokeScriptPatterns = $stage5B4C3B2B2ARequiredSmokeScriptPatterns
        stage5B4C3B2B2AMissingSmokeScriptPatterns = $stage5B4C3B2B2AMissingSmokeScriptPatterns
        stage5B4C3B2B2AForbiddenScopePatterns = $stage5B4C3B2B2AForbiddenScopePatterns
        stage5B4C3B2B2ARustAbiUnchanged = $stage5B4C3B2B2ARustAbiUnchanged
        stage5B4C3B2B2AScenarioJsonSerializeCallCount = $stage5B4C3B2B2AScenarioJsonSerializeCallCount
        stage5B4C3B2B2AManagedUiJsonSerializeCallCount = $stage5B4C3B2B2AManagedUiJsonSerializeCallCount
        stage5B4C3B2B2ASourceWarningMessages = $stage5B4C3B2B2ASourceWarningMessages
        stage5B4C3B2B2AExpectedWmc1510Count = $stage5B4C3B2B2AExpectedWmc1510Count
        stage5B4C3B2B2AActualWmc1510Count = $stage5B4C3B2B2AActualWmc1510Count
        stage5B4C3B2B2BSourceFiles = $stage5B4C3B2B2BSourceFiles
        stage5B4C3B2B2BRequiredScenarioPatterns = $stage5B4C3B2B2BRequiredScenarioPatterns
        stage5B4C3B2B2BMissingScenarioPatterns = $stage5B4C3B2B2BMissingScenarioPatterns
        stage5B4C3B2B2BRequiredProductPatterns = $stage5B4C3B2B2BRequiredProductPatterns
        stage5B4C3B2B2BMissingProductPatterns = $stage5B4C3B2B2BMissingProductPatterns
        stage5B4C3B2B2BRequiredSmokeScriptPatterns = $stage5B4C3B2B2BRequiredSmokeScriptPatterns
        stage5B4C3B2B2BMissingSmokeScriptPatterns = $stage5B4C3B2B2BMissingSmokeScriptPatterns
        stage5B4C3B2B2BForbiddenScopePatterns = $stage5B4C3B2B2BForbiddenScopePatterns
        stage5B4C3B2B2BRustAbiUnchanged = $stage5B4C3B2B2BRustAbiUnchanged
        stage5B4C3B2B2BScenarioJsonSerializeCallCount = $stage5B4C3B2B2BScenarioJsonSerializeCallCount
        stage5B4C3B2B2BManagedUiJsonSerializeCallCount = $stage5B4C3B2B2BManagedUiJsonSerializeCallCount
        stage5B4C3B2B2BSourceWarningMessages = $stage5B4C3B2B2BSourceWarningMessages
        stage5B4C3B2B2BExpectedWmc1510Count = $stage5B4C3B2B2BExpectedWmc1510Count
        stage5B4C3B2B2BActualWmc1510Count = $stage5B4C3B2B2BActualWmc1510Count
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
            jitDefault = "csharp"
            explicitOptInEnvironmentVariable = "DESKBOX_SHORTCUT_BACKEND"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
        }
        musicVolumeBackendPolicy = [ordered]@{
            jitDefault = "csharp"
            explicitOptInEnvironmentVariable = "DESKBOX_MUSIC_VOLUME_BACKEND"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
        }
        explorerShellBackendPolicy = [ordered]@{
            jitDefault = "csharp"
            explicitOptInEnvironmentVariable = "DESKBOX_EXPLORER_SHELL_BACKEND"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            productFallback = "Process.Start/SHOpenWithDialog"
        }
        quickAccessBackendPolicy = [ordered]@{
            jitDefault = "csharp"
            explicitOptInEnvironmentVariable = "DESKBOX_QUICK_ACCESS_BACKEND"
            nativeAot = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            asynchronousApartment = "dedicated STA"
        }
        recycleBinBackendPolicy = [ordered]@{
            productDelete = "C# SHFileOperationW"
            nativeAotExactQueryAndRestore = "rust"
            nativeAotCompileTimeDefine = "DESKBOX_NATIVE_AOT"
            fallbackOnNativeFailure = $false
            exactIdentity = "original parent plus item name"
            restoreRequiresExactlyOneMatch = $true
        }
    }
}

$summaryJson = $summary | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    $summaryPath,
    $summaryJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

if (-not $sourceStableDuringAudit) {
    throw "The repository changed while the AOT audit was running. The output is not a trusted source snapshot; see '$summaryPath'."
}

if ($warningCodes.Count -gt 0) {
    Write-Warning "AOT analysis warnings remain: $($warningCodes -join ', ')"
}

if ($alwaysThrowMessages.Count -gt 0) {
    Write-Warning "ILC reported $($alwaysThrowMessages.Count) method(s) that will always throw."
}

if ($targetedWarningCounts.MVVMTK0045 -ne 0 -or
    $targetedWarningCounts.CsWinRT1028 -ne 0) {
    throw "AOT compatibility regression detected in migrated MVVM or CsWinRT ABI declarations. See '$summaryPath'."
}

if ($stage4D1AWarningMessages.Count -gt 0) {
    throw "Stage 4D-1A target files still produce AOT analysis warnings. See '$summaryPath'."
}

if ($stage4D1BWarningMessages.Count -gt 0) {
    throw "Stage 4D-1B target files still produce AOT analysis warnings. See '$summaryPath'."
}

if ($stage4D2UnexpectedExistingSourceFiles.Count -gt 0) {
    throw "Stage 4D-2 removed source files are present: $($stage4D2UnexpectedExistingSourceFiles -join ', '). See '$summaryPath'."
}

if ($stage4D2FileOperationWarningMessages.Count -gt 0) {
    throw "Stage 4D-2 dead IFileOperation warnings remain. See '$summaryPath'."
}

if ($stage4D3ALegacyRcwSourceMatches.Count -gt 0) {
    throw "Stage 4D-3A legacy data-object RCW patterns remain: $($stage4D3ALegacyRcwSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4D3ADataReaderWarningMessages.Count -gt 0) {
    throw "Stage 4D-3A data reader produced AOT warnings. See '$summaryPath'."
}

if ($stage4D3BLegacyRegistrationSourceMatches.Count -gt 0) {
    throw "Stage 4D-3B legacy drop-target registration patterns remain: $($stage4D3BLegacyRegistrationSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4D3BMissingGeneratedComPatterns.Count -gt 0) {
    throw "Stage 4D-3B generated COM registration patterns are missing: $($stage4D3BMissingGeneratedComPatterns -join ', '). See '$summaryPath'."
}

if ($stage4D3BWarningMessages.Count -gt 0) {
    throw "Stage 4D-3B drop-target boundary produced AOT warnings. See '$summaryPath'."
}

if ($stage4D3BIl2050WarningMessages.Count -gt 0) {
    throw "Stage 4D-3B did not eliminate every IL2050 warning. See '$summaryPath'."
}

if ($stage4D4AWarningMessages.Count -gt 0) {
    throw "Stage 4D-4A Explorer-shell boundary produced AOT warnings. See '$summaryPath'."
}

if ($stage4D4BWarningMessages.Count -gt 0) {
    throw "Stage 4D-4B Quick Access boundary produced AOT warnings. See '$summaryPath'."
}

if ($stage4D5LegacyReflectionSourceMatches.Count -gt 0) {
    throw "Stage 4D-5 tray reflection patterns remain: $($stage4D5LegacyReflectionSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4D5MissingPublicPatterns.Count -gt 0) {
    throw "Stage 4D-5 public tray contracts are missing: $($stage4D5MissingPublicPatterns -join ', '). See '$summaryPath'."
}

if ($stage4D5WarningMessages.Count -gt 0) {
    throw "Stage 4D-5 tray sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E0LegacyOneWaySourceMatches.Count -gt 0) {
    throw "Stage 4E-0 legacy OneWay search-history bindings remain: $($stage4E0LegacyOneWaySourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E0MissingOneTimeBindings.Count -gt 0) {
    throw "Stage 4E-0 OneTime search-history binding counts changed: $($stage4E0MissingOneTimeBindings -join ', '). See '$summaryPath'."
}

if ($stage4E0MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-0 immutable search-history behavior contracts are missing: $($stage4E0MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E0Wmc1506WarningMessages.Count -gt 0) {
    throw "Stage 4E-0 search history bindings produced WMC1506 warnings. See '$summaryPath'."
}

if ($stage4E0SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-0 search widget sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E1LegacyBindingSourceMatches.Count -gt 0) {
    throw "Stage 4E-1 legacy leaf bindings remain: $($stage4E1LegacyBindingSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E1MissingCompiledBindings.Count -gt 0) {
    throw "Stage 4E-1 compiled binding counts changed: $($stage4E1MissingCompiledBindings -join ', '). See '$summaryPath'."
}

if ($stage4E1MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-1 dependency-property or refresh contracts are missing: $($stage4E1MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E1MissingDeferredBindings.Count -gt 0) {
    throw "Stage 4E-1 deferred runtime/style bindings changed scope: $($stage4E1MissingDeferredBindings -join ', '). See '$summaryPath'."
}

if ($stage4E1SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-1 leaf XAML sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E1ActualWmc1510Count -gt $stage4E1MaximumWmc1510Count) {
    throw "Stage 4E-1 WMC1510 count regressed above its ceiling: maximum=$stage4E1MaximumWmc1510Count actual=$stage4E1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage4E2LegacyBindingSourceMatches.Count -gt 0) {
    throw "Stage 4E-2 legacy leaf bindings remain: $($stage4E2LegacyBindingSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E2MissingCompiledBindings.Count -gt 0) {
    throw "Stage 4E-2 compiled binding counts changed: $($stage4E2MissingCompiledBindings -join ', '). See '$summaryPath'."
}

if ($stage4E2MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-2 dependency-property or interaction contracts are missing: $($stage4E2MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E2MissingDeferredBindings.Count -gt 0) {
    throw "Stage 4E-2 deferred runtime/style bindings changed scope: $($stage4E2MissingDeferredBindings -join ', '). See '$summaryPath'."
}

if ($stage4E2SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-2 leaf XAML sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E2ActualWmc1510Count -gt $stage4E2MaximumWmc1510Count) {
    throw "Stage 4E-2 WMC1510 count regressed above its ceiling: maximum=$stage4E2MaximumWmc1510Count actual=$stage4E2ActualWmc1510Count. See '$summaryPath'."
}

if ($stage4E3LegacyBindingSourceMatches.Count -gt 0) {
    throw "Stage 4E-3 legacy DataTemplate bindings remain: $($stage4E3LegacyBindingSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E3MissingCompiledBindings.Count -gt 0) {
    throw "Stage 4E-3 compiled DataTemplate binding counts changed: $($stage4E3MissingCompiledBindings -join ', '). See '$summaryPath'."
}

if ($stage4E3MissingDataTypes.Count -gt 0) {
    throw "Stage 4E-3 typed DataTemplate declarations changed: $($stage4E3MissingDataTypes -join ', '). See '$summaryPath'."
}

if ($stage4E3MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-3 notification, lazy-refresh, or interaction contracts are missing: $($stage4E3MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E3MissingDeferredBindings.Count -gt 0) {
    throw "Stage 4E-3 deferred runtime DataContext bindings changed scope: $($stage4E3MissingDeferredBindings -join ', '). See '$summaryPath'."
}

if ($stage4E3SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-3 typed DataTemplate sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E3ActualWmc1510Count -gt $stage4E3MaximumWmc1510Count) {
    throw "Stage 4E-3 WMC1510 count regressed above its ceiling: maximum=$stage4E3MaximumWmc1510Count actual=$stage4E3ActualWmc1510Count. See '$summaryPath'."
}

if ($stage4E4LegacyBindingSourceMatches.Count -gt 0) {
    throw "Stage 4E-4 legacy FileWidgetSettingsSection bindings remain: $($stage4E4LegacyBindingSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E4MissingCompiledBindings.Count -gt 0) {
    throw "Stage 4E-4 compiled ViewModel binding counts changed: $($stage4E4MissingCompiledBindings -join ', '). See '$summaryPath'."
}

if ($stage4E4MissingViewModelBridgePatterns.Count -gt 0) {
    throw "Stage 4E-4 typed ViewModel bridge patterns are missing: $($stage4E4MissingViewModelBridgePatterns -join ', '). See '$summaryPath'."
}

if (-not $stage4E4ViewModelBridgeOrderValid) {
    throw "Stage 4E-4 SettingsWindow ViewModel bridge assignment/clear order changed. See '$summaryPath'."
}

if ($stage4E4UnexpectedManualBridgePatterns.Count -gt 0) {
    throw "Stage 4E-4 added redundant manual compiled-binding refresh code: $($stage4E4UnexpectedManualBridgePatterns -join ', '). See '$summaryPath'."
}

if ($stage4E4MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-4 notification, selection, or persistence contracts are missing: $($stage4E4MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E4MissingDeferredBindings.Count -gt 0) {
    throw "Stage 4E-4 deferred runtime/style bindings changed scope: $($stage4E4MissingDeferredBindings -join ', '). See '$summaryPath'."
}

if ($stage4E4SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-4 typed ViewModel bridge sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E4ActualWmc1510Count -gt $stage4E4MaximumWmc1510Count) {
    throw "Stage 4E-4 WMC1510 count regressed above its ceiling: maximum=$stage4E4MaximumWmc1510Count actual=$stage4E4ActualWmc1510Count. See '$summaryPath'."
}

if ($stage4E5LegacyBindingSourceMatches.Count -gt 0) {
    throw "Stage 4E-5 legacy search-result row bindings remain: $($stage4E5LegacyBindingSourceMatches -join ', '). See '$summaryPath'."
}

if ($stage4E5MissingCompiledBindings.Count -gt 0) {
    throw "Stage 4E-5 compiled search-result row binding counts changed: $($stage4E5MissingCompiledBindings -join ', '). See '$summaryPath'."
}

if ($stage4E5MissingItemBridgePatterns.Count -gt 0) {
    throw "Stage 4E-5 internal typed Item bridge patterns are missing: $($stage4E5MissingItemBridgePatterns -join ', '). See '$summaryPath'."
}

if ($stage4E5UnexpectedPublicItemBridgePatterns.Count -gt 0) {
    throw "Stage 4E-5 exposed SearchResultItem to the XAML activator: $($stage4E5UnexpectedPublicItemBridgePatterns -join ', '). See '$summaryPath'."
}

if (-not $stage4E5ItemRefreshOrderValid) {
    throw "Stage 4E-5 item preparation no longer precedes the lazy metadata refresh. See '$summaryPath'."
}

if ($stage4E5MissingBehaviorPatterns.Count -gt 0) {
    throw "Stage 4E-5 recycle, lazy metadata, selection, or lookup contracts are missing: $($stage4E5MissingBehaviorPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E5MissingRequiredModelPatterns.Count -gt 0) {
    throw "Stage 4E-5 SearchResultItem required-member or lazy-metadata contracts changed: $($stage4E5MissingRequiredModelPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E5UnexpectedObservableModelPatterns.Count -gt 0) {
    throw "Stage 4E-5 added fake observability to SearchResultItem: $($stage4E5UnexpectedObservableModelPatterns -join ', '). See '$summaryPath'."
}

if ($stage4E5UnexpectedDataContextOverridePatterns.Count -gt 0) {
    throw "Stage 4E-5 replaced the repeater DataContext interaction boundary: $($stage4E5UnexpectedDataContextOverridePatterns -join ', '). See '$summaryPath'."
}

if (-not $stage4E5LifecycleOrderValid) {
    throw "Stage 4E-5 repeater unhook, ItemsSource clear, or ViewModel disposal order changed. See '$summaryPath'."
}

if ($stage4E5MissingDeferredBindings.Count -gt 0) {
    throw "Stage 4E-5 deferred runtime/style bindings changed scope: $($stage4E5MissingDeferredBindings -join ', '). See '$summaryPath'."
}

if ($stage4E5SourceWarningMessages.Count -gt 0) {
    throw "Stage 4E-5 search-result row sources produced AOT warnings. See '$summaryPath'."
}

if ($stage4E5ActualWmc1510Count -ne $stage4E5ExpectedWmc1510Count) {
    throw "Stage 4E-5 WMC1510 count changed: expected=$stage4E5ExpectedWmc1510Count actual=$stage4E5ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5AMissingDataPathPatterns.Count -gt 0) {
    throw "Stage 5A Native AOT preview data-root isolation changed: $($stage5AMissingDataPathPatterns -join ', '). See '$summaryPath'."
}

if ($stage5AMissingLauncherPatterns.Count -gt 0) {
    throw "Stage 5A Native AOT preview launcher gates are missing: $($stage5AMissingLauncherPatterns -join ', '). See '$summaryPath'."
}

if ($stage5AUnsafeLauncherPatterns.Count -gt 0) {
    throw "Stage 5A Native AOT preview launcher contains unsafe production/repository-wide behavior: $($stage5AUnsafeLauncherPatterns -join ', '). See '$summaryPath'."
}

if ($stage5ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5A data-root isolation produced AOT warnings. See '$summaryPath'."
}

if ($stage5AActualWmc1510Count -ne $stage5AExpectedWmc1510Count) {
    throw "Stage 5A WMC1510 count changed: expected=$stage5AExpectedWmc1510Count actual=$stage5AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B1MissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-1 AOT shortcut runner contracts are missing: $($stage5B1MissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B1MissingLaunchPatterns.Count -gt 0 -or -not $stage5B1LaunchOrderValid) {
    throw "Stage 5B-1 AOT shortcut smoke is not scheduled after successful launch initialization. See '$summaryPath'."
}

if ($stage5B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-1 shortcut smoke script gates are missing: $($stage5B1MissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B1UnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-1 shortcut runner contains unsafe non-preview behavior: $($stage5B1UnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-1 shortcut smoke sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B1ActualWmc1510Count -ne $stage5B1ExpectedWmc1510Count) {
    throw "Stage 5B-1 WMC1510 count changed: expected=$stage5B1ExpectedWmc1510Count actual=$stage5B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B2AMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-2A AOT shell runner contracts are missing: $($stage5B2AMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2AMissingLaunchPatterns.Count -gt 0 -or -not $stage5B2ALaunchOrderValid) {
    throw "Stage 5B-2A AOT shell smoke is not scheduled after successful launch initialization. See '$summaryPath'."
}

if ($stage5B2AMissingServicePatterns.Count -gt 0 -or
    $stage5B2AMissingQuickAccessPatterns.Count -gt 0) {
    throw "Stage 5B-2A product read boundaries are incomplete. See '$summaryPath'."
}

if ($stage5B2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-2A shell smoke script gates are missing: $($stage5B2AMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2AUnsafeMutationPatterns.Count -gt 0) {
    throw "Stage 5B-2A read-only runner contains Quick Access mutation operations: $($stage5B2AUnsafeMutationPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2AUnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-2A shell runner contains unsafe non-preview behavior: $($stage5B2AUnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-2A shell smoke sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B2AActualWmc1510Count -ne $stage5B2AExpectedWmc1510Count) {
    throw "Stage 5B-2A WMC1510 count changed: expected=$stage5B2AExpectedWmc1510Count actual=$stage5B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B2BMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-2B AOT Quick Access mutation runner contracts are missing: $($stage5B2BMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2BMissingLaunchPatterns.Count -gt 0 -or -not $stage5B2BLaunchOrderValid) {
    throw "Stage 5B-2B AOT Quick Access mutation smoke is not scheduled after successful launch initialization. See '$summaryPath'."
}

if ($stage5B2BMissingQuickAccessPatterns.Count -gt 0) {
    throw "Stage 5B-2B product Quick Access mutation boundaries are incomplete: $($stage5B2BMissingQuickAccessPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2BMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-2B Quick Access mutation script gates are missing: $($stage5B2BMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2BUnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-2B Quick Access mutation runner contains unsafe cleanup or direct native mutation: $($stage5B2BUnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B2BSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-2B Quick Access mutation sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B2BActualWmc1510Count -ne $stage5B2BExpectedWmc1510Count) {
    throw "Stage 5B-2B WMC1510 count changed: expected=$stage5B2BExpectedWmc1510Count actual=$stage5B2BActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B3AMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3A AOT music-volume read runner contracts are missing: $($stage5B3AMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3AMissingLaunchPatterns.Count -gt 0 -or -not $stage5B3ALaunchOrderValid) {
    throw "Stage 5B-3A AOT music-volume read smoke is not scheduled after successful launch initialization. See '$summaryPath'."
}

if ($stage5B3AMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-3A product music-volume read boundaries are incomplete: $($stage5B3AMissingProductPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-3A music-volume read smoke script gates are missing: $($stage5B3AMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3AUnsafeMutationPatterns.Count -gt 0) {
    throw "Stage 5B-3A read-only runner contains music-volume setter operations: $($stage5B3AUnsafeMutationPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3AUnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3A music-volume read runner contains unsafe non-preview behavior: $($stage5B3AUnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-3A music-volume read sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B3AActualWmc1510Count -ne $stage5B3AExpectedWmc1510Count) {
    throw "Stage 5B-3A WMC1510 count changed: expected=$stage5B3AExpectedWmc1510Count actual=$stage5B3AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B3BMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3B AOT system-volume mutation runner contracts are missing: $($stage5B3BMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3BMissingLaunchPatterns.Count -gt 0 -or -not $stage5B3BLaunchOrderValid) {
    throw "Stage 5B-3B AOT system-volume mutation smoke is not scheduled after successful launch initialization. See '$summaryPath'."
}

if ($stage5B3BMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-3B product system-volume setter boundary is incomplete: $($stage5B3BMissingProductPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3BMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-3B system-volume mutation script gates are missing: $($stage5B3BMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3BUnsafeMutationPatterns.Count -gt 0) {
    throw "Stage 5B-3B runner bypasses the product setter or reaches session mutation: $($stage5B3BUnsafeMutationPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3BUnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3B system-volume runner contains unsafe non-preview behavior: $($stage5B3BUnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if (-not $stage5B3BRecoveryOrderValid) {
    throw "Stage 5B-3B recovery ordering changed: intent must precede mutation and verified recovery must precede intent deletion. See '$summaryPath'."
}

if ($stage5B3BSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-3B system-volume mutation sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B3BActualWmc1510Count -ne $stage5B3BExpectedWmc1510Count) {
    throw "Stage 5B-3B WMC1510 count changed: expected=$stage5B3BExpectedWmc1510Count actual=$stage5B3BActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B3CMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3C AOT session-volume runner contracts are missing: $($stage5B3CMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CMissingLaunchPatterns.Count -gt 0 -or -not $stage5B3CLaunchOrderValid) {
    throw "Stage 5B-3C AOT session-volume smoke is not scheduled after the system-volume smoke. See '$summaryPath'."
}

if ($stage5B3CMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-3C product session getter/setter boundary is incomplete: $($stage5B3CMissingProductPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CMissingFixturePatterns.Count -gt 0) {
    throw "Stage 5B-3C controlled silent Rust fixture is incomplete: $($stage5B3CMissingFixturePatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-3C session-volume mutation script gates are missing: $($stage5B3CMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CUnsafeMutationPatterns.Count -gt 0) {
    throw "Stage 5B-3C runner bypasses the product session setter or reaches system mutation: $($stage5B3CUnsafeMutationPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CUnsafeRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-3C session-volume runner contains unsafe non-preview behavior: $($stage5B3CUnsafeRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B3CUnsafeFixtureScriptPatterns.Count -gt 0) {
    throw "Stage 5B-3C fixture/script process isolation is unsafe: $($stage5B3CUnsafeFixtureScriptPatterns -join ', '). See '$summaryPath'."
}

if (-not $stage5B3CRecoveryOrderValid) {
    throw "Stage 5B-3C recovery ordering changed: identity/original intent must precede session mutation and verified matched recovery must precede intent deletion. See '$summaryPath'."
}

if ($stage5B3CSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-3C session-volume sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B3CActualWmc1510Count -ne $stage5B3CExpectedWmc1510Count) {
    throw "Stage 5B-3C WMC1510 count changed: expected=$stage5B3CExpectedWmc1510Count actual=$stage5B3CActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4AMissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-4A managed UI runner contracts are missing: $($stage5B4AMissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4AMissingLaunchPatterns.Count -gt 0 -or -not $stage5B4ALaunchOrderValid) {
    throw "Stage 5B-4A managed UI smoke is not scheduled after all native boundary smokes. See '$summaryPath'."
}

if ($stage5B4AMissingSettingsPatterns.Count -gt 0) {
    throw "Stage 5B-4A settings-window diagnostic contracts are missing: $($stage5B4AMissingSettingsPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4AMissingSettingsNavigationPatterns.Count -gt 0 -or
    $stage5B4AUnsafeSettingsNavigationPatterns.Count -gt 0) {
    throw "Stage 5B-4A settings search empty-state AOT projection guard is incomplete. See '$summaryPath'."
}

if ($stage5B4AMissingSearchPatterns.Count -gt 0 -or
    $stage5B4ASortHandlerCountViolations.Count -gt 0) {
    throw "Stage 5B-4A search control routing contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4AMissingLocalePatterns.Count -gt 0) {
    throw "Stage 5B-4A locale resource diagnostic contracts are missing: $($stage5B4AMissingLocalePatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4A managed UI outer-runner gates are missing: $($stage5B4AMissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4AMissingSmokeOptInIsolation.Count -gt 0) {
    throw "Stage 5B-4A smoke opt-in isolation is incomplete: $($stage5B4AMissingSmokeOptInIsolation -join ', '). See '$summaryPath'."
}

if ($stage5B4AUnsafeRunnerPatterns.Count -gt 0 -or
    $stage5B4AUnsafeSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4A managed UI matrix contains a forbidden mutation or broad process operation. See '$summaryPath'."
}

if ($stage5B4AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4A managed UI evidence must use exactly one source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4A managed UI sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4AActualWmc1510Count -ne $stage5B4AExpectedWmc1510Count) {
    throw "Stage 5B-4A WMC1510 count changed: expected=$stage5B4AExpectedWmc1510Count actual=$stage5B4AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B1MissingRunnerPatterns.Count -gt 0) {
    throw "Stage 5B-4B1 deep-settings runner contracts are missing: $($stage5B4B1MissingRunnerPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4B1MissingSettingsPatterns.Count -gt 0 -or
    $stage5B4B1MissingNavigationPatterns.Count -gt 0 -or
    $stage5B4B1MissingProjectionPatterns.Count -gt 0 -or
    $stage5B4B1MissingInventoryPatterns.Count -gt 0 -or
    $stage5B4B1MissingBindableTypePatterns.Count -gt 0 -or
    $stage5B4B1MissingFileStackXamlPatterns.Count -gt 0 -or
    $stage5B4B1MissingFileWidgetProjectionPatterns.Count -gt 0 -or
    $stage5B4B1MissingWeatherProjectionPatterns.Count -gt 0 -or
    $stage5B4B1MissingCommandXamlPatterns.Count -gt 0 -or
    $stage5B4B1MissingCapsuleCommandXamlPatterns.Count -gt 0 -or
    $stage5B4B1MissingCapsuleCodeBehindPatterns.Count -gt 0 -or
    $stage5B4B1MissingRoutePatterns.Count -gt 0) {
    throw "Stage 5B-4B1 deep-settings search, navigation, breadcrumb, or route contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B1ActualBindableViewModelPropertyCount -ne
        $stage5B4B1ExpectedBindableViewModelPropertyCount -or
    $stage5B4B1UnsafeBindableViewModelPatterns.Count -gt 0) {
    throw "Stage 5B-4B1 SettingsViewModel generated binding scope is incomplete or includes unsupported generated commands. See '$summaryPath'."
}

if ($stage5B4B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B1 managed UI outer-runner gates are missing: $($stage5B4B1MissingSmokeScriptPatterns -join ', '). See '$summaryPath'."
}

if ($stage5B4B1UnsafeMutationPatterns.Count -gt 0) {
    throw "Stage 5B-4B1 deep-settings matrix contains a forbidden mutation or broad process operation. See '$summaryPath'."
}

if ($stage5B4B1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B1 deep-settings evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B1 deep-settings sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B1ActualWmc1510Count -ne $stage5B4B1ExpectedWmc1510Count) {
    throw "Stage 5B-4B1 WMC1510 count changed: expected=$stage5B4B1ExpectedWmc1510Count actual=$stage5B4B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2AMissingManagerPatterns.Count -gt 0 -or
    $stage5B4B2AMissingBoundsPatterns.Count -gt 0) {
    throw "Stage 5B-4B2A managed persistence runner, widget, or HWND bounds contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2AMissingSmokeScriptPatterns.Count -gt 0 -or
    $stage5B4B2AMissingLauncherPatterns.Count -gt 0) {
    throw "Stage 5B-4B2A three-process outer-runner or natural-exit launcher gates are missing. See '$summaryPath'."
}

if ($stage5B4B2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2A persistence matrix entered deferred content stores, OS interaction, or broad process scope. See '$summaryPath'."
}

if ($stage5B4B2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2A persistence sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2AActualWmc1510Count -ne $stage5B4B2AExpectedWmc1510Count) {
    throw "Stage 5B-4B2A WMC1510 count changed: expected=$stage5B4B2AExpectedWmc1510Count actual=$stage5B4B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2B1MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2B1MissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2B1MissingProductSurfacePatterns.Count -gt 0 -or
    $stage5B4B2B1MissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B1 Quick Capture runner, real UI timer, store, attachment, or host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B1 three-process outer-runner, cleanup, or natural-exit gates are missing. See '$summaryPath'."
}

if ($stage5B4B2B1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2B1 entered a deferred store, OS interaction, direct file mutation, or broad widget scope. See '$summaryPath'."
}

if ($stage5B4B2B1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2B1 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2B1 Quick Capture sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2B1ActualWmc1510Count -ne $stage5B4B2B1ExpectedWmc1510Count) {
    throw "Stage 5B-4B2B1 WMC1510 count changed: expected=$stage5B4B2B1ExpectedWmc1510Count actual=$stage5B4B2B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2B2AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2B2AMissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2B2AMissingProductPatterns.Count -gt 0 -or
    $stage5B4B2B2AMissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2A Todo runner, real UI timer/save paths, store, or host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2B2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2A three-process outer-runner, cleanup, or natural-exit gates are missing. See '$summaryPath'."
}

if ($stage5B4B2B2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2A entered deferred Todo steps/attachments/reminders/recurrence, direct mutation, OS interaction, or broad widget scope. See '$summaryPath'."
}

if ($stage5B4B2B2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2B2A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2B2A Todo sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2B2AActualWmc1510Count -ne $stage5B4B2B2AExpectedWmc1510Count) {
    throw "Stage 5B-4B2B2A WMC1510 count changed: expected=$stage5B4B2B2AExpectedWmc1510Count actual=$stage5B4B2B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2B2B1MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2B2B1MissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2B2B1MissingProductPatterns.Count -gt 0 -or
    $stage5B4B2B2B1MissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B1 Todo steps runner, real row UI, product paths, projection, store, or host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2B2B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B1 three-process outer-runner, cleanup, process, or natural-exit gates are missing. See '$summaryPath'."
}

if ($stage5B4B2B2B1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B1 entered deferred Todo attachments/reminders/recurrence, direct store mutation, OS interaction, or broad widget scope. See '$summaryPath'."
}

if ($stage5B4B2B2B1GeneratedBindableCount -ne 3) {
    throw "Stage 5B-4B2B2B1 must expose exactly the three exercised Todo AOT DataContext types. See '$summaryPath'."
}

if ($stage5B4B2B2B1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2B2B1 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2B2B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2B2B1 Todo steps sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2B2B1ActualWmc1510Count -ne $stage5B4B2B2B1ExpectedWmc1510Count) {
    throw "Stage 5B-4B2B2B1 WMC1510 count changed: expected=$stage5B4B2B2B1ExpectedWmc1510Count actual=$stage5B4B2B2B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2B2B2MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2B2B2MissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2B2B2MissingTilePatterns.Count -gt 0 -or
    $stage5B4B2B2B2MissingProductPatterns.Count -gt 0 -or
    $stage5B4B2B2B2MissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B2 Todo managed attachment runner, real tile UI, product paths, storage, projection, or host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2B2B2MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B2 three-process outer-runner, hash, physical-delete, cleanup, process, or natural-exit gates are missing. See '$summaryPath'."
}

if ($stage5B4B2B2B2ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2B2B2 entered deferred Todo reminders/recurrence, direct store mutation, OS picker/shell interaction, Rust ABI expansion, or broad widget scope. See '$summaryPath'."
}

if ($stage5B4B2B2B2GeneratedBindableCount -ne 3) {
    throw "Stage 5B-4B2B2B2 must retain exactly the three exercised Todo AOT DataContext bridge types. See '$summaryPath'."
}

if ($stage5B4B2B2B2JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2B2B2 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2B2B2SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2B2B2 Todo managed attachment sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2B2B2ActualWmc1510Count -ne $stage5B4B2B2B2ExpectedWmc1510Count) {
    throw "Stage 5B-4B2B2B2 WMC1510 count changed: expected=$stage5B4B2B2B2ExpectedWmc1510Count actual=$stage5B4B2B2B2ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2C1MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2C1MissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2C1MissingProductPatterns.Count -gt 0 -or
    $stage5B4B2C1MissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C1 Glance runner, product policy, ViewModel, decoded image surface, or host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2C1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C1 three-process outer-runner, image hash, process, cleanup, or postflight gates are missing. See '$summaryPath'."
}

if ($stage5B4B2C1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2C1 entered online images, network, picker/folder interaction, direct file mutation, Rust ABI expansion, or broad widget scope. See '$summaryPath'."
}

if ($stage5B4B2C1GeneratedBindableCount -ne 1 -or
    $stage5B4B2C1BindablePropertyCount -ne 33) {
    throw "Stage 5B-4B2C1 must expose exactly one narrow Glance AOT DataContext bridge with 33 XAML properties. See '$summaryPath'."
}

if ($stage5B4B2C1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2C1 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2C1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2C1 Glance persistence sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2C1ActualWmc1510Count -ne $stage5B4B2C1ExpectedWmc1510Count) {
    throw "Stage 5B-4B2C1 WMC1510 count changed: expected=$stage5B4B2C1ExpectedWmc1510Count actual=$stage5B4B2C1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2C2AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2C2AMissingPolicyPatterns.Count -gt 0 -or
    $stage5B4B2C2AMissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2A Weather settings runner, product policy, metadata, or suppressed-host contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2C2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2A three-process outer-runner, equality, process, offline-log, cleanup, or postflight gates are missing. See '$summaryPath'."
}

if ($stage5B4B2C2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2A entered deferred Weather surface/data/network/location/picker or Rust paths. See '$summaryPath'."
}

if ($stage5B4B2C2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2C2A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2C2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2C2A Weather settings persistence sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2C2AActualWmc1510Count -ne $stage5B4B2C2AExpectedWmc1510Count) {
    throw "Stage 5B-4B2C2A WMC1510 count changed: expected=$stage5B4B2C2AExpectedWmc1510Count actual=$stage5B4B2C2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4B2C2BMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4B2C2BMissingFixturePatterns.Count -gt 0 -or
    $stage5B4B2C2BMissingSurfacePatterns.Count -gt 0 -or
    $stage5B4B2C2BMissingManagerPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2B WeatherData fixture, real surface, generated binding, host, or runner contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4B2C2BBindableAttributeCount -ne 3) {
    throw "Stage 5B-4B2C2B must expose exactly three generated Weather bindable providers. See '$summaryPath'."
}

if ($stage5B4B2C2BMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2B three-process outer-runner, equality, fixture-log, offline, cleanup, or postflight gates are missing. See '$summaryPath'."
}

if ($stage5B4B2C2BForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4B2C2B fixture or real-surface probe entered production network, location, picker, file-write, or Rust paths. See '$summaryPath'."
}

if ($stage5B4B2C2BJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4B2C2B evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4B2C2BSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4B2C2B Weather surface persistence sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4B2C2BActualWmc1510Count -ne $stage5B4B2C2BExpectedWmc1510Count) {
    throw "Stage 5B-4B2C2B WMC1510 count changed: expected=$stage5B4B2C2BExpectedWmc1510Count actual=$stage5B4B2C2BActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1AMissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1AMissingSurfacePatterns.Count -gt 0 -or
    $stage5B4C1AMissingBindablePatterns.Count -gt 0) {
    throw "Stage 5B-4C1A owned local-file fixture, real surface, operation, or generated binding contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1ABindableAttributeCount -ne 3) {
    throw "Stage 5B-4C1A must expose exactly three narrow generated File Widget bindable providers. See '$summaryPath'."
}

if ($stage5B4C1AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1A three-process outer-runner, independent disk, equality, cleanup, or postflight gates are missing. See '$summaryPath'."
}

if ($stage5B4C1AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1A entered deferred Shell, picker, drag/drop, recycle, hotkey, media, network, or Rust paths. See '$summaryPath'."
}

if ($stage5B4C1AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1A local-file surface sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1AActualWmc1510Count -ne $stage5B4C1AExpectedWmc1510Count) {
    throw "Stage 5B-4C1A WMC1510 count changed: expected=$stage5B4C1AExpectedWmc1510Count actual=$stage5B4C1AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1B1MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1B1MissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1B1MissingProductPatterns.Count -gt 0 -or
    $stage5B4C1B1MissingMenuPatterns.Count -gt 0 -or
    $stage5B4C1B1MissingScenarioPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B1 scenario, owned identity, product menu, operation, or evidence contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1B1MissingNativePatterns.Count -gt 0 -or
    -not $stage5B4C1B1RestoreInvokeAfterEnumeration) {
    throw "Stage 5B-4C1B1 exact native Recycle Bin ABI, full enumeration, or unique-restore contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B1 three-process runner, exact hash, compensation, isolation, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C1B1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1B1 entered deferred Shell progress, Properties, picker, physical drag/drop, or broad Recycle Bin paths. See '$summaryPath'."
}

if ($stage5B4C1B1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1B1 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1B1 Recycle Bin sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1B1ActualWmc1510Count -ne $stage5B4C1B1ExpectedWmc1510Count) {
    throw "Stage 5B-4C1B1 WMC1510 count changed: expected=$stage5B4C1B1ExpectedWmc1510Count actual=$stage5B4C1B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1B2AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1B2AMissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1B2AMissingProductPatterns.Count -gt 0 -or
    $stage5B4C1B2AMissingMenuPatterns.Count -gt 0 -or
    $stage5B4C1B2AMissingScenarioPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2A scenario, owned fixture, real owner, product menu, or Shell move branch contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1B2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2A three-process runner, exact hash, compensation, isolation, runtime-log, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C1B2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2A entered deferred Properties, picker, physical drag/drop, IFileOperation, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C1B2ARustAbiUnchanged) {
    throw "Stage 5B-4C1B2A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C1B2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1B2A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1B2A Shell move sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1B2AActualWmc1510Count -ne $stage5B4C1B2AExpectedWmc1510Count) {
    throw "Stage 5B-4C1B2A WMC1510 count changed: expected=$stage5B4C1B2AExpectedWmc1510Count actual=$stage5B4C1B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1B2BMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1B2BMissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1B2BMissingProductPatterns.Count -gt 0 -or
    $stage5B4C1B2BMissingMenuPatterns.Count -gt 0 -or
    $stage5B4C1B2BMissingScenarioPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2B scenario, owned target, product menu, real owner, dialog, or close contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1B2BMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2B real-dialog runner, hash, isolation, natural-exit, runtime-log, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C1B2BForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1B2B entered deferred picker, physical drag/drop, IFileOperation, Recycle Bin, Shell move, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C1B2BRustAbiUnchanged) {
    throw "Stage 5B-4C1B2B changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C1B2BJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1B2B evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1B2BSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1B2B file Properties sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1B2BActualWmc1510Count -ne $stage5B4C1B2BExpectedWmc1510Count) {
    throw "Stage 5B-4C1B2B WMC1510 count changed: expected=$stage5B4C1B2BExpectedWmc1510Count actual=$stage5B4C1B2BActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1C1MissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1C1MissingProductPatterns.Count -gt 0 -or
    $stage5B4C1C1MissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1C1MissingProbePatterns.Count -gt 0 -or
    $stage5B4C1C1MissingScenarioPatterns.Count -gt 0) {
    throw "Stage 5B-4C1C1 modern picker, owner, cancel/select, StorageItems, import, or restart contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1C1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1C1 real picker UI Automation, isolation, natural-exit, fingerprint, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C1C1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1C1 entered deferred OLE/native drop, IFileOperation, global clipboard mutation, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C1C1RustAbiUnchanged) {
    throw "Stage 5B-4C1C1 changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C1C1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1C1 evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1C1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1C1 picker/StorageItems sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1C1ActualWmc1510Count -ne $stage5B4C1C1ExpectedWmc1510Count) {
    throw "Stage 5B-4C1C1 WMC1510 count changed: expected=$stage5B4C1C1ExpectedWmc1510Count actual=$stage5B4C1C1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C1C2AMissingRunnerPatterns.Count -gt 0 -or
    $stage5B4C1C2AMissingProductPatterns.Count -gt 0 -or
    $stage5B4C1C2AMissingFixturePatterns.Count -gt 0 -or
    $stage5B4C1C2AMissingProbePatterns.Count -gt 0 -or
    $stage5B4C1C2AMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C1C2AMissingVisualPatterns.Count -gt 0) {
    throw "Stage 5B-4C1C2A native OLE callback, stale-highlight, copy/move, progress, or visual contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C1C2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C1C2A three-process runner, large-file, hash, isolation, natural-exit, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C1C2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C1C2A entered global clipboard, synthetic mouse, Explorer automation, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C1C2ARustAbiUnchanged) {
    throw "Stage 5B-4C1C2A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C1C2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C1C2A evidence must reuse the single source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C1C2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C1C2A native-drop sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C1C2AActualWmc1510Count -ne $stage5B4C1C2AExpectedWmc1510Count) {
    throw "Stage 5B-4C1C2A WMC1510 count changed: expected=$stage5B4C1C2AExpectedWmc1510Count actual=$stage5B4C1C2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C2AMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C2AMissingHelperPatterns.Count -gt 0 -or
    $stage5B4C2AMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C2A registration, dispatch, rollback, or reserved-hook lifecycle contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C2A two-process, isolation, fingerprint, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C2A claimed physical input evidence or entered new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C2ARustAbiUnchanged) {
    throw "Stage 5B-4C2A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C2A evidence must use one source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C2A hotkey sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C2AActualWmc1510Count -ne $stage5B4C2AExpectedWmc1510Count) {
    throw "Stage 5B-4C2A WMC1510 count changed: expected=$stage5B4C2AExpectedWmc1510Count actual=$stage5B4C2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3AMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3AMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3A deterministic candidate, snooze, recurrence, restore, or cleanup contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3A five-process, isolation, continuity, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3A entered real system notification or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C3ARustAbiUnchanged) {
    throw "Stage 5B-4C3A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C3A evidence must use one source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C3ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3A Todo recurrence/reminder sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3AActualWmc1510Count -ne $stage5B4C3AExpectedWmc1510Count) {
    throw "Stage 5B-4C3A WMC1510 count changed: expected=$stage5B4C3AExpectedWmc1510Count actual=$stage5B4C3AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3B1MissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3B1MissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B1 notification registration, payload, display, history, or exact cleanup contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B1 three-process, real-display, isolation, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3B1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3B1 entered activation, broad notification deletion, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C3B1RustAbiUnchanged) {
    throw "Stage 5B-4C3B1 changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3B1JsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C3B1 evidence must use one source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C3B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3B1 Todo notification sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3B1ActualWmc1510Count -ne $stage5B4C3B1ExpectedWmc1510Count) {
    throw "Stage 5B-4C3B1 WMC1510 count changed: expected=$stage5B4C3B1ExpectedWmc1510Count actual=$stage5B4C3B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3B2AMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3B2AMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2A activation grammar, routing, mutation, rejection, or persistence contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3B2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2A three-process, isolation, continuity, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3B2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2A entered external notification activation, broad notification deletion, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C3B2ARustAbiUnchanged) {
    throw "Stage 5B-4C3B2A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3B2AJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C3B2A evidence must use one source-generated JSON call. See '$summaryPath'."
}

if ($stage5B4C3B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3B2A Todo activation sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3B2AActualWmc1510Count -ne $stage5B4C3B2AExpectedWmc1510Count) {
    throw "Stage 5B-4C3B2A WMC1510 count changed: expected=$stage5B4C3B2AExpectedWmc1510Count actual=$stage5B4C3B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3B2B1MissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3B2B1MissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B1 typed envelope, startup drain, UserInput, or single-instance contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3B2B1MissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B1 five-process, isolation, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3B2B1ForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B1 entered real Windows notification display/activation, broad deletion, legacy argument-only forwarding, or new Rust ABI scope. See '$summaryPath'."
}

if (-not $stage5B4C3B2B1RustAbiUnchanged) {
    throw "Stage 5B-4C3B2B1 changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3B2B1ScenarioJsonSerializeCallCount -ne 1 -or
    $stage5B4C3B2B1StoreJsonCallCount -ne 2) {
    throw "Stage 5B-4C3B2B1 must retain one fixture JSON call and two source-generated envelope-store JSON calls. See '$summaryPath'."
}

if ($stage5B4C3B2B1SourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3B2B1 forwarding sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3B2B1ActualWmc1510Count -ne $stage5B4C3B2B1ExpectedWmc1510Count) {
    throw "Stage 5B-4C3B2B1 WMC1510 count changed: expected=$stage5B4C3B2B1ExpectedWmc1510Count actual=$stage5B4C3B2B1ActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3B2B2AMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3B2B2AMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2A Todo target, content-ready, or visible-refresh contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3B2B2AMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2A isolated surface, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3B2B2AForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2A mislabeled controlled input as a real Windows click, entered broad notification deletion, or expanded Rust scope. See '$summaryPath'."
}

if (-not $stage5B4C3B2B2ARustAbiUnchanged) {
    throw "Stage 5B-4C3B2B2A changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3B2B2AScenarioJsonSerializeCallCount -ne 0 -or
    $stage5B4C3B2B2AManagedUiJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C3B2B2A must reuse the one source-generated managed UI evidence serializer without adding JSON calls. See '$summaryPath'."
}

if ($stage5B4C3B2B2ASourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3B2B2A Todo surface sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3B2B2AActualWmc1510Count -ne $stage5B4C3B2B2AExpectedWmc1510Count) {
    throw "Stage 5B-4C3B2B2A WMC1510 count changed: expected=$stage5B4C3B2B2AExpectedWmc1510Count actual=$stage5B4C3B2B2AActualWmc1510Count. See '$summaryPath'."
}

if ($stage5B4C3B2B2BMissingScenarioPatterns.Count -gt 0 -or
    $stage5B4C3B2B2BMissingProductPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2B Windows activation provenance, real click, Todo route, or visible-surface contracts are incomplete. See '$summaryPath'."
}

if ($stage5B4C3B2B2BMissingSmokeScriptPatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2B interactive running/cold-start, isolation, provenance, natural-exit, archive, or cleanup gates are missing. See '$summaryPath'."
}

if ($stage5B4C3B2B2BForbiddenScopePatterns.Count -gt 0) {
    throw "Stage 5B-4C3B2B2B used synthetic input/UI Automation, direct fixture notification APIs, broad notification deletion, or expanded Rust scope. See '$summaryPath'."
}

if (-not $stage5B4C3B2B2BRustAbiUnchanged) {
    throw "Stage 5B-4C3B2B2B changed the frozen Rust ABI 2 / capability 511 / ten-export surface. See '$summaryPath'."
}

if ($stage5B4C3B2B2BScenarioJsonSerializeCallCount -ne 0 -or
    $stage5B4C3B2B2BManagedUiJsonSerializeCallCount -ne 1) {
    throw "Stage 5B-4C3B2B2B must reuse the one source-generated managed UI evidence serializer without adding JSON calls. See '$summaryPath'."
}

if ($stage5B4C3B2B2BSourceWarningMessages.Count -gt 0) {
    throw "Stage 5B-4C3B2B2B notification-click sources produced AOT warnings. See '$summaryPath'."
}

if ($stage5B4C3B2B2BActualWmc1510Count -ne $stage5B4C3B2B2BExpectedWmc1510Count) {
    throw "Stage 5B-4C3B2B2B WMC1510 count changed: expected=$stage5B4C3B2B2BExpectedWmc1510Count actual=$stage5B4C3B2B2BActualWmc1510Count. See '$summaryPath'."
}

if ($unexpectedWarningCodes.Count -gt 0) {
    throw "The Stage 5B-4C3B2B2B AOT warning set expanded: $($unexpectedWarningCodes -join ', '). See '$summaryPath'."
}

if ($shortcutAlwaysThrowMessages.Count -gt 0) {
    throw "Legacy shortcut COM constructors remain reachable in Native AOT. See '$summaryPath'."
}

if ($musicVolumeAlwaysThrowMessages.Count -gt 0) {
    throw "Legacy music-volume COM constructors remain reachable in Native AOT. See '$summaryPath'."
}

if ($explorerShellAlwaysThrowMessages.Count -gt 0) {
    throw "Legacy Explorer-shell dynamic COM remains reachable in Native AOT. See '$summaryPath'."
}

if ($quickAccessAlwaysThrowMessages.Count -gt 0) {
    throw "Legacy Quick Access dynamic COM remains reachable in Native AOT. See '$summaryPath'."
}

if ($missingExpectedAlwaysThrowTypes.Count -gt 0 -or
    $unexpectedAlwaysThrowMessages.Count -gt 0) {
    throw "The Stage 5B-4A remaining always-throw contract changed. See '$summaryPath'."
}

if ($RequireCleanAnalysis.IsPresent -and
    ($warningCodes.Count -gt 0 -or $alwaysThrowMessages.Count -gt 0)) {
    throw "AOT publish passed structural validation, but analysis is not clean. See '$summaryPath'."
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
