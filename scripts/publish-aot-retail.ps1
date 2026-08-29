[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [string]$DotNetPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts\aot-retail"))
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$expectedMachine = if ($Platform -eq "ARM64") { 0xAA64 } else { 0x8664 }
$runRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot $runtimeIdentifier))
$buildArtifactsDir = Join-Path $runRoot "build"
$publishDir = Join-Path $runRoot "publish"
$symbolsDir = Join-Path $runRoot "symbols"
$rustIntermediateDir = Join-Path $runRoot "rust-staging"
$rustCargoTargetDir = Join-Path $runRoot "rust-target"
$logPath = Join-Path $runRoot "publish.log"
$summaryPath = Join-Path $runRoot "summary.json"
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$updaterProject = Join-Path $repoRoot "src\DeskBox.Updater\DeskBox.Updater.csproj"
$toolchainScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

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
        throw "Refusing to modify an AOT retail path outside '$normalizedRoot': '$normalizedCandidate'."
    }
}

function Get-TextSha256 {
    param([AllowEmptyString()][string]$Value)

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
    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve the repository commit."
    }

    $status = (& git -C $repoRoot status --porcelain=v1 --untracked-files=all) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to capture the repository status."
    }

    $diff = (& git -C $repoRoot diff --no-ext-diff --binary HEAD) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to capture the repository diff."
    }

    [pscustomobject]@{
        GitCommit = $commit
        GitDirty = -not [string]::IsNullOrWhiteSpace($status)
        Fingerprint = Get-TextSha256 -Value ($status + "`n" + $diff)
        StatusEntries = @($status -split "`n" | Where-Object { $_ })
    }
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

function Find-BinaryTextTokens {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Tokens
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
    @(
        $Tokens |
            Where-Object {
                $ascii.IndexOf($_, [System.StringComparison]::Ordinal) -ge 0 -or
                $unicode.IndexOf($_, [System.StringComparison]::Ordinal) -ge 0
            } |
            Sort-Object -Unique
    )
}

if (-not (Test-Path -LiteralPath $toolchainScript -PathType Leaf)) {
    throw "The explicit MSVC environment helper is missing: '$toolchainScript'."
}
. $toolchainScript

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
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$sourceSnapshotBefore = Get-WorkingTreeSnapshot

