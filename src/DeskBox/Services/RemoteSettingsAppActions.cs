using System.Threading;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Settings-host app facade over the IPC pipe (batch 3c-2). Lifecycle and
/// window-opener members run in the main process; the cleanup schedulers are
/// no-ops in the host process (closing its window exits the process); the
/// display/screen-home events arrive as pushes.
/// </summary>
public sealed class RemoteSettingsAppActions : ISettingsAppActions
{
    private readonly SettingsIpcClient _client;
    private readonly uint _mainProcessId;

    public RemoteSettingsAppActions(SettingsIpcClient client, uint mainProcessId = 0)
    {
        _client = client;
        _mainProcessId = mainProcessId;
        _client.EventReceived += OnEventReceived;
    }

    /// <summary>
    /// Window-opener senders grant the MAIN process the foreground right
    /// first: the host window is the foreground process, and a background
    /// process's SetForegroundWindow is refused by the OS — the opened
    /// onboarding/organization/release-notes window used to appear BEHIND
    /// the large settings window with nothing but a flashing taskbar button
    /// (batch R9).
    /// </summary>
    private void GrantMainProcessForegroundRight()
    {
        if (_mainProcessId == 0)
        {
            return;
        }

        try
        {
            Platform.Win32Helper.AllowSetForegroundWindow(_mainProcessId);
        }
        catch
        {
            // Best-effort: a refused grant only costs the focus race, never
            // the window itself.
        }
    }

    public event Action? DisplayTopologyChanged;

    public event Action? ScreenHomeChanged;

    private void OnEventReceived(string method, string? payload)
    {
        switch (method)
        {
            case SettingsIpcMethods.AppDisplayTopologyChanged:
                DisplayTopologyChanged?.Invoke();
                break;
            case SettingsIpcMethods.AppScreenHomeChanged:
                ScreenHomeChanged?.Invoke();
                break;
        }
    }

    public Task ShutdownForRestartAsync() =>
        RequestVoidAsync(SettingsIpcMethods.AppShutdownForRestart);

    public Task ShutdownForUpdateAsync() =>
        RequestVoidAsync(SettingsIpcMethods.AppShutdownForUpdate);

