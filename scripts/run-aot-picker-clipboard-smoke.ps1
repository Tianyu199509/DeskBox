[CmdletBinding()]
param(
    [string]$SummaryPath,

    [string]$DataRoot,

    [ValidateRange(60, 300)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scenario = "PickerClipboardStorageItemsPersistenceRestart"
$smokeEnvironmentVariable = "DESKBOX_AOT_MANAGED_UI_SMOKE"
$phaseEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE"
$runIdEnvironmentVariable =
    "DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_RUN_ID"
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
    ".artifacts\aot-managed-ui-smoke\win-x64"
$productionDataRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "DeskBox"))
$ownedMarkerName = ".deskbox-aot-managed-ui-owned.json"
$ownedMarkerKind = "DeskBox.Aot.PickerClipboardSmoke.v1"

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

function Wait-InteractionState {
    param(
        [string]$ResultPath,
        [string]$ExpectedState,
        [int]$Seconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $ResultPath -PathType Leaf) {
            $candidate = Read-JsonRetry -Path $ResultPath
            if ($null -ne $candidate) {
                if ([string]$candidate.state -ceq "Failed") {
                    throw "Picker/StorageItems AOT process failed: $($candidate.error)"
                }
                if ([string]$candidate.pickerClipboard.interactionState -ceq
                    $ExpectedState) {
                    return $candidate
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for interaction state '$ExpectedState'."
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
    throw "Picker/StorageItems smoke timed out without terminal evidence."
}

function Wait-PickerAutomationWindow {
    param([int]$ProcessId, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        [IntPtr]$windowHandle =
            [DeskBoxPickerWindowNative]::FindVisibleDialog($ProcessId)
        if ($windowHandle -ne [IntPtr]::Zero) {
            try {
                $window =
                    [System.Windows.Automation.AutomationElement]::FromHandle(
                        $windowHandle)
                if ($null -ne $window -and
                    [int]$window.Current.ProcessId -eq $ProcessId -and
                    [string]$window.Current.ClassName -ceq "#32770" -and
                    [bool]$window.Current.IsEnabled -and
                    -not [bool]$window.Current.IsOffscreen) {
                    return $window
                }
            }
            catch {
                # The system dialog can be replaced while its shell view loads.
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "The real system file picker HWND was not exposed through Win32 and UI Automation."
}

function Get-AutomationElements {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition))
}

function Get-AutomationElementsById {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition))
}

function Invoke-AutomationElement {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return "InvokePattern"
    }

    [IntPtr]$windowHandle =
        [IntPtr][long]$Element.Current.NativeWindowHandle
    if ($windowHandle -ne [IntPtr]::Zero -and
        [DeskBoxPickerWindowNative]::ClickButton($windowHandle)) {
        return "BM_CLICK"
    }

    return ""
}

function Set-AutomationElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value)
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern) -and
        -not ([System.Windows.Automation.ValuePattern]$pattern).Current.IsReadOnly) {
        ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
        return "ValuePattern"
    }

    [IntPtr]$windowHandle =
        [IntPtr][long]$Element.Current.NativeWindowHandle
    if ($windowHandle -ne [IntPtr]::Zero -and
        [DeskBoxPickerWindowNative]::SetWindowText($windowHandle, $Value)) {
        return "WM_SETTEXT"
    }

    return ""
}

