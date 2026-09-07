[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$requiredAuditProfileVersion = 61
$requiredSummarySchemaVersion = 55
$scenario = "EnvelopeAndSingleInstance"
$smokeEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_SMOKE"
$phaseEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_PHASE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_RUN_ID"
$runId = [Guid]::NewGuid().ToString("N")
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcher = Join-Path $PSScriptRoot "start-aot-preview.ps1"
$auditSummaryPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $repoRoot ".artifacts\aot-audit\win-x64\summary.json"
}
else {
    [System.IO.Path]::GetFullPath($SummaryPath)
}
$evidenceRoot = Join-Path `
    $repoRoot `
    ".artifacts\aot-todo-notification-forwarding-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-notification-forwarding-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoNotificationForwardingSmoke.v1"
$primaryPhases = @(
    "SeedColdStart",
    "ColdStartConsume",
    "PrimaryAwait",
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
                    -Left ([string]$_.ExecutablePath) `
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

function Wait-ResultState {
    param(
        [string]$ResultPath,
        [string[]]$States,
        [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            $candidate = Read-JsonRetry -Path $ResultPath
            if ($null -ne $candidate -and
                [string]$candidate.state -cin $States) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw (
        "Todo notification forwarding smoke timed out waiting for " +
        "'$($States -join ',')' at '$ResultPath'.")
}

function Invoke-WithForwardingEnvironment {
    param(
        [ValidateSet(
            "SeedColdStart",
            "ColdStartConsume",
            "PrimaryAwait",
            "SecondaryForward",
            "Postflight")]
        [string]$Phase,
        [scriptblock]$Action)

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
        & $Action
    }
    finally {
        foreach ($variable in $variables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previous[$variable],
                "Process")
        }
    }
}

function Assert-ResultContract {
    param([string]$Phase, [object]$Result, [int]$ExpectedProcessId)
    if (-not [bool]$Result.success -or
        [string]$Result.state -cne "Completed" -or
        [string]$Result.stage -cne "5B-4C3B2B1" -or
        [string]$Result.scenario -cne $scenario -or
        [string]$Result.phase -cne $Phase -or
        [string]$Result.runId -cne $runId -or
        [int]$Result.processId -ne $ExpectedProcessId -or
        [bool]$Result.isDynamicCodeSupported -or
        [bool]$Result.systemNotificationAttempted -or
        [bool]$Result.externalWindowsActivationAttempted -or
        -not [bool]$Result.normalShutdownRequested -or
        -not (Test-PathEqual `
            -Left ([string]$Result.previewDataRoot) `
            -Right $DataRoot)) {
        throw "Todo notification forwarding phase '$Phase' returned inconsistent evidence."
    }

    $commonSteps = @(
        "isolated-preview-root",
        "runtime-native-aot",
        "no-system-notification-or-windows-activation")
    $phaseSteps = switch ($Phase) {
        "SeedColdStart" {
            @(
                "fixture-seeded",
                "atomic-store-duplicate-and-corrupt-seeded")
        }
        "ColdStartConsume" {
            @(
                "cold-start-drain-preserved-user-input",
                "cold-start-mutation-persisted")
        }
        "PrimaryAwait" {
            @("live-second-instance-forwarding-persisted")
        }
        "Postflight" {
            @(
                "postflight-state-reloaded-and-spool-empty",
                "fixture-store-cleared")
        }
    }
    foreach ($step in @($commonSteps + $phaseSteps)) {
        if (@($Result.steps | Where-Object { [string]$_ -ceq $step }).Count -ne 1) {
            throw "Todo notification forwarding phase '$Phase' is missing '$step'."
        }
    }
}

function Invoke-StandardPhase {
    param(
        [ValidateSet("SeedColdStart", "ColdStartConsume", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-notification-forwarding-smoke\$($Phase.ToLowerInvariant())\result.json"
    $launchOutput = @(Invoke-WithForwardingEnvironment `
        -Phase $Phase `
        -Action {
            & $launcher `
                -SummaryPath $auditSummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1
        })
    $launch = $launchOutput[-1]
    if (-not (Test-PathEqual `
            -Left ([string]$launch.Exe) `
            -Right $ExecutablePath)) {
        throw "Todo notification forwarding phase '$Phase' used the wrong executable."
    }

    $result = Wait-ResultState `
        -ResultPath $resultPath `
        -States @("Completed", "Failed") `
        -Seconds $TimeoutSeconds
    if ([string]$result.state -ceq "Failed") {
        throw "Todo notification forwarding phase '$Phase' failed: $($result.error)"
    }
    if (-not (Wait-NaturalPreviewExit -ExecutablePath $ExecutablePath)) {
        throw "Todo notification forwarding phase '$Phase' did not exit naturally."
    }
    Assert-ResultContract `
        -Phase $Phase `
        -Result $result `
        -ExpectedProcessId ([int]$launch.StartedProcessId)
    return [PSCustomObject]@{
        phase = $Phase
        processId = [int]$launch.StartedProcessId
        executableSha256 = [string]$launch.ExeSha256
        resultPath = $resultPath
        result = $result
        naturalExit = $true
    }
}

