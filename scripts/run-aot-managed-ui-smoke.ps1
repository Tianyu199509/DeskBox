[CmdletBinding()]
param(
    [string]$SummaryPath,

    [ValidateSet("BasicReadOnly", "DeepSettingsReadOnly", "SettingsWidgetPersistenceRestart", "QuickCapturePersistenceRestart", "TodoPersistenceRestart", "TodoStepsPersistenceRestart", "TodoAttachmentsPersistenceRestart", "TodoRecurrenceReminderPersistenceRestart", "TodoNotificationDisplayCleanup", "TodoNotificationActionRouting", "TodoNotificationEnvelopeForwarding", "TodoNotificationSurfaceRouting", "GlancePersistenceRestart", "WeatherSettingsPersistenceRestart", "WeatherSurfacePersistenceRestart", "LocalFileSurfacePersistenceRestart", "RecycleBinMenuPersistenceRestart", "ShellMovePersistenceRestart", "FilePropertiesReadOnly", "PickerClipboardStorageItemsPersistenceRestart", "NativeDropPersistenceRestart")]
    [string]$Scenario = "BasicReadOnly",

    [string]$DataRoot,

    [ValidateRange(15, 300)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$managedUiSmokeEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SMOKE"
$managedUiPersistencePhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE"
$managedUiQuickCapturePhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE"
$managedUiTodoPhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_TODO_PHASE"
$managedUiTodoStepsPhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE"
$managedUiTodoAttachmentsPhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE"
$managedUiGlancePhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE"
$managedUiGlanceFixtureEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE"
$managedUiWeatherSettingsPhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE"
$managedUiWeatherSurfacePhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE"
$managedUiLocalFilePhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE"
$managedUiRecycleBinPhaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE"
$managedUiRecycleBinRunIdEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_RUN_ID"
$musicSessionMutationSmokeEnvironmentVariable =
    "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE"
$musicMutationSmokeEnvironmentVariable = "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE"
$musicReadSmokeEnvironmentVariable = "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE"
$mutationSmokeEnvironmentVariable = "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE"
$shellSmokeEnvironmentVariable = "DESKBOX_AOT_SHELL_SMOKE"
$shortcutSmokeEnvironmentVariable = "DESKBOX_AOT_SHORTCUT_SMOKE"
$scenario = $Scenario
$recycleBinRunId = if ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
    [Guid]::NewGuid().ToString("N")
}
else {
    $null
}
$ownedMarkerName = ".deskbox-aot-managed-ui-owned.json"
$ownedMarkerKind = "DeskBox.Aot.ManagedUiSmoke.v1"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcher = Join-Path $PSScriptRoot "start-aot-preview.ps1"
$shellMoveRunner = Join-Path $PSScriptRoot "run-aot-shell-move-persistence-smoke.ps1"
$filePropertiesRunner = Join-Path $PSScriptRoot "run-aot-file-properties-smoke.ps1"
$pickerClipboardRunner = Join-Path $PSScriptRoot "run-aot-picker-clipboard-smoke.ps1"
$nativeDropRunner = Join-Path $PSScriptRoot "run-aot-native-drop-smoke.ps1"
$todoRecurrenceReminderRunner = Join-Path `
    $PSScriptRoot `
    "run-aot-todo-recurrence-reminder-smoke.ps1"
$todoNotificationRunner = Join-Path `
    $PSScriptRoot `
    "run-aot-todo-notification-smoke.ps1"
$todoNotificationActivationRunner = Join-Path `
    $PSScriptRoot `
    "run-aot-todo-notification-activation-smoke.ps1"
$todoNotificationForwardingRunner = Join-Path `
    $PSScriptRoot `
    "run-aot-todo-notification-forwarding-smoke.ps1"
$todoNotificationSurfaceRunner = Join-Path `
    $PSScriptRoot `
    "run-aot-todo-notification-surface-smoke.ps1"
$previewSessionPath = Join-Path $repoRoot ".artifacts\aot-preview\win-x64\session.json"
$evidenceRoot = Join-Path $repoRoot ".artifacts\aot-managed-ui-smoke\win-x64"
$defaultDataRoot = Join-Path $evidenceRoot "preview-root"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))