function Invoke-PickerDialogAutomation {
    param(
        [int]$ProcessId,
        [ValidateSet("Cancel", "Select")]
        [string]$Action,
        [string]$SelectedPath,
        [int]$Seconds)

    $window = Wait-PickerAutomationWindow `
        -ProcessId $ProcessId `
        -Seconds $Seconds
    Start-Sleep -Milliseconds 350
    $snapshot = [ordered]@{
        action = $Action
        processId = [int]$window.Current.ProcessId
        windowHandle = [long]$window.Current.NativeWindowHandle
        className = [string]$window.Current.ClassName
        title = [string]$window.Current.Name
        selectedPath = if ($Action -ceq "Select") { $SelectedPath } else { "" }
        editAutomationId = ""
        editMethod = ""
        commitAutomationId = ""
        commitName = ""
        commitMethod = ""
        invoked = $false
    }

    if ($Action -ceq "Cancel") {
        $buttons = Get-AutomationElementsById `
            -Root $window `
            -AutomationId "2"
        $cancel = $buttons | Select-Object -First 1
        if ($null -ne $cancel) {
            $snapshot.commitAutomationId =
                [string]$cancel.Current.AutomationId
            $snapshot.commitName = [string]$cancel.Current.Name
            $snapshot.commitMethod =
                Invoke-AutomationElement -Element $cancel
            $snapshot.invoked =
                -not [string]::IsNullOrWhiteSpace(
                    [string]$snapshot.commitMethod)
        }
        if (-not [bool]$snapshot.invoked) {
            $windowPattern = $null
            if (-not $window.TryGetCurrentPattern(
                    [System.Windows.Automation.WindowPattern]::Pattern,
                    [ref]$windowPattern)) {
                throw "The picker exposed neither a Cancel button nor WindowPattern.Close."
            }
            ([System.Windows.Automation.WindowPattern]$windowPattern).Close()
            $snapshot.commitAutomationId = "WindowPattern.Close"
            $snapshot.commitMethod = "WindowPattern.Close"
            $snapshot.invoked = $true
        }
    }
    else {
        $edits = Get-AutomationElementsById `
            -Root $window `
            -AutomationId "1148"
        $editCandidates = @($edits | Sort-Object {
            if ([string]$_.Current.ClassName -ceq "Edit") {
                0
            }
            else {
                1
            }
        })
        $valueSet = $false
        foreach ($edit in $editCandidates) {
            $snapshot.editMethod =
                Set-AutomationElementValue `
                    -Element $edit `
                    -Value $SelectedPath
            if (-not [string]::IsNullOrWhiteSpace(
                    [string]$snapshot.editMethod)) {
                $snapshot.editAutomationId =
                    [string]$edit.Current.AutomationId
                $valueSet = $true
                break
            }
        }
        if (-not $valueSet) {
            throw "The picker did not expose a writable file-name field."
        }

        $buttons = Get-AutomationElementsById `
            -Root $window `
            -AutomationId "1"
        $commit = $buttons | Select-Object -First 1
        if ($null -eq $commit) {
            $allButtons = Get-AutomationElements `
                -Root $window `
                -ControlType ([System.Windows.Automation.ControlType]::Button)
            $commit = $allButtons | Where-Object {
                [bool]$_.Current.IsEnabled -and
                [string]$_.Current.Name -match
                    '^(Open|打开|開啟|Select|选择|選取)'
            } | Select-Object -First 1
        }
        if ($null -eq $commit) {
            throw "The picker did not expose its commit button."
        }
        $snapshot.commitAutomationId =
            [string]$commit.Current.AutomationId
        $snapshot.commitName = [string]$commit.Current.Name
        $snapshot.commitMethod =
            Invoke-AutomationElement -Element $commit
        $snapshot.invoked =
            -not [string]::IsNullOrWhiteSpace(
                [string]$snapshot.commitMethod)
        if (-not [bool]$snapshot.invoked) {
            throw "The picker commit button did not expose InvokePattern."
        }
    }

    return [PSCustomObject]$snapshot
}

