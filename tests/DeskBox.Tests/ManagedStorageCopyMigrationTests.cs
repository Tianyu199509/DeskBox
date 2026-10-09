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
    }

    [Fact]
    public async Task CopiesHashesAndCommits_KeepingOriginalsAndAlternateStreams()
    {
        string file = Path.Combine(Source, "report.txt");
        File.WriteAllText(file, "valuable original");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(Source, "empty", "nested"));
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.Equal(1, result.CopiedFileCount);
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
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
        await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
        await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
            new ManagedStorageMigrationOptions(RestartIfCopiesRemoved: true));
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
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
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
        var result = await _manager.UpdateDefaultManagedStorageRootAsync(New);
        Assert.Equal(fileCount, result.CopiedFileCount);
        Assert.Equal(fileCount, Directory.GetFiles(Source, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(fileCount, Directory.GetFiles(Destination, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("payload-1199", File.ReadAllText(Path.Combine(Destination, "11", "01199.txt")));
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
