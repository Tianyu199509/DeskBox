using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStorageCopyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly WidgetManager _manager;
    private string Old => Path.Combine(_root, "old");
    private string New => Path.Combine(_root, "new");
    private string Source => Path.Combine(Old, "A");
    private string Destination => Path.Combine(New, "A");
    private WidgetConfig Widget => Assert.Single(_settings.Settings.Widgets);

    public ManagedStorageCopyMigrationTests()
    {
        Directory.CreateDirectory(Source);
        _settings = new SettingsService(Path.Combine(_root, "settings"));
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = Old;
        _settings.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "A", WidgetKind = WidgetKind.File, ManagedFolderName = "A",
            MappedFolderPath = Source, FollowsDefaultStoragePath = true
        });
        var files = new FileService();
        _manager = new WidgetManager(_settings, files, TestOrganizerServices.Create(_settings, files),
            new ThemeService(_settings), new QuickCaptureService(new QuickCaptureStore(Path.Combine(_root, "notes"))),
            () => Path.Combine(_root, "desktop"), recycleManagedFolderDeletes: false);
        // Existing copy-migration tests live inside one temp volume; opt into the
        // cross-volume code path by default here, while dedicated same-volume tests
        // clear this flag to exercise atomic directory moves.
        _manager.StorageMigration.ForceCrossVolumeForTesting = true;
    }

    private static ManagedStorageMigrationOptions RetainOptions(
        IProgress<ManagedStorageMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool restartIfCopiesRemoved = false) =>
        new(progress, cancellationToken, restartIfCopiesRemoved, RetainOriginals: true);

    [Fact]
    public async Task CopiesHashesAndCommits_KeepingOriginalsAndAlternateStreams()
    {
        string file = Path.Combine(Source, "report.txt");
        File.WriteAllText(file, "valuable original");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(Source, "empty", "nested"));
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.Equal(1, result.CopiedFileCount);
        Assert.True(result.OriginalsRetained);
        Assert.True(result.OldRootStillExists);
        Assert.Equal("valuable original", File.ReadAllText(file));
        Assert.Equal(File.ReadAllText(file), File.ReadAllText(Path.Combine(Destination, "report.txt")));
        Assert.Equal(File.ReadAllText(file + ":Zone.Identifier"),
            File.ReadAllText(Path.Combine(Destination, "report.txt") + ":Zone.Identifier"));
        Assert.True(File.GetAttributes(Path.Combine(Destination, "report.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.True(Directory.Exists(Path.Combine(Destination, "empty", "nested")));
        Assert.Equal(Destination, Widget.MappedFolderPath);
        Assert.Equal(New, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(Destination, Assert.Single(reloaded.Settings.Widgets).MappedFolderPath);
    }

    [Fact]
    public async Task MissingSourceNeverCreatesSuccessfulEmptyMigration()
    {
        Directory.Delete(Source);
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task ExistingDestinationUniqueDataNeverBecomesCleanupCandidate()
    {
        File.WriteAllText(Path.Combine(Source, "source.txt"), "source only");
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "unique.txt"), "the only copy");
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        Assert.Equal("the only copy", File.ReadAllText(Path.Combine(Destination, "unique.txt")));
        Assert.Equal("source only", File.ReadAllText(Path.Combine(Source, "source.txt")));
        AssertOldMapping();
    }

    [Fact]
    public async Task DiskFullPreflightDoesNotSwitchOrDelete()
    {
        File.WriteAllText(Path.Combine(Source, "only.txt"), "original");
        _manager.StorageMigration.AvailableSpace = _ => 0;
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "only.txt")));
    }

    [Fact]
    public async Task LockedSourceFailsWholeBatchWithoutSkipping()
    {
        string file = Path.Combine(Source, "busy.txt");
        File.WriteAllText(file, "original");
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public async Task CancelAfterOneFile_PreservesBothLocations_AndResumesFromJournal()
    {
        File.WriteAllText(Path.Combine(Source, "one.txt"), "one");
        File.WriteAllText(Path.Combine(Source, "two.txt"), "two");
        using var cancellation = new CancellationTokenSource();
        var options = new ManagedStorageMigrationOptions(new InlineProgress<ManagedStorageMigrationProgress>(p =>
        {
            if (p.Phase == ManagedStorageMigrationPhase.Copying && p.CompletedItems == 1)
                cancellation.Cancel();
        }), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New, options));
        AssertOldMapping();
        Assert.Equal(2, Directory.GetFiles(Source).Length);
        Assert.Single(Directory.GetFiles(Destination));
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.True(result.Resumed);
        Assert.Equal(2, Directory.GetFiles(Source).Length);
        Assert.Equal(2, Directory.GetFiles(Destination).Length);
    }

    [Fact]
    public async Task FailureInSecondWidgetDoesNotSwitchTheFirst()
    {
        File.WriteAllText(Path.Combine(Source, "a.txt"), "first");
        string secondSource = Directory.CreateDirectory(Path.Combine(Old, "B")).FullName;
        string busy = Path.Combine(secondSource, "busy.txt");
        File.WriteAllText(busy, "second");
        _settings.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "B", WidgetKind = WidgetKind.File, ManagedFolderName = "B",
            MappedFolderPath = secondSource, FollowsDefaultStoragePath = true
        });
        FileStream? busyHandle = null;
        try
        {
            var options = new ManagedStorageMigrationOptions(new InlineProgress<ManagedStorageMigrationProgress>(p =>
            {
                if (p.Phase == ManagedStorageMigrationPhase.Copying && p.CompletedItems == 1 && busyHandle is null)
                    busyHandle = File.Open(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }));
            await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New, options));
        }
        finally { busyHandle?.Dispose(); }
        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.All(_settings.Settings.Widgets, w => Assert.StartsWith(Old, w.MappedFolderPath));
        Assert.Equal("first", File.ReadAllText(Path.Combine(Source, "a.txt")));
        Assert.Equal("second", File.ReadAllText(busy));
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.True(result.Resumed);
        Assert.Equal(2, result.AffectedWidgetCount);
    }

    [Fact]
    public async Task SameLengthTimestampSourceEditIsDetectedByContentHash()
    {
        string file = Path.Combine(Source, "file.txt");
        File.WriteAllText(file, "original");
        bool changed = false;
        _manager.StorageMigration.PhaseObserver = phase =>
        {
            if (phase != ManagedStorageMigrationPhase.Verifying || changed) return;
            changed = true;
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, "modified");
            File.SetLastWriteTimeUtc(file, stamp);
        };
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.Equal("modified", File.ReadAllText(file));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Destination, "file.txt")));
    }

    [Fact]
    public async Task InterruptedEmptyReservationCanResumeWithoutCleanup()
    {
        var folder = new ManagedStorageMigrationFolder(Widget.Id, Widget.Name, "A",
            Source, Destination, Widget.ManagedFolderName, Widget.MappedFolderPath);
        var journal = new StorageMigrationJournal
        {
            OldRootPath = Old, NewRootPath = New, Folders = [folder]
        };
        journal.TemporaryDirectory = Path.Combine(New, ManagedStorageMigrationService.TemporaryFolderName, journal.Id);
        await _manager.StorageMigration.SaveAsync(journal);
        Directory.CreateDirectory(Destination);
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.True(result.Resumed);
        Assert.True(Directory.Exists(Source));
        Assert.Equal(Destination, Widget.MappedFolderPath);
    }

    [Fact]
    public async Task DestinationCorruptionFailsVerificationAndPreservesSource()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "original");
        bool changed = false;
        _manager.StorageMigration.PhaseObserver = phase =>
        {
            if (phase != ManagedStorageMigrationPhase.Verifying || changed) return;
            changed = true;
            File.WriteAllText(Path.Combine(Destination, "file.txt"), "tampered");
        };
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "file.txt")));
        Assert.Equal("tampered", File.ReadAllText(Path.Combine(Destination, "file.txt")));
    }

    [Fact]
    public async Task DestinationDisappearingBeforeCommitKeepsOldMapping()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "original");
        bool detached = false;
        _manager.StorageMigration.PhaseObserver = phase =>
        {
            if (phase != ManagedStorageMigrationPhase.Verifying || detached) return;
            detached = true;
            Directory.Move(New, Path.Combine(_root, "detached"));
        };
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "file.txt")));
    }

    [Fact]
    public async Task CommitFailureKeepsBothCopiesAndOriginalDurableMapping()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "original");
        Assert.True(await _settings.SaveCheckedAsync());
        string settingsPath = Path.Combine(_root, "settings", "settings.json");
        using (File.Open(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(New));
        await _manager.RecoverManagedStorageCommitAsync();
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "file.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Destination, "file.txt")));
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(Source, Assert.Single(reloaded.Settings.Widgets).MappedFolderPath);
    }

    [Fact]
    public async Task InterruptedMetadataPairRecoversToIntactOriginals()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "original");
        await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        string journalPath = Assert.Single(Directory.GetFiles(_settings.ManagedStorageMigrationDirectory, "*.json"));
        var journal = JsonSerializer.Deserialize(File.ReadAllText(journalPath),
            StorageMigrationJsonContext.Default.StorageMigrationJournal)!;
        journal.State = "Committing";
        await _manager.StorageMigration.SaveAsync(journal);
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = Old;
        Assert.True(await _settings.SaveCheckedAsync());
        await _manager.RecoverManagedStorageCommitAsync();
        AssertOldMapping();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Source, "file.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Destination, "file.txt")));
    }

    [Fact]
    public async Task ReturningToRetainedOriginalsNeverDeletesThem()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "original");
        await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        File.WriteAllText(Path.Combine(Source, "only-in-old.txt"), "unique original");
        await Assert.ThrowsAnyAsync<IOException>(() => _manager.UpdateDefaultManagedStorageRootAsync(Old));
        Assert.Equal("unique original", File.ReadAllText(Path.Combine(Source, "only-in-old.txt")));
        Assert.Equal(Destination, Widget.MappedFolderPath);
    }

    [Fact]
    public void MidFileCancellationNeverDeletesOriginal()
    {
        string source = Path.Combine(Source, "large.bin");
        using (var output = File.Create(source))
        {
            byte[] block = new byte[1024 * 1024];
            Random.Shared.NextBytes(block);
            for (int i = 0; i < 32; i++) output.Write(block);
        }
        string hash = FileService.HashStorageFile(source, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => FileService.CopyStorageFileVerified(
            source, Path.Combine(_root, "partial.bin"), cancellation.Token,
            bytes => { if (bytes >= 1024 * 1024) cancellation.Cancel(); }));
        Assert.Equal(hash, FileService.HashStorageFile(source, CancellationToken.None));
    }

    private void AssertOldMapping()
    {
        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Source, Widget.MappedFolderPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedAllCopiesOffersFreshTaskAndArchivesOldRecord(bool legacyRecord)
    {
        await StopAfterFirstCopyAsync();
        var previous = ReadJournal();
        if (legacyRecord)
        {
            previous.State = "Copying";
            previous.LastStage = null;
            previous.LastOutcome = null;
            previous.TargetVolumeSerialNumber = null;
            await _manager.StorageMigration.SaveAsync(previous);
        }
        RemoveOwnedTestDirectory(New);
        File.WriteAllText(Path.Combine(Source, "added.txt"), "added after stopping");
        var preview = await _manager.PreviewManagedStorageMigrationAsync(New, CancellationToken.None);
        Assert.True(preview.CopiesRemoved);
        Assert.Equal(3, preview.FileCount);
        var error = await Assert.ThrowsAsync<StorageMigrationException>(
            () => _manager.UpdateDefaultManagedStorageRootAsync(New));
        Assert.Equal(StorageMigrationProblem.CopiesRemoved, error.Problem);
        Assert.False(Directory.Exists(New));
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New,
            new ManagedStorageMigrationOptions(RestartIfCopiesRemoved: true, RetainOriginals: true));
        Assert.False(result.Resumed);
        Assert.Equal(3, result.CopiedFileCount);
        Assert.NotEqual(previous.Id, ReadJournal().Id);
        Assert.True(File.Exists(Path.Combine(_settings.ManagedStorageMigrationDirectory, "history", previous.Id + ".json")));
        Assert.Equal("added after stopping", File.ReadAllText(Path.Combine(Destination, "added.txt")));
        Assert.Equal(3, Directory.GetFiles(Source).Length);
    }

    [Fact]
    public async Task RemovedTemporaryDirectoryIsRecreatedWithoutDiscardingCompletedCopies()
    {
        await StopAfterFirstCopyAsync();
        var journal = ReadJournal();
        string completedCopy = Assert.Single(Directory.GetFiles(Destination));
        var identity = FileService.TryCaptureSourceIdentity(completedCopy);
        RemoveOwnedTestDirectory(journal.TemporaryDirectory);
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.True(result.Resumed);
        Assert.Equal(journal.Id, ReadJournal().Id);
        Assert.Equal(identity, FileService.TryCaptureSourceIdentity(completedCopy));
        Assert.Equal(2, Directory.GetFiles(Source).Length);
    }

    [Fact]
    public async Task RemovedOneTargetFolderIsRecreatedWhileOtherOwnedFolderIsKept()
    {
        await StopAfterFirstCopyAsync();
        // Add a second empty widget to the saved plan and live settings before
        // reproducing loss of only the first destination reservation.
        string secondSource = Directory.CreateDirectory(Path.Combine(Old, "B")).FullName;
        string secondTarget = Directory.CreateDirectory(Path.Combine(New, "B")).FullName;
        var widget = new WidgetConfig
        {
            Name = "B", WidgetKind = WidgetKind.File, ManagedFolderName = "B",
            MappedFolderPath = secondSource, FollowsDefaultStoragePath = true
        };
        _settings.Settings.Widgets.Add(widget);
        var journal = ReadJournal();
        journal.Folders.Add(new(widget.Id, "B", "B", secondSource, secondTarget, "B", secondSource));
        journal.DestinationIdentities.Add(FileService.TryCaptureSourceIdentity(secondTarget));
        await _manager.StorageMigration.SaveAsync(journal);
        var secondIdentity = FileService.TryCaptureSourceIdentity(secondTarget);
        RemoveOwnedTestDirectory(Destination);
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.True(result.Resumed);
        Assert.Equal(2, result.CopiedFileCount);
        Assert.Equal(secondIdentity?.FileId, FileService.TryCaptureSourceIdentity(secondTarget)?.FileId);
        Assert.Equal(2, Directory.GetFiles(Source).Length);
    }

    [Fact]
    public async Task ReplacedTargetFolderIsNeverAdoptedOrCleaned()
    {
        await StopAfterFirstCopyAsync();
        RemoveOwnedTestDirectory(Destination);
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "foreign.txt"), "unique foreign data");
        var error = await Assert.ThrowsAsync<StorageMigrationException>(
            () => _manager.UpdateDefaultManagedStorageRootAsync(New));
        Assert.Equal(StorageMigrationProblem.DestinationChanged, error.Problem);
        Assert.Equal("unique foreign data", File.ReadAllText(Path.Combine(Destination, "foreign.txt")));
        AssertOldMapping();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCacheOnUnavailableOrDifferentVolumeIsNotRecreated(bool differentVolume)
    {
        await StopAfterFirstCopyAsync();
        var journal = ReadJournal();
        RemoveOwnedTestDirectory(journal.TemporaryDirectory);
        _manager.StorageMigration.VolumeIdentity = _ =>
            differentVolume ? journal.TargetVolumeSerialNumber!.Value ^ 1UL : null;
        var error = await Assert.ThrowsAsync<StorageMigrationException>(
            () => _manager.UpdateDefaultManagedStorageRootAsync(New));
        Assert.Equal(differentVolume ? StorageMigrationProblem.DestinationChanged : StorageMigrationProblem.VolumeUnavailable,
            error.Problem);
        Assert.False(Directory.Exists(journal.TemporaryDirectory));
        AssertOldMapping();
    }

    [Fact]
    public async Task StopStateAndExistingCopyVerificationProgressSurviveRetry()
    {
        await StopAfterFirstCopyAsync();
        var stopped = ReadJournal();
        Assert.Equal("Stopped", stopped.State);
        Assert.Equal("Copying", stopped.LastStage);
        Assert.Equal(1, stopped.CompletedFiles);
        Assert.True(stopped.BytesProcessed > 0);
        var reports = new List<ManagedStorageMigrationProgress>();
        await _manager.UpdateDefaultManagedStorageRootAsync(New,
            new ManagedStorageMigrationOptions(new InlineProgress<ManagedStorageMigrationProgress>(reports.Add)));
        Assert.Contains(reports, p => p.Phase == ManagedStorageMigrationPhase.CheckingExisting && p.BytesProcessed > 0);
        Assert.Contains(reports, p => p.Phase == ManagedStorageMigrationPhase.Copying && p.BytesProcessed > 0);
    }

    [Fact]
    public async Task ChangedExistingCopyProducesActionableConflictAndPreservesBothFiles()
    {
        await StopAfterFirstCopyAsync();
        string destination = Assert.Single(Directory.GetFiles(Destination));
        File.WriteAllText(destination, "a new unique version");
        var error = await Assert.ThrowsAsync<StorageMigrationException>(
            () => _manager.UpdateDefaultManagedStorageRootAsync(New));
        Assert.Equal(StorageMigrationProblem.DestinationConflict, error.Problem);
        Assert.True(ManagedStorageMigrationPresentation.NeedsNewLocation(error.Problem));
        Assert.Equal("a new unique version", File.ReadAllText(destination));
        AssertOldMapping();
    }

    [Fact]
    public async Task NestedStorageRootClassifiesRootsOverlapInBothDirections()
    {
        foreach (string nested in new[] { Path.Combine(Old, "inside"), _root })
        {
            var error = await Assert.ThrowsAsync<StorageMigrationException>(
                () => _manager.UpdateDefaultManagedStorageRootAsync(nested));
            Assert.Equal(StorageMigrationProblem.RootsOverlap, error.Problem);
            Assert.True(ManagedStorageMigrationPresentation.NeedsNewLocation(error.Problem));
        }
        AssertOldMapping();
    }

    [Fact]
    public void CopyPercentageUsesBytesInsteadOfNumberOfFiles()
    {
        var progress = new ManagedStorageMigrationProgress(ManagedStorageMigrationPhase.Copying,
            228, 824, 251, 468, "file");
        Assert.Equal(251d / 468 * 100, ManagedStorageMigrationPresentation.Percentage(progress));
        Assert.Null(ManagedStorageMigrationPresentation.Percentage(progress with { Phase = ManagedStorageMigrationPhase.Preparing }));
    }

    private async Task StopAfterFirstCopyAsync()
    {
        File.WriteAllText(Path.Combine(Source, "one.txt"), "one");
        File.WriteAllText(Path.Combine(Source, "two.txt"), "two");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _manager.UpdateDefaultManagedStorageRootAsync(New, new ManagedStorageMigrationOptions(
                new InlineProgress<ManagedStorageMigrationProgress>(p =>
                {
                    if (p.Phase == ManagedStorageMigrationPhase.Copying && p.CompletedItems == 1) cancel.Cancel();
                }), cancel.Token)));
    }

    private StorageMigrationJournal ReadJournal()
    {
        string file = Assert.Single(Directory.GetFiles(_settings.ManagedStorageMigrationDirectory, "*.json"));
        return JsonSerializer.Deserialize(File.ReadAllText(file), StorageMigrationJsonContext.Default.StorageMigrationJournal)!;
    }

    private void RemoveOwnedTestDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its fixture.");
        if (!Directory.Exists(full)) return;
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, recursive: true);
    }

    [Fact]
    public async Task ManySmallFilesAreVerifiedWithoutRemovingOriginals()
    {
        const int fileCount = 1200;
        for (int i = 0; i < fileCount; i++)
        {
            string directory = Path.Combine(Source, (i / 100).ToString("D2"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{i:D5}.txt"), $"payload-{i}");
        }
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New, RetainOptions());
        Assert.Equal(fileCount, result.CopiedFileCount);
        Assert.Equal(fileCount, Directory.GetFiles(Source, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(fileCount, Directory.GetFiles(Destination, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("payload-1199", File.ReadAllText(Path.Combine(Destination, "11", "01199.txt")));
    }

    [Fact]
    public async Task CrossVolumeDefault_CopiesVerifiesCommitsAndCleansVerifiedOriginals()
    {
        string fileA = Path.Combine(Source, "doc.txt");
        string subDir = Directory.CreateDirectory(Path.Combine(Source, "nested")).FullName;
        string fileB = Path.Combine(subDir, "readonly.txt");
        File.WriteAllText(fileA, "hello world");
        File.WriteAllText(fileB, "locked-attr content");
        File.SetAttributes(fileB, FileAttributes.ReadOnly);

        bool hostHookSawOldRoot = false;
        _manager.PostManagedStorageMigrationCommitAsync = (oldRoot, newRoot) =>
        {
            hostHookSawOldRoot = Directory.Exists(oldRoot) &&
                                 string.Equals(newRoot, New, StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        };

        var phases = new List<ManagedStorageMigrationPhase>();
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(
            New,
            new ManagedStorageMigrationOptions(new InlineProgress<ManagedStorageMigrationProgress>(p => phases.Add(p.Phase))));

        Assert.Equal(ManagedStorageMigrationMode.CrossVolumeCopy, result.Mode);
        Assert.False(result.OriginalsRetained);
        Assert.Equal(2, result.CopiedFileCount);
        Assert.Equal(2, result.CleanedSourceFileCount);
        Assert.Equal(0, result.RemainingSourceFileCount);
        Assert.False(result.OldRootStillExists);
        Assert.True(hostHookSawOldRoot);
        Assert.Contains(ManagedStorageMigrationPhase.CleaningOrigin, phases);
        Assert.False(Directory.Exists(Old));
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(Destination, "doc.txt")));
        Assert.Equal("locked-attr content", File.ReadAllText(Path.Combine(Destination, "nested", "readonly.txt")));
        Assert.True(File.GetAttributes(Path.Combine(Destination, "nested", "readonly.txt")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task CrossVolumeCleanup_LockedSourceSkipsWithoutRollingBackAndRetriesCleanly()
    {
        string freeFile = Path.Combine(Source, "free.txt");
        string busyFile = Path.Combine(Source, "busy.txt");
        File.WriteAllText(freeFile, "free content");
        File.WriteAllText(busyFile, "busy content");

        FileStream? lockHandle = null;
        try
        {
            // Lock the source file after Verifying finishes and right before post-commit cleanup
            // (simulate an app opening the old file with read-only non-delete sharing after copy).
            _manager.PostManagedStorageMigrationCommitAsync = (_, _) =>
            {
                lockHandle = new FileStream(busyFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            };

            var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
            Assert.Equal(Destination, Widget.MappedFolderPath);
            Assert.Equal(New, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
            Assert.False(result.OriginalsRetained);
            Assert.Equal(1, result.CleanedSourceFileCount);
            Assert.Equal(1, result.RemainingSourceFileCount);
            Assert.True(result.OldRootStillExists);
            Assert.False(File.Exists(freeFile));
            Assert.True(File.Exists(busyFile));
            Assert.Equal("busy content", File.ReadAllText(Path.Combine(Destination, "busy.txt")));
        }
        finally
        {
            lockHandle?.Dispose();
        }

        // After releasing the handle, retrying source cleanup finishes cleanly and prunes Old.
        var retry = await _manager.RetryManagedStorageSourceCleanupAsync(New);
        Assert.Equal(1, retry.CleanedFileCount);
        Assert.Equal(0, retry.RemainingFileCount);
        Assert.False(retry.TargetStillExists);
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public async Task CrossVolumeCleanup_NeverDeletesLateOrModifiedSourceFile()
    {
        string tracked = Path.Combine(Source, "tracked.txt");
        File.WriteAllText(tracked, "original");
        string lateFile = Path.Combine(Source, "late-arrival.txt");

        _manager.PostManagedStorageMigrationCommitAsync = (_, _) =>
        {
            File.WriteAllText(lateFile, "created right after verify");
            DateTime oldWrite = File.GetLastWriteTimeUtc(tracked);
            File.WriteAllText(tracked, "modified"); // same length, restored mtime -> USN changed!
            File.SetLastWriteTimeUtc(tracked, oldWrite);
            return Task.CompletedTask;
        };

        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.Equal(Destination, Widget.MappedFolderPath);
        Assert.Equal(0, result.CleanedSourceFileCount);
        Assert.Equal(1, result.RemainingSourceFileCount);
        Assert.Equal("modified", File.ReadAllText(tracked));
        Assert.Equal("created right after verify", File.ReadAllText(lateFile));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Destination, "tracked.txt")));
    }

    [Fact]
    public async Task CleanupInterruptedCopies_RemovesOnlyJournalVerifiedCopiesAndUnblocksAfterSourceChange()
    {
        await StopAfterFirstCopyAsync();

        // Simulate user modifying source after stopping -> would otherwise deadlock destination with SourceChanged.
        File.WriteAllText(Path.Combine(Source, "one.txt"), "edited-after-stop");
        var stalePreview = await _manager.PreviewManagedStorageMigrationAsync(New, CancellationToken.None);
        Assert.True(stalePreview.HasPendingResumeCopies);
        Assert.True(stalePreview.HasStaleResumeConflict);

        // Also verify no .partial files leaked during stop.
        string workRoot = Path.Combine(New, ManagedStorageMigrationService.TemporaryFolderName);
        if (Directory.Exists(workRoot))
        {
            Assert.Empty(Directory.GetFiles(workRoot, "*.partial", SearchOption.AllDirectories));
        }

        // Plant a foreign file in destination that must NEVER be deleted by cleanup.
        string foreign = Path.Combine(Destination, "user-foreign.txt");
        File.WriteAllText(foreign, "do not touch");

        var cleanup = await _manager.CleanupInterruptedMigrationCopiesAsync(New);
        Assert.Equal(1, cleanup.CleanedFileCount);
        Assert.Equal(0, cleanup.RemainingFileCount);
        Assert.False(File.Exists(Path.Combine(Destination, "one.txt")));
        Assert.Equal("do not touch", File.ReadAllText(foreign));

        // Remove the planted foreign file and confirm destination is completely unblocked for a fresh migration.
        File.Delete(foreign);
        Directory.Delete(Destination, recursive: false);
        var fresh = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.False(fresh.Resumed);
        Assert.Equal("edited-after-stop", File.ReadAllText(Path.Combine(Destination, "one.txt")));
    }

    [Fact]
    public async Task SameVolumeMove_InstantlyMovesDirectoriesAndPrunesEmptyOriginRoot()
    {
        _manager.StorageMigration.ForceCrossVolumeForTesting = false;
        string file = Path.Combine(Source, "instant.txt");
        File.WriteAllText(file, "same volume data");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var preview = await _manager.PreviewManagedStorageMigrationAsync(New, CancellationToken.None);
        Assert.Equal(ManagedStorageMigrationMode.SameVolumeMove, preview.Mode);
        Assert.Equal(0, preview.RequiredSpaceBytes);
        Assert.Equal(1, preview.FileCount);

        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.Equal(ManagedStorageMigrationMode.SameVolumeMove, result.Mode);
        Assert.Equal(1, result.CopiedFileCount);
        Assert.False(result.OriginalsRetained);
        Assert.False(result.OldRootStillExists);
        Assert.False(Directory.Exists(Old));
        Assert.Equal("same volume data", File.ReadAllText(Path.Combine(Destination, "instant.txt")));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3", File.ReadAllText(Path.Combine(Destination, "instant.txt") + ":Zone.Identifier"));
        Assert.True(File.GetAttributes(Path.Combine(Destination, "instant.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(Destination, Widget.MappedFolderPath);
        Assert.Equal(New, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
    }

    [Fact]
    public async Task SameVolumeMove_FailureInSecondWidgetRollsBackFirstAtomically()
    {
        _manager.StorageMigration.ForceCrossVolumeForTesting = false;
        File.WriteAllText(Path.Combine(Source, "a.txt"), "first");
        string secondSource = Directory.CreateDirectory(Path.Combine(Old, "B")).FullName;
        string lockedFile = Path.Combine(secondSource, "locked.txt");
        File.WriteAllText(lockedFile, "second");
        _settings.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "B", WidgetKind = WidgetKind.File, ManagedFolderName = "B",
            MappedFolderPath = secondSource, FollowsDefaultStoragePath = true
        });

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<StorageMigrationException>(
                () => _manager.UpdateDefaultManagedStorageRootAsync(New));
            Assert.Equal(StorageMigrationProblem.InUse, ex.Problem);
            Assert.Equal(secondSource, ex.BlockingPath);
        }

        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Source, "a.txt")));
        Assert.Equal("second", File.ReadAllText(lockedFile));
        Assert.False(Directory.Exists(Destination));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public async Task SameVolumeCrashRecovery_RollsForwardWhenFoldersPhysicallyAtDestination()
    {
        _manager.StorageMigration.ForceCrossVolumeForTesting = false;
        File.WriteAllText(Path.Combine(Source, "moved.txt"), "payload");
        await _manager.UpdateDefaultManagedStorageRootAsync(New);

        // Simulate crash mid-Committing: folders are already at New, settings still say Old.
        string journalPath = Assert.Single(Directory.GetFiles(_settings.ManagedStorageMigrationDirectory, "*.json"));
        var journal = JsonSerializer.Deserialize(File.ReadAllText(journalPath),
            StorageMigrationJsonContext.Default.StorageMigrationJournal)!;
        journal.State = "Committing";
        await _manager.StorageMigration.SaveAsync(journal);

        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = Old;
        Widget.MappedFolderPath = Source;
        Assert.True(await _settings.SaveCheckedAsync());

        await _manager.RecoverManagedStorageCommitAsync();

        Assert.Equal(New, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Destination, Widget.MappedFolderPath);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(Destination, "moved.txt")));
    }

    [Fact]
    public async Task OrphanKeptFolders_DefaultSkippedAndOptInMigrated()
    {
        File.WriteAllText(Path.Combine(Source, "active.txt"), "active");
        string orphanSource = Directory.CreateDirectory(Path.Combine(Old, "ClosedBox")).FullName;
        File.WriteAllText(Path.Combine(orphanSource, "kept.txt"), "orphan payload");
        DeskBox.Platform.ManagedStorageOwnershipFileOps.RegisterManagedFolders(
            Old, ["A", "ClosedBox"], ManagedStorageMigrationService.TemporaryFolderName);

        var preview = await _manager.PreviewManagedStorageMigrationAsync(New, CancellationToken.None);
        Assert.Equal(1, preview.OrphanFolderCount);
        Assert.Equal(1, preview.OrphanFileCount);
        Assert.True(preview.OrphanTotalBytes > 0);

        // Opt-out (default): orphan stays in Old, active moves to New.
        var skipped = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.Equal(0, skipped.MigratedOrphanFolderCount);
        Assert.Equal(1, skipped.SkippedOrphanFolderCount);
        Assert.True(skipped.OldRootStillExists);
        Assert.Equal("orphan payload", File.ReadAllText(Path.Combine(orphanSource, "kept.txt")));
        Assert.False(Directory.Exists(Path.Combine(New, "ClosedBox")));

        // Now move back with IncludeOrphanFolders=true on a fresh target to verify opt-in migration.
        string thirdRoot = Path.Combine(_root, "third");
        string orphanInNew = Directory.CreateDirectory(Path.Combine(New, "ClosedBox2")).FullName;
        File.WriteAllText(Path.Combine(orphanInNew, "kept2.txt"), "orphan2");
        DeskBox.Platform.ManagedStorageOwnershipFileOps.RegisterManagedFolders(
            New, ["A", "ClosedBox2"], ManagedStorageMigrationService.TemporaryFolderName);

        var included = await _manager.UpdateDefaultManagedStorageRootAsync(
            thirdRoot,
            new ManagedStorageMigrationOptions(IncludeOrphanFolders: true));
        Assert.Equal(1, included.MigratedOrphanFolderCount);
        Assert.Equal(0, included.SkippedOrphanFolderCount);
        Assert.False(included.OldRootStillExists);
        Assert.Equal("orphan2", File.ReadAllText(Path.Combine(thirdRoot, "ClosedBox2", "kept2.txt")));
    }

    [Fact]
    public async Task EmptySwitch_ReturnsNullWhenKeptOrphanFolderExists()
    {
        string orphanSource = Directory.CreateDirectory(Path.Combine(Old, "ClosedBox")).FullName;
        File.WriteAllText(Path.Combine(orphanSource, "kept.txt"), "orphan");
        DeskBox.Platform.ManagedStorageOwnershipFileOps.RegisterManagedFolders(
            Old, ["A", "ClosedBox"], ManagedStorageMigrationService.TemporaryFolderName);

        var switched = await _manager.TrySwitchEmptyManagedStorageRootAsync(New);
        Assert.Null(switched);
        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
    }

    [Fact]
    public async Task Preview_SurfacesBlockingPathOnDestinationConflict()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "hello");
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "existing.txt"), "conflict");

        var ex = await Assert.ThrowsAsync<StorageMigrationException>(
            () => _manager.PreviewManagedStorageMigrationAsync(New, CancellationToken.None));
        Assert.Equal(StorageMigrationProblem.DestinationConflict, ex.Problem);
        Assert.Equal(Destination, ex.BlockingPath);
    }

    [Fact]
    public async Task ActiveMigration_BlocksManagedWidgetMutations()
    {
        File.WriteAllText(Path.Combine(Source, "one.txt"), "one");
        File.WriteAllText(Path.Combine(Source, "two.txt"), "two");

        var enteredCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowFinish = new ManualResetEventSlim(false);
        _manager.StorageMigration.PhaseObserver = phase =>
        {
            if (phase == ManagedStorageMigrationPhase.Copying && !enteredCopy.Task.IsCompleted)
            {
                enteredCopy.TrySetResult();
                allowFinish.Wait(TimeSpan.FromSeconds(10));
            }
        };

        Task<ManagedStorageMigrationResult> migrationTask = _manager.UpdateDefaultManagedStorageRootAsync(New);
        await enteredCopy.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(_manager.IsStorageMigrationRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.CreateManagedWidgetAsync("Blocked"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.RenameWidgetAsync(Widget.Id, "Renamed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.RemoveWidgetAsync(Widget.Id));
        }
        finally
        {
            allowFinish.Set();
        }

        await migrationTask;
        Assert.False(_manager.IsStorageMigrationRunning);
    }

    [Fact]
    public async Task EmptyOnboardingSwitch_RebasesRoundTripAndPrunesEmptyOriginRoot()
    {
        var toNew = await _manager.TrySwitchEmptyManagedStorageRootAsync(New);
        Assert.NotNull(toNew);
        Assert.Equal(0, toNew!.CopiedFileCount);
        Assert.Equal(New, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Destination, Widget.MappedFolderPath);
        Assert.True(Directory.Exists(Destination));
        Assert.False(Directory.Exists(Old));

        var backToOld = await _manager.TrySwitchEmptyManagedStorageRootAsync(Old);
        Assert.NotNull(backToOld);
        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Source, Widget.MappedFolderPath);
        Assert.True(Directory.Exists(Source));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public async Task EmptyOnboardingSwitch_ReturnsNullWithoutDeletingWhenWidgetHoldsFiles()
    {
        string file = Path.Combine(Source, "important.txt");
        File.WriteAllText(file, "keep me");

        var switched = await _manager.TrySwitchEmptyManagedStorageRootAsync(New);

        Assert.Null(switched);
        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Source, Widget.MappedFolderPath);
        Assert.Equal("keep me", File.ReadAllText(file));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task RecoveringMissingSettingsJson_ReconcilesDefaultManagedStorageRootFromSurvivingLayout()
    {
        string settingsDir = Path.Combine(_root, "settings");
        var seeded = new SettingsService(settingsDir);
        await seeded.LoadAsync();
        seeded.Settings.FileWidget.DefaultManagedStorageRootPath = Old;
        seeded.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "A",
            WidgetKind = WidgetKind.File,
            ManagedFolderName = "A",
            MappedFolderPath = Source,
            FollowsDefaultStoragePath = true
        });
        Assert.True(await seeded.SaveCheckedAsync());
        Assert.True(File.Exists(Path.Combine(settingsDir, "widget-layout.json")));

        foreach (string candidate in Directory.GetFiles(settingsDir, "settings.json*"))
        {
            File.Delete(candidate);
        }

        var reloaded = new SettingsService(settingsDir);
        await reloaded.LoadAsync();

        Assert.Equal(Old, reloaded.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Source, Assert.Single(reloaded.Settings.Widgets).MappedFolderPath);
    }

    [Fact]
    public async Task RecoverManagedStorageCommitAsync_HealsDriftedDefaultRootWhenNoPendingCommitExists()
    {
        string driftedRoot = Directory.CreateDirectory(Path.Combine(_root, "drifted-root")).FullName;
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = driftedRoot;
        Assert.True(await _settings.SaveCheckedAsync());

        await _manager.RecoverManagedStorageCommitAsync();

        Assert.Equal(Old, _settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        Assert.Equal(Source, Widget.MappedFolderPath);

        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(Old, reloaded.Settings.FileWidget.DefaultManagedStorageRootPath);
    }

    [Fact]
    public void SyncStorageFolderEntries_DoesNotStampOutOfRootManagedWidgetsIntoManifest()
    {
        string driftedRoot = Directory.CreateDirectory(Path.Combine(_root, "drifted-manifest-root")).FullName;
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = driftedRoot;

        Assert.True(_manager.SyncStorageFolderEntries());

        DeskBox.Platform.ManagedStorageOwnershipSnapshot snapshot =
            DeskBox.Platform.ManagedStorageOwnershipFileOps.ReadSnapshot(driftedRoot);
        Assert.DoesNotContain("A", snapshot.ManagedFolders);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "DeskBox.Tests") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup root.");
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (string directory in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, FileAttributes.Directory);
        Directory.Delete(full, recursive: true);
    }
}