if ($scenario -ceq "ShellMovePersistenceRestart") {
    if (-not (Test-Path -LiteralPath $shellMoveRunner -PathType Leaf)) {
        throw "Shell move persistence runner was not found: '$shellMoveRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $shellMoveRunner @arguments
    return
}

if ($scenario -ceq "FilePropertiesReadOnly") {
    if (-not (Test-Path -LiteralPath $filePropertiesRunner -PathType Leaf)) {
        throw "File Properties runner was not found: '$filePropertiesRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $filePropertiesRunner @arguments
    return
}

if ($scenario -ceq "PickerClipboardStorageItemsPersistenceRestart") {
    if (-not (Test-Path -LiteralPath $pickerClipboardRunner -PathType Leaf)) {
        throw "Picker/StorageItems runner was not found: '$pickerClipboardRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $pickerClipboardRunner @arguments
    return
}

if ($scenario -ceq "NativeDropPersistenceRestart") {
    if (-not (Test-Path -LiteralPath $nativeDropRunner -PathType Leaf)) {
        throw "Native-drop runner was not found: '$nativeDropRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $nativeDropRunner @arguments
    return
}

if ($scenario -ceq "TodoRecurrenceReminderPersistenceRestart") {
    if (-not (Test-Path -LiteralPath $todoRecurrenceReminderRunner -PathType Leaf)) {
        throw "Todo recurrence/reminder runner was not found: '$todoRecurrenceReminderRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $todoRecurrenceReminderRunner @arguments
    return
}

if ($scenario -ceq "TodoNotificationDisplayCleanup") {
    if (-not (Test-Path -LiteralPath $todoNotificationRunner -PathType Leaf)) {
        throw "Todo notification runner was not found: '$todoNotificationRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $todoNotificationRunner @arguments
    return
}

if ($scenario -ceq "TodoNotificationActionRouting") {
    if (-not (Test-Path -LiteralPath $todoNotificationActivationRunner -PathType Leaf)) {
        throw "Todo notification activation runner was not found: '$todoNotificationActivationRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $todoNotificationActivationRunner @arguments
    return
}

if ($scenario -ceq "TodoNotificationEnvelopeForwarding") {
    if (-not (Test-Path -LiteralPath $todoNotificationForwardingRunner -PathType Leaf)) {
        throw "Todo notification forwarding runner was not found: '$todoNotificationForwardingRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $todoNotificationForwardingRunner @arguments
    return
}

if ($scenario -ceq "TodoNotificationSurfaceRouting") {
    if (-not (Test-Path -LiteralPath $todoNotificationSurfaceRunner -PathType Leaf)) {
        throw "Todo notification surface runner was not found: '$todoNotificationSurfaceRunner'."
    }
    $arguments = @{
        SummaryPath = $SummaryPath
        TimeoutSeconds = $TimeoutSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
        $arguments.DataRoot = $DataRoot
    }
    & $todoNotificationSurfaceRunner @arguments
    return
}

function Get-TextSha256 {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot hash missing file '$Path'."
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-DirectoryStateFingerprint {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

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
        $records.Add(
            ("{0}|{1}|{2}" -f @(
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

function Test-PathEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right
    )

    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathEqualOrInside {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    return (Test-PathEqual -Left $normalizedRoot -Right $normalizedCandidate) -or
        $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ExactPreviewProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    return @(
        Get-CimInstance Win32_Process -Filter "Name='DeskBox.exe'" |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
                (Test-PathEqual -Left $_.ExecutablePath -Right $ExecutablePath)
            }
    )
}

function Stop-ExactPreviewProcess {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $processes = @(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath)
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in $processes) {
        Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}

function Assert-StringSequence {
    param(
        [Parameter(Mandatory)]
        [object[]]$Actual,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Actual.Count -ne $Expected.Count) {
        throw "$Name count mismatch. Expected $($Expected.Count), got $($Actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ([string]$Actual[$index] -cne $Expected[$index]) {
            throw "$Name mismatch at index $index. Expected '$($Expected[$index])', got '$($Actual[$index])'."
        }
    }
}

function Get-LocalFileFixtureState {
    param(
        [Parameter(Mandatory)]
        [string]$FixtureRoot
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        throw "The owned local-file fixture root is missing: '$normalizedRoot'."
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

function Assert-LocalFileDiskState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectMutation,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedDirectories = @("sources", "widget-root", "widget-root/nested")
    $expectedFiles = if ($ExpectMutation) {
        @(
            "sources/copy-source.txt",
            "widget-root/baseline.txt",
            "widget-root/copied-renamed.txt",
            "widget-root/move-source.txt",
            "widget-root/nested/nested.txt",
            "widget-root/watcher-created.txt")
    }
    else {
        @(
            "sources/copy-source.txt",
            "sources/move-source.txt",
            "widget-root/baseline.txt",
            "widget-root/nested/nested.txt")
    }

    Assert-StringSequence `
        -Actual @($State.directories) `
        -Expected $expectedDirectories `
        -Name "$Name.directories"
    Assert-StringSequence `
        -Actual @($State.files | ForEach-Object { [string]$_.relativePath }) `
        -Expected $expectedFiles `
        -Name "$Name.files"
    foreach ($file in @($State.files)) {
        if ([long]$file.length -le 0 -or [string]$file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "$Name contains an empty or unhashed file '$($file.relativePath)'."
        }
    }
}

function Assert-LocalFileEvidenceState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectMutation,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedNames = if ($ExpectMutation) {
        @("nested", "baseline", "copied-renamed", "move-source", "watcher-created")
    }
    else {
        @("nested", "baseline")
    }
    $surface = $State.surface
    if (-not [bool]$State.isInitialized -or
        -not [bool]$State.isAtMappedRoot -or
        -not (Test-PathEqual `
            -Left ([string]$State.mappedFolderPath) `
            -Right ([string]$State.currentFolderPath)) -or
        -not [bool]$surface.isLoaded -or
        -not [bool]$surface.hasXamlRoot -or
        -not [bool]$surface.dataContextMatchesViewModel -or
        [double]$surface.actualWidth -le 0 -or
        [double]$surface.actualHeight -le 0 -or
        -not [bool]$surface.viewModelInitialized -or
        -not [bool]$surface.isAtMappedRoot -or
        [bool]$surface.canNavigateUp -or
        [bool]$surface.navigationBarVisible -or
        [string]$surface.navigationBarVisibility -cne "Collapsed" -or
        [string]$surface.viewMode -cne "Icon" -or
        [bool]$surface.emptyStateVisible -or
        -not [bool]$surface.activeViewVisible) {
        throw "$Name did not project the required real File Widget root surface."
    }
    foreach ($property in @(
            "viewModelItemCount", "visibleItemCount", "xamlItemCount",
            "realizedContainerCount", "projectedItemCount")) {
        if ([int]$surface.$property -ne $expectedNames.Count) {
            throw "$Name.$property expected $($expectedNames.Count), got $($surface.$property)."
        }
    }
    Assert-StringSequence `
        -Actual @($surface.items | ForEach-Object { [string]$_.name }) `
        -Expected $expectedNames `
        -Name "$Name.surface.items"
    foreach ($item in @($surface.items)) {
        if (-not [bool]$item.containerRealized -or
            -not [bool]$item.dataContextMatches -or
            -not [bool]$item.nameProjected -or
            [bool]$item.isFolder -ne ([string]$item.name -ceq "nested") -or
            [string]$item.name -cne [string]$item.projectedName -or
            -not (Test-PathEqualOrInside `
                -Root ([string]$State.mappedFolderPath) `
                -Candidate ([string]$item.path))) {
            throw "$Name contains an unprojected or escaped item '$($item.name)'."
        }
    }
    Assert-LocalFileDiskState `
        -State $State.disk `
        -ExpectMutation $ExpectMutation `
        -Name "$Name.disk"
}

function Assert-LocalFileStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    foreach ($property in @(
            "fixtureRoot", "mappedFolderPath", "currentFolderPath",
            "isInitialized", "isAtMappedRoot")) {
        Assert-PersistenceScalarEqual `
            -Expected $Expected.$property `
            -Actual $Actual.$property `
            -Name "$Name.$property"
    }
    foreach ($property in @(
            "isLoaded", "hasXamlRoot", "dataContextMatchesViewModel",
            "viewModelInitialized", "mappedFolderPath", "currentFolderPath",
            "isAtMappedRoot", "canNavigateUp", "navigationBarVisibility",
            "navigationBarVisible", "navigationText", "viewModelItemCount",
            "visibleItemCount", "xamlItemCount", "realizedContainerCount",
            "projectedItemCount", "emptyStateVisible", "activeViewVisible",
            "viewMode")) {
        Assert-PersistenceScalarEqual `
            -Expected $Expected.surface.$property `
            -Actual $Actual.surface.$property `
            -Name "$Name.surface.$property"
    }
    foreach ($property in @("actualWidth", "actualHeight")) {
        Assert-PersistenceNumberEqual `
            -Expected ([double]$Expected.surface.$property) `
            -Actual ([double]$Actual.surface.$property) `
            -Name "$Name.surface.$property" `
            -Tolerance 2
    }

    $expectedItems = @($Expected.surface.items)
    $actualItems = @($Actual.surface.items)
    if ($expectedItems.Count -ne $actualItems.Count) {
        throw "$Name.surface.items count mismatch."
    }
    for ($index = 0; $index -lt $expectedItems.Count; $index++) {
        foreach ($property in @(
                "name", "path", "isFolder", "containerRealized",
                "dataContextMatches", "projectedName", "nameProjected")) {
            Assert-PersistenceScalarEqual `
                -Expected $expectedItems[$index].$property `
                -Actual $actualItems[$index].$property `
                -Name "$Name.surface.items[$index].$property"
        }
    }

    Assert-StringSequence `
        -Actual @($Actual.disk.directories) `
        -Expected @($Expected.disk.directories) `
        -Name "$Name.disk.directories"
    $expectedFiles = @($Expected.disk.files)
    $actualFiles = @($Actual.disk.files)
    if ($expectedFiles.Count -ne $actualFiles.Count) {
        throw "$Name.disk.files count mismatch."
    }
    for ($index = 0; $index -lt $expectedFiles.Count; $index++) {
        foreach ($property in @("relativePath", "length", "sha256")) {
            Assert-PersistenceScalarEqual `
                -Expected $expectedFiles[$index].$property `
                -Actual $actualFiles[$index].$property `
                -Name "$Name.disk.files[$index].$property"
        }
    }
}

function Assert-RecycleBinDiskState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectOwnedOnDisk,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedDirectories = if ($ExpectOwnedOnDisk) {
        @("widget-root", "widget-root/$recycleBinMultiFolderName")
    }
    else {
        @("widget-root")
    }
    $expectedFiles = if ($ExpectOwnedOnDisk) {
        @(
            "widget-root/baseline",
            "widget-root/$recycleBinMultiFileName",
            "widget-root/$recycleBinMultiFolderName/$recycleBinFolderPayloadName",
            "widget-root/$recycleBinSingleName")
    }
    else {
        @("widget-root/baseline")
    }
    Assert-StringSequence `
        -Actual @($State.directories) `
        -Expected $expectedDirectories `
        -Name "$Name.directories"
    Assert-StringSequence `
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

function Assert-RecycleBinEvidenceState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectOwnedOnDisk,

        [Parameter(Mandatory)]
        [int]$ExpectedRecycleMatches,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedNames = if ($ExpectOwnedOnDisk) {
        @(
            $recycleBinMultiFolderName,
            "baseline",
            $recycleBinMultiFileName,
            $recycleBinSingleName)
    }
    else {
        @("baseline")
    }
    $expectedNameCount = @($expectedNames).Count
    $surface = $State.surface
    if (-not [bool]$surface.isLoaded -or
        -not [bool]$surface.hasXamlRoot -or
        -not [bool]$surface.dataContextMatchesViewModel -or
        -not [bool]$surface.viewModelInitialized -or
        -not [bool]$surface.isAtMappedRoot -or
        [bool]$surface.canNavigateUp -or
        [bool]$surface.navigationBarVisible -or
        [string]$surface.navigationBarVisibility -cne "Collapsed" -or
        [string]$surface.viewMode -cne "Icon" -or
        [bool]$surface.emptyStateVisible -or
        -not [bool]$surface.activeViewVisible -or
        -not (Test-PathEqual `
            -Left ([string]$State.mappedFolderPath) `
            -Right $recycleBinWidgetRoot)) {
        throw "$Name did not project the required Recycle Bin File Widget surface."
    }
    foreach ($property in @(
            "viewModelItemCount", "visibleItemCount", "xamlItemCount",
            "realizedContainerCount", "projectedItemCount")) {
        if ([int]$surface.$property -ne $expectedNameCount) {
            throw "$Name.$property expected $expectedNameCount, got $($surface.$property)."
        }
    }
    Assert-StringSequence `
        -Actual @($surface.items | ForEach-Object { [string]$_.name }) `
        -Expected $expectedNames `
        -Name "$Name.surface.items"

    if (-not [bool]$State.disk.baseline.exists -or
        [long]$State.disk.baseline.length -le 0 -or
        [string]$State.disk.baseline.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        @($State.disk.ownedItems).Count -ne 3 -or
        @($State.nativeQueries).Count -ne 3) {
        throw "$Name did not retain complete disk and native query evidence."
    }
    foreach ($entry in @($State.disk.ownedItems)) {
        if ([bool]$entry.exists -ne $ExpectOwnedOnDisk) {
            throw "$Name owned disk entry '$($entry.name)' has the wrong existence state."
        }
    }
    if ([bool]$State.disk.folderPayload.exists -ne $ExpectOwnedOnDisk) {
        throw "$Name folder payload has the wrong existence state."
    }
    foreach ($query in @($State.nativeQueries)) {
        if (-not [bool]$query.success -or
            [string]$query.operation -cne "Query" -or
            [int]$query.matchedCount -ne $ExpectedRecycleMatches -or
            [int]$query.restoredCount -ne 0 -or
            [int]$query.attemptedPhases -eq 0) {
            throw "$Name exact native query failed for '$($query.name)'."
        }
    }
}

function Assert-RecycleBinMenuEvidence {
    param(
        [Parameter(Mandatory)]
        [object]$Menu,

        [Parameter(Mandatory)]
        [int]$ExpectedSelectionCount,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ([bool]$Menu.multiSelection -ne ($ExpectedSelectionCount -gt 1) -or
        @($Menu.selectedNames).Count -ne $ExpectedSelectionCount -or
        @($Menu.selectedPaths).Count -ne $ExpectedSelectionCount -or
        [int]$Menu.menuItemCount -le 0 -or
        [int]$Menu.deleteIndex -ne ([int]$Menu.menuItemCount - 1) -or
        [string]::IsNullOrWhiteSpace([string]$Menu.deleteText) -or
        -not [bool]$Menu.deleteEnabled -or
        -not [bool]$Menu.automationInvoked -or
        [string]$Menu.feedbackKey -cne "file-delete" -or
        [string]$Menu.feedbackSeverity -cne "Success" -or
        [string]::IsNullOrWhiteSpace([string]$Menu.feedbackMessage) -or
        @($Menu.items).Count -ne [int]$Menu.menuItemCount -or
        @($Menu.items | Where-Object { [bool]$_.isDelete }).Count -ne 1) {
        throw "$Name did not prove the enabled menu shape, Invoke route, and success feedback."
    }
}

function Assert-PersistenceScalarEqual {
    param(
        [AllowNull()]
        [object]$Expected,

        [AllowNull()]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Expected -and $null -eq $Actual) {
        return
    }
    if ($null -eq $Expected -or $null -eq $Actual -or
        [string]$Expected -cne [string]$Actual) {
        throw "$Name mismatch. Expected '$Expected', got '$Actual'."
    }
}

function Assert-PersistenceNumberEqual {
    param(
        [double]$Expected,
        [double]$Actual,
        [string]$Name,
        [double]$Tolerance = 0.001
    )

    if ([Math]::Abs($Expected - $Actual) -gt $Tolerance) {
        throw "$Name mismatch. Expected '$Expected', got '$Actual', tolerance '$Tolerance'."
    }
}

function Assert-PersistenceWidgetEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    foreach ($property in @(
            "id", "name", "widgetKind", "viewMode", "isVisible", "isDisabled",
            "isPositionLocked", "isSizeLocked", "positionAnchor",
            "positionMonitorKey", "positionMonitorDeviceName",
            "positionMonitorWasPrimary", "boundsCoordinateVersion",
            "hasBaselineMetadata", "isLoaded", "isHostVisible", "hasXamlRoot",
            "viewModelName", "viewModelViewMode", "viewModelPositionLocked",
            "viewModelSizeLocked")) {
        Assert-PersistenceScalarEqual `
            -Expected $Expected.$property `
            -Actual $Actual.$property `
            -Name "$Name.$property"
    }

    foreach ($property in @(
            "x", "y", "width", "height", "positionMarginX", "positionMarginY")) {
        Assert-PersistenceNumberEqual `
            -Expected ([double]$Expected.$property) `
            -Actual ([double]$Actual.$property) `
            -Name "$Name.$property"
    }
    foreach ($property in @("x", "y", "width", "height")) {
        Assert-PersistenceNumberEqual `
            -Expected ([double]$Expected.actualBounds.$property) `
            -Actual ([double]$Actual.actualBounds.$property) `
            -Name "$Name.actualBounds.$property" `
            -Tolerance 2
    }

    if ([long]$Actual.windowHandle -eq 0) {
        throw "$Name.windowHandle did not identify a live HWND."
    }
}

function Assert-PersistenceStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    foreach ($property in @(
            "showFileExtensions", "fileNameLineCount", "trayIconStyle",
            "viewModelShowFileExtensions", "viewModelFileNameLineCount",
            "viewModelTrayIconStyle")) {
        Assert-PersistenceScalarEqual `
            -Expected $Expected.$property `
            -Actual $Actual.$property `
            -Name "$Name.$property"
    }
    foreach ($property in @("textSize", "viewModelTextSize")) {
        Assert-PersistenceNumberEqual `
            -Expected ([double]$Expected.$property) `
            -Actual ([double]$Actual.$property) `
            -Name "$Name.$property"
    }

    Assert-PersistenceWidgetEqual `
        -Expected $Expected.fileWidget `
        -Actual $Actual.fileWidget `
        -Name "$Name.fileWidget"
    Assert-PersistenceWidgetEqual `
        -Expected $Expected.searchWidget `
        -Actual $Actual.searchWidget `
        -Name "$Name.searchWidget"
}

function Assert-QuickCaptureStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedJson = $Expected | ConvertTo-Json -Depth 20 -Compress
    $actualJson = $Actual | ConvertTo-Json -Depth 20 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Name Quick Capture state mismatch. Expected '$expectedJson', got '$actualJson'."
    }
}

function Assert-TodoStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedJson = $Expected | ConvertTo-Json -Depth 20 -Compress
    $actualJson = $Actual | ConvertTo-Json -Depth 20 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Name Todo state mismatch. Expected '$expectedJson', got '$actualJson'."
    }
}

function Assert-GlanceStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedComparable = [ordered]@{
        store = $Expected.store
        viewModel = $Expected.viewModel
    }
    $actualComparable = [ordered]@{
        store = $Actual.store
        viewModel = $Actual.viewModel
    }
    $expectedJson = $expectedComparable | ConvertTo-Json -Depth 20 -Compress
    $actualJson = $actualComparable | ConvertTo-Json -Depth 20 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Name Glance persisted state mismatch. Expected '$expectedJson', got '$actualJson'."
    }
}

function Assert-GlanceEvidenceState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectMutation,

        [Parameter(Mandatory)]
        [string]$FixturePath,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $store = $State.store
    $viewModel = $State.viewModel
    $surface = $State.surface
    $commonStoreValid =
        [int]$store.version -eq 10 -and
        [string]$store.backgroundSource -ceq "LocalFiles" -and
        $null -eq $store.localFolderPath -and
        [bool]$store.showTime -and
        [bool]$store.showDate -and
        -not [bool]$store.showCalendar -and
        -not [bool]$store.randomOrder -and
        [bool]$store.showPhotoControls
    $commonViewModelValid =
        [string]$viewModel.backgroundSource -ceq "LocalFiles" -and
        [bool]$viewModel.showTime -and
        [bool]$viewModel.showDate -and
        -not [bool]$viewModel.showCalendar -and
        -not [bool]$viewModel.randomOrder -and
        [bool]$viewModel.showPhotoControlsSetting
    $commonSurfaceValid =
        [bool]$surface.isLoaded -and
        [bool]$surface.hasXamlRoot -and
        [bool]$surface.dataContextMatchesViewModel -and
        [double]$surface.actualWidth -gt 0 -and
        [double]$surface.actualHeight -gt 0
    if (-not $commonStoreValid -or
        -not $commonViewModelValid -or
        -not $commonSurfaceValid) {
        throw "$Name Glance state is missing its common store, ViewModel, or real-surface evidence."
    }

    if ($ExpectMutation) {
        $storePaths = @($store.localImagePaths)
        $viewModelPaths = @($viewModel.localImagePaths)
        if ($storePaths.Count -ne 1 -or
            $viewModelPaths.Count -ne 1 -or
            -not (Test-PathEqual -Left ([string]$storePaths[0]) -Right $FixturePath) -or
            -not (Test-PathEqual -Left ([string]$viewModelPaths[0]) -Right $FixturePath) -or
            -not [bool]$store.showYear -or
            [bool]$store.showWeekday -or
            [string]$store.layout -cne "Editorial" -or
            [double]$store.rotationIntervalMinutes -ne 0 -or
            [string]$store.transition -cne "None" -or
            [string]$store.transitionSpeed -cne "Fast" -or
            [string]$store.readability -cne "Strong" -or
            -not [bool]$viewModel.showYear -or
            [bool]$viewModel.showWeekday -or
            [string]$viewModel.layout -cne "Editorial" -or
            [double]$viewModel.rotationIntervalMinutes -ne 0 -or
            [string]$viewModel.transition -cne "None" -or
            [string]$viewModel.transitionSpeed -cne "Fast" -or
            [string]$viewModel.readability -cne "Strong" -or
            [int]$viewModel.imageCount -ne 1 -or
            -not [bool]$viewModel.hasCurrentImage -or
            -not (Test-PathEqual -Left ([string]$viewModel.currentImagePath) -Right $FixturePath) -or
            [bool]$viewModel.isCenteredLayout -or
            -not [bool]$viewModel.isEditorialLayout -or
            [Math]::Abs([double]$viewModel.readabilityOpacity - 0.5) -gt 0.001 -or
            -not [bool]$viewModel.showPhotoControls -or
            -not (Test-PathEqual -Left ([string]$surface.decodedImagePath) -Right $FixturePath) -or
            -not [bool]$surface.activeBackgroundIsImageBrush -or
            -not (Test-PathEqual -Left ([string]$surface.activeImageUri) -Right $FixturePath) -or
            [double]$surface.activeBackgroundOpacity -lt 0.99 -or
            [string]$surface.imageStretch -cne "UniformToFill" -or
            [string]$surface.imageAlignmentX -cne "Center" -or
            [string]$surface.imageAlignmentY -cne "Center" -or
            [bool]$surface.immersiveLayoutVisible -or
            [bool]$surface.centeredLayoutVisible -or
            -not [bool]$surface.editorialLayoutVisible -or
            [bool]$surface.calendarLayoutVisible -or
            -not [bool]$surface.readabilityLayerVisible -or
            [Math]::Abs([double]$surface.readabilityLayerOpacity - 0.5) -gt 0.001 -or
            -not [bool]$surface.actionLayerVisible) {
            throw "$Name Glance mutation did not reach the store, ViewModel, decoded image brush, and editorial surface."
        }
    }
    elseif (@($store.localImagePaths).Count -ne 0 -or
        @($viewModel.localImagePaths).Count -ne 0 -or
        [bool]$store.showYear -or
        -not [bool]$store.showWeekday -or
        [string]$store.layout -cne "Centered" -or
        [double]$store.rotationIntervalMinutes -ne 30 -or
        [string]$store.transition -cne "CrossFade" -or
        [string]$store.transitionSpeed -cne "Standard" -or
        [string]$store.readability -cne "Soft" -or
        [bool]$viewModel.showYear -or
        -not [bool]$viewModel.showWeekday -or
        [string]$viewModel.layout -cne "Centered" -or
        [double]$viewModel.rotationIntervalMinutes -ne 30 -or
        [string]$viewModel.transition -cne "CrossFade" -or
        [string]$viewModel.transitionSpeed -cne "Standard" -or
        [string]$viewModel.readability -cne "Soft" -or
        [int]$viewModel.imageCount -ne 0 -or
        $null -ne $viewModel.currentImagePath -or
        [bool]$viewModel.hasCurrentImage -or
        -not [bool]$viewModel.isCenteredLayout -or
        [bool]$viewModel.isEditorialLayout -or
        [Math]::Abs([double]$viewModel.readabilityOpacity - 0.28) -gt 0.001 -or
        [bool]$viewModel.showPhotoControls -or
        $null -ne $surface.decodedImagePath -or
        [bool]$surface.backgroundAHasBrush -or
        [bool]$surface.backgroundBHasBrush -or
        $null -ne $surface.activeImageUri -or
        [bool]$surface.immersiveLayoutVisible -or
        -not [bool]$surface.centeredLayoutVisible -or
        [bool]$surface.editorialLayoutVisible -or
        [bool]$surface.calendarLayoutVisible -or
        [bool]$surface.readabilityLayerVisible -or
        [bool]$surface.actionLayerVisible) {
        throw "$Name Glance baseline is not clean across the store, ViewModel, and real surface."
    }
}

function Assert-WeatherSettingsStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedJson = $Expected | ConvertTo-Json -Depth 12 -Compress
    $actualJson = $Actual | ConvertTo-Json -Depth 12 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Name Weather settings state mismatch. Expected '$expectedJson', got '$actualJson'."
    }
}

function Assert-WeatherSettingsEvidenceState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectMutation,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $widget = $State.widget
    $commonValid =
        -not [bool]$State.autoLocation -and
        [string]$State.dataSource -ceq "MSN" -and
        [string]$widget.id -ceq "aot-5b4b2c2a-weather" -and
        [string]$widget.widgetKind -ceq "Weather" -and
        [bool]$widget.isVisible -and
        -not [bool]$widget.isDisabled -and
        -not [bool]$widget.featureEnabled -and
        [bool]$widget.hasViewModeOverride -and
        -not [bool]$widget.isLoaded -and
        [long]$widget.windowHandle -eq 0 -and
        -not [bool]$widget.isHostVisible -and
        -not [bool]$widget.hasXamlRoot
    if (-not $commonValid) {
        throw "$Name did not prove a local-only fixed Weather configuration with no host."
    }

    if ($ExpectMutation) {
        if ([string]$State.cityName -cne "Chengdu AOT Mutation" -or
            [Math]::Abs([double]$State.latitude - 30.5728) -gt 0.000001 -or
            [Math]::Abs([double]$State.longitude - 104.0668) -gt 0.000001 -or
            [string]$State.temperatureUnit -cne "Fahrenheit" -or
            [string]$State.windSpeedUnit -cne "mph" -or
            [string]$State.defaultView -cne "Today" -or
            [string]$State.skin -cne "Standard" -or
            [bool]$State.showForecast -or
            [bool]$State.showSunrise -or
            [bool]$State.showUvIndex -or
            [bool]$State.showPrecipitation -or
            [bool]$State.showHumidity -or
            [bool]$State.showWind -or
            -not [bool]$State.showPressure -or
            [int]$State.refreshIntervalMinutes -ne 15 -or
            -not [bool]$widget.useWeekView -or
            [string]$widget.metadataValue -cne "Week") {
            throw "$Name Weather settings mutation is incomplete."
        }
    }
    elseif ([string]$State.cityName -cne "Shanghai AOT Baseline" -or
        [Math]::Abs([double]$State.latitude - 31.2304) -gt 0.000001 -or
        [Math]::Abs([double]$State.longitude - 121.4737) -gt 0.000001 -or
        [string]$State.temperatureUnit -cne "Celsius" -or
        [string]$State.windSpeedUnit -cne "kmh" -or
        [string]$State.defaultView -cne "Week" -or
        [string]$State.skin -cne "Rich" -or
        -not [bool]$State.showForecast -or
        -not [bool]$State.showSunrise -or
        -not [bool]$State.showUvIndex -or
        -not [bool]$State.showPrecipitation -or
        -not [bool]$State.showHumidity -or
        -not [bool]$State.showWind -or
        [bool]$State.showPressure -or
        [int]$State.refreshIntervalMinutes -ne 60 -or
        [bool]$widget.useWeekView -or
        [string]$widget.metadataValue -cne "Day") {
        throw "$Name Weather settings baseline is incomplete."
    }
}

function Assert-WeatherSurfaceStateEqual {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,

        [Parameter(Mandatory)]
        [object]$Actual,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $expectedPersistent = [ordered]@{
        autoLocation = [bool]$Expected.autoLocation
        cityName = [string]$Expected.cityName
        latitude = [double]$Expected.latitude
        longitude = [double]$Expected.longitude
        temperatureUnit = [string]$Expected.temperatureUnit
        windSpeedUnit = [string]$Expected.windSpeedUnit
        dataSource = [string]$Expected.dataSource
        skin = [string]$Expected.skin
        showForecast = [bool]$Expected.showForecast
        showSunrise = [bool]$Expected.showSunrise
        showUvIndex = [bool]$Expected.showUvIndex
        showPrecipitation = [bool]$Expected.showPrecipitation
        showHumidity = [bool]$Expected.showHumidity
        showWind = [bool]$Expected.showWind
        showPressure = [bool]$Expected.showPressure
        refreshIntervalMinutes = [int]$Expected.refreshIntervalMinutes
        widget = $Expected.widget
    }
    $actualPersistent = [ordered]@{
        autoLocation = [bool]$Actual.autoLocation
        cityName = [string]$Actual.cityName
        latitude = [double]$Actual.latitude
        longitude = [double]$Actual.longitude
        temperatureUnit = [string]$Actual.temperatureUnit
        windSpeedUnit = [string]$Actual.windSpeedUnit
        dataSource = [string]$Actual.dataSource
        skin = [string]$Actual.skin
        showForecast = [bool]$Actual.showForecast
        showSunrise = [bool]$Actual.showSunrise
        showUvIndex = [bool]$Actual.showUvIndex
        showPrecipitation = [bool]$Actual.showPrecipitation
        showHumidity = [bool]$Actual.showHumidity
        showWind = [bool]$Actual.showWind
        showPressure = [bool]$Actual.showPressure
        refreshIntervalMinutes = [int]$Actual.refreshIntervalMinutes
        widget = $Actual.widget
    }
    $expectedJson = $expectedPersistent | ConvertTo-Json -Depth 12 -Compress
    $actualJson = $actualPersistent | ConvertTo-Json -Depth 12 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Name Weather surface persisted state mismatch. Expected '$expectedJson', got '$actualJson'."
    }
}

function Assert-WeatherSurfaceEvidenceState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [bool]$ExpectMutation,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $widget = $State.widget
    $surface = $State.surface
    $compactSurface = $State.compactSurface
    $commonValid =
        -not [bool]$State.autoLocation -and
        [string]$State.cityName -ceq "Shanghai AOT Surface" -and
        [Math]::Abs([double]$State.latitude - 31.2304) -le 0.000001 -and
        [Math]::Abs([double]$State.longitude - 121.4737) -le 0.000001 -and
        [string]$State.dataSource -ceq "MSN" -and
        [bool]$State.showForecast -and
        [bool]$State.showSunrise -and
        [bool]$State.showPrecipitation -and
        [bool]$State.showHumidity -and
        [bool]$State.showWind -and
        [int]$State.refreshIntervalMinutes -eq 60 -and
        [string]$widget.id -ceq "aot-5b4b2c2b-weather" -and
        [string]$widget.widgetKind -ceq "Weather" -and
        [bool]$widget.isVisible -and
        -not [bool]$widget.isDisabled -and
        [bool]$widget.featureEnabled -and
        [bool]$widget.hasViewModeOverride -and
        [bool]$surface.isLoaded -and
        [bool]$surface.hasXamlRoot -and
        [bool]$surface.dataContextMatchesViewModel -and
        [double]$surface.actualWidth -gt 0 -and
        [double]$surface.actualHeight -gt 0 -and
        [bool]$surface.hasData -and
        [string]$surface.layoutMode -ceq "Expanded" -and
        [bool]$surface.expandedLayoutVisible -and
        [bool]$surface.loadingOverlayHidden -and
        [string]$surface.locationDisplay -ceq "Shanghai AOT Surface" -and
        [string]$surface.surfaceLocationText -ceq [string]$surface.locationDisplay -and
        -not [string]::IsNullOrWhiteSpace([string]$surface.currentDescription) -and
        [string]$surface.surfaceDescriptionText -ceq [string]$surface.currentDescription -and
        [string]$surface.humidityValueText -ceq "64%" -and
        [string]$surface.surfaceHumidityValueText -ceq "64%" -and
        [string]$surface.precipitationValueText -ceq "70%" -and
        [string]$surface.surfacePrecipitationValueText -ceq "70%" -and
        [string]$surface.uvIndexValueText -ceq "5" -and
        [string]$surface.surfaceUvIndexValueText -ceq "5" -and
        [string]$surface.pressureValueText -ceq "1012 hPa" -and
        [string]$surface.surfacePressureValueText -ceq "1012 hPa" -and
        [int]$surface.hourlyViewModelCount -eq 24 -and
        [int]$surface.dailyViewModelCount -eq 7 -and
        [bool]$compactSurface.isLoaded -and
        [bool]$compactSurface.hasXamlRoot -and
        [bool]$compactSurface.dataContextMatchesViewModel -and
        [double]$compactSurface.actualWidth -gt 0 -and
        [double]$compactSurface.actualWidth -lt [double]$surface.actualWidth -and
        [double]$compactSurface.actualHeight -gt 0 -and
        [bool]$compactSurface.hasData -and
        [string]$compactSurface.layoutMode -ceq "Compact" -and
        -not [bool]$compactSurface.miniLayoutVisible -and
        [bool]$compactSurface.compactLayoutVisible -and
        -not [bool]$compactSurface.expandedLayoutVisible -and
        [bool]$compactSurface.loadingOverlayHidden -and
        [string]$compactSurface.locationDisplay -ceq "Shanghai AOT Surface" -and
        [string]$compactSurface.surfaceLocationText -ceq "Shanghai AOT Surface" -and
        [string]$compactSurface.currentDescription -ceq [string]$surface.currentDescription -and
        [string]$compactSurface.surfaceDescriptionText -ceq [string]$surface.currentDescription -and
        [string]$compactSurface.surfaceHumidityValueText -ceq "64%" -and
        [string]$compactSurface.surfacePrecipitationValueText -ceq "70%"
    if (-not $commonValid) {
        throw "$Name did not prove the deterministic WeatherData, real HWND/XamlRoot, and non-empty surface."
    }

    if ($ExpectMutation) {
        if ([string]$State.temperatureUnit -cne "Fahrenheit" -or
            [string]$State.windSpeedUnit -cne "mph" -or
            [string]$State.skin -cne "Standard" -or
            [bool]$State.showUvIndex -or
            [bool]$State.showPressure -or
            -not [bool]$widget.useWeekView -or
            [string]$widget.metadataValue -cne "Week" -or
            -not [bool]$surface.isWeekView -or
            [int]$surface.selectedViewIndex -ne 1 -or
            [bool]$surface.richBackdropVisible -or
            [bool]$surface.uvMetricVisible -or
            [bool]$surface.pressureMetricVisible -or
            [bool]$surface.hourlyForecastVisible -or
            -not [bool]$surface.weekForecastVisible -or
            [string]$surface.currentTemperatureText -cne "68°F" -or
            [string]$surface.surfaceTemperatureText -cne "68°F" -or
            [string]$surface.windValueText -cne "11.2 mph" -or
            -not ([string]$surface.surfaceWindText).StartsWith("11.2 mph", [StringComparison]::Ordinal) -or
            [int]$surface.dailyItemsCount -ne 7 -or
            -not [bool]$surface.dailyContainerRealized -or
            -not [bool]$surface.dailyTemplateTextProjected -or
            [string]$surface.firstDailyMaxText -cne "75°F" -or
            [string]$surface.surfaceFirstDailyMaxText -cne "75°F" -or
            [string]$surface.firstDailyMinText -cne "61°F" -or
            [string]$surface.surfaceFirstDailyMinText -cne "61°F" -or
            [bool]$compactSurface.richBackdropVisible -or
            [string]$compactSurface.currentTemperatureText -cne "68°F" -or
            [string]$compactSurface.surfaceTemperatureText -cne "68°F" -or
            [string]$compactSurface.surfaceWindValueText -cne "11.2 mph") {
            throw "$Name Weather surface mutation is incomplete."
        }
    }
    elseif ([string]$State.temperatureUnit -cne "Celsius" -or
        [string]$State.windSpeedUnit -cne "kmh" -or
        [string]$State.skin -cne "Rich" -or
        -not [bool]$State.showUvIndex -or
        -not [bool]$State.showPressure -or
        [bool]$widget.useWeekView -or
        [string]$widget.metadataValue -cne "Day" -or
        [bool]$surface.isWeekView -or
        [int]$surface.selectedViewIndex -ne 0 -or
        -not [bool]$surface.richBackdropVisible -or
        -not [bool]$surface.uvMetricVisible -or
        -not [bool]$surface.pressureMetricVisible -or
        -not [bool]$surface.hourlyForecastVisible -or
        [bool]$surface.weekForecastVisible -or
        [string]$surface.currentTemperatureText -cne "20°C" -or
        [string]$surface.surfaceTemperatureText -cne "20°C" -or
        [string]$surface.windValueText -cne "18 km/h" -or
        -not ([string]$surface.surfaceWindText).StartsWith("18 km/h", [StringComparison]::Ordinal) -or
        [int]$surface.hourlyItemsCount -ne 24 -or
        -not [bool]$surface.hourlyContainerRealized -or
        -not [bool]$surface.hourlyTemplateTextProjected -or
        [string]$surface.firstHourlyTemperatureText -cne "20°C" -or
        [string]$surface.surfaceFirstHourlyTemperatureText -cne "20°C" -or
        -not [bool]$compactSurface.richBackdropVisible -or
        [string]$compactSurface.currentTemperatureText -cne "20°C" -or
        [string]$compactSurface.surfaceTemperatureText -cne "20°C" -or
        [string]$compactSurface.surfaceWindValueText -cne "18 km/h") {
        throw "$Name Weather surface baseline is incomplete."
    }
}

function Wait-NaturalPreviewExit {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,

        [ValidateRange(1, 60)]
        [int]$TimeoutSeconds = 20
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath).Count -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }

    return @(Get-ExactPreviewProcesses -ExecutablePath $ExecutablePath).Count -eq 0
}

