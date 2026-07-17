using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DeskBoxDataBackupService
{
    private const int BackupSchemaVersion = 2;
    private const int MinimumSupportedBackupSchemaVersion = 1;
    private const int MaxAutomaticSnapshotCount = 7;
    private const int MaxPreRestoreBackupCount = 5;
    private const int MaxRestoreFileCount = 100_000;
    private const long MaxRestoreFileSizeBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxRestoreTotalSizeBytes = 16L * 1024 * 1024 * 1024;
    private static readonly TimeSpan AutomaticSnapshotInterval = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions s_dataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootPath;

    public DeskBoxDataBackupService()
        : this(DeskBoxDataPathService.Current.RootPath)
    {
    }

    internal DeskBoxDataBackupService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    internal string DataDirectory => Path.Combine(_rootPath, "data");

    internal string AutomaticSnapshotDirectory => Path.Combine(_rootPath, "backups", "automatic");

    internal string PreRestoreBackupDirectory => Path.Combine(_rootPath, "backups", "pre-restore");

    internal string RestoreStagingDirectory => Path.Combine(_rootPath, "restore-staging");

    internal string PendingRestoreMarkerPath => Path.Combine(_rootPath, "restore-pending.json");

    public async Task<string?> CreateAutomaticSnapshotIfDueAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HasBackupSourceData())
            {
                return null;
            }

            Directory.CreateDirectory(AutomaticSnapshotDirectory);
            string? latestSnapshot = Directory
                .EnumerateFiles(AutomaticSnapshotDirectory, "DeskBox-Auto-*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latestSnapshot is not null &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(latestSnapshot) < AutomaticSnapshotInterval)
            {
                return null;
            }

            string snapshotPath = GetAvailableArchivePath(
                AutomaticSnapshotDirectory,
                $"DeskBox-Auto-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateArchiveCoreAsync(snapshotPath, "automatic", cancellationToken);
            PruneAutomaticSnapshots();
            App.Log($"[DataBackup] Created automatic snapshot '{snapshotPath}'.");
            return snapshotPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Automatic snapshot failed: {ex}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ExportBackupAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HasBackupSourceData())
            {
                throw new InvalidOperationException("DeskBox data directory is empty.");
            }

            Directory.CreateDirectory(destinationDirectory);
            string backupPath = GetAvailableArchivePath(
                destinationDirectory,
                $"DeskBox-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateArchiveCoreAsync(backupPath, "manual", cancellationToken);
            App.Log($"[DataBackup] Exported backup '{backupPath}'.");
            return backupPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeskBoxRestorePreparation> PrepareRestoreAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected DeskBox backup does not exist.", archivePath);
        }

        await _gate.WaitAsync(cancellationToken);
        string? stagingRoot = null;
        try
        {
            DeletePendingRestoreCore();
            stagingRoot = Path.Combine(RestoreStagingDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);

            RestoreArchiveInfo archiveInfo = await ExtractAndValidateRestoreArchiveAsync(
                archivePath,
                stagingRoot,
                cancellationToken);
            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            await RebaseManagedAttachmentPathsAsync(
                stagedDataDirectory,
                archiveInfo.Manifest.SourceDataPath,
                cancellationToken);
            ValidateRestoreData(stagedDataDirectory);

            var marker = new PendingRestoreMarker(
                stagingRoot,
                archivePath,
                DateTimeOffset.UtcNow,
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion);
            await WriteJsonAtomicallyAsync(PendingRestoreMarkerPath, marker, cancellationToken);
            App.Log($"[DataBackup] Prepared restore from '{archivePath}'.");
            return new DeskBoxRestorePreparation(
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                archiveInfo.FileCount,
                archiveInfo.TotalUncompressedBytes,
                archiveInfo.Manifest.SchemaVersion,
                archiveInfo.Manifest.SchemaVersion >= 2);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(stagingRoot))
            {
                TryDeleteDirectory(stagingRoot);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DeletePendingRestoreCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<DeskBoxRestoreApplyResult> ApplyPendingRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? rollbackRoot = null;
        try
        {
            if (!File.Exists(PendingRestoreMarkerPath))
            {
                return DeskBoxRestoreApplyResult.NoPendingRestore;
            }

            PendingRestoreMarker marker = await ReadPendingRestoreMarkerAsync(cancellationToken);
            string stagingRoot = Path.GetFullPath(marker.StagingRoot);
            if (!IsPathInsideDirectory(stagingRoot, RestoreStagingDirectory))
            {
                throw new InvalidDataException("The pending restore staging path is invalid.");
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            ValidateRestoreData(stagedDataDirectory);

            if (HasBackupSourceData())
            {
                Directory.CreateDirectory(PreRestoreBackupDirectory);
                string preRestorePath = GetAvailableArchivePath(
                    PreRestoreBackupDirectory,
                    $"DeskBox-PreRestore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                await CreateArchiveCoreAsync(preRestorePath, "pre-restore", cancellationToken);
                PrunePreRestoreBackups();
                App.Log($"[DataBackup] Created pre-restore backup '{preRestorePath}'.");
            }

            rollbackRoot = Path.Combine(_rootPath, "restore-rollback", Guid.NewGuid().ToString("N"));
            string rollbackDataDirectory = Path.Combine(rollbackRoot, "data");
            Directory.CreateDirectory(rollbackRoot);
            if (Directory.Exists(DataDirectory))
            {
                Directory.Move(DataDirectory, rollbackDataDirectory);
            }

            try
            {
                Directory.Move(stagedDataDirectory, DataDirectory);
            }
            catch
            {
                if (!Directory.Exists(DataDirectory) && Directory.Exists(rollbackDataDirectory))
                {
                    Directory.Move(rollbackDataDirectory, DataDirectory);
                }

                throw;
            }

            TryDeleteFile(PendingRestoreMarkerPath);
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(rollbackRoot);
            App.Log($"[DataBackup] Applied pending restore from '{marker.ArchivePath}'.");
            return new DeskBoxRestoreApplyResult(true, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Pending restore failed: {ex}");
            DeletePendingRestoreCore();
            return new DeskBoxRestoreApplyResult(true, false, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(rollbackRoot) &&
                Directory.Exists(rollbackRoot) &&
                Directory.Exists(DataDirectory))
            {
                TryDeleteDirectory(rollbackRoot);
            }

            _gate.Release();
        }
    }

    private async Task<RestoreArchiveInfo> ExtractAndValidateRestoreArchiveAsync(
        string archivePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? manifestEntry = archive.Entries.SingleOrDefault(entry =>
            string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("The backup manifest is missing.");
        }

        if (manifestEntry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The backup manifest is too large.");
        }

        DeskBoxBackupManifest manifest;
        await using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DeskBoxBackupManifest>(
                           manifestStream,
                           s_jsonOptions,
                           cancellationToken) ??
                       throw new InvalidDataException("The backup manifest is invalid.");
        }

        if (manifest.SchemaVersion < MinimumSupportedBackupSchemaVersion ||
            manifest.SchemaVersion > BackupSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported DeskBox backup schema version {manifest.SchemaVersion}.");
        }

        if (IsBackupFromNewerApp(manifest.AppVersion))
        {
            throw new InvalidDataException(
                $"This backup was created by newer DeskBox version {manifest.AppVersion}.");
        }

        string destinationRoot = EnsureTrailingDirectorySeparator(
            Path.GetFullPath(Path.Combine(stagingRoot, "data")));
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new Dictionary<string, DeskBoxBackupFileManifest>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        long totalUncompressedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.FullName.Contains('\\') ||
                (!entry.FullName.StartsWith("data/", StringComparison.Ordinal) &&
                 !string.Equals(entry.FullName, "data", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Unexpected backup entry '{entry.FullName}'.");
            }

            string destinationPath = Path.GetFullPath(
                Path.Combine(stagingRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            string dataRootPath = destinationRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.Equals(destinationPath, dataRootPath, StringComparison.OrdinalIgnoreCase) &&
                !destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe backup entry '{entry.FullName}'.");
            }

            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (string.Equals(entry.FullName, "data", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The backup data root entry must be a directory.");
            }

            fileCount++;
            if (fileCount > MaxRestoreFileCount || entry.Length > MaxRestoreFileSizeBytes)
            {
                throw new InvalidDataException("The backup contains too many files or an oversized file.");
            }

            totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
            if (totalUncompressedBytes > MaxRestoreTotalSizeBytes)
            {
                throw new InvalidDataException("The expanded backup is too large.");
            }

            if (!extractedPaths.Add(destinationPath))
            {
                throw new InvalidDataException($"Duplicate backup entry '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using Stream source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            (long extractedLength, string sha256) = await CopyAndHashAsync(
                source,
                destination,
                cancellationToken);
            string relativePath = entry.FullName["data/".Length..];
            extractedFiles[relativePath] = new DeskBoxBackupFileManifest(
                relativePath,
                extractedLength,
                sha256);
        }

        if (fileCount == 0)
        {
            throw new InvalidDataException("The backup contains no DeskBox data files.");
        }

        if (manifest.SchemaVersion >= 2)
        {
            ValidateIntegrityManifest(manifest.Files, extractedFiles);
        }

        ValidateRestoreData(Path.Combine(stagingRoot, "data"));
        return new RestoreArchiveInfo(manifest, fileCount, totalUncompressedBytes);
    }

    private static void ValidateRestoreData(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory) ||
            !Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("The backup data directory is empty.");
        }

        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            throw new InvalidDataException("The backup is missing settings.json.");
        }

        ValidateJsonFileIfPresent<AppSettings>(settingsPath);
        ValidateJsonFileIfPresent<QuickCaptureStoreData>(
            Path.Combine(dataDirectory, "quick-capture", "quick-capture.json"));

        string widgetsDirectory = Path.Combine(dataDirectory, "widgets");
        if (Directory.Exists(widgetsDirectory))
        {
            foreach (string todoPath in Directory.EnumerateFiles(
                         widgetsDirectory,
                         "todo.json",
                         SearchOption.AllDirectories))
            {
                ValidateJsonFileIfPresent<TodoWidgetData>(todoPath);
            }
        }
    }

    private static void ValidateJsonFileIfPresent<T>(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (JsonSerializer.Deserialize<T>(json, s_dataJsonOptions) is null)
            {
                throw new JsonException("The JSON document contains null.");
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Backup data file '{Path.GetFileName(path)}' is invalid.",
                ex);
        }
    }

    private async Task RebaseManagedAttachmentPathsAsync(
        string stagedDataDirectory,
        string? sourceDataPath,
        CancellationToken cancellationToken)
    {
        string quickCapturePath = Path.Combine(
            stagedDataDirectory,
            "quick-capture",
            "quick-capture.json");
        if (File.Exists(quickCapturePath))
        {
            await RebaseQuickCaptureFileAsync(
                quickCapturePath,
                stagedDataDirectory,
                sourceDataPath,
                cancellationToken);
            string backupPath = ResilientJsonStore.GetBackupPath(quickCapturePath);
            if (File.Exists(backupPath))
            {
                try
                {
                    await RebaseQuickCaptureFileAsync(
                        backupPath,
                        stagedDataDirectory,
                        sourceDataPath,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    App.Log($"[DataBackup] Skipped invalid Quick Capture backup store: {ex.Message}");
                }
            }
        }

        string widgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
        if (!Directory.Exists(widgetsDirectory))
        {
            return;
        }

        foreach (string todoPath in Directory.EnumerateFiles(
                     widgetsDirectory,
                     "todo.json",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RebaseTodoFileAsync(
                todoPath,
                stagedDataDirectory,
                sourceDataPath,
                cancellationToken);
            string backupPath = ResilientJsonStore.GetBackupPath(todoPath);
            if (File.Exists(backupPath))
            {
                try
                {
                    await RebaseTodoFileAsync(
                        backupPath,
                        stagedDataDirectory,
                        sourceDataPath,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    App.Log($"[DataBackup] Skipped invalid Todo backup store: {ex.Message}");
                }
            }
        }
    }

    private async Task RebaseQuickCaptureFileAsync(
        string path,
        string stagedDataDirectory,
        string? sourceDataPath,
        CancellationToken cancellationToken)
    {
        QuickCaptureStoreData data = JsonSerializer.Deserialize<QuickCaptureStoreData>(
                                         await File.ReadAllTextAsync(path, cancellationToken),
                                         s_dataJsonOptions) ??
                                     throw new InvalidDataException("Quick Capture backup data is invalid.");
        foreach (QuickCaptureItem item in (data.Items ?? []).Concat(data.RecentItems ?? []))
        {
            var rebasedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TodoAttachment attachment in (item.Attachments ?? []).Where(attachment =>
                         attachment is not null && attachment.IsManagedCopy))
            {
                string? rebasedPath = TryRebaseManagedPath(
                    attachment.FilePath,
                    sourceDataPath,
                    stagedDataDirectory,
                    "quick-capture");
                if (rebasedPath is not null)
                {
                    rebasedPaths[attachment.FilePath] = rebasedPath;
                    attachment.FilePath = rebasedPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.ImagePath))
            {
                if (rebasedPaths.TryGetValue(item.ImagePath, out string? rebasedImagePath))
                {
                    item.ImagePath = rebasedImagePath;
                }
                else
                {
                    item.ImagePath = TryRebaseManagedPath(
                        item.ImagePath,
                        sourceDataPath,
                        stagedDataDirectory,
                        "quick-capture") ?? item.ImagePath;
                }
            }
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(data, s_dataJsonOptions),
            cancellationToken);
    }

    private async Task RebaseTodoFileAsync(
        string path,
        string stagedDataDirectory,
        string? sourceDataPath,
        CancellationToken cancellationToken)
    {
        TodoWidgetData data = JsonSerializer.Deserialize<TodoWidgetData>(
                                  await File.ReadAllTextAsync(path, cancellationToken),
                                  s_dataJsonOptions) ??
                              throw new InvalidDataException("Todo backup data is invalid.");
        string storeRelativePath = Path.GetRelativePath(
                stagedDataDirectory,
                Path.GetDirectoryName(path)!)
            .Replace(Path.DirectorySeparatorChar, '/');
        foreach (TodoAttachment attachment in (data.Items ?? [])
                     .SelectMany(item => item.Attachments ?? [])
                     .Where(attachment => attachment is not null && attachment.IsManagedCopy))
        {
            attachment.FilePath = TryRebaseManagedPath(
                                      attachment.FilePath,
                                      sourceDataPath,
                                      stagedDataDirectory,
                                      storeRelativePath) ??
                                  attachment.FilePath;
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(data, s_dataJsonOptions),
            cancellationToken);
    }

    private string? TryRebaseManagedPath(
        string? originalPath,
        string? sourceDataPath,
        string stagedDataDirectory,
        string fallbackStoreRelativePath)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return null;
        }

        string? relativePath = null;
        if (!string.IsNullOrWhiteSpace(sourceDataPath) &&
            TryGetRelativePathInsideDirectory(originalPath, sourceDataPath, out string sourceRelativePath))
        {
            relativePath = sourceRelativePath;
        }

        relativePath ??= TryGetStoreRelativePath(originalPath, fallbackStoreRelativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string stagedPath = Path.GetFullPath(Path.Combine(stagedDataDirectory, relativePath));
        if (!IsPathInsideDirectory(stagedPath, stagedDataDirectory) || !File.Exists(stagedPath))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
    }

    private static string? TryGetStoreRelativePath(string originalPath, string storeRelativePath)
    {
        string normalizedOriginal = originalPath.Replace('\\', '/');
        string normalizedStore = storeRelativePath.Trim('/').Replace('\\', '/');
        int storeIndex = normalizedOriginal.IndexOf(
            $"/{normalizedStore}/",
            StringComparison.OrdinalIgnoreCase);
        if (storeIndex < 0)
        {
            return null;
        }

        return normalizedOriginal[(storeIndex + 1)..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool TryGetRelativePathInsideDirectory(
        string path,
        string directory,
        out string relativePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory);
            if (IsPathInsideDirectory(fullPath, fullDirectory))
            {
                relativePath = Path.GetRelativePath(fullDirectory, fullPath);
                return true;
            }
        }
        catch
        {
        }

        relativePath = string.Empty;
        return false;
    }

    private async Task<PendingRestoreMarker> ReadPendingRestoreMarkerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(PendingRestoreMarkerPath, cancellationToken);
            return JsonSerializer.Deserialize<PendingRestoreMarker>(json, s_jsonOptions) ??
                   throw new InvalidDataException("The pending restore marker is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pending restore marker is invalid.", ex);
        }
    }

    private void DeletePendingRestoreCore()
    {
        if (File.Exists(PendingRestoreMarkerPath))
        {
            try
            {
                string json = File.ReadAllText(PendingRestoreMarkerPath);
                PendingRestoreMarker? marker = JsonSerializer.Deserialize<PendingRestoreMarker>(
                    json,
                    s_jsonOptions);
                if (marker is not null &&
                    IsPathInsideDirectory(marker.StagingRoot, RestoreStagingDirectory))
                {
                    TryDeleteDirectory(marker.StagingRoot);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DataBackup] Failed to read pending restore while cancelling: {ex.Message}");
            }
        }

        TryDeleteFile(PendingRestoreMarkerPath);
        TryDeleteDirectory(RestoreStagingDirectory);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(value, s_jsonOptions),
                cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private bool HasBackupSourceData()
    {
        return Directory.Exists(DataDirectory) &&
            Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories).Any();
    }

    private async Task CreateArchiveCoreAsync(
        string archivePath,
        string backupKind,
        CancellationToken cancellationToken)
    {
        string settingsPath = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            throw new InvalidOperationException("DeskBox settings are not available for backup.");
        }

        (string SourcePath, string RelativePath)[] sourceFiles = Directory
            .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (
                SourcePath: path,
                RelativePath: Path.GetRelativePath(DataDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => ShouldIncludeInBackup(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string tempArchivePath = $"{archivePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var output = new FileStream(
                             tempArchivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var fileManifest = new List<DeskBoxBackupFileManifest>(sourceFiles.Length);
                    foreach ((string sourcePath, string relativePath) in sourceFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ZipArchiveEntry entry = archive.CreateEntry($"data/{relativePath}", CompressionLevel.Fastest);
                        await using var source = new FileStream(
                            sourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: 81920,
                            useAsync: true);
                        await using Stream destination = entry.Open();
                        (long length, string sha256) = await CopyAndHashAsync(
                            source,
                            destination,
                            cancellationToken);
                        fileManifest.Add(new DeskBoxBackupFileManifest(relativePath, length, sha256));
                    }

                    var manifest = new DeskBoxBackupManifest(
                        BackupSchemaVersion,
                        backupKind,
                        DateTimeOffset.UtcNow,
                        typeof(DeskBoxDataBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                        DataDirectory,
                        fileManifest);
                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using (Stream manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            manifest,
                            s_jsonOptions,
                            cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            File.Move(tempArchivePath, archivePath, overwrite: false);
        }
        finally
        {
            TryDeleteFile(tempArchivePath);
        }
    }

    private void PruneAutomaticSnapshots()
    {
        foreach (string obsoletePath in Directory
                     .EnumerateFiles(AutomaticSnapshotDirectory, "DeskBox-Auto-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxAutomaticSnapshotCount))
        {
            TryDeleteFile(obsoletePath);
        }
    }

    private void PrunePreRestoreBackups()
    {
        foreach (string obsoletePath in Directory
                     .EnumerateFiles(PreRestoreBackupDirectory, "DeskBox-PreRestore-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxPreRestoreBackupCount))
        {
            TryDeleteFile(obsoletePath);
        }
    }

    private static string GetAvailableArchivePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string directoryPrefix = EnsureTrailingDirectorySeparator(Path.GetFullPath(directory));
            return fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldIncludeInBackup(string relativePath)
    {
        if (relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !relativePath.StartsWith("quick-capture/thumbnails/", StringComparison.OrdinalIgnoreCase) &&
               !relativePath.StartsWith("quick-capture/exports/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, bytesRead);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytes = checked(totalBytes + bytesRead);
        }

        return (totalBytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void ValidateIntegrityManifest(
        IReadOnlyList<DeskBoxBackupFileManifest>? expectedFiles,
        IReadOnlyDictionary<string, DeskBoxBackupFileManifest> extractedFiles)
    {
        if (expectedFiles is null || expectedFiles.Count == 0)
        {
            throw new InvalidDataException("The backup integrity manifest is missing or empty.");
        }

        var expectedByPath = new Dictionary<string, DeskBoxBackupFileManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (DeskBoxBackupFileManifest expected in expectedFiles)
        {
            if (expected is null ||
                string.IsNullOrWhiteSpace(expected.Path) ||
                expected.Path.Contains('\\') ||
                expected.Path.StartsWith("/", StringComparison.Ordinal) ||
                expected.Path.Split('/').Any(segment => segment is "" or "." or "..") ||
                !expectedByPath.TryAdd(expected.Path, expected))
            {
                throw new InvalidDataException("The backup integrity manifest contains an invalid path.");
            }

            if (expected.Length < 0 ||
                string.IsNullOrWhiteSpace(expected.Sha256) ||
                expected.Sha256.Length != 64 ||
                !expected.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    $"The backup integrity entry for '{expected.Path}' is invalid.");
            }
        }

        if (expectedByPath.Count != extractedFiles.Count)
        {
            throw new InvalidDataException("The backup file list does not match its integrity manifest.");
        }

        foreach ((string path, DeskBoxBackupFileManifest actual) in extractedFiles)
        {
            if (!expectedByPath.TryGetValue(path, out DeskBoxBackupFileManifest? expected) ||
                expected.Length != actual.Length ||
                !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup integrity validation failed for '{path}'.");
            }
        }
    }

    private static bool IsBackupFromNewerApp(string? backupVersion)
    {
        string? currentVersion = typeof(DeskBoxDataBackupService).Assembly.GetName().Version?.ToString();
        return TryParseVersion(backupVersion, out Version? backup) &&
               TryParseVersion(currentVersion, out Version? current) &&
               backup > current;
    }

    private static bool TryParseVersion(string? value, out Version? version)
    {
        string normalized = (value ?? string.Empty).Split(['-', '+'], 2)[0];
        return Version.TryParse(normalized, out version);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private sealed record DeskBoxBackupManifest(
        int SchemaVersion,
        string Kind,
        DateTimeOffset CreatedAtUtc,
        string AppVersion,
        string? SourceDataPath = null,
        IReadOnlyList<DeskBoxBackupFileManifest>? Files = null);

    private sealed record DeskBoxBackupFileManifest(
        string Path,
        long Length,
        string Sha256);

    private sealed record RestoreArchiveInfo(
        DeskBoxBackupManifest Manifest,
        int FileCount,
        long TotalUncompressedBytes);

    private sealed record PendingRestoreMarker(
        string StagingRoot,
        string ArchivePath,
        DateTimeOffset PreparedAtUtc,
        DateTimeOffset BackupCreatedAtUtc,
        string AppVersion);
}

public sealed record DeskBoxRestorePreparation(
    DateTimeOffset BackupCreatedAtUtc,
    string AppVersion,
    int FileCount,
    long TotalUncompressedBytes,
    int BackupSchemaVersion,
    bool HasIntegrityManifest);

internal sealed record DeskBoxRestoreApplyResult(
    bool HadPendingRestore,
    bool Succeeded,
    string? ErrorMessage)
{
    public static DeskBoxRestoreApplyResult NoPendingRestore { get; } = new(false, true, null);
}
