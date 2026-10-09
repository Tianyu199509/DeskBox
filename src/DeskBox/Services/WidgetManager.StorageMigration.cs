using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private readonly SemaphoreSlim _storageMigrationGate = new(1, 1);
    private ManagedStorageMigrationService? _storageMigration;
    internal ManagedStorageMigrationService StorageMigration =>
        _storageMigration ??= new(_settingsService.ManagedStorageMigrationDirectory);
    internal ManagedStorageMigrationDiagnostic? StorageMigrationDiagnostic => _storageMigration?.Diagnostic;

    public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath, ManagedStorageMigrationOptions? options = null)
    {
        options ??= new();
        await _storageMigrationGate.WaitAsync(options.CancellationToken);
        List<ManagedStorageMigrationFolder> folders = [];
        try
        {
            string oldRoot = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
            if (ManagedStorageMigrationService.Same(oldRoot, newRoot))
                return new(0, oldRoot, newRoot, 0, 0, false);
            var widgets = GetStorageMigrationWidgets();
            folders = BuildStorageMigrationFolders(oldRoot, newRoot, widgets);
            SetManagedStorageMigrationBusy(folders.Select(f => f.WidgetId), true);
            using var transfer = _fileService.TransferSessions.Begin(folders.Select(f =>
                new FileTransferRegistration(f.SourceFolder, f.DestinationFolder, SourceIsDirectory: true)), isMove: true);
            App.Log($"[ManagedStorageMigration] Copy transaction requested widgets={folders.Count} source='{oldRoot}' target='{newRoot}'");
            var (journal, resumed) = await StorageMigration.CopyAsync(oldRoot, newRoot, folders, options);
            options.CancellationToken.ThrowIfCancellationRequested();
            // A component created/removed/repointed during copying invalidates the
            // whole batch. Never silently commit a stale widget snapshot.
            if (!ManagedStorageMigrationService.Same(oldRoot, _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath) ||
                !_settingsService.Settings.WidgetLayout.Widgets.Where(w => w.WidgetKind == WidgetKind.File &&
                    w.FollowsDefaultStoragePath && !IsDeleted(w.Id)).Select(w => w.Id).ToHashSet().SetEquals(widgets.Select(w => w.Id)) ||
                widgets.Any(w => folders.All(f => f.WidgetId != w.Id ||
                    f.OriginalMappedFolderPath != w.MappedFolderPath || f.OriginalManagedFolderName != w.ManagedFolderName)))
                throw new IOException("Widget storage settings changed while copying. No migration settings were committed.");

            int copiedFiles = journal.Entries.Count(e => !e.IsDirectory);
            journal.State = "Committing";
            await StorageMigration.SaveAsync(journal);
            options.Progress?.Report(new(ManagedStorageMigrationPhase.Committing, copiedFiles, copiedFiles,
                journal.TotalBytes, journal.TotalBytes, null));
            try
            {
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = newRoot;
                foreach (var widget in widgets)
                {
                    var folder = folders.Single(f => f.WidgetId == widget.Id);
                    widget.ManagedFolderName = folder.ManagedFolderName;
                    widget.MappedFolderPath = folder.DestinationFolder;
                }
                if (!await _settingsService.SaveCheckedAsync())
                    throw new IOException("The verified copy was retained, but the new storage settings could not be saved.");
            }
            catch
            {
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = oldRoot;
                foreach (var widget in widgets)
                {
                    var folder = folders.Single(f => f.WidgetId == widget.Id);
                    widget.ManagedFolderName = folder.OriginalManagedFolderName;
                    widget.MappedFolderPath = folder.OriginalMappedFolderPath;
                }
                // Original files never moved. The durable journal also repairs
                // an interrupted two-document commit on next startup.
                await _settingsService.SaveCheckedAsync(notifySubscribers: false);
                throw;
            }

            journal.State = "Committed";
            journal.Entries.Clear();
            try { await StorageMigration.SaveAsync(journal); }
            catch (Exception ex) { App.Log($"[ManagedStorageMigration] Commit receipt will reconcile on startup: {ex}"); }
            foreach (var folder in folders)
            {
                try { await RefreshFileWidgetAsync(folder.WidgetId); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Committed widget refresh failed: {ex}"); }
            }
            try
            {
                SyncStorageFolderEntries();
            }
            catch (Exception ex) { App.Log($"[ManagedStorageMigration] Post-commit shortcut sync deferred: {ex.Message}"); }
            App.Log($"[ManagedStorageMigration] Committed id={journal.Id} files={copiedFiles} bytes={journal.TotalBytes} originalsRetained=true");
            options.Progress?.Report(new(ManagedStorageMigrationPhase.Completed, copiedFiles, copiedFiles,
                journal.TotalBytes, journal.TotalBytes, null));
            return new(folders.Count, oldRoot, newRoot, copiedFiles, journal.TotalBytes, resumed, journal.TemporaryBytes);
        }
        catch (Exception ex)
        {
            await StorageMigration.RecordStoppedAsync(ex);
            long? temporaryBytes = StorageMigration.Diagnostic?.TemporaryBytes;
            if (ex is OperationCanceledException)
            {
                App.Log("[ManagedStorageMigration] User stopped the task; original files retained.");
                throw new StorageMigrationStoppedException(ex, temporaryBytes);
            }
            App.Log($"[ManagedStorageMigration] Failed; source files retained: {ex}");
            var failure = ex as StorageMigrationException ??
                new StorageMigrationException(ManagedStorageMigrationPresentation.Classify(ex), ex.Message, ex);
            failure.TemporaryBytes = temporaryBytes;
            throw failure;
        }
        finally
        {
            try { SetManagedStorageMigrationBusy(folders.Select(f => f.WidgetId), false); }
            finally { _storageMigrationGate.Release(); }
        }
    }

    internal async Task RecoverManagedStorageCommitAsync()
    {
        try
        {
            foreach (var journal in await StorageMigration.ReadPendingCommitsAsync())
            {
                var widgets = journal.Folders.Select(f => _settingsService.Settings.WidgetLayout.Widgets
                    .FirstOrDefault(w => w.Id == f.WidgetId && !IsDeleted(w.Id))).ToList();
                if (widgets.Any(w => w is null)) continue;
                bool allNew = journal.Folders.Select((f, i) =>
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.DestinationFolder)).All(v => v);
                bool known = journal.Folders.Select((f, i) =>
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.DestinationFolder) ||
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.OriginalMappedFolderPath)).All(v => v);
                string root = _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath;
                if (allNew && ManagedStorageMigrationService.Same(root, journal.NewRootPath))
                {
                    journal.State = "Committed";
                    journal.Entries.Clear();
                    await StorageMigration.SaveAsync(journal);
                }
                else if (known && (ManagedStorageMigrationService.Same(root, journal.NewRootPath) ||
                                  ManagedStorageMigrationService.Same(root, journal.OldRootPath)))
                {
                    // Interrupted metadata commit: return every affected mapping
                    // to its untouched original. No file access/deletion is needed,
                    // even when a BitLocker target has not yet been unlocked.
                    _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = journal.OldRootPath;
                    for (int i = 0; i < widgets.Count; i++)
                    {
                        widgets[i]!.MappedFolderPath = journal.Folders[i].OriginalMappedFolderPath;
                        widgets[i]!.ManagedFolderName = journal.Folders[i].OriginalManagedFolderName;
                    }
                    if (await _settingsService.SaveCheckedAsync(notifySubscribers: false))
                    {
                        journal.State = "Verified";
                        await StorageMigration.SaveAsync(journal);
                    }
                }
            }
        }
        catch (Exception ex) { App.Log($"[ManagedStorageMigration] Recovery needs attention; copies retained: {ex}"); }
    }
    internal Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath, CancellationToken token)
    {
        string oldRoot = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
        var folders = BuildStorageMigrationFolders(oldRoot, newRoot, GetStorageMigrationWidgets());
        return StorageMigration.PreviewAsync(newRoot, folders, token);
    }

    private List<WidgetConfig> GetStorageMigrationWidgets() => _settingsService.Settings.WidgetLayout.Widgets
        .Where(w => w.WidgetKind == WidgetKind.File && w.FollowsDefaultStoragePath && !IsDeleted(w.Id)).ToList();

    private List<ManagedStorageMigrationFolder> BuildStorageMigrationFolders(
        string oldRoot, string newRoot, IReadOnlyList<WidgetConfig> widgets)
    {
        if (ManagedStoragePathService.IsSameOrDescendant(newRoot, oldRoot) ||
            ManagedStoragePathService.IsSameOrDescendant(oldRoot, newRoot))
            throw new StorageMigrationException(StorageMigrationProblem.RootsOverlap,
                "The old and new storage roots must not contain one another.");

        var folders = new List<ManagedStorageMigrationFolder>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var widget in widgets)
        {
            string name = string.IsNullOrWhiteSpace(widget.ManagedFolderName)
                ? CreateManagedFolderName(widget.Name, widget.Id) : widget.ManagedFolderName;
            if (name is "." or ".." || Path.GetFileName(name) != name ||
                name.Equals(ManagedStorageMigrationService.TemporaryFolderName, StringComparison.OrdinalIgnoreCase) ||
                !names.Add(name))
                throw new IOException("Managed folder names must be unique, direct children of the selected root.");
            string source = Path.GetFullPath(string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                ? Path.Combine(oldRoot, name) : widget.MappedFolderPath);
            string destination = Path.Combine(newRoot, name);
            try { EnsureFileWidgetPathAvailable(destination, widget.Id, candidateFollowsDefaultStoragePath: true); }
            catch (InvalidOperationException ex)
            {
                throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict, ex.Message, ex);
            }
            folders.Add(new(widget.Id, widget.Name, name, source, destination,
                widget.ManagedFolderName, widget.MappedFolderPath));
        }
        foreach (var source in folders)
        foreach (var target in folders)
        {
            if (ManagedStoragePathService.IsSameOrDescendant(target.DestinationFolder, source.SourceFolder) ||
                ManagedStoragePathService.IsSameOrDescendant(source.SourceFolder, target.DestinationFolder))
                throw new StorageMigrationException(StorageMigrationProblem.RootsOverlap,
                    "Source and destination widget folders must not overlap.");
        }

        return folders;
    }
}