function Assert-PhaseResult {
    param(
        [object]$Result,
        [string]$Phase,
        [int]$ProcessId,
        [string]$ExecutablePath,
        [string]$ResultPath)

    if ([string]$Result.state -cne "Completed" -or
        -not [bool]$Result.success -or
        [int]$Result.schemaVersion -ne 1 -or
        [string]$Result.scenario -cne $scenario -or
        [bool]$Result.isDynamicCodeSupported -or
        [int]$Result.processId -ne $ProcessId -or
        -not (Test-PathEqual `
            -Left ([string]$Result.executablePath) `
            -Right $ExecutablePath) -or
        -not (Test-PathEqual `
            -Left ([string]$Result.previewDataRoot) `
            -Right $DataRoot) -or
        -not (Test-PathEqual `
            -Left ([string]$Result.resultPath) `
            -Right $ResultPath) -or
        [string]$Result.pickerClipboard.phase -cne $Phase -or
        -not [bool]$Result.pickerClipboard.normalShutdownRequested -or
        [string]$Result.pickerClipboard.runId -cne $runId -or
        [long]$Result.pickerClipboard.hostWindowHandle -eq 0 -or
        -not [bool]$Result.pickerClipboard.hostHasXamlRoot -or
        -not [bool]$Result.pickerClipboard.hostVisible -or
        @($Result.seededWidgetIds).Count -ne 2 -or
        [int]$Result.loadedSurfaceCount -ne 2 -or
        [int]$Result.visibleSurfaceCount -ne 2 -or
        @($Result.locales).Count -ne 12) {
        throw "Picker/StorageItems phase '$Phase' evidence is incomplete."
    }

    $required = @(
        "NativeAotRuntime",
        "TrayCreated",
        "SeededWidgetConfiguration",
        "SeededWidgetsRestored",
        "AllLocaleResourcesLoaded",
        "PickerClipboardHostReady",
        "PickerClipboardSourcesPreserved")
    if ($Phase -ceq "Mutate") {
        $required += @(
            "PickerClipboardOwnedBaselineVerified",
            "PickerCancelNoChangeVerified",
            "PickerSelectionImported",
            "ClipboardStorageItemsImported",
            "PickerClipboardMutationApplied")
    }
    elseif ($Phase -ceq "VerifyRestore") {
        $required += "PickerClipboardRestartMutationVerified"
    }
    else {
        $required += "PickerClipboardPostflightVerified"
    }
    $missing = @($required | Where-Object { $_ -notin @($Result.steps) })
    if ($missing.Count -gt 0) {
        throw "Phase '$Phase' is missing steps: $($missing -join ', ')."
    }
}

function Invoke-PickerClipboardPhase {
    param(
        [ValidateSet("Mutate", "VerifyRestore", "Postflight")]
        [string]$Phase,
        [string]$ExecutablePath)

    $phaseDirectory = switch ($Phase) {
        "Mutate" { "mutate" }
        "VerifyRestore" { "verify-restore" }
        default { "postflight" }
    }
    $resultPath = Join-Path `
        $DataRoot `
        "aot-managed-ui-smoke\picker-clipboard-storage-items-persistence-restart\$phaseDirectory\result.json"
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
        "DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID",
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
        throw "Phase '$Phase' did not use the audited executable and preview root."
    }

    $automation = @()
    if ($Phase -ceq "Mutate") {
        $null = Wait-InteractionState `
            -ResultPath $resultPath `
            -ExpectedState "CancelPending" `
            -Seconds $TimeoutSeconds
        $automation += Invoke-PickerDialogAutomation `
            -ProcessId ([int]$session.primaryProcessId) `
            -Action "Cancel" `
            -SelectedPath "" `
            -Seconds 30
        $null = Wait-InteractionState `
            -ResultPath $resultPath `
            -ExpectedState "SelectionPending" `
            -Seconds $TimeoutSeconds
        $automation += Invoke-PickerDialogAutomation `
            -ProcessId ([int]$session.primaryProcessId) `
            -Action "Select" `
            -SelectedPath $pickerSourceFile `
            -Seconds 30
    }

    $result = Wait-TerminalResult `
        -ResultPath $resultPath `
        -Seconds $TimeoutSeconds
    $naturalExit = Wait-NaturalPreviewExit `
        -ExecutablePath $phaseExecutablePath
    if (-not $naturalExit) {
        throw "Picker/StorageItems phase '$Phase' did not exit naturally."
    }
    if ([string]$result.state -ceq "Failed") {
        throw "Picker/StorageItems phase '$Phase' failed: $($result.error)"
    }
    Assert-PhaseResult `
        -Result $result `
        -Phase $Phase `
        -ProcessId ([int]$session.primaryProcessId) `
        -ExecutablePath $phaseExecutablePath `
        -ResultPath $resultPath

    if ($Phase -ceq "Mutate") {
        if ($automation.Count -ne 2 -or
            [string]$automation[0].action -cne "Cancel" -or
            [string]$automation[1].action -cne "Select" -or
            -not [bool]$automation[0].invoked -or
            -not [bool]$automation[1].invoked -or
            [long]$automation[0].windowHandle -ne
                [long]$result.pickerClipboard.cancelPicker.dialog.windowHandle -or
            [long]$automation[1].windowHandle -ne
                [long]$result.pickerClipboard.selectPicker.dialog.windowHandle) {
            throw "UI Automation and in-process picker dialog evidence do not match."
        }
    }

    $runtimeLogPath = Join-Path $DataRoot "DeskBox.log"
    $failureLines = @(
        Get-Content -LiteralPath $runtimeLogPath |
            Where-Object {
                $_.IndexOf(
                    "Unhandled exception:",
                    [StringComparison]::Ordinal) -ge 0 -or
                $_.IndexOf(
                    "[AotManagedUiSmoke] Failed:",
                    [StringComparison]::Ordinal) -ge 0
            })
    if ($failureLines.Count -gt 0) {
        throw "Runtime log contains failures: $($failureLines -join ' | ')"
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
        automation = $automation
    }
}

