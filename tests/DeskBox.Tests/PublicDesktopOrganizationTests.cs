using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PublicDesktopOrganizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "public-desktop-" + Guid.NewGuid().ToString("N"));
    private string Personal => Path.Combine(_root, "personal");
    private string Public => Path.Combine(_root, "public");
    private string Storage => Path.Combine(_root, "storage");
    private DesktopOrganizationRecoveryStore Recovery => new(Path.Combine(_root, "recovery.json"));
    private DesktopOrganizationScanner Scanner => new(new DesktopOrganizationClassifier(), () => Personal, () => Public);

    [Fact]
    public async Task ScanAndPlan_ExposePublicSourceButRequireOptIn_AndAutoRemainsPersonal()
    {
        Write(Personal, "personal.txt");
        string shared = Write(Public, "shared.txt");
        Write(Public, "pending.crdownload");
        var scan = await Scanner.ScanAsync();
        Assert.Equal(3, scan.Items.Count);
        Assert.Equal(DesktopOrganizationSourceScope.Public, scan.Items.Single(item => item.SourcePath == shared).SourceScope);
        var planner = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver());
        var personal = planner.CreatePlan(scan, Storage, [], []);
        Assert.Single(personal.Targets.SelectMany(target => target.Items));
        Assert.Equal(2, personal.ExcludedItems.Count(item => item.ExclusionReason == DesktopOrganizationExclusionReason.SourceNotSelected));
        var combined = planner.CreatePlan(scan, Storage, [], [], includePublicDesktop: true);
        Assert.Equal(2, combined.EligibleItemCount);
        Assert.Contains(combined.ExcludedItems, item => item.ExclusionReason == DesktopOrganizationExclusionReason.TemporaryOrDownloading);
        Assert.Equal(DesktopOrganizationExclusionReason.PublicDesktopItem, Scanner.CreateAutoOrganizationSnapshot(shared).ExclusionReason);
        Assert.Equal(3, personal.SourceItems.Count);
    }

    [Fact]
    public async Task Scanner_StillFindsPublicItemsWhenPersonalDesktopIsMissing()
    {
        Write(Public, "shared.txt");
        var scan = await Scanner.ScanAsync();
        Assert.Single(scan.Items);
        Assert.False(scan.PublicDesktopUnavailable);
    }

    [Fact]
    public async Task Transaction_PublicPartialCancellationCommitsPersonalAndActualNames_RetryUsesOneHistory()
    {
        Write(Personal, "same.txt", "personal");
        Write(Public, "same.txt", "public");
        Write(Public, "remaining.txt", "remaining");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var plan = await PlanAsync();
        var transfer = new FakeTransfer { PublicLimit = 1, RenamePublic = true };
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, transfer);
        var first = await transaction.ExecuteAsync(plan, null, ownerWindowHandle: new IntPtr(1));
        Assert.Equal([false, true], transfer.Batches.Select(batch => batch.Public));
        Assert.Equal(2, transfer.Batches[1].Sources.Count);
        Assert.Equal(2, first.History.Items.Count);
        var completedPublic = Assert.Single(first.History.Items.Where(item => item.SourceScope == DesktopOrganizationSourceScope.Public));
        Assert.Contains("shell-renamed-", Path.GetFileName(completedPublic.DestinationPath));
        Assert.Equal(Path.GetFileName(completedPublic.SourcePath) == "same.txt" ? "public" : "remaining",
            File.ReadAllText(completedPublic.DestinationPath));
        var pending = Assert.Single(first.RetainedItems);
        Assert.Equal(DesktopOrganizationRetentionReason.Canceled, pending.Reason);
        Assert.True(File.Exists(pending.SourcePath));
        Assert.False(Recovery.HasPendingJournal);
        var retry = DesktopOrganizationPlanner.CreateRetryPlan(plan,
            new HashSet<string>([pending.SourcePath], StringComparer.OrdinalIgnoreCase), settings.Settings.Widgets);
        Assert.All(retry.Targets, target => Assert.False(target.CreatesWidget));
        transfer.PublicLimit = int.MaxValue;
        var final = await transaction.ExecuteAsync(retry, null, ownerWindowHandle: new IntPtr(1));
        Assert.Equal(plan.Id, final.History.Id);
        Assert.Equal(3, final.History.Items.Count);
        Assert.Single(settings.Settings.RecentOrganizationHistory);
        Assert.Single(settings.Settings.Widgets);
        Assert.Empty(final.RetainedItems);
        Assert.Equal([pending.SourcePath], transfer.Batches.Last().Sources);
        Assert.Equal(3, final.History.Items.Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Undo_PublicDeniedKeepsWidgetsAndHistory_ThenRestoresOnlyPendingItems()
    {
        string personal = Write(Personal, "personal.txt");
        string shared = Write(Public, "shared.txt");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var transfer = new FakeTransfer();
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, transfer);
        var result = await transaction.ExecuteAsync(await PlanAsync(), null, ownerWindowHandle: new IntPtr(1));
        Write(Public, "shared.txt", "new file with original name");
        transfer.PublicLimit = 0;
        var partial = await Assert.ThrowsAsync<DesktopOrganizationIncompleteUndoException>(() => transaction.UndoAsync(result.History.Id, new IntPtr(1)));
        Assert.Equal(1, partial.RemainingCount);
        Assert.True(File.Exists(personal));
        Assert.False(result.History.IsUndone);
        Assert.True(result.History.CanUndo);
        Assert.Single(settings.Settings.Widgets);
        Assert.Single(result.History.Items.Where(item => item.IsRestored));
        transfer.PublicLimit = int.MaxValue;
        int batchCount = transfer.Batches.Count;
        await transaction.UndoAsync(result.History.Id, new IntPtr(1));
        Assert.Equal(batchCount + 1, transfer.Batches.Count);
        Assert.True(transfer.Batches.Last().Public);
        Assert.True(result.History.IsUndone);
        Assert.False(result.History.CanUndo);
        var restored = result.History.Items.Single(item => item.SourcePath == shared);
        Assert.NotEqual(shared, restored.RestoredPath);
        Assert.Equal("new file with original name", File.ReadAllText(shared));
        Assert.Equal("content", File.ReadAllText(restored.RestoredPath!));
    }

    [Fact]
    public async Task Recovery_StartupRestoresPersonalAndDefersPublic_ExplicitRecoveryContinues()
    {
        string personal = Write(Personal, "personal.txt");
        string shared = Write(Public, "shared.txt");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var journal = new DesktopOrganizationRecoveryJournal { TransactionId = "interrupted" };
        foreach (string source in new[] { personal, shared })
        {
            string dest = Path.Combine(Storage, Path.GetFileName(source));
            Directory.CreateDirectory(Storage);
            File.Move(source, dest);
            journal.Items.Add(new DesktopOrganizationRecoveryItem
            {
                SourcePath = source, DestinationPath = dest, Completed = true,
                SourceScope = source == shared ? DesktopOrganizationSourceScope.Public : DesktopOrganizationSourceScope.Personal,
                Size = new FileInfo(dest).Length, LastWriteTimeUtc = File.GetLastWriteTimeUtc(dest)
            });
        }
        await Recovery.SaveAsync(journal);
        var transfer = new FakeTransfer();
        var suppressions = new DesktopAutoOrganizationSuppressionRegistry();
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, transfer)
        {
            AutoOrganizationSuppressions = suppressions
        };
        Assert.Equal(1, await transaction.RecoverPendingAsync());
        Assert.True(File.Exists(personal));
        Assert.True(suppressions.TryConsume(personal));
        Assert.False(File.Exists(shared));
        Assert.Single(transfer.Batches);
        Assert.False(transfer.Batches[0].Public);
        Assert.Single((await Recovery.LoadAsync())!.Items);
        Assert.Equal(1, await transaction.RecoverPendingAsync(new IntPtr(1)));
        Assert.True(File.Exists(shared));
        Assert.False(Recovery.HasPendingJournal);
    }

    [Fact]
    public async Task Recovery_DoesNotTreatExistingDestinationAsProofOfAMove()
    {
        string source = Write(Personal, "source.txt", "original");
        string unrelated = Write(Storage, "source.txt", "unrelated");
        await Recovery.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "not-moved",
            Items = [new DesktopOrganizationRecoveryItem { SourcePath = source, DestinationPath = unrelated }]
        });
        var transfer = new FakeTransfer();
        var transaction = new DesktopOrganizationTransaction(new SettingsService(Path.Combine(_root, "settings")), new FileService(), Recovery, transfer);
        Assert.Equal(0, await transaction.RecoverPendingAsync());
        Assert.Empty(transfer.Batches);
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task Recovery_DoesNotRollBackACommittedOperationWhenJournalCleanupWasInterrupted()
    {
        Write(Personal, "source.txt");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, new FakeTransfer());
        var result = await transaction.ExecuteAsync(await PlanAsync(), null, ownerWindowHandle: new IntPtr(1));
        var item = Assert.Single(result.History.Items);
        await Recovery.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = result.History.Id,
            CreatedWidgetIds = result.CreatedWidgets.Select(widget => widget.Id).ToList(),
            Items = [new DesktopOrganizationRecoveryItem { SourcePath = item.SourcePath, DestinationPath = item.DestinationPath, Completed = true }]
        });
        Assert.Equal(0, await transaction.RecoverPendingAsync());
        Assert.True(File.Exists(item.DestinationPath));
        Assert.False(File.Exists(item.SourcePath));
        Assert.Single(settings.Settings.Widgets);
        Assert.False(Recovery.HasPendingJournal);
    }

    [Fact]
    public async Task Undo_ChangedDestinationRemainsPendingAndIsNeverMoved()
    {
        Write(Personal, "source.txt");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, new FakeTransfer());
        var result = await transaction.ExecuteAsync(await PlanAsync(), null, ownerWindowHandle: new IntPtr(1));
        var item = Assert.Single(result.History.Items);
        File.AppendAllText(item.DestinationPath, "changed");
        await Assert.ThrowsAsync<DesktopOrganizationIncompleteUndoException>(() => transaction.UndoAsync(result.History.Id));
        Assert.True(File.Exists(item.DestinationPath));
        Assert.False(File.Exists(item.SourcePath));
        Assert.True(result.History.CanUndo);
    }

    private async Task<DesktopOrganizationPlan> PlanAsync() => new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver())
        .CreatePlan(await Scanner.ScanAsync(), Storage, [], [], includePublicDesktop: true);

    [Fact]
    public async Task PartialUndo_PersistsScopeAndReceiptsAcrossReload()
    {
        Write(Personal, "personal.txt");
        Write(Public, "shared.txt");
        string settingsPath = Path.Combine(_root, "settings");
        var settings = new SettingsService(settingsPath);
        var transfer = new FakeTransfer();
        var transaction = new DesktopOrganizationTransaction(settings, new FileService(), Recovery, transfer);
        var result = await transaction.ExecuteAsync(await PlanAsync(), null, ownerWindowHandle: new IntPtr(1));
        transfer.PublicLimit = 0;
        await Assert.ThrowsAsync<DesktopOrganizationIncompleteUndoException>(() => transaction.UndoAsync(result.History.Id, new IntPtr(1)));
        var reloaded = new SettingsService(settingsPath);
        await reloaded.LoadAsync();
        var history = Assert.Single(reloaded.Settings.RecentOrganizationHistory);
        Assert.True(history.UndoStarted);
        Assert.Single(history.Items.Where(item => item.IsRestored));
        Assert.Equal(DesktopOrganizationSourceScope.Public, history.Items.Single(item => !item.IsRestored).SourceScope);
        transfer.PublicLimit = int.MaxValue;
        int batchesBefore = transfer.Batches.Count;
        await new DesktopOrganizationTransaction(reloaded, new FileService(), Recovery, transfer).UndoAsync(history.Id, new IntPtr(1));
        Assert.Equal(batchesBefore + 1, transfer.Batches.Count);
        Assert.True(history.IsUndone);
    }

    private static string Write(string directory, string name, string content = "content")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeTransfer : IDesktopOrganizationTransfer
    {
        public int PublicLimit { get; set; } = int.MaxValue;
        public bool RenamePublic { get; set; }
        public List<(bool Public, List<string> Sources)> Batches { get; } = [];
        public Task<IReadOnlyList<FileService.FileTransferResult>> MoveAsync(
            IReadOnlyList<FileService.FileTransferPlan> plans, bool publicDesktop, IntPtr ownerWindowHandle,
            Action<FileService.FileTransferResult> itemCompleted, CancellationToken cancellationToken)
        {
            Batches.Add((publicDesktop, plans.Select(plan => plan.SourcePath).ToList()));
            var completed = new List<FileService.FileTransferResult>();
            foreach (var plan in plans)
            {
                if (publicDesktop && completed.Count >= PublicLimit)
                    throw new FileService.FileTransferCanceledException(completed, cancellationToken);
                string destination = publicDesktop && RenamePublic
                    ? Path.Combine(Path.GetDirectoryName(plan.DestinationPath)!, "shell-renamed-" + Path.GetFileName(plan.DestinationPath))
                    : plan.DestinationPath;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(plan.SourcePath, destination);
                var result = new FileService.FileTransferResult(plan.SourcePath, destination);
                completed.Add(result);
                itemCompleted(result);
            }
            return Task.FromResult<IReadOnlyList<FileService.FileTransferResult>>(completed);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