function Invoke-PersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")

    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "SettingsWidgetPersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $Phase,
            "Process")
        foreach ($variable in @(
                $musicSessionMutationSmokeEnvironmentVariable,
                $musicMutationSmokeEnvironmentVariable,
                $musicReadSmokeEnvironmentVariable,
                $mutationSmokeEnvironmentVariable,
                $shellSmokeEnvironmentVariable,
                $shortcutSmokeEnvironmentVariable)) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Persistence phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Persistence phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Persistence phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Persistence phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Persistence phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "SettingsWidgetPersistenceRestart" -or
        [string]$phaseResult.persistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.persistence.flushSucceeded -or
        -not [bool]$phaseResult.persistence.normalShutdownRequested) {
        throw "Persistence phase '$Phase' did not prove its AOT phase, flush, and normal shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Persistence phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2) {
        throw "Persistence phase '$Phase' did not restore the tray and two fixed widget HWNDs."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-file", "aot-5b4a-search") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "SettingsPersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" { @("PersistenceBaselineCaptured", "PersistenceMutationApplied") }
        "VerifyRestore" { @("PersistenceRestartVerified", "PersistenceBaselineRestored") }
        default { @("PersistencePostflightVerified", "PersistenceBaselineRestored") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Persistence phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-QuickCapturePersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyDelete", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousQuickCapturePhase = [Environment]::GetEnvironmentVariable(
        $managedUiQuickCapturePhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")

    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "QuickCapturePersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $Phase,
            "Process")
        foreach ($variable in @(
                $musicSessionMutationSmokeEnvironmentVariable,
                $musicMutationSmokeEnvironmentVariable,
                $musicReadSmokeEnvironmentVariable,
                $mutationSmokeEnvironmentVariable,
                $shellSmokeEnvironmentVariable,
                $shortcutSmokeEnvironmentVariable)) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $previousQuickCapturePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Quick Capture phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Quick Capture phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Quick Capture phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Quick Capture phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Quick Capture phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "QuickCapturePersistenceRestart" -or
        [string]$phaseResult.quickCapturePersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.quickCapturePersistence.normalShutdownRequested) {
        throw "Quick Capture phase '$Phase' did not prove its AOT phase and normal shutdown contract."
    }
    if ($Phase -ceq "Mutate" -and
        (-not [bool]$phaseResult.quickCapturePersistence.pendingSaveFlushed -or
         -not [bool]$phaseResult.quickCapturePersistence.autoSaveObserved)) {
        throw "Quick Capture mutation did not prove pending-save flush and real auto-save."
    }
    if ($Phase -ceq "VerifyDelete" -and
        -not [bool]$phaseResult.quickCapturePersistence.pendingSaveFlushed) {
        throw "Quick Capture verify/delete did not prove its explicit flush."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Quick Capture phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2) {
        throw "Quick Capture phase '$Phase' did not restore the tray and two fixed widget HWNDs."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2b1-quick-capture") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("QuickCapture", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "QuickCaptureLiveHost")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "QuickCaptureDraftAndAutoSaveObserved",
                "QuickCaptureManagedAttachmentPersisted")
        }
        "VerifyDelete" {
            @(
                "QuickCaptureRestartAndExplicitFlushVerified",
                "QuickCaptureManagedAttachmentDeleted",
                "QuickCaptureItemDeleted")
        }
        default { @("QuickCaptureDeletePostflightVerified") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Quick Capture phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-TodoPersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyDelete", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousQuickCapturePhase = [Environment]::GetEnvironmentVariable(
        $managedUiQuickCapturePhaseEnvironmentVariable,
        "Process")
    $previousTodoPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoPhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")

    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "TodoPersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoPhaseEnvironmentVariable,
            $Phase,
            "Process")
        foreach ($variable in @(
                $musicSessionMutationSmokeEnvironmentVariable,
                $musicMutationSmokeEnvironmentVariable,
                $musicReadSmokeEnvironmentVariable,
                $mutationSmokeEnvironmentVariable,
                $shellSmokeEnvironmentVariable,
                $shortcutSmokeEnvironmentVariable)) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $previousQuickCapturePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoPhaseEnvironmentVariable,
            $previousTodoPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Todo phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Todo phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Todo phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Todo phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Todo phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "TodoPersistenceRestart" -or
        [string]$phaseResult.todoPersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.todoPersistence.normalShutdownRequested) {
        throw "Todo phase '$Phase' did not prove its AOT phase and normal shutdown contract."
    }
    if ($Phase -ceq "Mutate" -and
        -not [bool]$phaseResult.todoPersistence.autoSaveObserved) {
        throw "Todo mutation did not prove the real notes auto-save."
    }
    if ($Phase -ceq "VerifyDelete" -and
        (-not [bool]$phaseResult.todoPersistence.explicitNotesSaved -or
         -not [bool]$phaseResult.todoPersistence.completionRoundTripObserved -or
         $null -eq $phaseResult.todoPersistence.afterExplicitSave)) {
        throw "Todo verify/delete did not prove explicit notes save and completion round-trip."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Todo phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2) {
        throw "Todo phase '$Phase' did not restore the tray and two fixed widget HWNDs."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2b2a-todo") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Search", "Todo") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "TodoLiveHost")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "TodoTaskTitleNotesAndCompletionPersisted",
                "TodoNotesAutoSaveObserved")
        }
        "VerifyDelete" {
            @(
                "TodoRestartExplicitSaveAndCompletionVerified",
                "TodoItemDeleted")
        }
        default { @("TodoDeletePostflightVerified") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Todo phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-TodoStepsPersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyDelete", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousQuickCapturePhase = [Environment]::GetEnvironmentVariable(
        $managedUiQuickCapturePhaseEnvironmentVariable,
        "Process")
    $previousTodoPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoPhaseEnvironmentVariable,
        "Process")
    $previousTodoStepsPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoStepsPhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")

    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "TodoStepsPersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoPhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoStepsPhaseEnvironmentVariable,
            $Phase,
            "Process")
        foreach ($variable in @(
                $musicSessionMutationSmokeEnvironmentVariable,
                $musicMutationSmokeEnvironmentVariable,
                $musicReadSmokeEnvironmentVariable,
                $mutationSmokeEnvironmentVariable,
                $shellSmokeEnvironmentVariable,
                $shortcutSmokeEnvironmentVariable)) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $previousQuickCapturePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoPhaseEnvironmentVariable,
            $previousTodoPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoStepsPhaseEnvironmentVariable,
            $previousTodoStepsPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Todo steps phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Todo steps phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Todo steps phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Todo steps phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Todo steps phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "TodoStepsPersistenceRestart" -or
        [string]$phaseResult.todoStepsPersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.todoStepsPersistence.normalShutdownRequested) {
        throw "Todo steps phase '$Phase' did not prove its AOT phase and normal shutdown contract."
    }
    if ($Phase -ceq "Mutate" -and
        (-not [bool]$phaseResult.todoStepsPersistence.initialStepUiProjected -or
         -not [bool]$phaseResult.todoStepsPersistence.stepTextEditObserved)) {
        throw "Todo steps mutation did not prove its initial row projection and text edit."
    }
    if ($Phase -ceq "VerifyDelete" -and
        (-not [bool]$phaseResult.todoStepsPersistence.stepCompletionRoundTripObserved -or
         $null -eq $phaseResult.todoStepsPersistence.afterStepMutation -or
         $null -eq $phaseResult.todoStepsPersistence.afterStepDelete)) {
        throw "Todo steps verify/delete did not prove completion round-trip and deletion states."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Todo steps phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2) {
        throw "Todo steps phase '$Phase' did not restore the tray and two fixed widget HWNDs."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2b2b1-todo-steps") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Search", "Todo") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "TodoStepsLiveHost")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "TodoStepsTaskAndRowPersisted",
                "TodoStepTextAndCompletionPersisted")
        }
        "VerifyDelete" {
            @(
                "TodoStepsRestartProjectionVerified",
                "TodoStepCompletionRoundTripVerified",
                "TodoStepDeleted",
                "TodoStepsItemDeleted")
        }
        default { @("TodoStepsDeletePostflightVerified") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Todo steps phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-TodoAttachmentsPersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyDelete", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousQuickCapturePhase = [Environment]::GetEnvironmentVariable(
        $managedUiQuickCapturePhaseEnvironmentVariable,
        "Process")
    $previousTodoPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoPhaseEnvironmentVariable,
        "Process")
    $previousTodoStepsPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoStepsPhaseEnvironmentVariable,
        "Process")
    $previousTodoAttachmentsPhase = [Environment]::GetEnvironmentVariable(
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")

    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "TodoAttachmentsPersistenceRestart",
            "Process")
        foreach ($variable in @(
                $managedUiPersistencePhaseEnvironmentVariable,
                $managedUiQuickCapturePhaseEnvironmentVariable,
                $managedUiTodoPhaseEnvironmentVariable,
                $managedUiTodoStepsPhaseEnvironmentVariable,
                $musicSessionMutationSmokeEnvironmentVariable,
                $musicMutationSmokeEnvironmentVariable,
                $musicReadSmokeEnvironmentVariable,
                $mutationSmokeEnvironmentVariable,
                $shellSmokeEnvironmentVariable,
                $shortcutSmokeEnvironmentVariable)) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoAttachmentsPhaseEnvironmentVariable,
            $Phase,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiQuickCapturePhaseEnvironmentVariable,
            $previousQuickCapturePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoPhaseEnvironmentVariable,
            $previousTodoPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoStepsPhaseEnvironmentVariable,
            $previousTodoStepsPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiTodoAttachmentsPhaseEnvironmentVariable,
            $previousTodoAttachmentsPhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Todo attachments phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Todo attachments phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Todo attachments phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Todo attachments phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Todo attachments phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "TodoAttachmentsPersistenceRestart" -or
        [string]$phaseResult.todoAttachmentsPersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.todoAttachmentsPersistence.normalShutdownRequested) {
        throw "Todo attachments phase '$Phase' did not prove its AOT phase and normal shutdown contract."
    }
    if ($Phase -ceq "Mutate" -and
        -not [bool]$phaseResult.todoAttachmentsPersistence.initialAttachmentUiProjected) {
        throw "Todo attachments mutation did not prove its initial real tile projection."
    }
    if ($Phase -ceq "VerifyDelete" -and
        (-not [bool]$phaseResult.todoAttachmentsPersistence.restartAttachmentUiProjected -or
         -not [bool]$phaseResult.todoAttachmentsPersistence.managedAttachmentDeleted -or
         $null -eq $phaseResult.todoAttachmentsPersistence.afterAttachmentDelete)) {
        throw "Todo attachments verify/delete did not prove restart projection and physical deletion."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Todo attachments phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2) {
        throw "Todo attachments phase '$Phase' did not restore the tray and two fixed widget HWNDs."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2b2b2-todo-attachments") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Search", "Todo") `
        -Name "$Phase.visibleWidgetKinds"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "TodoAttachmentsLiveHost")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "TodoManagedAttachmentUiProjected",
                "TodoManagedAttachmentPersisted")
        }
        "VerifyDelete" {
            @(
                "TodoManagedAttachmentRestartProjectionVerified",
                "TodoManagedAttachmentDeleted",
                "TodoAttachmentsItemDeleted")
        }
        default { @("TodoAttachmentsDeletePostflightVerified") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Todo attachments phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-GlancePersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath,

        [Parameter(Mandatory)]
        [string]$FixturePath
    )

    $phaseVariables = @(
        $managedUiSmokeEnvironmentVariable,
        $managedUiPersistencePhaseEnvironmentVariable,
        $managedUiQuickCapturePhaseEnvironmentVariable,
        $managedUiTodoPhaseEnvironmentVariable,
        $managedUiTodoStepsPhaseEnvironmentVariable,
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        $managedUiGlancePhaseEnvironmentVariable,
        $managedUiGlanceFixtureEnvironmentVariable,
        $managedUiWeatherSettingsPhaseEnvironmentVariable,
        $managedUiWeatherSurfacePhaseEnvironmentVariable,
        $musicSessionMutationSmokeEnvironmentVariable,
        $musicMutationSmokeEnvironmentVariable,
        $musicReadSmokeEnvironmentVariable,
        $mutationSmokeEnvironmentVariable,
        $shellSmokeEnvironmentVariable,
        $shortcutSmokeEnvironmentVariable)
    $previousValues = @{}
    foreach ($variable in $phaseVariables) {
        $previousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }

    try {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "GlancePersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiGlancePhaseEnvironmentVariable,
            $Phase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiGlanceFixtureEnvironmentVariable,
            $FixturePath,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previousValues[$variable],
                "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Glance persistence phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Glance persistence phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Glance persistence phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Glance persistence phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Glance persistence phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "GlancePersistenceRestart" -or
        [string]$phaseResult.glancePersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.glancePersistence.normalShutdownRequested) {
        throw "Glance persistence phase '$Phase' did not prove its AOT phase and normal shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.glancePersistence.fixturePath) -Right $FixturePath)) {
        throw "Glance persistence phase '$Phase' evidence does not match its audited process, root, and fixture."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2 -or
        [long]$phaseResult.glancePersistence.windowHandle -eq 0 -or
        -not [bool]$phaseResult.glancePersistence.hasXamlRoot -or
        -not [bool]$phaseResult.glancePersistence.visible -or
        [long]$phaseResult.glancePersistence.fixtureLength -le 0) {
        throw "Glance persistence phase '$Phase' did not restore the tray, fixed widgets, and real Glance host."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2c1-glance") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Glance", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    $beforeMutation = $Phase -ceq "VerifyRestore"
    $afterMutation = $Phase -ceq "Mutate"
    Assert-GlanceEvidenceState `
        -State $phaseResult.glancePersistence.before `
        -ExpectMutation $beforeMutation `
        -FixturePath $FixturePath `
        -Name "$Phase.before"
    Assert-GlanceEvidenceState `
        -State $phaseResult.glancePersistence.after `
        -ExpectMutation $afterMutation `
        -FixturePath $FixturePath `
        -Name "$Phase.after"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "GlanceHostReady",
        "GlanceFixtureOwned",
        "GlanceBaselineVerified")
    $requiredSteps += switch ($Phase) {
        "Mutate" { @("GlanceMutationApplied", "GlanceOwnedImageRetained") }
        "VerifyRestore" {
            @(
                "GlanceMutationApplied",
                "GlanceOwnedImagePreservedAfterRestore")
        }
        default { @("GlancePostflightFixtureRetained") }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Glance persistence phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-WeatherSettingsPersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $phaseVariables = @(
        $managedUiSmokeEnvironmentVariable,
        $managedUiPersistencePhaseEnvironmentVariable,
        $managedUiQuickCapturePhaseEnvironmentVariable,
        $managedUiTodoPhaseEnvironmentVariable,
        $managedUiTodoStepsPhaseEnvironmentVariable,
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        $managedUiGlancePhaseEnvironmentVariable,
        $managedUiGlanceFixtureEnvironmentVariable,
        $managedUiWeatherSettingsPhaseEnvironmentVariable,
        $managedUiWeatherSurfacePhaseEnvironmentVariable,
        $musicSessionMutationSmokeEnvironmentVariable,
        $musicMutationSmokeEnvironmentVariable,
        $musicReadSmokeEnvironmentVariable,
        $mutationSmokeEnvironmentVariable,
        $shellSmokeEnvironmentVariable,
        $shortcutSmokeEnvironmentVariable)
    $previousValues = @{}
    foreach ($variable in $phaseVariables) {
        $previousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }

    try {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "WeatherSettingsPersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiWeatherSettingsPhaseEnvironmentVariable,
            $Phase,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previousValues[$variable],
                "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Weather settings phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Weather settings phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Weather settings phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Weather settings phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Weather settings phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "WeatherSettingsPersistenceRestart" -or
        [string]$phaseResult.weatherSettingsPersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.weatherSettingsPersistence.normalShutdownRequested -or
        -not [bool]$phaseResult.weatherSettingsPersistence.flushSucceeded) {
        throw "Weather settings phase '$Phase' did not prove its AOT phase, flush, and shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Weather settings phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 1 -or
        [int]$phaseResult.visibleSurfaceCount -ne 1) {
        throw "Weather settings phase '$Phase' did not retain only the Search host."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2c2a-weather") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Search") `
        -Name "$Phase.visibleWidgetKinds"

    Assert-WeatherSettingsEvidenceState `
        -State $phaseResult.weatherSettingsPersistence.before `
        -ExpectMutation ($Phase -ceq "VerifyRestore") `
        -Name "$Phase.before"
    Assert-WeatherSettingsEvidenceState `
        -State $phaseResult.weatherSettingsPersistence.after `
        -ExpectMutation ($Phase -ceq "Mutate") `
        -Name "$Phase.after"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "WeatherSettingsHostSuppressed",
        "WeatherSettingsPersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @("WeatherSettingsBaselineVerified", "WeatherSettingsMutationApplied")
        }
        "VerifyRestore" {
            @("WeatherSettingsRestartVerified", "WeatherSettingsBaselineRestored")
        }
        default {
            @("WeatherSettingsPostflightVerified", "WeatherSettingsBaselineRestored")
        }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Weather settings phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-WeatherSurfacePersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $phaseVariables = @(
        $managedUiSmokeEnvironmentVariable,
        $managedUiPersistencePhaseEnvironmentVariable,
        $managedUiQuickCapturePhaseEnvironmentVariable,
        $managedUiTodoPhaseEnvironmentVariable,
        $managedUiTodoStepsPhaseEnvironmentVariable,
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        $managedUiGlancePhaseEnvironmentVariable,
        $managedUiGlanceFixtureEnvironmentVariable,
        $managedUiWeatherSettingsPhaseEnvironmentVariable,
        $managedUiWeatherSurfacePhaseEnvironmentVariable,
        $musicSessionMutationSmokeEnvironmentVariable,
        $musicMutationSmokeEnvironmentVariable,
        $musicReadSmokeEnvironmentVariable,
        $mutationSmokeEnvironmentVariable,
        $shellSmokeEnvironmentVariable,
        $shortcutSmokeEnvironmentVariable)
    $previousValues = @{}
    foreach ($variable in $phaseVariables) {
        $previousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }

    try {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "WeatherSurfacePersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiWeatherSurfacePhaseEnvironmentVariable,
            $Phase,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previousValues[$variable],
                "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Weather surface phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Weather surface phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Weather surface phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Weather surface phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Weather surface phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "WeatherSurfacePersistenceRestart" -or
        [string]$phaseResult.weatherSurfacePersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.weatherSurfacePersistence.normalShutdownRequested -or
        -not [bool]$phaseResult.weatherSurfacePersistence.flushSucceeded) {
        throw "Weather surface phase '$Phase' did not prove its AOT phase, flush, and shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Weather surface phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2 -or
        [long]$phaseResult.weatherSurfacePersistence.windowHandle -eq 0 -or
        -not [bool]$phaseResult.weatherSurfacePersistence.hasXamlRoot -or
        -not [bool]$phaseResult.weatherSurfacePersistence.visible) {
        throw "Weather surface phase '$Phase' did not restore the tray and two real widget hosts."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4b2c2b-weather") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("Search", "Weather") `
        -Name "$Phase.visibleWidgetKinds"

    Assert-WeatherSurfaceEvidenceState `
        -State $phaseResult.weatherSurfacePersistence.before `
        -ExpectMutation ($Phase -ceq "VerifyRestore") `
        -Name "$Phase.before"
    Assert-WeatherSurfaceEvidenceState `
        -State $phaseResult.weatherSurfacePersistence.after `
        -ExpectMutation ($Phase -ceq "Mutate") `
        -Name "$Phase.after"

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "WeatherSurfaceHostReady",
        "WeatherSurfacePersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @("WeatherSurfaceBaselineVerified", "WeatherSurfaceMutationApplied")
        }
        "VerifyRestore" {
            @(
                "WeatherSurfaceRestartMutationVerified",
                "WeatherSurfaceBaselineRestored")
        }
        default {
            @("WeatherSurfaceBaselineVerified", "WeatherSurfacePostflightVerified")
        }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Weather surface phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-LocalFilePersistencePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $phaseVariables = @(
        $managedUiSmokeEnvironmentVariable,
        $managedUiPersistencePhaseEnvironmentVariable,
        $managedUiQuickCapturePhaseEnvironmentVariable,
        $managedUiTodoPhaseEnvironmentVariable,
        $managedUiTodoStepsPhaseEnvironmentVariable,
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        $managedUiGlancePhaseEnvironmentVariable,
        $managedUiGlanceFixtureEnvironmentVariable,
        $managedUiWeatherSettingsPhaseEnvironmentVariable,
        $managedUiWeatherSurfacePhaseEnvironmentVariable,
        $managedUiLocalFilePhaseEnvironmentVariable,
        $musicSessionMutationSmokeEnvironmentVariable,
        $musicMutationSmokeEnvironmentVariable,
        $musicReadSmokeEnvironmentVariable,
        $mutationSmokeEnvironmentVariable,
        $shellSmokeEnvironmentVariable,
        $shortcutSmokeEnvironmentVariable)
    $previousValues = @{}
    foreach ($variable in $phaseVariables) {
        $previousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }

    try {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "LocalFileSurfacePersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiLocalFilePhaseEnvironmentVariable,
            $Phase,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previousValues[$variable],
                "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Local-file phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$phaseSession.previewDataRoot) -Right $DataRoot)) {
        throw "Local-file phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Local-file phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Local-file phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Local-file phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "LocalFileSurfacePersistenceRestart" -or
        [string]$phaseResult.localFilePersistence.phase -cne $Phase -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.localFilePersistence.normalShutdownRequested -or
        -not [bool]$phaseResult.localFilePersistence.flushSucceeded) {
        throw "Local-file phase '$Phase' did not prove its AOT phase, flush, and shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$phaseResult.executablePath) -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$phaseResult.resultPath) -Right $ResultPath)) {
        throw "Local-file phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2 -or
        [long]$phaseResult.localFilePersistence.windowHandle -eq 0 -or
        -not [bool]$phaseResult.localFilePersistence.hasXamlRoot -or
        -not [bool]$phaseResult.localFilePersistence.visible) {
        throw "Local-file phase '$Phase' did not restore the tray and two real widget hosts."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4c1a-file") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    Assert-LocalFileEvidenceState `
        -State $phaseResult.localFilePersistence.before `
        -ExpectMutation ($Phase -ceq "VerifyRestore") `
        -Name "$Phase.before"
    Assert-LocalFileEvidenceState `
        -State $phaseResult.localFilePersistence.after `
        -ExpectMutation ($Phase -ceq "Mutate") `
        -Name "$Phase.after"

    $operations = $phaseResult.localFilePersistence.operations
    if ([bool]$operations.shellProgressRequested) {
        throw "Local-file phase '$Phase' entered the deferred Shell progress path."
    }
    if ($Phase -ceq "Mutate") {
        foreach ($property in @(
                "navigatedIntoFolder", "nestedSurfaceProjected", "navigatedUp",
                "copyCompleted", "copySourceRetained", "moveCompleted",
                "moveSourceRemoved", "renameCompleted", "conflictRejected",
                "conflictStatePreserved", "watcherObserved")) {
            if (-not [bool]$operations.$property) {
                throw "Local-file mutate operation '$property' was not proven."
            }
        }
        if ([string]$operations.conflictExceptionType -cne "System.IO.IOException" -or
            [string]::IsNullOrWhiteSpace([string]$operations.conflictMessage)) {
            throw "Local-file rename conflict did not retain its IOException evidence."
        }
    }
    elseif ($Phase -ceq "VerifyRestore") {
        if (-not [bool]$operations.ownedFixtureCleanupCompleted -or
            -not [bool]$operations.watcherRemovalObserved) {
            throw "Local-file verify/restore did not prove owned cleanup and watcher removal."
        }
    }

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "LocalFileSurfaceHostReady",
        "LocalFileOwnedRootVerified",
        "LocalFilePersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "LocalFileBaselineVerified",
                "LocalFileNavigationCycleCompleted",
                "LocalFileCopyCompleted",
                "LocalFileMoveCompleted",
                "LocalFileRenameCompleted",
                "LocalFileRenameConflictRejected",
                "LocalFileWatcherObservedExternalCreate",
                "LocalFileMutationApplied")
        }
        "VerifyRestore" {
            @(
                "LocalFileRestartMutationVerified",
                "LocalFileOwnedFixtureCleanupCompleted",
                "LocalFileBaselineRestored")
        }
        default {
            @("LocalFileBaselineVerified", "LocalFilePostflightVerified")
        }
    }
    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Local-file phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

function Invoke-RecycleBinPhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Mutate", "VerifyRestore", "Postflight", "Compensate")]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$ResultPath
    )

    $phaseVariables = @(
        $managedUiSmokeEnvironmentVariable,
        $managedUiPersistencePhaseEnvironmentVariable,
        $managedUiQuickCapturePhaseEnvironmentVariable,
        $managedUiTodoPhaseEnvironmentVariable,
        $managedUiTodoStepsPhaseEnvironmentVariable,
        $managedUiTodoAttachmentsPhaseEnvironmentVariable,
        $managedUiGlancePhaseEnvironmentVariable,
        $managedUiGlanceFixtureEnvironmentVariable,
        $managedUiWeatherSettingsPhaseEnvironmentVariable,
        $managedUiWeatherSurfacePhaseEnvironmentVariable,
        $managedUiLocalFilePhaseEnvironmentVariable,
        $managedUiRecycleBinPhaseEnvironmentVariable,
        $managedUiRecycleBinRunIdEnvironmentVariable,
        $musicSessionMutationSmokeEnvironmentVariable,
        $musicMutationSmokeEnvironmentVariable,
        $musicReadSmokeEnvironmentVariable,
        $mutationSmokeEnvironmentVariable,
        $shellSmokeEnvironmentVariable,
        $shortcutSmokeEnvironmentVariable)
    $previousValues = @{}
    foreach ($variable in $phaseVariables) {
        $previousValues[$variable] = [Environment]::GetEnvironmentVariable(
            $variable,
            "Process")
    }

    try {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            "RecycleBinMenuPersistenceRestart",
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiRecycleBinPhaseEnvironmentVariable,
            $Phase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiRecycleBinRunIdEnvironmentVariable,
            $recycleBinRunId,
            "Process")

        $null = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -AllowEarlyExit `
                -StartupWaitSeconds 1)
    }
    finally {
        foreach ($variable in $phaseVariables) {
            [Environment]::SetEnvironmentVariable(
                $variable,
                $previousValues[$variable],
                "Process")
        }
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Recycle Bin phase '$Phase' did not create preview session evidence."
    }
    $phaseSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual `
            -Left ([string]$phaseSession.previewDataRoot) `
            -Right $DataRoot)) {
        throw "Recycle Bin phase '$Phase' used the wrong preview data root."
    }

    $phaseResult = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    $phaseResult = $candidate
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }
        Start-Sleep -Milliseconds 200
    }

    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath ([string]$phaseSession.executablePath) `
        -TimeoutSeconds 20
    if (-not $naturalExit) {
        throw "Recycle Bin phase '$Phase' did not exit through the application shutdown path."
    }
    if ($null -eq $phaseResult) {
        throw "Recycle Bin phase '$Phase' timed out without terminal structured evidence."
    }
    if ($phaseResult.state -ne "Completed" -or -not [bool]$phaseResult.success) {
        throw "Recycle Bin phase '$Phase' failed: $($phaseResult.error)"
    }
    if ([int]$phaseResult.schemaVersion -ne 1 -or
        [string]$phaseResult.scenario -cne "RecycleBinMenuPersistenceRestart" -or
        [string]$phaseResult.recycleBin.phase -cne $Phase -or
        [string]$phaseResult.recycleBin.runId -cne $recycleBinRunId -or
        [bool]$phaseResult.isDynamicCodeSupported -or
        -not [bool]$phaseResult.recycleBin.normalShutdownRequested -or
        -not [bool]$phaseResult.recycleBin.flushSucceeded) {
        throw "Recycle Bin phase '$Phase' did not prove its AOT identity, flush, and shutdown contract."
    }
    if ([int]$phaseResult.processId -ne [int]$phaseSession.primaryProcessId -or
        -not (Test-PathEqual `
            -Left ([string]$phaseResult.executablePath) `
            -Right ([string]$phaseSession.executablePath)) -or
        -not (Test-PathEqual `
            -Left ([string]$phaseResult.previewDataRoot) `
            -Right $DataRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$phaseResult.resultPath) `
            -Right $ResultPath)) {
        throw "Recycle Bin phase '$Phase' evidence does not match its audited process and root."
    }
    if (-not [bool]$phaseResult.trayIconCreated -or
        [long]$phaseResult.trayIconWindowHandle -eq 0 -or
        [long]$phaseResult.trayOwnerWindowHandle -eq 0 -or
        [int]$phaseResult.loadedSurfaceCount -ne 2 -or
        [int]$phaseResult.visibleSurfaceCount -ne 2 -or
        [long]$phaseResult.recycleBin.windowHandle -eq 0 -or
        -not [bool]$phaseResult.recycleBin.hasXamlRoot -or
        -not [bool]$phaseResult.recycleBin.visible) {
        throw "Recycle Bin phase '$Phase' did not restore the tray and two real widget hosts."
    }
    Assert-StringSequence `
        -Actual @($phaseResult.seededWidgetIds) `
        -Expected @("aot-5b4a-search", "aot-5b4c1b1-file") `
        -Name "$Phase.seededWidgetIds"
    Assert-StringSequence `
        -Actual @($phaseResult.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "$Phase.visibleWidgetKinds"

    if ($Phase -cne "Compensate") {
        $beforePresent = $Phase -cne "VerifyRestore"
        Assert-RecycleBinEvidenceState `
            -State $phaseResult.recycleBin.before `
            -ExpectOwnedOnDisk $beforePresent `
            -ExpectedRecycleMatches $(if ($beforePresent) { 0 } else { 1 }) `
            -Name "$Phase.before"
    }
    $afterPresent = $Phase -cne "Mutate"
    Assert-RecycleBinEvidenceState `
        -State $phaseResult.recycleBin.after `
        -ExpectOwnedOnDisk $afterPresent `
        -ExpectedRecycleMatches $(if ($afterPresent) { 0 } else { 1 }) `
        -Name "$Phase.after"

    $operations = $phaseResult.recycleBin.operations
    if ($Phase -ceq "Mutate") {
        Assert-RecycleBinMenuEvidence `
            -Menu $operations.singleMenu `
            -ExpectedSelectionCount 1 `
            -Name "Mutate.singleMenu"
        Assert-RecycleBinMenuEvidence `
            -Menu $operations.multiMenu `
            -ExpectedSelectionCount 2 `
            -Name "Mutate.multiMenu"
        if (-not [bool]$operations.productDeletePathCompleted -or
            -not [bool]$operations.ownedPathsRemoved -or
            [int]$operations.singleMenu.menuItemCount -le
                [int]$operations.multiMenu.menuItemCount) {
            throw "Recycle Bin mutate did not prove product single/multi menu deletion."
        }
    }
    elseif ($Phase -ceq "VerifyRestore" -or $Phase -ceq "Compensate") {
        if (-not [bool]$operations.exactRestoreCompleted -or
            [bool]$operations.compensation -ne ($Phase -ceq "Compensate") -or
            @($operations.nativeCalls).Count -lt 3) {
            throw "Recycle Bin '$Phase' did not prove exact native recovery."
        }
    }

    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "RecycleBinSurfaceHostReady",
        "RecycleBinOwnedRootVerified",
        "RecycleBinPersistenceFlushed")
    $requiredSteps += switch ($Phase) {
        "Mutate" {
            @(
                "RecycleBinOwnedBaselineVerified",
                "RecycleBinSingleMenuDeleteCompleted",
                "RecycleBinMultiMenuDeleteCompleted",
                "RecycleBinOwnedPathsRemoved",
                "RecycleBinMenuDeletionApplied")
        }
        "VerifyRestore" {
            @(
                "RecycleBinRestartDeletionVerified",
                "RecycleBinExactIdentityQueried",
                "RecycleBinExactIdentityRestored",
                "RecycleBinExactRestoreCompleted")
        }
        "Compensate" {
            @(
                "RecycleBinCompensationIdentityQueried",
                "RecycleBinCompensationCompleted")
        }
        default {
            @("RecycleBinOwnedBaselineVerified", "RecycleBinPostflightVerified")
        }
    }
    $missingSteps = @(
        $requiredSteps | Where-Object { $_ -notin @($phaseResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Recycle Bin phase '$Phase' is missing steps: $($missingSteps -join ', ')."
    }

    return [PSCustomObject]@{
        phase = $Phase
        resultPath = $ResultPath
        session = $phaseSession
        result = $phaseResult
        naturalExit = $naturalExit
    }
}

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Native AOT preview launcher was not found: '$launcher'."
}

