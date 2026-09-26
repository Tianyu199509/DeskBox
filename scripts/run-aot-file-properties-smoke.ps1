[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "FilePropertiesReadOnly"
$smokeEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SMOKE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID"
$runId = [Guid]::NewGuid().ToString("N")
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcher = Join-Path $PSScriptRoot "start-aot-preview.ps1"
$previewSessionPath = Join-Path `
    $repoRoot `
    ".artifacts\aot-preview\win-x64\session.json"
$auditSummaryPath = Join-Path `
    $repoRoot `
    ".artifacts\aot-audit\win-x64\summary.json"
$evidenceRoot = Join-Path `
    $repoRoot `
    ".artifacts\aot-managed-ui-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-managed-ui-owned.json"
$ownedMarkerKind = "DeskBox.Aot.FilePropertiesSmoke.v1"
$targetName = "properties-$runId.txt"

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
        Stop-Process `
            -Id $process.ProcessId `
            -Force `
            -ErrorAction SilentlyContinue
        Wait-Process `
            -Id $process.ProcessId `
            -Timeout 5 `
            -ErrorAction SilentlyContinue
    }
}

function Wait-NaturalPreviewExit {
    param([string]$ExecutablePath, [int]$Seconds = 20)
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

function Get-OwnedFixtureState {
    param([string]$FixtureRoot)
    $normalizedRoot = [System.IO.Path]::GetFullPath(
        $FixtureRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        throw "Owned file Properties fixture root is missing: '$normalizedRoot'."
    }
    $directories = @(
        Get-ChildItem `
            -LiteralPath $normalizedRoot `
            -Directory `
            -Recurse `
            -Force |
            ForEach-Object {
                $_.FullName.Substring(
                    $normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
            } |
            Sort-Object)
    $files = @(
        Get-ChildItem `
            -LiteralPath $normalizedRoot `
            -File `
            -Recurse `
            -Force |
            ForEach-Object {
                [PSCustomObject]@{
                    relativePath = $_.FullName.Substring(
                        $normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    name = $_.Name
                    length = [long]$_.Length
                    sha256 = (Get-FileHash `
                        -LiteralPath $_.FullName `
                        -Algorithm SHA256).Hash
                }
            } |
            Sort-Object -Property relativePath)
    return [PSCustomObject]@{
        fixtureRoot = $normalizedRoot
        directories = $directories
        files = $files
    }
}

function Assert-OwnedFixtureState {
    param([object]$State, [string]$Name)
    if (@($State.directories).Count -ne 1 -or
        [string]$State.directories[0] -cne "widget-root" -or
        @($State.files).Count -ne 1 -or
        [string]$State.files[0].relativePath -cne "widget-root/$targetName" -or
        [long]$State.files[0].length -le 0 -or
        [string]$State.files[0].sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Name does not contain the exact owned Properties target baseline."
    }
}

function Assert-StringSequence {
    param([object[]]$Actual, [string[]]$Expected, [string]$Name)
    if ($Actual.Count -ne $Expected.Count) {
        throw "$Name count mismatch. Expected $($Expected.Count), got $($Actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ([string]$Actual[$index] -cne $Expected[$index]) {
            throw "$Name mismatch at index $index."
        }
    }
}

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Native AOT preview launcher was not found: '$launcher'."
}
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = $auditSummaryPath
}
$SummaryPath = [System.IO.Path]::GetFullPath($SummaryPath)
if (-not (Test-Path -LiteralPath $SummaryPath -PathType Leaf)) {
    throw "Native AOT audit summary was not found: '$SummaryPath'."
}
$auditSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
if ([int]$auditSummary.schemaVersion -ne 55 -or
    [int]$auditSummary.auditProfileVersion -ne 59 -or
    -not [bool]$auditSummary.sourceStableDuringAudit) {
    throw "File Properties smoke requires stable AOT audit profile 59 / schema 55."
}

