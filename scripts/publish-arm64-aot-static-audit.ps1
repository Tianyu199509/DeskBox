[CmdletBinding()]
param(
    [string]$DotNetPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$auditStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$auditProfileVersion = 2
$summarySchemaVersion = 1
$platform = "ARM64"
$runtimeIdentifier = "win-arm64"
$targetTriple = "aarch64-pc-windows-msvc"
$expectedMachine = 0xAA64
$processArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$updaterProject = Join-Path $repoRoot "src\DeskBox.Updater\DeskBox.Updater.csproj"
$peContractScript = Join-Path $PSScriptRoot "native-pe-contract.ps1"
$arm64EnvironmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
$artifactRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot ".artifacts\aot-arm64-static-audit"))
$runRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot $runtimeIdentifier))
$buildArtifactsDir = Join-Path $runRoot "build"
$publishDir = Join-Path $runRoot "publish"
$symbolsDir = Join-Path $runRoot "symbols"
$rustIntermediateDir = Join-Path $runRoot "rust-staging"
$rustCargoTargetDir = Join-Path $runRoot "rust-target"
$logPath = Join-Path $runRoot "publish.log"
$summaryPath = Join-Path $runRoot "summary.json"

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
        throw "Refusing to modify an ARM64 audit path outside '$normalizedRoot': '$normalizedCandidate'."
    }
}

function Get-TextSha256 {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
                $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value)))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-WorkingTreeSnapshot {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $gitCommitOutput = @(& git -C $repoRoot rev-parse HEAD 2>$null)
        $gitCommitExitCode = $LASTEXITCODE
        $gitStatusEntries = @(
            & git -C $repoRoot -c core.quotepath=false status --porcelain=v1 --untracked-files=all 2>$null)
        $gitStatusExitCode = $LASTEXITCODE
        $trackedDiff = @(& git -C $repoRoot diff --binary --no-ext-diff HEAD -- 2>$null) -join "`n"
        $gitDiffExitCode = $LASTEXITCODE
        $untrackedFiles = @(
            & git -C $repoRoot -c core.quotepath=false ls-files --others --exclude-standard 2>$null |
                Sort-Object)
        $gitUntrackedExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($gitStatusExitCode -ne 0 -or
        $gitDiffExitCode -ne 0 -or
        $gitUntrackedExitCode -ne 0) {
        throw "Failed to capture the Git working-tree state for the ARM64 audit."
    }

    $untrackedManifest = @(
        foreach ($relativePath in $untrackedFiles) {
            $fullPath = Join-Path $repoRoot $relativePath
            if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
                "$relativePath`t$((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash)"
            }
        }
    ) -join "`n"

    [pscustomobject]@{
        GitCommit = if ($gitCommitExitCode -eq 0) {
            ($gitCommitOutput -join "").Trim()
        }
        else {
            $null
        }
        GitDirty = $gitStatusEntries.Count -gt 0
        GitStatusEntries = $gitStatusEntries
        WorkingTreeFingerprint = Get-TextSha256 -Value (
            $trackedDiff + "`n--UNTRACKED--`n" + $untrackedManifest)
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

function Get-DumpBinPath {
    $command = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vsWhere -PathType Leaf) {
        $candidates = @(
            & $vsWhere -latest -products * -find "VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe" 2>$null)
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return [System.IO.Path]::GetFullPath($candidate)
            }
        }
    }

    throw "Unable to locate x64-hosted dumpbin.exe for the ARM64 dependency inventory."
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

function Get-Arm64ToolchainState {
    $rustc = (Get-Command rustc -ErrorAction Stop).Source
    $cargo = (Get-Command cargo -ErrorAction Stop).Source
    $targetLibDirectory = @(& $rustc --print target-libdir --target $targetTriple 2>$null)

    $missing = [System.Collections.Generic.List[string]]::new()
    if ($targetLibDirectory.Count -ne 1 -or
        -not (Test-Path -LiteralPath $targetLibDirectory[0] -PathType Container)) {
        $missing.Add("rust-std 1.96.0 for $targetTriple")
    }
    $msvcEnvironment = $null
    try {
        $msvcEnvironment = Get-DeskBoxArm64MsvcEnvironment
    }
    catch {
        $missing.Add($_.Exception.Message.TrimEnd('.'))
    }
    if ($missing.Count -gt 0) {
        throw "ARM64 static audit prerequisites are missing: $($missing -join '; ')."
    }

    [pscustomobject]@{
        RustcPath = $rustc
        RustcVersion = (& $rustc --version)
        CargoPath = $cargo
        CargoVersion = (& $cargo --version)
        TargetTriple = $targetTriple
        TargetLibDirectory = [System.IO.Path]::GetFullPath($targetLibDirectory[0])
        LinkerPath = $msvcEnvironment.LinkerPath
        MsvcLibraryPath = $msvcEnvironment.VcLibraryDirectory
        WindowsSdkVersion = $msvcEnvironment.WindowsSdkVersion
        WindowsSdkPath = $msvcEnvironment.WindowsSdkLibRoot
        MsvcEnvironment = $msvcEnvironment
    }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf) -or
    -not (Test-Path -LiteralPath $updaterProject -PathType Leaf) -or
    -not (Test-Path -LiteralPath $peContractScript -PathType Leaf) -or
    -not (Test-Path -LiteralPath $arm64EnvironmentScript -PathType Leaf)) {
    throw "ARM64 static audit source inputs are incomplete."
}

