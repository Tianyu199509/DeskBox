[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "RegistrationLifecycle"
$smokeEnvironmentVariable = "DESKBOX_AOT_HOTKEY_SMOKE"
$phaseEnvironmentVariable = "DESKBOX_AOT_HOTKEY_PHASE"
$runIdEnvironmentVariable = "DESKBOX_AOT_HOTKEY_RUN_ID"
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
$evidenceRoot = Join-Path $repoRoot ".artifacts\aot-hotkey-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-hotkey-owned.json"
$ownedMarkerKind = "DeskBox.Aot.HotkeySmoke.v1"

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
    throw "Hotkey smoke timed out without terminal evidence."
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned hotkey root '$resolvedRoot'."
    }

    $markerPath = Join-Path $resolvedRoot $ownedMarkerName
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ([string]$marker.kind -cne $ownedMarkerKind -or
        [string]$marker.runId -cne $runId -or
        -not (Test-PathEqual `
            -Left ([string]$marker.repositoryRoot) `
            -Right $repoRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$marker.dataRoot) `
            -Right $resolvedRoot)) {
        throw "The hotkey ownership marker does not match '$resolvedRoot'."
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

function Invoke-HotkeyPhase {
    param(
        [ValidateSet("Primary", "Release")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = $Phase.ToLowerInvariant()
    $resultPath = Join-Path `
        $DataRoot `
        "aot-hotkey-smoke\$phaseDirectory\result.json"
    if (Test-Path -LiteralPath $resultPath) {
        throw "Hotkey phase result already exists: '$resultPath'."
    }

    $variables = @(
        $smokeEnvironmentVariable,
        $phaseEnvironmentVariable,
        $runIdEnvironmentVariable,
        "DESKBOX_AOT_MANAGED_UI_SMOKE",
        "DESKBOX_AOT_SHORTCUT_SMOKE",
        "DESKBOX_AOT_SHELL_SMOKE",
        "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
        "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE",
        "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE",
        "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE")
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
        throw "Hotkey phase '$Phase' did not use the audited executable and preview root."
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Hotkey phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Hotkey phase '$Phase' failed: $($result.error)"
    }
    if (-not [bool]$result.success -or
        [string]$result.stage -cne "5B-4C2A" -or
        [string]$result.scenario -cne $scenario -or
        [string]$result.phase -cne $Phase -or
        [string]$result.runId -cne $runId -or
        [int]$result.processId -ne [int]$session.primaryProcessId -or
        [string]$result.inputSource -cne
            "SyntheticSendInputForRegisterHotKeyOnly" -or
        [bool]$result.isDynamicCodeSupported -or
        [bool]$result.physicalStandardKeyboardVerified -or
        [bool]$result.physicalWinSpaceVerified -or
        [bool]$result.physicalRecorderVerified -or
        [bool]$result.reservedHookSyntheticTriggerAttempted -or
        -not [bool]$result.normalShutdownRequested) {
        throw "Hotkey phase '$Phase' returned inconsistent structured evidence."
    }
    if (-not [bool]$result.globalStandardRegistered -or
        [bool]$result.globalStandardUsesReservedHook -or
        [long]$result.globalReceivedDelta -ne 1 -or
        [long]$result.globalInvocationDelta -ne 1 -or
        [long]$result.globalDispatchFailureDelta -ne 0 -or
        -not [bool]$result.searchStandardRegistered -or
        [long]$result.searchReceivedDelta -ne 1 -or
        [long]$result.searchInvocationDelta -ne 1 -or
        [long]$result.searchDispatchFailureDelta -ne 0 -or
        -not [bool]$result.globalConflictRolledBack -or
        -not [bool]$result.globalConflictHolderReleased -or
        -not [bool]$result.searchConflictRolledBack -or
        -not [bool]$result.searchConflictHolderReleased -or
        -not [bool]$result.reservedHookUsesHook -or
        [uint32]$result.reservedHookThreadId -eq 0 -or
        [int]$result.reservedHookLastError -ne 0 -or
        -not [bool]$result.reservedHookStopped -or
        -not [bool]$result.finalGlobalRegistered -or
        -not [bool]$result.finalSearchRegistered) {
        throw "Hotkey phase '$Phase' did not complete the registration lifecycle matrix."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf(
                    "Unhandled exception:",
                    [StringComparison]::Ordinal) -ge 0 -or
                ($_.IndexOf(
                    "[AotHotkeySmoke] Phase ",
                    [StringComparison]::Ordinal) -ge 0 -and
                    $_.IndexOf(
                        " failed:",
                        [StringComparison]::Ordinal) -ge 0)
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains hotkey smoke failures: $($failureLines -join ' | ')"
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
    [int]$auditSummary.rustNative.capabilities -ne 511) {
    throw "Hotkey smoke requires a successful profile 56 / schema 53 audit."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "hotkey-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "hotkey-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "hotkey-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned hotkey preview or archive root."
}

$dataDirectory = Join-Path $DataRoot "data"
$settingsPath = Join-Path $dataDirectory "settings.json"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    dataRoot = $DataRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $DataRoot $ownedMarkerName) `
    -Encoding UTF8

$settings = [ordered]@{
    schemaVersion = 5
    language = "zh-CN"
    autoStart = $false
    autoCheckForUpdates = $false
    globalHotkeyEnabled = $false
    globalHotkeyModifiers = 6
    globalHotkeyKey = 134
    searchHotkeyEnabled = $false
    searchHotkeyModifiers = 3
    searchHotkeyKey = 135
    trayIconStyle = "Colorful"
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
    searchShowRecommendations = $false
    widgets = @(
        [ordered]@{
            id = "aot-5b4c2a-search"
            name = "AOT Hotkey Fixture"
            isDefaultTitle = $false
            x = 100
            y = 100
            boundsCoordinateVersion = 1
            width = 320
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

$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$runSucceeded = $false
$previewRootCleaned = $false
$primary = $null
$release = $null

try {
    $primary = Invoke-HotkeyPhase `
        -Phase "Primary" `
        -ExecutablePath $previewExecutablePath
    $release = Invoke-HotkeyPhase `
        -Phase "Release" `
        -ExecutablePath $previewExecutablePath

    $processIdsDistinct = $primary.processId -ne $release.processId
    $executableHashesMatch =
        [string]$primary.executableSha256 -ceq [string]$release.executableSha256 -and
        [string]$primary.executableSha256 -ceq
            [string]$primary.result.executableSha256 -and
        [string]$release.executableSha256 -ceq
            [string]$release.result.executableSha256
    if (-not $processIdsDistinct -or -not $executableHashesMatch -or
        [bool]$primary.result.startupGlobalEnabled -or
        [bool]$primary.result.startupGlobalRegistered -or
        [bool]$primary.result.startupSearchEnabled -or
        [bool]$primary.result.startupSearchRegistered -or
        -not [bool]$release.result.startupGlobalEnabled -or
        -not [bool]$release.result.startupGlobalRegistered -or
        -not [bool]$release.result.startupSearchEnabled -or
        -not [bool]$release.result.startupSearchRegistered) {
        throw "Two-process hotkey registration release evidence is inconsistent."
    }

    $persistedSettings = Get-Content `
        -LiteralPath $settingsPath `
        -Raw | ConvertFrom-Json
    if (-not [bool]$persistedSettings.globalHotkeyEnabled -or
        [int]$persistedSettings.globalHotkeyModifiers -ne 6 -or
        [int]$persistedSettings.globalHotkeyKey -ne 134 -or
        -not [bool]$persistedSettings.searchHotkeyEnabled -or
        [int]$persistedSettings.searchHotkeyModifiers -ne 3 -or
        [int]$persistedSettings.searchHotkeyKey -ne 135) {
        throw "Final hotkey settings do not match the committed standard gestures."
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production DeskBox data changed during the isolated hotkey smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    foreach ($phase in @($primary, $release)) {
        $phaseArchive = Join-Path $archiveRoot $phase.phase.ToLowerInvariant()
        New-Item -ItemType Directory -Path $phaseArchive -Force | Out-Null
        Copy-Item `
            -LiteralPath ([string]$phase.resultPath) `
            -Destination (Join-Path $phaseArchive "result.json")
    }
    Copy-Item `
        -LiteralPath $settingsPath `
        -Destination (Join-Path $archiveRoot "settings.json")
    Copy-Item `
        -LiteralPath (Join-Path $DataRoot "DeskBox.log") `
        -Destination (Join-Path $archiveRoot "DeskBox.log")

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    if (-not $previewRootCleaned) {
        throw "Owned hotkey preview root was not removed."
    }

    $session = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C2A"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        auditProfileVersion = 60
        auditSummarySchemaVersion = 51
        dataRoot = $DataRoot
        previewRootCleaned = $previewRootCleaned
        archiveRoot = $archiveRoot
        processIdsDistinct = $processIdsDistinct
        executableHashesMatch = $executableHashesMatch
        executablePath = $previewExecutablePath
        executableSha256 = [string]$primary.executableSha256
        phases = @(
            [ordered]@{
                phase = $primary.phase
                processId = $primary.processId
                resultPath = Join-Path $archiveRoot "primary\result.json"
                naturalExit = $primary.naturalExit
            },
            [ordered]@{
                phase = $release.phase
                processId = $release.processId
                resultPath = Join-Path $archiveRoot "release\result.json"
                naturalExit = $release.naturalExit
            })
        inputSource = "SyntheticSendInputForRegisterHotKeyOnly"
        physicalStandardKeyboardVerified = $false
        physicalWinSpaceVerified = $false
        physicalRecorderVerified = $false
        reservedHookSyntheticTriggerAttempted = $false
        productionDataFingerprintBefore = $productionBefore
        productionDataFingerprintAfter = $productionAfter
    }
    $sessionJson = $session | ConvertTo-Json -Depth 12
    $sessionJson | Set-Content `
        -LiteralPath (Join-Path $archiveRoot "session.json") `
        -Encoding UTF8
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    $sessionJson | Set-Content -LiteralPath $latestSessionPath -Encoding UTF8
    $runSucceeded = $true
    [PSCustomObject]$session
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($previewExecutablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    }
    if (-not $runSucceeded) {
        Write-Warning (
            "Hotkey smoke failed; owned evidence was preserved at '$DataRoot'.")
    }
}
