[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 180)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "TodoNotificationSurfaceRouting"
$smokeEnvironmentVariable =
    "DESKBOX_AOT_TODO_NOTIFICATION_SURFACE_SMOKE"
$requiredAuditProfileVersion = 61
$requiredSummarySchemaVersion = 55
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
    ".artifacts\aot-todo-notification-surface-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-todo-notification-surface-owned.json"
$ownedMarkerKind = "DeskBox.Aot.TodoNotificationSurfaceSmoke.v1"

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
    throw "Todo notification surface smoke timed out without terminal evidence."
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

function Assert-OwnedRootAndRemove {
    param([string]$Root)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $resolvedRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
        throw "Refusing to clean an unowned Todo notification surface root '$resolvedRoot'."
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
        throw "The Todo notification surface ownership marker does not match '$resolvedRoot'."
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
    throw "Native AOT audit summary is stale or incompatible for the Todo notification surface smoke."
}

New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "work\$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot)) {
    throw "Refusing an unsafe Todo notification surface data root '$DataRoot'."
}
if (Test-Path -LiteralPath $DataRoot) {
    throw "Refusing to replace an existing Todo notification surface root '$DataRoot'."
}

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
$ownedMarkerPath = Join-Path $DataRoot $ownedMarkerName
[ordered]@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    dataRoot = $DataRoot
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
} | ConvertTo-Json | Set-Content -LiteralPath $ownedMarkerPath -Encoding UTF8

$resultPath = Join-Path `
    $DataRoot `
    "aot-todo-notification-surface-smoke\result.json"
$productionStateBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$executablePath = $null
$naturalExit = $false
$previewRootCleaned = $false

