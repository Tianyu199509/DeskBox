using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Feedback 237(2): consecutive multi-folder drags into a box produced
/// watcher batches above the old 24-change threshold, and every such batch
/// paid a full reload (double enumeration, whole-list Items sync, hydration
/// generation restart). These tests pin the migrated contract: a large batch
/// with a KNOWN change set stays incremental (coalesced, applied inside one
/// mutation scope, classified per path instead of via a full-root snapshot),
/// while the state-loss signals - watcher overflow/error/query channels,
/// empty change lists, desktop roots, stale folders - keep reloading.
/// </summary>
public sealed class LargeBatchIncrementalRefreshTests
{
    // ---------------------------------------------------------------
    // Batch-size policy
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(25)]   // first batch size past the old threshold of 24
    [InlineData(64)]   // the watcher's buffered-change cap
    [InlineData(300)]  // storm scale; escalation is RequiresFullReload's job
    public void LargeKnownBatch_DoesNotForceAFullReload(int changeCount)
    {
        string mapped = Path.Combine(Path.GetTempPath(), "DeskBox-large-batch");
        var changes = Enumerable.Range(0, changeCount)
            .Select(index => new FolderChange(
                Path.Combine(mapped, $"file-{index:D3}.txt"),
                WatcherChangeTypes.Created))
            .ToArray();
        var batch = new FolderChangeBatch(mapped, changes, RequiresFullReload: false);

        Assert.False(WidgetViewModel.ShouldUseFullReload(batch, mapped));
    }

    [Fact]
    public void StateLossSignals_StillForceAFullReload()
    {
        string mapped = Path.Combine(Path.GetTempPath(), "DeskBox-reload-signals");
        string other = Path.Combine(Path.GetTempPath(), "DeskBox-other-root");
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        var changes = new[]
        {
            new FolderChange(Path.Combine(mapped, "a.txt"), WatcherChangeTypes.Created)
        };

        // Watcher overflow/error/query channels arrive as RequiresFullReload:
        // ReadDirectoryChangesW only provides a blanket notification after its
        // buffer overflows, so the change list cannot be trusted.
        Assert.True(WidgetViewModel.ShouldUseFullReload(
            new FolderChangeBatch(mapped, changes, RequiresFullReload: true), mapped));
        // The query-watcher channel signals with an empty change list.
        Assert.True(WidgetViewModel.ShouldUseFullReload(
            new FolderChangeBatch(mapped, [], RequiresFullReload: false), mapped));
        // The combined desktop roots enumerate two folders; the incremental
        // path applies single-root changes only.
        Assert.True(WidgetViewModel.ShouldUseFullReload(
            new FolderChangeBatch(userDesktop, changes, false), userDesktop));
        Assert.True(WidgetViewModel.ShouldUseFullReload(
            new FolderChangeBatch(
                publicDesktop,
                new[] { new FolderChange(Path.Combine(mapped, "a.txt"), WatcherChangeTypes.Created) },
                false),
            publicDesktop));
        // A batch from a folder this widget no longer displays.
        Assert.True(WidgetViewModel.ShouldUseFullReload(
            new FolderChangeBatch(other, changes, false), mapped));
    }

    // ---------------------------------------------------------------
    // Batch coalescing
    // ---------------------------------------------------------------

    [Fact]
    public void CoalesceFolderChanges_KeepsLastEventPerPathInBatchOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox-coalesce");
        string a = Path.Combine(root, "a.txt");
        string b = Path.Combine(root, "b.txt");
        string c = Path.Combine(root, "c.txt");
        var changes = new List<FolderChange>
        {
            new(a, WatcherChangeTypes.Created),
            new(a, WatcherChangeTypes.Changed),
            new(b, WatcherChangeTypes.Created),
            new(a, WatcherChangeTypes.Changed), // shell/AV event multiplication
            new(a, WatcherChangeTypes.Deleted),
            new(c, WatcherChangeTypes.Created)
        };

        IReadOnlyList<FolderChange> coalesced =
            WidgetViewModel.CoalesceFolderChanges(changes);

        // Only the newest signal per path carries information: classification
        // re-reads disk state, so the older duplicates are pure waste.
        Assert.Equal(3, coalesced.Count);
        Assert.Equal((b, WatcherChangeTypes.Created), (coalesced[0].FullPath, coalesced[0].ChangeType));
        Assert.Equal((a, WatcherChangeTypes.Deleted), (coalesced[1].FullPath, coalesced[1].ChangeType));
        Assert.Equal((c, WatcherChangeTypes.Created), (coalesced[2].FullPath, coalesced[2].ChangeType));
    }

    [Fact]
    public void CoalesceFolderChanges_RenameSurvivesASamePathFollowUp()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox-coalesce-rename");
        string oldPath = Path.Combine(root, "old.txt");
        string newPath = Path.Combine(root, "new.txt");

        // A real move followed by the copy engine writing the destination:
        // last-wins alone would swallow the rename and leak the stale old
        // path item, so renames are exempt from collapsing.
        var changes = new List<FolderChange>
        {
            new(newPath, WatcherChangeTypes.Renamed, OldFullPath: oldPath),
            new(newPath, WatcherChangeTypes.Changed)
        };

        IReadOnlyList<FolderChange> coalesced =
            WidgetViewModel.CoalesceFolderChanges(changes);

        Assert.Equal(2, coalesced.Count);
        Assert.Equal(WatcherChangeTypes.Renamed, coalesced[0].ChangeType);
        Assert.Equal(oldPath, coalesced[0].OldFullPath);
        Assert.Equal(WatcherChangeTypes.Changed, coalesced[1].ChangeType);

        // Distinct paths and single-event batches pass through untouched.
        var single = new List<FolderChange>
        {
            new(Path.Combine(root, "solo.txt"), WatcherChangeTypes.Created)
        };
        Assert.Same(single, WidgetViewModel.CoalesceFolderChanges(single));
        var distinct = new List<FolderChange>
        {
            new(Path.Combine(root, "x.txt"), WatcherChangeTypes.Created),
            new(Path.Combine(root, "y.txt"), WatcherChangeTypes.Created)
        };
        Assert.Same(distinct, WidgetViewModel.CoalesceFolderChanges(distinct));
    }

    // ---------------------------------------------------------------
    // Per-path classification (replaces the per-batch full-root snapshot)
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClassifyDirectChild_PerPathStat_MatchesSnapshotSemantics()
    {
        string root = Path.Combine(Path.GetTempPath(), $"DeskBox-classify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string present = Path.Combine(root, "present.txt");
            string missing = Path.Combine(root, "missing.txt");
            string hidden = Path.Combine(root, "hidden.txt");
            string ini = Path.Combine(root, "desktop.ini");
            await File.WriteAllTextAsync(present, "ready");
            await File.WriteAllTextAsync(hidden, "hidden");
            await File.WriteAllTextAsync(ini, "[.ShellClassInfo]");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            FolderPathSnapshot snapshot =
                await FileService.CaptureDirectChildSnapshotAsync(root);

            foreach (string path in new[] { present, missing, hidden, ini })
            {
                FolderEntryRefreshStatus snapshotState =
                    FileService.ClassifyDirectChild(snapshot, path);
                FolderEntryRefreshStatus perPathState =
                    FileService.ClassifyDirectChild(path);
                Assert.True(
                    snapshotState == perPathState,
                    $"per-path classification must match the snapshot oracle for '{path}': " +
                    $"snapshot={snapshotState} perPath={perPathState}");
            }

            Assert.Equal(FolderEntryRefreshStatus.Available, FileService.ClassifyDirectChild(present));
            Assert.Equal(FolderEntryRefreshStatus.NotFound, FileService.ClassifyDirectChild(missing));
            Assert.Equal(FolderEntryRefreshStatus.Filtered, FileService.ClassifyDirectChild(hidden));
            Assert.Equal(FolderEntryRefreshStatus.Filtered, FileService.ClassifyDirectChild(ini));
        }
        finally
        {
            File.SetAttributes(
                Path.Combine(root, "hidden.txt"),
                FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClassifyDirectChild_OfflineRoot_StaysUnavailableNotDeleted()
    {
        // The watched root itself is gone: the old incremental path dropped
        // the whole batch (snapshot status Unavailable -> retain items), and
        // the per-path stat must preserve that conservatism - a dead root is
        // never proof that an individual child was deleted.
        string root = Path.Combine(Path.GetTempPath(), $"DeskBox-offline-{Guid.NewGuid():N}");
        string child = Path.Combine(root, "retained.txt");

        Assert.Equal(
            FolderEntryRefreshStatus.Unavailable,
            FileService.ClassifyDirectChild(child));
        Assert.False(WidgetViewModel.ShouldRemoveExistingItem(
            WatcherChangeTypes.Deleted,
            FileService.ClassifyDirectChild(child)));
    }

    // ---------------------------------------------------------------
    // End-to-end decision table: large batch -> final state == disk state
    // ---------------------------------------------------------------

    [Fact]
    public async Task LargeBatchPipeline_FinalStateMatchesDiskWithoutFullReload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"DeskBox-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // Phase 1 - the drag storm: 30 created files plus one folder, with
            // the same shell/AV event multiplication a real copy produces.
            var createdPaths = new List<string>();
            var firstStorm = new List<FolderChange>();
            for (int index = 0; index < 30; index++)
            {
                string path = Path.Combine(root, $"file-{index:D2}.txt");
                await File.WriteAllTextAsync(path, index.ToString());
                createdPaths.Add(path);
                firstStorm.Add(new FolderChange(path, WatcherChangeTypes.Created));
                firstStorm.Add(new FolderChange(path, WatcherChangeTypes.Changed));
                if (index % 3 == 0)
                {
                    firstStorm.Add(new FolderChange(path, WatcherChangeTypes.Changed));
                }
            }

            string folder = Path.Combine(root, "dropped-folder");
            Directory.CreateDirectory(folder);
            firstStorm.Add(new FolderChange(folder, WatcherChangeTypes.Created));
            firstStorm.Add(new FolderChange(folder, WatcherChangeTypes.Changed));

            // A file that exists on disk but never appears in a change list:
            // a full reload would pull it in, the incremental path must not.
            string unreported = Path.Combine(root, "unreported.txt");
            await File.WriteAllTextAsync(unreported, "out of band");

            var firstBatch = new FolderChangeBatch(root, firstStorm, RequiresFullReload: false);
            Assert.False(WidgetViewModel.ShouldUseFullReload(firstBatch, root));

            var model = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await ApplyBatchToModelAsync(firstBatch, model);

            var expected = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(root)
                    .Where(path =>
                        FileService.ClassifyDirectChild(path) == FolderEntryRefreshStatus.Available),
                StringComparer.OrdinalIgnoreCase);
            // The unreported file proves no whole-folder reconciliation ran
            // inside the incremental pass...
            Assert.DoesNotContain(unreported, model);
            expected.Remove(unreported);
            // ...and everything the batch reported matches disk truth.
            Assert.True(expected.SetEquals(model),
                $"phase-1 model must equal disk truth; " +
                $"missing=[{string.Join("; ", expected.Except(model))}] " +
                $"extra=[{string.Join("; ", model.Except(expected))}]");
            Assert.Contains(folder, model);

            // Phase 2 - a follow-up batch deleting five files and renaming
            // one, against the settled (non-empty) model.
            foreach (int index in new[] { 2, 7, 11, 19, 23 })
            {
                string path = createdPaths[index];
                File.Delete(path);
            }

            string renameSource = createdPaths[15];
            string renameTarget = Path.Combine(root, "file-renamed.txt");
            File.Move(renameSource, renameTarget);

            var secondStorm = new List<FolderChange>();
            foreach (int index in new[] { 2, 7, 11, 19, 23 })
            {
                secondStorm.Add(new FolderChange(createdPaths[index], WatcherChangeTypes.Deleted));
            }

            secondStorm.Add(new FolderChange(
                renameTarget,
                WatcherChangeTypes.Renamed,
                OldFullPath: renameSource));

            var secondBatch = new FolderChangeBatch(root, secondStorm, RequiresFullReload: false);
            await ApplyBatchToModelAsync(secondBatch, model);

            var expectedAfter = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(root)
                    .Where(path =>
                        FileService.ClassifyDirectChild(path) == FolderEntryRefreshStatus.Available),
                StringComparer.OrdinalIgnoreCase);
            expectedAfter.Remove(unreported);
            Assert.True(expectedAfter.SetEquals(model),
                $"phase-2 model must equal disk truth; " +
                $"missing=[{string.Join("; ", expectedAfter.Except(model))}] " +
                $"extra=[{string.Join("; ", model.Except(expectedAfter))}]");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Mirrors the production apply loop's decision table - coalesce, then
    /// per-path classification plus <see cref="WidgetViewModel.ShouldRemoveExistingItem"/>
    /// for removals and upsert-on-Available - against a plain path set that
    /// stands in for the Items collection.
    /// </summary>
    private static async Task ApplyBatchToModelAsync(
        FolderChangeBatch batch,
        ISet<string> model)
    {
        foreach (FolderChange change in WidgetViewModel.CoalesceFolderChanges(batch.Changes))
        {
            if (change.ChangeType == WatcherChangeTypes.Renamed &&
                !string.IsNullOrWhiteSpace(change.OldFullPath))
            {
                FolderEntryRefreshStatus oldState =
                    await FileService.ClassifyDirectChildAsync(change.OldFullPath);
                FolderEntryRefreshStatus newState =
                    await FileService.ClassifyDirectChildAsync(change.FullPath);
                if (WidgetViewModel.ShouldRemoveExistingItem(change.ChangeType, oldState))
                {
                    model.Remove(change.OldFullPath);
                }

                if (newState == FolderEntryRefreshStatus.Available)
                {
                    model.Add(change.FullPath);
                }
                else if (newState == FolderEntryRefreshStatus.Filtered)
                {
                    model.Remove(change.FullPath);
                }

                continue;
            }

            FolderEntryRefreshStatus state =
                await FileService.ClassifyDirectChildAsync(change.FullPath);
            if (WidgetViewModel.ShouldRemoveExistingItem(change.ChangeType, state))
            {
                model.Remove(change.FullPath);
            }
            else if (state == FolderEntryRefreshStatus.Available)
            {
                model.Add(change.FullPath);
            }
        }
    }

    // ---------------------------------------------------------------
    // Source contracts for the pipeline shape a live dispatcher forbids
    // ---------------------------------------------------------------

    [Fact]
    public void IncrementalBatch_RunsCoalescedInsideOneMutationScope()
    {
        string watchers = File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/ViewModels/WidgetViewModel.SortingAndWatchers.cs"))
            .Replace("\r\n", "\n");

        // The incremental branch opens one scope for the whole batch...
        int incremental = watchers.IndexOf(
            "using (EnterItemMutationScope())",
            StringComparison.Ordinal);
        int coalesce = watchers.IndexOf(
            "CoalesceFolderChanges(changeBatch.Changes)",
            incremental,
            StringComparison.Ordinal);
        int applyLoop = watchers.IndexOf(
            "await ApplyFolderChangeAsync(change);",
            coalesce,
            StringComparison.Ordinal);
        Assert.True(incremental > 0 && coalesce > incremental && applyLoop > coalesce,
            "the incremental branch must coalesce and apply inside one mutation scope");

        // ...classifies per path instead of re-enumerating the whole root per
        // batch...
        Assert.DoesNotContain(
            "CaptureDirectChildSnapshotAsync",
            watchers,
            StringComparison.Ordinal);
        Assert.Contains(
            "await FileService.ClassifyDirectChildAsync(change.FullPath);",
            watchers,
            StringComparison.Ordinal);
        Assert.Contains(
            "await FileService.ClassifyDirectChildAsync(change.OldFullPath);",
            watchers,
            StringComparison.Ordinal);

        // ...and the count-based escalation is gone from the reload policy.
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/WidgetViewModel.cs"));
        Assert.DoesNotContain("IncrementalRefreshBatchThreshold", viewModel, StringComparison.Ordinal);
        int policy = watchers.IndexOf(
            "internal static bool ShouldUseFullReload(",
            StringComparison.Ordinal);
        int policyEnd = watchers.IndexOf("\n    }\n", policy, StringComparison.Ordinal);
        Assert.True(policy > 0 && policyEnd > policy, "ShouldUseFullReload must exist");
        Assert.DoesNotContain(
            "Changes.Count >",
            watchers[policy..policyEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherBufferCap_RemainsTheOverflowFallback()
    {
        // The watcher-level cap is the surviving count-based escalation: past
        // it, events arrive faster than debatching drains them, and the batch
        // must fall back to the authoritative full reload.
        Assert.Equal(64, FolderWatcherService.MaxBufferedChangesBeforeReload);

        string watcher = File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/FolderWatcherService.cs"))
            .Replace("\r\n", "\n");
        int queueChange = watcher.IndexOf(
            "private void QueueChange(FolderChange change, int generation)",
            StringComparison.Ordinal);
        int cap = watcher.IndexOf(
            "_pendingChanges.Count > MaxBufferedChangesBeforeReload",
            queueChange,
            StringComparison.Ordinal);
        int escalate = watcher.IndexOf(
            "_requiresFullReload = true;",
            cap,
            StringComparison.Ordinal);
        Assert.True(queueChange > 0 && cap > queueChange && escalate > cap,
            "exceeding the buffered-change cap must still escalate to a full reload");
    }
}
