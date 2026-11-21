using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Transactional storage migration engine supporting same-volume atomic directory
/// moves and cross-volume verified copy + commit + identity-verified source cleanup.
/// </summary>
internal sealed class ManagedStorageMigrationService(string journalDirectory)
{
    internal const string TemporaryFolderName = ".deskbox-migration";
    private const FileAttributes PreservedDirectoryAttributeMask =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.NotContentIndexed;

    internal Action<ManagedStorageMigrationPhase>? PhaseObserver { get; set; }
    internal Func<string, long?> AvailableSpace { get; set; } = GetAvailableSpace;
    internal Func<string, ulong?> VolumeIdentity { get; set; } = ReadVolumeIdentity;
    internal Func<string, string, bool>? SameVolumePredicate { get; set; }
    internal bool ForceCrossVolumeForTesting { get; set; }

    internal string JournalDirectory { get; } = journalDirectory;
    private ManagedStorageMigrationDiagnostic? _diagnostic;
    private StorageMigrationJournal? _activeJournal;
    internal ManagedStorageMigrationDiagnostic? Diagnostic => Volatile.Read(ref _diagnostic);

    internal static long ComputeSpaceReserve(long bytes) =>
        Math.Max(64L * 1024 * 1024, bytes / 100);

    internal bool CanMoveSameVolume(string newRoot, IReadOnlyList<ManagedStorageMigrationFolder> folders)
    {
        if (ForceCrossVolumeForTesting || folders.Count == 0)
        {
            return false;
        }

        if (SameVolumePredicate is { } predicate)
        {
            return folders.All(f => predicate(f.SourceFolder, newRoot));
        }

        ulong? targetVolume = VolumeIdentity(newRoot);
        if (targetVolume is null)
        {
            return false;
        }

        string? targetComparableVolume = ResolveExistingComparableVolumeIdentity(newRoot);
        foreach (var folder in folders)
        {
            if (!PathPresent(folder.SourceFolder))
            {
                return false;
            }

            if (VolumeIdentity(folder.SourceFolder) != targetVolume)
            {
                return false;
            }

            string? sourceComparableVolume = ResolveExistingComparableVolumeIdentity(folder.SourceFolder);
            if (!string.IsNullOrWhiteSpace(targetComparableVolume) &&
                !string.IsNullOrWhiteSpace(sourceComparableVolume) &&
                !Same(targetComparableVolume, sourceComparableVolume))
            {
                return false;
            }
        }

        return true;
    }

    internal async Task RecordStoppedAsync(Exception error)
    {
        string outcome = error is OperationCanceledException ? "Stopped" : "Failed";
        int? errorCode = error is OperationCanceledException ? null : error.HResult;
        if (Diagnostic is { } state)
            Volatile.Write(ref _diagnostic, state with
            {
                State = outcome,
                LastErrorCode = errorCode
            });
        if (_activeJournal is not { } journal) return;
        StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(journal.TemporaryDirectory);
        journal.LastOutcome = outcome;
        journal.LastErrorCode = errorCode;
        journal.CompletedFiles = Diagnostic?.CompletedFiles ?? 0;
        journal.BytesProcessed = Diagnostic?.BytesProcessed ?? 0;
        journal.TemporaryBytes = MeasureTemporaryBytes(journal.TemporaryDirectory);
        // A failed metadata commit must retain its recovery authority until
        // startup reconciles the two durable settings documents.
        if (journal.State is not ("Committing" or "Committed"))
        {
            if (journal.State is not ("Stopped" or "Failed")) journal.LastStage = journal.State;
            journal.State = outcome;
        }
        try { await SaveAsync(journal); }
        catch (Exception ex) { App.Log($"[ManagedStorageMigration] Could not persist stop state: {ex.Message}"); }
    }

    internal Task<ManagedStorageMigrationPreview> PreviewAsync(
        string newRoot, IReadOnlyList<ManagedStorageMigrationFolder> folders, CancellationToken token) =>
        PreviewAsync(newRoot, folders, [], token);

    internal async Task<ManagedStorageMigrationPreview> PreviewAsync(
        string newRoot,
        IReadOnlyList<ManagedStorageMigrationFolder> folders,
        IReadOnlyList<ManagedStorageMigrationFolder> orphanFolders,
        CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            RequireSupportedVolume(newRoot);
            // Reject reparse-point destination ancestors at preview time so the
            // user picks a different root before any disk work starts.
            FileService.RequireStorageDestinationAncestry(newRoot);
            var saved = await ReadAsync(GetJournalPath(newRoot), repair: false);
            bool cleared = false;
            bool hasPendingResumeCopies = false;
            bool hasStaleResumeConflict = false;
            if (saved is { State: not "Committed" })
            {
                ValidateJournal(saved);
                RequireDestinationVolume(saved);
                cleared = saved.Folders.Count > 0 && saved.DestinationIdentities.Count > 0 &&
                    saved.State != "Planned" && saved.LastStage != "Planned" &&
                    saved.Folders.All(f => !PathPresent(f.DestinationFolder));
                hasPendingResumeCopies = !cleared &&
                    saved.Mode != nameof(ManagedStorageMigrationMode.SameVolumeMove) &&
                    saved.Folders.Any(f => PathPresent(f.DestinationFolder));
            }

            foreach (var folder in folders)
            {
                RequireSupportedVolume(folder.SourceFolder);
            }

            bool sameVolume = !hasPendingResumeCopies &&
                              !cleared &&
                              CanMoveSameVolume(newRoot, folders);

            // Detect destination conflicts immediately at preview time when no
            // active resume journal owns those destination folders.
            if (!hasPendingResumeCopies && !cleared)
            {
                foreach (var folder in folders)
                {
                    if (PathPresent(folder.DestinationFolder))
                    {
                        throw new StorageMigrationException(
                            StorageMigrationProblem.DestinationConflict,
                            $"The destination already exists and will not be overwritten: '{folder.DestinationFolder}'.",
                            blockingPath: folder.DestinationFolder);
                    }
                }
            }

            int activeFileCount;
            long activeTotalBytes;
            bool allActiveFoldersEmpty;
            if (sameVolume)
            {
                (activeFileCount, activeTotalBytes, allActiveFoldersEmpty) =
                    ScanStatsForSameVolumeMove(folders, token);
            }
            else
            {
                var entries = Scan(folders, token);
                activeFileCount = entries.Count(e => !e.IsDirectory);
                activeTotalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Length);
                allActiveFoldersEmpty = entries.Count == 0;

                if (hasPendingResumeCopies && saved is not null)
                {
                    hasStaleResumeConflict = !MatchesSavedResumeSource(saved, folders, entries);
                }
            }

            int orphanFileCount = 0;
            long orphanTotalBytes = 0;
            if (orphanFolders.Count > 0)
            {
                (orphanFileCount, orphanTotalBytes, _) = ScanStatsForSameVolumeMove(orphanFolders, token);
            }

            bool isEmptySwitch = allActiveFoldersEmpty &&
                                 activeFileCount == 0 &&
                                 orphanFolders.Count == 0 &&
                                 !cleared &&
                                 !hasPendingResumeCopies;
            ManagedStorageMigrationMode mode = isEmptySwitch
                ? ManagedStorageMigrationMode.EmptySwitch
                : sameVolume
                    ? ManagedStorageMigrationMode.SameVolumeMove
                    : ManagedStorageMigrationMode.CrossVolumeCopy;
            long? availableBytes = AvailableSpace(newRoot);
            long requiredBytes = mode == ManagedStorageMigrationMode.CrossVolumeCopy
                ? activeTotalBytes + ComputeSpaceReserve(activeTotalBytes)
                : 0;