Assert-PathInsideRoot -Root $artifactRoot -Candidate $runRoot
if (Test-Path -LiteralPath $runRoot -PathType Container) {
    Remove-Item -LiteralPath $runRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $symbolsDir -Force | Out-Null

$commonProperties = @(
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:DeskBoxDistribution=Direct",
    "-p:DeskBoxAotAudit=true",
    "-p:DeskBoxAotSmokeHarness=false",
    "-p:PublishAot=true",
    "-p:DeskBoxRustNative=true",
    "-p:DeskBoxRustCrtLinkage=Static",
    "-p:JsonSerializerIsReflectionEnabledByDefault=false",
    "-p:IlcUseEnvironmentalTools=true",
    "-p:SelfContained=true",
    "-p:WindowsAppSDKSelfContained=true"
)

$previousCliLanguage = [Environment]::GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "Process")
$previousNoLogo = [Environment]::GetEnvironmentVariable("DOTNET_NOLOGO", "Process")
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US", "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", "1", "Process")

    foreach ($restoreProject in @($project, $updaterProject)) {
        $restoreArguments = @(
            "restore",
            $restoreProject,
            "--artifacts-path", $buildArtifactsDir,
            "-v:minimal"
        ) + $commonProperties
        & $dotnet @restoreArguments 2>&1 | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) {
            throw "AOT retail restore failed for '$restoreProject'. See '$logPath'."
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
        throw "Native AOT retail publish failed. See '$logPath'."
    }
}
finally {
    [Environment]::SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", $previousCliLanguage, "Process")
    [Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", $previousNoLogo, "Process")
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}

$nativeValidation = & (Join-Path $PSScriptRoot "build-rust-native.ps1") `
    -Platform $Platform `
    -Configuration Release `
    -OutputDirectory $publishDir `
    -ValidateOnly
$nativeStagingPath = Join-Path $rustIntermediateDir "deskbox_native.dll"
$nativePublishPath = Join-Path $publishDir "deskbox_native.dll"
foreach ($requiredPath in @(
        $nativeStagingPath,
        $nativePublishPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "AOT retail native payload is missing '$requiredPath'."
    }
}
$nativeStagingSha256 = (Get-FileHash -LiteralPath $nativeStagingPath -Algorithm SHA256).Hash
$nativePublishSha256 = (Get-FileHash -LiteralPath $nativePublishPath -Algorithm SHA256).Hash
if ($nativeStagingSha256 -cne $nativePublishSha256) {
    throw "AOT retail Rust module does not match this run's isolated staging output."
}

$pdbFiles = @(Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -Recurse)
foreach ($pdb in $pdbFiles) {
    Assert-PathInsideRoot -Root $publishDir -Candidate $pdb.FullName
    $relativePath = $pdb.FullName.Substring($publishDir.TrimEnd('\', '/').Length + 1)
    $destination = Join-Path $symbolsDir $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Move-Item -LiteralPath $pdb.FullName -Destination $destination -Force
}

$installManifestName = "DeskBox.InstallManifest.txt"
$installManifestPath = Join-Path $publishDir $installManifestName
$installManifestEntries = [System.Collections.Generic.List[string]]::new()
foreach ($publishedFile in Get-ChildItem -LiteralPath $publishDir -File -Recurse) {
    if ($publishedFile.FullName.Equals(
            $installManifestPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $relativePath = $publishedFile.FullName.Substring(
        $publishDir.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    $installManifestEntries.Add($relativePath)
}
$installManifestEntries.Add($installManifestName)
$installManifestEntries = @($installManifestEntries | Sort-Object -Unique)
[System.IO.File]::WriteAllLines(
    $installManifestPath,
    $installManifestEntries,
    [System.Text.UTF8Encoding]::new($false))

$requiredFiles = @(
    "DeskBox.exe",
    "DeskBox.Updater.exe",
    "DeskBox.ThumbnailProxy.exe",
    "DeskBox.pri",
    "deskbox_native.dll",
    "EverythingSdk.dll",
    "Microsoft.UI.Input.dll",
    "Microsoft.ui.xaml.dll",
    "Microsoft.WindowsAppRuntime.dll",
    $installManifestName,
    "ThirdParty/Everything/LICENSE.txt"
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $requiredFile) -PathType Leaf)) {
        throw "AOT retail output is missing '$requiredFile'."
    }
}

$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsDir -File -Recurse)
foreach ($requiredSymbolFile in @(
        "DeskBox.pdb",
        "DeskBox.Updater.pdb",
        "DeskBox.ThumbnailProxy.pdb",
        "deskbox_native.pdb")) {
    if (-not ($symbolFiles | Where-Object Name -eq $requiredSymbolFile)) {
        throw "AOT retail symbols are missing '$requiredSymbolFile'."
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
$forbiddenFiles = @($publishedFiles | Where-Object { $_.Name -in $forbiddenNames -or $_.Extension -eq ".pdb" })
if ($forbiddenFiles.Count -gt 0) {
    throw "AOT retail output contains forbidden managed/JIT/symbol files: $($forbiddenFiles.Name -join ', ')."
}

$peResults = @(
    foreach ($fileName in @(
            "DeskBox.exe",
            "DeskBox.Updater.exe",
            "DeskBox.ThumbnailProxy.exe",
            "deskbox_native.dll",
            "EverythingSdk.dll")) {
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
        }
    }
)

$smokeHarnessTokens = @(
    "DESKBOX_AOT_SHORTCUT_SMOKE",
    "DESKBOX_AOT_SHELL_SMOKE",
    "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
    "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE",
    "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE",
    "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE",
    "DESKBOX_AOT_MANAGED_UI_SMOKE",
    "DESKBOX_AOT_HOTKEY_SMOKE",
    "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE",
    "DESKBOX_AOT_TODO_NOTIFICATION_SMOKE",
    "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_SMOKE",
    "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_SMOKE",
    "DESKBOX_AOT_TODO_NOTIFICATION_SURFACE_SMOKE",
    "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_SMOKE",
    "AotManagedUiSmokeResult",
    "AotWeatherSurfaceFixture",
    "AotShellMoveFixture",
    "AotFilePropertiesFixture"
)
$smokeHarnessMatches = @(
    Find-BinaryTextTokens `
        -Path (Join-Path $publishDir "DeskBox.exe") `
        -Tokens $smokeHarnessTokens
)
if ($smokeHarnessMatches.Count -gt 0) {
    throw "AOT retail executable still contains smoke-harness tokens: $($smokeHarnessMatches -join ', ')."
}

$warningCodeRegex = [regex]::new(
    "\b(?:IL|CS|MSB|WMC|MVVMTK|CsWinRT|NETSDK|SYSLIB)\d+\b",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$warningCodes = @(
    @(
        foreach ($line in @(Get-Content -LiteralPath $logPath)) {
            foreach ($match in $warningCodeRegex.Matches($line)) {
                $match.Value.ToUpperInvariant()
            }
        }
    ) | Sort-Object -Unique
)
$allowedWarningCodes = @("CS0108", "CS0169", "CS0414", "CS8601", "CS8602", "WMC1510")
$unexpectedWarningCodes = @($warningCodes | Where-Object { $_ -notin $allowedWarningCodes })
if ($unexpectedWarningCodes.Count -gt 0) {
    throw "AOT retail publish produced unexpected warning codes: $($unexpectedWarningCodes -join ', ')."
}

$sourceSnapshotAfter = Get-WorkingTreeSnapshot
$sourceStableDuringPublish =
    $sourceSnapshotBefore.GitCommit -eq $sourceSnapshotAfter.GitCommit -and
    $sourceSnapshotBefore.Fingerprint -eq $sourceSnapshotAfter.Fingerprint
if (-not $sourceStableDuringPublish) {
    throw "The repository changed during the AOT retail publish; the output is not trusted."
}

$stopwatch.Stop()
$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsDir -File -Recurse)
$summary = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    durationMilliseconds = $stopwatch.ElapsedMilliseconds
    productProfile = "retail"
    deploymentProfile = "full"
    smokeHarnessEnabled = $false
    selfContained = $true
    windowsAppSdkSelfContained = $true
    gitCommit = $sourceSnapshotBefore.GitCommit
    gitDirty = $sourceSnapshotBefore.GitDirty
    gitStatusEntries = $sourceSnapshotBefore.StatusEntries
    workingTreeFingerprint = $sourceSnapshotBefore.Fingerprint
    sourceStableDuringPublish = $sourceStableDuringPublish
    dotnetHost = $dotnet
    dotnetSdkVersion = (& $dotnet --version).Trim()
    configuration = "Release"
    platform = $Platform
    runtimeIdentifier = $runtimeIdentifier
    publishDirectory = $publishDir
    symbolsDirectory = $symbolsDir
    installManifest = $installManifestPath
    installManifestFileCount = $installManifestEntries.Count
    publishFileCount = $publishedFiles.Count
    publishBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
    symbolFileCount = $symbolFiles.Count
    symbolBytes = ($symbolFiles | Measure-Object -Property Length -Sum).Sum
    warningCodes = $warningCodes
    allowedWarningCodes = $allowedWarningCodes
    unexpectedWarningCodes = $unexpectedWarningCodes
    smokeHarnessSourceExclusions = @("**\*.Aot*Smoke.cs", "Services\Aot*Fixture.cs")
    smokeHarnessScannedTokens = $smokeHarnessTokens
    smokeHarnessBinaryMatches = $smokeHarnessMatches
    peFiles = $peResults
    rustNative = [ordered]@{
        abiVersion = $nativeValidation.AbiVersion
        capabilities = $nativeValidation.Capabilities
        publishMatchesStaging = $true
        sha256 = $nativePublishSha256
    }
}
[System.IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Summary = $summaryPath
    PublishDirectory = $publishDir
    SymbolsDirectory = $symbolsDir
    RuntimeIdentifier = $runtimeIdentifier
    PublishFiles = $publishedFiles.Count
    PublishMiB = [Math]::Round($summary.publishBytes / 1MB, 1)
    SmokeHarnessEnabled = $false
}