. $peContractScript
. $arm64EnvironmentScript
$toolchain = Get-Arm64ToolchainState
$dotnet = if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    (Get-Command dotnet -ErrorAction Stop).Source
}
else {
    $candidate = [System.IO.Path]::GetFullPath($DotNetPath)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The explicitly selected dotnet host does not exist: '$candidate'."
    }
    $candidate
}
$dotnetSdkVersion = (& $dotnet --version).Trim()
$dumpBinPath = Get-DumpBinPath

Assert-PathInsideRoot -Root $artifactRoot -Candidate $runRoot
$sourceSnapshotBefore = Get-WorkingTreeSnapshot
if (Test-Path -LiteralPath $runRoot -PathType Container) {
    Remove-Item -LiteralPath $runRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $symbolsDir -Force | Out-Null

$commonProperties = @(
    "-p:Platform=$platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:DeskBoxDistribution=Direct",
    "-p:DeskBoxAotAudit=true",
    "-p:DeskBoxAotSmokeHarness=true",
    "-p:PublishAot=true",
    "-p:DeskBoxRustNative=true",
    "-p:JsonSerializerIsReflectionEnabledByDefault=false",
    "-p:IlcUseEnvironmentalTools=true",
    "-p:SelfContained=true",
    "-p:WindowsAppSDKSelfContained=false"
)

$previousCliLanguage = [Environment]::GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "Process")
$previousNoLogo = [Environment]::GetEnvironmentVariable("DOTNET_NOLOGO", "Process")
$arm64EnvironmentState =
    Enter-DeskBoxArm64MsvcEnvironment -Toolchain $toolchain.MsvcEnvironment
try {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US", "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", "1", "Process")
    # Discover every project directly under src/ so newly added assemblies
    # (pluginization Step 1+) are restored automatically instead of requiring
    # manual edits to a hardcoded project array.
    $restoreProjects = @(
        Get-ChildItem -Path (Join-Path $repoRoot "src") -Directory |
            ForEach-Object { Get-ChildItem -Path $_.FullName -Filter "*.csproj" -File } |
            Sort-Object Name |
            ForEach-Object { $_.FullName })

    foreach ($restoreProject in $restoreProjects) {
        $arguments = @(
            "restore",
            $restoreProject,
            "--artifacts-path", $buildArtifactsDir,
            "-v:minimal"
        ) + $commonProperties
        & $dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) {
            throw "ARM64 restore failed for '$restoreProject'. See '$logPath'."
        }
    }

    $publishArguments = @(
        "publish",
        $project,
        "--configuration", "Release",
        "--output", $publishDir,
        "--artifacts-path", $buildArtifactsDir,
        "--no-restore",
        "-p:DeskBoxRustNativeIntermediateDir=$rustIntermediateDir",
        "-p:DeskBoxRustNativeCargoTargetDir=$rustCargoTargetDir",
        "-p:PublishSingleFile=false",
        "-v:minimal"
    ) + $commonProperties
    & $dotnet @publishArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "ARM64 Native AOT publish failed. See '$logPath'."
    }
}
finally {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", $previousCliLanguage, "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", $previousNoLogo, "Process")
    Exit-DeskBoxArm64MsvcEnvironment -State $arm64EnvironmentState
}

$nativeValidation = & (Join-Path $PSScriptRoot "build-rust-native.ps1") `
    -Platform ARM64 `
    -Configuration Release `
    -OutputDirectory $rustIntermediateDir `
    -ValidateOnly
