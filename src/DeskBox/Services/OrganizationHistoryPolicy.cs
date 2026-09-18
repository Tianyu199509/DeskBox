using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Bounds how much undo receipt data
/// <see cref="DeskBox.FileSafety.DesktopOrganizationHistoryStore.Entries"/>
/// may retain. The history lives in its own FileSafety-domain file, so a
/// single unbounded multi-thousand-file batch would permanently bloat the
/// store on disk and the object graph rebuilt at every startup. Oversized
/// batches keep their history entry as a summary — real item count, no
/// receipts, never partially undoable.
/// </summary>
/// <remarks>
/// Compaction is only safe once no recovery journal can still reference the
/// receipts: in the desktop organization flow <c>OrganizationHistory.Items</c>
/// doubles as the durable commit evidence that tells
/// <c>RecoverPendingAsync</c> which moves already committed, so receipts are
/// dropped strictly after the journal is cleared, never before the commit
/// save.
/// </remarks>
public static class OrganizationHistoryPolicy
{
    /// <summary>
    /// Maximum receipts a single entry may retain while staying undoable.
    /// Batches above the limit keep only their summary: undo receipts are
    /// all-or-nothing, so a truncated receipt list must never keep
    /// <c>CanUndo=true</c>.
    /// </summary>
    public const int MaxUndoReceiptItemsPerEntry = 500;

    /// <summary>
    /// Maximum receipts retained across the whole history. When the budget
    /// is exceeded the oldest entries are downgraded to summaries first;
    /// history entries are never deleted for budget reasons.
    /// </summary>
    public const int MaxUndoReceiptItemBudget = 2500;

    /// <summary>
    /// Enforces the entry cap, the per-entry limit, and the global budget.
    /// The list is newest-first (index 0 is the most recent entry),
    /// matching every append site and the load normalizer. Entries whose
    /// undo lifecycle is still in progress, and the entry a pending
    /// recovery journal still references, are left untouched: their
    /// receipts are resume or commit evidence, not historical data.
    /// Returns true when anything changed.
    /// </summary>
    public static bool ApplyRetentionPolicy(
        List<OrganizationHistoryEntry> history,
        string? protectedTransactionId = null)
    {
        if (history.Count == 0)
        {
            return false;
        }

        bool changed = EnforceEntryCap(history, protectedTransactionId);

        foreach (var entry in history)
        {
            if (IsProtected(entry, protectedTransactionId))
            {
                continue;
            }

            if (entry.UndoReceiptsDiscarded)
            {
                // A retry may have re-added this run's receipts after the
                // transaction already lost older ones; the entry stays a
                // non-undoable summary.
                entry.CanUndo = false;
                if (entry.Items.Count > 0)
                {
                    DowngradeToSummary(entry);
                    changed = true;
                }

                continue;
            }

            if (entry.Items.Count == 0)
            {
                continue;
            }

            if (entry.TotalItemCount < entry.Items.Count)
            {
                entry.TotalItemCount = entry.Items.Count;
                changed = true;
            }

            if (entry.Items.Count > MaxUndoReceiptItemsPerEntry)
            {
                DowngradeToSummary(entry);
                changed = true;
            }
        }

        int totalItems = 0;
        foreach (var entry in history)
        {
            if (!IsProtected(entry, protectedTransactionId) && !entry.UndoReceiptsDiscarded)
            {
                totalItems += entry.Items.Count;
            }
        }

        for (int i = history.Count - 1; i >= 0 && totalItems > MaxUndoReceiptItemBudget; i--)
        {
            var entry = history[i];
            if (IsProtected(entry, protectedTransactionId) || entry.UndoReceiptsDiscarded || entry.Items.Count == 0)
            {
                continue;
            }

            totalItems -= entry.Items.Count;
            DowngradeToSummary(entry);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Merges a retry run into its own previous history entry (same
    /// transaction id). A transaction whose receipts were already discarded
    /// can never regain undo: the merge inherits the discarded marker, keeps
    /// this run's receipts only until the post-journal compaction clears
    /// them, and carries the real total forward.
    /// </summary>
    public static void MergeRetryHistory(OrganizationHistoryEntry history, OrganizationHistoryEntry previous)
    {
        history.Items.InsertRange(0, previous.Items);
        if (previous.UndoReceiptsDiscarded)
        {
            history.UndoReceiptsDiscarded = true;
            history.CanUndo = false;
            history.TotalItemCount = previous.TotalItemCount + history.Items.Count;
        }
    }

    /// <summary>
    /// Strips the receipts while keeping the entry visible in history with
    /// its original item count. A summary entry is never undoable.
    /// </summary>
    public static void DowngradeToSummary(OrganizationHistoryEntry entry)
    {
        entry.TotalItemCount = Math.Max(entry.TotalItemCount, entry.Items.Count);
        entry.Items.Clear();
        entry.CanUndo = false;
        entry.UndoStarted = false;
        entry.UndoReceiptsDiscarded = true;
    }

    /// <summary>
    /// True while an undo is in progress or interrupted: the receipts are
    /// resume state, and an interrupted ManagedDrop undo has no recovery
    /// journal at all, so the check must rely on the persisted entry alone.
    /// Abandoned and fully undone entries are lifecycle endpoints —
    /// <see cref="OrganizationHistoryEntry.CanUndo"/> is false there — and
    /// their receipts may compact.
    /// </summary>
    public static bool IsUndoLifecycleActive(OrganizationHistoryEntry entry)
    {
        return entry.CanUndo &&
            !entry.IsUndone &&
            (entry.UndoStarted || entry.Items.Any(item => item.IsRestored));
    }

    private static bool IsProtected(OrganizationHistoryEntry entry, string? protectedTransactionId)
    {
        return IsUndoLifecycleActive(entry) ||
            (protectedTransactionId is not null &&
             string.Equals(entry.Id, protectedTransactionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Trims the entry count to the retention cap, keeping the newest
    /// entries. Transaction and recovery state outranks the cap: active
    /// undos and a journal-referenced entry are never trim victims — if
    /// they alone exceed the cap the list temporarily grows past it rather
    /// than dropping resume or commit evidence.
    /// </summary>
    private static bool EnforceEntryCap(List<OrganizationHistoryEntry> history, string? protectedTransactionId)
    {
        int cap = SettingsService.MaxRecentOrganizationHistoryCount;
        if (history.Count <= cap)
        {
            return false;
        }

        var keep = new HashSet<OrganizationHistoryEntry>();
        foreach (var entry in history)
        {
            if (IsProtected(entry, protectedTransactionId))
            {
                keep.Add(entry);
            }
        }

        foreach (var entry in history
                     .Where(entry => !keep.Contains(entry))
                     .OrderByDescending(entry => entry.TimestampUtc)
                     .Take(Math.Max(0, cap - keep.Count)))
        {
            keep.Add(entry);
        }

        int removed = history.RemoveAll(entry => !keep.Contains(entry));
        return removed > 0;
    }

    /// <summary>
    /// Caps a single freshly appended entry without touching the rest of
    /// the history. Used when the recovery journal state is unknown and the
    /// caller must not run destructive retention over older entries: a new
    /// entry can never be journal-referenced, so capping it is always safe.
    /// </summary>
    public static void CapEntryReceipts(OrganizationHistoryEntry entry)
    {
        if (entry.Items.Count == 0)
        {
            return;
        }

        if (entry.TotalItemCount < entry.Items.Count)
        {
            entry.TotalItemCount = entry.Items.Count;
        }

        if (entry.Items.Count > MaxUndoReceiptItemsPerEntry)
        {
            DowngradeToSummary(entry);
        }
    }
}
