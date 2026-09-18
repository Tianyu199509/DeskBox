using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Xunit.Abstractions;

namespace DeskBox.Tests;

public sealed class OrganizationHistoryPolicyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;

    public OrganizationHistoryPolicyTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static OrganizationHistoryEntry CreateEntry(
        int itemCount,
        bool canUndo = true,
        int pathLength = 40,
        DateTime? timestampUtc = null,
        string? errorMessage = null,
        string destinationRoot = "E:\\dst")
    {
        string filler = new('a', Math.Max(0, pathLength - 20));
        return new OrganizationHistoryEntry
        {
            Id = Guid.NewGuid().ToString(),
            TimestampUtc = timestampUtc ?? DateTime.UtcNow,
            WidgetId = "widget",
            WidgetName = "Widget",
            CanUndo = canUndo,
            ErrorMessage = errorMessage,
            Items = Enumerable.Range(0, itemCount).Select(i => new OrganizationHistoryItem
            {
                Name = $"{filler}-{i}.txt",
                SourcePath = $"C:\\src\\{filler}-{i}.txt",
                DestinationPath = $"{destinationRoot}\\{filler}-{i}.txt",
                TargetWidgetId = "widget",
                TargetWidgetName = "Widget"
            }).ToList()
        };
    }

    [Fact]
    public void PerEntryCap_KeepsReceiptsAtLimit()
    {
        foreach (int count in (int[])[499, 500])
        {
            var history = new List<OrganizationHistoryEntry> { CreateEntry(count, canUndo: true) };

            bool changed = OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

            var entry = Assert.Single(history);
            Assert.True(changed); // TotalItemCount backfill counts as a change.
            Assert.True(entry.CanUndo);
            Assert.Equal(count, entry.Items.Count);
            Assert.Equal(count, entry.TotalItemCount);
            Assert.Equal(count, entry.ItemCount);
            Assert.False(entry.UndoReceiptsDiscarded);
        }
    }

    [Fact]
    public void PerEntryCap_DowngradesAboveLimit()
    {
        var entry = CreateEntry(501, canUndo: true);
        var history = new List<OrganizationHistoryEntry> { entry };

        bool changed = OrganizationHistoryPolicy.ApplyRetentionPolicy(history);
        bool changedAgain = OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

        var result = Assert.Single(history);
        Assert.True(changed);
        Assert.False(changedAgain); // Idempotent.
        Assert.False(result.CanUndo);
        Assert.True(result.UndoReceiptsDiscarded);
        Assert.Empty(result.Items);
        Assert.Equal(501, result.TotalItemCount);
        Assert.Equal(501, result.ItemCount);
    }

    [Fact]
    public void GlobalBudget_DowngradesOldestFirst()
    {
        // Newest-first list: index 0 is now, index 5 is five minutes ago.
        // 6 entries x 500 receipts = 3000 > 2500 budget, so the oldest entry
        // loses its receipts and the five newest stay undoable (= 2500).
        var history = Enumerable.Range(0, 6)
            .Select(i => CreateEntry(500, timestampUtc: DateTime.UtcNow.AddMinutes(-i)))
            .ToList();

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

        Assert.Equal(6, history.Count);
        Assert.All(history.Take(5), entry =>
        {
            Assert.True(entry.CanUndo);
            Assert.Equal(500, entry.Items.Count);
        });
        var downgraded = history[^1];
        Assert.Equal(history.Min(entry => entry.TimestampUtc), downgraded.TimestampUtc);
        Assert.False(downgraded.CanUndo);
        Assert.Empty(downgraded.Items);
        Assert.Equal(500, downgraded.TotalItemCount);
        Assert.Equal(2500, history.Sum(entry => entry.Items.Count));
    }

    [Fact]
    public void GlobalBudget_KeepsNormalSizedHistory()
    {
        var history = Enumerable.Range(0, SettingsService.MaxRecentOrganizationHistoryCount)
            .Select(i => CreateEntry(20, timestampUtc: DateTime.UtcNow.AddMinutes(-i)))
            .ToList();

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

        Assert.Equal(SettingsService.MaxRecentOrganizationHistoryCount, history.Count);
        Assert.All(history, entry =>
        {
            Assert.True(entry.CanUndo);
            Assert.Equal(20, entry.Items.Count);
        });
    }

    [Fact]
    public void RetentionPolicy_SkipsEntriesWithInterruptedUndo()
    {
        // An interrupted ManagedDrop undo has no recovery journal at all;
        // the partially restored receipts are its only resume state.
        var entry = CreateEntry(600);
        entry.Items[0].IsRestored = true;

        bool changed = OrganizationHistoryPolicy.ApplyRetentionPolicy([entry]);

        Assert.False(changed);
        Assert.Equal(600, entry.Items.Count);
        Assert.True(entry.CanUndo);

        // A fully undone entry is no longer active; its receipts compact.
        foreach (var item in entry.Items) item.IsRestored = true;
        entry.IsUndone = true;
        changed = OrganizationHistoryPolicy.ApplyRetentionPolicy([entry]);
        Assert.True(changed);
        Assert.Empty(entry.Items);
    }

    [Fact]
    public void MergeRetryHistory_DiscardedTransactionNeverRegainsUndo()
    {
        var previous = new OrganizationHistoryEntry
        {
            TotalItemCount = 600,
            UndoReceiptsDiscarded = true,
            CanUndo = false
        };
        var retry = CreateEntry(10, canUndo: true);

        OrganizationHistoryPolicy.MergeRetryHistory(retry, previous);

        Assert.True(retry.UndoReceiptsDiscarded);
        Assert.False(retry.CanUndo);
        Assert.Equal(610, retry.TotalItemCount);
        // This run's receipts survive the merge (they are the current
        // result page data) and are cleared by the post-journal compaction.
        Assert.Equal(10, retry.Items.Count);

        OrganizationHistoryPolicy.ApplyRetentionPolicy([retry]);

        Assert.Empty(retry.Items);
        Assert.False(retry.CanUndo);
        Assert.Equal(610, retry.TotalItemCount);
        Assert.Equal(610, retry.ItemCount);
    }

    [Fact]
    public void MergeRetryHistory_NormalRetryKeepsUndoableMergedReceipts()
    {
        var previous = CreateEntry(20);
        var retry = CreateEntry(10, canUndo: true);

        OrganizationHistoryPolicy.MergeRetryHistory(retry, previous);

        Assert.False(retry.UndoReceiptsDiscarded);
        Assert.True(retry.CanUndo);
        Assert.Equal(30, retry.Items.Count);
    }

    [Fact]
    public void LegacyEntry_ItemCountFallsBackToItems()
    {
        var legacy = new OrganizationHistoryEntry { Items = CreateItems(5) };
        Assert.Equal(0, legacy.TotalItemCount);
        Assert.Equal(5, legacy.ItemCount);

        var summary = new OrganizationHistoryEntry { TotalItemCount = 2088 };
        Assert.Equal(2088, summary.ItemCount);
    }

    [Fact]
    public void LongPaths_DoNotTriggerDowngradeWithinCountCap()
    {
        // The cap is deliberately count-based: measuring bytes would need a
        // serialization pass on every append. 500 receipts with very long
        // paths stay retained.
        var history = new List<OrganizationHistoryEntry> { CreateEntry(500, pathLength: 1000) };

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

        var entry = Assert.Single(history);
        Assert.True(entry.CanUndo);
        Assert.Equal(500, entry.Items.Count);
        Assert.All(entry.Items, item => Assert.True(item.SourcePath.Length >= 900));
    }

    [Fact]
    public async Task DowngradedEntry_IsNeverUndoable()
    {
        string desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(600);
        settings.OrganizationHistory.Entries.Add(entry);
        OrganizationHistoryPolicy.ApplyRetentionPolicy(settings.OrganizationHistory.Entries);

        var organizer = new OrganizerService(settings, new FileService(), () => desktopRoot);

        Assert.Null(organizer.GetLatestUndoableEntry());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => organizer.UndoAsync(entry.Id));
    }

    [Fact]
    public async Task ExecuteAsync_LargeBatchCompactsAfterJournalClearAndReportsFullResult()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        for (int i = 0; i < 601; i++)
        {
            File.WriteAllText(Path.Combine(desktop, $"file-{i:D4}.pdf"), "x");
        }

        var classifier = new DesktopOrganizationClassifier();
        var scanner = new DesktopOrganizationScanner(classifier, () => desktop, () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync(includeSlowItems: true);
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan,
            storage,
            [],
            [],
            _ => "Documents");

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);

        DesktopOrganizationExecutionResult result = await transaction.ExecuteAsync(plan);

        // The result page keeps the real per-run receipts (D)...
        Assert.Equal(601, result.CompletedItems.Count);
        Assert.All(result.CompletedItems, item => Assert.True(File.Exists(item.DestinationPath)));
        Assert.All(result.CompletedItems, item => Assert.False(File.Exists(item.SourcePath)));
        // ...while the persisted entry was compacted strictly after the
        // journal was cleared (A), so no crash window can lose the commit
        // evidence.
        var entry = Assert.Single(settings.OrganizationHistory.Entries);
        Assert.False(result.History.CanUndo);
        Assert.Empty(entry.Items);
        Assert.Equal(601, entry.TotalItemCount);
        Assert.True(entry.UndoReceiptsDiscarded);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task RecoverPendingAsync_PendingUndoJournalKeepsActiveUndoReceipts()
    {
        // An interrupted desktop organization undo: the startup reconcile
        // clears the journal, but the entry keeps its receipts so the user
        // can finish or abandon the undo (B).
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst")).FullName;
        string restoreRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "restore")).FullName;
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(600, canUndo: true, destinationRoot: destinationRoot);
        entry.UndoStarted = true;
        foreach (var item in entry.Items)
        {
            File.WriteAllText(item.DestinationPath, "moved");
        }

        settings.OrganizationHistory.Entries.Add(entry);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = true,
            TransactionId = entry.Id,
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = entry.Items[0].SourcePath,
                    DestinationPath = entry.Items[0].DestinationPath,
                    RestorePath = Path.Combine(restoreRoot, "restored-0.txt"),
                    Completed = true
                }
            ]
        };
        File.WriteAllText(journal.Items[0].RestorePath!, "restored");
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).RecoverPendingAsync();

        Assert.Equal(600, entry.Items.Count);
        Assert.True(entry.UndoStarted);
        Assert.True(entry.CanUndo);
        Assert.False(entry.IsUndone);
        Assert.True(entry.Items[0].IsRestored);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task LoadAsync_DoesNotDowngradeBloatWithoutRecoveryPass()
    {
        string dataDir = Path.Combine(_tempRoot, "settings");
        Directory.CreateDirectory(dataDir);
        var bloated = new AppSettings();
        bloated.RecentOrganizationHistory.AddRange(Enumerable.Range(0, 30).Select(i =>
            CreateEntry(2000, timestampUtc: DateTime.UtcNow.AddMinutes(-i))));
        string settingsPath = Path.Combine(dataDir, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(bloated, SettingsJsonContext.Default.AppSettings));
        long originalLength = new FileInfo(settingsPath).Length;

        var service = new SettingsService(dataDir);
        await service.LoadAsync();

        // The load never drops receipts AND never trims the entry count:
        // the cap belongs to the retention policy's safe window, which knows
        // about active undos and journal-protected entries.
        Assert.Equal(30, service.OrganizationHistory.Entries.Count);
        Assert.All(service.OrganizationHistory.Entries, entry =>
        {
            Assert.Equal(2000, entry.Items.Count);
            Assert.True(entry.CanUndo);
        });
        // The receipts moved to the FileSafety-domain history file; it, not
        // settings.json, now carries the bulk — nothing was dropped on load.
        string historyPath = Path.Combine(dataDir, "desktop-organization-history.json");
        Assert.True(new FileInfo(historyPath).Length > originalLength / 2);
    }

    [Fact]
    public async Task RecoverPendingAsync_CompactsBloatWhenNoJournalOutstanding()
    {
        string dataDir = Path.Combine(_tempRoot, "settings");
        Directory.CreateDirectory(dataDir);
        var bloated = new AppSettings();
        bloated.RecentOrganizationHistory.AddRange(Enumerable.Range(0, 30).Select(i =>
            CreateEntry(2000, timestampUtc: DateTime.UtcNow.AddMinutes(-i))));
        string settingsPath = Path.Combine(dataDir, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(bloated, SettingsJsonContext.Default.AppSettings));
        long originalLength = new FileInfo(settingsPath).Length;

        var service = new SettingsService(dataDir);
        await service.LoadAsync();
        var transaction = new DesktopOrganizationTransaction(
            service,
            new FileService(),
            new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json")));

        await transaction.RecoverPendingAsync(); // no journal exists -> compaction runs

        long compactedLength = new FileInfo(settingsPath).Length;
        _output.WriteLine($"settings.json: {originalLength} -> {compactedLength} bytes");
        Assert.True(compactedLength < originalLength / 10,
            $"expected a >10x shrink, got {originalLength} -> {compactedLength}");
        Assert.Equal(SettingsService.MaxRecentOrganizationHistoryCount, service.OrganizationHistory.Entries.Count);
        Assert.All(service.OrganizationHistory.Entries, entry =>
        {
            Assert.False(entry.CanUndo);
            Assert.Empty(entry.Items);
            Assert.Equal(2000, entry.TotalItemCount);
        });
    }

    [Fact]
    public async Task LoadAsync_PreservesSmallUndoableHistory()
    {
        string dataDir = Path.Combine(_tempRoot, "settings");
        Directory.CreateDirectory(dataDir);
        var profile = new AppSettings();
        profile.RecentOrganizationHistory.AddRange(Enumerable.Range(0, 3).Select(i =>
            CreateEntry(20, timestampUtc: DateTime.UtcNow.AddMinutes(-i))));
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            JsonSerializer.Serialize(profile, SettingsJsonContext.Default.AppSettings));

        var service = new SettingsService(dataDir);
        await service.LoadAsync();
        var transaction = new DesktopOrganizationTransaction(
            service,
            new FileService(),
            new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json")));
        await transaction.RecoverPendingAsync();

        Assert.Equal(3, service.OrganizationHistory.Entries.Count);
        Assert.All(service.OrganizationHistory.Entries, entry =>
        {
            Assert.True(entry.CanUndo);
            Assert.Equal(20, entry.Items.Count);
        });
    }

    [Fact]
    public async Task OrganizeDropAsync_LargeBatchReturnsFullCompletedItemsAndSummarizesEntry()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        var sourcePaths = new List<string>();
        for (int i = 0; i < 501; i++)
        {
            string path = Path.Combine(sourceDirectory, $"file-{i:D4}.txt");
            File.WriteAllText(path, "x");
            sourcePaths.Add(path);
        }

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var organizer = new OrganizerService(
            settings,
            new FileService(),
            () => Path.Combine(_tempRoot, "desktop"));

        OrganizerOperationResult operation = await organizer.OrganizeDropAsync(
            CreateWidget(targetDirectory), "Widget", sourcePaths, move: true);

        // The caller's per-run result is complete even though the persisted
        // entry was compacted to a summary before returning.
        Assert.Equal(501, operation.CompletedItems.Count);
        Assert.All(operation.CompletedItems, item => Assert.True(File.Exists(item.DestinationPath)));
        var persisted = Assert.Single(settings.OrganizationHistory.Entries);
        Assert.Empty(persisted.Items);
        Assert.False(persisted.CanUndo);
        Assert.Equal(501, persisted.TotalItemCount);
        Assert.True(persisted.UndoReceiptsDiscarded);
    }

    [Fact]
    public async Task MoveItemsBackToDesktopAsync_LargeBatchReturnsFullCompletedItems()
    {
        string widgetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string desktopDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        var sourcePaths = new List<string>();
        for (int i = 0; i < 501; i++)
        {
            string path = Path.Combine(widgetDirectory, $"file-{i:D4}.txt");
            File.WriteAllText(path, "x");
            sourcePaths.Add(path);
        }

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var organizer = new OrganizerService(
            settings,
            new FileService(),
            () => desktopDirectory);

        OrganizerOperationResult operation = await organizer.MoveItemsBackToDesktopAsync(
            CreateWidget(widgetDirectory), "Widget", sourcePaths);

        Assert.Equal(501, operation.CompletedItems.Count);
        Assert.All(operation.CompletedItems, item => Assert.True(File.Exists(item.DestinationPath)));
        var persisted = Assert.Single(settings.OrganizationHistory.Entries);
        Assert.Empty(persisted.Items);
        Assert.Equal(501, persisted.TotalItemCount);
    }

    [Fact]
    public async Task ExecuteAsync_SettingsSaveFailureThrowsAndKeepsRecoveryJournal()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        string sourceOne = Path.Combine(desktop, "one.pdf");
        string sourceTwo = Path.Combine(desktop, "two.pdf");
        File.WriteAllText(sourceOne, "one");
        File.WriteAllText(sourceTwo, "two");

        var classifier = new DesktopOrganizationClassifier();
        var scanner = new DesktopOrganizationScanner(classifier, () => desktop, () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan, storage, [], [], _ => "Documents");

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        await settings.LoadAsync(); // creates settings.json on disk
        string settingsPath = Path.Combine(_tempRoot, "settings", "settings.json");
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);

        File.SetAttributes(settingsPath, FileAttributes.ReadOnly);
        try
        {
            // The commit save is checked: a silent failure would clear the
            // journal with no durable commit anywhere.
            await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync(plan));
            Assert.True(recovery.HasPendingJournal);
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task RecoverPendingAsync_CommittedMovesStayPutWithFullReceiptsOnDisk()
    {
        // Simulates the crash window between the commit save (full receipts
        // persisted) and the journal clear, with a real settings reload as
        // the "restart": recovery must recognize every journal item as
        // committed and must not move files back.
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst")).FullName;
        string dataDir = Path.Combine(_tempRoot, "settings");
        Directory.CreateDirectory(dataDir);
        var profile = new AppSettings();
        var entry = CreateEntry(600, canUndo: true, destinationRoot: destinationRoot);
        profile.RecentOrganizationHistory.Add(entry);
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            JsonSerializer.Serialize(profile, SettingsJsonContext.Default.AppSettings));
        foreach (var item in entry.Items)
        {
            File.WriteAllText(item.DestinationPath, "moved");
        }

        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = false,
            TransactionId = entry.Id,
            Items = entry.Items.Select(item => new DesktopOrganizationRecoveryItem
            {
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                TargetWidgetId = item.TargetWidgetId,
                Completed = true
            }).ToList()
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        // "Restart": load settings from disk, not from the in-memory graph.
        var service = new SettingsService(dataDir);
        await service.LoadAsync();
        var reloaded = Assert.Single(service.OrganizationHistory.Entries);
        Assert.Equal(600, reloaded.Items.Count); // receipts survived the load

        int restored = await new DesktopOrganizationTransaction(
            service, new FileService(), recovery).RecoverPendingAsync();

        Assert.Equal(0, restored);
        Assert.All(reloaded.Items, item => Assert.True(File.Exists(item.DestinationPath)));
        Assert.False(recovery.HasPendingJournal);
        // With the journal resolved the entry may finally compact.
        Assert.Empty(reloaded.Items);
        Assert.Equal(600, reloaded.TotalItemCount);
    }

    [Fact]
    public async Task AbandonUndoAsync_CompactsAbandonedEntryReceipts()
    {
        // Abandon is an undo lifecycle endpoint: after it the entry must not
        // stay protected from retention forever.
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(600);
        entry.UndoStarted = true;
        settings.OrganizationHistory.Entries.Add(entry);
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));

        var abandoned = await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).AbandonUndoAsync(entry.Id);

        Assert.NotNull(abandoned);
        Assert.False(abandoned.CanUndo);
        Assert.False(abandoned.UndoStarted);
        Assert.Empty(abandoned.Items);
        Assert.Equal(600, abandoned.TotalItemCount);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public void ApplyRetentionPolicy_ProtectsJournalReferencedEntry()
    {
        // 7 entries x 500 receipts = 3500, the journal still references the
        // oldest one: it keeps its receipts, the next-oldest compact instead.
        var history = Enumerable.Range(0, 7)
            .Select(i => CreateEntry(500, timestampUtc: DateTime.UtcNow.AddMinutes(-i)))
            .ToList();
        string protectedId = history[^1].Id;

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history, protectedId);

        var protectedEntry = history.Single(entry => entry.Id == protectedId);
        Assert.True(protectedEntry.CanUndo);
        Assert.Equal(500, protectedEntry.Items.Count);
        // Newest five stay full (2500 budget met), the sixth-from-newest
        // compacted, the protected oldest survived the budget.
        Assert.True(history[0].CanUndo);
        Assert.True(history[4].CanUndo);
        Assert.Empty(history[5].Items);
        Assert.Single(history, entry => entry.Id == protectedId);
    }

    [Fact]
    public void ApplyRetentionPolicy_EntryCapNeverTrimsProtectedEntry()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => CreateEntry(10, timestampUtc: DateTime.UtcNow.AddMinutes(-i)))
            .ToList();
        string protectedId = history[^1].Id; // oldest, would normally be trimmed

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history, protectedId);

        Assert.Equal(SettingsService.MaxRecentOrganizationHistoryCount, history.Count);
        Assert.Contains(history, entry => entry.Id == protectedId);
        Assert.True(history.Single(entry => entry.Id == protectedId).CanUndo);
    }

    [Fact]
    public async Task OrganizeDropAsync_EnforcesGlobalBudgetDuringSession()
    {
        // A long-running session must not grow the history without bound
        // between compaction passes: ordinary imports run the full policy.
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "x");

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        settings.OrganizationHistory.Entries.AddRange(Enumerable.Range(0, 6).Select(i =>
            CreateEntry(500, timestampUtc: DateTime.UtcNow.AddMinutes(-i))));
        var organizer = new OrganizerService(
            settings,
            new FileService(),
            () => Path.Combine(_tempRoot, "desktop"));

        OrganizerOperationResult operation = await organizer.OrganizeDropAsync(
            CreateWidget(targetDirectory), "Widget", [sourcePath], move: true);

        Assert.Single(operation.CompletedItems);
        Assert.True(
            settings.OrganizationHistory.Entries.Sum(entry => entry.Items.Count) <=
            OrganizationHistoryPolicy.MaxUndoReceiptItemBudget);
    }

    [Fact]
    public async Task RecoverPendingAsync_AbandonedUndoJournalIsNotRevived()
    {
        // Crash window: AbandonUndo persisted the terminal entry state but
        // died before clearing the journal. Startup must not flip CanUndo
        // back on through the reconcile.
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(600, canUndo: false);
        entry.UndoStarted = false; // abandon already finalized the entry
        settings.OrganizationHistory.Entries.Add(entry);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = true,
            TransactionId = entry.Id,
            Items = []
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).RecoverPendingAsync();

        Assert.False(entry.CanUndo);
        Assert.False(entry.UndoStarted);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task RecoverPendingAsync_AbandonedMarkerNeverRestoresFiles()
    {
        // Crash window in AbandonPendingRecoveryAsync: the durable
        // IsAbandoned marker is on disk, settings still hold the old state.
        // The user chose to keep the moved files; recovery must not undo
        // that choice.
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst")).FullName;
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(3, canUndo: true, destinationRoot: destinationRoot);
        settings.OrganizationHistory.Entries.Add(entry);
        foreach (var item in entry.Items)
        {
            File.WriteAllText(item.DestinationPath, "moved");
        }

        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = false,
            IsAbandoned = true,
            TransactionId = entry.Id,
            Items = entry.Items.Select(item => new DesktopOrganizationRecoveryItem
            {
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                Completed = true
            }).ToList()
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        int restored = await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).RecoverPendingAsync();

        Assert.Equal(0, restored);
        Assert.All(entry.Items, item => Assert.True(File.Exists(item.DestinationPath)));
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public void ApplyRetentionPolicy_EntryCapNeverTrimsActiveUndo()
    {
        // A partial ManagedDrop undo has no journal: its receipts are the
        // only resume state, so the cap must never delete it even when it
        // is the oldest entry and the list is over the cap.
        var history = Enumerable.Range(0, SettingsService.MaxRecentOrganizationHistoryCount + 1)
            .Select(i => CreateEntry(10, timestampUtc: DateTime.UtcNow.AddMinutes(-i)))
            .ToList();
        var active = history[^1];
        active.Items[0].IsRestored = true;

        OrganizationHistoryPolicy.ApplyRetentionPolicy(history);

        Assert.Contains(history, candidate => ReferenceEquals(candidate, active));
        Assert.Equal(SettingsService.MaxRecentOrganizationHistoryCount, history.Count);
        Assert.Equal(10, active.Items.Count);
        Assert.True(active.CanUndo);
    }

    [Fact]
    public async Task OrganizeDropAsync_CorruptJournalDoesNotFailTheImport()
    {
        // A transient journal read race or corrupt file must never fail an
        // ordinary import whose files already moved: retention falls back to
        // capping only the new entry.
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        string dataDir = Path.Combine(_tempRoot, "journaldata");
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "desktop-organization-recovery.json"),
            "{ not valid json");

        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var organizer = new OrganizerService(
            settings,
            new FileService(),
            () => Path.Combine(_tempRoot, "desktop"),
            new DesktopAutoOrganizationSuppressionRegistry(),
            Path.Combine(dataDir, "desktop-organization-recovery.json"));

        OrganizerOperationResult operation = await organizer.OrganizeDropAsync(
            CreateWidget(targetDirectory), "Widget", [sourcePath], move: true);

        Assert.Single(operation.CompletedItems);
        Assert.True(File.Exists(operation.CompletedItems.Single().DestinationPath));
    }

    [Fact]
    public async Task RecoverPendingAsync_AbandonedUndoWALFinalizesTerminalState()
    {
        // Crash window: the IsAbandoned WAL marker is durable but the
        // settings-side finalize (CanUndo/UndoStarted flip) never landed.
        // Startup must complete the abandon, not just skip the journal.
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(600, canUndo: true);
        entry.UndoStarted = true; // pre-abandon state on disk
        settings.OrganizationHistory.Entries.Add(entry);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = true,
            IsAbandoned = true,
            TransactionId = entry.Id,
            Items = []
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).RecoverPendingAsync();

        Assert.False(entry.CanUndo);
        Assert.False(entry.UndoStarted);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task RecoverPendingAsync_AbandonedForwardWALCompletesCleanup()
    {
        // Crash window in a forward abandon: the WAL is durable but
        // RemoveUncommittedWidgets never ran. Startup must finish removing
        // the empty created widget, its rule, and its directory.
        string widgetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "created-widget")).FullName;
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var entry = CreateEntry(3, canUndo: false);
        entry.Targets = []; // the widget is not part of a committed retry
        settings.OrganizationHistory.Entries.Add(entry);
        settings.Settings.Widgets.Add(new WidgetConfig
        {
            Id = "created-widget",
            Name = "Created",
            MappedFolderPath = widgetDirectory,
            ManagedFolderName = Path.GetFileName(widgetDirectory)
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = "created-widget"
        });
        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = false,
            IsAbandoned = true,
            TransactionId = entry.Id,
            CreatedWidgetIds = ["created-widget"],
            Items = []
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);

        await new DesktopOrganizationTransaction(
            settings, new FileService(), recovery).RecoverPendingAsync();

        Assert.DoesNotContain(settings.Settings.Widgets, widget => widget.Id == "created-widget");
        Assert.DoesNotContain(
            settings.Settings.DesktopOrganizationRules,
            rule => rule.TargetWidgetId == "created-widget");
        Assert.False(Directory.Exists(widgetDirectory));
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task RecoverPendingAsync_AbandonedFinalizeRetriesAfterSaveFailure()
    {
        // WAL semantics: when the finalize save fails the journal must
        // survive and a later recovery pass must be able to finish it.
        string dataDir = Path.Combine(_tempRoot, "settings");
        var settings = new SettingsService(dataDir);
        await settings.LoadAsync(); // creates settings.json on disk
        var entry = CreateEntry(600, canUndo: true);
        entry.UndoStarted = true;
        settings.OrganizationHistory.Entries.Add(entry);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            IsUndo = true,
            IsAbandoned = true,
            TransactionId = entry.Id,
            Items = []
        };
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        await recovery.SaveAsync(journal);
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);

        string settingsPath = Path.Combine(dataDir, "settings.json");
        File.SetAttributes(settingsPath, FileAttributes.ReadOnly);
        try
        {
            await transaction.RecoverPendingAsync();
            Assert.True(recovery.HasPendingJournal); // WAL survives the failure
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }

        await transaction.RecoverPendingAsync();

        Assert.False(recovery.HasPendingJournal);
        Assert.False(entry.CanUndo);
        Assert.False(entry.UndoStarted);
    }

    [Fact]
    public async Task UndoSaveFailure_ThenSameProcessRecoverKeepsDiskTerminalStateDurable()
    {
        // Full durability matrix for the undo checked-save boundary: the
        // undo's terminal state exists only in memory after the save fails,
        // and a same-process RecoverPendingAsync must persist it before it
        // may clear the journal — proven by a fresh settings reload.
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        string sourceOne = Path.Combine(desktop, "one.pdf");
        string sourceTwo = Path.Combine(desktop, "two.pdf");
        File.WriteAllText(sourceOne, "one");
        File.WriteAllText(sourceTwo, "two");

        var classifier = new DesktopOrganizationClassifier();
        var scanner = new DesktopOrganizationScanner(classifier, () => desktop, () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan, storage, [], [], _ => "Documents");

        string dataDir = Path.Combine(_tempRoot, "settings");
        var settings = new SettingsService(dataDir);
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_tempRoot, "recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);
        string historyId = (await transaction.ExecuteAsync(plan)).History.Id;

        string settingsPath = Path.Combine(dataDir, "settings.json");
        File.SetAttributes(settingsPath, FileAttributes.ReadOnly);
        OrganizationHistoryEntry entry = settings.OrganizationHistory.Entries
            .Single(candidate => candidate.Id == historyId);
        try
        {
            // All files physically restore; the receipts reconcile in
            // memory; the checked save fails and the journal must survive.
            await Assert.ThrowsAsync<IOException>(() => transaction.UndoAsync(historyId));
            Assert.True(recovery.HasPendingJournal);
            Assert.True(entry.IsUndone);   // in-memory terminal state only
            Assert.False(entry.CanUndo);
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }

        // Same process, same SettingsService instance: recovery sees the
        // in-memory terminal state and must confirm it durably before
        // clearing the journal.
        await transaction.RecoverPendingAsync();

        Assert.False(recovery.HasPendingJournal);

        // Fresh reload from disk: the terminal state is durable, not merely
        // an in-memory leftover.
        var reloadedService = new SettingsService(dataDir);
        await reloadedService.LoadAsync();
        var diskEntry = reloadedService.OrganizationHistory.Entries
            .Single(candidate => candidate.Id == historyId);
        Assert.True(diskEntry.IsUndone);
        Assert.False(diskEntry.CanUndo);
    }

    private static WidgetConfig CreateWidget(string folderPath) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Widget",
        MappedFolderPath = folderPath,
        ManagedFolderName = Path.GetFileName(folderPath)
    };

    /// <summary>
    /// Local-only harness: runs the retention policy over a real
    /// (uncommitted) settings.json copy pointed to by
    /// DESKBOX_HISTORY_MIGRATION_FIXTURE. Skips silently when the variable
    /// is unset, e.g. in CI.
    /// </summary>
    [Fact]
    public async Task RealSettingsCopy_MigrationShrinksBloat()
    {
        string? fixturePath = Environment.GetEnvironmentVariable("DESKBOX_HISTORY_MIGRATION_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath))
        {
            return;
        }

        string json = await File.ReadAllTextAsync(fixturePath);
        long originalLength = new FileInfo(fixturePath).Length;
        var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
        Assert.NotNull(settings);

        OrganizationHistoryPolicy.ApplyRetentionPolicy(settings.RecentOrganizationHistory);

        string migrated = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        _output.WriteLine(
            $"fixture: {originalLength} -> {migrated.Length} bytes, " +
            $"entries={settings.RecentOrganizationHistory.Count}, " +
            $"retainedReceipts={settings.RecentOrganizationHistory.Sum(entry => entry.Items.Count)}");
        Assert.True(migrated.Length < originalLength / 5, $"expected a >5x shrink, got {originalLength} -> {migrated.Length}");
        Assert.All(settings.RecentOrganizationHistory, entry =>
        {
            Assert.True(entry.Items.Count <= OrganizationHistoryPolicy.MaxUndoReceiptItemsPerEntry);
            // Receipts are all-or-nothing: no entry may stay undoable once
            // its receipt list is empty.
            if (entry.Items.Count == 0)
            {
                Assert.False(entry.CanUndo);
            }
        });
    }

    private static List<OrganizationHistoryItem> CreateItems(int count) => Enumerable.Range(0, count)
        .Select(i => new OrganizationHistoryItem
        {
            Name = $"file-{i}.txt",
            SourcePath = $"C:\\src\\file-{i}.txt",
            DestinationPath = $"E:\\dst\\file-{i}.txt"
        })
        .ToList();
}