            return new ManagedStorageMigrationPreview(
                folders.Count,
                activeFileCount,
                activeTotalBytes,
                cleared,
                saved is null ? null : MeasureTemporaryBytes(saved.TemporaryDirectory),
                Mode: mode,
                DestinationAvailableBytes: availableBytes,
                RequiredSpaceBytes: requiredBytes,
                OrphanFolderCount: orphanFolders.Count,
                OrphanFileCount: orphanFileCount,
                OrphanTotalBytes: orphanTotalBytes,
                HasPendingResumeCopies: hasPendingResumeCopies,
                HasStaleResumeConflict: hasStaleResumeConflict);
        }, token);
    }

    internal async Task<bool> ShouldUseSameVolumeMoveAsync(
        string newRoot, IReadOnlyList<ManagedStorageMigrationFolder> folders)
    {
        if (!CanMoveSameVolume(newRoot, folders))
        {
            return false;
        }

        var saved = await ReadAsync(GetJournalPath(newRoot), repair: false);
        if (saved is { State: not "Committed" } &&
            saved.Mode != nameof(ManagedStorageMigrationMode.SameVolumeMove) &&
            saved.Folders.Any(f => PathPresent(f.DestinationFolder)))
        {
            return false;
        }

        return true;
    }

    internal async Task<(StorageMigrationJournal Journal, bool CreatedNewRoot)> MoveSameVolumeAsync(
        string oldRoot,
        string newRoot,
        IReadOnlyList<ManagedStorageMigrationFolder> folders,
        ManagedStorageMigrationOptions options)
    {
        return await Task.Run(async () =>
        {
            _activeJournal = null;
            Volatile.Write(ref _diagnostic, null);
            CancellationToken token = options.CancellationToken;
            Report(ManagedStorageMigrationPhase.Preparing, 0, folders.Count, 0, 0, null, stepIndex: 1, totalSteps: 1);

            RequireSupportedVolume(newRoot);
            FileService.RequireStorageDestinationAncestry(newRoot);
            FileService.RequireStorageDirectory(Path.GetPathRoot(newRoot)!);

            string journalPath = GetJournalPath(newRoot);
            StorageMigrationJournal? saved = await ReadAsync(journalPath);
            if (saved is { State: not "Committed" })
            {
                if (saved.Folders.All(f => !PathPresent(f.DestinationFolder)))
                {
                    saved.LastOutcome = "Abandoned";
                    await SaveAsync(saved, archive: true);
                }
            }

            foreach (var folder in folders)
            {
                RequireSupportedVolume(folder.SourceFolder);
                FileService.RequireStorageDirectory(folder.SourceFolder);
                if (PathPresent(folder.DestinationFolder))
                {
                    throw new StorageMigrationException(
                        StorageMigrationProblem.DestinationConflict,
                        $"The destination already exists and will not be overwritten: '{folder.DestinationFolder}'.",
                        blockingPath: folder.DestinationFolder);
                }
            }

            (int fileCount, long totalBytes, _) = ScanStatsForSameVolumeMove(folders, token);
            token.ThrowIfCancellationRequested();

            bool createdNewRoot = !PathPresent(newRoot);
            Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(newRoot));
            FileService.RequireStorageDirectory(newRoot);

            var journal = new StorageMigrationJournal
            {
                OldRootPath = oldRoot,
                NewRootPath = newRoot,
                Mode = nameof(ManagedStorageMigrationMode.SameVolumeMove),
                RetainOriginals = false,
                IncludeOrphanFolders = options.IncludeOrphanFolders,
                State = "Moving",
                Folders = folders.ToList(),
                FileCount = fileCount,
                TotalBytes = totalBytes,
                TargetVolumeSerialNumber = VolumeIdentity(newRoot)
                    ?? throw new StorageMigrationException(
                        StorageMigrationProblem.VolumeUnavailable,
                        "The destination volume is unavailable.")
            };
            journal.TemporaryDirectory = Path.Combine(newRoot, TemporaryFolderName, journal.Id);
            await SaveAsync(journal);

            var moved = new List<ManagedStorageMigrationFolder>(folders.Count);
            try
            {
                for (int i = 0; i < folders.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var folder = folders[i];
                    Report(ManagedStorageMigrationPhase.Moving, i, folders.Count, 0, totalBytes,
                        folder.SourceFolder, stepIndex: 1, totalSteps: 1);

                    MoveDirectoryAtomicWithRetry(folder.SourceFolder, folder.DestinationFolder, token);
                    moved.Add(folder);

                    var destIdentity = FileService.TryCaptureSourceIdentity(folder.DestinationFolder);
                    journal.DestinationIdentities.Add(destIdentity);
                    Report(ManagedStorageMigrationPhase.Moving, i + 1, folders.Count, totalBytes, totalBytes,
                        folder.DestinationFolder, stepIndex: 1, totalSteps: 1);
                }

                journal.State = "Verified";
                await SaveAsync(journal);
                return (journal, createdNewRoot);
            }
            catch (Exception ex)
            {
                RollbackMovedFolders(moved, newRoot, createdNewRoot);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                if (ex is StorageMigrationException)
                {
                    throw;
                }

                StorageMigrationProblem problem = ClassifySameVolumeMoveError(ex, moved.Count < folders.Count ? folders[moved.Count].SourceFolder : newRoot);
                string blockingPath = moved.Count < folders.Count ? folders[moved.Count].SourceFolder : newRoot;
                throw new StorageMigrationException(problem, ex.Message, ex, blockingPath: blockingPath);
            }

            void Report(ManagedStorageMigrationPhase phase, int done, int count, long bytes, long total,
                string? path, int stepIndex, int totalSteps)
            {
                Volatile.Write(ref _diagnostic, new(Diagnostic?.TaskId ?? "", phase.ToString(), done, count, bytes, total, false));
                PhaseObserver?.Invoke(phase);
                options.Progress?.Report(new(phase, done, count, bytes, total, path,
                    CheckingCurrentFile: false, BytesPerSecond: 0, StepIndex: stepIndex, TotalSteps: totalSteps));
            }
        }, options.CancellationToken);
    }

    internal static void RollbackSameVolumeMoves(StorageMigrationJournal journal, bool createdNewRoot)
    {
        RollbackMovedFolders(journal.Folders, journal.NewRootPath, createdNewRoot);
    }

    internal async Task<(StorageMigrationJournal Journal, bool Resumed)> CopyAsync(
        string oldRoot, string newRoot, IReadOnlyList<ManagedStorageMigrationFolder> folders,
        ManagedStorageMigrationOptions options)
    {
        // All filesystem scanning/hashing runs off the UI thread. Settings commit
        // remains on the caller's context after this task has completed.
        return await Task.Run(async () =>
        {
            _activeJournal = null;
            Volatile.Write(ref _diagnostic, null);
            CancellationToken token = options.CancellationToken;
            int totalSteps = options.RetainOriginals ? 2 : 3;
            Report(ManagedStorageMigrationPhase.Preparing, 0, 0, 0, 0, null, 0, 0, totalSteps);
            string journalPath = GetJournalPath(newRoot);
            StorageMigrationJournal? saved = await ReadAsync(journalPath);
            if (saved is { State: not "Committed" }) _activeJournal = saved;
            RequireSupportedVolume(newRoot);
            FileService.RequireStorageDirectory(Path.GetPathRoot(newRoot)!);
            foreach (var folder in folders)
            {
                RequireSupportedVolume(folder.SourceFolder);
                FileService.RequireStorageDirectory(folder.SourceFolder);
            }

            if (saved is { State: not "Committed" })
            {
                ValidateJournal(saved);
                RequireDestinationVolume(saved);
                StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(saved.TemporaryDirectory);
                bool copiesRemoved = saved.Folders.Count > 0 &&
                    saved.Folders.All(f => !PathPresent(f.DestinationFolder));
                // A never-started plan may simply not have reserved its folders.
                bool hadCopies = saved.DestinationIdentities.Count > 0 &&
                    (saved.State != "Planned" && saved.LastStage != "Planned");
                if (copiesRemoved && hadCopies)
                {
                    if (!options.RestartIfCopiesRemoved)
                        throw new StorageMigrationException(StorageMigrationProblem.CopiesRemoved,
                            "The previous destination folders were removed. A fresh copy can be started after rescanning the source.");
                    saved.LastOutcome = "Abandoned";
                    await SaveAsync(saved, archive: true);
                    saved = null;
                    _activeJournal = null;
                }
            }
            bool resumed = saved is { State: not "Committed" };
            StorageMigrationJournal journal;
            if (resumed)
            {
                journal = saved!;
                if (!Same(journal.OldRootPath, oldRoot) || !Same(journal.NewRootPath, newRoot) ||
                    !journal.Folders.SequenceEqual(folders))
                    throw new StorageMigrationException(StorageMigrationProblem.SourceChanged,
                        "The unfinished migration no longer matches the widget paths.");
                ValidateJournal(journal);
                RequireDestinationVolume(journal);
                RequireUnchangedSources(journal, token);
                journal.RetainOriginals = options.RetainOriginals;
                journal.IncludeOrphanFolders = options.IncludeOrphanFolders;
            }
            else
            {
                foreach (var folder in folders)
                    if (PathPresent(folder.DestinationFolder))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"The destination already exists and will not be overwritten: '{folder.DestinationFolder}'.",
                            blockingPath: folder.DestinationFolder);
                journal = new StorageMigrationJournal
                {
                    OldRootPath = oldRoot,
                    NewRootPath = newRoot,
                    Mode = nameof(ManagedStorageMigrationMode.CrossVolumeCopy),
                    RetainOriginals = options.RetainOriginals,
                    IncludeOrphanFolders = options.IncludeOrphanFolders,
                    Folders = folders.ToList(),
                    TargetVolumeSerialNumber = VolumeIdentity(newRoot)
                        ?? throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable, "The destination volume is unavailable.")
                };
                journal.TemporaryDirectory = Path.Combine(newRoot, TemporaryFolderName, journal.Id);
                journal.Entries = Scan(folders, token);
                journal.TotalBytes = journal.Entries.Where(e => !e.IsDirectory).Sum(e => e.Length);
                journal.FileCount = journal.Entries.Count(e => !e.IsDirectory);
                CheckSpace(newRoot, journal.TotalBytes);
                await SaveAsync(journal);
            }

            if (journal.State is "Stopped" or "Failed") journal.State = journal.LastStage ?? "Copying";
            journal.LastOutcome = null;
            journal.LastErrorCode = null;
            // Reconcile missing reservations only after the original volume and
            // source snapshot have been checked. No existing directory is cleared.
            {
                FileService.RequireStorageDestinationAncestry(newRoot);
                Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(newRoot));
                FileService.RequireStorageDirectory(newRoot);
                FileService.RequireStorageDestinationAncestry(journal.TemporaryDirectory);
                Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(journal.TemporaryDirectory));
                FileService.RequireStorageDirectory(journal.TemporaryDirectory);
                for (int i = 0; i < folders.Count; i++)
                {
                    var folder = folders[i];
                    bool exists = PathPresent(folder.DestinationFolder);
                    if (i < journal.DestinationIdentities.Count && exists)
                    {
                        RequireDestinationIdentity(journal, i);
                        continue;
                    }
                    // Atomic creation refuses a competing directory, even an empty one.
                    // After a saved plan was interrupted, an empty reservation can
                    // be adopted: nothing in it is overwritten or ever deleted.
                    bool emptyReservation = resumed && exists;
                    if (emptyReservation)
                    {
                        FileService.RequireStorageDirectory(folder.DestinationFolder);
                        if (Directory.EnumerateFileSystemEntries(StorageMigrationNativeMethods.Extended(folder.DestinationFolder)).Any())
                            throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                                $"The interrupted reservation contains untracked content: '{folder.DestinationFolder}'.",
                                blockingPath: folder.DestinationFolder);
                    }
                    else if (!Kernel32NativeMethods.CreateDirectory(StorageMigrationNativeMethods.Extended(folder.DestinationFolder), IntPtr.Zero))
                        throw new IOException($"The destination could not be exclusively created: '{folder.DestinationFolder}'.");
                    var identity = FileService.TryCaptureSourceIdentity(folder.DestinationFolder)
                        ?? throw new IOException("The destination volume cannot provide a stable directory identity.");
                    if (i < journal.DestinationIdentities.Count) journal.DestinationIdentities[i] = identity;
                    else journal.DestinationIdentities.Add(identity);
                    await SaveAsync(journal);
                }
            }

            token.ThrowIfCancellationRequested();
            RequireDestinations(journal);
            foreach (var entry in journal.Entries.Where(e => e.IsDirectory))
            {
                string destination = Destination(journal, entry);
                Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(destination));
                FileService.RequireStorageDirectory(destination);
            }

            var files = journal.Entries.Where(e => !e.IsDirectory).ToList();
            var existing = files.Where(e => PathPresent(Destination(journal, e))).ToList();
            CheckSpace(newRoot, files.Except(existing).Sum(e => e.Length));
            int completed = 0;
            long copied = 0;
            var verifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var verifiedSourceUsns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var rateTracker = new TransferRateTracker();

            if (existing.Count > 0)
            {
                journal.State = "CheckingExisting";
                await SaveAsync(journal);
                long existingBytes = existing.Sum(e => e.Length);
                rateTracker.Reset();
                foreach (var entry in existing)
                {
                    token.ThrowIfCancellationRequested();
                    string source = Source(journal, entry);
                    string destination = Destination(journal, entry);
                    RequireUnchangedFile(entry, source);
                    Report(ManagedStorageMigrationPhase.CheckingExisting, completed, existing.Count, copied, existingBytes,
                        source, rateTracker.BytesPerSecond, 1, totalSteps);
                    long read = 0;
                    long lastProgress = 0;
                    void Checked(long bytes)
                    {
                        read += bytes;
                        rateTracker.RecordBytes(bytes);
                        if (Environment.TickCount64 - lastProgress < 150) return;
                        lastProgress = Environment.TickCount64;
                        Report(ManagedStorageMigrationPhase.CheckingExisting, completed, existing.Count,
                            copied + read / 2, existingBytes, source, rateTracker.BytesPerSecond, 1, totalSteps);
                    }
                    string hash = FileService.HashStorageFile(source, token, Checked);
                    if (StorageMigrationNativeMethods.TryReadFileUsn(source, out long existingUsn))
                    {
                        verifiedSourceUsns[source] = existingUsn;
                        entry.SourceUsn = existingUsn;
                    }
                    if (hash != FileService.HashStorageFile(destination, token, Checked))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"The existing destination differs from its source: '{destination}'.",
                            blockingPath: destination);
                    entry.DestinationIdentity = FileService.TryCaptureSourceIdentity(destination);
                    verifiedHashes.Add(destination, hash);
                    copied += entry.Length;
                    Report(ManagedStorageMigrationPhase.CheckingExisting, ++completed, existing.Count, copied, existingBytes,
                        source, rateTracker.BytesPerSecond, 1, totalSteps);
                }
            }

            journal.State = "Copying";
            await SaveAsync(journal);
            rateTracker.Reset();
            int copiedSincePersist = 0;
            foreach (var entry in files.Except(existing))
            {
                token.ThrowIfCancellationRequested();
                RequireEntryDestination(journal, entry.FolderIndex);
                string source = Source(journal, entry);
                string destination = Destination(journal, entry);
                RequireUnchangedFile(entry, source);
                Report(ManagedStorageMigrationPhase.Copying, completed, files.Count, copied, journal.TotalBytes,
                    source, rateTracker.BytesPerSecond, 1, totalSteps);
                string hash;
                {
                    string temporary = Path.Combine(journal.TemporaryDirectory, Guid.NewGuid().ToString("N") + ".partial");
                    long baseBytes = copied;
                    long lastFileBytes = 0;
                    long lastReport = Environment.TickCount64;
                    try
                    {
                        hash = FileService.CopyStorageFileVerified(
                            source,
                            temporary,
                            token,
                            bytes =>
                            {
                                long delta = bytes - lastFileBytes;
                                if (delta > 0)
                                {
                                    rateTracker.RecordBytes(delta);
                                    lastFileBytes = bytes;
                                }
                                if (Environment.TickCount64 - lastReport < 150) return;
                                lastReport = Environment.TickCount64;
                                Report(ManagedStorageMigrationPhase.Copying, completed, files.Count,
                                    baseBytes + bytes, journal.TotalBytes, source, rateTracker.BytesPerSecond, 1, totalSteps);
                            },
                            verificationProgress: null,
                            verifyDestinationImmediately: false);
                        RequireUnchangedFile(entry, source);
                        if (StorageMigrationNativeMethods.TryReadFileUsn(source, out long copiedUsn))
                        {
                            verifiedSourceUsns[source] = copiedUsn;
                            entry.SourceUsn = copiedUsn;
                        }
                        FileService.PublishStorageCopy(temporary, destination);
                    }
                    catch
                    {
                        StorageMigrationNativeMethods.TryDeleteTemporaryPartialFile(temporary);
                        throw;
                    }
                }
                entry.DestinationIdentity = FileService.TryCaptureSourceIdentity(destination);
                verifiedHashes.Add(destination, hash);
                copied += entry.Length;
                completed++;
                copiedSincePersist++;
                if (copiedSincePersist >= 64)
                {
                    copiedSincePersist = 0;
                    journal.CompletedFiles = completed;
                    journal.BytesProcessed = copied;
                    await SaveAsync(journal);
                }
                Report(ManagedStorageMigrationPhase.Copying, completed, files.Count, copied, journal.TotalBytes,
                    source, rateTracker.BytesPerSecond, 1, totalSteps);
            }

            journal.State = "Verifying";
            await SaveAsync(journal);
            rateTracker.Reset();
            Report(ManagedStorageMigrationPhase.Verifying, 0, files.Count, 0, journal.TotalBytes, null, 0, 2, totalSteps);
            completed = 0;
            long verified = 0;
            foreach (var entry in files)
            {
                token.ThrowIfCancellationRequested();
                string source = Source(journal, entry);
                string destination = Destination(journal, entry);
                RequireUnchangedFile(entry, source);
                string expected = verifiedHashes[destination];
                bool sourceUsnUnchanged =
                    verifiedSourceUsns.TryGetValue(source, out long expectedUsn) &&
                    StorageMigrationNativeMethods.TryReadFileUsn(source, out long currentUsn) &&
                    currentUsn == expectedUsn;
                long checkedBytes = 0;
                long lastProgress = Environment.TickCount64;
                void OnVerifiedBytes(long bytes)
                {
                    checkedBytes += bytes;
                    rateTracker.RecordBytes(bytes);
                    if (Environment.TickCount64 - lastProgress < 150) return;
                    lastProgress = Environment.TickCount64;
                    long delta = sourceUsnUnchanged ? checkedBytes : checkedBytes / 2;
                    options.Progress?.Report(new(ManagedStorageMigrationPhase.Verifying, completed, files.Count,
                        verified + delta, journal.TotalBytes, source,
                        CheckingCurrentFile: false, BytesPerSecond: rateTracker.BytesPerSecond,
                        StepIndex: 2, TotalSteps: totalSteps));
                }
                // Only re-read source bytes when MFT USN changed or USN is unavailable:
                // any content/stream edit bumps USN even if size and mtime were restored.
                if (!sourceUsnUnchanged && FileService.HashStorageFile(source, token, OnVerifiedBytes) != expected)
                    throw new StorageMigrationException(StorageMigrationProblem.SourceChanged,
                        $"The source changed during verification: '{source}'.",
                        blockingPath: source);
                if (FileService.HashStorageFile(destination, token, OnVerifiedBytes) != expected)
                    throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                        $"The destination changed during verification: '{destination}'.",
                        blockingPath: destination);
                verified += entry.Length;
                Report(ManagedStorageMigrationPhase.Verifying, ++completed, files.Count, verified, journal.TotalBytes,
                    source, rateTracker.BytesPerSecond, 2, totalSteps);
            }
            // Final full inventory verification catches any newly dropped or removed
            // file on either side before the metadata commit is authorized.
            RequireUnchangedSources(journal, token);
            RequireExactDestinationInventory(journal);
            foreach (var entry in journal.Entries.Where(e => e.IsDirectory).OrderByDescending(e => e.RelativePath.Length))
            {
                string destination = Destination(journal, entry);
                string extendedDest = StorageMigrationNativeMethods.Extended(destination);
                Directory.SetLastWriteTimeUtc(extendedDest, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
                FileAttributes dirAttrs = entry.Attributes & PreservedDirectoryAttributeMask;
                if (dirAttrs != 0)
                {
                    File.SetAttributes(extendedDest, dirAttrs | FileAttributes.Directory);
                }
            }
            token.ThrowIfCancellationRequested();
            StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(journal.TemporaryDirectory);
            journal.State = "Verified";
            journal.TemporaryBytes = MeasureTemporaryBytes(journal.TemporaryDirectory);
            await SaveAsync(journal);
            return (journal, resumed);

            void Report(ManagedStorageMigrationPhase phase, int done, int count, long bytes, long total,
                string? path, double rate, int stepIndex, int steps)
            {
                Volatile.Write(ref _diagnostic, new(Diagnostic?.TaskId ?? "", phase.ToString(), done, count, bytes, total, options.RetainOriginals));
                PhaseObserver?.Invoke(phase);
                options.Progress?.Report(new(phase, done, count, bytes, total, path,
                    CheckingCurrentFile: false, BytesPerSecond: rate, StepIndex: stepIndex, TotalSteps: steps));
            }
        }, options.CancellationToken);
    }

    /// <summary>
    /// Post-commit identity-verified cleanup of copied source files. Never throws
    /// to roll back a committed migration: locked or concurrently changed files
    /// are left untouched and recorded in the journal so the user can retry.
    /// </summary>
    internal async Task<ManagedStorageMigrationCleanupResult> CleanupVerifiedSourcesAsync(
        StorageMigrationJournal journal,
        IProgress<ManagedStorageMigrationProgress>? progress,
        CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            var fileEntries = journal.Entries.Where(e => !e.IsDirectory).ToList();
            int total = fileEntries.Count;
            long totalBytes = fileEntries.Sum(e => e.Length);
            int processed = 0;
            int cleanedCount = 0;
            long freedBytes = 0;
            var remainingEntries = new List<StorageMigrationEntry>();
            long lastReport = 0;

            progress?.Report(new(ManagedStorageMigrationPhase.CleaningOrigin, 0, total, 0, totalBytes,
                null, StepIndex: 3, TotalSteps: 3));

            foreach (var entry in fileEntries)
            {
                if (token.IsCancellationRequested)
                {
                    remainingEntries.Add(entry);
                    continue;
                }

                string source = Source(journal, entry);
                bool removed = false;
                if (!PathPresent(source))
                {
                    removed = true;
                }
                else if (CanSafelyDeleteVerifiedSource(entry, source) &&
                         entry.SourceIdentity is { } expectedId)
                {
                    removed = FileService.TryDeleteFileByIdentity(source, expectedId);
                }

                if (removed)
                {
                    cleanedCount++;
                    freedBytes += entry.Length;
                }
                else
                {
                    remainingEntries.Add(entry);
                }

                processed++;
                if (processed == total || Environment.TickCount64 - lastReport >= 120)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new(ManagedStorageMigrationPhase.CleaningOrigin, processed, total,
                        freedBytes, totalBytes, source, StepIndex: 3, TotalSteps: 3));
                }
            }

            PruneEmptySourceDirectories(journal);

            journal.Entries = remainingEntries;
            journal.RemainingSourceFiles = remainingEntries.Count;
            journal.CleanupPending = remainingEntries.Count > 0;
            try { await SaveAsync(journal); }
            catch (Exception ex) { App.Log($"[ManagedStorageMigration] Post-cleanup journal save failed: {ex.Message}"); }

            return new ManagedStorageMigrationCleanupResult(
                cleanedCount,
                remainingEntries.Count,
                freedBytes,
                Directory.Exists(StorageMigrationNativeMethods.Extended(journal.OldRootPath)));
        }, CancellationToken.None);
    }

    internal async Task<ManagedStorageMigrationCleanupResult> RetrySourceCleanupAsync(
        string newRootPath,
        CancellationToken token)
    {
        StorageMigrationJournal? journal = await ReadAsync(GetJournalPath(newRootPath), repair: false);
        if (journal is null || journal.State != "Committed" || !journal.CleanupPending || journal.Entries.Count == 0)
        {
            bool exists = journal is not null && Directory.Exists(StorageMigrationNativeMethods.Extended(journal.OldRootPath));
            return new ManagedStorageMigrationCleanupResult(0, 0, 0, exists);
        }

        ValidateJournal(journal);
        var result = await CleanupVerifiedSourcesAsync(journal, progress: null, token);
        if (result.RemainingFileCount == 0)
        {
            ManagedStorageOwnershipFileOps.TryPruneEmptyAbandonedOriginRoot(
                journal.OldRootPath,
                journal.Folders.Select(static f => f.ManagedFolderName).ToArray(),
                TemporaryFolderName);
            result = result with { TargetStillExists = Directory.Exists(StorageMigrationNativeMethods.Extended(journal.OldRootPath)) };
        }

        return result;
    }

    /// <summary>
    /// Cleans only files proven by the unfinished journal to have been created
    /// by this interrupted migration task, then archives and clears the journal
    /// so the destination path is unblocked for a fresh start.
    /// </summary>
    internal async Task<ManagedStorageMigrationCleanupResult> CleanupInterruptedDestinationCopiesAsync(
        string newRootPath,
        CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            string journalPath = GetJournalPath(newRootPath);
            StorageMigrationJournal? saved = await ReadAsync(journalPath, repair: false);
            if (saved is null || saved.State == "Committed")
            {
                return new ManagedStorageMigrationCleanupResult(0, 0, 0, false);
            }

            ValidateJournal(saved);
            RequireDestinationVolume(saved);
            StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(saved.TemporaryDirectory);

            int cleaned = 0;
            int remaining = 0;
            long freedBytes = 0;

            foreach (var entry in saved.Entries.Where(e => !e.IsDirectory))
            {
                token.ThrowIfCancellationRequested();
                if (entry.FolderIndex >= saved.DestinationIdentities.Count ||
                    !DestinationFolderMatchesIdentity(saved, entry.FolderIndex))
                {
                    continue;
                }

                string destination = Destination(saved, entry);
                if (!PathPresent(destination))
                {
                    continue;
                }

                bool deleted = false;
                if (entry.DestinationIdentity is { } expectedDestId)
                {
                    deleted = FileService.TryDeleteFileByIdentity(destination, expectedDestId);
                }
                else
                {
                    // Legacy interrupted journal without recorded destination identity:
                    // only remove if destination is proven byte-identical to unchanged source.
                    string source = Source(saved, entry);
                    if (PathPresent(source) &&
                        CanSafelyDeleteVerifiedSource(entry, source) &&
                        FileService.TryCaptureSourceIdentity(destination) is { } destId &&
                        string.Equals(
                            FileService.HashStorageFile(source, token),
                            FileService.HashStorageFile(destination, token),
                            StringComparison.Ordinal))
                    {
                        deleted = FileService.TryDeleteFileByIdentity(destination, destId);
                    }
                }

                if (deleted)
                {
                    cleaned++;
                    freedBytes += entry.Length;
                }
                else
                {
                    remaining++;
                }
            }

            // Prune only empty subdirectories inside identity-verified destination folders.
            for (int i = 0; i < saved.Folders.Count; i++)
            {
                if (i >= saved.DestinationIdentities.Count || !DestinationFolderMatchesIdentity(saved, i))
                {
                    continue;
                }

                string destFolder = saved.Folders[i].DestinationFolder;
                PruneEmptySubdirectoriesBottomUp(destFolder);
                StorageMigrationNativeMethods.TryDeleteEmptyDirectory(destFolder);
            }

            StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(saved.TemporaryDirectory);

            if (remaining == 0)
            {
                saved.LastOutcome = "Abandoned";
                await SaveAsync(saved, archive: true);
                DeleteActiveJournalFiles(journalPath);
                if (ReferenceEquals(_activeJournal, saved))
                {
                    _activeJournal = null;
                }
            }
            else
            {
                saved.TemporaryBytes = MeasureTemporaryBytes(saved.TemporaryDirectory);
                await SaveAsync(saved);
            }

            bool anyDestStillExists = saved.Folders.Any(f => PathPresent(f.DestinationFolder));
            return new ManagedStorageMigrationCleanupResult(cleaned, remaining, freedBytes, anyDestStillExists);
        }, token);
    }

    internal bool HasUnfinishedCopiesForDestination(string newRootPath)
    {
        try
        {
            if (_activeJournal is { State: not "Committed" } active &&
                Same(active.NewRootPath, newRootPath))
            {
                return active.Folders.Any(f => PathPresent(f.DestinationFolder));
            }
        }
        catch { }

        return false;
    }

    internal Task SaveAsync(StorageMigrationJournal journal, bool archive = false)
    {
        if (!archive)
        {
            _activeJournal = journal;
            Volatile.Write(ref _diagnostic, new(journal.Id, journal.State,
                journal.State == "Committed" ? journal.FileCount : Diagnostic?.CompletedFiles ?? journal.CompletedFiles,
                journal.FileCount, Diagnostic?.BytesProcessed ?? journal.BytesProcessed, journal.TotalBytes,
                journal.RetainOriginals || journal.RemainingSourceFiles > 0, journal.LastErrorCode, journal.TemporaryBytes));
        }
        string path = archive ? Path.Combine(JournalDirectory, "history", journal.Id + ".json") : GetJournalPath(journal.NewRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return ResilientJsonStore.SaveAsync(path, async temporary =>
        {
            await using var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, journal, StorageMigrationJsonContext.Default.StorageMigrationJournal);
            stream.Flush(flushToDisk: true);
        });
    }

    internal async Task<IReadOnlyList<StorageMigrationJournal>> ReadPendingCommitsAsync()
    {
        if (!Directory.Exists(JournalDirectory)) return [];
        var result = new List<StorageMigrationJournal>();
        foreach (string path in Directory.EnumerateFiles(JournalDirectory, "*.json*")
                     .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var journal = await ReadAsync(path);
            if (journal is not null)
                Volatile.Write(ref _diagnostic, new(journal.Id, journal.State,
                    journal.State == "Committed" ? journal.FileCount : journal.CompletedFiles,
                    journal.FileCount, journal.BytesProcessed, journal.TotalBytes,
                    journal.RetainOriginals || journal.RemainingSourceFiles > 0,
                    journal.LastErrorCode, journal.TemporaryBytes));
            if (journal is { State: "Committing" or "Moving" })
            {
                ValidateJournal(journal);
                result.Add(journal);
            }
        }
        return result;
    }

    private static async Task<StorageMigrationJournal?> ReadAsync(string path, bool repair = true)
    {
        if (!File.Exists(path) && !File.Exists(ResilientJsonStore.GetBackupPath(path))) return null;
        StorageMigrationJournal? Decode(string json) =>
            JsonSerializer.Deserialize(json, StorageMigrationJsonContext.Default.StorageMigrationJournal);
        if (!repair)
        {
            foreach (string candidate in new[] { path, ResilientJsonStore.GetBackupPath(path) })
            {
                if (!File.Exists(candidate)) continue;
                try { return Decode(await File.ReadAllTextAsync(candidate)) ?? throw new InvalidDataException("Empty recovery record."); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            }
            throw new IOException("The migration recovery record cannot be read.");
        }
        var result = await ResilientJsonStore.LoadAsync<StorageMigrationJournal?>(path,
            Decode,
            () => null, "ManagedStorageMigration");
        return result ?? throw new IOException("The migration recovery record cannot be read. Existing files have been preserved.");
    }

    private static void DeleteActiveJournalFiles(string journalPath) =>
        StorageMigrationNativeMethods.TryDeleteJournalFiles(
            journalPath,
            ResilientJsonStore.GetBackupPath(journalPath));

    private string GetJournalPath(string newRoot) => Path.Combine(JournalDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(newRoot).ToUpperInvariant()))) + ".json");

    private static (int FileCount, long TotalBytes, bool AllEmpty) ScanStatsForSameVolumeMove(
        IReadOnlyList<ManagedStorageMigrationFolder> folders, CancellationToken token)
    {
        int fileCount = 0;
        long totalBytes = 0;
        bool allEmpty = true;

        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            if (!PathPresent(folder.SourceFolder))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(folder.SourceFolder);
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                string extendedDir = StorageMigrationNativeMethods.Extended(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(extendedDir))
                {
                    token.ThrowIfCancellationRequested();
                    allEmpty = false;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch { continue; }

                    bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (isDirectory)
                    {
                        // Never traverse directory symlinks/junctions/mount points.
                        if ((attributes & FileAttributes.ReparsePoint) == 0 ||
                            !StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(entry))
                        {
                            pending.Push(entry);
                        }
                        continue;
                    }

                    fileCount++;
                    try { totalBytes += new FileInfo(entry).Length; }
                    catch { }
                }
            }
        }

        return (fileCount, totalBytes, allEmpty);
    }

    private static bool MatchesSavedResumeSource(
        StorageMigrationJournal saved,
        IReadOnlyList<ManagedStorageMigrationFolder> currentFolders,
        List<StorageMigrationEntry> currentEntries)
    {
        if (!saved.Folders.SequenceEqual(currentFolders))
        {
            return false;
        }

        if (saved.Entries.Count != currentEntries.Count)
        {
            return false;
        }

        for (int i = 0; i < currentEntries.Count; i++)
        {
            var before = saved.Entries[i];
            var now = currentEntries[i];
            if (before.FolderIndex != now.FolderIndex ||
                before.RelativePath != now.RelativePath ||
                before.IsDirectory != now.IsDirectory ||
                before.Length != now.Length ||
                before.Attributes != now.Attributes ||
                (!before.IsDirectory &&
                 (before.LastWriteUtcTicks != now.LastWriteUtcTicks ||
                  before.SourceIdentity != now.SourceIdentity)))
            {
                return false;
            }
        }

        return true;
    }

    private static List<StorageMigrationEntry> Scan(IReadOnlyList<ManagedStorageMigrationFolder> folders, CancellationToken token)
    {
        var entries = new List<StorageMigrationEntry>();
        for (int index = 0; index < folders.Count; index++)
        {
            var pending = new Stack<string>();
            pending.Push(folders[index].SourceFolder);
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                FileService.RequireStorageDirectory(directory);
                // Directory alternate streams would be lost by ordinary directory creation.
                if (StorageMigrationNativeMethods.ReadStreams(directory).Count != 0)
                    throw new StorageMigrationException(
                        StorageMigrationProblem.Unsupported,
                        $"Directory data streams require manual copying: '{directory}'.",
                        blockingPath: directory);
                foreach (string extendedEntry in Directory.EnumerateFileSystemEntries(StorageMigrationNativeMethods.Extended(directory)))
                {
                    token.ThrowIfCancellationRequested();
                    string path = DriveMediaInterop.StripExtendedPathPrefix(extendedEntry);
                    FileAttributes attributes = File.GetAttributes(extendedEntry);
                    bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    FileService.RequireStoragePath(path, isDirectory);
                    long size = isDirectory ? 0 : StorageMigrationNativeMethods.ReadStreams(path).Sum(s => s.Length);
                    var identity = FileService.TryCaptureSourceIdentity(path)
                        ?? throw new StorageMigrationException(
                            StorageMigrationProblem.InUse,
                            $"The source is busy or unavailable and cannot be safely inspected: '{path}'.",
                            blockingPath: path);
                    entries.Add(new StorageMigrationEntry
                    {
                        FolderIndex = index, RelativePath = Path.GetRelativePath(folders[index].SourceFolder, path),
                        IsDirectory = isDirectory, Length = size, Attributes = attributes,
                        LastWriteUtcTicks = File.GetLastWriteTimeUtc(extendedEntry).Ticks,
                        SourceIdentity = identity
                    });
                    if (isDirectory) pending.Push(path);
                }
            }
        }
        return entries.OrderBy(e => e.FolderIndex).ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void RequireUnchangedSources(StorageMigrationJournal journal, CancellationToken token)
    {
        var current = Scan(journal.Folders, token);
        if (current.Count != journal.Entries.Count) throw new StorageMigrationException(
            StorageMigrationProblem.SourceChanged, "The source inventory changed. No path was switched.");
        for (int i = 0; i < current.Count; i++)
        {
            var before = journal.Entries[i];
            var now = current[i];
            if (before.FolderIndex != now.FolderIndex || before.RelativePath != now.RelativePath ||
                before.IsDirectory != now.IsDirectory || before.Length != now.Length ||
                before.Attributes != now.Attributes ||
                (!before.IsDirectory && (before.LastWriteUtcTicks != now.LastWriteUtcTicks || before.SourceIdentity != now.SourceIdentity)))
                throw new StorageMigrationException(StorageMigrationProblem.SourceChanged,
                    $"The source changed during migration: '{Source(journal, before)}'.",
                    blockingPath: Source(journal, before));
        }
    }

    private static void RequireUnchangedFile(StorageMigrationEntry entry, string source)
    {
        FileService.RequireStoragePath(source, directory: false);
        string extended = StorageMigrationNativeMethods.Extended(source);
        if (File.GetLastWriteTimeUtc(extended).Ticks != entry.LastWriteUtcTicks ||
            File.GetAttributes(extended) != entry.Attributes ||
            FileService.TryCaptureSourceIdentity(source) != entry.SourceIdentity)
            throw new StorageMigrationException(StorageMigrationProblem.SourceChanged,
                $"The source file changed: '{source}'.",
                blockingPath: source);
    }

    private static bool CanSafelyDeleteVerifiedSource(StorageMigrationEntry entry, string source)
    {
        try
        {
            if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(source))
            {
                return false;
            }

            if (entry.SourceIdentity is null)
            {
                return false;
            }

            if (entry.SourceUsn is { } expectedUsn)
            {
                if (!StorageMigrationNativeMethods.TryReadFileUsn(source, out long currentUsn) ||
                    currentUsn != expectedUsn)
                {
                    return false;
                }
            }

            string extended = StorageMigrationNativeMethods.Extended(source);
            FileAttributes currentAttrs = File.GetAttributes(extended);
            if (currentAttrs.HasFlag(FileAttributes.Directory) ||
                File.GetLastWriteTimeUtc(extended).Ticks != entry.LastWriteUtcTicks ||
                currentAttrs != entry.Attributes)
            {
                return false;
            }

            return FileService.TryCaptureSourceIdentity(source) == entry.SourceIdentity;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneEmptySourceDirectories(StorageMigrationJournal journal)
    {
        foreach (var folder in journal.Folders)
        {
            if (!PathPresent(folder.SourceFolder))
            {
                continue;
            }

            PruneEmptySubdirectoriesBottomUp(folder.SourceFolder);
            if (StorageMigrationNativeMethods.TryDeleteEmptyDirectory(folder.SourceFolder))
            {
                ManagedStorageOwnershipFileOps.UnregisterRemovedOrphanFolder(
                    journal.OldRootPath,
                    folder.ManagedFolderName);
            }
        }
    }

    private static void PruneEmptySubdirectoriesBottomUp(string rootDirectory)
    {
        try
        {
            string extendedRoot = StorageMigrationNativeMethods.Extended(rootDirectory);
            if (!Directory.Exists(extendedRoot) ||
                (File.GetAttributes(extendedRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootDirectory);
            while (pending.TryPop(out string? current))
            {
                foreach (string extendedChild in Directory.EnumerateDirectories(StorageMigrationNativeMethods.Extended(current)))
                {
                    string child = DriveMediaInterop.StripExtendedPathPrefix(extendedChild);
                    if ((File.GetAttributes(extendedChild) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    directories.Add(child);
                    pending.Push(child);
                }
            }

            foreach (string directory in directories.OrderByDescending(static d => d.Length))
            {
                StorageMigrationNativeMethods.TryDeleteEmptyDirectory(directory);
            }
        }
        catch
        {
            // Non-empty or locked subdirectories remain intact.
        }
    }

    private static void MoveDirectoryAtomicWithRetry(string source, string destination, CancellationToken token)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 150;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                StorageMigrationNativeMethods.MoveDirectoryAtomic(source, destination);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMs);
            }
        }
    }

    private static void RollbackMovedFolders(
        IReadOnlyList<ManagedStorageMigrationFolder> movedFolders,
        string newRoot,
        bool createdNewRoot)
    {
        for (int i = movedFolders.Count - 1; i >= 0; i--)
        {
            var folder = movedFolders[i];
            if (!PathPresent(folder.DestinationFolder) || PathPresent(folder.SourceFolder))
            {
                continue;
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    StorageMigrationNativeMethods.MoveDirectoryAtomic(folder.DestinationFolder, folder.SourceFolder);
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt >= 3)
                    {
                        App.Log($"[ManagedStorageMigration] Same-volume rollback failed for '{folder.DestinationFolder}': {ex.Message}");
                    }
                    else
                    {
                        Thread.Sleep(150);
                    }
                }
            }
        }

        if (createdNewRoot)
        {
            StorageMigrationNativeMethods.TryDeleteEmptyDirectory(newRoot);
        }
    }

    private static StorageMigrationProblem ClassifySameVolumeMoveError(Exception ex, string sourceFolder)
    {
        if (ex.InnerException is System.ComponentModel.Win32Exception native)
        {
            if (native.NativeErrorCode is 32 or 33)
            {
                return StorageMigrationProblem.InUse;
            }

            // On Windows NTFS, MoveFileExW on a directory containing an open file
            // or Explorer handle commonly reports ERROR_ACCESS_DENIED (5) even
            // when the user owns full ACLs. If the directory is still enumerable,
            // classify as InUse so the user knows to close open apps/Explorer windows.
            if (native.NativeErrorCode == 5)
            {
                try
                {
                    using var e = Directory.EnumerateFileSystemEntries(StorageMigrationNativeMethods.Extended(sourceFolder)).GetEnumerator();
                    _ = e.MoveNext();
                    return StorageMigrationProblem.InUse;
                }
                catch
                {
                    return StorageMigrationProblem.AccessDenied;
                }
            }
        }

        return ManagedStorageMigrationPresentation.Classify(ex);
    }

    private static bool DestinationFolderMatchesIdentity(StorageMigrationJournal journal, int index)
    {
        try
        {
            string path = journal.Folders[index].DestinationFolder;
            if (!PathPresent(path)) return false;
            var before = journal.DestinationIdentities[index];
            var now = FileService.TryCaptureSourceIdentity(path);
            return before is not null && now is not null &&
                   before.Value.FileId == now.Value.FileId &&
                   before.Value.VolumeSerialNumber == now.Value.VolumeSerialNumber;
        }
        catch
        {
            return false;
        }
    }

    private static void RequireDestinations(StorageMigrationJournal journal)
    {
        if (journal.DestinationIdentities.Count != journal.Folders.Count)
            throw new IOException("Destination reservation was interrupted. Existing files are preserved; choose a new destination.");
        FileService.RequireStorageDirectory(journal.TemporaryDirectory);
        for (int i = 0; i < journal.Folders.Count; i++)
            RequireDestinationIdentity(journal, i);
    }

    private static void RequireEntryDestination(StorageMigrationJournal journal, int folderIndex)
    {
        if (journal.DestinationIdentities.Count != journal.Folders.Count)
            throw new IOException("Destination reservation was interrupted. Existing files are preserved; choose a new destination.");
        FileService.RequireStorageDirectory(journal.TemporaryDirectory);
        RequireDestinationIdentity(journal, folderIndex);
    }

    private static void RequireDestinationIdentity(StorageMigrationJournal journal, int index)
    {
        string path = journal.Folders[index].DestinationFolder;
        FileService.RequireStorageDirectory(path);
        var before = journal.DestinationIdentities[index];
        var now = FileService.TryCaptureSourceIdentity(path);
        if (before is null || now is null || before.Value.FileId != now.Value.FileId ||
            before.Value.VolumeSerialNumber != now.Value.VolumeSerialNumber)
            throw new StorageMigrationException(StorageMigrationProblem.DestinationChanged,
                $"The destination directory or volume changed: '{path}'.",
                blockingPath: path);
    }

    private void RequireDestinationVolume(StorageMigrationJournal journal)
    {
        RequireSupportedVolume(journal.NewRootPath);
        FileService.RequireStorageDirectory(Path.GetPathRoot(journal.NewRootPath)!);
        ulong current = VolumeIdentity(journal.NewRootPath)
            ?? throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable, "The destination volume is unavailable.");
        ulong? expected = journal.TargetVolumeSerialNumber ??
            journal.DestinationIdentities.FirstOrDefault(identity => identity is not null)?.VolumeSerialNumber;
        if (expected is { } volume && volume != current)
            throw new StorageMigrationException(StorageMigrationProblem.DestinationChanged,
                "The drive letter now refers to a different volume.");
        journal.TargetVolumeSerialNumber ??= current;
    }

    private static ulong? ReadVolumeIdentity(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!PathPresent(current)) continue;
            FileService.RequireStorageDirectory(current);
            return FileService.TryCaptureSourceIdentity(current)?.VolumeSerialNumber;
        }
        return null;
    }

    private static string? ResolveExistingComparableVolumeIdentity(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!PathPresent(current)) continue;
            return FileService.GetComparableVolumeIdentity(current, useParentPath: false);
        }
        return null;
    }

    private static long? MeasureTemporaryBytes(string directory)
    {
        try
        {
            string workRoot = Path.GetDirectoryName(directory)!;
            if (!PathPresent(workRoot)) return 0;
            FileService.RequireStorageDirectory(workRoot);
            long total = 0;
            foreach (string taskDirectory in Directory.EnumerateDirectories(StorageMigrationNativeMethods.Extended(workRoot)))
            {
                string taskDirPath = DriveMediaInterop.StripExtendedPathPrefix(taskDirectory);
                if (!Guid.TryParseExact(Path.GetFileName(taskDirPath), "N", out _)) continue;
                FileService.RequireStorageDirectory(taskDirPath);
                foreach (string extendedPath in Directory.EnumerateFiles(taskDirectory, "*.partial"))
                {
                    string path = DriveMediaInterop.StripExtendedPathPrefix(extendedPath);
                    FileService.RequireStoragePath(path, directory: false);
                    total = checked(total + new FileInfo(extendedPath).Length);
                }
            }
            return total;
        }
        catch { return null; }
    }

    private static void RequireExactDestinationInventory(StorageMigrationJournal journal)
    {
        RequireDestinations(journal);
        for (int i = 0; i < journal.Folders.Count; i++)
        {
            var expected = journal.Entries.Where(e => e.FolderIndex == i)
                .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(journal.Folders[i].DestinationFolder);
            int count = 0;
            while (pending.TryPop(out string? directory))
            {
                FileService.RequireStorageDirectory(directory);
                foreach (string extendedPath in Directory.EnumerateFileSystemEntries(StorageMigrationNativeMethods.Extended(directory)))
                {
                    string path = DriveMediaInterop.StripExtendedPathPrefix(extendedPath);
                    string relative = Path.GetRelativePath(journal.Folders[i].DestinationFolder, path);
                    if (!expected.TryGetValue(relative, out var entry))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"Unexpected content was found in the destination: '{path}'.",
                            blockingPath: path);
                    FileService.RequireStoragePath(path, entry.IsDirectory);
                    count++;
                    if (entry.IsDirectory) pending.Push(path);
                }
            }
            if (count != expected.Count) throw new IOException("The destination inventory is incomplete.");
        }
    }

    private void CheckSpace(string path, long bytes)
    {
        long reserve = ComputeSpaceReserve(bytes);
        if (AvailableSpace(path) is not { } free)
            throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable, "The destination is unavailable.");
        if (free < bytes || free - bytes < reserve)
            throw new StorageMigrationException(StorageMigrationProblem.DiskFull,
                $"The destination lacks space for {FileMetaService.FormatSize(bytes)} plus verification workspace.");
    }

    private static long? GetAvailableSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch { return null; }
    }

    private static void RequireSupportedVolume(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new StorageMigrationException(StorageMigrationProblem.Unsupported,
                "A local NTFS or ReFS volume is required.", blockingPath: path);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!drive.IsReady)
                throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable,
                    "Unlock or reconnect the drive.", blockingPath: path);
            if (!IsSupportedVolumeFormat(drive.DriveFormat))
                throw new StorageMigrationException(StorageMigrationProblem.Unsupported,
                    "A local NTFS or ReFS volume is required.", blockingPath: path);
        }
        catch (Exception ex) when (ex is not StorageMigrationException && ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable,
                "Unlock or reconnect the drive.", ex, blockingPath: path);
        }
    }

    internal static bool IsSupportedVolumeFormat(string? driveFormat) =>
        string.Equals(driveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(driveFormat, "ReFS", StringComparison.OrdinalIgnoreCase);

    private static bool PathPresent(string path)
    {
        try { _ = File.GetAttributes(StorageMigrationNativeMethods.Extended(path)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ValidateJournal(StorageMigrationJournal journal)
    {
        if (journal.SchemaVersion != 1 || !Guid.TryParseExact(journal.Id, "N", out _) ||
            !Same(journal.TemporaryDirectory, Path.Combine(journal.NewRootPath, TemporaryFolderName, journal.Id)))
            throw new IOException("The migration recovery record is invalid.");
        foreach (var entry in journal.Entries)
        {
            if (entry.FolderIndex < 0 || entry.FolderIndex >= journal.Folders.Count ||
                string.IsNullOrEmpty(entry.RelativePath) || Path.IsPathRooted(entry.RelativePath) ||
                entry.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "."))
                throw new IOException("The migration inventory contains an invalid relative path.");
        }
    }

    private static string Source(StorageMigrationJournal journal, StorageMigrationEntry entry) =>
        Path.Combine(journal.Folders[entry.FolderIndex].SourceFolder, entry.RelativePath);
    private static string Destination(StorageMigrationJournal journal, StorageMigrationEntry entry) =>
        Path.Combine(journal.Folders[entry.FolderIndex].DestinationFolder, entry.RelativePath);
    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class TransferRateTracker
    {
        private readonly Queue<(long Tick, long Bytes)> _samples = new();
        private long _windowBytes;

        internal double BytesPerSecond { get; private set; }

        internal void Reset()
        {
            _samples.Clear();
            _windowBytes = 0;
            BytesPerSecond = 0;
        }

        internal void RecordBytes(long bytes)
        {
            if (bytes <= 0) return;
            long now = Environment.TickCount64;
            _samples.Enqueue((now, bytes));
            _windowBytes += bytes;
            while (_samples.Count > 1 && now - _samples.Peek().Tick > 2000)
            {
                _windowBytes -= _samples.Dequeue().Bytes;
            }

            long elapsedMs = _samples.Count > 0 ? now - _samples.Peek().Tick : 0;
            BytesPerSecond = elapsedMs >= 200 ? _windowBytes * 1000d / elapsedMs : 0;
        }
    }
}
