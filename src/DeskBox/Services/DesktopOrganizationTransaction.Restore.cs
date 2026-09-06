using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class DesktopOrganizationTransaction
{
    public async Task UndoAsync(string historyId, IntPtr ownerWindowHandle = default)
    {
        await OperationGate.WaitAsync();
        try
        {
            var history = _settingsService.Settings.RecentOrganizationHistory.FirstOrDefault(entry => entry.Id == historyId)
                ?? throw new InvalidOperationException("The organization history no longer exists.");
            if (!history.CanUndo || history.IsUndone) throw new InvalidOperationException("This operation cannot be undone.");
            var pending = await _recoveryStore.LoadAsync();
            if (pending is not null)
            {
                if (!pending.IsUndo || pending.TransactionId != historyId)
                    throw new InvalidOperationException("Recover the pending desktop operation first.");
                ApplyUndoReceipts(history, pending);
            }
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var journal = new DesktopOrganizationRecoveryJournal
            {
                TransactionId = historyId,
                IsUndo = true,
                Items = history.Items.Where(item => !item.IsRestored).Select(item => new DesktopOrganizationRecoveryItem
                {
                    SourcePath = item.SourcePath,
                    DestinationPath = item.DestinationPath,
                    RestorePath = FileService.GetAvailablePath(item.SourcePath, reserved),
                    TargetWidgetId = item.TargetWidgetId,
                    SourceScope = item.SourceScope,
                    Size = item.Size,
                    LastWriteTimeUtc = item.LastWriteTimeUtc
                }).ToList()
            };
            await _recoveryStore.SaveAsync(journal);
            await RestoreItemsAsync(journal, ownerWindowHandle);
            ApplyUndoReceipts(history, journal);
            await _settingsService.SaveAsync(notifySubscribers: false);
            _recoveryStore.Clear();
            int remaining = history.Items.Count(item => !item.IsRestored);
            if (remaining > 0)
                throw new DesktopOrganizationIncompleteUndoException(history.Items.Count - remaining, remaining);
        }
        finally { OperationGate.Release(); }
    }

    public async Task<int> RecoverPendingAsync(IntPtr ownerWindowHandle = default)
    {
        await OperationGate.WaitAsync();
        try
        {
            var journal = await _recoveryStore.LoadAsync();
            if (journal is null) return 0;
            var history = _settingsService.Settings.RecentOrganizationHistory.FirstOrDefault(entry => entry.Id == journal.TransactionId);
            if (journal.IsUndo)
            {
                // Startup only reconciles receipts. Unfinished undo remains in
                // history and never prompts for elevation in the background.
                if (history is null) return 0;
                ApplyUndoReceipts(history, journal);
                await _settingsService.SaveAsync(notifySubscribers: false);
                _recoveryStore.Clear();
                return journal.Items.Count(item => item.Completed);
            }

            // Saving settings is the commit point. A crash before deleting the
            // journal must not roll back an already committed (or retried) item.
            journal.Items.RemoveAll(item => history?.Items.Any(committed =>
                string.Equals(committed.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)) == true);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<DesktopOrganizationRecoveryItem>();
            foreach (var item in journal.Items)
            {
                if (!EntryExists(item.DestinationPath)) continue;
                // A planned name alone is not proof that the move happened.
                if (!item.Completed && (EntryExists(item.SourcePath) || !MatchesSnapshot(item.DestinationPath, item))) continue;
                item.RestorePath ??= FileService.GetAvailablePath(item.SourcePath, reserved);
                candidates.Add(item);
            }
            journal.Items = candidates;
            // Completion below refers to restoration; the original move receipt
            // is no longer needed once a durable restore destination is assigned.
            foreach (var item in journal.Items) item.Completed = false;
            await _recoveryStore.SaveAsync(journal);
            await RestoreItemsAsync(journal, ownerWindowHandle);
            int restored = journal.Items.Count(item => item.Completed);
            journal.Items.RemoveAll(item => item.Completed);
            if (journal.Items.Count > 0)
            {
                await _recoveryStore.SaveAsync(journal);
                return restored;
            }

            if (history is { Items.Count: 0 })
                _settingsService.Settings.RecentOrganizationHistory.Remove(history);
            var createdIds = journal.CreatedWidgetIds.ToHashSet(StringComparer.Ordinal);
            // Keep widgets that a committed retry still uses, or that acquired
            // other files since the interrupted operation.
            createdIds.ExceptWith(history?.Targets.Select(target => target.WidgetId) ?? []);
            var removable = _settingsService.Settings.Widgets.Where(widget => createdIds.Contains(widget.Id) &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath) && IsEmptyDirectory(widget.MappedFolderPath)).ToList();
            foreach (var widget in removable)
            {
                _settingsService.Settings.Widgets.Remove(widget);
                _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule => rule.TargetWidgetId == widget.Id);
                RemoveEmptyCreatedDirectories([widget.MappedFolderPath!]);
            }
            await _settingsService.SaveAsync(notifySubscribers: false);
            _recoveryStore.Clear();
            return restored;
        }
        finally { OperationGate.Release(); }
    }

    private async Task RestoreItemsAsync(DesktopOrganizationRecoveryJournal journal, IntPtr ownerWindowHandle)
    {
        var ready = journal.Items.Where(item => !item.Completed && MatchesSnapshot(item.DestinationPath, item)).ToList();
        var batches = ready.Where(item => item.SourceScope == DesktopOrganizationSourceScope.Personal)
            .Select(item => new List<DesktopOrganizationRecoveryItem> { item }).ToList();
        var publicItems = ready.Where(item => item.SourceScope == DesktopOrganizationSourceScope.Public).ToList();
        if (publicItems.Count > 0 && ownerWindowHandle != IntPtr.Zero) batches.Add(publicItems);
        foreach (var batch in batches)
        {
            string suppressionId = Guid.NewGuid().ToString("N");
            var plans = batch.Select(item => new FileService.FileTransferPlan(item.DestinationPath, item.RestorePath!)).ToList();
            AutoOrganizationSuppressions?.BeginOperation(suppressionId, plans);
            void Record(FileService.FileTransferResult result)
            {
                if (!FileService.IsCompletedShellMove(result.SourcePath, result.DestinationPath)) return;
                var item = batch.First(candidate => string.Equals(candidate.DestinationPath, result.SourcePath, StringComparison.OrdinalIgnoreCase));
                item.RestorePath = result.DestinationPath;
                item.Completed = true;
                _recoveryStore.Save(journal);
            }
            try
            {
                var results = await _transfer.MoveAsync(plans,
                    batch[0].SourceScope == DesktopOrganizationSourceScope.Public, ownerWindowHandle, Record, CancellationToken.None);
                foreach (var result in results) Record(result);
            }
            catch (Exception ex)
            {
                if (ex is FileService.IFileTransferWithCompletedResults partial)
                    foreach (var result in partial.CompletedResults) Record(result);
                App.Log($"[DesktopOrganization] Restore remains pending: {ex.Message}");
            }
            finally
            {
                AutoOrganizationSuppressions?.CompleteOperation(suppressionId,
                    batch.Where(item => item.Completed).Select(item => item.RestorePath!)
                        .Concat(plans.Select(plan => plan.DestinationPath)));
            }
        }
    }

    private static void ApplyUndoReceipts(OrganizationHistoryEntry history, DesktopOrganizationRecoveryJournal journal)
    {
        history.UndoStarted = true;
        foreach (var receipt in journal.Items)
        {
            if (!receipt.Completed && (EntryExists(receipt.DestinationPath) || receipt.RestorePath is null ||
                !MatchesSnapshot(receipt.RestorePath, receipt))) continue;
            var item = history.Items.FirstOrDefault(candidate => string.Equals(candidate.DestinationPath,
                receipt.DestinationPath, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            item.IsRestored = true;
            item.RestoredPath = receipt.RestorePath;
        }
        history.IsUndone = history.Items.All(item => item.IsRestored);
        history.CanUndo = !history.IsUndone;
    }

    private static bool MatchesSnapshot(string path, DesktopOrganizationRecoveryItem item)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return (!item.Size.HasValue || file.Length == item.Size.Value) &&
                    (!item.LastWriteTimeUtc.HasValue || file.LastWriteTimeUtc == item.LastWriteTimeUtc.Value);
            }
            return Directory.Exists(path) && !item.Size.HasValue &&
                (!item.LastWriteTimeUtc.HasValue || Directory.GetLastWriteTimeUtc(path) == item.LastWriteTimeUtc.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsEmptyDirectory(string path) => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
}