function Invoke-LiveForwardPhase {
    param([string]$ExecutablePath)
    $phase = "PrimaryAwait"
    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-notification-forwarding-smoke\primaryawait\result.json"
    $primaryOutput = @(Invoke-WithForwardingEnvironment `
        -Phase $phase `
        -Action {
            & $launcher `
                -SummaryPath $auditSummaryPath `
                -DataRoot $DataRoot `
                -StartupWaitSeconds 1
        })
    $primary = $primaryOutput[-1]
    $ready = Wait-ResultState `
        -ResultPath $resultPath `
        -States @("Ready", "Failed") `
        -Seconds $TimeoutSeconds
    if ([string]$ready.state -ceq "Failed") {
        throw "Todo notification forwarding primary failed before readiness: $($ready.error)"
    }

    $secondaryOutput = @(Invoke-WithForwardingEnvironment `
        -Phase "SecondaryForward" `
        -Action {
            & $launcher `
                -SummaryPath $auditSummaryPath `
                -DataRoot $DataRoot `
                -NoStop `
                -ExpectExistingInstance `
                -StartupWaitSeconds 1
        })
    $secondary = $secondaryOutput[-1]
    if (-not [bool]$secondary.ExistingInstanceActivated -or
        [bool]$secondary.Running -or
        [int]$secondary.PrimaryProcessId -ne [int]$primary.PrimaryProcessId -or
        [int]$secondary.StartedProcessId -eq [int]$primary.PrimaryProcessId) {
        throw "The real secondary process did not exit into the existing primary."
    }

    $result = Wait-ResultState `
        -ResultPath $resultPath `
        -States @("Completed", "Failed") `
        -Seconds $TimeoutSeconds
    if ([string]$result.state -ceq "Failed") {
        throw "Todo notification forwarding primary failed: $($result.error)"
    }
    if (-not (Wait-NaturalPreviewExit -ExecutablePath $ExecutablePath)) {
        throw "Todo notification forwarding primary did not exit naturally."
    }
    Assert-ResultContract `
        -Phase $phase `
        -Result $result `
        -ExpectedProcessId ([int]$primary.PrimaryProcessId)
    if ([int]$result.secondaryProcessId -ne [int]$secondary.StartedProcessId -or
        -not [bool]$result.singleInstanceForwardingObserved) {
        throw "Primary evidence did not identify the exact real secondary process."
    }

    return [PSCustomObject]@{
        phase = $phase
        processId = [int]$primary.PrimaryProcessId
        secondaryProcessId = [int]$secondary.StartedProcessId
        executableSha256 = [string]$primary.ExeSha256
        resultPath = $resultPath
        result = $result
        naturalExit = $true
    }
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned Todo notification forwarding root '$resolvedRoot'."
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
        throw "The Todo notification forwarding ownership marker does not match."
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
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
if ([int]$auditSummary.auditProfileVersion -ne $requiredAuditProfileVersion -or
    [int]$auditSummary.schemaVersion -ne $requiredSummarySchemaVersion -or
    -not [bool]$auditSummary.sourceStableDuringAudit -or
    [string]$auditSummary.configuration -cne "Release" -or
    [string]$auditSummary.platform -cne "x64" -or
    [string]$auditSummary.runtimeIdentifier -cne "win-x64" -or
    [int]$auditSummary.rustNative.abiVersion -ne 2 -or
    [int]$auditSummary.rustNative.capabilities -ne 511) {
    throw (
        "Todo notification forwarding smoke requires profile " +
        "$requiredAuditProfileVersion / schema $requiredSummarySchemaVersion.")
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "forwarding-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "forwarding-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "forwarding-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot)) {
    throw "Refusing to replace an existing, production, or unowned forwarding root."
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
    $phaseRuns.Add((Invoke-StandardPhase `
        -Phase "SeedColdStart" `
        -ExecutablePath $previewExecutablePath))
    $phaseRuns.Add((Invoke-StandardPhase `
        -Phase "ColdStartConsume" `
        -ExecutablePath $previewExecutablePath))
    $phaseRuns.Add((Invoke-LiveForwardPhase `
        -ExecutablePath $previewExecutablePath))
    $phaseRuns.Add((Invoke-StandardPhase `
        -Phase "Postflight" `
        -ExecutablePath $previewExecutablePath))

    $seed = $phaseRuns[0].result
    $cold = $phaseRuns[1].result
    $live = $phaseRuns[2].result
    $postflight = $phaseRuns[3].result
    $allProcessIds = @(
        @($phaseRuns | ForEach-Object { [int]$_.processId }) +
        @([int]$phaseRuns[2].secondaryProcessId))
    $processIdsDistinct = @($allProcessIds | Sort-Object -Unique).Count -eq 5
    $executableHashes = @(
        @($phaseRuns | ForEach-Object { [string]$_.executableSha256 }) +
        @($phaseRuns | ForEach-Object { [string]$_.result.executableSha256 }) |
        Sort-Object -Unique)
    $executableHashesMatch = $executableHashes.Count -eq 1
    if (-not $processIdsDistinct -or -not $executableHashesMatch) {
        throw "The forwarding matrix did not retain five distinct PIDs and one EXE hash."
    }

    if ([string]$seed.storedDisposition -cne "Stored" -or
        [string]$seed.duplicateDisposition -cne "Duplicate" -or
        [int]$seed.pendingAfter -ne 2 -or
        [int]$cold.rejectedEnvelopeCount -ne 1 -or
        @($cold.consumedEnvelopeIds).Count -ne 1 -or
        [int]$cold.consumedSourceProcessIds[0] -ne [int]$seed.processId -or
        [string]$cold.consumedUserInput.todoSnooze -cne "30m" -or
        [int]$cold.pendingAfter -ne 0 -or
        @($live.consumedEnvelopeIds).Count -ne 1 -or
        [int]$live.consumedSourceProcessIds[0] -ne
            [int]$phaseRuns[2].secondaryProcessId -or
        [string]$live.consumedUserInput.todoSnooze -cne "tomorrow" -or
        [int]$live.pendingAfter -ne 0 -or
        [int]$postflight.pendingAfter -ne 0 -or
        -not [bool]$postflight.storeCleared) {
        throw "Typed envelope, corruption, UserInput, or postflight evidence changed."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $forbiddenLogLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Native notification shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[TodoReminder] Tray notification fallback shown",
                    [StringComparison]::Ordinal) -ge 0 -or
                ($_.IndexOf(
                    "[AotTodoNotificationForwarding] Phase ",
                    [StringComparison]::Ordinal) -ge 0 -and
                    $_.IndexOf(" failed:", [StringComparison]::Ordinal) -ge 0)
            })
    if ($forbiddenLogLines.Count -gt 0) {
        throw "Forwarding runtime log contains forbidden lines: $($forbiddenLogLines -join ' | ')"
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production DeskBox data changed during the isolated forwarding smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path `
            $DataRoot `
            "aot-todo-notification-forwarding-smoke") `
        -Destination (Join-Path $archiveRoot "results") `
        -Recurse
    Copy-Item `
        -LiteralPath $settingsPath `
        -Destination (Join-Path $archiveRoot "preview-settings.json")
    Copy-Item `
        -LiteralPath (Join-Path $DataRoot "DeskBox.log") `
        -Destination (Join-Path $archiveRoot "DeskBox.log")

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    if (-not $previewRootCleaned) {
        throw "Owned Todo notification forwarding preview root was not removed."
    }

    $session = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C3B2B1"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        auditProfileVersion = $requiredAuditProfileVersion
        auditSummarySchemaVersion = $requiredSummarySchemaVersion
        dataRoot = $DataRoot
        previewRootCleaned = $previewRootCleaned
        archiveRoot = $archiveRoot
        processIdsDistinct = $processIdsDistinct
        processIds = $allProcessIds
        executableHashesMatch = $executableHashesMatch
        executablePath = $previewExecutablePath
        executableSha256 = $executableHashes[0]
        systemNotificationAttempted = $false
        externalWindowsActivationAttempted = $false
        typedUserInputPreserved = $true
        corruptEnvelopeRejected = $true
        duplicateEnvelopeRejected = $true
        coldStartDrainVerified = $true
        realSecondaryProcessForwardingVerified = $true
        productionDataFingerprintBefore = $productionBefore
        productionDataFingerprintAfter = $productionAfter
        phases = @(
            $phaseRuns | ForEach-Object {
                [ordered]@{
                    phase = $_.phase
                    processId = $_.processId
                    secondaryProcessId = if ($_.phase -ceq "PrimaryAwait") {
                        $_.secondaryProcessId
                    }
                    else {
                        $null
                    }
                    resultPath = Join-Path `
                        $archiveRoot `
                        "results\$(([string]$_.phase).ToLowerInvariant())\result.json"
                    naturalExit = $_.naturalExit
                }
            })
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
            "Todo notification forwarding smoke failed; owned evidence was " +
            "preserved at '$DataRoot'.")
    }
}