New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = if ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
        Join-Path $evidenceRoot "recycle-preview-$recycleBinRunId"
    }
    else {
        $defaultDataRoot
    }
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot)) {
    throw "Managed UI smoke DataRoot must be a child of '$evidenceRoot'."
}
if ((Test-PathEqualOrInside -Root $productionDataRoot -Candidate $DataRoot) -or
    (Test-PathEqualOrInside -Root $DataRoot -Candidate $productionDataRoot)) {
    throw "Managed UI smoke DataRoot must not overlap production data."
}

$ownedMarkerPath = Join-Path $DataRoot $ownedMarkerName
$recycleBinRecoveryRoot = if ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
    [System.IO.Path]::GetFullPath("$DataRoot-Recovery")
}
else {
    $null
}
if ($null -ne $recycleBinRecoveryRoot) {
    if (-not (Test-PathEqualOrInside `
            -Root $evidenceRoot `
            -Candidate $recycleBinRecoveryRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $recycleBinRecoveryRoot) -or
        (Test-PathEqualOrInside `
            -Root $productionDataRoot `
            -Candidate $recycleBinRecoveryRoot) -or
        (Test-PathEqualOrInside `
            -Root $recycleBinRecoveryRoot `
            -Candidate $productionDataRoot)) {
        throw "Recycle Bin recovery root escaped its owned evidence boundary."
    }
    if (Test-Path -LiteralPath $recycleBinRecoveryRoot) {
        throw "Refusing to replace an existing Recycle Bin recovery root."
    }
}
if (Test-Path -LiteralPath $DataRoot -PathType Container) {
    if ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
        throw "Refusing to replace an existing Recycle Bin preview root; use a fresh owned path so an older recovery identity is not discarded."
    }
    if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf)) {
        throw "Refusing to replace an unowned managed UI preview root: '$DataRoot'."
    }
    $existingMarker = Get-Content -LiteralPath $ownedMarkerPath -Raw | ConvertFrom-Json
    if ([string]$existingMarker.kind -cne $ownedMarkerKind -or
        -not (Test-PathEqual -Left ([string]$existingMarker.repositoryRoot) -Right $repoRoot)) {
        throw "Refusing to replace a managed UI preview root with an invalid ownership marker."
    }

    $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
    if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
        (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
        throw "Resolved managed UI preview root escaped its intended evidence directory."
    }
    Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