    public AppRelaunchScheduleResult ScheduleRestartAfterCurrentProcessExit()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.AppScheduleRestart)
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppRelaunchScheduleResult)
            is { } schedule
                ? schedule
                : new AppRelaunchScheduleResult(false, "schedule request failed");
    }

    public bool TryRelaunchAsExplorerUser(string arguments)
    {
        (bool ok, _, _) = _client.RequestAsync(
                SettingsIpcMethods.AppRelaunchAsExplorerUser,
                arguments)
            .GetAwaiter().GetResult();
        return ok;
    }

    public void ShowOnboarding(bool resumeProgress = false)
    {
        // Fire-and-forget: the main process builds a heavy window; blocking
        // the host UI thread on that reads as a frozen settings page.
        GrantMainProcessForegroundRight();
        _ = RequestVoidAsync(SettingsIpcMethods.AppShowOnboarding, SettingsIpcPayload.EncodeBoolean(resumeProgress));
    }

    public void ShowDesktopOrganizationWindow(nint ownerWindowHandle = 0)
    {
        // Fire-and-forget on purpose: the main process builds a heavy window
        // and scans the desktop before this command would answer; blocking
        // the host UI thread on that reads as a frozen settings page.
        GrantMainProcessForegroundRight();
        _ = _client.RequestAsync(
            SettingsIpcMethods.AppShowDesktopOrgWindow,
            ((long)ownerWindowHandle).ToString());
    }

    public void OpenReleaseNotesWindow(
        AppUpdateManifest? manifest,
        string version,
        Func<CancellationToken, Task<AppUpdateManifest?>>? manifestLoader,
        nint ownerWindowHandle = 0)
    {
        // Fire-and-forget, same rationale as ShowOnboarding.
        GrantMainProcessForegroundRight();
        _ = RequestVoidAsync(
            SettingsIpcMethods.AppOpenReleaseNotes,
            SettingsIpcPayload.Encode(
                new SettingsIpcReleaseNotes(
                    manifest is null
                        ? null
                        : SettingsIpcPayload.Encode(manifest, SettingsIpcJsonContext.Default.AppUpdateManifest),
                    version,
                    AllowLazyFetch: manifestLoader is not null,
                    OwnerHwnd: ownerWindowHandle),
                SettingsIpcJsonContext.Default.SettingsIpcReleaseNotes));
    }

    public void UpdateTrayIcon() =>
        RequestVoidAsync(SettingsIpcMethods.AppUpdateTrayIcon).GetAwaiter().GetResult();

    public void SetSnapEnabled(bool value) =>
        RequestVoidAsync(SettingsIpcMethods.AppSetSnapEnabled, SettingsIpcPayload.EncodeBoolean(value))
            .GetAwaiter().GetResult();

    public void SetSnapSpacingDips(double value) =>
        RequestVoidAsync(SettingsIpcMethods.AppSetSnapSpacing, value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .GetAwaiter().GetResult();

    public void ScheduleLightMemoryCleanup()
    {
        // No-op in the host process: closing its window exits the process,
        // which releases everything the scheduler would reclaim.
    }

    public void ScheduleSettingsHiddenCleanup()
    {
        // No-op in the host process (see ScheduleLightMemoryCleanup).
    }

    public void BringSettingsWindowToRaisedBand(nint windowHandle, string reason) =>
        RequestVoidAsync(
                SettingsIpcMethods.AppRaiseBand,
                SettingsIpcPayload.Encode(
                    new SettingsIpcStringPair(((long)windowHandle).ToString(), reason),
                    SettingsIpcJsonContext.Default.SettingsIpcStringPair))
            .GetAwaiter().GetResult();

    public void ReleaseRaisedBandGuest(nint windowHandle, string reason) =>
        RequestVoidAsync(
                SettingsIpcMethods.AppReleaseBand,
                SettingsIpcPayload.Encode(
                    new SettingsIpcStringPair(((long)windowHandle).ToString(), reason),
                    SettingsIpcJsonContext.Default.SettingsIpcStringPair))
            .GetAwaiter().GetResult();

    public bool HasManagedStorageDesktopShortcut()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.AppHasShortcut)
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    public async Task<bool> CreateManagedStorageDesktopShortcutAsync()
    {
        (bool ok, _, _) = await _client.RequestAsync(SettingsIpcMethods.AppCreateShortcut);
        return ok;
    }

    public async Task<bool> RemoveManagedStorageDesktopShortcutAsync()
    {
        (bool ok, _, _) = await _client.RequestAsync(SettingsIpcMethods.AppRemoveShortcut);
        return ok;
    }

    public Task SyncManagedStorageDesktopShortcutAsync(string oldRootPath) =>
        RequestVoidAsync(SettingsIpcMethods.AppSyncShortcut, oldRootPath);

    public async Task<string> ExportDiagnosticBundleAsync(string destinationDirectory)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.AppExportDiagnostics,
            destinationDirectory);
        if (!ok || string.IsNullOrEmpty(payload))
        {
            throw new InvalidOperationException(error ?? "diagnostic export failed");
        }

        return payload;
    }

    public async Task<bool> UploadDiagnosticsForFeedbackAsync(long feedbackId, string tempDirectory)
    {
        // tempDirectory is ignored: the main side exports into its own
        // scratch directory (the host's temp path is unreachable there).
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.AppUploadDiagnostics,
            feedbackId.ToString());
        return ok;
    }

    public AppRuntimeHealthSnapshot? GetRuntimeHealthSnapshot()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.AppRuntimeHealth)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppRuntimeHealthSnapshot)
            : null;
    }

    public Task ForceExternalStateRecoveryAsync() =>
        RequestVoidAsync(SettingsIpcMethods.AppForceExternalRecovery);

    public DragDropPermissionDiagnostic DiagnoseDragDropPermissions()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.AppDiagnoseDragDrop)
            .GetAwaiter().GetResult();
        return SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DragDropPermissionDiagnostic)
            ?? new DragDropPermissionDiagnostic(
                DragDropDiagnosticSeverity.Error,
                DragDropDiagnosticIssue.None,
                "Diagnose unavailable",
                "The main process did not answer the diagnose request.",
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                false,
                false,
                false,
                false,
                false,
                false,
                false);
    }

    public DragDropPermissionRepairResult RepairDragDropPermissions()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.AppRepairDragDrop)
            .GetAwaiter().GetResult();
        return SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DragDropPermissionRepairResult)
            ?? new DragDropPermissionRepairResult(false, false, 0, "repair request failed", false);
    }

    private async Task RequestVoidAsync(string method, string? payload = null)
    {
        await _client.RequestAsync(method, payload);
    }
}
