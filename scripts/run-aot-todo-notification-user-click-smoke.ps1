[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(60, 1800)]
    [int]$TimeoutSeconds = 600,

    [switch]$IncludeColdStart
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "RealWindowsNotificationUserClick"
$smokeEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_SMOKE"
$phaseEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_PHASE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_RUN_ID"
$previewRootEnvironmentVariable = "DESKBOX_AOT_PREVIEW_DATA_ROOT"
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
    ".artifacts\aot-todo-notification-user-click-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-notification-user-click-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoNotificationUserClickSmoke.v1"
$requiredAuditProfileVersion = 61
$requiredSummarySchemaVersion = 55

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
    foreach ($process in @(Get-ExactPreviewProcesses `
            -ExecutablePath $ExecutablePath)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process `
            -Id $process.ProcessId `
            -Timeout 5 `
            -ErrorAction SilentlyContinue
    }
}

function Wait-NaturalPreviewExit {
    param([string]$ExecutablePath, [int]$Seconds = 45)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-ExactPreviewProcesses `
                -ExecutablePath $ExecutablePath).Count -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return @(Get-ExactPreviewProcesses `
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

function Wait-InteractiveResult {
    param(
        [string]$ResultPath,
        [string[]]$TerminalStates,
        [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $lastMessage = ""
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            $candidate = Read-JsonRetry -Path $ResultPath
            if ($null -ne $candidate) {
                $instruction = [string]$candidate.todoNotificationUserClick.currentInstruction
                $message = "state=$($candidate.state) instruction=$instruction"
                if ($message -cne $lastMessage) {
                    Write-Host "[真人点击] $message"
                    $lastMessage = $message
                }
                if ([string]$candidate.state -cin $TerminalStates) {
                    return $candidate
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for interactive notification evidence '$ResultPath'."
}

function Invoke-WithSmokeEnvironment {
    param(
        [ValidateSet("RunningMatrix", "ColdSeed", "Postflight")]
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

function Invoke-StartedPhase {
    param(
        [ValidateSet("RunningMatrix", "ColdSeed", "Postflight")]
        [string]$Phase,
        [string[]]$TerminalStates,
        [string]$ExecutablePath)

    $resultPath = Join-Path `
        $DataRoot `
        "aot-todo-notification-user-click-smoke\$($Phase.ToLowerInvariant())\result.json"
    $launchOutput = @(Invoke-WithSmokeEnvironment `
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
        throw "Phase '$Phase' launched the wrong executable."
    }

    $result = Wait-InteractiveResult `
        -ResultPath $resultPath `
        -TerminalStates $TerminalStates `
        -Seconds $TimeoutSeconds
    if ([string]$result.state -ceq "Failed") {
        throw "Phase '$Phase' failed: $($result.error)"
    }
    if (-not (Wait-NaturalPreviewExit -ExecutablePath $ExecutablePath)) {
        throw "Phase '$Phase' did not exit naturally."
    }
    return [PSCustomObject]@{
        phase = $Phase
        processId = [int]$result.processId
        resultPath = $resultPath
        result = $result
        naturalExit = $true
    }
}

function Assert-CompletedClickResult {
    param(
        [string]$Phase,
        [object]$Result,
        [int]$ExpectedCaseCount)
    $cases = @($Result.todoNotificationUserClick.cases)
    if (-not [bool]$Result.success -or
        [string]$Result.state -cne "Completed" -or
        [string]$Result.scenario -cne $scenario -or
        [string]$Result.todoNotificationUserClick.stage -cne "5B-4C3B2B2B" -or
        [string]$Result.todoNotificationUserClick.phase -cne $Phase -or
        [string]$Result.todoNotificationUserClick.runId -cne $runId -or
        [bool]$Result.isDynamicCodeSupported -or
        -not [bool]$Result.todoNotificationUserClick.externalWindowsActivationObserved -or
        -not [bool]$Result.todoNotificationUserClick.userClickVerified -or
        $cases.Count -ne $ExpectedCaseCount -or
        [int]$Result.todoNotificationUserClick.activationCount -ne
            $ExpectedCaseCount -or
        [int]$Result.todoNotificationUserClick.routeCount -ne
            $ExpectedCaseCount -or
        @($cases |
            Where-Object { -not [bool]$_.userClickVerified }).Count -ne 0) {
        throw "Phase '$Phase' returned inconsistent real-click evidence."
    }

    $invalidSources = @($cases | Where-Object {
        [string]$_.activationSource -cnotin @(
            "NotificationInvokedEvent",
            "CurrentAppInstance") -or
        [int]$_.activationSourceProcessId -le 0 -or
        [int]$_.receivingProcessId -ne [int]$Result.processId -or
        [bool]$_.forwardedThroughEnvelope -or
        -not [bool]$_.routeSucceeded -or
        [long]$_.windowHandle -eq 0 -or
        -not [bool]$_.visible -or
        -not [bool]$_.hasXamlRoot -or
        -not [bool]$_.itemVisible
    })
    if ($invalidSources.Count -ne 0) {
        throw "Phase '$Phase' contains invalid Windows activation provenance."
    }

    if ($Phase -ceq "RunningMatrix") {
        $body = @($cases | Where-Object { [string]$_.case -ceq "body" })
        $complete = @($cases | Where-Object {
            [string]$_.case -ceq "complete"
        })
        $snooze = @($cases | Where-Object {
            [string]$_.case -ceq "snooze"
        })
        if ($body.Count -ne 1 -or
            $complete.Count -ne 1 -or
            $snooze.Count -ne 1 -or
            @($cases | Where-Object {
                [string]$_.activationSource -cne
                    "NotificationInvokedEvent" -or
                [int]$_.activationSourceProcessId -ne
                    [int]$Result.processId
            }).Count -ne 0 -or
            [string]$body[0].routeDisposition -cne "Opened" -or
            -not [bool]$body[0].targetPresented -or
            -not [bool]$body[0].itemSelected -or
            [string]$complete[0].expectedAction -cne "complete" -or
            [string]$complete[0].routeDisposition -cne "Completed" -or
            -not [bool]$complete[0].refreshCompleted -or
            -not [bool]$complete[0].isCompleted -or
            [string]$snooze[0].expectedAction -cne "snooze" -or
            [string]$snooze[0].expectedSnooze -cne "30m" -or
            [string]$snooze[0].userInput.todoSnooze -cne "30m" -or
            [string]$snooze[0].routeDisposition -cne "Snoozed" -or
            -not [bool]$snooze[0].refreshCompleted) {
            throw "RunningMatrix body/Complete/Snooze evidence is incomplete."
        }
    }
    elseif ($Phase -ceq "ColdConsume") {
        if ([string]$cases[0].case -cne "cold" -or
            [int]$cases[0].activationSourceProcessId -ne
                [int]$Result.processId -or
            [string]$cases[0].routeDisposition -cne "Opened" -or
            -not [bool]$cases[0].targetPresented -or
            -not [bool]$cases[0].itemSelected) {
            throw "ColdConsume external launch evidence is incomplete."
        }
    }
}

function Set-ColdActivationUserEnvironment {
    param([hashtable]$PreviousValues)
    foreach ($variable in @(
            $previewRootEnvironmentVariable,
            $smokeEnvironmentVariable,
            $phaseEnvironmentVariable,
            $runIdEnvironmentVariable)) {
        $PreviousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "User")
    }
    [Environment]::SetEnvironmentVariable(
        $previewRootEnvironmentVariable,
        $DataRoot,
        "User")
    [Environment]::SetEnvironmentVariable(
        $smokeEnvironmentVariable,
        $scenario,
        "User")
    [Environment]::SetEnvironmentVariable(
        $phaseEnvironmentVariable,
        "ColdConsume",
        "User")
    [Environment]::SetEnvironmentVariable(
        $runIdEnvironmentVariable,
        $runId,
        "User")
}

function Restore-ColdActivationUserEnvironment {
    param([hashtable]$PreviousValues)
    foreach ($entry in $PreviousValues.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            [string]$entry.Key,
            $entry.Value,
            "User")
    }
}

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean unowned click root '$resolvedRoot'."
    }
    $marker = Get-Content `
        -LiteralPath (Join-Path $resolvedRoot $ownedMarkerName) `
        -Raw | ConvertFrom-Json
    if ([string]$marker.kind -cne $ownedMarkerKind -or
        [string]$marker.runId -cne $runId -or
        -not (Test-PathEqual `
            -Left ([string]$marker.repositoryRoot) `
            -Right $repoRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$marker.dataRoot) `
            -Right $resolvedRoot)) {
        throw "The real-click ownership marker does not match."
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
    [string]$auditSummary.runtimeIdentifier -cne "win-x64") {
    throw (
        "Real-click smoke requires profile " +
        "$requiredAuditProfileVersion / schema $requiredSummarySchemaVersion.")
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "click-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$archiveRoot = Join-Path $evidenceRoot "runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $archiveRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot)) {
    throw "Refusing to replace an existing, production, or unowned click root."
}

$dataDirectory = Join-Path $DataRoot "data"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    dataRoot = $DataRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $DataRoot $ownedMarkerName) `
    -Encoding UTF8
@{
    schemaVersion = 5
    language = "zh-CN"
    autoStart = $false
    autoCheckForUpdates = $false
    globalHotkeyEnabled = $false
    searchHotkeyEnabled = $false
    todoReminderEnabled = $false
    hasCompletedOnboarding = $true
    completedOnboardingVersion = 1
    hasResolvedInitialFileWidgetSetup = $true
    featureWidgetEnabledStates = @{
        QuickCapture = $false
        Todo = $true
        Music = $false
        Weather = $false
        Search = $false
        Glance = $false
    }
    widgets = @()
} | ConvertTo-Json -Depth 16 | Set-Content `
    -LiteralPath (Join-Path $dataDirectory "settings.json") `
    -Encoding UTF8

$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$userEnvironmentBefore = @{}
$userEnvironmentChanged = $false
$runSucceeded = $false
$previewRootCleaned = $false
$phaseRuns = [System.Collections.Generic.List[object]]::new()

try {
    Write-Host "将依次出现 3 条真实通知；请按通知正文提示操作。"
    $running = Invoke-StartedPhase `
        -Phase "RunningMatrix" `
        -TerminalStates @("Completed", "Failed") `
        -ExecutablePath $previewExecutablePath
    Assert-CompletedClickResult `
        -Phase "RunningMatrix" `
        -Result $running.result `
        -ExpectedCaseCount 3
    $phaseRuns.Add($running)

    $cold = $null
    if ($IncludeColdStart) {
        Set-ColdActivationUserEnvironment -PreviousValues $userEnvironmentBefore
        $userEnvironmentChanged = $true
        $seed = Invoke-StartedPhase `
            -Phase "ColdSeed" `
            -TerminalStates @("ReadyForUserClick", "Failed") `
            -ExecutablePath $previewExecutablePath
        $phaseRuns.Add($seed)
        if (@(Get-ExactPreviewProcesses `
                -ExecutablePath $previewExecutablePath).Count -ne 0) {
            throw "Cold-seed process was still running before the user click."
        }

        Write-Host "应用已完全退出。现在请点击通知中心里的冷启动验证通知正文。"
        $coldResultPath = Join-Path `
            $DataRoot `
            "aot-todo-notification-user-click-smoke\coldconsume\result.json"
        $coldResult = Wait-InteractiveResult `
            -ResultPath $coldResultPath `
            -TerminalStates @("Completed", "Failed") `
            -Seconds $TimeoutSeconds
        if ([string]$coldResult.state -ceq "Failed") {
            throw "ColdConsume failed: $($coldResult.error)"
        }
        Assert-CompletedClickResult `
            -Phase "ColdConsume" `
            -Result $coldResult `
            -ExpectedCaseCount 1
        if (-not (Wait-NaturalPreviewExit `
                -ExecutablePath $previewExecutablePath)) {
            throw "ColdConsume did not exit naturally."
        }
        $cold = [PSCustomObject]@{
            phase = "ColdConsume"
            processId = [int]$coldResult.processId
            resultPath = $coldResultPath
            result = $coldResult
            naturalExit = $true
        }
        $phaseRuns.Add($cold)
        if ([int]$seed.processId -eq [int]$cold.processId) {
            throw "Cold activation did not launch a distinct process."
        }
    }

    $postflight = Invoke-StartedPhase `
        -Phase "Postflight" `
        -TerminalStates @("Completed", "Failed") `
        -ExecutablePath $previewExecutablePath
    $phaseRuns.Add($postflight)

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production DeskBox data changed during the isolated click matrix."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path `
            $DataRoot `
            "aot-todo-notification-user-click-smoke") `
        -Destination (Join-Path $archiveRoot "results") `
        -Recurse
    Copy-Item `
        -LiteralPath (Join-Path $DataRoot "DeskBox.log") `
        -Destination (Join-Path $archiveRoot "DeskBox.log")

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = -not (Test-Path -LiteralPath $DataRoot)
    if (-not $previewRootCleaned) {
        throw "Owned real-click preview root was not removed."
    }

    $session = [ordered]@{
        schemaVersion = 1
        stage = "5B-4C3B2B2B"
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        includeColdStart = [bool]$IncludeColdStart
        auditProfileVersion = $requiredAuditProfileVersion
        auditSummarySchemaVersion = $requiredSummarySchemaVersion
        executablePath = $previewExecutablePath
        executableSha256 = (Get-FileHash `
            -LiteralPath $previewExecutablePath `
            -Algorithm SHA256).Hash
        previewRootCleaned = $previewRootCleaned
        archiveRoot = $archiveRoot
        runningUserClicksVerified = $true
        coldStartUserClickVerified = if ($IncludeColdStart) { $true } else { $false }
        productionDataFingerprintBefore = $productionBefore
        productionDataFingerprintAfter = $productionAfter
        phases = @($phaseRuns | ForEach-Object {
            [ordered]@{
                phase = $_.phase
                processId = $_.processId
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
    if ($userEnvironmentChanged) {
        Restore-ColdActivationUserEnvironment `
            -PreviousValues $userEnvironmentBefore
    }
    if (-not [string]::IsNullOrWhiteSpace($previewExecutablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    }
    if (-not $runSucceeded) {
        Write-Warning (
            "Real notification click smoke failed or was interrupted; owned " +
            "evidence was preserved at '$DataRoot'.")
    }
}
