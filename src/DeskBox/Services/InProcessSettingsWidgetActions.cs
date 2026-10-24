namespace DeskBox.Services;

using System.Threading;
using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// In-process default of <see cref="ISettingsWidgetActions"/>: a thin wrapper
/// over the App-owned widget manager, used while the settings UI still runs
/// inside the main process. The manager may be null in headless hosts; every
/// member degrades exactly like the previous inline null checks.
/// </summary>
public sealed class InProcessSettingsWidgetActions : ISettingsWidgetActions
{
    private readonly WidgetManager? _widgetManager;
    private readonly SettingsService _settingsService;
    private readonly FileService? _fileService;
    private readonly OrganizerService? _organizerService;
    private readonly LocalizationService _localizationService;

    private readonly Func<bool, Task>? _applyQuickCaptureEnabled;
    private readonly Func<bool, Task>? _applyTodoEnabled;
    private readonly Func<bool, Task>? _applySearchEnabled;

    public InProcessSettingsWidgetActions(
        WidgetManager? widgetManager,
        SettingsService settingsService,
        FileService? fileService,
        OrganizerService? organizerService,
        LocalizationService localizationService,
        Func<bool, Task>? applyQuickCaptureEnabled = null,
        Func<bool, Task>? applyTodoEnabled = null,
        Func<bool, Task>? applySearchEnabled = null)
    {
        _applyQuickCaptureEnabled = applyQuickCaptureEnabled;
        _applyTodoEnabled = applyTodoEnabled;
        _applySearchEnabled = applySearchEnabled;
        _widgetManager = widgetManager;
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _localizationService = localizationService;
    }

    public bool IsHostAvailable => _widgetManager is not null;

