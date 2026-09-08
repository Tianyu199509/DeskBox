[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "RealDisplayAndCleanup"
$smokeEnvironmentVariable = "DESKBOX_AOT_TODO_NOTIFICATION_SMOKE"
$phaseEnvironmentVariable = "DESKBOX_AOT_TODO_NOTIFICATION_PHASE"
$runIdEnvironmentVariable = "DESKBOX_AOT_TODO_NOTIFICATION_RUN_ID"
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
    ".artifacts\aot-todo-notification-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-notification-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoNotificationSmoke.v1"
$phases = @("ShowAndInspect", "Cleanup", "Postflight")

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
    throw "Todo notification smoke timed out without terminal evidence."
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned Todo notification root '$resolvedRoot'."
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
        throw "The Todo notification ownership marker does not match '$resolvedRoot'."
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

function Assert-RequiredSteps {
    param([string]$Phase, [object]$Result)
    $common = @(
        "native-notification-service-created",
        "native-notification-registered",
        "system-notifications-enabled",
        "runtime-native-aot",
        "native-notification-unregistered")
    $phaseSteps = switch ($Phase) {
        "ShowAndInspect" {
            @(
                "owned-history-empty-before-show",
                "single-notification-show-returned-success",
                "aggregate-notification-show-returned-success",
                "notification-center-shows-two-owned-items",
                "single-payload-actions-and-snooze-options-exact",
                "aggregate-payload-has-no-actions",
                "real-system-notification-display-proved")
        }
        "Cleanup" {
            @(
                "cross-process-history-reloaded",
                "single-tag-group-cleanup-exact",
                "aggregate-tag-group-cleanup-exact",
                "cleanup-process-did-not-display")
        }
        "Postflight" {
            @(
                "new-process-postflight-empty",
                "postflight-process-did-not-display")
        }
    }

    foreach ($step in @($common + $phaseSteps)) {
        if (@($Result.steps | Where-Object { [string]$_ -ceq $step }).Count -ne 1) {
            throw "Todo notification phase '$Phase' is missing required step '$step'."
        }
    }
}

function Invoke-TodoNotificationPhase {
    param(
        [ValidateSet("ShowAndInspect", "Cleanup", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = $Phase.ToLowerInvariant()
    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-notification-smoke\$phaseDirectory\result.json"
    if (Test-Path -LiteralPath $resultPath) {
        throw "Todo notification phase result already exists: '$resultPath'."
    }

    $variables = @(
        $smokeEnvironmentVariable,
        $phaseEnvironmentVariable,
        $runIdEnvironmentVariable,
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE",
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_PHASE",
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_RUN_ID",
        "DESKBOX_AOT_MANAGED_UI_SMOKE",
        "DESKBOX_AOT_HOTKEY_SMOKE",
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
        throw "Todo notification phase '$Phase' did not use the audited executable and preview root."
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Todo notification phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Todo notification phase '$Phase' failed: $($result.error)"
    }

    $expectedGroup = "db-c3b1-$runId"
    $expectedSingleTag = "single-$runId"
    $expectedAggregateTag = "aggregate-$runId"
    if (-not [bool]$result.success -or
        [string]$result.stage -cne "5B-4C3B1" -or
        [string]$result.scenario -cne $scenario -or
        [string]$result.phase -cne $Phase -or
        [string]$result.runId -cne $runId -or
        [int]$result.processId -ne [int]$session.primaryProcessId -or
        [bool]$result.isDynamicCodeSupported -or
        [string]$result.notificationSetting -cne "Enabled" -or
        -not [bool]$result.registeredAtStart -or
        -not [bool]$result.unregisterSucceeded -or
        [bool]$result.registeredAfterUnregister -or
        -not [bool]$result.normalShutdownRequested -or
        [string]$result.group -cne $expectedGroup -or
        [string]$result.singleTag -cne $expectedSingleTag -or
        [string]$result.aggregateTag -cne $expectedAggregateTag -or
        -not (Test-PathEqual `
            -Left ([string]$result.previewDataRoot) `
            -Right $DataRoot)) {
        throw "Todo notification phase '$Phase' returned inconsistent structured evidence."
    }
    Assert-RequiredSteps -Phase $Phase -Result $result

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("Native app notification registration failed", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("Native app notification show failed", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("Native app notification unregister failed", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[TodoReminder] Tray notification fallback shown", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[Notification] Native notification activated", [StringComparison]::Ordinal) -ge 0 -or
                ($_.IndexOf("[AotTodoNotificationSmoke] Phase ", [StringComparison]::Ordinal) -ge 0 -and
                    $_.IndexOf(" failed:", [StringComparison]::Ordinal) -ge 0)
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains Todo notification smoke failures: $($failureLines -join ' | ')"
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
    throw "Todo notification smoke requires a successful profile 56 / schema 53 audit."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "notification-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "notification-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "notification-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned Todo notification preview or archive root."
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
    searchHotkeyEnabled = $false
    todoReminderEnabled = $false
    trayIconStyle = "Colorful"
    hasCompletedOnboarding = $true
    completedOnboardingVersion = 1
    hasResolvedInitialFileWidgetSetup = $true
    featureWidgetEnabledStates = [ordered]@{
        QuickCapture = $false
        Todo = $false
        Music = $false
        Weather = $false
        Search = $false
        Glance = $false
    }
    widgets = @()
}
$settings | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8

$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$runSucceeded = $false
$previewRootCleaned = $false
$phaseRuns = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($phase in $phases) {
        $phaseRuns.Add((Invoke-TodoNotificationPhase `
            -Phase $phase `
            -ExecutablePath $previewExecutablePath))
    }

    $processIdsDistinct = @($phaseRuns.processId | Sort-Object -Unique).Count -eq 3
    $executableHashes = @(
        $phaseRuns |
            ForEach-Object {
                @(
                    [string]$_.executableSha256,
                    [string]$_.result.executableSha256)
            } |
            Sort-Object -Unique)
    $executableHashesMatch = $executableHashes.Count -eq 1
    if (-not $processIdsDistinct -or -not $executableHashesMatch) {
        throw "The three-process Todo notification matrix did not keep distinct PIDs and one audited executable hash."
    }

    $show = $phaseRuns[0].result
    $cleanup = $phaseRuns[1].result
    $postflight = $phaseRuns[2].result
    if (-not [bool]$show.systemNotificationAttempted -or
        -not [bool]$show.singleShowSucceeded -or
        -not [bool]$show.aggregateShowSucceeded -or
        [int]$show.notificationCountBefore -ne 0 -or
        [int]$show.notificationCountAfter -ne 2 -or
        @($show.notifications).Count -ne 2 -or
        [bool]$cleanup.systemNotificationAttempted -or
        [int]$cleanup.notificationCountBefore -ne 2 -or
        [int]$cleanup.notificationCountAfter -ne 0 -or
        -not [bool]$cleanup.singleCleanupSucceeded -or
        -not [bool]$cleanup.aggregateCleanupSucceeded -or
        [bool]$postflight.systemNotificationAttempted -or
        [int]$postflight.notificationCountBefore -ne 0 -or
        [int]$postflight.notificationCountAfter -ne 0) {
        throw "The Todo notification display, persistence, exact cleanup, or postflight counts are inconsistent."
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionBefore.fingerprint -cne
            [string]$productionAfter.fingerprint -or
        [int]$productionBefore.fileCount -ne
            [int]$productionAfter.fileCount -or
        [long]$productionBefore.bytes -ne [long]$productionAfter.bytes) {
        throw "Formal DeskBox data changed during the Todo notification smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $DataRoot "aot-todo-notification-smoke") `
        -Destination (Join-Path $archiveRoot "aot-todo-notification-smoke") `
        -Recurse
    Copy-Item -LiteralPath $settingsPath -Destination $archiveRoot
    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    if (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf) {
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archiveRoot
    }

    $sessionSummary = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C3B1"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        auditProfileVersion = [int]$auditSummary.auditProfileVersion
        auditSummarySchemaVersion = [int]$auditSummary.schemaVersion
        dataRoot = $DataRoot
        previewRootCleaned = $true
        archiveRoot = $archiveRoot
        processIdsDistinct = $processIdsDistinct
        executableHashesMatch = $executableHashesMatch
        executablePath = $previewExecutablePath
        executableSha256 = $executableHashes[0]
        realSystemNotificationsShown = 2
        exactTagGroupCleanup = $true
        activationObserved = $false
        phases = @(
            $phaseRuns | ForEach-Object {
                [ordered]@{
                    phase = $_.phase
                    processId = $_.processId
                    resultPath = Join-Path `
                        $archiveRoot `
                        ("aot-todo-notification-smoke\{0}\result.json" -f
                            $_.phase.ToLowerInvariant())
                    naturalExit = $_.naturalExit
                    notificationCountBefore = [int]$_.result.notificationCountBefore
                    notificationCountAfter = [int]$_.result.notificationCountAfter
                }
            })
        productionDataFingerprintBefore = $productionBefore
        productionDataFingerprintAfter = $productionAfter
    }
    $sessionJson = $sessionSummary | ConvertTo-Json -Depth 16
    $sessionJson | Set-Content `
        -LiteralPath (Join-Path $archiveRoot "session.json") `
        -Encoding UTF8
    $sessionJson | Set-Content -LiteralPath $latestSessionPath -Encoding UTF8
    $runSucceeded = $true
}
finally {
    if (-not $runSucceeded -and
        (Test-Path -LiteralPath $DataRoot -PathType Container)) {
        $showResultPath = Join-Path `
            $DataRoot `
            "aot-todo-notification-smoke\showandinspect\result.json"
        $cleanupResultPath = Join-Path `
            $DataRoot `
            "aot-todo-notification-smoke\cleanup\result.json"
        $showResult = if (Test-Path -LiteralPath $showResultPath -PathType Leaf) {
            Read-JsonRetry -Path $showResultPath
        }
        else {
            $null
        }
        if ($null -ne $showResult -and
            [bool]$showResult.systemNotificationAttempted -and
            -not [bool]$showResult.compensationSucceeded -and
            -not (Test-Path -LiteralPath $cleanupResultPath -PathType Leaf)) {
            try {
                $null = Invoke-TodoNotificationPhase `
                    -Phase "Cleanup" `
                    -ExecutablePath $previewExecutablePath
            }
            catch {
                Write-Warning (
                    "Compensating Todo notification cleanup did not pass all " +
                    "gates: $($_.Exception.Message)")
            }
        }
    }
    Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    if (Test-Path -LiteralPath $DataRoot -PathType Container) {
        if (-not (Test-Path -LiteralPath $archiveRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
        }
        $failureResultRoot = Join-Path $DataRoot "aot-todo-notification-smoke"
        if (-not $runSucceeded -and
            (Test-Path -LiteralPath $failureResultRoot -PathType Container)) {
            Copy-Item `
                -LiteralPath $failureResultRoot `
                -Destination (Join-Path $archiveRoot "failed-aot-todo-notification-smoke") `
                -Recurse `
                -Force
        }
        Assert-OwnedRootAndRemove -Root $DataRoot
        $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    }
}

if (-not $runSucceeded -or -not $previewRootCleaned) {
    throw "Todo notification smoke did not complete with owned preview cleanup."
}

Get-Content -LiteralPath $latestSessionPath -Raw | ConvertFrom-Json