New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "file-properties-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$recoveryRoot = [System.IO.Path]::GetFullPath("$DataRoot-Recovery")
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    -not (Test-PathEqualOrInside `
        -Root $evidenceRoot `
        -Candidate $recoveryRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $recoveryRoot) -or
    (Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot) -or
    (Test-PathEqualOrInside `
        -Root $productionDataRoot `
        -Candidate $recoveryRoot) -or
    (Test-PathEqualOrInside `
        -Root $recoveryRoot `
        -Candidate $productionDataRoot)) {
    throw "File Properties preview/recovery roots escaped or overlap production data."
}
if (Test-Path -LiteralPath $DataRoot) {
    throw "Refusing to replace an existing file Properties preview root."
}
if (Test-Path -LiteralPath $recoveryRoot) {
    throw "Refusing to replace an existing file Properties recovery root."
}

$archiveRoot = Join-Path $evidenceRoot "file-properties-read-only-$runId"
if (Test-Path -LiteralPath $archiveRoot) {
    throw "Refusing to replace an existing file Properties archive root."
}
$latestSessionPath = Join-Path $evidenceRoot "file-properties-session.json"
$ownedMarkerPath = Join-Path $DataRoot $ownedMarkerName
$recoveryMarkerPath = Join-Path $recoveryRoot $ownedMarkerName
$dataDirectory = Join-Path $DataRoot "data"
$fixtureRoot = Join-Path $DataRoot "fixtures\file-properties"
$widgetRoot = Join-Path $fixtureRoot "widget-root"
$targetPath = Join-Path $widgetRoot $targetName
$settingsPath = Join-Path $dataDirectory "settings.json"
$resultPath = Join-Path `
    $DataRoot `
    "aot-managed-ui-smoke\file-properties-read-only\result.json"

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $widgetRoot -Force | Out-Null
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
$ownedMarker = [ordered]@{
    kind = $ownedMarkerKind
    repositoryRoot = $repoRoot
    scenario = $scenario
    runId = $runId
    dataRoot = $DataRoot
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
$ownedMarker | ConvertTo-Json |
    Set-Content -LiteralPath $ownedMarkerPath -Encoding UTF8
$recoveryMarker = [ordered]@{
    kind = $ownedMarkerKind
    repositoryRoot = $repoRoot
    scenario = $scenario
    runId = $runId
    recoveryRoot = $recoveryRoot
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
$recoveryMarker | ConvertTo-Json |
    Set-Content -LiteralPath $recoveryMarkerPath -Encoding UTF8
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText(
    $targetPath,
    "DeskBox AOT 5B-4C1B2B file Properties target:$runId`n",
    $utf8WithoutBom)

$settings = [ordered]@{
    schemaVersion = 9
    language = "en-US"
    autoStart = $false
    autoCheckForUpdates = $false
    globalHotkeyEnabled = $false
    trayIconStyle = "Colorful"
    textSize = 11.5
    fileNameLineCount = 2
    showFileExtensions = $true
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
            id = "aot-5b4c1b2b-file"
            name = "AOT File Properties Fixture"
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
            metadata = [ordered]@{
                FolderOpenBehavior = "Embedded"
            }
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

$initialFixture = Get-OwnedFixtureState -FixtureRoot $fixtureRoot
Assert-OwnedFixtureState -State $initialFixture -Name "initial-fixture"
$productionDataFingerprintBefore =
    Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$runSucceeded = $false
$rootCleaned = $false
$recoveryRootCleaned = $false

try {
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
            [Environment]::SetEnvironmentVariable(
                $variable,
                $null,
                "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $smokeEnvironmentVariable,
            $scenario,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $runIdEnvironmentVariable,
            $runId,
            "Process")
        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
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

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "File Properties smoke did not create preview session evidence."
    }
    $previewSession = Get-Content `
        -LiteralPath $previewSessionPath `
        -Raw | ConvertFrom-Json
    $previewExecutablePath = [string]$previewSession.executablePath
    if (-not (Test-PathEqual `
            -Left ([string]$previewSession.previewDataRoot) `
            -Right $DataRoot)) {
        throw "File Properties smoke used the wrong preview root."
    }

    $smokeResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            try {
                $candidate = Get-Content `
                    -LiteralPath $resultPath `
                    -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or
                    $candidate.state -eq "Failed") {
                    $smokeResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during atomic replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $previewExecutablePath
    if (-not $naturalExit) {
        throw "File Properties AOT process did not exit naturally."
    }
    if ($null -eq $smokeResult) {
        throw "File Properties smoke timed out without terminal evidence."
    }
    if ($smokeResult.state -ne "Completed" -or
        -not [bool]$smokeResult.success) {
        throw "File Properties smoke failed: $($smokeResult.error)"
    }
    if ([int]$smokeResult.schemaVersion -ne 1 -or
        [string]$smokeResult.scenario -cne $scenario -or
        [bool]$smokeResult.isDynamicCodeSupported -or
        [int]$smokeResult.processId -ne
            [int]$previewSession.primaryProcessId -or
        -not (Test-PathEqual `
            -Left ([string]$smokeResult.executablePath) `
            -Right $previewExecutablePath) -or
        -not (Test-PathEqual `
            -Left ([string]$smokeResult.previewDataRoot) `
            -Right $DataRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$smokeResult.resultPath) `
            -Right $resultPath)) {
        throw "File Properties result does not match the audited AOT process/root."
    }
    Assert-StringSequence `
        -Actual @($smokeResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4c1b2b-file") `
        -Name "seededWidgetIds"
    Assert-StringSequence `
        -Actual @($smokeResult.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "visibleWidgetKinds"
    if ([int]$smokeResult.loadedSurfaceCount -ne 2 -or
        [int]$smokeResult.visibleSurfaceCount -ne 2 -or
        @($smokeResult.locales).Count -ne 12) {
        throw "File Properties smoke did not restore its two real widget surfaces and locale baseline."
    }

    $properties = $smokeResult.fileProperties
    if ($null -eq $properties -or
        -not [bool]$properties.normalShutdownRequested -or
        [string]$properties.runId -cne $runId -or
        [string]$properties.targetName -cne $targetName -or
        -not (Test-PathEqual `
            -Left ([string]$properties.targetPath) `
            -Right $targetPath) -or
        [long]$properties.targetLengthBefore -le 0 -or
        [long]$properties.targetLengthAfter -ne
            [long]$properties.targetLengthBefore -or
        [string]$properties.targetSha256Before -cne
            [string]$properties.targetSha256After -or
        -not [bool]$properties.targetExistsAfter -or
        [long]$properties.hostWindowHandle -eq 0 -or
        -not [bool]$properties.hostHasXamlRoot -or
        -not [bool]$properties.hostVisible) {
        throw "File Properties owned target or host evidence is incomplete."
    }

    $menu = $properties.menu
    $invocation = $menu.invocation
    $dialog = $menu.dialog
    if (-not [bool]$menu.automationInvoked -or
        -not [bool]$menu.propertiesEnabled -or
        [int]$menu.propertiesIndex -lt 0 -or
        [int]$menu.menuItemCount -le [int]$menu.propertiesIndex -or
        [long]$menu.hostWindowHandle -ne
            [long]$properties.hostWindowHandle -or
        -not [string]::IsNullOrEmpty([string]$menu.feedbackKey) -or
        [int]$menu.remainingMatchingDialogCount -ne 0 -or
        -not [bool]$invocation.resultRecorded -or
        -not [bool]$invocation.invoked -or
        -not [string]::IsNullOrEmpty([string]$invocation.error) -or
        [long]$invocation.ownerWindowHandle -ne
            [long]$properties.hostWindowHandle -or
        -not (Test-PathEqual `
            -Left ([string]$invocation.targetPath) `
            -Right $targetPath) -or
        [long]$dialog.windowHandle -eq 0 -or
        [long]$dialog.expectedOwnerWindowHandle -ne
            [long]$properties.hostWindowHandle -or
        [long]$dialog.expectedOwner.windowHandle -ne
            [long]$properties.hostWindowHandle -or
        -not [bool]$dialog.expectedOwner.isWindow -or
        [long]$dialog.directOwnerWindowHandle -eq 0 -or
        [long]$dialog.directOwnerWindowHandle -eq
            [long]$dialog.windowHandle -or
        [long]$dialog.directOwner.windowHandle -ne
            [long]$dialog.directOwnerWindowHandle -or
        -not [bool]$dialog.directOwner.isWindow -or
        [long]$dialog.rootOwnerWindowHandle -eq 0 -or
        [long]$dialog.rootOwnerWindowHandle -eq
            [long]$dialog.windowHandle -or
        [long]$dialog.rootOwner.windowHandle -ne
            [long]$dialog.rootOwnerWindowHandle -or
        -not [bool]$dialog.rootOwner.isWindow -or
        [string]$dialog.className -cne "#32770" -or
        -not ([string]$dialog.title).Contains(
            $targetName,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [bool]$dialog.visibleBeforeClose -or
        -not [bool]$dialog.closePosted -or
        -not [bool]$dialog.windowDestroyedAfterClose) {
        throw "File Properties menu, SHObjectProperties, owner, dialog, or close evidence is incomplete."
    }

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "FilePropertiesOwnedBaselineVerified",
        "FilePropertiesMenuInvoked",
        "FilePropertiesInvocationVerified",
        "FilePropertiesDialogObserved",
        "FilePropertiesDialogClosed",
        "FilePropertiesPostflightVerified")
    $missingSteps = @(
        $requiredSteps | Where-Object { $_ -notin @($smokeResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "File Properties result is missing steps: $($missingSteps -join ', ')."
    }

    $finalFixture = Get-OwnedFixtureState -FixtureRoot $fixtureRoot
    Assert-OwnedFixtureState -State $finalFixture -Name "final-fixture"
    if ([long]$finalFixture.files[0].length -ne
            [long]$initialFixture.files[0].length -or
        [string]$finalFixture.files[0].sha256 -cne
            [string]$initialFixture.files[0].sha256) {
        throw "The read-only Properties run changed the owned target content."
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
        throw "File Properties smoke did not produce a runtime log."
    }
    $runtimeFailureLogLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf(
                    "Unhandled exception:",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[AotManagedUiSmoke] Failed:",
                    [StringComparison]::Ordinal) -ge 0
            })
    if ($runtimeFailureLogLines.Count -gt 0) {
        throw "File Properties runtime log contains failures: $($runtimeFailureLogLines -join ' | ')"
    }

    $productionDataFingerprintAfter =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionDataFingerprintAfter.fingerprint -cne
            [string]$productionDataFingerprintBefore.fingerprint -or
        [int]$productionDataFingerprintAfter.fileCount -ne
            [int]$productionDataFingerprintBefore.fileCount -or
        [long]$productionDataFingerprintAfter.bytes -ne
            [long]$productionDataFingerprintBefore.bytes) {
        throw "Production data changed during the file Properties smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Copy-Item -LiteralPath $resultPath -Destination (
        Join-Path $archiveRoot "result.json")
    Copy-Item -LiteralPath $settingsPath -Destination (
        Join-Path $archiveRoot "settings.json")
    Copy-Item -LiteralPath $runtimeLogPath -Destination (
        Join-Path $archiveRoot "DeskBox.log")
    $fixtureEvidence = [ordered]@{
        runId = $runId
        initial = $initialFixture
        final = $finalFixture
    }
    $fixtureEvidence | ConvertTo-Json -Depth 12 |
        Set-Content `
            -LiteralPath (Join-Path $archiveRoot "fixture-state.json") `
            -Encoding UTF8

    $session = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        scenario = $scenario
        runId = $runId
        success = $true
        naturalExit = $naturalExit
        processId = [int]$smokeResult.processId
        executablePath = $previewExecutablePath
        executableSha256 = [string]$previewSession.executableSha256
        previewDataRoot = $DataRoot
        recoveryRoot = $recoveryRoot
        resultPath = $resultPath
        archiveRoot = $archiveRoot
        targetName = $targetName
        targetPath = $targetPath
        targetLength = [long]$finalFixture.files[0].length
        targetSha256 = [string]$finalFixture.files[0].sha256
        hostWindowHandle = [long]$properties.hostWindowHandle
        dialogWindowHandle = [long]$dialog.windowHandle
        dialogOwnerWindowHandle = [long]$dialog.directOwnerWindowHandle
        dialogProcessId = [long]$dialog.processId
        dialogClassName = [string]$dialog.className
        dialogTitle = [string]$dialog.title
        dialogClosed = [bool]$dialog.windowDestroyedAfterClose
        expectedOwner = $dialog.expectedOwner
        directOwner = $dialog.directOwner
        rootOwner = $dialog.rootOwner
        recoveryFileCountBeforeCleanup = @(
            Get-ChildItem `
                -LiteralPath $recoveryRoot `
                -File `
                -Recurse `
                -Force).Count
        productionDataFingerprintBefore =
            [string]$productionDataFingerprintBefore.fingerprint
        productionDataFingerprintAfter =
            [string]$productionDataFingerprintAfter.fingerprint
        runtimeFailureLogLines = $runtimeFailureLogLines
        ownedPreviewRootCleaned = $false
        ownedRecoveryRootCleaned = $false
        steps = @($smokeResult.steps)
    }
    $archivedSessionPath = Join-Path $archiveRoot "session.json"

    foreach ($cleanup in @(
            [PSCustomObject]@{
                root = $DataRoot
                markerPath = $ownedMarkerPath
                markerRootProperty = "dataRoot"
                kind = "preview"
            },
            [PSCustomObject]@{
                root = $recoveryRoot
                markerPath = $recoveryMarkerPath
                markerRootProperty = "recoveryRoot"
                kind = "recovery"
            })) {
        $resolvedRoot = [System.IO.Path]::GetFullPath([string]$cleanup.root)
        $marker = Get-Content `
            -LiteralPath ([string]$cleanup.markerPath) `
            -Raw | ConvertFrom-Json
        $markerRoot = [string]$marker.([string]$cleanup.markerRootProperty)
        if ([string]$marker.kind -cne $ownedMarkerKind -or
            [string]$marker.runId -cne $runId -or
            -not (Test-PathEqual `
                -Left ([string]$marker.repositoryRoot) `
                -Right $repoRoot) -or
            -not (Test-PathEqual `
                -Left $markerRoot `
                -Right $resolvedRoot) -or
            -not (Test-PathEqualOrInside `
                -Root $evidenceRoot `
                -Candidate $resolvedRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
            if ([string]$cleanup.kind -ceq "preview") {
                throw "Refusing to clean an unowned file Properties preview root."
            }
            throw "Refusing to clean an unowned file Properties recovery root."
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        if (Test-Path -LiteralPath $resolvedRoot) {
            throw "The owned file Properties $($cleanup.kind) root was not cleaned."
        }
        if ([string]$cleanup.kind -ceq "preview") {
            $rootCleaned = $true
        }
        else {
            $recoveryRootCleaned = $true
        }
    }

    $session['ownedPreviewRootCleaned'] = $rootCleaned
    $session['ownedRecoveryRootCleaned'] = $recoveryRootCleaned
    $sessionJson = $session | ConvertTo-Json -Depth 24
    $sessionJson | Set-Content `
        -LiteralPath ($archivedSessionPath + ".tmp") `
        -Encoding UTF8
    Move-Item `
        -LiteralPath ($archivedSessionPath + ".tmp") `
        -Destination $archivedSessionPath `
        -Force
    $sessionJson | Set-Content `
        -LiteralPath ($latestSessionPath + ".tmp") `
        -Encoding UTF8
    Move-Item `
        -LiteralPath ($latestSessionPath + ".tmp") `
        -Destination $latestSessionPath `
        -Force

    $runSucceeded = $true
    [PSCustomObject]@{
        Scenario = $scenario
        Success = $true
        RunId = $runId
        ProcessId = [int]$smokeResult.processId
        Exe = $previewExecutablePath
        Target = $targetName
        HostWindowHandle = [long]$properties.hostWindowHandle
        DialogWindowHandle = [long]$dialog.windowHandle
        DialogOwnerWindowHandle = [long]$dialog.directOwnerWindowHandle
        DialogTitle = [string]$dialog.title
        NaturalExit = $naturalExit
        RuntimeFailureLogCount = $runtimeFailureLogLines.Count
        ProductionDataFingerprint =
            [string]$productionDataFingerprintAfter.fingerprint
        PreviewRootCleaned = $rootCleaned
        RecoveryRootCleaned = $recoveryRootCleaned
        SessionPath = $latestSessionPath
        ArchiveRoot = $archiveRoot
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($previewExecutablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath $previewExecutablePath
    }
    if (-not $runSucceeded) {
        Write-Warning (
            "File Properties smoke failed. The exact owned preview/recovery " +
            "roots and run ID were preserved: root='$DataRoot' " +
            "recovery='$recoveryRoot' runId='$runId'.")
    }
}