$runtimeAbiProbeExecuted = $nativeValidation.RuntimeProbeExecuted
if ($processArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
    if (-not $runtimeAbiProbeExecuted) {
        throw "A native ARM64 audit must execute the target Rust ABI probe."
    }
    $evidenceLevel = "native-arm64-runtime-plus-static"
}
elseif ($processArchitecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
    if ($runtimeAbiProbeExecuted) {
        throw "An ARM64 cross-audit must not execute a target DLL on the x64 host."
    }
    $evidenceLevel = "cross-compiled-static-only"
}
else {
    throw "The ARM64 audit supports only native ARM64 or x64 cross-build hosts; actual=$processArchitecture."
}

$requiredFiles = @(
    "DeskBox.exe",
    "DeskBox.Updater.exe",
    "DeskBox.ThumbnailProxy.exe",
    "DeskBox.pri",
    "deskbox_native.dll"
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $requiredFile) -PathType Leaf)) {
        throw "ARM64 AOT output is missing '$requiredFile'."
    }
}

foreach ($moduleName in @(
        "DeskBox.ThumbnailProxy.exe",
        "deskbox_native.dll")) {
    $matches = @(Get-ChildItem -LiteralPath $publishDir -Filter $moduleName -File -Recurse)
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $publishDir $moduleName))
    if ($matches.Count -ne 1 -or
        -not [string]::Equals(
            $matches[0].FullName,
            $expectedPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ARM64 AOT output must contain exactly one root-level '$moduleName'."
    }
}

$nativeStagingSha256 =
    (Get-FileHash -LiteralPath (Join-Path $rustIntermediateDir "deskbox_native.dll") -Algorithm SHA256).Hash
$nativePublishSha256 =
    (Get-FileHash -LiteralPath (Join-Path $publishDir "deskbox_native.dll") -Algorithm SHA256).Hash
if ($nativeStagingSha256 -cne $nativePublishSha256) {
    throw "ARM64 published Rust module does not match this run's isolated staging output."
}

