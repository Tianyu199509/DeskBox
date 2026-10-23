namespace DeskBox.Contracts;

using System.Threading;
using DeskBox.Models;
using DeskBox.Services;

/// <summary>
/// Settings-side surface for widget-host operations, which live in the main
/// process once the settings UI runs in its own process: feature-widget
/// enable state, widget group editing, orphan managed-folder cleanup,
/// managed-storage root migration, glance instance management, display
/// moves, and folder-watcher health. Availability degrades exactly like the
/// previous inline null checks: <see cref="IsHostAvailable"/> is false in
/// headless hosts and value-returning members fall back to settings-only
/// behavior where the old call sites did.
/// </summary>
public interface ISettingsWidgetActions
{
    /// <summary>False when no widget manager exists (headless host).</summary>
    bool IsHostAvailable { get; }

    // ── Feature widgets ─────────────────────────────────────────────

    /// <summary>
    /// Live enable state, falling back to the persisted setting when no
    /// manager exists.
    /// </summary>
    bool IsFeatureWidgetEnabled(WidgetKind kind);

    /// <summary>
    /// Applies an enable change through the live manager, falling back to a
    /// plain settings save when no manager exists.
    /// </summary>
    Task SyncFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled);

    /// <summary>
    /// Resets a feature widget to defaults through the live manager, falling
    /// back to a plain settings save when no manager exists.
    /// </summary>
    Task ResetFeatureWidgetAsync(WidgetKind kind);

    void RefreshVisibleWidgetDesktopLayers(string reason);

    void NotifyWidgetGroupPresentationSettingsChanged();

    // ── Widget groups ───────────────────────────────────────────────

    Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId);

    Task<bool> RemoveWidgetFromGroupAsync(string widgetId);

    Task<bool> DissolveWidgetGroupContainingAsync(string widgetId);

    // ── Orphan managed folders ──────────────────────────────────────

    /// <summary>Candidates, or null when no manager exists.</summary>
    IReadOnlyList<ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders();

    Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths);

    Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath);

    Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle);

    // ── Managed-storage root migration ──────────────────────────────

    int GetDefaultManagedStorageWidgetCount();

    Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath,
        CancellationToken token);

    Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options);

    // ── Glance instances ────────────────────────────────────────────

    IReadOnlyList<GlanceWidgetInstanceInfo> GetGlanceWidgetInstances();

    Task<GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(string? sourceWidgetId);

    Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled);

    Task<bool> LocateGlanceWidgetAsync(string widgetId);

    Task RemoveGlanceWidgetAsync(string widgetId);

    Task RenameWidgetAsync(string widgetId, string newName);

    // ── Displays ────────────────────────────────────────────────────

    Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
        string displayStableId);

    Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token);

    Task ApplyScreenBindingAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundScreenId,
        bool persistImmediately = true);

    // ── Diagnostics ─────────────────────────────────────────────────

    /// <summary>Watcher health snapshots, or null when no manager exists.</summary>
    IReadOnlyList<FolderWatcherHealthSnapshot>? GetFolderWatcherHealthSnapshots();
}