    public bool IsFeatureWidgetEnabled(WidgetKind kind) =>
        _widgetManager?.IsFeatureWidgetEnabled(kind) ??
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);

    public async Task SyncFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled)
    {
        if (_widgetManager is not { } manager)
        {
            await _settingsService.SaveAsync();
            return;
        }

        await manager.SetFeatureWidgetEnabledAsync(kind, enabled, reveal: enabled);
    }

    public async Task<bool> SetFeatureWidgetEnabled(WidgetKind kind, bool enabled)
    {
        // Every feature switch rides this one command. QuickCapture/Todo/
        // Search have their own explicit coordinator chains whose side
        // effects (widget apply, runtime release, reminder refresh) only
        // happen on the explicit call — an external snapshot alone never
        // triggers them. The dictionary kinds write the main-process-owned
        // dictionary and apply through the manager in the same command.
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                if (_applyQuickCaptureEnabled is { } applyQuickCapture)
                {
                    await applyQuickCapture(enabled);
                    return FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
                }

                break;
            case WidgetKind.Todo:
                if (_applyTodoEnabled is { } applyTodo)
                {
                    await applyTodo(enabled);
                    return FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
                }

                break;
            case WidgetKind.Search:
                if (_applySearchEnabled is { } applySearch)
                {
                    await applySearch(enabled);
                    return FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
                }

                break;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
        if (_widgetManager is not { } manager)
        {
            await _settingsService.SaveAsync();
            return enabled;
        }

        await manager.SetFeatureWidgetEnabledAsync(kind, enabled, reveal: enabled);
        return enabled;
    }

    public async Task ResetFeatureWidgetAsync(WidgetKind kind)
    {
        if (_widgetManager is not { } manager)
        {
            await _settingsService.SaveAsync();
            return;
        }

        await manager.ResetFeatureWidgetAsync(kind);
    }

    public void RefreshVisibleWidgetDesktopLayers(string reason) =>
        _widgetManager?.RefreshVisibleWidgetDesktopLayers(reason);

    public void NotifyWidgetGroupPresentationSettingsChanged() =>
        _widgetManager?.NotifyWidgetGroupPresentationSettingsChanged();

    public Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId) =>
        _widgetManager is { } manager
            ? manager.ReorderWidgetGroupMemberAsync(sourceWidgetId, targetWidgetId)
            : Task.FromResult(false);

    public Task<bool> RemoveWidgetFromGroupAsync(string widgetId) =>
        _widgetManager is { } manager
            ? manager.RemoveWidgetFromGroupAsync(widgetId, revealStandalone: true)
            : Task.FromResult(false);

    public Task<bool> DissolveWidgetGroupContainingAsync(string widgetId) =>
        _widgetManager is { } manager
            ? manager.DissolveWidgetGroupContainingAsync(widgetId)
            : Task.FromResult(false);

    public IReadOnlyList<ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders() =>
        _widgetManager?.GetOrphanManagedStorageFolders();

    public Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths) =>
        _widgetManager is { } manager
            ? manager.RestoreOrphanManagedStorageFoldersAsync(folderPaths)
            : Task.FromResult(0);

    public Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath) =>
        _widgetManager is { } manager
            ? manager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(folderPath)
            : Task.CompletedTask;

    public Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle) =>
        _widgetManager is { } manager
            ? manager.DeleteOrphanManagedStorageFolderAsync(folderPath, ownerWindowHandle)
            : Task.CompletedTask;

    public int GetDefaultManagedStorageWidgetCount() =>
        _widgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;

    public Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath,
        CancellationToken token) =>
        _widgetManager is { } manager
            ? manager.PreviewManagedStorageMigrationAsync(newRootPath, token)
            : Task.FromResult(new ManagedStorageMigrationPreview(0, 0, 0, false, null));

    public Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options) =>
        _widgetManager is { } manager
            ? manager.UpdateDefaultManagedStorageRootAsync(newRootPath, options)
            : Task.FromResult(new ManagedStorageMigrationResult(0, string.Empty, newRootPath, 0, 0, false));

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetGlanceWidgetInstances() =>
        _widgetManager?.GetGlanceWidgetInstances() ?? [];

    public bool IsGlanceFeatureEnabled => _widgetManager?.IsGlanceFeatureEnabled ?? false;

    public Task<GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(string? sourceWidgetId) =>
        _widgetManager is { } manager
            ? manager.CreateGlanceWidgetAsync(sourceWidgetId)
            : Task.FromResult(new GlanceWidgetInstanceInfo(string.Empty, string.Empty, false));

    public Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled) =>
        _widgetManager is { } manager
            ? manager.SetGlanceWidgetInstanceEnabledAsync(widgetId, enabled)
            : Task.CompletedTask;

    public Task<bool> LocateGlanceWidgetAsync(string widgetId) =>
        _widgetManager is { } manager
            ? manager.LocateGlanceWidgetAsync(widgetId)
            : Task.FromResult(false);

    public Task RemoveGlanceWidgetAsync(string widgetId) =>
        _widgetManager is { } manager
            ? manager.RemoveGlanceWidgetAsync(widgetId)
            : Task.CompletedTask;

    public Task RenameWidgetAsync(string widgetId, string newName) =>
        _widgetManager is { } manager
            ? manager.RenameWidgetAsync(widgetId, newName)
            : Task.CompletedTask;

    public Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
        string displayStableId) =>
        _widgetManager is { } manager
            ? manager.MoveAllWidgetSurfacesToDisplayAsync(displayStableId)
            : Task.FromResult((0, new WidgetMoveAllUndoToken()));

    public Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token) =>
        _widgetManager is { } manager
            ? manager.UndoMoveAllWidgetSurfacesAsync(token)
            : Task.FromResult(false);

    public Task ApplyScreenBindingAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundScreenId,
        bool persistImmediately = true) =>
        _widgetManager is { } manager
            ? manager.ApplyScreenBindingAsync(widgetId, mode, boundScreenId, persistImmediately)
            : Task.CompletedTask;

    public IReadOnlyList<FolderWatcherHealthSnapshot>? GetFolderWatcherHealthSnapshots() =>
        _widgetManager?.GetFolderWatcherHealthSnapshots();

    public OrganizationHistoryEntry? GetLatestUndoableDesktopOrganizationEntry() =>
        _organizerService?.GetLatestUndoableEntry();

    public OrganizationHistoryEntry? GetDesktopOrganizationHistoryEntry(string historyId) =>
        _settingsService.OrganizationHistory.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, historyId, StringComparison.Ordinal));

    public async Task<DesktopOrganizationUndoResult> UndoDesktopOrganizationAsync(
        string historyId,
        nint ownerWindowHandle)
    {
        if (_widgetManager is not { } manager ||
            _fileService is not { } fileService ||
            _organizerService is not { } organizerService)
        {
            return new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Failed,
                Error: "host-unavailable");
        }

        var coordinator = new DesktopOrganizationCoordinator(
            _settingsService,
            fileService,
            manager,
            organizerService,
            _localizationService);
        try
        {
            await coordinator.UndoAsync(historyId, (IntPtr)ownerWindowHandle);
            return new DesktopOrganizationUndoResult(DesktopOrganizationUndoOutcome.Completed);
        }
        catch (DesktopOrganizationIncompleteUndoException ex)
        {
            return new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Incomplete,
                ex.RestoredCount,
                ex.RemainingCount);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Undo from settings failed: {ex}");
            return new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Failed,
                Error: ex.Message);
        }
    }

    public async Task<DesktopOrganizationUndoResult> AbandonDesktopOrganizationUndoAsync(string historyId)
    {
        if (_widgetManager is not { } manager ||
            _fileService is not { } fileService ||
            _organizerService is not { } organizerService)
        {
            return new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Failed,
                Error: "host-unavailable");
        }

        var coordinator = new DesktopOrganizationCoordinator(
            _settingsService,
            fileService,
            manager,
            organizerService,
            _localizationService);
        try
        {
            await coordinator.AbandonUndoAsync(historyId);
            return new DesktopOrganizationUndoResult(DesktopOrganizationUndoOutcome.Completed);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Abandon undo from settings failed: {ex}");
            return new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Failed,
                Error: ex.Message);
        }
    }
}