$ownedMarker = [ordered]@{
    kind = $ownedMarkerKind
    repositoryRoot = $repoRoot
    scenario = $scenario
    recycleBinRunId = $recycleBinRunId
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
$ownedMarker | ConvertTo-Json | Set-Content -LiteralPath $ownedMarkerPath -Encoding UTF8
$recycleBinRecoveryMarkerPath = if ($null -ne $recycleBinRecoveryRoot) {
    New-Item `
        -ItemType Directory `
        -Path $recycleBinRecoveryRoot `
        -Force | Out-Null
    $markerPath = Join-Path $recycleBinRecoveryRoot $ownedMarkerName
    $ownedMarker | ConvertTo-Json |
        Set-Content -LiteralPath $markerPath -Encoding UTF8
    $markerPath
}
else {
    $null
}

$dataDirectory = Join-Path $DataRoot "data"
$fixtureDirectory = Join-Path $DataRoot "fixtures\empty-file-surface"
$quickCaptureAttachmentFixturePath = Join-Path `
    $DataRoot `
    "fixtures\quick-capture-attachment.txt"
$todoAttachmentFixturePath = Join-Path `
    $DataRoot `
    "fixtures\todo-managed-attachment.txt"
$glanceImageFixturePath = Join-Path `
    $DataRoot `
    "fixtures\glance-local.png"
$localFileFixtureRoot = Join-Path $DataRoot "fixtures\local-file-surface"
$localFileWidgetRoot = Join-Path $localFileFixtureRoot "widget-root"
$localFileNestedRoot = Join-Path $localFileWidgetRoot "nested"
$localFileSourceRoot = Join-Path $localFileFixtureRoot "sources"
$recycleBinFixtureRoot = Join-Path $DataRoot "fixtures\recycle-bin-menu"
$recycleBinWidgetRoot = Join-Path $recycleBinFixtureRoot "widget-root"
$recycleBinSingleName = "single-$recycleBinRunId"
$recycleBinMultiFileName = "multi-file-$recycleBinRunId"
$recycleBinMultiFolderName = "multi-folder-$recycleBinRunId"
$recycleBinFolderPayloadName = "payload-$recycleBinRunId"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null

# Seed AppSettings.HasCompletedOnboarding, FeatureWidgetEnabledStates, and SearchSaveHistory.
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
            id = "aot-5b4a-file"
            name = "AOT File Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 300
            height = 360
            widgetKind = "File"
            viewMode = "Icon"
            isVisible = $true
            isDisabled = $false
            isPositionLocked = $false
            isSizeLocked = $false
            isCollapsed = $false
            mappedFolderPath = $fixtureDirectory
            followsDefaultStoragePath = $false
            sortMode = "Name"
            items = @()
            metadata = [ordered]@{}
            fileAddedAtByPath = [ordered]@{}
            fileAddedAtTrackingInitialized = $true
        },
        [ordered]@{
            id = "aot-5b4a-search"
            name = "AOT Search Fixture"
            isDefaultTitle = $false
            x = 420
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
        }
    )
}
if ($scenario -ceq "DeepSettingsReadOnly") {
    $settings["fileStacksEnabled"] = $true
    $settings["fileStackAutoStacking"] = $true
    $settings["fileStackGroupBy"] = "Custom"
    $settings["fileStackCustomRules"] = @(
        [ordered]@{
            id = "aot-5b4b1-design"
            name = "AOT Design Fixture"
            extensions = @(".aotfixture")
        })
}
elseif ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
    $settings["fileWidgetFolderOpenBehavior"] = "Embedded"
    $fileWidget = $settings["widgets"][0]
    $searchWidget = $settings["widgets"][1]
    $fileWidget["id"] = "aot-5b4c1b1-file"
    $fileWidget["name"] = "AOT Recycle Bin Menu Fixture"
    $fileWidget["width"] = 380
    $fileWidget["height"] = 460
    $fileWidget["mappedFolderPath"] = $recycleBinWidgetRoot
    $fileWidget["metadata"] = [ordered]@{
        FolderOpenBehavior = "Embedded"
    }
    $settings["widgets"] = @($fileWidget, $searchWidget)

    $recycleBinMultiFolderPath = Join-Path `
        $recycleBinWidgetRoot `
        $recycleBinMultiFolderName
    New-Item `
        -ItemType Directory `
        -Path $recycleBinMultiFolderPath `
        -Force | Out-Null
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $recycleBinWidgetRoot "baseline"),
        "DeskBox AOT 5B-4C1B1 baseline.`n",
        $utf8WithoutBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $recycleBinWidgetRoot $recycleBinSingleName),
        "single:$recycleBinRunId`n",
        $utf8WithoutBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $recycleBinWidgetRoot $recycleBinMultiFileName),
        "multi-file:$recycleBinRunId`n",
        $utf8WithoutBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $recycleBinMultiFolderPath $recycleBinFolderPayloadName),
        "multi-folder:$recycleBinRunId`n",
        $utf8WithoutBom)
}
elseif ($scenario -ceq "LocalFileSurfacePersistenceRestart") {
    $settings["fileWidgetFolderOpenBehavior"] = "Embedded"
    $fileWidget = $settings["widgets"][0]
    $searchWidget = $settings["widgets"][1]
    $fileWidget["id"] = "aot-5b4c1a-file"
    $fileWidget["name"] = "AOT Local File Surface Fixture"
    $fileWidget["width"] = 380
    $fileWidget["height"] = 460
    $fileWidget["mappedFolderPath"] = $localFileWidgetRoot
    $fileWidget["metadata"] = [ordered]@{
        FolderOpenBehavior = "Embedded"
    }
    $settings["widgets"] = @($fileWidget, $searchWidget)

    New-Item -ItemType Directory -Path $localFileNestedRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $localFileSourceRoot -Force | Out-Null
    "DeskBox AOT 5B-4C1A baseline fixture.`n" |
        Set-Content `
            -LiteralPath (Join-Path $localFileWidgetRoot "baseline.txt") `
            -Encoding UTF8
    "DeskBox AOT 5B-4C1A nested fixture.`n" |
        Set-Content `
            -LiteralPath (Join-Path $localFileNestedRoot "nested.txt") `
            -Encoding UTF8
    "DeskBox AOT 5B-4C1A copy source fixture.`n" |
        Set-Content `
            -LiteralPath (Join-Path $localFileSourceRoot "copy-source.txt") `
            -Encoding UTF8
    "DeskBox AOT 5B-4C1A move source fixture.`n" |
        Set-Content `
            -LiteralPath (Join-Path $localFileSourceRoot "move-source.txt") `
            -Encoding UTF8
}
elseif ($scenario -ceq "QuickCapturePersistenceRestart") {
    $settings["featureWidgetEnabledStates"]["QuickCapture"] = $true
    $settings["quickCaptureDefaultView"] = "Records"
    $settings["quickCaptureWideOpenMode"] = "Editing"
    $settings["quickCaptureShowRecordsTab"] = $true
    $settings["quickCaptureShowPinnedTab"] = $false
    $settings["quickCaptureShowRecentTab"] = $false
    $searchWidget = $settings["widgets"][1]
    $settings["widgets"] = @(
        [ordered]@{
            id = "aot-5b4b2b1-quick-capture"
            name = "AOT Quick Capture Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 320
            height = 420
            widgetKind = "QuickCapture"
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
        },
        $searchWidget)
    New-Item `
        -ItemType Directory `
        -Path (Split-Path -Parent $quickCaptureAttachmentFixturePath) `
        -Force | Out-Null
    "DeskBox AOT 5B-4B2B1 managed attachment fixture.`n" |
        Set-Content -LiteralPath $quickCaptureAttachmentFixturePath -Encoding UTF8
}
elseif ($scenario -in @(
        "TodoPersistenceRestart",
        "TodoStepsPersistenceRestart",
        "TodoAttachmentsPersistenceRestart")) {
    $settings["featureWidgetEnabledStates"]["Todo"] = $true
    $settings["todoDefaultFilter"] = "All"
    $settings["todoShowCompletedTasks"] = $true
    $settings["todoShowAllTab"] = $true
    $settings["todoShowActiveTab"] = $false
    $settings["todoShowTodayTab"] = $false
    $settings["todoShowThisWeekTab"] = $false
    $settings["todoShowThisMonthTab"] = $false
    $settings["todoShowImportantTab"] = $false
    $settings["todoShowCompletedTab"] = $true
    $settings["todoReminderEnabled"] = $false
    $settings["todoLayoutMode"] = "SinglePane"
    $settings["todoAutoSelectFirstInWideLayout"] = $false
    $searchWidget = $settings["widgets"][1]
    $todoWidgetId = switch ($scenario) {
        "TodoStepsPersistenceRestart" { "aot-5b4b2b2b1-todo-steps" }
        "TodoAttachmentsPersistenceRestart" { "aot-5b4b2b2b2-todo-attachments" }
        default { "aot-5b4b2b2a-todo" }
    }
    $todoWidgetName = switch ($scenario) {
        "TodoStepsPersistenceRestart" { "AOT Todo Steps Fixture" }
        "TodoAttachmentsPersistenceRestart" { "AOT Todo Attachments Fixture" }
        default { "AOT Todo Fixture" }
    }
    $settings["widgets"] = @(
        [ordered]@{
            id = $todoWidgetId
            name = $todoWidgetName
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 360
            height = 480
            widgetKind = "Todo"
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
        },
        $searchWidget)
    if ($scenario -ceq "TodoAttachmentsPersistenceRestart") {
        New-Item `
            -ItemType Directory `
            -Path (Split-Path -Parent $todoAttachmentFixturePath) `
            -Force | Out-Null
        "DeskBox AOT 5B-4B2B2B2 managed Todo attachment fixture.`n" |
            Set-Content -LiteralPath $todoAttachmentFixturePath -Encoding UTF8
    }
}
elseif ($scenario -ceq "WeatherSurfacePersistenceRestart") {
    $settings["featureWidgetEnabledStates"]["Weather"] = $true
    $settings["weatherAutoLocation"] = $false
    $settings["weatherCityName"] = "Shanghai AOT Surface"
    $settings["weatherLatitude"] = 31.2304
    $settings["weatherLongitude"] = 121.4737
    $settings["weatherTemperatureUnit"] = "Celsius"
    $settings["weatherWindSpeedUnit"] = "kmh"
    $settings["weatherDataSource"] = "MSN"
    $settings["weatherDefaultView"] = "Week"
    $settings["weatherSkin"] = "Rich"
    $settings["weatherShowForecast"] = $true
    $settings["weatherShowSunrise"] = $true
    $settings["weatherShowUvIndex"] = $true
    $settings["weatherShowPrecipitation"] = $true
    $settings["weatherShowHumidity"] = $true
    $settings["weatherShowWind"] = $true
    $settings["weatherShowPressure"] = $true
    $settings["weatherRefreshIntervalMinutes"] = 60
    $searchWidget = $settings["widgets"][1]
    $settings["widgets"] = @(
        [ordered]@{
            id = "aot-5b4b2c2b-weather"
            name = "AOT Weather Surface Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 420
            height = 520
            widgetKind = "Weather"
            viewMode = "Icon"
            isVisible = $true
            isDisabled = $false
            isPositionLocked = $false
            isSizeLocked = $false
            isCollapsed = $false
            followsDefaultStoragePath = $false
            sortMode = "Name"
            items = @()
            metadata = [ordered]@{
                "Weather.ViewMode" = "Day"
            }
            fileAddedAtByPath = [ordered]@{}
            fileAddedAtTrackingInitialized = $true
        },
        $searchWidget)
}
elseif ($scenario -ceq "WeatherSettingsPersistenceRestart") {
    $settings["weatherAutoLocation"] = $false
    $settings["weatherCityName"] = "Shanghai AOT Baseline"
    $settings["weatherLatitude"] = 31.2304
    $settings["weatherLongitude"] = 121.4737
    $settings["weatherTemperatureUnit"] = "Celsius"
    $settings["weatherWindSpeedUnit"] = "kmh"
    $settings["weatherDataSource"] = "MSN"
    $settings["weatherDefaultView"] = "Week"
    $settings["weatherSkin"] = "Rich"
    $settings["weatherShowForecast"] = $true
    $settings["weatherShowSunrise"] = $true
    $settings["weatherShowUvIndex"] = $true
    $settings["weatherShowPrecipitation"] = $true
    $settings["weatherShowHumidity"] = $true
    $settings["weatherShowWind"] = $true
    $settings["weatherShowPressure"] = $false
    $settings["weatherRefreshIntervalMinutes"] = 60
    $searchWidget = $settings["widgets"][1]
    $settings["widgets"] = @(
        [ordered]@{
            id = "aot-5b4b2c2a-weather"
            name = "AOT Weather Settings Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 360
            height = 420
            widgetKind = "Weather"
            viewMode = "Icon"
            isVisible = $true
            isDisabled = $false
            isPositionLocked = $false
            isSizeLocked = $false
            isCollapsed = $false
            followsDefaultStoragePath = $false
            sortMode = "Name"
            items = @()
            metadata = [ordered]@{
                "Weather.ViewMode" = "Day"
            }
            fileAddedAtByPath = [ordered]@{}
            fileAddedAtTrackingInitialized = $true
        },
        $searchWidget)
}
elseif ($scenario -ceq "GlancePersistenceRestart") {
    $settings["featureWidgetEnabledStates"]["Glance"] = $true
    $searchWidget = $settings["widgets"][1]
    $settings["widgets"] = @(
        [ordered]@{
            id = "aot-5b4b2c1-glance"
            name = "AOT Glance Fixture"
            isDefaultTitle = $false
            x = 80
            y = 80
            boundsCoordinateVersion = 1
            width = 380
            height = 320
            widgetKind = "Glance"
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
        },
        $searchWidget)

    New-Item `
        -ItemType Directory `
        -Path (Split-Path -Parent $glanceImageFixturePath) `
        -Force | Out-Null
    $glancePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
    [System.IO.File]::WriteAllBytes(
        $glanceImageFixturePath,
        [Convert]::FromBase64String($glancePngBase64))

    $glanceStoreDirectory = Join-Path $dataDirectory "glance\widgets"
    New-Item -ItemType Directory -Path $glanceStoreDirectory -Force | Out-Null
    $glanceStorePath = Join-Path `
        $glanceStoreDirectory `
        "aot-5b4b2c1-glance.json"
    # Seed the fixture at an old schema form (version 8). The evidence gate
    # below expects version 10, so the scenario verifies that the glance
    # preference store's Normalize pass stamps the current schema version on
    # load instead of echoing whatever the fixture shipped.
    $glanceBaseline = [ordered]@{
        version = 8
        showTime = $true
        showDate = $true
        showYear = $false
        showWeekday = $true
        showCalendar = $false
        layout = "Centered"
        backgroundSource = "LocalFiles"
        localImagePaths = @()
        localFolderPath = $null
        rotationIntervalMinutes = 30.0
        randomOrder = $false
        transition = "CrossFade"
        transitionSpeed = "Standard"
        readability = "Soft"
        showPhotoControls = $true
    }
    $glanceBaseline | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $glanceStorePath -Encoding UTF8
}
$settingsPath = Join-Path $dataDirectory "settings.json"
$settings | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8
if ($scenario -ceq "RecycleBinMenuPersistenceRestart") {
    $recycleBinScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\recycle-bin-menu-persistence-restart"
    $mutateResultPath = Join-Path $recycleBinScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $recycleBinScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path `
        $recycleBinScenarioRoot `
        "postflight\result.json"
    $compensateResultPath = Join-Path `
        $recycleBinScenarioRoot `
        "compensate\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $recycleBinArchiveRoot = Join-Path `
        $evidenceRoot `
        "recycle-bin-menu-persistence-restart-$recycleBinRunId"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $initialDiskState = Get-LocalFileFixtureState `
        -FixtureRoot $recycleBinFixtureRoot
    Assert-RecycleBinDiskState `
        -State $initialDiskState `
        -ExpectOwnedOnDisk $true `
        -Name "initial-independent-disk"
    $recycleBinExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false
    $recoveryRootCleaned = $false
    $recycleSafetyVerified = $false

    try {
        $mutatePhase = Invoke-RecycleBinPhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $recycleBinExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result
        $mutateDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $recycleBinFixtureRoot
        Assert-RecycleBinDiskState `
            -State $mutateDiskState `
            -ExpectOwnedOnDisk $false `
            -Name "mutate-independent-disk"

        $verifyRestorePhase = Invoke-RecycleBinPhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath
        $verifyRestore = $verifyRestorePhase.result
        $verifyRestoreDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $recycleBinFixtureRoot
        Assert-RecycleBinDiskState `
            -State $verifyRestoreDiskState `
            -ExpectOwnedOnDisk $true `
            -Name "verify-restore-independent-disk"

        $postflightPhase = Invoke-RecycleBinPhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result
        $postflightDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $recycleBinFixtureRoot
        Assert-RecycleBinDiskState `
            -State $postflightDiskState `
            -ExpectOwnedOnDisk $true `
            -Name "postflight-independent-disk"

        foreach ($statePair in @(
                @($verifyRestoreDiskState, "verify-restore"),
                @($postflightDiskState, "postflight"))) {
            $actualState = $statePair[0]
            $stateName = [string]$statePair[1]
            $initialFiles = @($initialDiskState.files)
            $actualFiles = @($actualState.files)
            if ($initialFiles.Count -ne $actualFiles.Count) {
                throw "$stateName did not restore the complete owned file set."
            }
            for ($index = 0; $index -lt $initialFiles.Count; $index++) {
                foreach ($property in @("relativePath", "length", "sha256")) {
                    Assert-PersistenceScalarEqual `
                        -Expected $initialFiles[$index].$property `
                        -Actual $actualFiles[$index].$property `
                        -Name "$stateName.files[$index].$property"
                }
            }
        }
        $recycleSafetyVerified = $true

        $naturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyRestorePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($naturalExit.Values -contains $false) {
            throw "Recycle Bin matrix did not exit naturally in every normal phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Recycle Bin matrix did not use three distinct normal application processes."
        }
        $phaseExecutableHashes = @(
            [string]$mutatePhase.session.executableSha256,
            [string]$verifyRestorePhase.session.executableSha256,
            [string]$postflightPhase.session.executableSha256)
        if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
            throw "Recycle Bin phases did not use one identical audited executable."
        }
        $previewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $recycleBinExecutablePath)
        if ($previewProcessesAfter.Count -ne 0) {
            throw "Recycle Bin matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Recycle Bin matrix did not produce its runtime log."
        }
        $runtimeLogLines = @(Get-Content -LiteralPath $runtimeLogPath)
        $runtimeFailureLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[AotManagedUiSmoke] Failed:", [StringComparison]::Ordinal) -ge 0
            })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Recycle Bin runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Recycle Bin matrix."
        }

        New-Item `
            -ItemType Directory `
            -Path $recycleBinArchiveRoot `
            -Force | Out-Null
        $archivedMutateResultPath = Join-Path `
            $recycleBinArchiveRoot `
            "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path `
            $recycleBinArchiveRoot `
            "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path `
            $recycleBinArchiveRoot `
            "postflight-result.json"
        $archivedSessionPath = Join-Path $recycleBinArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $recycleBinArchiveRoot "DeskBox.log"
        $archivedFinalSettingsPath = Join-Path `
            $recycleBinArchiveRoot `
            "final-settings.json"
        $archivedDiskStatesPath = Join-Path `
            $recycleBinArchiveRoot `
            "disk-states.json"
        $archivedFinalFixtureRoot = Join-Path `
            $recycleBinArchiveRoot `
            "final-fixture"
        Copy-Item `
            -LiteralPath $mutateResultPath `
            -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyRestoreResultPath `
            -Destination $archivedVerifyRestoreResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath
        Copy-Item `
            -LiteralPath $recycleBinFixtureRoot `
            -Destination $archivedFinalFixtureRoot `
            -Recurse
        [ordered]@{
            initial = $initialDiskState
            mutate = $mutateDiskState
            verifyRestore = $verifyRestoreDiskState
            postflight = $postflightDiskState
        } | ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath $archivedDiskStatesPath -Encoding UTF8

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside `
                -Root $evidenceRoot `
                -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Recycle Bin preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $resolvedRecoveryRoot = [System.IO.Path]::GetFullPath(
            $recycleBinRecoveryRoot)
        if (-not (Test-Path `
                -LiteralPath $recycleBinRecoveryMarkerPath `
                -PathType Leaf) -or
            -not (Test-PathEqualOrInside `
                -Root $evidenceRoot `
                -Candidate $resolvedRecoveryRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedRecoveryRoot)) {
            throw "Refusing to clean an unowned Recycle Bin recovery root."
        }
        Remove-Item -LiteralPath $resolvedRecoveryRoot -Recurse -Force
        $recoveryRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            runId = $recycleBinRunId
            executablePath = $recycleBinExecutablePath
            executableSha256 = $phaseExecutableHashes[0]
            previewDataRoot = $DataRoot
            sessionPath = $archivedSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalSettingsPath = $archivedFinalSettingsPath
            finalFixtureRoot = $archivedFinalFixtureRoot
            diskStatesPath = $archivedDiskStatesPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.recycleBin
            verifyRestore = $verifyRestore.recycleBin
            postflight = $postflight.recycleBin
            naturalExit = $naturalExit
            processIds = $processIds
            phaseExecutableHashes = $phaseExecutableHashes
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            previewProcessesAfter = $previewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            recoveryRootCleaned = $recoveryRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $archivedSessionTemporaryPath = $archivedSessionPath + ".tmp"
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $sessionJson |
            Set-Content `
                -LiteralPath $archivedSessionTemporaryPath `
                -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedSessionPath `
            -Force
        $sessionJson |
            Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $sessionTemporaryPath `
            -Destination $sessionPath `
            -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            RunId = $recycleBinRunId
            Exe = $recycleBinExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalSettingsPath = $archivedFinalSettingsPath
            FinalFixtureRoot = $archivedFinalFixtureRoot
            DiskStatesPath = $archivedDiskStatesPath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            RecoveryRootCleaned = $recoveryRootCleaned
            Running = $false
        }
    }
    catch {
        $primaryFailure = $_
        if ($recycleSafetyVerified) {
            throw $primaryFailure
        }
        try {
            $compensationPhase = Invoke-RecycleBinPhase `
                -Phase "Compensate" `
                -ResultPath $compensateResultPath
            $recycleBinExecutablePath = [string]$compensationPhase.session.executablePath
            $compensatedDiskState = Get-LocalFileFixtureState `
                -FixtureRoot $recycleBinFixtureRoot
            Assert-RecycleBinDiskState `
                -State $compensatedDiskState `
                -ExpectOwnedOnDisk $true `
                -Name "compensation-independent-disk"
        }
        catch {
            throw "Recycle Bin matrix failed ('$primaryFailure') and its independent compensation failed ('$($_)'). The owned preview/recovery roots and run ID '$recycleBinRunId' were preserved for recovery."
        }
        throw $primaryFailure
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($recycleBinExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $recycleBinExecutablePath
        }
    }
}

if ($scenario -ceq "LocalFileSurfacePersistenceRestart") {
    $localFileScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\local-file-surface-persistence-restart"
    $mutateResultPath = Join-Path $localFileScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $localFileScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path `
        $localFileScenarioRoot `
        "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $localFileArchiveRoot = Join-Path `
        $evidenceRoot `
        "local-file-surface-persistence-restart"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $localFileExecutablePath = $null
    $runtimeFailureLogLines = @()
    $runtimeDeferredPathLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-LocalFilePersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $localFileExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result
        $mutateDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $localFileFixtureRoot
        Assert-LocalFileDiskState `
            -State $mutateDiskState `
            -ExpectMutation $true `
            -Name "mutate-independent-disk"

        $verifyRestorePhase = Invoke-LocalFilePersistencePhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath
        $verifyRestore = $verifyRestorePhase.result
        $verifyRestoreDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $localFileFixtureRoot
        Assert-LocalFileDiskState `
            -State $verifyRestoreDiskState `
            -ExpectMutation $false `
            -Name "verify-restore-independent-disk"

        $postflightPhase = Invoke-LocalFilePersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result
        $postflightDiskState = Get-LocalFileFixtureState `
            -FixtureRoot $localFileFixtureRoot
        Assert-LocalFileDiskState `
            -State $postflightDiskState `
            -ExpectMutation $false `
            -Name "postflight-independent-disk"

        Assert-LocalFileStateEqual `
            -Expected $mutate.localFilePersistence.after `
            -Actual $verifyRestore.localFilePersistence.before `
            -Name "mutate-after-to-verify-before"
        Assert-LocalFileStateEqual `
            -Expected $mutate.localFilePersistence.before `
            -Actual $verifyRestore.localFilePersistence.after `
            -Name "mutate-baseline-to-restored-after"
        Assert-LocalFileStateEqual `
            -Expected $verifyRestore.localFilePersistence.after `
            -Actual $postflight.localFilePersistence.before `
            -Name "restored-after-to-postflight-before"
        Assert-LocalFileStateEqual `
            -Expected $postflight.localFilePersistence.before `
            -Actual $postflight.localFilePersistence.after `
            -Name "postflight-before-to-after"

        foreach ($pair in @(
                @($mutateDiskState, $mutate.localFilePersistence.after, "mutate"),
                @($verifyRestoreDiskState, $verifyRestore.localFilePersistence.after, "verify-restore"),
                @($postflightDiskState, $postflight.localFilePersistence.after, "postflight"))) {
            $runnerDisk = $pair[0]
            $appState = $pair[1]
            $pairName = [string]$pair[2]
            if (-not (Test-PathEqual `
                    -Left ([string]$runnerDisk.fixtureRoot) `
                    -Right ([string]$appState.fixtureRoot))) {
                throw "$pairName independent disk root did not match application evidence."
            }
            $runnerFiles = @($runnerDisk.files)
            $appFiles = @($appState.disk.files)
            if ($runnerFiles.Count -ne $appFiles.Count) {
                throw "$pairName independent disk file count did not match application evidence."
            }
            for ($index = 0; $index -lt $runnerFiles.Count; $index++) {
                foreach ($property in @("relativePath", "length", "sha256")) {
                    Assert-PersistenceScalarEqual `
                        -Expected $runnerFiles[$index].$property `
                        -Actual $appFiles[$index].$property `
                        -Name "$pairName.independentDisk[$index].$property"
                }
            }
        }

        $localFileNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyRestorePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($localFileNaturalExit.Values -contains $false) {
            throw "Local-file matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Local-file matrix did not use three distinct application processes."
        }

        $phaseExecutableHashes = @(
            [string]$mutatePhase.session.executableSha256,
            [string]$verifyRestorePhase.session.executableSha256,
            [string]$postflightPhase.session.executableSha256)
        if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
            throw "Local-file phases did not use one identical audited executable."
        }

        $localFilePreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $localFileExecutablePath)
        if ($localFilePreviewProcessesAfter.Count -ne 0) {
            throw "Local-file matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Local-file matrix did not produce its runtime log."
        }
        $runtimeLogLines = @(Get-Content -LiteralPath $runtimeLogPath)
        $runtimeFailureLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[AotManagedUiSmoke] Failed:", [StringComparison]::Ordinal) -ge 0
            })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Local-file runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }
        $runtimeDeferredPathLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf("[FileTransfer] Shell move", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[FolderPicker]", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[NativeDrop", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("RecycleBin", [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
        if ($runtimeDeferredPathLogLines.Count -gt 0) {
            throw "Local-file matrix entered a deferred Shell, picker, drag/drop, or recycle path: $($runtimeDeferredPathLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the local-file matrix."
        }

        if (Test-Path -LiteralPath $localFileArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $localFileArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $localFileArchiveRoot)) {
                throw "Local-file archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $localFileArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $localFileArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $localFileArchiveRoot "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path `
            $localFileArchiveRoot `
            "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path `
            $localFileArchiveRoot `
            "postflight-result.json"
        $archivedLocalFileSessionPath = Join-Path $localFileArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $localFileArchiveRoot "DeskBox.log"
        $archivedFinalSettingsPath = Join-Path $localFileArchiveRoot "final-settings.json"
        $archivedDiskStatesPath = Join-Path $localFileArchiveRoot "disk-states.json"
        $archivedFinalFixtureRoot = Join-Path $localFileArchiveRoot "final-fixture"
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyRestoreResultPath `
            -Destination $archivedVerifyRestoreResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath
        Copy-Item `
            -LiteralPath $localFileFixtureRoot `
            -Destination $archivedFinalFixtureRoot `
            -Recurse
        [ordered]@{
            mutate = $mutateDiskState
            verifyRestore = $verifyRestoreDiskState
            postflight = $postflightDiskState
        } | ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath $archivedDiskStatesPath -Encoding UTF8

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned local-file preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $localFileExecutablePath
            executableSha256 = $phaseExecutableHashes[0]
            previewDataRoot = $DataRoot
            sessionPath = $archivedLocalFileSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalSettingsPath = $archivedFinalSettingsPath
            finalFixtureRoot = $archivedFinalFixtureRoot
            diskStatesPath = $archivedDiskStatesPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.localFilePersistence
            verifyRestore = $verifyRestore.localFilePersistence
            postflight = $postflight.localFilePersistence
            localFileNaturalExit = $localFileNaturalExit
            processIds = $processIds
            phaseExecutableHashes = $phaseExecutableHashes
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            localFilePreviewProcessesAfter = $localFilePreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
            runtimeDeferredPathLogLines = $runtimeDeferredPathLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedLocalFileSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $sessionJson |
            Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedLocalFileSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $localFileExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedLocalFileSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalSettingsPath = $archivedFinalSettingsPath
            FinalFixtureRoot = $archivedFinalFixtureRoot
            DiskStatesPath = $archivedDiskStatesPath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            DeferredPathLogCount = $runtimeDeferredPathLogLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($localFileExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $localFileExecutablePath
        }
    }
}

if ($scenario -ceq "WeatherSurfacePersistenceRestart") {
    $weatherSurfaceScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\weather-surface-persistence-restart"
    $mutateResultPath = Join-Path $weatherSurfaceScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $weatherSurfaceScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path `
        $weatherSurfaceScenarioRoot `
        "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $weatherSurfaceArchiveRoot = Join-Path `
        $evidenceRoot `
        "weather-surface-persistence-restart"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $weatherSurfaceExecutablePath = $null
    $runtimeFailureLogLines = @()
    $runtimeFixtureLogLines = @()
    $runtimeNetworkLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-WeatherSurfacePersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $weatherSurfaceExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyRestorePhase = Invoke-WeatherSurfacePersistencePhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath
        $verifyRestore = $verifyRestorePhase.result

        $postflightPhase = Invoke-WeatherSurfacePersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-WeatherSurfaceStateEqual `
            -Expected $mutate.weatherSurfacePersistence.after `
            -Actual $verifyRestore.weatherSurfacePersistence.before `
            -Name "mutate-after-to-verify-before"
        Assert-WeatherSurfaceStateEqual `
            -Expected $mutate.weatherSurfacePersistence.before `
            -Actual $verifyRestore.weatherSurfacePersistence.after `
            -Name "mutate-baseline-to-restored-after"
        Assert-WeatherSurfaceStateEqual `
            -Expected $verifyRestore.weatherSurfacePersistence.after `
            -Actual $postflight.weatherSurfacePersistence.before `
            -Name "restored-after-to-postflight-before"
        Assert-WeatherSurfaceStateEqual `
            -Expected $postflight.weatherSurfacePersistence.before `
            -Actual $postflight.weatherSurfacePersistence.after `
            -Name "postflight-before-to-after"

        $weatherSurfaceNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyRestorePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($weatherSurfaceNaturalExit.Values -contains $false) {
            throw "Weather surface matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Weather surface matrix did not use three distinct application processes."
        }

        $phaseExecutableHashes = @(
            [string]$mutatePhase.session.executableSha256,
            [string]$verifyRestorePhase.session.executableSha256,
            [string]$postflightPhase.session.executableSha256)
        if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
            throw "Weather surface phases did not use one identical audited executable."
        }

        $weatherSurfacePreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $weatherSurfaceExecutablePath)
        if ($weatherSurfacePreviewProcessesAfter.Count -ne 0) {
            throw "Weather surface matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Weather surface matrix did not produce its runtime log."
        }
        $runtimeLogLines = @(Get-Content -LiteralPath $runtimeLogPath)
        $runtimeFailureLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[AotManagedUiSmoke] Failed:", [StringComparison]::Ordinal) -ge 0
            })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Weather surface runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }
        $runtimeFixtureLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf(
                    "[AotWeatherSurfaceFixture] Served deterministic WeatherData request",
                    [StringComparison]::Ordinal) -ge 0
            })
        if ($runtimeFixtureLogLines.Count -lt 3) {
            throw "Weather surface matrix did not serve deterministic WeatherData in every process."
        }
        $runtimeNetworkLogLines = @(
            $runtimeLogLines | Where-Object {
                $_.IndexOf("[WeatherService]", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[WindowsLocation]", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[Weather] Auto-location", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[WeatherWidget] Refresh failed", [StringComparison]::Ordinal) -ge 0
            })
        if ($runtimeNetworkLogLines.Count -gt 0) {
            throw "Weather surface matrix entered a production network or location path: $($runtimeNetworkLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Weather surface matrix."
        }

        if (Test-Path -LiteralPath $weatherSurfaceArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $weatherSurfaceArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $weatherSurfaceArchiveRoot)) {
                throw "Weather surface archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $weatherSurfaceArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $weatherSurfaceArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $weatherSurfaceArchiveRoot "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path `
            $weatherSurfaceArchiveRoot `
            "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path `
            $weatherSurfaceArchiveRoot `
            "postflight-result.json"
        $archivedWeatherSurfaceSessionPath = Join-Path $weatherSurfaceArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $weatherSurfaceArchiveRoot "DeskBox.log"
        $archivedFinalSettingsPath = Join-Path $weatherSurfaceArchiveRoot "final-settings.json"
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyRestoreResultPath `
            -Destination $archivedVerifyRestoreResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Weather surface preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $weatherSurfaceExecutablePath
            executableSha256 = $phaseExecutableHashes[0]
            previewDataRoot = $DataRoot
            sessionPath = $archivedWeatherSurfaceSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalSettingsPath = $archivedFinalSettingsPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.weatherSurfacePersistence
            verifyRestore = $verifyRestore.weatherSurfacePersistence
            postflight = $postflight.weatherSurfacePersistence
            weatherSurfaceNaturalExit = $weatherSurfaceNaturalExit
            processIds = $processIds
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            weatherSurfacePreviewProcessesAfter =
                $weatherSurfacePreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
            runtimeFixtureLogLines = $runtimeFixtureLogLines
            runtimeNetworkLogLines = $runtimeNetworkLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedWeatherSurfaceSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $sessionJson |
            Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedWeatherSurfaceSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $weatherSurfaceExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedWeatherSurfaceSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalSettingsPath = $archivedFinalSettingsPath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            FixtureLogCount = $runtimeFixtureLogLines.Count
            NetworkLogCount = $runtimeNetworkLogLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($weatherSurfaceExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $weatherSurfaceExecutablePath
        }
    }
}

if ($scenario -ceq "WeatherSettingsPersistenceRestart") {
    $weatherScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\weather-settings-persistence-restart"
    $mutateResultPath = Join-Path $weatherScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $weatherScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path `
        $weatherScenarioRoot `
        "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $weatherArchiveRoot = Join-Path `
        $evidenceRoot `
        "weather-settings-persistence-restart"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $weatherExecutablePath = $null
    $runtimeFailureLogLines = @()
    $runtimeWeatherInitializationLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-WeatherSettingsPersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $weatherExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyRestorePhase = Invoke-WeatherSettingsPersistencePhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath
        $verifyRestore = $verifyRestorePhase.result

        $postflightPhase = Invoke-WeatherSettingsPersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-WeatherSettingsStateEqual `
            -Expected $mutate.weatherSettingsPersistence.after `
            -Actual $verifyRestore.weatherSettingsPersistence.before `
            -Name "mutate-after-to-verify-before"
        Assert-WeatherSettingsStateEqual `
            -Expected $mutate.weatherSettingsPersistence.before `
            -Actual $verifyRestore.weatherSettingsPersistence.after `
            -Name "mutate-baseline-to-restored-after"
        Assert-WeatherSettingsStateEqual `
            -Expected $verifyRestore.weatherSettingsPersistence.after `
            -Actual $postflight.weatherSettingsPersistence.before `
            -Name "restored-after-to-postflight-before"
        Assert-WeatherSettingsStateEqual `
            -Expected $postflight.weatherSettingsPersistence.before `
            -Actual $postflight.weatherSettingsPersistence.after `
            -Name "postflight-before-to-after"

        $weatherSettingsNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyRestorePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($weatherSettingsNaturalExit.Values -contains $false) {
            throw "Weather settings matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Weather settings matrix did not use three distinct application processes."
        }

        $phaseExecutableHashes = @(
            [string]$mutatePhase.session.executableSha256,
            [string]$verifyRestorePhase.session.executableSha256,
            [string]$postflightPhase.session.executableSha256)
        if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
            throw "Weather settings phases did not use one identical audited executable."
        }

        $weatherSettingsPreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $weatherExecutablePath)
        if ($weatherSettingsPreviewProcessesAfter.Count -ne 0) {
            throw "Weather settings matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Weather settings matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Weather settings runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }
        $runtimeWeatherInitializationLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("[WeatherService]", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[WeatherWidgetViewModel]", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeWeatherInitializationLines.Count -gt 0) {
            throw "Weather settings matrix entered deferred Weather data initialization: $($runtimeWeatherInitializationLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Weather settings matrix."
        }

        if (Test-Path -LiteralPath $weatherArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $weatherArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $weatherArchiveRoot)) {
                throw "Weather settings archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $weatherArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $weatherArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $weatherArchiveRoot "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path `
            $weatherArchiveRoot `
            "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path `
            $weatherArchiveRoot `
            "postflight-result.json"
        $archivedWeatherSessionPath = Join-Path $weatherArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $weatherArchiveRoot "DeskBox.log"
        $archivedFinalSettingsPath = Join-Path $weatherArchiveRoot "final-settings.json"
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyRestoreResultPath `
            -Destination $archivedVerifyRestoreResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Weather settings preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $weatherExecutablePath
            executableSha256 = $phaseExecutableHashes[0]
            previewDataRoot = $DataRoot
            sessionPath = $archivedWeatherSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalSettingsPath = $archivedFinalSettingsPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.weatherSettingsPersistence
            verifyRestore = $verifyRestore.weatherSettingsPersistence
            postflight = $postflight.weatherSettingsPersistence
            weatherSettingsNaturalExit = $weatherSettingsNaturalExit
            processIds = $processIds
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            weatherSettingsPreviewProcessesAfter =
                $weatherSettingsPreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
            runtimeWeatherInitializationLines = $runtimeWeatherInitializationLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedWeatherSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 24
        $sessionJson |
            Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedWeatherSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $weatherExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedWeatherSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalSettingsPath = $archivedFinalSettingsPath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            WeatherInitializationLogCount = $runtimeWeatherInitializationLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($weatherExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $weatherExecutablePath
        }
    }
}