$pdbFiles = @(Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -Recurse)
foreach ($pdb in $pdbFiles) {
    Assert-PathInsideRoot -Root $publishDir -Candidate $pdb.FullName
    $normalizedPublishDir = [System.IO.Path]::GetFullPath($publishDir).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedPdbPath = [System.IO.Path]::GetFullPath($pdb.FullName)
    $relativePath = $normalizedPdbPath.Substring($normalizedPublishDir.Length + 1)
    $destination = Join-Path $symbolsDir $relativePath
    $destinationParent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Move-Item -LiteralPath $pdb.FullName -Destination $destination -Force
}
$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsDir -Filter "*.pdb" -File -Recurse)
foreach ($requiredSymbol in @(
        "DeskBox.pdb",
        "DeskBox.Updater.pdb",
        "DeskBox.ThumbnailProxy.pdb",
        "deskbox_native.pdb")) {
    if (-not ($symbolFiles | Where-Object Name -eq $requiredSymbol)) {
        throw "ARM64 symbols are missing '$requiredSymbol'."
    }
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
$forbiddenNames = @(
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
$forbiddenMatches = @($publishedFiles | Where-Object Name -in $forbiddenNames)
if ($forbiddenMatches.Count -gt 0) {
    throw "ARM64 AOT output contains managed/JIT files: $($forbiddenMatches.Name -join ', ')."
}
if (@($publishedFiles | Where-Object Extension -eq ".pdb").Count -gt 0) {
    throw "ARM64 publish directory still contains PDB files after symbol separation."
}

$peResults = @(
    foreach ($fileName in @(
            "DeskBox.exe",
            "DeskBox.Updater.exe",
            "DeskBox.ThumbnailProxy.exe",
            "deskbox_native.dll")) {
        $path = Join-Path $publishDir $fileName
        $machine = Get-PeMachine -Path $path
        if ($machine -ne $expectedMachine) {
            throw "Unexpected PE machine for '$fileName': 0x$($machine.ToString('X4'))."
        }
        [ordered]@{
            file = $fileName
            machine = "0x$($machine.ToString('X4'))"
            bytes = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            imports = @(Get-PeImports -Path $path -DumpBinPath $dumpBinPath)
        }
    }
)

$warningCodeRegex = [regex]::new(
    "\b(?:IL|CS|MSB|WMC|MVVMTK|CsWinRT|NETSDK|SYSLIB)\d+\b",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$warningMatches = @(
    foreach ($line in @(Get-Content -LiteralPath $logPath)) {
        foreach ($match in $warningCodeRegex.Matches($line)) {
            $match.Value
        }
    }
)
$warningCodes = @($warningMatches | Sort-Object -Unique)
$allowedWarningCodes = @("CS0108", "CS0169", "CS0414", "CS8601", "CS8602", "WMC1510")
$unexpectedWarningCodes = @($warningCodes | Where-Object { $_ -notin $allowedWarningCodes })
if ($unexpectedWarningCodes.Count -gt 0) {
    throw "ARM64 AOT publish produced unexpected warning codes: $($unexpectedWarningCodes -join ', ')."
}

$sourceSnapshotAfter = Get-WorkingTreeSnapshot
$sourceStableDuringAudit =
    [string]::Equals(
        $sourceSnapshotBefore.GitCommit,
        $sourceSnapshotAfter.GitCommit,
        [System.StringComparison]::Ordinal) -and
    [string]::Equals(
        $sourceSnapshotBefore.WorkingTreeFingerprint,
        $sourceSnapshotAfter.WorkingTreeFingerprint,
        [System.StringComparison]::Ordinal)

$auditStopwatch.Stop()
$summary = [ordered]@{
    schemaVersion = $summarySchemaVersion
    auditProfileVersion = $auditProfileVersion
    productProfile = "smoke-audit"
    smokeHarnessEnabled = $true
    evidenceLevel = $evidenceLevel
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    durationMilliseconds = $auditStopwatch.ElapsedMilliseconds
    gitCommit = $sourceSnapshotBefore.GitCommit
    gitDirty = $sourceSnapshotBefore.GitDirty
    workingTreeFingerprintBefore = $sourceSnapshotBefore.WorkingTreeFingerprint
    workingTreeFingerprintAfter = $sourceSnapshotAfter.WorkingTreeFingerprint
    sourceStableDuringAudit = $sourceStableDuringAudit
    dotnetHost = $dotnet
    dotnetSdkVersion = $dotnetSdkVersion
    configuration = "Release"
    platform = $platform
    runtimeIdentifier = $runtimeIdentifier
    targetDeviceExecuted = $false
    physicalUserDeviceExecuted = $false
    processArchitecture = $processArchitecture.ToString()
    runtimeAbiProbeExecuted = $runtimeAbiProbeExecuted
    publishDirectory = $publishDir
    symbolsDirectory = $symbolsDir
    publishFileCount = $publishedFiles.Count
    publishBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
    symbolFileCount = $symbolFiles.Count
    symbolBytes = ($symbolFiles | Measure-Object -Property Length -Sum).Sum
    warningCodes = $warningCodes
    allowedWarningCodes = $allowedWarningCodes
    unexpectedWarningCodes = $unexpectedWarningCodes
    toolchain = [ordered]@{
        rustcPath = $toolchain.RustcPath
        rustcVersion = $toolchain.RustcVersion
        cargoPath = $toolchain.CargoPath
        cargoVersion = $toolchain.CargoVersion
        targetTriple = $toolchain.TargetTriple
        targetLibDirectory = $toolchain.TargetLibDirectory
        linkerPath = $toolchain.LinkerPath
        msvcLibraryPath = $toolchain.MsvcLibraryPath
        windowsSdkVersion = $toolchain.WindowsSdkVersion
        windowsSdkPath = $toolchain.WindowsSdkPath
        ilcUseEnvironmentalTools = $true
    }
    peFiles = $peResults
    rustNative = [ordered]@{
        abiVersion = $nativeValidation.AbiVersion
        capabilities = $nativeValidation.Capabilities
        requiredExports = @($nativeValidation.RequiredExports)
        machine = $nativeValidation.MachineHex
        contractValidation = $nativeValidation.ContractValidation
        stagingSha256 = $nativeStagingSha256
        publishSha256 = $nativePublishSha256
        publishMatchesStaging = $true
    }
}

[System.IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

if (-not $sourceStableDuringAudit) {
    throw "The repository changed during the ARM64 static audit; '$summaryPath' is not trusted."
}

[pscustomobject]@{
    Summary = $summaryPath
    PublishDirectory = $publishDir
    SymbolsDirectory = $symbolsDir
    RuntimeIdentifier = $runtimeIdentifier
    PublishFiles = $publishedFiles.Count
    PublishMiB = [Math]::Round(
        (($publishedFiles | Measure-Object -Property Length -Sum).Sum / 1MB),
        1)
    WarningCodes = $warningCodes -join ", "
    NativeMachine = $nativeValidation.MachineHex
    TargetDeviceExecuted = $false
}
