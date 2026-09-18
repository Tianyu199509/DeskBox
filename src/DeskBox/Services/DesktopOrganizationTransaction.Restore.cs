using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class DesktopOrganizationTransaction
{
    public async Task UndoAsync(string historyId, IntPtr ownerWindowHandle = default)
    {
        await OperationGate.WaitAsync();
        try
        {
            var history = _settingsService.OrganizationHistory.Entries.FirstOrDefault(entry => entry.Id == historyId)
                ?? throw new InvalidOperationException("The organization history no longer exists.");
            if (!history.CanUndo || history.IsUndone) throw new InvalidOperationException("This operation cannot be undone.");
            var pending = await _recoveryStore.LoadAsync();
            if (pending is not null)
            {
                if (pending.IsAbandoned || !pending.IsUndo || pending.TransactionId != historyId)
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
                    LastWriteTimeUtc = item.LastWriteTimeUtc,
                    // The history receipt travels with the undo candidate:
                    // verification compares the object at the destination
                    // against the identity recorded at move time. Legacy
                    // entries without one keep their null identity — no
                    // automatic undo authority — instead of capturing a
                    // fresh identity that would hand a replacement a valid id.
                    DestinationIdentity = item.DestinationIdentity
                }).ToList()
            };

            await _recoveryStore.SaveAsync(journal);
            await RestoreItemsAsync(journal, ownerWindowHandle);
            ApplyUndoReceipts(history, journal);
            // Checked persistence: clearing the journal below must only
            // happen once the reconciled receipts are durable. Settings land
            // first (the dependent half); the history entry drops last as the
            // linearization point — a durable terminal receipt proves both
            // halves committed and can never revive this undo.
            if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
                !await _settingsService.OrganizationHistory.SaveCheckedAsync())
            {
                throw new IOException(
                    "Persisting the undo receipts failed; the recovery journal is kept for the next launch.");
            }

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
            if (journal is null)
            {
                // Startup always lands here: with no journal outstanding this
                // is the safe window to shrink bloated history profiles.
                await CompactHistoryAfterJournalResolutionAsync();
                return 0;
            }

            if (journal.IsAbandoned)
            {
                // The abandon's WAL marker is durable but its settings-side
                // finalize may not have landed before the crash. Complete it
                // now (or leave the WAL for the next startup if settings
                // cannot be persisted); the journal is never executed.
                await TryFinalizeAbandonedJournalAsync(journal);
                return 0;
            }

            var history = _settingsService.OrganizationHistory.Entries.FirstOrDefault(entry => entry.Id == journal.TransactionId);
            if (journal.IsUndo)
            {
                // Startup only reconciles receipts. Unfinished undo remains in
                // history and never prompts for elevation in the background.
                if (history is null)
                {
                    // The entry was pruned or the settings were reset; nothing
                    // can be reconciled. Discard the stale journal so it cannot
                    // block organization forever.
                    _recoveryStore.Clear();
                    App.Log("[DesktopOrganization] Discarded an undo journal whose history entry no longer exists.");
                    await CompactHistoryAfterJournalResolutionAsync();
                    return 0;
                }

                // Terminal entries — the user abandoned, or the undo finished
                // but a crash preceded the journal clear — must not be revived
                // by the reconcile below, which would flip CanUndo back on.
                // The terminal state may exist only in memory (an undo whose
                // checked save failed earlier in this process), so confirm it
                // durably before the journal may be cleared; re-saving the
                // already-durable case is an idempotent no-op.
                if (!history.CanUndo || history.IsUndone)
                {
                    if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
                        !await _settingsService.OrganizationHistory.SaveCheckedAsync())
                    {
                        App.Log("[DesktopOrganization] Terminal undo state could not be persisted; journal kept.");
                        return journal.Items.Count(item => item.Completed);
                    }

                    _recoveryStore.Clear();
                    await CompactHistoryAfterJournalResolutionAsync();
                    return journal.Items.Count(item => item.Completed);
                }

                ApplyUndoReceipts(history, journal);
                // Checked persistence: the journal clear below must only
                // happen once the reconciled receipts are durable. On
                // failure the journal survives and the next startup retries
                // the reconcile, which is idempotent.
                if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
                    !await _settingsService.OrganizationHistory.SaveCheckedAsync())
                {
                    App.Log("[DesktopOrganization] Undo reconcile could not persist settings; the journal is kept.");
                    return journal.Items.Count(item => item.Completed);
                }

                _recoveryStore.Clear();
                // An interrupted undo keeps its receipts (IsUndoLifecycleActive
                // guards them); only unrelated bloated entries compact here.
                await CompactHistoryAfterJournalResolutionAsync();
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
                _settingsService.OrganizationHistory.Entries.Remove(history);
            RemoveUncommittedWidgets(journal, history);
            // Checked persistence: same rule as everywhere the journal is
            // cleared — no durable state, no journal removal. Settings land
            // first (widget/rule cleanup is the dependent half); the history
            // entry removal is the reconcile guard and drops last.
            if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
                !await _settingsService.OrganizationHistory.SaveCheckedAsync())
            {
                App.Log("[DesktopOrganization] Recovery could not persist settings; the journal is kept.");
                return restored;
            }

            _recoveryStore.Clear();
            await CompactHistoryAfterJournalResolutionAsync();
            return restored;
        }
        finally { OperationGate.Release(); }
    }

    /// <summary>
    /// Compacts history receipts once no recovery journal can reference
    /// them. Call only right after the journal was cleared (or confirmed
    /// absent): the receipts double as durable commit evidence for
    /// <see cref="RecoverPendingAsync"/>, so they must survive every crash
    /// window in which that evidence is still needed. Entries with an
    /// undo still in progress keep their receipts (see
    /// <see cref="OrganizationHistoryPolicy.IsUndoLifecycleActive"/>).
    /// </summary>
    private async Task CompactHistoryAfterJournalResolutionAsync()
    {
        if (_recoveryStore.HasPendingJournal) return;
        if (OrganizationHistoryPolicy.ApplyRetentionPolicy(_settingsService.OrganizationHistory.Entries))
        {
            await _settingsService.OrganizationHistory.SaveCheckedAsync();
            await _settingsService.SaveAsync(notifySubscribers: false);
        }
    }

    /// <summary>
    /// Completes an abandoned transaction whose durable WAL marker
    /// (<see cref="DesktopOrganizationRecoveryJournal.IsAbandoned"/>) is on
    /// disk but whose settings-side finalize may not have landed before the
    /// crash. Idempotent: applies the terminal state, persists it with a
    /// checked save, and only then clears the journal. Returns false when
    /// settings could not be persisted — the WAL stays and the next startup
    /// retries. Shared by startup recovery and the interactive abandon so
    /// the semantics cannot drift apart.
    /// </summary>
    private async Task<bool> TryFinalizeAbandonedJournalAsync(DesktopOrganizationRecoveryJournal journal)
    {
        var history = _settingsService.OrganizationHistory.Entries
            .FirstOrDefault(entry => string.Equals(entry.Id, journal.TransactionId, StringComparison.Ordinal));
        ApplyAbandonedTerminalState(journal, history);

        // Settings first (widget/rule cleanup is the dependent half), then
        // the history entry's terminal state — a crash between them leaves
        // the journal for an idempotent re-finalize either way.
        if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
            !await _settingsService.OrganizationHistory.SaveCheckedAsync())
        {
            App.Log("[DesktopOrganization] Abandoned transaction finalize could not persist settings; the journal is kept.");
            return false;
        }

        _recoveryStore.Clear();
        await CompactHistoryAfterJournalResolutionAsync();
        return true;
    }

    /// <summary>The settings-side half of an abandon.</summary>
    private void ApplyAbandonedTerminalState(
        DesktopOrganizationRecoveryJournal journal,
        OrganizationHistoryEntry? history)
    {
        if (journal.IsUndo)
        {
            if (history is { CanUndo: true, IsUndone: false })
            {
                MarkUndoAbandoned(history);
            }
        }
        else
        {
            RemoveUncommittedWidgets(journal, history);
        }
    }

    /// <summary>
    /// Abandon is an undo lifecycle endpoint: the entry can neither resume
    /// undo nor stay protected from retention compaction.
    /// </summary>
    private static void MarkUndoAbandoned(OrganizationHistoryEntry history)
    {
        history.CanUndo = false;
        history.UndoStarted = false;
    }

    /// <summary>
    /// Stops all further restore attempts for an interrupted undo. The
    /// history entry keeps its receipts but can no longer block new
    /// organization, and a stale undo journal for it is discarded. Returns
    /// the entry so callers can clean up widgets it created.
    /// </summary>
    public async Task<OrganizationHistoryEntry?> AbandonUndoAsync(string historyId)
    {
        await OperationGate.WaitAsync();
        try
        {
            var history = _settingsService.OrganizationHistory.Entries
                .FirstOrDefault(entry => string.Equals(entry.Id, historyId, StringComparison.Ordinal));

            var journal = await _recoveryStore.LoadAsync();
            bool ownsJournal = journal is null ||
                (journal.IsUndo && string.Equals(journal.TransactionId, historyId, StringComparison.Ordinal));
            if (journal is not null && ownsJournal)
            {
                // Durable terminal marker first: a crash after this point can
                // never let startup recovery execute the abandoned undo or
                // revive the entry the user gave up on.
                journal.IsAbandoned = true;
                await _recoveryStore.SaveAsync(journal);
            }

            if (history is { CanUndo: true, IsUndone: false })
            {
                MarkUndoAbandoned(history);
                // Checked persistence: the durable terminal state must land
                // before the journal is discarded below — it is the guard
                // against reviving this undo, so it lands first.
                if (!await _settingsService.OrganizationHistory.SaveCheckedAsync() ||
                    !await _settingsService.SaveCheckedAsync(notifySubscribers: false))
                {
                    throw new IOException("Persisting the abandon state failed; the recovery journal is kept.");
                }
            }

            if (ownsJournal)
            {
                // A forward journal belongs to a different recovery flow and
                // must survive this abandon.
                _recoveryStore.Clear();
                await CompactHistoryAfterJournalResolutionAsync();
            }

            return history;
        }
        finally { OperationGate.Release(); }
    }

    /// <summary>
    /// Discards a pending recovery journal without restoring anything. Files
    /// already moved into created widgets stay there; widgets that ended up
    /// empty are removed together with their rules.
    /// </summary>
    public async Task AbandonPendingRecoveryAsync()
    {
        await OperationGate.WaitAsync();
        try
        {
            var journal = await _recoveryStore.LoadAsync();
            if (journal is null) return;

            // WAL first: a crash after this point must never let startup
            // recovery execute the abandoned transaction, and startup will
            // finish whatever half of the finalize did not land.
            journal.IsAbandoned = true;
            await _recoveryStore.SaveAsync(journal);

            if (!await TryFinalizeAbandonedJournalAsync(journal))
            {
                throw new IOException("Persisting the abandoned recovery failed; the journal is kept.");
            }
        }
        finally { OperationGate.Release(); }
    }

    private void RemoveUncommittedWidgets(
        DesktopOrganizationRecoveryJournal journal,
        OrganizationHistoryEntry? history)
    {
        if (history is { Items.Count: 0 })
        {
            _settingsService.OrganizationHistory.Entries.Remove(history);
        }

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
                // The undo journal's receipts also carry the restored object's
                // identity so a later reconcile can verify them the same way.
                RecordDestinationIdentity(item, result.DestinationPath);
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

    /// <summary>
    /// Explains why one history item cannot be restored, so the abandon
    /// dialog can name the file and the cause instead of a bare count.
    /// </summary>
    internal static string GetUndoBlockReasonKey(OrganizationHistoryItem item)
    {
        if (!EntryExists(item.DestinationPath))
        {
            return "DesktopOrganization.Public.StuckReason.Missing";
        }

        return MatchesSnapshot(item.DestinationPath, new DesktopOrganizationRecoveryItem
        {
            Size = item.Size,
            LastWriteTimeUtc = item.LastWriteTimeUtc,
            // Without the recorded receipt every item reads as Changed.
            DestinationIdentity = item.DestinationIdentity
        })
            ? "DesktopOrganization.Public.StuckReason.Busy"
            : "DesktopOrganization.Public.StuckReason.Changed";
    }

    private static bool MatchesSnapshot(string path, DesktopOrganizationRecoveryItem item)
    {
        // Identity-only authority: recovery may only act while the object at
        // the path still carries the exact identity recorded when the item
        // physically completed its move. Items without a recorded identity
        // (legacy journals written before this field existed, captures that
        // failed, or the rare crash between the physical move and the
        // receipt write) have NO automatic restore authority — the item
        // stays where it is and the journal keeps the record for the user.
        if (item.DestinationIdentity is not { } identity)
        {
            return false;
        }

        return FileService.TryCaptureSourceIdentity(path) is { } current &&
            current.FileId is { } currentId &&
            currentId == new FileService.FileId128(identity.FileIdHigh, identity.FileIdLow) &&
            current.VolumeSerialNumber == identity.VolumeSerialNumber &&
            (!item.Size.HasValue || current.Length == item.Size.Value) &&
            (!item.LastWriteTimeUtc.HasValue ||
                current.LastWriteTimeUtc == item.LastWriteTimeUtc.Value);
    }

    private static bool IsEmptyDirectory(string path) => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
}