try {
    $variables = @(
        @(Get-ChildItem Env: |
            Where-Object { $_.Name -like "DESKBOX_AOT_*_SMOKE" } |
            Select-Object -ExpandProperty Name) +
        @($smokeEnvironmentVariable)) | Sort-Object -Unique
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

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    if ($result.state -ne "Completed" -or -not [bool]$result.success) {
        throw "Todo notification surface smoke failed: $($result.error)"
    }
    if ([int]$result.schemaVersion -ne 1 -or
        [string]$result.scenario -cne $scenario -or
        [bool]$result.isDynamicCodeSupported -or
        [string]$result.todoNotificationSurface.stage -cne "5B-4C3B2B2A" -or
        [int]$result.todoNotificationSurface.windowHandle -eq 0 -or
        -not [bool]$result.todoNotificationSurface.visible -or
        -not [bool]$result.todoNotificationSurface.hasXamlRoot -or
        -not [bool]$result.todoNotificationSurface.bodyTargetPresented -or
        -not [bool]$result.todoNotificationSurface.bodyItemVisible -or
        -not [bool]$result.todoNotificationSurface.bodyItemSelected -or
        -not [bool]$result.todoNotificationSurface.completeRefreshCompleted -or
        -not [bool]$result.todoNotificationSurface.completeVisibleState -or
        -not [bool]$result.todoNotificationSurface.snoozeRefreshCompleted -or
        [string]$result.todoNotificationSurface.snoozeSelection -cne "30m" -or
        [int]$result.todoNotificationSurface.routeCount -ne 3 -or
        [bool]$result.todoNotificationSurface.systemNotificationAttempted -or
        [bool]$result.todoNotificationSurface.externalWindowsActivationAttempted -or
        [bool]$result.todoNotificationSurface.userClickVerified -or
        -not [bool]$result.todoNotificationSurface.normalShutdownRequested) {
        throw "Todo notification surface evidence is incomplete or mislabeled."
    }

    $requiredSteps = @(
        "runtime-native-aot",
        "product-services-ready",
        "isolated-fixture-seeded",
        "body-route-target-presented",
        "body-visible-item-located",
        "complete-visible-refresh-proved",
        "snooze-user-input-visible-refresh-proved",
        "exact-three-routes-observed",
        "controlled-input-not-mislabeled-as-real-click")
    foreach ($step in $requiredSteps) {
        if (@($result.steps | Where-Object { [string]$_ -ceq $step }).Count -ne 1) {
            throw "Todo notification surface evidence is missing step '$step'."
        }
    }

    $previewSession = Get-Content `
        -LiteralPath $previewSessionPath `
        -Raw | ConvertFrom-Json
    $executablePath = [string]$previewSession.executablePath
    if ([string]::IsNullOrWhiteSpace($executablePath) -or
        -not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or
        -not (Test-PathEqual `
            -Left ([string]$result.executablePath) `
            -Right $executablePath)) {
        throw "Todo notification surface process did not use the audited executable."
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $executablePath `
        -Seconds 30
    if (-not $naturalExit) {
        throw "Todo notification surface process did not exit naturally."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $runtimeFailureLogLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[AotTodoNotificationSurface] Failed:", [StringComparison]::Ordinal) -ge 0
            })
    if ($runtimeFailureLogLines.Count -gt 0) {
        throw "Todo notification surface runtime log contains a failure."
    }

    $productionStateAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if (-not [string]::Equals(
            [string]$productionStateAfter.fingerprint,
            [string]$productionStateBefore.fingerprint,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [int]$productionStateAfter.fileCount -ne
            [int]$productionStateBefore.fileCount -or
        [long]$productionStateAfter.bytes -ne [long]$productionStateBefore.bytes) {
        throw "Production data changed during the Todo notification surface smoke."
    }

    $archiveRoot = Join-Path $evidenceRoot "runs\$runId"
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    $archivedResultPath = Join-Path $archiveRoot "result.json"
    $archivedLogPath = Join-Path $archiveRoot "DeskBox.log"
    $archivedPreviewSessionPath = Join-Path $archiveRoot "preview-session.json"
    Copy-Item -LiteralPath $resultPath -Destination $archivedResultPath
    Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedLogPath
    Copy-Item `
        -LiteralPath $previewSessionPath `
        -Destination $archivedPreviewSessionPath

    Assert-OwnedRootAndRemove -Root $DataRoot
    $previewRootCleaned = $true

    $sessionPath = Join-Path $evidenceRoot "surface-session.json"
    $session = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        runId = $runId
        scenario = $scenario
        executablePath = $executablePath
        executableSha256 = $previewSession.executableSha256
        auditProfileVersion = $requiredAuditProfileVersion
        auditSummarySchemaVersion = $requiredSummarySchemaVersion
        processId = [int]$result.processId
        naturalExit = $naturalExit
        previewDataRoot = $DataRoot
        resultPath = $archivedResultPath
        runtimeLogPath = $archivedLogPath
        previewSessionPath = $archivedPreviewSessionPath
        productionDataRoot = $productionDataRoot
        productionDataFingerprintBefore = $productionStateBefore.fingerprint
        productionDataFingerprintAfter = $productionStateAfter.fingerprint
        previewRootCleaned = $previewRootCleaned
        userClickVerified = [bool]$result.todoNotificationSurface.userClickVerified
        automatedSurfaceEvidence = $result.todoNotificationSurface
    }
    $temporarySessionPath = $sessionPath + ".tmp"
    $session | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath $temporarySessionPath -Encoding UTF8
    Move-Item `
        -LiteralPath $temporarySessionPath `
        -Destination $sessionPath `
        -Force

    [PSCustomObject]@{
        Scenario = $scenario
        Success = $true
        Exe = $executablePath
        DataRoot = $DataRoot
        SessionPath = $sessionPath
        ResultPath = $archivedResultPath
        ProcessCount = 1
        NaturalExitCount = 1
        RouteCount = [int]$result.todoNotificationSurface.routeCount
        UserClickVerified = [bool]$result.todoNotificationSurface.userClickVerified
        ProductionDataFingerprint = $productionStateAfter.fingerprint
        PreviewRootCleaned = $previewRootCleaned
        Running = $false
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($executablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath $executablePath
    }
}