try {
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskBoxPickerWindowNative
{
    private const uint BmClick = 0x00F5;
    private const uint WmSetText = 0x000C;

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendPointerMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendStringMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        string lParam);

    public static IntPtr FindVisibleDialog(int processId)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out uint ownerProcessId);
            if (ownerProcessId != (uint)processId)
            {
                return true;
            }

            var className = new StringBuilder(64);
            GetClassName(windowHandle, className, className.Capacity);
            if (!string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
            {
                return true;
            }

            match = windowHandle;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    public static bool ClickButton(IntPtr windowHandle)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        SendPointerMessage(
            windowHandle,
            BmClick,
            IntPtr.Zero,
            IntPtr.Zero);
        return true;
    }

    public static bool SetWindowText(IntPtr windowHandle, string value)
    {
        return IsWindow(windowHandle) &&
            SendStringMessage(
                windowHandle,
                WmSetText,
                IntPtr.Zero,
                value) != IntPtr.Zero;
    }
}
'@
}
catch {
    throw "Windows UI Automation assemblies are required for the real picker smoke: $_"
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
if ([int]$auditSummary.auditProfileVersion -ne 50 -or
    [int]$auditSummary.schemaVersion -ne 47 -or
    -not [bool]$auditSummary.sourceStableDuringAudit -or
    [string]$auditSummary.configuration -cne "Release" -or
    [string]$auditSummary.platform -cne "x64" -or
    [string]$auditSummary.runtimeIdentifier -cne "win-x64" -or
    @($auditSummary.warningCodes | Where-Object { $_ -ceq "WMC1506" }).Count -ne 0 -or
    [int]$auditSummary.warningCodeCounts.WMC1510 -ne 1213 -or
    @($auditSummary.alwaysThrowMessages).Count -ne 0 -or
    [int]$auditSummary.rustNative.abiVersion -ne 2 -or
    [int]$auditSummary.rustNative.capabilities -ne 1023) {
    throw "Picker/StorageItems smoke requires a successful profile 50 / schema 47 audit."
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $evidenceRoot "picker-clipboard-preview-$runId"
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$recoveryRoot = [System.IO.Path]::GetFullPath($DataRoot + "-Recovery")
$archiveRoot = Join-Path $evidenceRoot "picker-clipboard-runs\$runId"
$latestSessionPath = Join-Path $evidenceRoot "picker-clipboard-session.json"
if (-not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $DataRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $DataRoot) -or
    -not (Test-PathEqualOrInside -Root $evidenceRoot -Candidate $recoveryRoot) -or
    (Test-PathEqual -Left $evidenceRoot -Right $recoveryRoot) -or
    (Test-Path -LiteralPath $DataRoot) -or
    (Test-Path -LiteralPath $recoveryRoot) -or
    (Test-Path -LiteralPath $archiveRoot)) {
    throw "Refusing to replace an existing or unowned picker/StorageItems preview, recovery, or archive root."
}

$fixtureRoot = Join-Path $DataRoot "fixtures\picker-clipboard-storage-items"
$widgetRoot = Join-Path $fixtureRoot "widget-root"
$pickerSourceRoot = Join-Path $fixtureRoot "sources\picker"
$clipboardSourceRoot = Join-Path $fixtureRoot "sources\clipboard"
$pickerFileName = "picker-$runId.txt"
$clipboardFileName = "storage-file-$runId.txt"
$clipboardFolderName = "storage-folder-$runId"
$nestedFileName = "nested-$runId.txt"
$pickerSourceFile = Join-Path $pickerSourceRoot $pickerFileName
$clipboardSourceFile = Join-Path $clipboardSourceRoot $clipboardFileName
$clipboardSourceFolder = Join-Path $clipboardSourceRoot $clipboardFolderName
$clipboardNestedSourceFile = Join-Path $clipboardSourceFolder $nestedFileName
$pickerDestinationFile = Join-Path $widgetRoot $pickerFileName
$clipboardDestinationFile = Join-Path $widgetRoot $clipboardFileName
$clipboardDestinationFolder = Join-Path $widgetRoot $clipboardFolderName
$dataDirectory = Join-Path $DataRoot "data"
$settingsPath = Join-Path $dataDirectory "settings.json"

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $widgetRoot -Force | Out-Null
New-Item -ItemType Directory -Path $pickerSourceRoot -Force | Out-Null
New-Item -ItemType Directory -Path $clipboardSourceFolder -Force | Out-Null
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    dataRoot = $DataRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $DataRoot $ownedMarkerName) `
    -Encoding UTF8
@{
    kind = $ownedMarkerKind
    runId = $runId
    repositoryRoot = $repoRoot
    recoveryRoot = $recoveryRoot
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $recoveryRoot $ownedMarkerName) `
    -Encoding UTF8