if ($scenario -ceq "GlancePersistenceRestart") {
    $glanceScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\glance-persistence-restart"
    $mutateResultPath = Join-Path $glanceScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $glanceScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path `
        $glanceScenarioRoot `
        "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $glanceArchiveRoot = Join-Path `
        $evidenceRoot `
        "glance-persistence-restart"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $fixtureSha256Before = Get-FileSha256 -Path $glanceImageFixturePath
    $glanceExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-GlancePersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath `
            -FixturePath $glanceImageFixturePath
        $glanceExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyRestorePhase = Invoke-GlancePersistencePhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath `
            -FixturePath $glanceImageFixturePath
        $verifyRestore = $verifyRestorePhase.result

        $postflightPhase = Invoke-GlancePersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath `
            -FixturePath $glanceImageFixturePath
        $postflight = $postflightPhase.result

        Assert-GlanceStateEqual `
            -Expected $mutate.glancePersistence.after `
            -Actual $verifyRestore.glancePersistence.before `
            -Name "mutate-after-to-verify-before"
        Assert-GlanceStateEqual `
            -Expected $mutate.glancePersistence.before `
            -Actual $verifyRestore.glancePersistence.after `
            -Name "mutate-baseline-to-restored-after"
        Assert-GlanceStateEqual `
            -Expected $verifyRestore.glancePersistence.after `
            -Actual $postflight.glancePersistence.before `
            -Name "restored-after-to-postflight-before"
        Assert-GlanceStateEqual `
            -Expected $postflight.glancePersistence.before `
            -Actual $postflight.glancePersistence.after `
            -Name "postflight-before-to-after"

        $glanceNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyRestore = [bool]$verifyRestorePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($glanceNaturalExit.Values -contains $false) {
            throw "Glance persistence matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Glance persistence matrix did not use three distinct application processes."
        }

        $phaseExecutableHashes = @(
            [string]$mutatePhase.session.executableSha256,
            [string]$verifyRestorePhase.session.executableSha256,
            [string]$postflightPhase.session.executableSha256)
        if (@($phaseExecutableHashes | Sort-Object -Unique).Count -ne 1) {
            throw "Glance persistence phases did not use one identical audited executable."
        }
        $fixtureSha256After = Get-FileSha256 -Path $glanceImageFixturePath
        if (-not [string]::Equals(
                $fixtureSha256Before,
                $fixtureSha256After,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Glance persistence matrix changed its owned image fixture."
        }

        $glancePreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $glanceExecutablePath)
        if ($glancePreviewProcessesAfter.Count -ne 0) {
            throw "Glance persistence matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Glance persistence matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[GlanceWidgetContent] Image decode failed", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Glance persistence runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Glance persistence matrix."
        }

        if (Test-Path -LiteralPath $glanceArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $glanceArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $glanceArchiveRoot)) {
                throw "Glance archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $glanceArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $glanceArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $glanceArchiveRoot "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path `
            $glanceArchiveRoot `
            "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path `
            $glanceArchiveRoot `
            "postflight-result.json"
        $archivedGlanceSessionPath = Join-Path $glanceArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $glanceArchiveRoot "DeskBox.log"
        $archivedFinalStorePath = Join-Path $glanceArchiveRoot "final-glance.json"
        $archivedFinalSettingsPath = Join-Path $glanceArchiveRoot "final-settings.json"
        $archivedFixturePath = Join-Path $glanceArchiveRoot "glance-local.png"
        if (-not (Test-Path -LiteralPath $glanceStorePath -PathType Leaf)) {
            throw "Glance persistence matrix did not leave final per-widget store evidence."
        }
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyRestoreResultPath `
            -Destination $archivedVerifyRestoreResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $glanceStorePath -Destination $archivedFinalStorePath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath
        Copy-Item -LiteralPath $glanceImageFixturePath -Destination $archivedFixturePath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Glance persistence preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $glanceExecutablePath
            executableSha256 = $phaseExecutableHashes[0]
            previewDataRoot = $DataRoot
            sessionPath = $archivedGlanceSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalStorePath = $archivedFinalStorePath
            finalSettingsPath = $archivedFinalSettingsPath
            fixturePath = $archivedFixturePath
            fixtureSha256Before = $fixtureSha256Before
            fixtureSha256After = $fixtureSha256After
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.glancePersistence
            verifyRestore = $verifyRestore.glancePersistence
            postflight = $postflight.glancePersistence
            glanceNaturalExit = $glanceNaturalExit
            processIds = $processIds
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            glancePreviewProcessesAfter = $glancePreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedGlanceSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $sessionJson |
            Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedGlanceSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $glanceExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedGlanceSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalStorePath = $archivedFinalStorePath
            FixturePath = $archivedFixturePath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($glanceExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $glanceExecutablePath
        }
    }
}

if ($scenario -ceq "TodoAttachmentsPersistenceRestart") {
    $todoAttachmentsScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\todo-attachments-persistence-restart"
    $mutateResultPath = Join-Path $todoAttachmentsScenarioRoot "mutate\result.json"
    $verifyDeleteResultPath = Join-Path `
        $todoAttachmentsScenarioRoot `
        "verify-delete\result.json"
    $postflightResultPath = Join-Path `
        $todoAttachmentsScenarioRoot `
        "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $todoAttachmentsArchiveRoot = Join-Path `
        $evidenceRoot `
        "todo-attachments-persistence-restart"
    $productionDataFingerprintBefore =
        Get-DirectoryStateFingerprint -Path $productionDataRoot
    $fixtureSha256 = Get-FileSha256 -Path $todoAttachmentFixturePath
    $todoAttachmentsExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-TodoAttachmentsPersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $todoAttachmentsExecutablePath =
            [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $managedAttachmentPath =
            [string]$mutate.todoAttachmentsPersistence.managedAttachmentPath
        $managedAttachmentRoot = Join-Path `
            $dataDirectory `
            "widgets\aot-5b4b2b2b2-todo-attachments\attachments"
        if ([string]::IsNullOrWhiteSpace($managedAttachmentPath) -or
            -not (Test-PathEqualOrInside `
                -Root $managedAttachmentRoot `
                -Candidate $managedAttachmentPath) -or
            (Test-PathEqual -Left $managedAttachmentRoot -Right $managedAttachmentPath) -or
            -not (Test-Path -LiteralPath $managedAttachmentPath -PathType Leaf)) {
            throw "Todo attachments mutation did not create its owned managed file."
        }
        $managedAttachmentSha256 = Get-FileSha256 -Path $managedAttachmentPath
        if (-not [string]::Equals(
                $managedAttachmentSha256,
                $fixtureSha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Todo managed attachment content does not match its owned fixture."
        }

        $verifyDeletePhase = Invoke-TodoAttachmentsPersistencePhase `
            -Phase "VerifyDelete" `
            -ResultPath $verifyDeleteResultPath
        $verifyDelete = $verifyDeletePhase.result

        $postflightPhase = Invoke-TodoAttachmentsPersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-TodoStateEqual `
            -Expected $mutate.todoAttachmentsPersistence.after `
            -Actual $verifyDelete.todoAttachmentsPersistence.before `
            -Name "mutate-after-to-verify-delete-before"
        Assert-TodoStateEqual `
            -Expected $verifyDelete.todoAttachmentsPersistence.after `
            -Actual $postflight.todoAttachmentsPersistence.before `
            -Name "verify-delete-after-to-postflight-before"
        Assert-TodoStateEqual `
            -Expected $postflight.todoAttachmentsPersistence.before `
            -Actual $postflight.todoAttachmentsPersistence.after `
            -Name "postflight-before-to-after"

        $afterAttachmentDelete =
            $verifyDelete.todoAttachmentsPersistence.afterAttachmentDelete
        if ($null -eq $afterAttachmentDelete -or
            @($afterAttachmentDelete.items).Count -ne 1 -or
            [int]$afterAttachmentDelete.items[0].attachmentCount -ne 0 -or
            @($afterAttachmentDelete.items[0].attachments).Count -ne 0 -or
            [int]$afterAttachmentDelete.managedAttachmentFileCount -ne 0 -or
            @($afterAttachmentDelete.managedAttachmentRelativePaths).Count -ne 0 -or
            [int]$afterAttachmentDelete.attachmentUiItemCount -ne 0) {
            throw "Todo attachment deletion did not retain a clean zero-attachment task state."
        }
        if (Test-Path -LiteralPath $managedAttachmentPath -PathType Leaf) {
            throw "Todo attachment verify/delete left the managed file behind."
        }
        $managedFilesAfterDelete = @(
            if (Test-Path -LiteralPath $managedAttachmentRoot -PathType Container) {
                Get-ChildItem `
                    -LiteralPath $managedAttachmentRoot `
                    -File `
                    -Recurse `
                    -Force
            }
        )
        if ($managedFilesAfterDelete.Count -ne 0) {
            throw "Todo attachment postflight retained managed files."
        }

        $todoAttachmentsNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyDelete = [bool]$verifyDeletePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($todoAttachmentsNaturalExit.Values -contains $false) {
            throw "Todo attachments matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyDelete.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Todo attachments matrix did not use three distinct application processes."
        }

        $todoAttachmentsPreviewProcessesAfter = @(
            Get-ExactPreviewProcesses `
                -ExecutablePath $todoAttachmentsExecutablePath)
        if ($todoAttachmentsPreviewProcessesAfter.Count -ne 0) {
            throw "Todo attachments matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Todo attachments matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Todo attachments runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter =
            Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne
                [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne
                [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Todo attachments matrix."
        }

        if (Test-Path -LiteralPath $todoAttachmentsArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $todoAttachmentsArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $todoAttachmentsArchiveRoot)) {
                throw "Todo attachments archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $todoAttachmentsArchiveRoot -Recurse -Force
        }
        New-Item `
            -ItemType Directory `
            -Path $todoAttachmentsArchiveRoot `
            -Force | Out-Null
        $archivedMutateResultPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "mutate-result.json"
        $archivedVerifyDeleteResultPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "verify-delete-result.json"
        $archivedPostflightResultPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "postflight-result.json"
        $archivedTodoAttachmentsSessionPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "session.json"
        $archivedRuntimeLogPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "DeskBox.log"
        $archivedFinalStorePath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "final-todo.json"
        $archivedFinalSettingsPath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "final-settings.json"
        $archivedFixturePath = Join-Path `
            $todoAttachmentsArchiveRoot `
            "todo-managed-attachment.txt"
        $finalStorePath = Join-Path `
            $dataDirectory `
            "widgets\aot-5b4b2b2b2-todo-attachments\todo.json"
        if (-not (Test-Path -LiteralPath $finalStorePath -PathType Leaf)) {
            throw "Todo attachments matrix did not leave final store evidence."
        }
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyDeleteResultPath `
            -Destination $archivedVerifyDeleteResultPath
        Copy-Item `
            -LiteralPath $postflightResultPath `
            -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $finalStorePath -Destination $archivedFinalStorePath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath
        Copy-Item -LiteralPath $todoAttachmentFixturePath -Destination $archivedFixturePath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Todo attachments preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $todoAttachmentsExecutablePath
            executableSha256 = $postflightPhase.session.executableSha256
            previewDataRoot = $DataRoot
            sessionPath = $archivedTodoAttachmentsSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyDeleteResultPath = $archivedVerifyDeleteResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalStorePath = $archivedFinalStorePath
            finalSettingsPath = $archivedFinalSettingsPath
            fixturePath = $archivedFixturePath
            fixtureSha256 = $fixtureSha256
            managedAttachmentPath = $managedAttachmentPath
            managedAttachmentSha256 = $managedAttachmentSha256
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.todoAttachmentsPersistence
            verifyDelete = $verifyDelete.todoAttachmentsPersistence
            postflight = $postflight.todoAttachmentsPersistence
            afterAttachmentDelete = $afterAttachmentDelete
            todoAttachmentsNaturalExit = $todoAttachmentsNaturalExit
            processIds = $processIds
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore =
                $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter =
                $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter =
                $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            todoAttachmentsPreviewProcessesAfter =
                $todoAttachmentsPreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath =
            $archivedTodoAttachmentsSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $sessionJson |
            Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedTodoAttachmentsSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $todoAttachmentsExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedTodoAttachmentsSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyDeleteResultPath = $archivedVerifyDeleteResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalStorePath = $archivedFinalStorePath
            FixturePath = $archivedFixturePath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint =
                $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($todoAttachmentsExecutablePath)) {
            Stop-ExactPreviewProcess `
                -ExecutablePath $todoAttachmentsExecutablePath
        }
    }
}

