[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "DeterministicActionRouting"
$smokeEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_SMOKE"
$phaseEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_PHASE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_RUN_ID"
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
    ".artifacts\aot-todo-notification-activation-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-notification-activation-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoNotificationActivationSmoke.v1"
$phases = @("RouteAndPersist", "VerifyAndClear", "Postflight")

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
    throw "Todo notification activation smoke timed out without terminal evidence."
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned Todo notification activation root '$resolvedRoot'."
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
        throw "The Todo notification activation ownership marker does not match '$resolvedRoot'."
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

function Assert-RequiredSteps {
    param([string]$Phase, [object]$Result)
    $common = @(
        "fixture-settings-configured",
        "runtime-native-aot",
        "no-system-notification-or-external-activation")
    $phaseSteps = switch ($Phase) {
        "RouteAndPersist" {
            @(
                "route-baseline-empty",
                "activation-seed-persisted",
                "semicolon-body-open-routed",
                "ampersand-grammar-compatible",
                "complete-action-persisted",
                "complete-action-idempotent",
                "snooze-10m-persisted-and-idempotent",
                "snooze-30m-persisted-and-idempotent",
                "snooze-1h-persisted-and-idempotent",
                "snooze-tomorrow-persisted-and-idempotent",
                "legacy-snooze10-compatible",
                "invalid-inputs-rejected-without-mutation",
                "route-matrix-complete")
        }
        "VerifyAndClear" {
            @(
                "cross-process-action-state-reloaded",
                "restart-open-routed-without-mutation",
                "restart-rejection-stable",
                "activation-store-cleared")
        }
        "Postflight" {
            @(
                "cleared-store-reloaded",
                "postflight-empty-and-stable")
        }
    }

    foreach ($step in @($common + $phaseSteps)) {
        if (@($Result.steps | Where-Object { [string]$_ -ceq $step }).Count -ne 1) {
            throw "Todo notification activation phase '$Phase' is missing required step '$step'."
        }
    }
}

function Invoke-TodoNotificationActivationPhase {
    param(
        [ValidateSet("RouteAndPersist", "VerifyAndClear", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = $Phase.ToLowerInvariant()
    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-notification-activation-smoke\$phaseDirectory\result.json"
    if (Test-Path -LiteralPath $resultPath) {
        throw "Todo notification activation result already exists: '$resultPath'."
    }

    $variables = @(
        @(Get-ChildItem Env: |
            Where-Object { $_.Name -like "DESKBOX_AOT_*_SMOKE" } |
            Select-Object -ExpandProperty Name) +
        @(
            $smokeEnvironmentVariable,
            $phaseEnvironmentVariable,
            $runIdEnvironmentVariable) |
        Sort-Object -Unique)
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
        throw "Todo notification activation phase '$Phase' used the wrong executable or root."
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Todo notification activation phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Todo notification activation phase '$Phase' failed: $($result.error)"
    }

    $expectedFixtureRoot = Join-Path `
        $DataRoot `
        "aot-todo-notification-activation-fixture"
    if (-not [bool]$result.success -or
        [string]$result.stage -cne "5B-4C3B2A" -or
        [string]$result.scenario -cne $scenario -or
        [string]$result.phase -cne $Phase -or
        [string]$result.runId -cne $runId -or
        [int]$result.processId -ne [int]$session.primaryProcessId -or
        [bool]$result.isDynamicCodeSupported -or
        [bool]$result.systemNotificationAttempted -or
        [bool]$result.externalActivationAttempted -or
        -not [bool]$result.normalShutdownRequested -or
        -not (Test-PathEqual `
            -Left ([string]$result.previewDataRoot) `
            -Right $DataRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$result.fixtureRoot) `
            -Right $expectedFixtureRoot)) {
        throw "Todo notification activation phase '$Phase' returned inconsistent evidence."
    }
    Assert-RequiredSteps -Phase $Phase -Result $result

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[Notification] Native notification activated",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Native notification shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Tray notification fallback shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Snooze confirmation notification shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                ($_.IndexOf(
                    "[AotTodoNotificationActivationSmoke] Phase ",
                    [StringComparison]::Ordinal) -ge 0 -and
                    $_.IndexOf(" failed:", [StringComparison]::Ordinal) -ge 0)
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains forbidden activation/system-notification lines: $($failureLines -join ' | ')"
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
if ([int]$auditSummary.auditProfileVersion -ne 56 -or
    [int]$auditSummary.schemaVersion -ne 53 -or
    -not [bool]$auditSummary.sourceStableDuringAudit -or
    [string]$auditSummary.configuration -cne "Release" -or
    [string]$auditSummary.platform -cne "x64" -or
    [string]$auditSummary.runtimeIdentifier -cne "win-x64" -or
    @($auditSummary.warningCodes | Where-Object { $_ -ceq "WMC1506" }).Count -ne 0 -or
    [int]$auditSummary.warningCodeCounts.WMC1510 -ne 1213 -or
    @($auditSummary.alwaysThrowMessages).Count -ne 0 -or
    [int]$auditSummary.rustNative.abiVersion -ne 2 -or
    [int]$auditSummary.rustNative.capabilities -ne 511) {
    throw "Todo notification activation smoke requires profile 56 / schema 53."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "activation-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "activation-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "activation-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned Todo notification activation root."
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
        $phaseRuns.Add((Invoke-TodoNotificationActivationPhase `
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
        throw "The three-process action matrix did not keep distinct PIDs and one EXE hash."
    }

    for ($index = 0; $index -lt ($phaseRuns.Count - 1); $index++) {
        $left = $phaseRuns[$index]
        $right = $phaseRuns[$index + 1]
        if ([string]$left.result.after.storeSha256 -cne
                [string]$right.result.before.storeSha256 -or
            [long]$left.result.after.storeLength -ne
                [long]$right.result.before.storeLength) {
            throw "Todo action store continuity failed between '$($left.phase)' and '$($right.phase)'."
        }
    }

    $route = $phaseRuns[0].result
    $verify = $phaseRuns[1].result
    $postflight = $phaseRuns[2].result
    if (@($route.routes).Count -ne 18 -or
        @($route.after.items).Count -ne 7 -or
        @($verify.routes).Count -ne 2 -or
        @($verify.after.items).Count -ne 0 -or
        @($postflight.routes).Count -ne 0 -or
        @($postflight.after.items).Count -ne 0) {
        throw "The deterministic action, restart, clear, or postflight counts changed."
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production DeskBox data changed during the isolated action smoke."
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
            "aot-todo-notification-activation-fixture") `
        -Destination (Join-Path $archiveRoot "fixture") `
        -Recurse

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    if (-not $previewRootCleaned) {
        throw "Owned Todo notification activation preview root was not removed."
    }

    $session = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C3B2A"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        auditProfileVersion = 61
        auditSummarySchemaVersion = 55
        dataRoot = $DataRoot
        previewRootCleaned = $previewRootCleaned
        archiveRoot = $archiveRoot
        processIdsDistinct = $processIdsDistinct
        executableHashesMatch = $executableHashesMatch
        executablePath = $previewExecutablePath
        executableSha256 = $executableHashes[0]
        systemNotificationAttempted = $false
        externalActivationAttempted = $false
        totalRoutes = 20
        routeAndPersistRoutes = 18
        verifyAndClearRoutes = 2
        phases = @(
            $phaseRuns | ForEach-Object {
                [ordered]@{
                    phase = $_.phase
                    processId = $_.processId
                    resultPath = Join-Path `
                        $archiveRoot `
                        "$($_.phase.ToLowerInvariant())\result.json"
                    naturalExit = $_.naturalExit
                    routeCount = @($_.result.routes).Count
                    itemCountBefore = @($_.result.before.items).Count
                    itemCountAfter = @($_.result.after.items).Count
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
            "Todo notification activation smoke failed; owned evidence was preserved at '$DataRoot'.")
    }
}
