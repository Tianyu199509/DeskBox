using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationRecoveryAbandonTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AbandonUndo_StopsRetriesKeepsReceiptsAndClearsItsJournal()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
        string source = Path.Combine(desktop, "one.pdf");
        File.WriteAllText(source, "one");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);
        DesktopOrganizationPlan plan = await BuildPlanAsync(desktop, storage);
        string historyId = (await transaction.ExecuteAsync(plan)).History.Id;

        // The user moved the organized copy away, so the undo can never finish.
        string destination = settings.OrganizationHistory.Entries
            .Single(entry => entry.Id == historyId).Items.Single().DestinationPath;
        File.Delete(destination);
        await Assert.ThrowsAsync<DesktopOrganizationIncompleteUndoException>(
            () => transaction.UndoAsync(historyId));
        OrganizationHistoryEntry stuck = settings.OrganizationHistory.Entries
            .Single(entry => entry.Id == historyId);
        Assert.True(stuck.UndoStarted);
        Assert.True(stuck.CanUndo);
        Assert.False(stuck.Items.Single().IsRestored);

        await transaction.AbandonUndoAsync(historyId);

        // The UI execute gate is "UndoStarted && CanUndo"; abandon is a
        // lifecycle endpoint, so both flip and unblock organization. The
        // small entry's receipts stay (only oversized entries compact);
        // oversized ones are summarized by the post-abandon retention pass.
        Assert.False(stuck.UndoStarted);
        Assert.False(stuck.CanUndo);
        Assert.False(stuck.Items.Single().IsRestored);
        Assert.False(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task AbandonUndo_KeepsAnUnrelatedForwardJournalIntact()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
        File.WriteAllText(Path.Combine(desktop, "one.pdf"), "one");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "recovery.json"));
        await recovery.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "forward-transaction",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(desktop, "one.pdf"),
                    DestinationPath = Path.Combine(storage, "one.pdf")
                }
            ]
        });

        await new DesktopOrganizationTransaction(settings, new FileService(), recovery)
            .AbandonUndoAsync("some-undo-entry");

        Assert.True(recovery.HasPendingJournal);
    }

    [Fact]
    public async Task Clear_RemovesBackupSoClearedJournalDoesNotResurrect()
    {
        var store = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "recovery.json"));
        var journal = new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "cleared-transaction",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(_root, "a.txt"),
                    DestinationPath = Path.Combine(_root, "b", "a.txt")
                }
            ]
        };
        // Two saves: the second one uses File.Replace and produces the .bak.
        await store.SaveAsync(journal);
        await store.SaveAsync(journal);
        string backupPath = ResilientJsonStore.GetBackupPath(
            Path.Combine(_root, "recovery.json"));
        Assert.True(File.Exists(backupPath));

        store.Clear();

        Assert.False(store.HasPendingJournal);
        Assert.False(File.Exists(backupPath));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_BackupOnlyJournal_IsPendingAndRecoverable()
    {
        // A crash mid-Clear (old delete order) or a quarantined primary can
        // leave only .bak behind. Since LoadAsync treats .bak as a valid
        // recovery source, HasPendingJournal must agree — a backup-only
        // journal is still pending work.
        string journalPath = Path.Combine(_root, "recovery.json");
        var store = new DesktopOrganizationRecoveryStore(journalPath);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "backup-only",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(_root, "a.txt"),
                    DestinationPath = Path.Combine(_root, "b", "a.txt")
                }
            ]
        };
        await store.SaveAsync(journal);
        await store.SaveAsync(journal); // produces .bak
        File.Delete(journalPath);

        Assert.True(store.HasPendingJournal);
        DesktopOrganizationRecoveryJournal? recovered = await store.LoadAsync();
        Assert.Equal("backup-only", recovered!.TransactionId);
    }

    [Fact]
    public async Task Clear_AfterAbandon_CannotResurrectPreAbandonJournal()
    {
        // Regression guard for the delete-order fix: after an abandon's WAL
        // marker is durable, .bak still holds the PRE-abandon journal
        // (IsAbandoned=false). Clear must delete .bak before the primary so
        // no crash window can resurrect a live forward journal and re-run
        // moves the user chose to abandon.
        string journalPath = Path.Combine(_root, "recovery.json");
        var store = new DesktopOrganizationRecoveryStore(journalPath);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "abandoned-transaction",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(_root, "a.txt"),
                    DestinationPath = Path.Combine(_root, "b", "a.txt")
                }
            ]
        };
        await store.SaveAsync(journal);                    // live WAL
        journal.IsAbandoned = true;
        await store.SaveAsync(journal);                    // primary=abandoned, .bak=live

        // Simulate the new Clear order's only crash window: .bak already
        // gone, primary still present — whatever resurrects is the terminal
        // abandoned marker, never the pre-abandon live journal.
        File.Delete(ResilientJsonStore.GetBackupPath(journalPath));
        DesktopOrganizationRecoveryJournal? resurrected = await store.LoadAsync();
        Assert.True(resurrected!.IsAbandoned);

        store.Clear();
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(journalPath)));
        Assert.False(store.HasPendingJournal);
    }

    [Fact]
    public void Clear_DeletesBackupBeforePrimary_SourceOrderPin()
    {
        // Order is the only crash-safety tool here (two deletes cannot be
        // atomic): pin that the .bak delete precedes the primary delete in
        // Clear() so a future "cleanup" cannot silently reintroduce the
        // resurrection window.
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs"));
        int clearIndex = source.IndexOf("public void Clear()", StringComparison.Ordinal);
        Assert.True(clearIndex >= 0);
        string clearBody = source[clearIndex..];
        int backupDelete = clearBody.IndexOf("File.Delete(backupPath)", StringComparison.Ordinal);
        int primaryDelete = clearBody.IndexOf("File.Delete(_journalPath)", StringComparison.Ordinal);
        Assert.True(backupDelete >= 0 && primaryDelete >= 0);
        Assert.True(
            backupDelete < primaryDelete,
            "Clear() must delete .bak before the primary journal.");
    }

    [Fact]
    public async Task LoadAsync_CorruptPrimary_RecoversJournalFromBackup()
    {
        string journalPath = Path.Combine(_root, "recovery.json");
        var store = new DesktopOrganizationRecoveryStore(journalPath);
        var journal = new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "rescued-transaction",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(_root, "a.txt"),
                    DestinationPath = Path.Combine(_root, "b", "a.txt")
                }
            ]
        };
        await store.SaveAsync(journal);
        await store.SaveAsync(journal);
        File.WriteAllText(journalPath, "{ not valid json !!!");

        DesktopOrganizationRecoveryJournal? recovered = await store.LoadAsync();

        Assert.NotNull(recovered);
        Assert.Equal("rescued-transaction", recovered!.TransactionId);
        Assert.Single(Directory.EnumerateFiles(_root, "recovery.json.corrupt-*"));
    }

    [Fact]
    public async Task AbandonPendingRecovery_KeepsMovedFilesAndRemovesOnlyEmptyWidgets()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        string keptTarget = Directory.CreateDirectory(Path.Combine(_root, "kept")).FullName;
        string removedTarget = Directory.CreateDirectory(Path.Combine(_root, "removed")).FullName;
        string destination = Path.Combine(keptTarget, "report.pdf");
        File.WriteAllText(destination, "content");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.Widgets.Add(CreateWidget("Kept", keptTarget, "kept"));
        settings.Settings.Widgets.Add(CreateWidget("Removed", removedTarget, "removed"));
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = "kept",
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = "removed",
            CategoryIds = [DesktopOrganizationCategoryIds.Images]
        });
        var store = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "pending.json"));
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "transaction",
            CreatedWidgetIds = ["kept", "removed"],
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(desktop, "report.pdf"),
                    DestinationPath = destination,
                    TargetWidgetId = "kept",
                    Completed = false
                }
            ]
        });

        await new DesktopOrganizationTransaction(settings, new FileService(), store)
            .AbandonPendingRecoveryAsync();

        Assert.False(store.HasPendingJournal);
        // Abandoning must not undo the interrupted move itself.
        Assert.True(File.Exists(destination));
        Assert.Contains(settings.Settings.Widgets, widget => widget.Id == "kept");
        Assert.DoesNotContain(settings.Settings.Widgets, widget => widget.Id == "removed");
        Assert.Contains(settings.Settings.DesktopOrganizationRules, rule => rule.TargetWidgetId == "kept");
        Assert.DoesNotContain(settings.Settings.DesktopOrganizationRules, rule => rule.TargetWidgetId == "removed");
    }

    [Fact]
    public async Task RecoverPending_DiscardsAnOrphanUndoJournal()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var store = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "orphan.json"));
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "pruned-entry",
            IsUndo = true,
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(_root, "one.pdf"),
                    DestinationPath = Path.Combine(_root, "storage", "one.pdf")
                }
            ]
        });

        int restored = await new DesktopOrganizationTransaction(settings, new FileService(), store)
            .RecoverPendingAsync();

        Assert.Equal(0, restored);
        Assert.False(store.HasPendingJournal);
    }

    [Fact]
    public async Task Execute_WhileRecoveryIsPendingThrowsTheTypedException()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
        File.WriteAllText(Path.Combine(desktop, "one.pdf"), "one");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var recovery = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "recovery.json"));
        await recovery.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "transaction",
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(desktop, "one.pdf"),
                    DestinationPath = Path.Combine(storage, "one.pdf")
                }
            ]
        });
        DesktopOrganizationPlan plan = await BuildPlanAsync(desktop, storage);
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), recovery);

        await Assert.ThrowsAsync<DesktopOrganizationPendingRecoveryException>(
            () => transaction.ExecuteAsync(plan));
    }

    [Fact]
    public void ComputeRequiredSpaceByDrive_ExemptsSameVolumeMoves()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_root))!;
        var sameVolumePlan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetDirectoryPath = Path.Combine(root, "DeskBoxStorage", "Documents"),
                    Items = [Snapshot(Path.Combine(root, "Users", "user", "Desktop", "big.iso"), 100)]
                }
            ]
        };
        var crossVolumePlan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetDirectoryPath = @"D:\DeskBoxStorage\Documents",
                    Items =
                    [
                        Snapshot(Path.Combine(root, "Users", "user", "Desktop", "big.iso"), 100),
                        Snapshot(Path.Combine(root, "Users", "user", "Desktop", "huge.vhdx"), 50)
                    ]
                }
            ]
        };

        // The default configuration puts the desktop and the storage root on
        // the same volume; those moves are renames and must not be charged.
        Assert.Empty(DesktopOrganizationTransaction.ComputeRequiredSpaceByDrive(sameVolumePlan));
        IReadOnlyDictionary<string, long> crossVolume =
            DesktopOrganizationTransaction.ComputeRequiredSpaceByDrive(crossVolumePlan);
        KeyValuePair<string, long> requirement = Assert.Single(crossVolume);
        Assert.Equal("D:\\", requirement.Key, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(150, requirement.Value);
    }

    [Fact]
    public void ValidateAvailableSpace_ThrowsOnlyForCrossVolumeShortfall()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_root))!;
        var sameVolumePlan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetDirectoryPath = Path.Combine(root, "DeskBoxStorage", "Documents"),
                    Items = [Snapshot(Path.Combine(root, "Users", "user", "Desktop", "big.iso"), 100)]
                }
            ]
        };
        var crossVolumePlan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetDirectoryPath = @"D:\DeskBoxStorage\Documents",
                    Items = [Snapshot(Path.Combine(root, "Users", "user", "Desktop", "big.iso"), 100)]
                }
            ]
        };

        DesktopOrganizationTransaction.ValidateAvailableSpace(sameVolumePlan, _ => 0);
        DesktopOrganizationTransaction.ValidateAvailableSpace(crossVolumePlan, _ => long.MaxValue);
        DesktopOrganizationInsufficientSpaceException exception = Assert.Throws<
            DesktopOrganizationInsufficientSpaceException>(
            () => DesktopOrganizationTransaction.ValidateAvailableSpace(crossVolumePlan, _ => 0));
        Assert.Equal("D:\\", exception.DriveName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DesktopOrganizationPlan> BuildPlanAsync(string desktop, string storage)
    {
        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => desktop,
            () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        return new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver())
            .CreatePlan(scan, storage, [], [], _ => "Documents");
    }

    private static DesktopOrganizationFileSnapshot Snapshot(string sourcePath, long size) => new(
        sourcePath,
        Path.GetFileName(sourcePath),
        DesktopOrganizationClassifier.NormalizeExtension(Path.GetExtension(sourcePath)),
        size,
        DateTime.UtcNow,
        DesktopOrganizationCategoryIds.Other,
        null,
        DesktopOrganizationExclusionReason.None);

    private static WidgetConfig CreateWidget(string name, string path, string id) => new()
    {
        Id = id,
        Name = name,
        WidgetKind = WidgetKind.File,
        MappedFolderPath = path,
        FollowsDefaultStoragePath = true,
        ManagedFolderName = Path.GetFileName(path)
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