if ($scenario -ceq "TodoStepsPersistenceRestart") {
    $todoStepsScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\todo-steps-persistence-restart"
    $mutateResultPath = Join-Path $todoStepsScenarioRoot "mutate\result.json"
    $verifyDeleteResultPath = Join-Path `
        $todoStepsScenarioRoot `
        "verify-delete\result.json"
    $postflightResultPath = Join-Path $todoStepsScenarioRoot "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $todoStepsArchiveRoot = Join-Path `
        $evidenceRoot `
        "todo-steps-persistence-restart"
    $productionDataFingerprintBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
    $todoStepsExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-TodoStepsPersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $todoStepsExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyDeletePhase = Invoke-TodoStepsPersistencePhase `
            -Phase "VerifyDelete" `
            -ResultPath $verifyDeleteResultPath
        $verifyDelete = $verifyDeletePhase.result

        $postflightPhase = Invoke-TodoStepsPersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-TodoStateEqual `
            -Expected $mutate.todoStepsPersistence.after `
            -Actual $verifyDelete.todoStepsPersistence.before `
            -Name "todo-steps-mutate-after-to-verify-delete-before"
        Assert-TodoStateEqual `
            -Expected $verifyDelete.todoStepsPersistence.after `
            -Actual $postflight.todoStepsPersistence.before `
            -Name "todo-steps-verify-delete-after-to-postflight-before"
        Assert-TodoStateEqual `
            -Expected $postflight.todoStepsPersistence.before `
            -Actual $postflight.todoStepsPersistence.after `
            -Name "todo-steps-postflight-before-to-after"

        $afterStepMutation = $verifyDelete.todoStepsPersistence.afterStepMutation
        if ($null -eq $afterStepMutation -or
            @($afterStepMutation.items).Count -ne 1 -or
            @($afterStepMutation.items[0].steps).Count -ne 1 -or
            [bool]$afterStepMutation.items[0].steps[0].isCompleted -or
            -not [string]::Equals(
                [string]$afterStepMutation.items[0].steps[0].text,
                "AOT Todo persisted edited step",
                [System.StringComparison]::Ordinal) -or
            [int]$afterStepMutation.stepUiItemCount -ne 1 -or
            [bool]$afterStepMutation.stepUiIsChecked) {
            throw "Todo steps verify/delete did not archive the completion round-trip state."
        }
        $afterStepDelete = $verifyDelete.todoStepsPersistence.afterStepDelete
        if ($null -eq $afterStepDelete -or
            @($afterStepDelete.items).Count -ne 1 -or
            @($afterStepDelete.items[0].steps).Count -ne 0 -or
            [int]$afterStepDelete.items[0].stepCount -ne 0 -or
            [int]$afterStepDelete.stepUiItemCount -ne 0 -or
            [bool]$afterStepDelete.stepUiContainerRealized) {
            throw "Todo steps verify/delete did not archive the zero-step task state."
        }

        $todoStepsNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyDelete = [bool]$verifyDeletePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($todoStepsNaturalExit.Values -contains $false) {
            throw "Todo steps persistence matrix did not exit naturally in every phase."
        }
        $processIds = @(
            [int]$mutate.processId,
            [int]$verifyDelete.processId,
            [int]$postflight.processId)
        if (@($processIds | Sort-Object -Unique).Count -ne 3) {
            throw "Todo steps persistence matrix did not use three distinct process IDs."
        }

        $todoStepsPreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $todoStepsExecutablePath)
        if ($todoStepsPreviewProcessesAfter.Count -ne 0) {
            throw "Todo steps persistence matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Todo steps persistence matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Todo steps runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Todo steps persistence matrix."
        }

        if (Test-Path -LiteralPath $todoStepsArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $todoStepsArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $todoStepsArchiveRoot)) {
                throw "Todo steps archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $todoStepsArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $todoStepsArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $todoStepsArchiveRoot "mutate-result.json"
        $archivedVerifyDeleteResultPath = Join-Path `
            $todoStepsArchiveRoot `
            "verify-delete-result.json"
        $archivedPostflightResultPath = Join-Path `
            $todoStepsArchiveRoot `
            "postflight-result.json"
        $archivedTodoStepsSessionPath = Join-Path $todoStepsArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $todoStepsArchiveRoot "DeskBox.log"
        $archivedFinalStorePath = Join-Path $todoStepsArchiveRoot "final-todo.json"
        $archivedFinalSettingsPath = Join-Path $todoStepsArchiveRoot "final-settings.json"
        $finalStorePath = Join-Path `
            $dataDirectory `
            "widgets\aot-5b4b2b2b1-todo-steps\todo.json"
        if (-not (Test-Path -LiteralPath $finalStorePath -PathType Leaf)) {
            throw "Todo steps persistence matrix did not leave final store evidence."
        }
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyDeleteResultPath `
            -Destination $archivedVerifyDeleteResultPath
        Copy-Item -LiteralPath $postflightResultPath -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $finalStorePath -Destination $archivedFinalStorePath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Todo steps preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $todoStepsExecutablePath
            executableSha256 = $postflightPhase.session.executableSha256
            previewDataRoot = $DataRoot
            sessionPath = $archivedTodoStepsSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyDeleteResultPath = $archivedVerifyDeleteResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalStorePath = $archivedFinalStorePath
            finalSettingsPath = $archivedFinalSettingsPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.todoStepsPersistence
            verifyDelete = $verifyDelete.todoStepsPersistence
            postflight = $postflight.todoStepsPersistence
            afterStepMutation = $afterStepMutation
            afterStepDelete = $afterStepDelete
            todoStepsNaturalExit = $todoStepsNaturalExit
            processIds = $processIds
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore = $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter = $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter = $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            todoStepsPreviewProcessesAfter = $todoStepsPreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedTodoStepsSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 32
        $sessionJson | Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedTodoStepsSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $todoStepsExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedTodoStepsSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyDeleteResultPath = $archivedVerifyDeleteResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalStorePath = $archivedFinalStorePath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint = $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($todoStepsExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $todoStepsExecutablePath
        }
    }
}

if ($scenario -ceq "TodoPersistenceRestart") {
    $todoScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\todo-persistence-restart"
    $mutateResultPath = Join-Path $todoScenarioRoot "mutate\result.json"
    $verifyDeleteResultPath = Join-Path `
        $todoScenarioRoot `
        "verify-delete\result.json"
    $postflightResultPath = Join-Path $todoScenarioRoot "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $todoArchiveRoot = Join-Path $evidenceRoot "todo-persistence-restart"
    $productionDataFingerprintBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
    $todoExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-TodoPersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $todoExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyDeletePhase = Invoke-TodoPersistencePhase `
            -Phase "VerifyDelete" `
            -ResultPath $verifyDeleteResultPath
        $verifyDelete = $verifyDeletePhase.result

        $postflightPhase = Invoke-TodoPersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-TodoStateEqual `
            -Expected $mutate.todoPersistence.after `
            -Actual $verifyDelete.todoPersistence.before `
            -Name "mutate-after-to-verify-delete-before"
        Assert-TodoStateEqual `
            -Expected $verifyDelete.todoPersistence.after `
            -Actual $postflight.todoPersistence.before `
            -Name "verify-delete-after-to-postflight-before"
        Assert-TodoStateEqual `
            -Expected $postflight.todoPersistence.before `
            -Actual $postflight.todoPersistence.after `
            -Name "postflight-before-to-after"

        $afterExplicitSave = $verifyDelete.todoPersistence.afterExplicitSave
        if ($null -eq $afterExplicitSave -or
            @($afterExplicitSave.items).Count -ne 1 -or
            [bool]$afterExplicitSave.items[0].isCompleted -or
            -not [string]::Equals(
                [string]$afterExplicitSave.items[0].notes,
                "AOT Todo explicit restart save notes",
                [System.StringComparison]::Ordinal)) {
            throw "Todo verify/delete did not archive the explicit save state."
        }

        $todoNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyDelete = [bool]$verifyDeletePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($todoNaturalExit.Values -contains $false) {
            throw "Todo persistence matrix did not exit naturally in every phase."
        }

        $todoPreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $todoExecutablePath)
        if ($todoPreviewProcessesAfter.Count -ne 0) {
            throw "Todo persistence matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Todo persistence matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Todo runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Todo persistence matrix."
        }

        if (Test-Path -LiteralPath $todoArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $todoArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $todoArchiveRoot)) {
                throw "Todo archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $todoArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $todoArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $todoArchiveRoot "mutate-result.json"
        $archivedVerifyDeleteResultPath = Join-Path `
            $todoArchiveRoot `
            "verify-delete-result.json"
        $archivedPostflightResultPath = Join-Path `
            $todoArchiveRoot `
            "postflight-result.json"
        $archivedTodoSessionPath = Join-Path $todoArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $todoArchiveRoot "DeskBox.log"
        $archivedFinalStorePath = Join-Path $todoArchiveRoot "final-todo.json"
        $archivedFinalSettingsPath = Join-Path $todoArchiveRoot "final-settings.json"
        $finalStorePath = Join-Path `
            $dataDirectory `
            "widgets\aot-5b4b2b2a-todo\todo.json"
        if (-not (Test-Path -LiteralPath $finalStorePath -PathType Leaf)) {
            throw "Todo persistence matrix did not leave final store evidence."
        }
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyDeleteResultPath `
            -Destination $archivedVerifyDeleteResultPath
        Copy-Item -LiteralPath $postflightResultPath -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $finalStorePath -Destination $archivedFinalStorePath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Todo preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $todoExecutablePath
            executableSha256 = $postflightPhase.session.executableSha256
            previewDataRoot = $DataRoot
            sessionPath = $archivedTodoSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyDeleteResultPath = $archivedVerifyDeleteResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalStorePath = $archivedFinalStorePath
            finalSettingsPath = $archivedFinalSettingsPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.todoPersistence
            verifyDelete = $verifyDelete.todoPersistence
            postflight = $postflight.todoPersistence
            afterExplicitSave = $afterExplicitSave
            todoNaturalExit = $todoNaturalExit
            processIds = @(
                [int]$mutate.processId,
                [int]$verifyDelete.processId,
                [int]$postflight.processId)
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore = $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter = $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter = $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            todoPreviewProcessesAfter = $todoPreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedTodoSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 24
        $sessionJson | Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedTodoSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $todoExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedTodoSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyDeleteResultPath = $archivedVerifyDeleteResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalStorePath = $archivedFinalStorePath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint = $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($todoExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $todoExecutablePath
        }
    }
}

if ($scenario -ceq "QuickCapturePersistenceRestart") {
    $quickCaptureScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\quick-capture-persistence-restart"
    $mutateResultPath = Join-Path $quickCaptureScenarioRoot "mutate\result.json"
    $verifyDeleteResultPath = Join-Path `
        $quickCaptureScenarioRoot `
        "verify-delete\result.json"
    $postflightResultPath = Join-Path $quickCaptureScenarioRoot "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $quickCaptureArchiveRoot = Join-Path `
        $evidenceRoot `
        "quick-capture-persistence-restart"
    $productionDataFingerprintBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
    $fixtureSha256 = Get-FileSha256 -Path $quickCaptureAttachmentFixturePath
    $quickCaptureExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-QuickCapturePersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $quickCaptureExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $managedAttachmentPath =
            [string]$mutate.quickCapturePersistence.managedAttachmentPath
        $managedAttachmentRoot = Join-Path `
            $dataDirectory `
            "quick-capture\attachments"
        if ([string]::IsNullOrWhiteSpace($managedAttachmentPath) -or
            -not (Test-PathEqualOrInside `
                -Root $managedAttachmentRoot `
                -Candidate $managedAttachmentPath) -or
            (Test-PathEqual -Left $managedAttachmentRoot -Right $managedAttachmentPath) -or
            -not (Test-Path -LiteralPath $managedAttachmentPath -PathType Leaf)) {
            throw "Quick Capture mutation did not create its owned managed attachment."
        }
        $managedAttachmentSha256 = Get-FileSha256 -Path $managedAttachmentPath
        if (-not [string]::Equals(
                $managedAttachmentSha256,
                $fixtureSha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Quick Capture managed attachment content does not match its fixture."
        }

        $verifyDeletePhase = Invoke-QuickCapturePersistencePhase `
            -Phase "VerifyDelete" `
            -ResultPath $verifyDeleteResultPath
        $verifyDelete = $verifyDeletePhase.result

        $postflightPhase = Invoke-QuickCapturePersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-QuickCaptureStateEqual `
            -Expected $mutate.quickCapturePersistence.after `
            -Actual $verifyDelete.quickCapturePersistence.before `
            -Name "mutate-after-to-verify-delete-before"
        Assert-QuickCaptureStateEqual `
            -Expected $verifyDelete.quickCapturePersistence.after `
            -Actual $postflight.quickCapturePersistence.before `
            -Name "verify-delete-after-to-postflight-before"
        Assert-QuickCaptureStateEqual `
            -Expected $postflight.quickCapturePersistence.before `
            -Actual $postflight.quickCapturePersistence.after `
            -Name "postflight-before-to-after"

        if (Test-Path -LiteralPath $managedAttachmentPath) {
            throw "Quick Capture verify/delete left the managed attachment file behind."
        }
        if (@($verifyDelete.quickCapturePersistence.after.managedAttachmentRelativePaths).Count -ne 0 -or
            @($postflight.quickCapturePersistence.before.managedAttachmentRelativePaths).Count -ne 0) {
            throw "Quick Capture delete/postflight evidence retained managed attachment paths."
        }

        $quickCaptureNaturalExit = [ordered]@{
            mutate = [bool]$mutatePhase.naturalExit
            verifyDelete = [bool]$verifyDeletePhase.naturalExit
            postflight = [bool]$postflightPhase.naturalExit
        }
        if ($quickCaptureNaturalExit.Values -contains $false) {
            throw "Quick Capture persistence matrix did not exit naturally in every phase."
        }

        $quickCapturePreviewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $quickCaptureExecutablePath)
        if ($quickCapturePreviewProcessesAfter.Count -ne 0) {
            throw "Quick Capture persistence matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Quick Capture persistence matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Quick Capture runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the Quick Capture persistence matrix."
        }

        if (Test-Path -LiteralPath $quickCaptureArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside `
                    -Root $evidenceRoot `
                    -Candidate $quickCaptureArchiveRoot) -or
                (Test-PathEqual `
                    -Left $evidenceRoot `
                    -Right $quickCaptureArchiveRoot)) {
                throw "Quick Capture archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $quickCaptureArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $quickCaptureArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $quickCaptureArchiveRoot "mutate-result.json"
        $archivedVerifyDeleteResultPath = Join-Path `
            $quickCaptureArchiveRoot `
            "verify-delete-result.json"
        $archivedPostflightResultPath = Join-Path `
            $quickCaptureArchiveRoot `
            "postflight-result.json"
        $archivedQuickCaptureSessionPath = Join-Path $quickCaptureArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $quickCaptureArchiveRoot "DeskBox.log"
        $archivedFinalStorePath = Join-Path `
            $quickCaptureArchiveRoot `
            "final-quick-capture.json"
        $archivedFixturePath = Join-Path `
            $quickCaptureArchiveRoot `
            "quick-capture-attachment.txt"
        $finalStorePath = Join-Path `
            $dataDirectory `
            "quick-capture\quick-capture.json"
        if (-not (Test-Path -LiteralPath $finalStorePath -PathType Leaf)) {
            throw "Quick Capture persistence matrix did not leave final store evidence."
        }
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item `
            -LiteralPath $verifyDeleteResultPath `
            -Destination $archivedVerifyDeleteResultPath
        Copy-Item -LiteralPath $postflightResultPath -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $finalStorePath -Destination $archivedFinalStorePath
        Copy-Item `
            -LiteralPath $quickCaptureAttachmentFixturePath `
            -Destination $archivedFixturePath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned Quick Capture preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $quickCaptureExecutablePath
            executableSha256 = $postflightPhase.session.executableSha256
            previewDataRoot = $DataRoot
            sessionPath = $archivedQuickCaptureSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyDeleteResultPath = $archivedVerifyDeleteResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalStorePath = $archivedFinalStorePath
            fixturePath = $archivedFixturePath
            fixtureSha256 = $fixtureSha256
            managedAttachmentSha256 = $managedAttachmentSha256
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.quickCapturePersistence
            verifyDelete = $verifyDelete.quickCapturePersistence
            postflight = $postflight.quickCapturePersistence
            quickCaptureNaturalExit = $quickCaptureNaturalExit
            processIds = @(
                [int]$mutate.processId,
                [int]$verifyDelete.processId,
                [int]$postflight.processId)
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore = $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter = $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter = $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            quickCapturePreviewProcessesAfter = $quickCapturePreviewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedQuickCaptureSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 24
        $sessionJson | Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedQuickCaptureSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $quickCaptureExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedQuickCaptureSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyDeleteResultPath = $archivedVerifyDeleteResultPath
            PostflightResultPath = $archivedPostflightResultPath
            FinalStorePath = $archivedFinalStorePath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint = $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($quickCaptureExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $quickCaptureExecutablePath
        }
    }
}

