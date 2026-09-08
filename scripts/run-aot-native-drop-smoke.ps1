[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(60, 300)]
    [int]$TimeoutSeconds = 210
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "NativeDropPersistenceRestart"
$smokeEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SMOKE"
$phaseEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE"
$runIdEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_RUN_ID"
$runId = [Guid]::NewGuid().ToString("N")
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcher = Join-Path $PSScriptRoot "start-aot-preview.ps1"
$previewSessionPath = Join-Path `
    $repoRoot `
    ".artifacts\aot-preview\win-x64\session.json"
$auditSummaryPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $repoRoot ".artifacts\aot-audit\win-x64\summary.json"
}
else {
    [System.IO.Path]::GetFullPath($SummaryPath)
}
$evidenceRoot = Join-Path `
    $repoRoot `
    ".artifacts\aot-managed-ui-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-managed-ui-owned.json"
$ownedMarkerKind = "DeskBox.Aot.NativeDropSmoke.v1"
$largeFileLength = 384MB

function Test-PathEqual {
    param([string]$Left, [string]$Right)
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathEqualOrInside {
    param([string]$Root, [string]$Candidate)
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedCandidate =
        [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    return (Test-PathEqual -Left $normalizedRoot -Right $normalizedCandidate) -or
        $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-TextSha256 {
    param([AllowEmptyString()][string]$Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha.ComputeHash(
                [System.Text.Encoding]::UTF8.GetBytes($Value))).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DirectoryStateFingerprint {
    param([string]$Path)
    $normalizedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $normalizedPath -PathType Container)) {
        return [PSCustomObject]@{
            exists = $false
            fileCount = 0
            bytes = 0L
            fingerprint = Get-TextSha256 -Value "<missing>"
        }
    }

    $files = @(Get-ChildItem -LiteralPath $normalizedPath -File -Recurse -Force)
    $records = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring(
            $normalizedPath.Length).TrimStart('\', '/')
        $records.Add(("{0}|{1}|{2}" -f @(
            $relativePath.Replace('\', '/').ToUpperInvariant(),
            $file.Length,
            $file.LastWriteTimeUtc.Ticks)))
    }
    $records.Sort([System.StringComparer]::Ordinal)
    return [PSCustomObject]@{
        exists = $true
        fileCount = $files.Count
        bytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        fingerprint = Get-TextSha256 -Value ([string]::Join("`n", $records))
    }
}

function Get-ExactPreviewProcesses {
    param([string]$ExecutablePath)
    return @(
        Get-CimInstance Win32_Process -Filter "Name='DeskBox.exe'" |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace(
                    [string]$_.ExecutablePath) -and
                (Test-PathEqual `
                    -Left $_.ExecutablePath `
                    -Right $ExecutablePath)
            })
}

function Stop-ExactPreviewProcess {
    param([string]$ExecutablePath)
    foreach ($process in @(
            Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process `
            -Id $process.ProcessId `
            -Timeout 5 `
            -ErrorAction SilentlyContinue
    }
}

function Wait-NaturalPreviewExit {
    param([string]$ExecutablePath, [int]$Seconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(
                Get-ExactPreviewProcesses `
                    -ExecutablePath $ExecutablePath).Count -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return @(
        Get-ExactPreviewProcesses `
            -ExecutablePath $ExecutablePath).Count -eq 0
}

function Read-JsonRetry {
    param([string]$Path)
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Wait-TerminalResult {
    param([string]$ResultPath, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            $candidate = Read-JsonRetry -Path $ResultPath
            if ($null -ne $candidate -and
                [string]$candidate.state -in @("Completed", "Failed")) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Native-drop smoke timed out without terminal evidence."
}

function New-LargeOwnedFile {
    param([string]$Path, [long]$Length, [string]$Token)
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Token)
        $stream.SetLength($Length)
        $stream.Position = 0
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Position = $Length - $bytes.Length
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-OwnedRootAndRemove {
    param([string]$Root, [string]$ExpectedProperty)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned native-drop root '$resolvedRoot'."
    }
    $markerPath = Join-Path $resolvedRoot $ownedMarkerName
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ([string]$marker.kind -cne $ownedMarkerKind -or
        [string]$marker.runId -cne $runId -or
        -not (Test-PathEqual `
            -Left ([string]$marker.repositoryRoot) `
            -Right $repoRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$marker.$ExpectedProperty) `
            -Right $resolvedRoot)) {
        throw "The native-drop ownership marker does not match '$resolvedRoot'."
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

function Invoke-NativeDropPhase {
    param(
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = switch ($Phase) {
        "Mutate" { "mutate" }
        "VerifyRestore" { "verify-restore" }
        default { "postflight" }
    }
    $resultPath = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\native-drop-persistence-restart\$phaseDirectory\result.json"
    if (Test-Path -LiteralPath $resultPath) {
        Remove-Item -LiteralPath $resultPath -Force
    }

    $variables = @(
        "DESKBOX_AOT_MANAGED_UI_SMOKE",
        "DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_TODO_PHASE",
        "DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE",
        "DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE",
        "DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE",
        "DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE",
        "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE",
        "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_RUN_ID",
        "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE",
        "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_RUN_ID",
        "DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID",
        "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE",
        "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_RUN_ID",
        $phaseEnvironmentVariable,
        $runIdEnvironmentVariable,
        "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE",
        "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE",
        "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE",
        "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
        "DESKBOX_AOT_SHELL_SMOKE",
        "DESKBOX_AOT_SHORTCUT_SMOKE")
    $previous = @{}
    foreach ($variable in $variables) {
        $previous[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }
    try {
        foreach ($variable in $variables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $smokeEnvironmentVariable,
            $scenario,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $phaseEnvironmentVariable,
            $Phase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $runIdEnvironmentVariable,
            $runId,
            "Process")
        $null = @(
            & $launcher `
                -SummaryPath $auditSummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $variables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previous[$variable],
                "Process")
        }
    }

    $session = Get-Content `
        -LiteralPath $previewSessionPath `
        -Raw | ConvertFrom-Json
    $phaseExecutablePath = [string]$session.executablePath
    if (-not (Test-PathEqual -Left $phaseExecutablePath -Right $ExecutablePath) -or
        -not (Test-PathEqual `
            -Left ([string]$session.previewDataRoot) `
            -Right $DataRoot)) {
        throw "Phase '$Phase' did not use the audited executable and preview root."
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Native-drop phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Native-drop phase '$Phase' failed: $($result.error)"
    }
    if (-not [bool]$result.success -or
        [string]$result.scenario -cne $scenario -or
        [string]$result.nativeDrop.phase -cne $Phase -or
        [string]$result.nativeDrop.runId -cne $runId -or
        [int]$result.processId -ne [int]$session.primaryProcessId -or
        [string]$result.nativeDrop.sourceKind -cne
            "ProgrammaticGeneratedCcwHDrop" -or
        [bool]$result.nativeDrop.physicalExplorerMouseVerified -or
        -not [bool]$result.nativeDrop.flushSucceeded) {
        throw "Native-drop phase '$Phase' returned inconsistent structured evidence."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf(
                    "Unhandled exception:",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[AotManagedUiSmoke] Failed:",
                    [StringComparison]::Ordinal) -ge 0
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains failures: $($failureLines -join ' | ')"
    }

    return [PSCustomObject]@{
        phase = $Phase
        processId = [int]$session.primaryProcessId
        executablePath = $phaseExecutablePath
        executableSha256 = [string]$session.executableSha256
        resultPath = $resultPath
        runtimeLogPath = $runtimeLogPath
        naturalExit = $naturalExit
        result = $result
    }
}

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Native AOT preview launcher was not found: '$launcher'."
}
if (-not (Test-Path -LiteralPath $auditSummaryPath -PathType Leaf)) {
    throw "Native AOT audit summary was not found: '$auditSummaryPath'."
}
$auditSummary = Get-Content `
    -LiteralPath $auditSummaryPath `
    -Raw | ConvertFrom-Json
if ([int]$auditSummary.auditProfileVersion -ne 54 -or
    [int]$auditSummary.schemaVersion -ne 51 -or
    -not [bool]$auditSummary.sourceStableDuringAudit -or
    [string]$auditSummary.configuration -cne "Release" -or
    [string]$auditSummary.platform -cne "x64" -or
    [string]$auditSummary.runtimeIdentifier -cne "win-x64" -or
    @($auditSummary.warningCodes | Where-Object { $_ -ceq "WMC1506" }).Count -ne 0 -or
    [int]$auditSummary.warningCodeCounts.WMC1510 -ne 1213 -or
    @($auditSummary.alwaysThrowMessages).Count -ne 0 -or
    [int]$auditSummary.rustNative.abiVersion -ne 2 -or
    [int]$auditSummary.rustNative.capabilities -ne 1023) {
    throw "Native-drop smoke requires a successful profile 56 / schema 53 audit."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "native-drop-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$recoveryRoot = [System.IO.Path]::GetFullPath($DataRoot + "-Recovery")
$archiveRoot = Join-Path $evidenceRoot "native-drop-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "native-drop-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $recoveryRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $recoveryRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $recoveryRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned native-drop preview, recovery, or archive root."
}

$fixtureRoot = Join-Path $DataRoot "fixtures\native-drop\$runId"
$widgetRoot = Join-Path $fixtureRoot "widget-root"
$sourceRoot = Join-Path $fixtureRoot "sources"
$baselineFile = Join-Path $widgetRoot "baseline.txt"
$targetFolder = Join-Path $widgetRoot "target-folder"
$copyLargeSourceFile = Join-Path $sourceRoot "copy-large.bin"
$copySourceFolder = Join-Path $sourceRoot "copy-folder"
$copySourceNestedFile = Join-Path $copySourceFolder "payload.txt"
$moveSourceFile = Join-Path $sourceRoot "move-small.txt"
$moveSourceFolder = Join-Path $sourceRoot "move-folder"
$moveSourceNestedFile = Join-Path $moveSourceFolder "payload.txt"
$copyDestinationFile = Join-Path $widgetRoot "copy-large.bin"
$copyDestinationFolder = Join-Path $widgetRoot "copy-folder"
$copyDestinationNestedFile = Join-Path $copyDestinationFolder "payload.txt"
$moveDestinationFile = Join-Path $widgetRoot "move-small.txt"
$moveDestinationFolder = Join-Path $widgetRoot "move-folder"
$moveDestinationNestedFile = Join-Path $moveDestinationFolder "payload.txt"
$dataDirectory = Join-Path $DataRoot "data"
$settingsPath = Join-Path $dataDirectory "settings.json"

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
New-Item -ItemType Directory -Path $copySourceFolder -Force | Out-Null
New-Item -ItemType Directory -Path $moveSourceFolder -Force | Out-Null
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    dataRoot = $DataRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $DataRoot $ownedMarkerName) `
    -Encoding UTF8
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    recoveryRoot = $recoveryRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $recoveryRoot $ownedMarkerName) `
    -Encoding UTF8
Set-Content `
    -LiteralPath $baselineFile `
    -Value "native-drop-baseline-$runId" `
    -NoNewline `
    -Encoding UTF8
New-LargeOwnedFile `
    -Path $copyLargeSourceFile `
    -Length $largeFileLength `
    -Token "native-drop-large-$runId"
Set-Content `
    -LiteralPath $copySourceNestedFile `
    -Value "native-drop-copy-folder-$runId" `
    -NoNewline `
    -Encoding UTF8
Set-Content `
    -LiteralPath $moveSourceFile `
    -Value "native-drop-move-file-$runId" `
    -NoNewline `
    -Encoding UTF8
Set-Content `
    -LiteralPath $moveSourceNestedFile `
    -Value "native-drop-move-folder-$runId" `
    -NoNewline `
    -Encoding UTF8

$settings = [ordered]@{
    schemaVersion = 5
    language = "zh-CN"
    managedDropAction = "Copy"
    autoStart = $false
    autoCheckForUpdates = $false
    globalHotkeyEnabled = $false
    trayIconStyle = "Colorful"
    textSize = 11.5
    fileNameLineCount = 2
    showFileExtensions = $false
    capsuleModeEnabled = $false
    hasCompletedOnboarding = $true
    completedOnboardingVersion = 1
    hasResolvedInitialFileWidgetSetup = $true
    featureWidgetEnabledStates = [ordered]@{
        QuickCapture = $false
        Todo = $false
        Music = $false
        Weather = $false
        Search = $true
        Glance = $false
    }
    searchSaveHistory = $false
    searchDefaultTab = "all"
    searchShowRecommendations = $false
    widgets = @(
        [ordered]@{
            id = "aot-5b4c1c2a-file"
            name = "AOT Native Drop Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 380
            height = 460
            widgetKind = "File"
            viewMode = "Icon"
            isVisible = $true
            isDisabled = $false
            isPositionLocked = $false
            isSizeLocked = $false
            isCollapsed = $false
            mappedFolderPath = $widgetRoot
            followsDefaultStoragePath = $false
            sortMode = "Name"
            items = @()
            metadata = [ordered]@{ FolderOpenBehavior = "Embedded" }
            fileAddedAtByPath = [ordered]@{}
            fileAddedAtTrackingInitialized = $true
        },
        [ordered]@{
            id = "aot-5b4a-search"
            name = "AOT Search Fixture"
            isDefaultTitle = $false
            x = 500
            y = 80
            boundsCoordinateVersion = 1
            width = 300
            height = 360
            widgetKind = "Search"
            viewMode = "Icon"
            isVisible = $true
            isDisabled = $false
            isPositionLocked = $false
            isSizeLocked = $false
            isCollapsed = $false
            followsDefaultStoragePath = $false
            sortMode = "Name"
            items = @()
            metadata = [ordered]@{}
            fileAddedAtByPath = [ordered]@{}
            fileAddedAtTrackingInitialized = $true
        })
}
$settings | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8

$initialHashes = [ordered]@{
    baseline = (Get-FileHash -LiteralPath $baselineFile -Algorithm SHA256).Hash
    copyLarge = (Get-FileHash -LiteralPath $copyLargeSourceFile -Algorithm SHA256).Hash
    copyNested = (Get-FileHash -LiteralPath $copySourceNestedFile -Algorithm SHA256).Hash
    moveFile = (Get-FileHash -LiteralPath $moveSourceFile -Algorithm SHA256).Hash
    moveNested = (Get-FileHash -LiteralPath $moveSourceNestedFile -Algorithm SHA256).Hash
}
$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$runSucceeded = $false
$previewRootCleaned = $false
$recoveryRootCleaned = $false

try {
    $mutate = Invoke-NativeDropPhase `
        -Phase "Mutate" `
        -ExecutablePath $previewExecutablePath
    if (-not (Test-Path -LiteralPath $copyLargeSourceFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $copySourceFolder -PathType Container) -or
        -not (Test-Path -LiteralPath $copyDestinationFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $copyDestinationNestedFile -PathType Leaf) -or
        (Test-Path -LiteralPath $moveSourceFile) -or
        (Test-Path -LiteralPath $moveSourceFolder) -or
        -not (Test-Path -LiteralPath $moveDestinationFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $moveDestinationNestedFile -PathType Leaf)) {
        throw "Mutate phase did not realize exact native copy and move semantics."
    }
    if ((Get-FileHash -LiteralPath $copyDestinationFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.copyLarge -or
        (Get-FileHash -LiteralPath $copyDestinationNestedFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.copyNested -or
        (Get-FileHash -LiteralPath $moveDestinationFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.moveFile -or
        (Get-FileHash -LiteralPath $moveDestinationNestedFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.moveNested) {
        throw "Native-drop destination content hashes do not match their sources."
    }
    if (-not [bool]$mutate.result.nativeDrop.copyImport.duringImport.cardVisible -or
        -not [bool]$mutate.result.nativeDrop.copyImport.duringImport.backgroundIsAcrylicBrush -or
        [int]$mutate.result.nativeDrop.copyImport.duringImport.canvasZIndex -lt 1000 -or
        [double]$mutate.result.nativeDrop.copyImport.duringImport.translationZ -lt 64 -or
        [bool]$mutate.result.nativeDrop.copyImport.immediatelyAfterCallback.isImportBusy -or
        [bool]$mutate.result.nativeDrop.nativePointerClear.highlightActiveAfter -or
        [bool]$mutate.result.nativeDrop.nativeLeaveClear.highlightActiveAfter) {
        throw "Mutate phase did not prove progress layering or stale-highlight cleanup."
    }

    $verifyRestore = Invoke-NativeDropPhase `
        -Phase "VerifyRestore" `
        -ExecutablePath $previewExecutablePath
    if (-not (Test-Path -LiteralPath $moveSourceFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $moveSourceNestedFile -PathType Leaf) -or
        (Test-Path -LiteralPath $copyDestinationFile) -or
        (Test-Path -LiteralPath $copyDestinationFolder) -or
        (Test-Path -LiteralPath $moveDestinationFile) -or
        (Test-Path -LiteralPath $moveDestinationFolder) -or
        (Get-FileHash -LiteralPath $moveSourceFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.moveFile -or
        (Get-FileHash -LiteralPath $moveSourceNestedFile -Algorithm SHA256).Hash -cne
            [string]$initialHashes.moveNested) {
        throw "VerifyRestore did not return the owned native-drop fixture to baseline."
    }

    $postflight = Invoke-NativeDropPhase `
        -Phase "Postflight" `
        -ExecutablePath $previewExecutablePath
    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production data changed during the native-drop smoke."
    }

    $phases = @($mutate, $verifyRestore, $postflight)
    if (@($phases.processId | Select-Object -Unique).Count -ne 3 -or
        @($phases.executableSha256 | Select-Object -Unique).Count -ne 1 -or
        @($phases | Where-Object { -not $_.naturalExit }).Count -ne 0) {
        throw "Native-drop phases did not use three natural exits and one executable hash."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    foreach ($phase in $phases) {
        $phaseArchive = Join-Path $archiveRoot (
            ([string]$phase.phase).ToLowerInvariant())
        New-Item -ItemType Directory -Path $phaseArchive -Force | Out-Null
        Copy-Item `
            -LiteralPath ([string]$phase.resultPath) `
            -Destination (Join-Path $phaseArchive "result.json")
        Copy-Item `
            -LiteralPath ([string]$phase.runtimeLogPath) `
            -Destination (Join-Path $phaseArchive "DeskBox.log")
    }
    Copy-Item `
        -LiteralPath $settingsPath `
        -Destination (Join-Path $archiveRoot "settings.json")

    Assert-OwnedRootAndRemove -Root $DataRoot -ExpectedProperty "dataRoot"
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    Assert-OwnedRootAndRemove `
        -Root $recoveryRoot `
        -ExpectedProperty "recoveryRoot"
    $recoveryRootCleaned = -not (Test-Path -LiteralPath $recoveryRoot)
    $runSucceeded = $true

    $summary = [ordered]@{
        schemaVersion = 1
        scenario = $scenario
        runId = $runId
        sourceKind = "ProgrammaticGeneratedCcwHDrop"
        physicalExplorerMouseVerified = $false
        executablePath = $previewExecutablePath
        executableSha256 = [string]$mutate.executableSha256
        processIds = @($phases.processId)
        phases = @($phases | ForEach-Object {
            [ordered]@{
                phase = $_.phase
                processId = $_.processId
                naturalExit = $_.naturalExit
                resultPath = $_.resultPath
            }
        })
        nativePointerClearVerified =
            -not [bool]$mutate.result.nativeDrop.nativePointerClear.highlightActiveAfter
        nativeLeaveClearVerified =
            -not [bool]$mutate.result.nativeDrop.nativeLeaveClear.highlightActiveAfter
        copyMoveVerified = $true
        progressCard = $mutate.result.nativeDrop.copyImport.duringImport
        callbackReleasedBeforeImport =
            -not [bool]$mutate.result.nativeDrop.copyImport.immediatelyAfterCallback.isImportBusy
        productionDataFingerprintBefore = $productionBefore
        productionDataFingerprintAfter = $productionAfter
        previewRootCleaned = $previewRootCleaned
        recoveryRootCleaned = $recoveryRootCleaned
        archiveRoot = $archiveRoot
        completedAtUtc = [DateTimeOffset]::UtcNow
    }
    $summary | ConvertTo-Json -Depth 24 |
        Set-Content -LiteralPath $latestSessionPath -Encoding UTF8
    $summary | ConvertTo-Json -Depth 24
}
finally {
    Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    if (-not $runSucceeded) {
        Write-Warning (
            "Native-drop smoke did not complete; owned preview roots were " +
            "preserved for audit: '$DataRoot', '$recoveryRoot'.")
    }
}
