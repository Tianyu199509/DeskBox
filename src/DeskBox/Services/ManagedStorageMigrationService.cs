using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Copy-only storage transaction. Source deletion, recursive destination cleanup,
/// conflict merging and automatic move-back are deliberately absent.
/// </summary>
internal sealed class ManagedStorageMigrationService(string journalDirectory)
{
    internal const string TemporaryFolderName = ".deskbox-migration";
    internal Action<ManagedStorageMigrationPhase>? PhaseObserver { get; set; }
    internal Func<string, long?> AvailableSpace { get; set; } = GetAvailableSpace;
    internal Func<string, ulong?> VolumeIdentity { get; set; } = ReadVolumeIdentity;

    internal string JournalDirectory { get; } = journalDirectory;
    private ManagedStorageMigrationDiagnostic? _diagnostic;
    private StorageMigrationJournal? _activeJournal;
    internal ManagedStorageMigrationDiagnostic? Diagnostic => Volatile.Read(ref _diagnostic);

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

    internal async Task<ManagedStorageMigrationPreview> PreviewAsync(
        string newRoot, IReadOnlyList<ManagedStorageMigrationFolder> folders, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            RequireSupportedVolume(newRoot);
            // Deep review R15 (F2 audit): the destination-ancestry guard
            // (rejects reparse-point ancestors) only ran at copy time, so a
            // junction root previewed green and failed late. Fail at preview
            // so the user picks a different root before any copying.
            FileService.RequireStorageDestinationAncestry(newRoot);
            var saved = await ReadAsync(GetJournalPath(newRoot), repair: false);
            bool cleared = false;
            if (saved is { State: not "Committed" })
            {
                ValidateJournal(saved);
                RequireDestinationVolume(saved);
                cleared = saved.Folders.Count > 0 && saved.DestinationIdentities.Count > 0 &&
                    saved.State != "Planned" && saved.LastStage != "Planned" &&
                    saved.Folders.All(f => !PathPresent(f.DestinationFolder));
            }
            foreach (var folder in folders) RequireSupportedVolume(folder.SourceFolder);
            var entries = Scan(folders, token);
            return new ManagedStorageMigrationPreview(folders.Count, entries.Count(e => !e.IsDirectory),
                entries.Where(e => !e.IsDirectory).Sum(e => e.Length), cleared,
                saved is null ? null : MeasureTemporaryBytes(saved.TemporaryDirectory));
        }, token);
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
            Report(ManagedStorageMigrationPhase.Preparing, 0, 0, 0, 0, null);
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
            }
            else
            {
                foreach (var folder in folders)
                    if (PathPresent(folder.DestinationFolder))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"The destination already exists and will not be overwritten: '{folder.DestinationFolder}'.");
                journal = new StorageMigrationJournal
                {
                    OldRootPath = oldRoot, NewRootPath = newRoot, Folders = folders.ToList(),
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
                Directory.CreateDirectory(newRoot);
                FileService.RequireStorageDirectory(newRoot);
                FileService.RequireStorageDestinationAncestry(journal.TemporaryDirectory);
                Directory.CreateDirectory(journal.TemporaryDirectory);
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
                        if (Directory.EnumerateFileSystemEntries(folder.DestinationFolder).Any())
                            throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                                $"The interrupted reservation contains untracked content: '{folder.DestinationFolder}'.");
                    }
                    else if (!Kernel32NativeMethods.CreateDirectory(folder.DestinationFolder, IntPtr.Zero))
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
                Directory.CreateDirectory(destination);
                FileService.RequireStorageDirectory(destination);
            }

            var files = journal.Entries.Where(e => !e.IsDirectory).ToList();
            var existing = files.Where(e => PathPresent(Destination(journal, e))).ToList();
            CheckSpace(newRoot, files.Except(existing).Sum(e => e.Length));
            int completed = 0;
            long copied = 0;
            var verifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (existing.Count > 0)
            {
                journal.State = "CheckingExisting";
                await SaveAsync(journal);
                long existingBytes = existing.Sum(e => e.Length);
                foreach (var entry in existing)
                {
                    token.ThrowIfCancellationRequested();
                    string source = Source(journal, entry);
                    string destination = Destination(journal, entry);
                    RequireUnchangedFile(entry, source);
                    Report(ManagedStorageMigrationPhase.CheckingExisting, completed, existing.Count, copied, existingBytes, source);
                    long read = 0;
                    long lastProgress = 0;
                    void Checked(long bytes)
                    {
                        read += bytes;
                        if (Environment.TickCount64 - lastProgress < 150) return;
                        lastProgress = Environment.TickCount64;
                        Report(ManagedStorageMigrationPhase.CheckingExisting, completed, existing.Count,
                            copied + read / 2, existingBytes, source);
                    }
                    string hash = FileService.HashStorageFile(source, token, Checked);
                    if (hash != FileService.HashStorageFile(destination, token, Checked))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"The existing destination differs from its source: '{destination}'.");
                    verifiedHashes.Add(destination, hash);
                    copied += entry.Length;
                    Report(ManagedStorageMigrationPhase.CheckingExisting, ++completed, existing.Count, copied, existingBytes, source);
                }
            }
            journal.State = "Copying";
            await SaveAsync(journal);
            foreach (var entry in files.Except(existing))
            {
                token.ThrowIfCancellationRequested();
                RequireDestinations(journal);
                string source = Source(journal, entry);
                string destination = Destination(journal, entry);
                RequireUnchangedFile(entry, source);
                Report(ManagedStorageMigrationPhase.Copying, completed, files.Count, copied, journal.TotalBytes, source);
                string hash;
                {
                    // Failed attempts stay in this task's private area. Retrying never
                    // overwrites/deletes a possibly useful partial file.
                    string temporary = Path.Combine(journal.TemporaryDirectory, Guid.NewGuid().ToString("N") + ".partial");
                    long baseBytes = copied;
                    long lastReport = Environment.TickCount64;
                    long checkingBytes = 0;
                    hash = FileService.CopyStorageFileVerified(source, temporary, token, bytes =>
                    {
                        if (Environment.TickCount64 - lastReport < 150) return;
                        lastReport = Environment.TickCount64;
                        Report(ManagedStorageMigrationPhase.Copying, completed, files.Count,
                            baseBytes + bytes, journal.TotalBytes, source);
                    }, bytes =>
                    {
                        checkingBytes += bytes;
                        if (entry.Length < 64L * 1024 * 1024 || Environment.TickCount64 - lastReport < 150) return;
                        lastReport = Environment.TickCount64;
                        options.Progress?.Report(new(ManagedStorageMigrationPhase.Copying, completed, files.Count,
                            baseBytes + entry.Length, journal.TotalBytes, source, CheckingCurrentFile: true));
                    });
                    RequireUnchangedFile(entry, source);
                    FileService.PublishStorageCopy(temporary, destination);
                }
                verifiedHashes.Add(destination, hash);
                copied += entry.Length;
                completed++;
                Report(ManagedStorageMigrationPhase.Copying, completed, files.Count, copied, journal.TotalBytes, source);
            }

            journal.State = "Verifying";
            await SaveAsync(journal);
            Report(ManagedStorageMigrationPhase.Verifying, 0, files.Count, 0, journal.TotalBytes, null);
            RequireUnchangedSources(journal, token);
            RequireExactDestinationInventory(journal);
            completed = 0;
            long verified = 0;
            foreach (var entry in files)
            {
                token.ThrowIfCancellationRequested();
                string source = Source(journal, entry);
                string destination = Destination(journal, entry);
                RequireUnchangedFile(entry, source);
                string expected = verifiedHashes[destination];
                long checkedBytes = 0;
                long lastProgress = Environment.TickCount64;
                void OnVerifiedBytes(long bytes)
                {
                    checkedBytes += bytes;
                    if (Environment.TickCount64 - lastProgress < 150) return;
                    lastProgress = Environment.TickCount64;
                    options.Progress?.Report(new(ManagedStorageMigrationPhase.Verifying, completed, files.Count,
                        verified + checkedBytes / 2, journal.TotalBytes, source));
                }
                // Re-read BOTH sides. A source edited after its first copy, even
                // with the original size/mtime restored, cannot silently commit.
                if (FileService.HashStorageFile(source, token, OnVerifiedBytes) != expected)
                    throw new StorageMigrationException(StorageMigrationProblem.SourceChanged,
                        $"The source changed during verification: '{source}'.");
                if (FileService.HashStorageFile(destination, token, OnVerifiedBytes) != expected)
                    throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                        $"The destination changed during verification: '{destination}'.");
                verified += entry.Length;
                Report(ManagedStorageMigrationPhase.Verifying, ++completed, files.Count, verified, journal.TotalBytes, source);
            }
            RequireUnchangedSources(journal, token);
            RequireExactDestinationInventory(journal);
            foreach (var entry in journal.Entries.Where(e => e.IsDirectory).OrderByDescending(e => e.RelativePath.Length))
            {
                string destination = Destination(journal, entry);
                Directory.SetLastWriteTimeUtc(destination, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
                File.SetAttributes(destination, entry.Attributes);
            }
            token.ThrowIfCancellationRequested();
            journal.State = "Verified";
            journal.TemporaryBytes = MeasureTemporaryBytes(journal.TemporaryDirectory);
            await SaveAsync(journal);
            return (journal, resumed);

            void Report(ManagedStorageMigrationPhase phase, int done, int count, long bytes, long total, string? path)
            {
                Volatile.Write(ref _diagnostic, new(Diagnostic?.TaskId ?? "", phase.ToString(), done, count, bytes, total, true));
                PhaseObserver?.Invoke(phase);
                options.Progress?.Report(new(phase, done, count, bytes, total, path));
            }
        }, options.CancellationToken);
    }

    internal Task SaveAsync(StorageMigrationJournal journal, bool archive = false)
    {
        if (!archive)
        {
            _activeJournal = journal;
            Volatile.Write(ref _diagnostic, new(journal.Id, journal.State,
                journal.State == "Committed" ? journal.FileCount : Diagnostic?.CompletedFiles ?? journal.CompletedFiles,
                journal.FileCount, Diagnostic?.BytesProcessed ?? journal.BytesProcessed, journal.TotalBytes,
                true, journal.LastErrorCode, journal.TemporaryBytes));
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
                    journal.FileCount, journal.BytesProcessed, journal.TotalBytes, true,
                    journal.LastErrorCode, journal.TemporaryBytes));
            if (journal is { State: "Committing" })
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

    private string GetJournalPath(string newRoot) => Path.Combine(JournalDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(newRoot).ToUpperInvariant()))) + ".json");

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
                    throw new IOException($"Directory data streams require manual copying: '{directory}'.");
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    FileService.RequireStoragePath(path, isDirectory);
                    long size = isDirectory ? 0 : StorageMigrationNativeMethods.ReadStreams(path).Sum(s => s.Length);
                    var identity = FileService.TryCaptureSourceIdentity(path)
                        ?? throw new IOException($"The source is busy or unavailable and cannot be safely inspected: '{path}'.");
                    entries.Add(new StorageMigrationEntry
                    {
                        FolderIndex = index, RelativePath = Path.GetRelativePath(folders[index].SourceFolder, path),
                        IsDirectory = isDirectory, Length = size, Attributes = attributes,
                        LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
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
                    $"The source changed during migration: '{Source(journal, before)}'.");
        }
    }

    private static void RequireUnchangedFile(StorageMigrationEntry entry, string source)
    {
        FileService.RequireStoragePath(source, directory: false);
        if (File.GetLastWriteTimeUtc(source).Ticks != entry.LastWriteUtcTicks ||
            File.GetAttributes(source) != entry.Attributes ||
            FileService.TryCaptureSourceIdentity(source) != entry.SourceIdentity)
            throw new StorageMigrationException(StorageMigrationProblem.SourceChanged, $"The source file changed: '{source}'.");
    }

    private static void RequireDestinations(StorageMigrationJournal journal)
    {
        if (journal.DestinationIdentities.Count != journal.Folders.Count)
            throw new IOException("Destination reservation was interrupted. Existing files are preserved; choose a new destination.");
        FileService.RequireStorageDirectory(journal.TemporaryDirectory);
        for (int i = 0; i < journal.Folders.Count; i++)
            RequireDestinationIdentity(journal, i);
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
                $"The destination directory or volume changed: '{path}'.");
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

    private static long? MeasureTemporaryBytes(string directory)
    {
        try
        {
            string workRoot = Path.GetDirectoryName(directory)!;
            if (!PathPresent(workRoot)) return 0;
            FileService.RequireStorageDirectory(workRoot);
            long total = 0;
            foreach (string taskDirectory in Directory.EnumerateDirectories(workRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(taskDirectory), "N", out _)) continue;
                FileService.RequireStorageDirectory(taskDirectory);
                foreach (string path in Directory.EnumerateFiles(taskDirectory, "*.partial"))
                {
                    FileService.RequireStoragePath(path, directory: false);
                    total = checked(total + new FileInfo(path).Length);
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
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    string relative = Path.GetRelativePath(journal.Folders[i].DestinationFolder, path);
                    if (!expected.TryGetValue(relative, out var entry))
                        throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict,
                            $"Unexpected content was found in the destination: '{path}'.");
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
        long reserve = Math.Max(64L * 1024 * 1024, bytes / 100);
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
            throw new StorageMigrationException(StorageMigrationProblem.Unsupported, "A local NTFS volume is required.");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!drive.IsReady)
                throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable, "Unlock or reconnect the drive.");
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new StorageMigrationException(StorageMigrationProblem.Unsupported, "A local NTFS volume is required.");
        }
        catch (Exception ex) when (ex is not StorageMigrationException && ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageMigrationException(StorageMigrationProblem.VolumeUnavailable, "Unlock or reconnect the drive.", ex);
        }
    }

    private static bool PathPresent(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
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
}