if ($scenario -ceq "SettingsWidgetPersistenceRestart") {
    $persistenceScenarioRoot = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\settings-widget-persistence-restart"
    $mutateResultPath = Join-Path $persistenceScenarioRoot "mutate\result.json"
    $verifyRestoreResultPath = Join-Path `
        $persistenceScenarioRoot `
        "verify-restore\result.json"
    $postflightResultPath = Join-Path $persistenceScenarioRoot "postflight\result.json"
    $sessionPath = Join-Path $evidenceRoot "session.json"
    $persistenceArchiveRoot = Join-Path `
        $evidenceRoot `
        "settings-widget-persistence-restart"
    $productionDataFingerprintBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
    $persistenceExecutablePath = $null
    $runtimeFailureLogLines = @()
    $previewRootCleaned = $false

    try {
        $mutatePhase = Invoke-PersistencePhase `
            -Phase "Mutate" `
            -ResultPath $mutateResultPath
        $persistenceExecutablePath = [string]$mutatePhase.session.executablePath
        $mutate = $mutatePhase.result

        $verifyRestorePhase = Invoke-PersistencePhase `
            -Phase "VerifyRestore" `
            -ResultPath $verifyRestoreResultPath
        $verifyRestore = $verifyRestorePhase.result

        $postflightPhase = Invoke-PersistencePhase `
            -Phase "Postflight" `
            -ResultPath $postflightResultPath
        $postflight = $postflightPhase.result

        Assert-PersistenceStateEqual `
            -Expected $mutate.persistence.after `
            -Actual $verifyRestore.persistence.before `
            -Name "mutate-after-to-verify-before"
        Assert-PersistenceStateEqual `
            -Expected $mutate.persistence.before `
            -Actual $verifyRestore.persistence.after `
            -Name "mutate-baseline-to-restored-after"
        Assert-PersistenceStateEqual `
            -Expected $verifyRestore.persistence.after `
            -Actual $postflight.persistence.before `
            -Name "restored-after-to-postflight-before"
        Assert-PersistenceStateEqual `
            -Expected $postflight.persistence.before `
            -Actual $postflight.persistence.after `
            -Name "postflight-before-to-after"

        $previewProcessesAfter = @(
            Get-ExactPreviewProcesses -ExecutablePath $persistenceExecutablePath)
        if ($previewProcessesAfter.Count -ne 0) {
            throw "Persistence restart matrix left an audited preview process running."
        }

        $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
        if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
            throw "Persistence restart matrix did not produce its runtime log."
        }
        $runtimeFailureLogLines = @(
            Get-Content -LiteralPath $runtimeLogPath |
                Where-Object {
                    $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                    $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
                })
        if ($runtimeFailureLogLines.Count -gt 0) {
            throw "Persistence restart runtime log contains a failure: $($runtimeFailureLogLines -join ' | ')"
        }

        $productionDataFingerprintAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
        if (-not [string]::Equals(
                [string]$productionDataFingerprintAfter.fingerprint,
                [string]$productionDataFingerprintBefore.fingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]$productionDataFingerprintAfter.fileCount -ne [int]$productionDataFingerprintBefore.fileCount -or
            [long]$productionDataFingerprintAfter.bytes -ne [long]$productionDataFingerprintBefore.bytes) {
            throw "Production data changed during the persistence restart matrix."
        }

        if (Test-Path -LiteralPath $persistenceArchiveRoot -PathType Container) {
            if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $persistenceArchiveRoot) -or
                (Test-PathEqual -Left $evidenceRoot -Right $persistenceArchiveRoot)) {
                throw "Persistence archive root escaped the managed UI evidence directory."
            }
            Remove-Item -LiteralPath $persistenceArchiveRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $persistenceArchiveRoot -Force | Out-Null
        $archivedMutateResultPath = Join-Path $persistenceArchiveRoot "mutate-result.json"
        $archivedVerifyRestoreResultPath = Join-Path $persistenceArchiveRoot "verify-restore-result.json"
        $archivedPostflightResultPath = Join-Path $persistenceArchiveRoot "postflight-result.json"
        $archivedPersistenceSessionPath = Join-Path $persistenceArchiveRoot "session.json"
        $archivedRuntimeLogPath = Join-Path $persistenceArchiveRoot "DeskBox.log"
        $archivedFinalSettingsPath = Join-Path $persistenceArchiveRoot "final-settings.json"
        Copy-Item -LiteralPath $mutateResultPath -Destination $archivedMutateResultPath
        Copy-Item -LiteralPath $verifyRestoreResultPath -Destination $archivedVerifyRestoreResultPath
        Copy-Item -LiteralPath $postflightResultPath -Destination $archivedPostflightResultPath
        Copy-Item -LiteralPath $runtimeLogPath -Destination $archivedRuntimeLogPath
        Copy-Item -LiteralPath $settingsPath -Destination $archivedFinalSettingsPath

        $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path -LiteralPath $ownedMarkerPath -PathType Leaf) -or
            -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $resolvedDataRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedDataRoot)) {
            throw "Refusing to clean an unowned persistence preview root."
        }
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        $previewRootCleaned = $true

        $session = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("O")
            scenario = $scenario
            executablePath = $persistenceExecutablePath
            executableSha256 = $postflightPhase.session.executableSha256
            previewDataRoot = $DataRoot
            sessionPath = $archivedPersistenceSessionPath
            mutateResultPath = $archivedMutateResultPath
            verifyRestoreResultPath = $archivedVerifyRestoreResultPath
            postflightResultPath = $archivedPostflightResultPath
            finalSettingsPath = $archivedFinalSettingsPath
            runtimeLogPath = $archivedRuntimeLogPath
            mutate = $mutate.persistence
            verifyRestore = $verifyRestore.persistence
            postflight = $postflight.persistence
            naturalExit = [ordered]@{
                mutate = [bool]$mutatePhase.naturalExit
                verifyRestore = [bool]$verifyRestorePhase.naturalExit
                postflight = [bool]$postflightPhase.naturalExit
            }
            processIds = @(
                [int]$mutate.processId,
                [int]$verifyRestore.processId,
                [int]$postflight.processId)
            productionDataRoot = $productionDataRoot
            productionDataFingerprintBefore = $productionDataFingerprintBefore.fingerprint
            productionDataFingerprintAfter = $productionDataFingerprintAfter.fingerprint
            productionDataFileCountAfter = $productionDataFingerprintAfter.fileCount
            productionDataBytesAfter = $productionDataFingerprintAfter.bytes
            previewProcessesAfter = $previewProcessesAfter.Count
            previewRootCleaned = $previewRootCleaned
            runtimeFailureLogLines = $runtimeFailureLogLines
        }
        $sessionTemporaryPath = $sessionPath + ".tmp"
        $archivedSessionTemporaryPath = $archivedPersistenceSessionPath + ".tmp"
        $sessionJson = $session | ConvertTo-Json -Depth 20
        $sessionJson | Set-Content -LiteralPath $archivedSessionTemporaryPath -Encoding UTF8
        Move-Item `
            -LiteralPath $archivedSessionTemporaryPath `
            -Destination $archivedPersistenceSessionPath `
            -Force
        $sessionJson | Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
        Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

        return [PSCustomObject]@{
            Scenario = $scenario
            Success = $true
            Exe = $persistenceExecutablePath
            DataRoot = $DataRoot
            SessionPath = $archivedPersistenceSessionPath
            LatestSessionPath = $sessionPath
            MutateResultPath = $archivedMutateResultPath
            VerifyRestoreResultPath = $archivedVerifyRestoreResultPath
            PostflightResultPath = $archivedPostflightResultPath
            ProcessCount = 3
            NaturalExitCount = 3
            RuntimeFailureLogCount = $runtimeFailureLogLines.Count
            ProductionDataFingerprint = $productionDataFingerprintAfter.fingerprint
            PreviewRootCleaned = $previewRootCleaned
            Running = $false
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($persistenceExecutablePath)) {
            Stop-ExactPreviewProcess -ExecutablePath $persistenceExecutablePath
        }
    }
}

$scenarioDirectory = if ($scenario -ceq "BasicReadOnly") {
    "basic-read-only"
}
else {
    "deep-settings-read-only"
}
$resultPath = Join-Path $DataRoot "aot-managed-ui-smoke\$scenarioDirectory\result.json"
$resultTemporaryPath = $resultPath + ".tmp"
$sessionPath = Join-Path $evidenceRoot "session.json"
$productionDataFingerprintBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewSession = $null
$previewStopped = $false
$smokeResult = $null
$productionDataFingerprintAfter = $null
$runtimeFailureLogLines = @()

try {
    $previousManagedUiSmoke = [Environment]::GetEnvironmentVariable(
        $managedUiSmokeEnvironmentVariable,
        "Process")
    $previousManagedUiPersistencePhase = [Environment]::GetEnvironmentVariable(
        $managedUiPersistencePhaseEnvironmentVariable,
        "Process")
    $previousMusicSessionMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicSessionMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicMutationSmoke = [Environment]::GetEnvironmentVariable(
        $musicMutationSmokeEnvironmentVariable,
        "Process")
    $previousMusicReadSmoke = [Environment]::GetEnvironmentVariable(
        $musicReadSmokeEnvironmentVariable,
        "Process")
    $previousMutationSmoke = [Environment]::GetEnvironmentVariable(
        $mutationSmokeEnvironmentVariable,
        "Process")
    $previousShellSmoke = [Environment]::GetEnvironmentVariable(
        $shellSmokeEnvironmentVariable,
        "Process")
    $previousShortcutSmoke = [Environment]::GetEnvironmentVariable(
        $shortcutSmokeEnvironmentVariable,
        "Process")
    try {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $scenario,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $null,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $null,
            "Process")

        $previewOutput = @(
            & $launcher `
                -SummaryPath $SummaryPath `
                -DataRoot $DataRoot `
                -StartupWaitSeconds 5)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $managedUiSmokeEnvironmentVariable,
            $previousManagedUiSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $managedUiPersistencePhaseEnvironmentVariable,
            $previousManagedUiPersistencePhase,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicSessionMutationSmokeEnvironmentVariable,
            $previousMusicSessionMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicMutationSmokeEnvironmentVariable,
            $previousMusicMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $musicReadSmokeEnvironmentVariable,
            $previousMusicReadSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $mutationSmokeEnvironmentVariable,
            $previousMutationSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shellSmokeEnvironmentVariable,
            $previousShellSmoke,
            "Process")
        [Environment]::SetEnvironmentVariable(
            $shortcutSmokeEnvironmentVariable,
            $previousShortcutSmoke,
            "Process")
    }

    if (-not (Test-Path -LiteralPath $previewSessionPath -PathType Leaf)) {
        throw "Native AOT preview session.json was not created at '$previewSessionPath'."
    }
    $previewSession = Get-Content -LiteralPath $previewSessionPath -Raw | ConvertFrom-Json
    if (-not (Test-PathEqual -Left ([string]$previewSession.previewDataRoot) -Right $DataRoot)) {
        throw "Preview session data root does not match the managed UI smoke root."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                $smokeResult = $candidate
                if ($candidate.state -eq "Completed" -or $candidate.state -eq "Failed") {
                    break
                }
            }
            catch {
                # Retry a bounded transient read during the atomic result replacement.
            }
        }

        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $smokeResult -or
        ($smokeResult.state -ne "Completed" -and $smokeResult.state -ne "Failed")) {
        throw "AOT managed UI smoke timed out after $TimeoutSeconds seconds. Result='$resultPath'."
    }
    if ($smokeResult.state -ne "Completed" -or -not [bool]$smokeResult.success) {
        throw "AOT managed UI smoke failed: $($smokeResult.error)"
    }
    if ([int]$smokeResult.schemaVersion -ne 1 -or
        [string]$smokeResult.scenario -cne $scenario -or
        [bool]$smokeResult.isDynamicCodeSupported) {
        throw "Managed UI result does not prove the requested Native AOT scenario."
    }
    if ([int]$smokeResult.processId -ne [int]$previewSession.primaryProcessId -or
        -not (Test-PathEqual -Left ([string]$smokeResult.executablePath) -Right ([string]$previewSession.executablePath)) -or
        -not (Test-PathEqual -Left ([string]$smokeResult.previewDataRoot) -Right $DataRoot) -or
        -not (Test-PathEqual -Left ([string]$smokeResult.resultPath) -Right $resultPath)) {
        throw "Managed UI structured evidence does not match the current audited preview process/root."
    }

    $exactProcesses = @(Get-ExactPreviewProcesses -ExecutablePath ([string]$previewSession.executablePath))
    if ($exactProcesses.Count -ne 1 -or
        [int]$exactProcesses[0].ProcessId -ne [int]$smokeResult.processId) {
        throw "Managed UI smoke requires exactly one live audited preview process."
    }
    if (-not [bool]$smokeResult.trayIconCreated -or
        [long]$smokeResult.trayIconWindowHandle -eq 0 -or
        [long]$smokeResult.trayOwnerWindowHandle -eq 0) {
        throw "Managed UI smoke did not prove the real tray icon and owner HWND."
    }

    Assert-StringSequence `
        -Actual @($smokeResult.seededWidgetIds) `
        -Expected @("aot-5b4a-file", "aot-5b4a-search") `
        -Name "seededWidgetIds"
    Assert-StringSequence `
        -Actual @($smokeResult.visibleWidgetKinds) `
        -Expected @("File", "Search") `
        -Name "visibleWidgetKinds"
    if ([int]$smokeResult.loadedSurfaceCount -ne 2 -or
        [int]$smokeResult.visibleSurfaceCount -ne 2) {
        throw "Managed UI smoke did not restore exactly two visible owned widget surfaces."
    }

    $expectedLocales = @(
        "zh-CN", "zh-TW", "en-US", "ja-JP", "de-DE", "pt-BR",
        "hi-IN", "es-ES", "fr-FR", "ar-SA", "bn-BD", "ru-RU")
    $locales = @($smokeResult.locales)
    if ($locales.Count -ne $expectedLocales.Count) {
        throw "Managed UI smoke locale count mismatch."
    }
    foreach ($locale in $expectedLocales) {
        $matches = @($locales | Where-Object { [string]$_.locale -ceq $locale })
        if ($matches.Count -ne 1 -or
            [int]$matches[0].resourceCount -le 0 -or
            -not [bool]$matches[0].hasSettingsTitle -or
            -not [bool]$matches[0].hasOpenSettingsAction) {
            throw "Locale '$locale' did not load both required UI resources."
        }
    }

    $settingsSections = @()
    $filterTransitions = @()
    $sortTransitions = @()
    $deepSettings = $null
    $requiredSteps = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded")

    if ($scenario -ceq "BasicReadOnly") {
        $expectedSettingsSections = @(
            "General", "Appearance", "FeatureWidgets", "Interaction", "Maintenance", "About")
        $settingsSections = @($smokeResult.settingsSections)
        if ($settingsSections.Count -ne $expectedSettingsSections.Count) {
            throw "Managed UI settings section count mismatch."
        }
        foreach ($section in $expectedSettingsSections) {
            $matches = @($settingsSections | Where-Object { [string]$_.section -ceq $section })
            if ($matches.Count -ne 1 -or
                [long]$matches[0].windowHandle -eq 0 -or
                -not [bool]$matches[0].isAppWindowVisible -or
                -not [bool]$matches[0].hasXamlRoot -or
                [double]$matches[0].actualWidth -le 0 -or
                [double]$matches[0].actualHeight -le 0 -or
                [string]::IsNullOrWhiteSpace([string]$matches[0].title) -or
                [string]$matches[0].currentSection -cne $section -or
                [string]$matches[0].selectedSection -cne $section -or
                $section -notin @($matches[0].visibleSections)) {
                throw "Settings section '$section' did not prove a visible loaded routed window."
            }
        }

        $filterTransitions = @(
            "All:All",
            "FilesAndFolders:FilesAndFolders",
            "Apps:Apps",
            "Images:Images",
            "Documents:Documents",
            "DeskBox:DeskBox")
        $sortTransitions = @(
            "Name:True", "Name:False",
            "Size:False", "Size:True",
            "Date:False", "Date:True",
            "Type:True", "Type:False")
        Assert-StringSequence `
            -Actual @($smokeResult.search.filterTransitions) `
            -Expected $filterTransitions `
            -Name "filterTransitions"
        Assert-StringSequence `
            -Actual @($smokeResult.search.sortTransitions) `
            -Expected $sortTransitions `
            -Name "sortTransitions"
        if ([string]::IsNullOrWhiteSpace([string]$smokeResult.search.query) -or
            [long]$smokeResult.search.windowHandle -eq 0 -or
            -not [bool]$smokeResult.search.hasXamlRoot -or
            -not [bool]$smokeResult.search.hasResults -or
            -not [bool]$smokeResult.search.hasCurrentResults -or
            [int]$smokeResult.search.currentResultsCount -le 0 -or
            [string]$smokeResult.search.selectedTabId -cne "all" -or
            -not [bool]$smokeResult.search.resultFilterBarVisible -or
            -not [bool]$smokeResult.search.sortHeaderRowVisible -or
            -not [bool]$smokeResult.search.hasOpenSettingsAction -or
            [string]$smokeResult.search.finalResultFilter -cne "All" -or
            [string]$smokeResult.search.finalSortColumn -cne "Relevance" -or
            -not [bool]$smokeResult.search.finalSortAscending) {
            throw "Managed UI search evidence is incomplete or did not return to its read-only baseline."
        }

        $requiredSteps += @(
            "SettingsSectionsCompleted",
            "LocalizedSearchQuery",
            "SearchControlRoutes",
            "SearchCompleted")
    }
    else {
        $deepSettings = $smokeResult.deepSettings
        $deepRouteExpectations = [ordered]@{
            AppearanceDetail = @{ parent = $null; nav = "AppearanceDetail" }
            CapsuleMode = @{ parent = $null; nav = "CapsuleMode" }
            WidgetGroups = @{ parent = "Appearance"; nav = "Appearance" }
            FileDisplaySettings = @{ parent = "AppearanceDetail"; nav = "AppearanceDetail" }
            ManagedStorage = @{ parent = "AppearanceDetail"; nav = "AppearanceDetail" }
            FileStackSettings = @{ parent = "AppearanceDetail"; nav = "AppearanceDetail" }
            DesktopOrganizationSettings = @{ parent = "AppearanceDetail"; nav = "AppearanceDetail" }
            QuickCaptureSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            TodoSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            MusicSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            WeatherSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            GlanceSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            SearchSettings = @{ parent = "FeatureWidgets"; nav = "FeatureWidgets" }
            AppearanceMaterialSettings = @{ parent = "Appearance"; nav = "Appearance" }
            AppearanceDensitySettings = @{ parent = "Appearance"; nav = "Appearance" }
            AppearanceWindowSettings = @{ parent = "Appearance"; nav = "Appearance" }
            AppearanceAnimationSettings = @{ parent = "Appearance"; nav = "Appearance" }
            CapsuleBehaviorSettings = @{ parent = "CapsuleMode"; nav = "CapsuleMode" }
            CapsuleArrangementSettings = @{ parent = "CapsuleMode"; nav = "CapsuleMode" }
            CapsuleAnimationSettings = @{ parent = "CapsuleMode"; nav = "CapsuleMode" }
            CapsuleOverridesSettings = @{ parent = "CapsuleMode"; nav = "CapsuleMode" }
            BackupRestoreSettings = @{ parent = "Maintenance"; nav = "Maintenance" }
            DataHealthSettings = @{ parent = "Maintenance"; nav = "Maintenance" }
            CompatibilityDiagnosticsSettings = @{ parent = "Maintenance"; nav = "Maintenance" }
            PerformanceSettings = @{ parent = "General"; nav = "General" }
        }
        $pageTransitions = @($deepSettings.pageTransitions)
        $searchSuggestions = @($deepSettings.searchSuggestions)
        if ([string]::IsNullOrWhiteSpace([string]$deepSettings.searchQuery) -or
            [string]$deepSettings.searchActivatedSection -cne "BackupRestoreSettings" -or
            -not [bool]$deepSettings.breadcrumbParentReturned -or
            [int]$deepSettings.fileStackRuleCount -ne 1 -or
            [int]$deepSettings.backupSnapshotCount -le 0 -or
            $searchSuggestions.Count -le 0 -or
            @($searchSuggestions | Where-Object {
                [string]$_.sectionTag -ceq "BackupRestoreSettings" -and [bool]$_.isPage
            }).Count -ne 1 -or
            $pageTransitions.Count -ne $deepRouteExpectations.Count) {
            throw "Deep settings search or page evidence is incomplete."
        }

        $routeIndex = 0
        foreach ($entry in $deepRouteExpectations.GetEnumerator()) {
            $page = $pageTransitions[$routeIndex]
            $expectedParent = $entry.Value.parent
            $breadcrumbs = @($page.breadcrumbItems)
            if ([string]$page.section -cne $entry.Key -or
                [string]$page.currentSection -cne $entry.Key -or
                [string]$page.expectedNavTag -cne [string]$entry.Value.nav -or
                [string]$page.selectedNavTag -cne [string]$entry.Value.nav -or
                -not [bool]$page.hasXamlRoot -or
                [double]$page.actualWidth -le 0 -or
                [double]$page.actualHeight -le 0 -or
                $entry.Key -notin @($page.visibleSections)) {
                throw "Deep settings route '$($entry.Key)' did not prove its loaded navigation state."
            }

            if ($null -eq $expectedParent) {
                if ($null -ne $page.expectedParentTag -or
                    $breadcrumbs.Count -ne 0 -or
                    [bool]$page.breadcrumbHostVisible -or
                    [bool]$page.breadcrumbBarVisible -or
                    [bool]$page.backButtonVisible) {
                    throw "Top-level deep settings route '$($entry.Key)' exposed an unexpected breadcrumb."
                }
            }
            elseif ([string]$page.expectedParentTag -cne [string]$expectedParent -or
                $breadcrumbs.Count -ne 2 -or
                [string]$breadcrumbs[0].sectionTag -cne [string]$expectedParent -or
                [string]$breadcrumbs[1].sectionTag -cne $entry.Key -or
                -not [bool]$page.breadcrumbHostVisible -or
                -not [bool]$page.breadcrumbBarVisible -or
                -not [bool]$page.backButtonVisible) {
                throw "Nested deep settings route '$($entry.Key)' exposed an invalid breadcrumb."
            }

            $routeIndex++
        }

        $requiredSteps += "DeepSettingsCompleted"
    }

    $missingSteps = @($requiredSteps | Where-Object { $_ -notin @($smokeResult.steps) })
    if ($missingSteps.Count -gt 0) {
        throw "Managed UI result is missing required steps: $($missingSteps -join ', ')."
    }

    Stop-ExactPreviewProcess -ExecutablePath ([string]$previewSession.executablePath)
    $previewStopped = $true
    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    if (-not (Test-Path -LiteralPath $runtimeLogPath -PathType Leaf)) {
        throw "Managed UI smoke did not produce its runtime log."
    }
    $runtimeFailureLogLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf("Unhandled exception:", [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf("[DataBackup] Snapshot inventory failed:", [StringComparison]::Ordinal) -ge 0
            })
    if ($runtimeFailureLogLines.Count -gt 0) {
        throw "Managed UI smoke runtime log contains an unhandled or projection failure: $($runtimeFailureLogLines -join ' | ')"
    }
    $productionDataFingerprintAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if (-not [string]::Equals(
            [string]$productionDataFingerprintAfter.fingerprint,
            [string]$productionDataFingerprintBefore.fingerprint,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [int]$productionDataFingerprintAfter.fileCount -ne [int]$productionDataFingerprintBefore.fileCount -or
        [long]$productionDataFingerprintAfter.bytes -ne [long]$productionDataFingerprintBefore.bytes) {
        throw "Production data changed during the AOT managed UI smoke."
    }

    $session = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString("O")
        scenario = $scenario
        resultPath = $resultPath
        previewSessionPath = $previewSessionPath
        executablePath = $smokeResult.executablePath
        executableSha256 = $previewSession.executableSha256
        processId = [int]$smokeResult.processId
        previewDataRoot = $DataRoot
        settingsPath = $settingsPath
        ownedMarkerPath = $ownedMarkerPath
        trayIconWindowHandle = [long]$smokeResult.trayIconWindowHandle
        trayOwnerWindowHandle = [long]$smokeResult.trayOwnerWindowHandle
        seededWidgetIds = @($smokeResult.seededWidgetIds)
        visibleWidgetKinds = @($smokeResult.visibleWidgetKinds)
        locales = @($smokeResult.locales)
        settingsSections = @($smokeResult.settingsSections)
        filterTransitions = $filterTransitions
        sortTransitions = $sortTransitions
        deepSettings = $deepSettings
        steps = @($smokeResult.steps)
        productionDataRoot = $productionDataRoot
        productionDataFingerprintBefore = $productionDataFingerprintBefore.fingerprint
        productionDataFingerprintAfter = $productionDataFingerprintAfter.fingerprint
        productionDataFileCountAfter = $productionDataFingerprintAfter.fileCount
        productionDataBytesAfter = $productionDataFingerprintAfter.bytes
        previewStopped = $previewStopped
        runtimeFailureLogLines = $runtimeFailureLogLines
    }
    $sessionTemporaryPath = $sessionPath + ".tmp"
    $session | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $sessionTemporaryPath -Encoding UTF8
    Move-Item -LiteralPath $sessionTemporaryPath -Destination $sessionPath -Force

    [PSCustomObject]@{
        Scenario = $scenario
        Success = $true
        ProcessId = [int]$smokeResult.processId
        Exe = $smokeResult.executablePath
        DataRoot = $DataRoot
        ResultPath = $resultPath
        SessionPath = $sessionPath
        LoadedSurfaceCount = [int]$smokeResult.loadedSurfaceCount
        SettingsSectionCount = $settingsSections.Count
        LocaleCount = $locales.Count
        FilterTransitionCount = $filterTransitions.Count
        SortTransitionCount = $sortTransitions.Count
        DeepSettingsPageCount = if ($null -eq $deepSettings) { 0 } else { @($deepSettings.pageTransitions).Count }
        DeepSettingsSuggestionCount = if ($null -eq $deepSettings) { 0 } else { @($deepSettings.searchSuggestions).Count }
        RuntimeFailureLogCount = $runtimeFailureLogLines.Count
        ProductionDataFingerprint = $productionDataFingerprintAfter.fingerprint
        Running = $false
    }
}
finally {
    if (-not $previewStopped -and
        $null -ne $previewSession -and
        -not [string]::IsNullOrWhiteSpace([string]$previewSession.executablePath)) {
        Stop-ExactPreviewProcess -ExecutablePath ([string]$previewSession.executablePath)
    }
}
