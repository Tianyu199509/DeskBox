namespace DeskBox.Contracts;

using System.Threading;
using DeskBox.Models;
using DeskBox.Services;

/// <summary>
/// Settings-side surface for app-shell operations: lifecycle (restart,
/// relaunch, shutdown), main-process window openers, tray/snap/raised-band
/// shell state, managed-storage desktop shortcut, diagnostics bundles, and
/// drag-drop permission diagnose/repair. Everything here must run in the
/// main process once the settings UI runs in its own process; the cleanup
/// schedulers degrade to no-ops there because closing the settings window
/// exits that process outright.
/// </summary>
public interface ISettingsAppActions
{
    // ── Lifecycle ───────────────────────────────────────────────────

    Task ShutdownForRestartAsync();

    Task ShutdownForUpdateAsync();

    /// <summary>Schedules the app restart that outlives this process.</summary>
    AppRelaunchScheduleResult ScheduleRestartAfterCurrentProcessExit();

    /// <summary>
    /// Relaunches the app at Explorer integrity with the given appended
    /// arguments (drag-drop permission repair chain).
    /// </summary>
    bool TryRelaunchAsExplorerUser(string arguments);

    // ── Main-process window openers ─────────────────────────────────

    void ShowOnboarding(bool resumeProgress = false);

    void ShowDesktopOrganizationWindow(nint ownerWindowHandle = 0);

    void OpenReleaseNotesWindow(
        AppUpdateManifest? manifest,
        string version,
        Func<CancellationToken, Task<AppUpdateManifest?>>? manifestLoader,
        nint ownerWindowHandle = 0);

    // ── Shell state ─────────────────────────────────────────────────

    void UpdateTrayIcon();

    void SetSnapEnabled(bool value);

    void SetSnapSpacingDips(double value);

    void BringSettingsWindowToRaisedBand(nint windowHandle, string reason);

    void ReleaseRaisedBandGuest(nint windowHandle, string reason);

    /// <summary>In-process memory hygiene after an appearance commit.</summary>
    void ScheduleLightMemoryCleanup();

    /// <summary>In-process memory hygiene after the window hides.</summary>
    void ScheduleSettingsHiddenCleanup();

    // ── Managed-storage desktop shortcut ────────────────────────────

    bool HasManagedStorageDesktopShortcut();

    Task<bool> CreateManagedStorageDesktopShortcutAsync();

    Task<bool> RemoveManagedStorageDesktopShortcutAsync();

    Task SyncManagedStorageDesktopShortcutAsync(string oldRootPath);

    // ── Diagnostics ─────────────────────────────────────────────────

    /// <summary>Snapshot + bundle export; returns the archive path.</summary>
    Task<string> ExportDiagnosticBundleAsync(string destinationDirectory);

    /// <summary>Snapshot + bundle export + feedback upload.</summary>
    Task<bool> UploadDiagnosticsForFeedbackAsync(long feedbackId, string tempDirectory);

    AppRuntimeHealthSnapshot? GetRuntimeHealthSnapshot();

    Task ForceExternalStateRecoveryAsync();

    // ── Drag-drop permissions ───────────────────────────────────────

    DragDropPermissionDiagnostic DiagnoseDragDropPermissions();

    DragDropPermissionRepairResult RepairDragDropPermissions();
}
