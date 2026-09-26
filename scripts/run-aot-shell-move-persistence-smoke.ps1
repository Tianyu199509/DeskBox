[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "ShellMovePersistenceRestart"
$phaseEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE"
$runIdEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_RUN_ID"
$smokeEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SMOKE"
$runId = [Guid]::NewGuid().ToString("N")
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcher = Join-Path $PSScriptRoot "start-aot-preview.ps1"
$previewSessionPath = Join-Path $repoRoot ".artifacts\aot-preview\win-x64\session.json"
$evidenceRoot = Join-Path $repoRoot ".artifacts\aot-managed-ui-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-managed-ui-owned.json"
$ownedMarkerKind = "DeskBox.Aot.ShellMoveSmoke.v1"

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
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
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
            $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))).Replace("-", "")
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
        $relativePath = $file.FullName.Substring($normalizedPath.Length).TrimStart('\', '/')
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
                -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
                (Test-PathEqual -Left $_.ExecutablePath -Right $ExecutablePath)
            })
}

function Stop-ExactPreviewProcess {
    param([string]$ExecutablePath)
    foreach ($process in @(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}

function Wait-NaturalPreviewExit {
    param([string]$ExecutablePath, [int]$Seconds = 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath).Count -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return @(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath).Count -eq 0
}

function Get-FixtureState {
    param([string]$FixtureRoot)
    $normalizedRoot = [System.IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        throw "Owned Shell move fixture root is missing: '$normalizedRoot'."
    }
    $directories = @(
        Get-ChildItem -LiteralPath $normalizedRoot -Directory -Recurse -Force |
            ForEach-Object {
                $_.FullName.Substring($normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
            } |
            Sort-Object)
    $files = @(
        Get-ChildItem -LiteralPath $normalizedRoot -File -Recurse -Force |
            ForEach-Object {
                [PSCustomObject]@{
                    relativePath = $_.FullName.Substring($normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    name = $_.Name
                    length = [long]$_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            } |
            Sort-Object -Property relativePath)
    return [PSCustomObject]@{
        fixtureRoot = $normalizedRoot
        directories = $directories
        files = $files
    }
}

function Assert-Sequence {
    param([object[]]$Actual, [string[]]$Expected, [string]$Name)
    if ($Actual.Count -ne $Expected.Count) {
        throw "$Name count mismatch. Expected $($Expected.Count), got $($Actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ([string]$Actual[$index] -cne $Expected[$index]) {
            throw "$Name mismatch at index $index. Expected '$($Expected[$index])', got '$($Actual[$index])'."
        }
    }
}

function Assert-FixtureState {
    param(
        [object]$State,
        [ValidateSet("Baseline", "Mutated")][string]$Expected,
        [string]$Name
    )
    Assert-Sequence `
        -Actual @($State.directories) `
        -Expected @("desktop-root", "widget-root") `
        -Name "$Name.directories"
    $expectedFiles = if ($Expected -ceq "Baseline") {
        @(
            "widget-root/baseline.txt",
            "widget-root/$cancelName",
            "widget-root/$lateName",
            "widget-root/$partialFirstName",
            "widget-root/$partialSecondName",
            "widget-root/$realName")
    }
    else {
        @(
            "desktop-root/$lateName",
            "desktop-root/$partialFirstName",
            "desktop-root/$realName",
            "widget-root/baseline.txt",
            "widget-root/$cancelName",
            "widget-root/$partialSecondName")
    }
    Assert-Sequence `
        -Actual @($State.files | ForEach-Object { [string]$_.relativePath }) `
        -Expected $expectedFiles `
        -Name "$Name.files"
    foreach ($file in @($State.files)) {
        if ([long]$file.length -le 0 -or
            [string]$file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "$Name contains an empty or unhashed file '$($file.relativePath)'."
        }
    }
}

function Assert-OwnedFileHashes {
    param([object]$Initial, [object]$Actual, [string]$Name)
    $initialByName = @{}
    foreach ($file in @($Initial.files)) {
        $initialByName[[string]$file.name] = $file
    }
    foreach ($file in @($Actual.files)) {
        $baseline = $initialByName[[string]$file.name]
        if ($null -eq $baseline -or
            [long]$baseline.length -ne [long]$file.length -or
            [string]$baseline.sha256 -cne [string]$file.sha256) {
            throw "$Name changed the content identity for '$($file.name)'."
        }
    }
}

function Assert-PhaseEvidence {
    param([object]$Result, [string]$Phase, [object]$Session)
    if ([int]$Result.schemaVersion -ne 1 -or
        [string]$Result.scenario -cne $scenario -or
        [string]$Result.shellMove.phase -cne $Phase -or
        [string]$Result.shellMove.runId -cne $runId -or
        [bool]$Result.isDynamicCodeSupported -or
        -not [bool]$Result.shellMove.normalShutdownRequested -or
        -not [bool]$Result.shellMove.flushSucceeded -or
        [int]$Result.processId -ne [int]$Session.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$Result.executablePath) -Right ([string]$Session.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$Result.previewDataRoot) -Right $DataRoot) -or
        [long]$Result.shellMove.windowHandle -eq 0 -or
        -not [bool]$Result.shellMove.hasXamlRoot -or
        -not [bool]$Result.shellMove.visible -or
        [int]$Result.loadedSurfaceCount -ne 2 -or
        [int]$Result.visibleSurfaceCount -ne 2) {
        throw "Shell move phase '$Phase' did not prove its AOT process, owner surface, flush, and shutdown identity."
    }
    Assert-Sequence `
        -Actual @($Result.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4c1b2a-file") `
        -Name "$Phase.seededWidgetIds"
    Assert-Sequence `
        -Actual @($Result.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "ShellMoveSurfaceHostReady",
        "ShellMoveOwnedRootsVerified",
        "ShellMovePersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @("ShellMoveOwnedBaselineVerified", "ShellMoveMenuMatrixCompleted", "ShellMoveMutationApplied")
        }
        "VerifyRestore" {
            @("ShellMoveRestartMutationVerified", "ShellMoveFilesRestoredByHarness", "ShellMoveBaselineRestored")
        }
        "Compensate" {
            @("ShellMoveCompensationFilesRestored", "ShellMoveCompensationCompleted")
        }
        default { @("ShellMovePostflightVerified") }
    }
    foreach ($step in $requiredSteps) {
        if (@($Result.steps) -cnotcontains $step) {
            throw "Shell move phase '$Phase' omitted required step '$step'."
        }
    }

    $operations = $Result.shellMove.operations
    if ($Phase -ceq "Mutate") {
        if (-not [bool]$operations.productMenuPathCompleted -or
            -not [bool]$operations.lateTaskPendingWhenProductReturned -or
            @($operations.menus).Count -ne 4 -or
            @($operations.invocations).Count -ne 4) {
            throw "Shell move mutate did not retain the complete menu and invocation matrix."
        }
        Assert-Sequence `
            -Actual @($operations.invocations | ForEach-Object { [string]$_.mode }) `
            -Expected @("Real", "Partial", "Cancel", "Late") `
            -Name "Mutate.invocationModes"
        Assert-Sequence `
            -Actual @($operations.invocations | ForEach-Object { [string]$_.fileServiceOutcome }) `
            -Expected @("Returned", "Returned", "Returned", "RecoveredPending") `
            -Name "Mutate.outcomes"
        Assert-Sequence `
            -Actual @($operations.invocations | ForEach-Object { [string]$_.completedCount }) `
            -Expected @("1", "1", "0", "1") `
            -Name "Mutate.completedCounts"
        Assert-Sequence `
            -Actual @($operations.menus | ForEach-Object { [string]$_.feedbackSeverity }) `
            -Expected @("Success", "Success", "Info", "Success") `
            -Name "Mutate.feedbackSeverities"
        foreach ($menu in @($operations.menus)) {
            if ([long]$menu.hostWindowHandle -ne [long]$Result.shellMove.windowHandle -or
                -not [bool]$menu.moveEnabled -or
                -not [bool]$menu.automationInvoked -or
                [string]$menu.feedbackKey -cne "file-move-desktop") {
                throw "Shell move menu evidence did not retain the real host owner or product feedback route."
            }
        }
        foreach ($invocation in @($operations.invocations)) {
            if ([long]$invocation.ownerWindowHandle -ne [long]$Result.shellMove.windowHandle -or
                -not [bool]$invocation.nativeTaskReturned) {
                throw "Shell move invocation did not use the real owner HWND or eventually return."
            }
        }
        if (-not [bool]$operations.invocations[0].actualShellOperation -or
            -not [bool]$operations.invocations[1].simulatedOperationsAborted -or
            -not [bool]$operations.invocations[2].simulatedOperationsAborted -or
            [datetimeoffset]$operations.invocations[3].productReturnedAtUtc -ge
                [datetimeoffset]$operations.invocations[3].nativeTaskReturnedAtUtc) {
            throw "Shell move invocation branch evidence is inconsistent."
        }
        Assert-Sequence `
            -Actual @($Result.shellMove.after.history | ForEach-Object { [string]$_.itemCount }) `
            -Expected @("1", "0", "1", "1") `
            -Name "Mutate.historyItemCounts"
    }
    elseif ($Phase -ceq "VerifyRestore") {
        Assert-Sequence `
            -Actual @($Result.shellMove.before.history | ForEach-Object { [string]$_.itemCount }) `
            -Expected @("1", "0", "1", "1") `
            -Name "VerifyRestore.beforeHistory"
        if (-not [bool]$operations.restoredByHarness -or
            [bool]$operations.compensation -or
            [int]$operations.restoredFileCount -ne 3 -or
            @($Result.shellMove.after.history).Count -ne 0) {
            throw "Shell move VerifyRestore did not restore three moved files and clear history."
        }
    }
    elseif ($Phase -ceq "Compensate") {
        if (-not [bool]$operations.restoredByHarness -or
            -not [bool]$operations.compensation -or
            @($Result.shellMove.after.history).Count -ne 0) {
            throw "Shell move compensation did not restore its owned baseline."
        }
    }
    elseif (@($Result.shellMove.before.history).Count -ne 0 -or
        @($Result.shellMove.after.history).Count -ne 0) {
        throw "Shell move postflight retained unexpected organization history."
    }
}

function Invoke-ShellMovePhase {
    param(
        [ValidateSet("Mutate", "VerifyRestore", "Postflight", "Compensate")]
        [string]$Phase,
        [string]$ResultPath
    )
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
        $previous[$variable] = [Environment]::GetEnvironmentVariable($variable, "Process")
    }
    try {
        foreach ($variable in $variables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable($smokeEnvironmentVariable, $scenario, "Process")
        [Environment]::SetEnvironmentVariable($phaseEnvironmentVariable, $Phase, "Process")
        [Environment]::SetEnvironmentVariable($runIdEnvironmentVariable, $runId, "Process")
        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $variables) {
            [Environment]::SetEnvironmentVariable($variable, $previous[$variable], "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Shell move phase '$Phase' did not create preview session evidence."
    }
    $session = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$session.previewDataRoot) -Right $DataRoot)) {
        throw "Shell move phase '$Phase' used the wrong preview root."
    }
    $result = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $result = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during atomic replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }
    $naturalExit = Wait-NaturalPreviewExit -ExecutablePath ([string]$session.executablePath)
    if (-not $naturalExit) {
        throw "Shell move phase '$Phase' did not exit naturally."
    }
    if ($null -eq $result) {
        throw "Shell move phase '$Phase' timed out without terminal evidence."
    }
    if ($result.state -ne "Completed" -or -not [bool]$result.success) {
        throw "Shell move phase '$Phase' failed: $($result.error)"
    }
    Assert-PhaseEvidence -Result $result -Phase $Phase -Session $session
    return [PSCustomObject]@{
        phase = $Phase
        session = $session
        result = $result
        naturalExit = $naturalExit
    }
}

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Native AOT preview launcher was not found: '$launcher'."
}
if (-not (Test-Path -LiteralPath $SummaryPath -PathType Leaf)) {
    throw "Native AOT audit summary was not found: '$SummaryPath'."
}
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "shell-move-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$recoveryRoot = [System.IO.Path]::GetFullPath("$DataRoot-Recovery")
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $recoveryRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $recoveryRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $recoveryRoot) -or
    (Test-PathEqualOrInside -Root $recoveryRoot -Candidate $productionDataRoot)) {
    throw "Shell move preview/recovery roots escaped their owned evidence boundary."
}
if (Test-Path -LiteralPath $DataRoot) {
    throw "Refusing to replace an existing Shell move preview root."
}
if (Test-Path -LiteralPath $recoveryRoot) {
    throw "Refusing to replace an existing Shell move recovery root."
}

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
$marker = [ordered]@{
    kind = $ownedMarkerKind
    repositoryRoot = $repoRoot
    scenario = $scenario
    runId = $runId
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
$ownedMarkerPath = Join-Path $DataRoot $ownedMarkerName
$recoveryMarkerPath = Join-Path $recoveryRoot $ownedMarkerName
$marker | ConvertTo-Json | Set-Content -LiteralPath $ownedMarkerPath -Encoding UTF8
$marker | ConvertTo-Json | Set-Content -LiteralPath $recoveryMarkerPath -Encoding UTF8

$dataDirectory = Join-Path $DataRoot "data"
$fixtureRoot = Join-Path $DataRoot "fixtures\shell-move"
$widgetRoot = Join-Path $fixtureRoot "widget-root"
$desktopRoot = Join-Path $fixtureRoot "desktop-root"
$realName = "real-$runId.txt"
$partialFirstName = "partial-first-$runId.txt"
$partialSecondName = "partial-second-$runId.txt"
$cancelName = "cancel-$runId.txt"
$lateName = "late-$runId.txt"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $widgetRoot -Force | Out-Null
New-Item -ItemType Directory -Path $desktopRoot -Force | Out-Null
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
foreach ($entry in @(
        @("baseline.txt", "baseline"),
        @($realName, "real"),
        @($partialFirstName, "partial-first"),
        @($partialSecondName, "partial-second"),
        @($cancelName, "cancel"),
        @($lateName, "late"))) {
    [System.IO.File]::WriteAllText(
        (Join-Path $widgetRoot $entry[0]),
        "$($entry[1]):$runId`n",
        $utf8WithoutBom)
}

$settings = [ordered]@{
    schemaVersion = 9
    language = "en-US"
    autoStart = $false
    autoCheckForUpdates = $false
    globalHotkeyEnabled = $false
    trayIconStyle = "Colorful"
    textSize = 11.5
    fileNameLineCount = 2
    showFileExtensions = $false
    fileWidgetFolderOpenBehavior = "Embedded"
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
    recentOrganizationHistory = @()
    widgets = @(
        [ordered]@{
            id = "aot-5b4c1b2a-file"
            name = "AOT Shell Move Fixture"
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
$settingsPath = Join-Path $dataDirectory "settings.json"
$settings | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8

$scenarioRoot = Join-Path $DataRoot "aot-managed-ui-smoke\shell-move-persistence-restart"
$mutateResultPath = Join-Path $scenarioRoot "mutate\result.json"
$verifyRestoreResultPath = Join-Path $scenarioRoot "verify-restore\result.json"
$postflightResultPath = Join-Path $scenarioRoot "postflight\result.json"
$compensateResultPath = Join-Path $scenarioRoot "compensate\result.json"
$archiveRoot = Join-Path $evidenceRoot "shell-move-persistence-restart-$runId"
$latestSessionPath = Join-Path $evidenceRoot "shell-move-session.json"
$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$initialState = Get-FixtureState -FixtureRoot $fixtureRoot
Assert-FixtureState -State $initialState -Expected "Baseline" -Name "initial-independent-disk"
$previewExecutablePath = $null
$safetyVerified = $false
$previewRootCleaned = $false
$recoveryRootCleaned = $false

try {
    $mutatePhase = Invoke-ShellMovePhase -Phase "Mutate" -ResultPath $mutateResultPath
    $previewExecutablePath = [string]$mutatePhase.session.executablePath
    $mutatedState = Get-FixtureState -FixtureRoot $fixtureRoot
    Assert-FixtureState -State $mutatedState -Expected "Mutated" -Name "mutate-independent-disk"
    Assert-OwnedFileHashes -Initial $initialState -Actual $mutatedState -Name "mutate-independent-hashes"

    $verifyPhase = Invoke-ShellMovePhase -Phase "VerifyRestore" -ResultPath $verifyRestoreResultPath
    $verifyState = Get-FixtureState -FixtureRoot $fixtureRoot
    Assert-FixtureState -State $verifyState -Expected "Baseline" -Name "verify-restore-independent-disk"
    Assert-OwnedFileHashes -Initial $initialState -Actual $verifyState -Name "verify-restore-independent-hashes"

    $postflightPhase = Invoke-ShellMovePhase -Phase "Postflight" -ResultPath $postflightResultPath
    $postflightState = Get-FixtureState -FixtureRoot $fixtureRoot
    Assert-FixtureState -State $postflightState -Expected "Baseline" -Name "postflight-independent-disk"
    Assert-OwnedFileHashes -Initial $initialState -Actual $postflightState -Name "postflight-independent-hashes"
    $safetyVerified = $true

    $processIds = @(
        [int]$mutatePhase.result.processId,
        [int]$verifyPhase.result.processId,
        [int]$postflightPhase.result.processId)
    if (@($processIds | Sort-Object -Unique).Count -ne 3) {
        throw "Shell move matrix did not use three distinct application processes."
    }
    $phaseExecutableHashes = @(
        [string]$mutatePhase.session.executableSha256,
        [string]$verifyPhase.session.executableSha256,
        [string]$postflightPhase.session.executableSha256)
    if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
        throw "Shell move phases did not use one identical audited executable."
    }
    if (@(Get-ExactPreviewProcesses -ExecutablePath $previewExecutablePath).Count -ne 0) {
        throw "Shell move matrix left an audited preview process running."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
        throw "Shell move matrix did not produce its runtime log."
    }
    $runtimeLogLines = @(Get-Content -LiteralPath $runtimeLogPath)
    $runtimeFailureLogLines = @(
        $runtimeLogLines | Where-Object {
            $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
            $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0 -or
            $_.IndexOf("[AotManagedUiSmoke] Failed:", [StringComparison]::Ordinal) -ge 0
        })
    if ($runtimeFailureLogLines.Count -gt 0) {
        throw "Shell move runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
    }
    foreach ($requiredLog in @(
            "[FileTransfer] Shell move start",
            "[FileTransfer] Shell move recovered from pending call",
            "[FileTransfer] Pending shell move call eventually returned")) {
        if (@($runtimeLogLines | Where-Object { $_.Contains($requiredLog) }).Count -eq 0) {
            throw "Shell move runtime log omitted '$requiredLog'."
        }
    }

    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production data changed during the Shell move matrix."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    $archivedMutatePath = Join-Path $archiveRoot "mutate-result.json"
    $archivedVerifyPath = Join-Path $archiveRoot "verify-restore-result.json"
    $archivedPostflightPath = Join-Path $archiveRoot "postflight-result.json"
    $archivedRuntimeLogPath = Join-Path $archiveRoot "DeskBox.log"
    $archivedSettingsPath = Join-Path $archiveRoot "final-settings.json"
    $archivedFixtureRoot = Join-Path $archiveRoot "final-fixture"
    $archivedDiskStatesPath = Join-Path $archiveRoot "disk-states.json"
    $archivedSessionPath = Join-Path $archiveRoot "session.json"
    Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutatePath
    Copy-Item -LiteralPath $verifyRestoreResultPath -Destination $archivedVerifyPath
    Copy-Item -LiteralPath $postflightResultPath -Destination $archivedPostflightPath
    Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
    Copy-Item -LiteralPath $settingsPath -Destination $archivedSettingsPath
    Copy-Item -LiteralPath $fixtureRoot -Destination $archivedFixtureRoot -Recurse
    [ordered]@{
        initial = $initialState
        mutate = $mutatedState
        verifyRestore = $verifyState
        postflight = $postflightState
    } | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $archivedDiskStatesPath -Encoding UTF8

    foreach ($cleanup in @(
            [PSCustomObject]@{
                root = $DataRoot
                marker = $ownedMarkerPath
                kind = "preview"
            },
            [PSCustomObject]@{
                root = $recoveryRoot
                marker = $recoveryMarkerPath
                kind = "recovery"
            })) {
        $resolvedRoot = [System.IO.Path]::GetFullPath([string]$cleanup.root)
        $markerPath = [string]$cleanup.marker
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
            throw "Refusing to clean an unowned Shell move $($cleanup.kind) root."
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        if ([string]$cleanup.kind -ceq "preview") {
            $previewRootCleaned = $true
        }
        else {
            $recoveryRootCleaned = $true
        }
    }

    $session = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        scenario = $scenario
        runId = $runId
        executablePath = $previewExecutablePath
        executableSha256 = $phaseExecutableHashes[0]
        previewDataRoot = $DataRoot
        mutateResultPath = $archivedMutatePath
        verifyRestoreResultPath = $archivedVerifyPath
        postflightResultPath = $archivedPostflightPath
        finalSettingsPath = $archivedSettingsPath
        finalFixtureRoot = $archivedFixtureRoot
        diskStatesPath = $archivedDiskStatesPath
        runtimeLogPath = $archivedRuntimeLogPath
        mutate = $mutatePhase.result.shellMove
        verifyRestore = $verifyPhase.result.shellMove
        postflight = $postflightPhase.result.shellMove
        processIds = $processIds
        phaseExecutableHashes = $phaseExecutableHashes
        naturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyPhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        productionDataFingerprintBefore = $productionBefore.fingerprint
        productionDataFingerprintAfter = $productionAfter.fingerprint
        previewProcessesAfter = 0
        previewRootCleaned = $previewRootCleaned
        recoveryRootCleaned = $recoveryRootCleaned
        runtimeFailureLogLines = $runtimeFailureLogLines
    }
    $sessionJson = $session | ConvertTo-Json -Depth 32
    $sessionJson | Set-Content -LiteralPath ($archivedSessionPath + ".tmp") -Encoding UTF8
    Move-Item -LiteralPath ($archivedSessionPath + ".tmp") -Destination $archivedSessionPath -Force
    $sessionJson | Set-Content -LiteralPath ($latestSessionPath + ".tmp") -Encoding UTF8
    Move-Item -LiteralPath ($latestSessionPath + ".tmp") -Destination $latestSessionPath -Force

    [PSCustomObject]@{
        Scenario = $scenario
        Success = $true
        RunId = $runId
        Exe = $previewExecutablePath
        DataRoot = $DataRoot
        SessionPath = $archivedSessionPath
        LatestSessionPath = $latestSessionPath
        MutateResultPath = $archivedMutatePath
        VerifyRestoreResultPath = $archivedVerifyPath
        PostflightResultPath = $archivedPostflightPath
        FinalSettingsPath = $archivedSettingsPath
        FinalFixtureRoot = $archivedFixtureRoot
        DiskStatesPath = $archivedDiskStatesPath
        ProcessCount = 3
        NaturalExitCount = 3
        RuntimeFailureLogCount = $runtimeFailureLogLines.Count
        ProductionDataFingerprint = $productionAfter.fingerprint
        PreviewRootCleaned = $previewRootCleaned
        RecoveryRootCleaned = $recoveryRootCleaned
        Running = $false
    }
}
catch {
    $primaryFailure = $_
    if (-not $safetyVerified) {
        try {
            $compensationPhase = Invoke-ShellMovePhase `
                -Phase "Compensate" `
                -ResultPath $compensateResultPath
            $previewExecutablePath = [string]$compensationPhase.session.executablePath
            $compensatedState = Get-FixtureState -FixtureRoot $fixtureRoot
            Assert-FixtureState `
                -State $compensatedState `
                -Expected "Baseline" `
                -Name "compensation-independent-disk"
            Assert-OwnedFileHashes `
                -Initial $initialState `
                -Actual $compensatedState `
                -Name "compensation-independent-hashes"
        }
        catch {
            throw "Shell move matrix failed ('$primaryFailure') and independent compensation failed ('$($_)'). The owned preview/recovery roots and run ID '$runId' were preserved for recovery."
        }
    }
    throw $primaryFailure
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($previewExecutablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    }
}
