namespace DeskBox.Services;

using System.Threading;
using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// In-process default of <see cref="ISettingsAppActions"/>: the app-shell
/// facade. Holds the App instance injected at construction (never a global
/// lookup) and forwards every member to the same App surface the settings
/// window reached for inline before the facade.
/// </summary>
public sealed class InProcessSettingsAppActions : ISettingsAppActions
{
    private readonly App _app;
    private readonly LocalizationService _localizationService;
    private FeedbackService? _feedbackService;

    private FeedbackService FeedbackService => _feedbackService ??= new FeedbackService(
        localeProvider: () => _localizationService.CurrentCultureName);

    public InProcessSettingsAppActions(App app, LocalizationService localizationService)
    {
        _app = app;
        _localizationService = localizationService;
    }

    public Task ShutdownForRestartAsync() => _app.ShutdownForRestartAsync();

    public Task ShutdownForUpdateAsync() => _app.ShutdownForUpdateAsync();

    public AppRelaunchScheduleResult ScheduleRestartAfterCurrentProcessExit() =>
        AppRelaunchService.ScheduleAfterCurrentProcessExit();

    public bool TryRelaunchAsExplorerUser(string arguments) =>
        DragDropPermissionService.TryRelaunchAsExplorerUser(arguments);

    public void ShowOnboarding(bool resumeProgress = false) =>
        _app.ShowOnboarding(resumeProgress);

    public void ShowDesktopOrganizationWindow(nint ownerWindowHandle = 0) =>
        _app.ShowDesktopOrganizationWindow((IntPtr)ownerWindowHandle);

    public void OpenReleaseNotesWindow(
        AppUpdateManifest? manifest,
        string version,
        Func<CancellationToken, Task<AppUpdateManifest?>>? manifestLoader,
        nint ownerWindowHandle = 0) =>
        _app.OpenAppReleaseNotesWindow(
            manifest,
            version,
            manifestLoader,
            (IntPtr)ownerWindowHandle);

    public void UpdateTrayIcon() => _app.UpdateTrayIcon();

    public void SetSnapEnabled(bool value) => _app.ResizeGuideOverlay.IsSnapEnabled = value;

    public void SetSnapSpacingDips(double value) => _app.ResizeGuideOverlay.SnapSpacingDips = value;

    public void BringSettingsWindowToRaisedBand(nint windowHandle, string reason) =>
        _app.WidgetManager?.BringAuxiliaryWindowToFront((IntPtr)windowHandle, reason);

    public void ReleaseRaisedBandGuest(nint windowHandle, string reason) =>
        _app.WidgetManager?.ReleaseRaisedBandGuest((IntPtr)windowHandle, reason);

    public void ScheduleLightMemoryCleanup() => App.ScheduleLightMemoryCleanup();

    public void ScheduleSettingsHiddenCleanup() =>
        App.ScheduleBackgroundMemoryCleanup("settings-hidden");

    public bool HasManagedStorageDesktopShortcut() =>
        _app.ManagedStorageDesktopShortcutService.HasShortcut();

    public Task<bool> CreateManagedStorageDesktopShortcutAsync() =>
        _app.ManagedStorageDesktopShortcutService.CreateAsync();

    public Task<bool> RemoveManagedStorageDesktopShortcutAsync() =>
        _app.ManagedStorageDesktopShortcutService.RemoveAsync();

    public Task SyncManagedStorageDesktopShortcutAsync(string oldRootPath) =>
        _app.ManagedStorageDesktopShortcutService.SyncAsync(oldRootPath);

    public async Task<string> ExportDiagnosticBundleAsync(string destinationDirectory)
    {
        DeskBoxDiagnosticSnapshot snapshot = _app.CreateDiagnosticSnapshot();
        return await _app.DiagnosticsBundleService.ExportAsync(
            destinationDirectory,
            snapshot,
            DeskBoxDataPathService.Current.LogFilePath);
    }

    public async Task<bool> UploadDiagnosticsForFeedbackAsync(long feedbackId, string tempDirectory)
    {
        string archivePath = await ExportDiagnosticBundleAsync(tempDirectory);
        try
        {
            DeskBoxFeedbackDiagnosticsStatus status = await FeedbackService.UploadDiagnosticsAsync(
                feedbackId,
                archivePath,
                CancellationToken.None);
            return status == DeskBoxFeedbackDiagnosticsStatus.Uploaded;
        }
        finally
        {
            try { File.Delete(archivePath); }
            catch (Exception ex)
            {
                App.Log($"[Feedback] Temp diagnostics cleanup failed: {ex.Message}");
            }
        }
    }

    public AppRuntimeHealthSnapshot? GetRuntimeHealthSnapshot() =>
        _app.DiagnosticsService?.GetRuntimeHealthSnapshot(
            _app.EverythingSearchService,
            _app.WidgetManager?.GetFolderWatcherHealthSnapshots());

    public Task ForceExternalStateRecoveryAsync() =>
        _app.ForceExternalStateRecoveryAsync();

    public DragDropPermissionDiagnostic DiagnoseDragDropPermissions() =>
        DragDropPermissionService.Diagnose(_localizationService);

    public DragDropPermissionRepairResult RepairDragDropPermissions() =>
        DragDropPermissionService.Repair(_app.SettingsService);
}
