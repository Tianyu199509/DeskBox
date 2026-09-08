[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "DeterministicStateMatrix"
$smokeEnvironmentVariable =
    "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE"
$phaseEnvironmentVariable =
    "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_PHASE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_RUN_ID"
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
    ".artifacts\aot-todo-recurrence-reminder-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-recurrence-reminder-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoRecurrenceReminderSmoke.v1"
$phases = @(
    "SeedAndSnooze",
    "SnoozeAndComplete",
    "NextOccurrence",
    "Restore",
    "Postflight")

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
    throw "Todo recurrence/reminder smoke timed out without terminal evidence."
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned Todo recurrence/reminder root '$resolvedRoot'."
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
        throw "The Todo recurrence/reminder ownership marker does not match '$resolvedRoot'."
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

function Assert-RequiredSteps {
    param([string]$Phase, [object]$Result)
    $common = @(
        "fixture-settings-configured",
        "runtime-native-aot",
        "no-system-notification")
    $phaseSteps = switch ($Phase) {
        "SeedAndSnooze" {
            @(
                "seed-baseline-empty",
                "initial-due-candidates-exact",
                "reminder-controls-skipped",
                "recurring-snooze-persisted",
                "snooze-before-deadline-suppressed",
                "snooze-state-durable")
        }
        "SnoozeAndComplete" {
            @(
                "seeded-state-reloaded",
                "restart-before-snooze-suppressed",
                "snooze-deadline-fired-once",
                "snooze-repeat-suppressed",
                "snooze-trigger-state-persisted",
                "recurring-completed",
                "next-occurrence-generated",
                "next-occurrence-state-reset")
        }
        "NextOccurrence" {
            @(
                "completed-state-reloaded",
                "next-reminder-before-deadline-suppressed",
                "next-reminder-fired-once",
                "next-reminder-repeat-suppressed",
                "next-reminder-dismissal-persisted")
        }
        "Restore" {
            @(
                "next-dismissal-reloaded",
                "restart-dismissal-persisted",
                "store-cleared")
        }
        "Postflight" {
            @(
                "cleanup-restart-empty",
                "cleanup-postflight-empty")
        }
    }

    foreach ($step in @($common + $phaseSteps)) {
        if (@($Result.steps | Where-Object { [string]$_ -ceq $step }).Count -ne 1) {
            throw "Todo phase '$Phase' is missing required step '$step'."
        }
    }
}

function Invoke-TodoRecurrenceReminderPhase {
    param(
        [ValidateSet("SeedAndSnooze", "SnoozeAndComplete", "NextOccurrence", "Restore", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = $Phase.ToLowerInvariant()
    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-recurrence-reminder-smoke\$phaseDirectory\result.json"
    if (Test-Path -LiteralPath $resultPath) {
        throw "Todo phase result already exists: '$resultPath'."
    }

    $variables = @(
        $smokeEnvironmentVariable,
        $phaseEnvironmentVariable,
        $runIdEnvironmentVariable,
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
        throw "Todo phase '$Phase' did not use the audited executable and preview root."
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Todo phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Todo phase '$Phase' failed: $($result.error)"
    }

    $expectedFixtureRoot = Join-Path `
        $DataRoot `
        "aot-todo-recurrence-reminder-fixture"
    if (-not [bool]$result.success -or
        [string]$result.stage -cne "5B-4C3A" -or
        [string]$result.scenario -cne $scenario -or
        [string]$result.phase -cne $Phase -or
        [string]$result.runId -cne $runId -or
        [int]$result.processId -ne [int]$session.primaryProcessId -or
        [bool]$result.isDynamicCodeSupported -or
        [string]$result.notificationChannel -cne "CapturedCallbackOnly" -or
        [bool]$result.systemNotificationAttempted -or
        -not [bool]$result.normalShutdownRequested -or
        -not (Test-PathEqual `
            -Left ([string]$result.previewDataRoot) `
            -Right $DataRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$result.fixtureRoot) `
            -Right $expectedFixtureRoot)) {
        throw "Todo phase '$Phase' returned inconsistent structured evidence."
    }
    Assert-RequiredSteps -Phase $Phase -Result $result

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf(
                    "Unhandled exception:",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Native notification shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Tray notification fallback shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                ($_.IndexOf(
                    "[AotTodoRecurrenceReminderSmoke] Phase ",
                    [StringComparison]::Ordinal) -ge 0 -and
                    $_.IndexOf(
                        " failed:",
                        [StringComparison]::Ordinal) -ge 0)
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains Todo recurrence/reminder smoke failures: $($failureLines -join ' | ')"
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
    throw "Todo recurrence/reminder smoke requires a successful profile 56 / schema 53 audit."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "todo-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "todo-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "todo-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned Todo recurrence/reminder preview or archive root."
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
        $phaseRuns.Add((Invoke-TodoRecurrenceReminderPhase `
            -Phase $phase `
            -ExecutablePath $previewExecutablePath))
    }

    $processIdsDistinct = @($phaseRuns.processId | Sort-Object -Unique).Count -eq 5
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
        throw "The five-process Todo matrix did not keep distinct PIDs and one audited executable hash."
    }

    for ($index = 0; $index -lt ($phaseRuns.Count - 1); $index++) {
        $left = $phaseRuns[$index]
        $right = $phaseRuns[$index + 1]
        if ([string]$left.result.after.storeSha256 -cne
                [string]$right.result.before.storeSha256 -or
            [long]$left.result.after.storeLength -ne
                [long]$right.result.before.storeLength) {
            throw "Todo store continuity failed between '$($left.phase)' and '$($right.phase)'."
        }
    }

    $seed = $phaseRuns[0].result
    $complete = $phaseRuns[1].result
    $next = $phaseRuns[2].result
    $restore = $phaseRuns[3].result
    $postflight = $phaseRuns[4].result
    if ((@($seed.checkCounts) -join ',') -cne '2,0' -or
        -not [bool]$seed.snoozeSucceeded -or
        @($seed.after.items).Count -ne 5 -or
        (@($complete.checkCounts) -join ',') -cne '0,1,0' -or
        -not [bool]$complete.completeSucceeded -or
        @($complete.after.items).Count -ne 6 -or
        (@($next.checkCounts) -join ',') -cne '0,1,0' -or
        @($next.after.items).Count -ne 6 -or
        (@($restore.checkCounts) -join ',') -cne '0' -or
        -not [bool]$restore.storeCleared -or
        @($restore.after.items).Count -ne 0 -or
        (@($postflight.checkCounts) -join ',') -cne '0' -or
        @($postflight.after.items).Count -ne 0) {
        throw "The deterministic Todo candidate, snooze, recurrence, restore, or postflight counts changed."
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production DeskBox data changed during the isolated Todo recurrence/reminder smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    foreach ($phaseRun in $phaseRuns) {
        $phaseArchive = Join-Path `
            $archiveRoot `
            $phaseRun.phase.ToLowerInvariant()
        New-Item -ItemType Directory -Path $phaseArchive -Force | Out-Null
        Copy-Item `
            -LiteralPath ([string]$phaseRun.resultPath) `
            -Destination (Join-Path $phaseArchive "result.json")
    }
    Copy-Item `
        -LiteralPath $settingsPath `
        -Destination (Join-Path $archiveRoot "preview-settings.json")
    Copy-Item `
        -LiteralPath (Join-Path $DataRoot "DeskBox.log") `
        -Destination (Join-Path $archiveRoot "DeskBox.log")
    Copy-Item `
        -LiteralPath (Join-Path `
            $DataRoot `
            "aot-todo-recurrence-reminder-fixture") `
        -Destination (Join-Path $archiveRoot "fixture") `
        -Recurse

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    if (-not $previewRootCleaned) {
        throw "Owned Todo recurrence/reminder preview root was not removed."
    }

    $session = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C3A"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        auditProfileVersion = 62
        auditSummarySchemaVersion = 51
        dataRoot = $DataRoot
        previewRootCleaned = $previewRootCleaned
        archiveRoot = $archiveRoot
        processIdsDistinct = $processIdsDistinct
        executableHashesMatch = $executableHashesMatch
        executablePath = $previewExecutablePath
        executableSha256 = $executableHashes[0]
        notificationChannel = "CapturedCallbackOnly"
        systemNotificationAttempted = $false
        phases = @(
            $phaseRuns | ForEach-Object {
                [ordered]@{
                    phase = $_.phase
                    processId = $_.processId
                    resultPath = Join-Path `
                        $archiveRoot `
                        "$($_.phase.ToLowerInvariant())\result.json"
                    naturalExit = $_.naturalExit
                    checkCounts = @($_.result.checkCounts)
                }
            })
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
            "Todo recurrence/reminder smoke failed; owned evidence was preserved at '$DataRoot'.")
    }
}