Set-Content `
    -LiteralPath $pickerSourceFile `
    -Value "picker-source-$runId" `
    -NoNewline `
    -Encoding UTF8
Set-Content `
    -LiteralPath $clipboardSourceFile `
    -Value "storage-file-source-$runId" `
    -NoNewline `
    -Encoding UTF8
Set-Content `
    -LiteralPath $clipboardNestedSourceFile `
    -Value "storage-folder-nested-source-$runId" `
    -NoNewline `
    -Encoding UTF8

$settings = [ordered]@{
    schemaVersion = 5
    language = "zh-CN"
    managedDropAction = "Copy"
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
            id = "aot-5b4c1c1-file"
            name = "AOT Picker StorageItems Fixture"
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
$settings | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8

$productionBefore = Get-DirectoryStateFingerprint -Path $productionDataRoot
$previewExecutablePath = [System.IO.Path]::GetFullPath(
    (Join-Path ([string]$auditSummary.publishDirectory) "DeskBox.exe"))
$runSucceeded = $false
$previewRootCleaned = $false
$recoveryRootCleaned = $false

try {
    $mutate = Invoke-PickerClipboardPhase `
        -Phase "Mutate" `
        -ExecutablePath $previewExecutablePath
    foreach ($path in @(
            $pickerDestinationFile,
            $clipboardDestinationFile,
            (Join-Path $clipboardDestinationFolder $nestedFileName))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Mutate phase did not create '$path'."
        }
    }
    $verifyRestore = Invoke-PickerClipboardPhase `
        -Phase "VerifyRestore" `
        -ExecutablePath $previewExecutablePath

    foreach ($path in @(
            $pickerDestinationFile,
            $clipboardDestinationFile,
            $clipboardDestinationFolder)) {
        $resolved = [System.IO.Path]::GetFullPath($path)
        if (-not (Test-PathEqualOrInside `
                -Root $widgetRoot `
                -Candidate $resolved) -or
            (Test-PathEqual -Left $widgetRoot -Right $resolved)) {
            throw "Refusing to clean an import outside the exact widget root."
        }
    }
    Remove-Item -LiteralPath $pickerDestinationFile -Force
    Remove-Item -LiteralPath $clipboardDestinationFile -Force
    Remove-Item -LiteralPath $clipboardDestinationFolder -Recurse -Force
    if (@(Get-ChildItem -LiteralPath $widgetRoot -Force).Count -ne 0) {
        throw "The exact imported widget entries were not cleaned."
    }

    $postflight = Invoke-PickerClipboardPhase `
        -Phase "Postflight" `
        -ExecutablePath $previewExecutablePath
    $productionAfter = Get-DirectoryStateFingerprint -Path $productionDataRoot
    if ([string]$productionAfter.fingerprint -cne
            [string]$productionBefore.fingerprint -or
        [int]$productionAfter.fileCount -ne
            [int]$productionBefore.fileCount -or
        [long]$productionAfter.bytes -ne [long]$productionBefore.bytes) {
        throw "Production data changed during the picker/StorageItems smoke."
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    foreach ($phase in @($mutate, $verifyRestore, $postflight)) {
        $phaseArchive = Join-Path $archiveRoot (
            ([string]$phase.phase).ToLowerInvariant())
        New-Item -ItemType Directory -Path $phaseArchive -Force | Out-Null
        Copy-Item `
            -LiteralPath ([string]$phase.resultPath) `
            -Destination (Join-Path $phaseArchive "result.json")
        Copy-Item `
            -LiteralPath ([string]$phase.runtimeLogPath) `
            -Destination (Join-Path $phaseArchive "DeskBox.log")
        @($phase.automation) | ConvertTo-Json -Depth 12 |
            Set-Content `
                -LiteralPath (Join-Path $phaseArchive "picker-automation.json") `
                -Encoding UTF8
    }
    Copy-Item `
        -LiteralPath $settingsPath `
        -Destination (Join-Path $archiveRoot "settings.json")

    foreach ($cleanup in @(
            [PSCustomObject]@{
                root = $DataRoot
                property = "dataRoot"
                kind = "preview"
            },
            [PSCustomObject]@{
                root = $recoveryRoot
                property = "recoveryRoot"
                kind = "recovery"
            })) {
        $resolvedRoot = [System.IO.Path]::GetFullPath([string]$cleanup.root)
        $markerPath = Join-Path $resolvedRoot $ownedMarkerName
        $marker = Get-Content `
            -LiteralPath $markerPath `
            -Raw | ConvertFrom-Json
        $markerRoot = [string]$marker.([string]$cleanup.property)
        if ([string]$marker.kind -cne $ownedMarkerKind -or
            [string]$marker.runId -cne $runId -or
            -not (Test-PathEqual `
                -Left ([string]$marker.repositoryRoot) `
                -Right $repoRoot) -or
            -not (Test-PathEqual -Left $markerRoot -Right $resolvedRoot) -or
            -not (Test-PathEqualOrInside `
                -Root $evidenceRoot `
                -Candidate $resolvedRoot) -or
            (Test-PathEqual -Left $evidenceRoot -Right $resolvedRoot)) {
            throw "Refusing to clean an unowned picker/StorageItems $($cleanup.kind) root."
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        if (Test-Path -LiteralPath $resolvedRoot) {
            throw "The owned picker/StorageItems $($cleanup.kind) root was not cleaned."
        }
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
        success = $true
        executablePath = $previewExecutablePath
        executableSha256 = [string]$mutate.executableSha256
        previewDataRoot = $DataRoot
        recoveryRoot = $recoveryRoot
        archiveRoot = $archiveRoot
        phaseProcessIds = @(
            [int]$mutate.processId,
            [int]$verifyRestore.processId,
            [int]$postflight.processId)
        pickerAutomation = @($mutate.automation)
        sourceFiles = @(
            $pickerSourceFile,
            $clipboardSourceFile,
            $clipboardNestedSourceFile)
        productionDataFingerprintBefore =
            [string]$productionBefore.fingerprint
        productionDataFingerprintAfter =
            [string]$productionAfter.fingerprint
        ownedPreviewRootCleaned = $previewRootCleaned
        ownedRecoveryRootCleaned = $recoveryRootCleaned
    }
    $sessionJson = $session | ConvertTo-Json -Depth 20
    $archivedSessionPath = Join-Path $archiveRoot "session.json"
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
        ProcessIds = @($session.phaseProcessIds)
        Exe = $previewExecutablePath
        CancelDialog = [long]$mutate.automation[0].windowHandle
        SelectDialog = [long]$mutate.automation[1].windowHandle
        PickerSource = $pickerFileName
        StorageItems = @($clipboardFileName, $clipboardFolderName)
        GlobalClipboardUntouched = $true
        NaturalExit = $true
        ProductionDataFingerprint =
            [string]$productionAfter.fingerprint
        PreviewRootCleaned = $previewRootCleaned
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
            "Picker/StorageItems smoke failed. The exact owned preview/" +
            "recovery roots and run ID were preserved: root='$DataRoot' " +
            "recovery='$recoveryRoot' runId='$runId'.")
    }
}
