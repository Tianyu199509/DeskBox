using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Core.Persistence;
using DeskBox.FileSafety;
using DeskBox.Models;

using DeskBox.Contracts;

namespace DeskBox.Services;

public sealed partial class DeskBoxDataBackupService
{
    private const int BackupSchemaVersion = 2;
    private const int MinimumSupportedBackupSchemaVersion = 1;
    private const int MaxPreRestoreBackupCount = 5;
    private const int MaxRestoreFileCount = 100_000;
    // The widget-style document is a small projected JSON (~60 scalar
    // fields); 8 MiB is far beyond any legitimate document.
    private const long MaxWidgetStyleEntryBytes = 8L * 1024 * 1024;
    private const long MaxRestoreFileSizeBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxRestoreTotalSizeBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxSnapshotCopyAttempts = 4;
    private const int MaxScopedRestoreApplyAttempts = 3;
    /// <summary>
    /// <see cref="Exception.Data"/> key stamped on the InvalidDataException
    /// <summary>Stamped on the
    /// InvalidDataException for an unsupported schema version so the UI can
    /// localize the failure. Carries the unsupported version number.</summary>
    public const string BackupSchemaVersionDataKey = "DeskBox.BackupSchemaVersion";
    /// <summary>Stamped on InvalidDataException when the archive lacks a usable
    /// manifest — i.e. the picked file is not a DeskBox backup at all.</summary>
    public const string BackupManifestInvalidDataKey = "DeskBox.BackupManifestInvalid";
    private static readonly SettingsJsonContext s_settingsDataJsonContext =
        new(CreateDataJsonOptions());
    private static readonly QuickCaptureJsonContext s_quickCaptureDataJsonContext =
        new(CreateDataJsonOptions());
    private static readonly TodoJsonContext s_todoDataJsonContext =
        new(CreateDataJsonOptions());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootPath;
    private readonly string _recoveryRootPath;
    private volatile AutomaticBackupOptions _automaticBackupOptions = AutomaticBackupOptions.Default;
    private volatile string? _lastAutomaticSnapshotFallbackMessage;

    /// <summary>
    /// Current automatic-backup schedule and folder policy. Updated from live
    /// settings; the default matches the pre-setting behavior (daily, 7 kept,
    /// default recovery folder).
    /// </summary>
    public AutomaticBackupOptions AutomaticBackupOptions => _automaticBackupOptions;

    /// <summary>
    /// Set when the most recent automatic snapshot had to fall back from the
    /// configured custom directory to the default recovery directory; cleared
    /// once a snapshot succeeds in the configured directory again.
    /// </summary>
    public string? LastAutomaticSnapshotFallbackMessage => _lastAutomaticSnapshotFallbackMessage;

    /// <summary>
    /// Raised once per fallback streak when an automatic snapshot falls back
    /// from the configured custom directory to the default recovery directory.
    /// </summary>
    public event Action? AutomaticSnapshotFallbackDetected;

    public void UpdateAutomaticBackupOptions(AutomaticBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _automaticBackupOptions = options;
        if (options.CustomDirectory is null)
        {
            _lastAutomaticSnapshotFallbackMessage = null;
        }
    }

    public DeskBoxDataBackupService()
        : this(
            DeskBoxDataPathService.Current.RootPath,
            DeskBoxDataPathService.Current.RecoveryDirectory)
    {
    }

    internal DeskBoxDataBackupService(string rootPath, string? recoveryRootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _recoveryRootPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(recoveryRootPath)
                ? Path.Combine(Path.GetDirectoryName(_rootPath) ?? _rootPath, "DeskBox-Recovery")
                : recoveryRootPath);
    }

    internal string DataDirectory => Path.Combine(_rootPath, "data");

    /// <summary>
    /// Automatic snapshots are retained outside the app-data root so they
    /// survive normal uninstall and can be restored after reinstall.
    /// </summary>
    internal string AutomaticSnapshotDirectory => Path.Combine(_recoveryRootPath, "automatic");

    /// <summary>
    /// Directory the next automatic snapshot will be written to: the
    /// configured custom directory when it is usable, otherwise the default
    /// recovery directory. Only the directory shape is validated here; write
    /// access is probed when a snapshot is actually created.
    /// </summary>
    public string EffectiveAutomaticSnapshotDirectory =>
        ResolveAutomaticSnapshotTarget(probeWriteAccess: false).Directory;

    /// <summary>
    /// Folder status for the settings UI: which custom directory is configured
    /// and whether it would be used right now. Missing directories are still
    /// reported as active because they are created on demand.
    /// </summary>
    public AutomaticBackupDirectoryStatus GetAutomaticBackupDirectoryStatus()
    {
        AutomaticSnapshotTarget target = ResolveAutomaticSnapshotTarget(probeWriteAccess: false);
        return new AutomaticBackupDirectoryStatus(
            _automaticBackupOptions.CustomDirectory,
            target.Directory,
            target.Directory != AutomaticSnapshotDirectory);
    }

    /// <summary>
    /// Validates a user-selected folder for automatic snapshots. Folders inside
    /// the app-data root are rejected because snapshots would then back up the
    /// recovery copies of previous snapshots.
    /// </summary>
    public bool IsValidCustomAutomaticBackupDirectory(string path, out string? rejectionReasonKey)
    {
        rejectionReasonKey = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (IsPathInsideDirectory(fullPath, _rootPath))
            {
                rejectionReasonKey = "Settings.DataBackup.AutomaticBackupDirectory.InvalidInsideDataRoot";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejectionReasonKey = "Settings.DataBackup.AutomaticBackupDirectory.InvalidPath";
            return false;
        }
    }

    private readonly record struct AutomaticSnapshotTarget(string Directory, bool UsedFallback);

    private AutomaticSnapshotTarget ResolveAutomaticSnapshotTarget(bool probeWriteAccess)
    {
        string? customDirectory = _automaticBackupOptions.CustomDirectory;
        if (string.IsNullOrWhiteSpace(customDirectory))
        {
            return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: false);
        }

        try
        {
            string fullPath = Path.GetFullPath(customDirectory.Trim());
            if (IsPathInsideDirectory(fullPath, _rootPath))
            {
                App.Log(
                    $"[DataBackup] Custom backup directory '{fullPath}' is inside the DeskBox data root; " +
                    "using the default recovery directory instead.");
                return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
            }

            // A file occupying the path can never become the snapshot directory.
            if (File.Exists(fullPath))
            {
                App.Log(
                    $"[DataBackup] Custom backup directory '{fullPath}' is a file; " +
                    "using the default recovery directory instead.");
                return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
            }

            if (probeWriteAccess)
            {
                Directory.CreateDirectory(fullPath);
                // Probe write access so read-only locations fall back to the
                // default directory instead of failing every snapshot attempt.
                string probePath = Path.Combine(
                    fullPath,
                    $".deskbox-backup-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probePath, string.Empty);
                TryDeleteFile(probePath);
            }

            return new AutomaticSnapshotTarget(fullPath, UsedFallback: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log(
                $"[DataBackup] Custom backup directory '{customDirectory}' is not usable ({ex.Message}); " +
                "using the default recovery directory instead.");
            return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
        }
    }

    // Keep snapshots written by builds released before recovery isolation
    // visible and restorable during the transition.
    internal string LegacyAutomaticSnapshotDirectory => Path.Combine(_rootPath, "backups", "automatic");

    internal string PreRestoreBackupDirectory => Path.Combine(_rootPath, "backups", "pre-restore");

    internal string RestoreStagingDirectory => Path.Combine(_rootPath, "restore-staging");

    internal string BackupSnapshotStagingDirectory => Path.Combine(_rootPath, "backup-staging");

    internal string PendingRestoreMarkerPath => Path.Combine(_rootPath, "restore-pending.json");

    public async Task<string?> CreateAutomaticSnapshotIfDueAsync(
        CancellationToken cancellationToken = default)
    {
        return await CreateAutomaticSnapshotAsync(force: false, cancellationToken);
    }

    public async Task<string?> CreateAutomaticSnapshotNowAsync(
        CancellationToken cancellationToken = default)
    {
        return await CreateAutomaticSnapshotAsync(force: true, cancellationToken);
    }

    private async Task<string?> CreateAutomaticSnapshotAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        return (await CreateAutomaticSnapshotResultAsync(force, cancellationToken)).ArchivePath;
    }

    public async Task<LocalBackupResult> CreateAutomaticSnapshotResultAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AutomaticBackupOptions options = _automaticBackupOptions;
            // "Back up now" (force) stays available even when the schedule is off.
            if (!options.IsEnabled && !force)
            {
                return new(BackupOutcome.Skipped);
            }

            if (!HasBackupSourceData())
            {
                return new(BackupOutcome.Skipped);
            }

            AutomaticSnapshotTarget target = ResolveAutomaticSnapshotTarget(probeWriteAccess: true);
            Directory.CreateDirectory(target.Directory);
            string? latestSnapshot = Directory
                .EnumerateFiles(target.Directory, "DeskBox-Auto-*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!force && latestSnapshot is not null &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(latestSnapshot) <
                    TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)))
            {
                return new(BackupOutcome.Skipped);
            }

            string snapshotPath = GetAvailableArchivePath(
                target.Directory,
                $"DeskBox-Auto-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateArchiveCoreAsync(snapshotPath, "automatic", cancellationToken);
            PruneAutomaticSnapshots(target.Directory, options.RetentionCount);
            if (target.UsedFallback)
            {
                string fallbackMessage =
                    $"Custom backup directory '{options.CustomDirectory}' was unavailable; " +
                    $"snapshot saved to '{target.Directory}'.";
                bool firstFallbackInStreak = _lastAutomaticSnapshotFallbackMessage is null;
                _lastAutomaticSnapshotFallbackMessage = fallbackMessage;
                if (firstFallbackInStreak)
                {
                    AutomaticSnapshotFallbackDetected?.Invoke();
                }
            }
            else
            {
                _lastAutomaticSnapshotFallbackMessage = null;
            }

            App.Log($"[DataBackup] Created automatic snapshot '{snapshotPath}'.");
            return new(BackupOutcome.Succeeded, snapshotPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Automatic snapshot failed: {ex}");
            return new(BackupOutcome.Failed, Error: ex.Message);
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

    /// <summary>
    /// Domain-scoped cloud backup (roadmap §10): archives only the enabled
    /// domains' data files, plus widget-style.json when the WidgetStyle
    /// domain is on. The manifest carries the domain list so the archive
    /// can never be mistaken for a full backup.
    /// </summary>
    /// <param name="widgetStyleProvider">
    /// Supplies the widget-style document bytes when the scope includes
    /// <see cref="CloudBackupDomain.WidgetStyle"/>; the service itself does
    /// not hold a settings reference, so the caller projects live settings.
    /// </param>
    public async Task<string> ExportScopedBackupAsync(
        string destinationDirectory,
        CloudBackupDomain scope,
        Func<CancellationToken, Task<byte[]?>>? widgetStyleProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (scope == CloudBackupDomain.None)
        {
            throw new ArgumentException("At least one backup domain must be enabled.", nameof(scope));
        }

        await _gate.WaitAsync(cancellationToken);
        string? snapshotRoot = null;
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            snapshotRoot = Path.Combine(BackupSnapshotStagingDirectory, $"scoped-{Guid.NewGuid():N}");
            string snapshotDataDirectory = Path.Combine(snapshotRoot, "data");
            Directory.CreateDirectory(snapshotDataDirectory);

            await CreateScopedDataSnapshotAsync(snapshotDataDirectory, scope, cancellationToken);

            IReadOnlyDictionary<string, byte[]>? rootEntries = null;
            if (scope.HasFlag(CloudBackupDomain.WidgetStyle) && widgetStyleProvider is not null)
            {
                byte[]? styleDocument = await widgetStyleProvider(cancellationToken);
                if (styleDocument is { Length: > 0 })
                {
                    rootEntries = new Dictionary<string, byte[]>
                    {
                        [CloudBackupDomains.WidgetStyleEntryName] = styleDocument
                    };
                }
            }

            string backupPath = GetAvailableArchivePath(
                destinationDirectory,
                $"DeskBox-CloudBackup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            // The manifest must only claim domains that actually shipped —
            // WidgetStyle without a produced document does not count.
            CloudBackupDomain shippedScope = rootEntries is null
                ? scope & ~CloudBackupDomain.WidgetStyle
                : scope;
            if (shippedScope == CloudBackupDomain.None)
            {
                throw new InvalidOperationException(
                    "The scoped backup would be empty — no domain produced content.");
            }
            await CreateArchiveFromSnapshotAsync(
                backupPath,
                CloudBackupDomains.BackupKind,
                snapshotDataDirectory,
                cancellationToken,
                CloudBackupDomains.ToManifestNames(shippedScope),
                rootEntries,
                DeviceIdentity.Id);
            App.Log($"[DataBackup] Exported scoped backup '{backupPath}' (domains: {shippedScope}).");
            return backupPath;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(snapshotRoot))
            {
                TryDeleteDirectory(snapshotRoot);
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Copies only the enabled file domains' paths into the snapshot
    /// staging dir. No FileSafety metadata and no OperationGate barrier
    /// here: scoped domains never carry settings/history/journal, and
    /// widget stores are not transaction files — the per-file stable copy
    /// is the existing semantic.
    /// </summary>
    private async Task CreateScopedDataSnapshotAsync(
        string snapshotDataDirectory,
        CloudBackupDomain scope,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(DataDirectory))
        {
            return;
        }

        // Ship only todo stores a live widget actually reads. Orphan
        // widgets/<id>/ dirs (deleted widgets, leftovers of earlier unmapped
        // restores) would otherwise ride along in every backup and poison
        // the destination's remap: each foreign id lands as data no widget
        // displays. An empty live set means "cannot tell" — keep everything
        // rather than silently drop data.
        HashSet<string>? liveTodoIds = null;
        if (scope.HasFlag(CloudBackupDomain.TodoData))
        {
            HashSet<string> live = await ReadLiveTodoWidgetIdsAsync(cancellationToken);
            if (live.Count > 0)
            {
                liveTodoIds = live;
            }
        }

        foreach (string sourcePath in Directory
                     .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(DataDirectory, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!CloudBackupDomains.IsInScope(scope, relativePath))
            {
                continue;
            }

            if (liveTodoIds is not null &&
                CloudBackupDomains.TryGetTodoWidgetId(relativePath) is string todoWidgetId &&
                !liveTodoIds.Contains(todoWidgetId))
            {
                continue;
            }

            App.MarkStartupProgress();
            string destinationPath = Path.Combine(
                snapshotDataDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyStableSnapshotFileAsync(sourcePath, destinationPath, cancellationToken);
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
            if (archiveInfo.Manifest.Domains is { Count: > 0 })
            {
                // A scoped cloud backup holds only its domains — the
                // whole-directory swap below would wipe everything else.
                throw new InvalidDataException(
                    "This is a scoped cloud backup; restore it through cloud restore.");
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            await RebaseManagedAttachmentPathsAsync(
                stagedDataDirectory,
                archiveInfo.Manifest.SourceDataPath,
                todoWidgetIdRemaps: null,
                cancellationToken);
            // Pre-restore archives are the safety net for restores: they can
            // be created without settings.json, so they must also be
            // restorable without it. Manual/automatic snapshots stay strict.
            bool allowMissingSettings = string.Equals(
                archiveInfo.Manifest.Kind, "pre-restore", StringComparison.Ordinal);
            ValidateRestoreData(
                stagedDataDirectory,
                requireSettings: !allowMissingSettings);

            var marker = new PendingRestoreMarker(
                stagingRoot,
                archivePath,
                DateTimeOffset.UtcNow,
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                Domains: null,
                AllowMissingSettings: allowMissingSettings);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker,
                cancellationToken);
            App.Log($"[DataBackup] Prepared restore from '{archivePath}'.");
            return new DeskBoxRestorePreparation(
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                archiveInfo.FileCount,
                archiveInfo.TotalUncompressedBytes,
                archiveInfo.Manifest.SchemaVersion,
                archiveInfo.Manifest.SchemaVersion >= 2,
                IsFromNewerAppVersion: IsBackupFromNewerApp(archiveInfo.Manifest.AppVersion));
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

    /// <summary>
    /// Scoped cloud restore (roadmap §10): stages a cloud backup and marks
    /// it pending with the domain list. Apply replaces ONLY the staged
    /// domains' files in the live data directory — every other path stays
    /// untouched, and the marker makes a mid-apply crash retry-safe.
    /// </summary>
    /// <param name="requestedDomains">
    /// Domains the user asked to restore; the applied scope is the
    /// intersection with the archive's manifest domains (an archive lacking
    /// a requested domain simply cannot provide it).
    /// </param>
    public async Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(
        string archivePath,
        CloudBackupDomain requestedDomains,
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

            CloudBackupDomain manifestScope = CloudBackupDomains.FromManifestNames(
                archiveInfo.Manifest.Domains);
            if (manifestScope == CloudBackupDomain.None)
            {
                throw new InvalidDataException(
                    "This is not a scoped cloud backup; use the full restore flow.");
            }

            CloudBackupDomain appliedScope = manifestScope & requestedDomains;
            if (appliedScope == CloudBackupDomain.None)
            {
                throw new InvalidDataException(
                    "The backup does not contain any of the requested domains.");
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            if (Directory.Exists(stagedDataDirectory))
            {
                // The archive must not carry files outside its own manifest
                // domains — a forged manifest could otherwise make the
                // scoped apply overwrite non-domain data. Files from a
                // manifest domain the user did not request are staged but
                // simply not applied.
                foreach (string stagedFile in Directory.EnumerateFiles(
                             stagedDataDirectory, "*", SearchOption.AllDirectories))
                {
                    string stagedRelative = Path
                        .GetRelativePath(stagedDataDirectory, stagedFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!CloudBackupDomains.IsInScope(manifestScope, stagedRelative))
                    {
                        throw new InvalidDataException(
                            $"Scoped backup entry '{stagedRelative}' is outside the manifest domains.");
                    }
                }
            }
            // Remap order matters: plan source→target ids, move the staged
            // widget dirs, THEN rebase attachment paths — embedded FilePaths
            // carry the source id, so the rebase rewrites widgets/<source>
            // to widgets/<target> while the moved files already sit there.
            TodoRestorePlan todoPlan = appliedScope.HasFlag(CloudBackupDomain.TodoData)
                ? await PlanOrphanedTodoWidgetRemapsAsync(
                    stagingRoot, stagedDataDirectory, cancellationToken)
                : TodoRestorePlan.Empty;
            await ApplyTodoRestorePlanAsync(stagedDataDirectory, todoPlan, cancellationToken);

            // Remaps AND merges are both "source store landed on a live
            // widget" — report them together so the confirm dialog counts
            // every redirected source store.
            IReadOnlyList<DeskBoxTodoWidgetRemap> remaps =
                todoPlan.Merges.Count == 0
                    ? todoPlan.Remaps
                    : todoPlan.Remaps.Concat(todoPlan.Merges).ToList();
            IReadOnlyList<string> unmapped = todoPlan.Unmapped;
            IReadOnlyDictionary<string, string>? todoWidgetIdRemaps =
                todoPlan.Remaps.Count + todoPlan.Merges.Count > 0
                    ? todoPlan.Remaps.Concat(todoPlan.Merges)
                        .ToDictionary(
                            r => r.SourceWidgetId,
                            r => r.TargetWidgetId,
                            StringComparer.Ordinal)
                    : null;
            await RebaseManagedAttachmentPathsAsync(
                stagedDataDirectory,
                archiveInfo.Manifest.SourceDataPath,
                todoWidgetIdRemaps,
                cancellationToken);
            ValidateScopedRestoreData(stagedDataDirectory, appliedScope);
            IReadOnlyList<DeskBoxDomainItemCount> domainItemCounts =
                CountStagedDomainItems(stagedDataDirectory, appliedScope);
            int attachmentReferences =
                CountStagedAttachmentReferences(stagedDataDirectory, appliedScope);

            var marker = new PendingRestoreMarker(
                stagingRoot,
                archivePath,
                DateTimeOffset.UtcNow,
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                CloudBackupDomains.ToManifestNames(appliedScope),
                ScopedRestoreConfirmed: false);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker,
                cancellationToken);
            App.Log($"[DataBackup] Prepared scoped restore from '{archivePath}' (domains: {appliedScope}).");
            return new DeskBoxRestorePreparation(
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                archiveInfo.FileCount,
                archiveInfo.TotalUncompressedBytes,
                archiveInfo.Manifest.SchemaVersion,
                archiveInfo.Manifest.SchemaVersion >= 2,
                CloudBackupDomains.ToManifestNames(appliedScope),
                archiveInfo.Manifest.SourceDeviceId,
                remaps,
                unmapped,
                domainItemCounts,
                attachmentReferences,
                IsFromNewerAppVersion: IsBackupFromNewerApp(archiveInfo.Manifest.AppVersion));
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

    /// <summary>
    /// Scoped-archive validation: the staged data dir must hold only files
    /// inside the applied domains (a tampered archive must never reach
    /// non-domain paths), and each domain store file must parse.
    /// </summary>
    private static void ValidateScopedRestoreData(
        string stagedDataDirectory,
        CloudBackupDomain appliedScope)
    {
        foreach (CloudBackupDomain domain in CloudBackupDomains.FileDomains)
        {
            if (!appliedScope.HasFlag(domain))
            {
                continue;
            }

            if (domain == CloudBackupDomain.QuickCaptureData)
            {
                ValidateJsonFileIfPresent<QuickCaptureStoreData>(
                    Path.Combine(stagedDataDirectory, "quick-capture", "quick-capture.json"),
                    s_quickCaptureDataJsonContext.StoreData);
            }
        }

        if (!Directory.Exists(stagedDataDirectory))
        {
            return;
        }

        string widgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
        if (appliedScope.HasFlag(CloudBackupDomain.TodoData) &&
            Directory.Exists(widgetsDirectory))
        {
            foreach (string todoPath in Directory.EnumerateFiles(
                         widgetsDirectory,
                         "todo.json",
                         SearchOption.AllDirectories))
            {
                ValidateJsonFileIfPresent<TodoWidgetData>(
                    todoPath,
                    s_todoDataJsonContext.StoreData);
            }
        }
    }

    /// <summary>
    /// Live-item count per applied item domain, for the restore confirm
    /// dialog: a manifest domain can legally hold zero records (the backup
    /// was taken before any data existed), and restoring it still wipes the
    /// local domain — the preview must say so. Tombstoned items do not
    /// count. A store that fails to parse contributes zero rather than
    /// failing the restore the validation step already accepted.
    /// </summary>
    private static IReadOnlyList<DeskBoxDomainItemCount> CountStagedDomainItems(
        string stagedDataDirectory,
        CloudBackupDomain appliedScope)
    {
        var counts = new List<DeskBoxDomainItemCount>(2);

        if (appliedScope.HasFlag(CloudBackupDomain.TodoData))
        {
            int items = 0;
            string widgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
            if (Directory.Exists(widgetsDirectory))
            {
                foreach (string todoPath in Directory.EnumerateFiles(
                             widgetsDirectory,
                             "todo.json",
                             SearchOption.AllDirectories))
                {
                    items += CountLiveItems<TodoWidgetData>(
                        todoPath,
                        s_todoDataJsonContext.StoreData,
                        data => data.Items?.Count(item => item is { IsDeleted: false }) ?? 0);
                }
            }

            counts.Add(new DeskBoxDomainItemCount(
                CloudBackupDomains.ToManifestName(CloudBackupDomain.TodoData), items));
        }

        if (appliedScope.HasFlag(CloudBackupDomain.QuickCaptureData))
        {
            int items = CountLiveItems<QuickCaptureStoreData>(
                Path.Combine(stagedDataDirectory, "quick-capture", "quick-capture.json"),
                s_quickCaptureDataJsonContext.StoreData,
                data =>
                    (data.Items?.Count(item => item is { IsDeleted: false }) ?? 0) +
                    (data.RecentItems?.Count(item => item is { IsDeleted: false }) ?? 0));
            counts.Add(new DeskBoxDomainItemCount(
                CloudBackupDomains.ToManifestName(CloudBackupDomain.QuickCaptureData), items));
        }

        return counts;
    }

    private static int CountLiveItems<TData>(
        string storePath,
        JsonTypeInfo<TData> typeInfo,
        Func<TData, int> countItems)
    {
        try
        {
            if (!File.Exists(storePath))
            {
                return 0;
            }

            TData? data = JsonSerializer.Deserialize(File.ReadAllText(storePath), typeInfo);
            return data is null ? 0 : countItems(data);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            App.Log($"[DataBackup] Item count skipped for '{storePath}': {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Distinct attachment/image references inside the staged item data.
    /// Attachment files are excluded from cloud backups by design, so the
    /// confirm dialog uses this count to warn that these references will
    /// dangle on a device that does not hold the files.
    /// </summary>
    private static int CountStagedAttachmentReferences(
        string stagedDataDirectory,
        CloudBackupDomain appliedScope)
    {
        int references = 0;

        if (appliedScope.HasFlag(CloudBackupDomain.TodoData))
        {
            string widgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
            if (Directory.Exists(widgetsDirectory))
            {
                foreach (string todoPath in Directory.EnumerateFiles(
                             widgetsDirectory,
                             "todo.json",
                             SearchOption.AllDirectories))
                {
                    references += CountLiveItems<TodoWidgetData>(
                        todoPath,
                        s_todoDataJsonContext.StoreData,
                        data => (data.Items ?? []).Sum(item =>
                            item is null ? 0 : CountAttachmentPaths(item.Attachments, null)));
                }
            }
        }

        if (appliedScope.HasFlag(CloudBackupDomain.QuickCaptureData))
        {
            references += CountLiveItems<QuickCaptureStoreData>(
                Path.Combine(stagedDataDirectory, "quick-capture", "quick-capture.json"),
                s_quickCaptureDataJsonContext.StoreData,
                data => (data.Items ?? []).Concat(data.RecentItems ?? []).Sum(item =>
                    item is null ? 0 : CountAttachmentPaths(item.Attachments, item.ImagePath)));
        }

        return references;
    }

    private static int CountAttachmentPaths(
        IEnumerable<TodoAttachment>? attachments,
        string? imagePath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TodoAttachment attachment in attachments ?? [])
        {
            if (attachment is not null && !string.IsNullOrWhiteSpace(attachment.FilePath))
            {
                paths.Add(attachment.FilePath.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            paths.Add(imagePath.Trim());
        }

        return paths.Count;
    }

    /// <summary>
    /// Todo stores are keyed by widget id, which is device-local. A snapshot
    /// taken on another device — or before the widget was deleted and
    /// recreated — stages widgets/&lt;id&gt;/ dirs the live settings do not
    /// know; restoring them verbatim would wipe the live stores AND leave
    /// the restored data invisible. This is a pure plan: it only decides
    /// source→target id pairs and never touches the file system, so the
    /// caller can move dirs first and then rebase attachment paths with
    /// the remap applied.
    ///
    /// Pairing: orphans map onto free live widgets in id order — when the
    /// snapshot carries more source lists than this device has free widgets,
    /// the leftovers merge by item id into a target that will exist after
    /// the restore rather than landing invisible under a foreign id. Only
    /// a device with no todo widget at all leaves orphans unmapped —
    /// preserved on disk under the source id and reported.
    /// </summary>
    private async Task<TodoRestorePlan> PlanOrphanedTodoWidgetRemapsAsync(
        string stagingRoot,
        string stagedDataDirectory,
        CancellationToken cancellationToken)
    {
        const int MaxOrphanScanDepth = 64;
        string stagedWidgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
        if (!Directory.Exists(stagedWidgetsDirectory))
        {
            return TodoRestorePlan.Empty;
        }

        // Widget dirs that actually carry todo-domain payload in the snapshot.
        var stagedIds = new List<string>();
        foreach (string widgetDir in Directory.EnumerateDirectories(stagedWidgetsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool carriesTodoDomain = Directory
                .EnumerateFiles(widgetDir, "*", SearchOption.AllDirectories)
                .Take(MaxOrphanScanDepth)
                .Select(path => Path.GetRelativePath(stagedDataDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Any(rel => CloudBackupDomains.IsInDomain(CloudBackupDomain.TodoData, rel));
            if (carriesTodoDomain)
            {
                stagedIds.Add(Path.GetFileName(widgetDir));
            }
        }

        if (stagedIds.Count == 0)
        {
            return TodoRestorePlan.Empty;
        }

        // The archive's widget-style document names the SOURCE device's live
        // widgets, so staged todo dirs it does not list are the source's own
        // orphan leftovers — not user data — and get dropped from the
        // restore. An absent/unreadable document (or one naming none of the
        // staged ids) means "cannot tell": every staged dir stays a
        // candidate rather than risking real data.
        var authoritativeSet = new HashSet<string>(stagedIds, StringComparer.Ordinal);
        HashSet<string>? sourceLiveIds =
            await ReadSourceLiveTodoWidgetIdsAsync(stagingRoot, cancellationToken);
        if (sourceLiveIds is not null)
        {
            var named = stagedIds.Where(sourceLiveIds.Contains).ToList();
            if (named.Count > 0)
            {
                authoritativeSet.IntersectWith(sourceLiveIds);
            }
        }

        List<string> junk = stagedIds
            .Where(id => !authoritativeSet.Contains(id))
            .ToList();

        HashSet<string> liveTodoIds = await ReadLiveTodoWidgetIdsAsync(cancellationToken);
        List<string> orphans = stagedIds
            .Where(id => authoritativeSet.Contains(id) && !liveTodoIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        List<string> freeTargets = liveTodoIds
            .Where(id => !authoritativeSet.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var remaps = new List<DeskBoxTodoWidgetRemap>();
        var merges = new List<DeskBoxTodoWidgetRemap>();
        var unmapped = new List<string>();

        int pairs = Math.Min(orphans.Count, freeTargets.Count);
        for (int index = 0; index < pairs; index++)
        {
            remaps.Add(new DeskBoxTodoWidgetRemap(orphans[index], freeTargets[index]));
        }

        if (orphans.Count > pairs)
        {
            // The merge target must be a widget whose staged store exists
            // after the plan is applied: the first paired target, else the
            // first live widget whose own store is already in the snapshot.
            string? mergeTarget = remaps.Count > 0
                ? remaps[0].TargetWidgetId
                : liveTodoIds
                    .Where(authoritativeSet.Contains)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .FirstOrDefault();
            foreach (string leftover in orphans.Skip(pairs))
            {
                if (mergeTarget is null)
                {
                    unmapped.Add(leftover);
                }
                else
                {
                    merges.Add(new DeskBoxTodoWidgetRemap(leftover, mergeTarget));
                }
            }
        }

        return new TodoRestorePlan(remaps, merges, unmapped, junk);
    }

    /// <summary>
    /// Widget ids the archive's widget-style document lists as live Todo
    /// widgets on the source device — the liveness signal that lets the
    /// restore tell real todo stores apart from the source's orphan debris.
    /// Null when the document is absent, unreadable, or carries no widget
    /// map (callers fall back to treating every staged dir as candidate).
    /// </summary>
    private static async Task<HashSet<string>?> ReadSourceLiveTodoWidgetIdsAsync(
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        string documentPath = Path.Combine(
            stagingRoot, CloudBackupDomains.WidgetStyleEntryName);
        if (!File.Exists(documentPath))
        {
            return null;
        }

        try
        {
            JsonObject? document = JsonNode
                .Parse(await File.ReadAllBytesAsync(documentPath, cancellationToken))
                ?.AsObject();
            // Same shape check the apply path enforces — a foreign document
            // must never decide which staged stores count as live.
            if (document?["kind"] is not JsonValue docKindValue ||
                !docKindValue.TryGetValue(out string? docKind) ||
                !string.Equals(docKind, "widget-style", StringComparison.Ordinal) ||
                document["widgets"] is not JsonObject widgets)
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string id, JsonNode? style) in widgets)
            {
                if (style is JsonObject styleObject &&
                    styleObject["widgetKind"] is JsonValue kindValue &&
                    kindValue.TryGetValue(out string? kind) &&
                    string.Equals(kind, nameof(WidgetKind.Todo), StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[DataBackup] Could not read source widget liveness for scoped remap: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Materializes the plan inside the staging directory: junk source dirs
    /// are deleted, remap dirs move onto the target id, and merge sources
    /// append their items into the target store before being deleted. Runs
    /// before attachment-path rebasing: embedded FilePaths carry the source
    /// id, so the rebase rewrites widgets/&lt;source&gt; to
    /// widgets/&lt;target&gt; while the payload already sits there.
    /// </summary>
    private static async Task ApplyTodoRestorePlanAsync(
        string stagedDataDirectory,
        TodoRestorePlan plan,
        CancellationToken cancellationToken)
    {
        string stagedWidgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
        foreach (string junkId in plan.JunkSourceIds)
        {
            TryDeleteDirectory(Path.Combine(stagedWidgetsDirectory, junkId));
            App.Log($"[DataBackup] Scoped restore dropped source-orphan todo store '{junkId}'.");
        }

        foreach (DeskBoxTodoWidgetRemap remap in plan.Remaps)
        {
            // freeTargets excludes staged ids by construction, so the
            // destination directory cannot already exist in the snapshot.
            Directory.Move(
                Path.Combine(stagedWidgetsDirectory, remap.SourceWidgetId),
                Path.Combine(stagedWidgetsDirectory, remap.TargetWidgetId));
            App.Log($"[DataBackup] Scoped restore remapped todo store '{remap.SourceWidgetId}' -> '{remap.TargetWidgetId}'.");
        }

        foreach (IGrouping<string, DeskBoxTodoWidgetRemap> group in plan.Merges
                     .GroupBy(merge => merge.TargetWidgetId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetStore = new TodoWidgetStore(stagedWidgetsDirectory, group.Key);
            TodoWidgetData target = await targetStore.LoadAsync();
            target.Items ??= [];
            var seenIds = new HashSet<string>(
                target.Items.Where(item => item is not null).Select(item => item.Id),
                StringComparer.Ordinal);
            int nextSortOrder = target.Items.Count == 0
                ? 0
                : target.Items.Max(item => item?.SortOrder ?? 0) + 1;

            foreach (DeskBoxTodoWidgetRemap merge in group)
            {
                var orphanStore = new TodoWidgetStore(stagedWidgetsDirectory, merge.SourceWidgetId);
                TodoWidgetData orphan = await orphanStore.LoadAsync();
                int appended = 0;
                foreach (TodoItem item in orphan.Items ?? [])
                {
                    if (item is null ||
                        string.IsNullOrWhiteSpace(item.Text) ||
                        !seenIds.Add(item.Id))
                    {
                        continue;
                    }

                    item.SortOrder = nextSortOrder++;
                    target.Items.Add(item);
                    appended++;
                }

                TryDeleteDirectory(Path.Combine(stagedWidgetsDirectory, merge.SourceWidgetId));
                App.Log($"[DataBackup] Scoped restore merged {appended} todo item(s) from '{merge.SourceWidgetId}' into '{merge.TargetWidgetId}'.");
            }

            await targetStore.SaveAsync(target);
        }
    }

    /// <summary>
    /// Live todo-widget ids from the data dir. widget-layout.json is the
    /// first authority once it exists — it survives a settings.json loss
    /// independently, and the widgets array may already live there while
    /// settings is gone. settings.json is only the pre-adoption fallback; a
    /// wiped device (the disaster-recovery case) has neither file, and the
    /// empty set just leaves every orphan unmapped, which is the honest
    /// answer.
    /// </summary>
    private async Task<HashSet<string>> ReadLiveTodoWidgetIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string layoutPath = Path.Combine(DataDirectory, "widget-layout.json");
        string settingsPath = Path.Combine(DataDirectory, "settings.json");

        try
        {
            JsonArray? widgets;
            if (File.Exists(layoutPath))
            {
                byte[] layoutJson = await File.ReadAllBytesAsync(layoutPath, cancellationToken);
                widgets = JsonNode.Parse(layoutJson)?["layout"]?["widgets"] as JsonArray;
            }
            else if (File.Exists(settingsPath))
            {
                byte[] json = await File.ReadAllBytesAsync(settingsPath, cancellationToken);
                widgets = JsonNode.Parse(json)?["widgets"] as JsonArray;
            }
            else
            {
                return ids;
            }

            if (widgets is null)
            {
                return ids;
            }

            foreach (JsonNode? node in widgets)
            {
                if (node is not JsonObject element ||
                    element["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out string? id) ||
                    string.IsNullOrEmpty(id) ||
                    element["widgetKind"] is not JsonValue kindValue ||
                    !kindValue.TryGetValue(out string? kind) ||
                    !string.Equals(kind, nameof(WidgetKind.Todo), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[DataBackup] Could not enumerate live todo widgets for scoped remap: {ex.Message}");
        }

        return ids;
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

            if (marker.Domains is { Count: > 0 } &&
                marker.ScopedRestoreConfirmed == false)
            {
                // A crash or timed-out shutdown must never apply a cloud
                // restore while its confirmation dialog was still open.
                DeletePendingRestoreCore();
                App.Log("[DataBackup] Discarded an unconfirmed scoped restore.");
                return DeskBoxRestoreApplyResult.NoPendingRestore;
            }

            if (marker.Domains is { Count: > 0 } markerDomains)
            {
                return await ApplyScopedRestoreCoreAsync(
                    marker,
                    stagingRoot,
                    CloudBackupDomains.FromManifestNames(markerDomains),
                    cancellationToken);
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            ValidateRestoreData(
                stagedDataDirectory,
                requireSettings: !marker.AllowMissingSettings);

            marker = await EnsurePreRestoreSafetyBackupAsync(marker, cancellationToken);

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

    /// <summary>
    /// Applies a scoped cloud restore: replaces only the marked domains'
    /// files inside the live data directory. Everything outside the domains
    /// — settings.json, FileSafety files, other widget stores — is never
    /// touched. The operation is idempotent (staged files are the source of
    /// truth and are not modified), so a mid-apply crash leaves the marker
    /// in place and the next boot simply retries to convergence.
    /// </summary>
    private async Task<DeskBoxRestoreApplyResult> ApplyScopedRestoreCoreAsync(
        PendingRestoreMarker marker,
        string stagingRoot,
        CloudBackupDomain scope,
        CancellationToken cancellationToken)
    {
        string stagedDataDirectory = Path.Combine(stagingRoot, "data");
        try
        {
            // Full local safety net before ANY restore — same as the
            // classic path.
            marker = await EnsurePreRestoreSafetyBackupAsync(marker, cancellationToken);

            Directory.CreateDirectory(DataDirectory);
            foreach (CloudBackupDomain domain in CloudBackupDomains.FileDomains)
            {
                if (!scope.HasFlag(domain))
                {
                    continue;
                }

                if (!marker.ReplaceItemData)
                {
                    // Additive merge: staged items fold into the live stores
                    // by item id. Nothing local is ever deleted — a live
                    // store the snapshot does not carry keeps its data.
                    if (Directory.Exists(stagedDataDirectory))
                    {
                        if (domain == CloudBackupDomain.TodoData)
                        {
                            await MergeStagedTodoStoresAsync(
                                stagedDataDirectory, cancellationToken);
                        }
                        else if (domain == CloudBackupDomain.QuickCaptureData)
                        {
                            await MergeStagedQuickCaptureStoreAsync(
                                stagedDataDirectory, cancellationToken);
                        }
                    }

                    continue;
                }

                // Snapshot-faithful domain replace: live domain files absent
                // from the staged snapshot are deleted, then staged files
                // are copied in. Non-domain paths are never enumerated.
                foreach (string liveFile in Directory
                             .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                             .ToArray())
                {
                    string liveRelative = Path
                        .GetRelativePath(DataDirectory, liveFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (CloudBackupDomains.IsInDomain(domain, liveRelative))
                    {
                        File.Delete(liveFile);
                    }
                }

                if (!Directory.Exists(stagedDataDirectory))
                {
                    continue;
                }

                foreach (string stagedFile in Directory.EnumerateFiles(
                             stagedDataDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string stagedRelative = Path
                        .GetRelativePath(stagedDataDirectory, stagedFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!CloudBackupDomains.IsInDomain(domain, stagedRelative))
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(
                        DataDirectory,
                        stagedRelative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(stagedFile, destinationPath, overwrite: true);
                }
            }

            if (scope.HasFlag(CloudBackupDomain.WidgetStyle))
            {
                string styleDocumentPath = Path.Combine(
                    stagingRoot,
                    CloudBackupDomains.WidgetStyleEntryName);
                if (File.Exists(styleDocumentPath))
                {
                    byte[] documentBytes = await File.ReadAllBytesAsync(
                        styleDocumentPath,
                        cancellationToken);
                    WidgetStyleBackupProjection.ApplyResult styleResult =
                        await WidgetStyleBackupProjection.ApplyAsync(
                            documentBytes,
                            Path.Combine(DataDirectory, "settings.json"),
                            Path.Combine(DataDirectory, "widget-layout.json"),
                            cancellationToken);
                    App.Log(
                        $"[DataBackup] Widget style restore: applied={styleResult.Applied}, " +
                        $"shell={styleResult.ShellFieldsPatched}, widgets={styleResult.WidgetsPatched}" +
                        (styleResult.SkippedReason is null ? "." : $", skipped={styleResult.SkippedReason}."));
                }
            }

            TryDeleteFile(PendingRestoreMarkerPath);
            TryDeleteDirectory(stagingRoot);
            App.Log($"[DataBackup] Applied scoped restore from '{marker.ArchivePath}' (domains: {scope}).");
            return new DeskBoxRestoreApplyResult(true, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The marker and staging stay in place → next boot retries;
            // the pre-restore zip is the manual recovery net. Retries are
            // bounded: a deterministically failing archive would otherwise
            // block every scheduled upload forever.
            int attempts = marker.ApplyAttemptCount + 1;
            if (attempts >= MaxScopedRestoreApplyAttempts)
            {
                App.Log(
                    $"[DataBackup] Scoped restore abandoned after {attempts} attempts: {ex.Message}");
                TryDeleteFile(PendingRestoreMarkerPath);
                TryDeleteDirectory(stagingRoot);
                return new DeskBoxRestoreApplyResult(
                    true,
                    false,
                    $"Restore failed on {attempts} launches and was abandoned: {ex.Message}");
            }

            App.Log(
                $"[DataBackup] Scoped restore failed (will retry on next launch, " +
                $"attempt {attempts}/{MaxScopedRestoreApplyAttempts}): {ex}");
            try
            {
                // The counter must persist even when the apply token is
                // already cancelled — losing it would restart the budget.
                await WritePendingRestoreMarkerAtomicallyAsync(
                    PendingRestoreMarkerPath,
                    marker with { ApplyAttemptCount = attempts },
                    CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                App.Log($"[DataBackup] Failed to persist restore attempt count: {writeEx.Message}");
            }

            return new DeskBoxRestoreApplyResult(true, false, ex.Message);
        }
    }

    /// <summary>
    /// Records the item-restore mode chosen in the confirm dialog onto the
    /// pending marker — additive merge (default) vs snapshot-faithful
    /// replace. The choice is made after PrepareScopedRestoreAsync wrote
    /// the marker, so it is rewritten atomically; returns false when
    /// nothing is pending.
    /// </summary>
    internal async Task<bool> SetPendingRestoreItemReplaceModeAsync(
        bool replaceItemData,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(PendingRestoreMarkerPath))
            {
                return false;
            }

            PendingRestoreMarker marker = await ReadPendingRestoreMarkerAsync(cancellationToken);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker with
                {
                    ReplaceItemData = replaceItemData,
                    ScopedRestoreConfirmed = marker.Domains is { Count: > 0 }
                        ? true
                        : marker.ScopedRestoreConfirmed
                },
                cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Merge-mode apply for the todo domain: every staged
    /// widgets/&lt;id&gt;/todo.json folds its items into the live store at the
    /// same path — remapped targets merge onto live widgets, unmapped
    /// source ids still land on disk under their own id. Nothing is ever
    /// deleted: live stores the snapshot does not carry keep their data.
    /// </summary>
    private async Task MergeStagedTodoStoresAsync(
        string stagedDataDirectory,
        CancellationToken cancellationToken)
    {
        string stagedWidgetsDirectory = Path.Combine(stagedDataDirectory, "widgets");
        if (!Directory.Exists(stagedWidgetsDirectory))
        {
            return;
        }

        string liveWidgetsDirectory = Path.Combine(DataDirectory, "widgets");
        foreach (string stagedTodoPath in Directory.EnumerateFiles(
                     stagedWidgetsDirectory, "todo.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string widgetId = Path.GetFileName(Path.GetDirectoryName(stagedTodoPath)!);
            var stagedStore = new TodoWidgetStore(stagedWidgetsDirectory, widgetId);
            var liveStore = new TodoWidgetStore(liveWidgetsDirectory, widgetId);
            TodoWidgetData stagedData = await stagedStore.LoadAsync();
            TodoWidgetData liveData = await liveStore.LoadAsync();
            liveData.Items ??= [];
            var liveIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < liveData.Items.Count; index++)
            {
                if (liveData.Items[index] is { } existing)
                {
                    liveIndexById.TryAdd(existing.Id, index);
                }
            }

            int added = 0;
            int updated = 0;
            foreach (TodoItem item in stagedData.Items ?? [])
            {
                if (item is null)
                {
                    continue;
                }

                if (!liveIndexById.TryGetValue(item.Id, out int index))
                {
                    liveIndexById[item.Id] = liveData.Items.Count;
                    liveData.Items.Add(item);
                    added++;
                }
                else if (liveData.Items[index] is { } local &&
                         ShouldRemoteItemWin(
                             item.IsDeleted,
                             item.UpdatedAt,
                             local.IsDeleted,
                             local.UpdatedAt))
                {
                    liveData.Items[index] = item;
                    updated++;
                }
            }

            await liveStore.SaveAsync(liveData);
            App.Log($"[DataBackup] Merged staged todo store '{widgetId}': +{added} item(s), {updated} updated.");
        }
    }

    /// <summary>
    /// Merge-mode apply for the quick-capture domain: staged Items and
    /// RecentItems union into the live store by item id under the same
    /// no-delete rule. CurrentView is device-local UI state and stays.
    /// </summary>
    private async Task MergeStagedQuickCaptureStoreAsync(
        string stagedDataDirectory,
        CancellationToken cancellationToken)
    {
        string stagedStoreDirectory = Path.Combine(stagedDataDirectory, "quick-capture");
        if (!File.Exists(Path.Combine(stagedStoreDirectory, "quick-capture.json")))
        {
            return;
        }

        var stagedStore = new QuickCaptureStore(stagedStoreDirectory);
        var liveStore = new QuickCaptureStore(Path.Combine(DataDirectory, "quick-capture"));
        QuickCaptureStoreData stagedData = await stagedStore.LoadAsync();
        QuickCaptureStoreData liveData = await liveStore.LoadAsync();
        liveData.Items ??= [];
        liveData.RecentItems ??= [];
        (int added, int updated) = (0, 0);
        MergeList(liveData.Items, stagedData.Items);
        MergeList(liveData.RecentItems, stagedData.RecentItems);
        await liveStore.SaveAsync(liveData);

        void MergeList(List<QuickCaptureItem> liveList, List<QuickCaptureItem>? stagedList)
        {
            var liveIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < liveList.Count; index++)
            {
                if (liveList[index] is { } existing)
                {
                    liveIndexById.TryAdd(existing.Id, index);
                }
            }

            foreach (QuickCaptureItem item in stagedList ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null)
                {
                    continue;
                }

                if (!liveIndexById.TryGetValue(item.Id, out int index))
                {
                    liveIndexById[item.Id] = liveList.Count;
                    liveList.Add(item);
                    added++;
                }
                else if (liveList[index] is { } local &&
                         ShouldRemoteItemWin(
                             item.IsDeleted,
                             item.UpdatedAt,
                             local.IsDeleted,
                             local.UpdatedAt))
                {
                    liveList[index] = item;
                    updated++;
                }
            }
        }
        App.Log($"[DataBackup] Merged staged quick-capture store: +{added} item(s), {updated} updated.");
    }

    /// <summary>
    /// Item-level merge conflict rule shared by the todo and quick-capture
    /// merge paths: the staged record wins only when it is strictly newer
    /// AND is not a tombstone covering a live local record — a merge must
    /// never reduce what the local device can see.
    /// </summary>
    private static bool ShouldRemoteItemWin(
        bool remoteIsDeleted,
        DateTimeOffset remoteUpdatedAt,
        bool localIsDeleted,
        DateTimeOffset localUpdatedAt)
    {
        return !(remoteIsDeleted && !localIsDeleted) && remoteUpdatedAt > localUpdatedAt;
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
        // A non-zip or truncated archive throws here — that's the common
        // "user picked the wrong file" case, so it gets the same localized
        // "not a valid backup" surface as a missing manifest.
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException("The selected file is not a readable backup archive.", ex)
            {
                Data = { [BackupManifestInvalidDataKey] = true }
            };
        }

        using (archive)
        {
            return await ExtractAndValidateManifestAsync(archive, stagingRoot, cancellationToken);
        }
    }

    private async Task<RestoreArchiveInfo> ExtractAndValidateManifestAsync(
        ZipArchive archive,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        DeskBoxBackupManifest manifest;
        try
        {
            ZipArchiveEntry? manifestEntry = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal));
            if (manifestEntry is null)
            {
                throw new InvalidDataException("The backup manifest is missing.")
                {
                    Data = { [BackupManifestInvalidDataKey] = true }
                };
            }

            if (manifestEntry.Length > 1024 * 1024)
            {
                throw new InvalidDataException("The backup manifest is too large.")
                {
                    Data = { [BackupManifestInvalidDataKey] = true }
                };
            }

            await using (Stream manifestStream = manifestEntry.Open())
            {
                // The declared Length is only the cheap pre-check — cap the
                // actual inflated bytes so a crafted entry cannot expand
                // unboundedly into the JSON parser.
                var manifestBytes = new MemoryStream();
                await CopyAndHashAsync(
                    manifestStream,
                    manifestBytes,
                    cancellationToken,
                    maxBytes: 1024 * 1024);
                manifestBytes.Position = 0;
                manifest = await JsonSerializer.DeserializeAsync(
                               manifestBytes,
                               BackupJsonContext.Default.BackupManifest,
                               cancellationToken) ??
                           throw new InvalidDataException("The backup manifest is invalid.")
                           {
                               Data = { [BackupManifestInvalidDataKey] = true }
                           };
            }
        }
        catch (Exception ex) when (
            (ex is InvalidDataException or JsonException or NotSupportedException) &&
            !ex.Data.Contains(BackupManifestInvalidDataKey))
        {
            // Malformed manifest JSON, unsupported compression, truncated
            // entries — same user-facing story as a missing manifest.
            throw new InvalidDataException("The backup manifest is unreadable.", ex)
            {
                Data = { [BackupManifestInvalidDataKey] = true }
            };
        }

        if (manifest.SchemaVersion < MinimumSupportedBackupSchemaVersion ||
            manifest.SchemaVersion > BackupSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported DeskBox backup schema version {manifest.SchemaVersion}.")
            {
                Data = { [BackupSchemaVersionDataKey] = manifest.SchemaVersion }
            };
        }

        // Newer-app backups are NOT rejected: the schema gate above is the
        // real format contract, and a hard version block strands every
        // device that has not updated yet during a staggered rollout. The
        // preparation record carries IsFromNewerAppVersion so both restore
        // dialogs can warn that newer-version-only data may not survive.

        string destinationRoot = EnsureTrailingDirectorySeparator(
            Path.GetFullPath(Path.Combine(stagingRoot, "data")));
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new Dictionary<string, DeskBoxBackupFileManifest>(StringComparer.OrdinalIgnoreCase);
        DeskBoxBackupFileManifest? extractedWidgetStyle = null;
        int fileCount = 0;
        long totalUncompressedBytes = 0;
        // Remaining ACTUAL-byte budget: separate from the declared-length
        // sum because a malformed entry can under-report its true size.
        long totalExtractedBudget = MaxRestoreTotalSizeBytes;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.FullName.Contains('\\') ||
                (!entry.FullName.StartsWith("data/", StringComparison.Ordinal) &&
                 !string.Equals(entry.FullName, "data", StringComparison.Ordinal) &&
                 !string.Equals(
                     entry.FullName,
                     CloudBackupDomains.WidgetStyleEntryName,
                     StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Unexpected backup entry '{entry.FullName}'.");
            }

            string destinationPath = Path.GetFullPath(
                Path.Combine(stagingRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            string dataRootPath = destinationRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            bool isWidgetStyleEntry = string.Equals(
                entry.FullName,
                CloudBackupDomains.WidgetStyleEntryName,
                StringComparison.Ordinal);
            if (!string.Equals(destinationPath, dataRootPath, StringComparison.OrdinalIgnoreCase) &&
                !destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                !(isWidgetStyleEntry &&
                  string.Equals(
                      destinationPath,
                      Path.Combine(stagingRoot, CloudBackupDomains.WidgetStyleEntryName),
                      StringComparison.OrdinalIgnoreCase)))
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

            if (isWidgetStyleEntry)
            {
                // The style entry bypasses the per-file manifest but not the
                // safety budget: it is a small JSON document by construction,
                // so a dedicated cap stops a crafted archive from expanding
                // unboundedly before the DOM parse.
                if (entry.Length > MaxWidgetStyleEntryBytes)
                {
                    throw new InvalidDataException("The backup widget-style document is oversized.");
                }
            }
            else
            {
                fileCount++;
                if (fileCount > MaxRestoreFileCount || entry.Length > MaxRestoreFileSizeBytes)
                {
                    throw new InvalidDataException("The backup contains too many files or an oversized file.");
                }
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
            // The declared entry.Length checks above are only the cheap early
            // reject; the hard caps are enforced on the bytes the deflate
            // stream actually produces, capped at whichever budget runs out
            // first — the per-file limit or the remaining total budget.
            long extractedBudget = isWidgetStyleEntry
                ? MaxWidgetStyleEntryBytes
                : MaxRestoreFileSizeBytes;
            (long extractedLength, string sha256) = await CopyAndHashAsync(
                source,
                destination,
                cancellationToken,
                Math.Min(extractedBudget, totalExtractedBudget));
            totalExtractedBudget -= extractedLength;
            if (isWidgetStyleEntry)
            {
                extractedWidgetStyle = new DeskBoxBackupFileManifest(
                    entry.FullName,
                    extractedLength,
                    sha256);
            }
            else
            {
                string relativePath = entry.FullName["data/".Length..];
                extractedFiles[relativePath] = new DeskBoxBackupFileManifest(
                    relativePath,
                    extractedLength,
                    sha256);
            }
        }

        bool isScopedArchive = manifest.Domains is { Count: > 0 };
        if (fileCount == 0 && !isScopedArchive)
        {
            throw new InvalidDataException("The backup contains no DeskBox data files.");
        }

        if (manifest.SchemaVersion >= 2 &&
            (extractedFiles.Count > 0 || !isScopedArchive))
        {
            ValidateIntegrityManifest(manifest.Files, extractedFiles);
        }

        // widget-style.json bypasses Files (root-level, not data-relative)
        // but is covered by its own manifest field. Verify when declared —
        // archives written before the field existed legitimately lack it;
        // a declared entry with no matching archive entry is corruption.
        if (manifest.WidgetStyleFile is { } expectedStyle)
        {
            if (extractedWidgetStyle is null ||
                expectedStyle.Length != extractedWidgetStyle.Length ||
                !string.Equals(
                    expectedStyle.Sha256,
                    extractedWidgetStyle.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The backup widget-style document failed integrity validation.");
            }
        }

        if (!isScopedArchive)
        {
            ValidateRestoreData(
                Path.Combine(stagingRoot, "data"),
                requireSettings: !string.Equals(
                    manifest.Kind, "pre-restore", StringComparison.Ordinal));
        }

        return new RestoreArchiveInfo(manifest, fileCount, totalUncompressedBytes);
    }

    private static void ValidateRestoreData(string dataDirectory, bool requireSettings = true)
    {
        if (!Directory.Exists(dataDirectory) ||
            !Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("The backup data directory is empty.");
        }

        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        if (requireSettings && !File.Exists(settingsPath))
        {
            throw new InvalidDataException("The backup is missing settings.json.");
        }

        ValidateJsonFileIfPresent<AppSettings>(
            settingsPath,
            s_settingsDataJsonContext.AppSettings);
        ValidateJsonFileIfPresent<QuickCaptureStoreData>(
            Path.Combine(dataDirectory, "quick-capture", "quick-capture.json"),
            s_quickCaptureDataJsonContext.StoreData);
        ValidateJsonFileIfPresent<DesktopOrganizationHistoryData>(
            Path.Combine(dataDirectory, "desktop-organization-history.json"),
            DesktopOrganizationHistoryJsonContext.Default.DesktopOrganizationHistoryData);
        ValidateJsonFileIfPresent<WidgetLayoutDocument>(
            Path.Combine(dataDirectory, "widget-layout.json"),
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);

        string widgetsDirectory = Path.Combine(dataDirectory, "widgets");
        if (Directory.Exists(widgetsDirectory))
        {
            foreach (string todoPath in Directory.EnumerateFiles(
                         widgetsDirectory,
                         "todo.json",
                         SearchOption.AllDirectories))
            {
                ValidateJsonFileIfPresent<TodoWidgetData>(
                    todoPath,
                    s_todoDataJsonContext.StoreData);
            }
        }
    }

    private static void ValidateJsonFileIfPresent<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (JsonSerializer.Deserialize(json, jsonTypeInfo) is null)
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
        IReadOnlyDictionary<string, string>? todoWidgetIdRemaps,
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
                todoWidgetIdRemaps,
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
                        todoWidgetIdRemaps,
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
        QuickCaptureStoreData data = JsonSerializer.Deserialize(
                                         await File.ReadAllTextAsync(path, cancellationToken),
                                         s_quickCaptureDataJsonContext.StoreData) ??
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
                    ["quick-capture"]);
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
                        ["quick-capture"]) ?? item.ImagePath;
                }
            }
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(data, s_quickCaptureDataJsonContext.StoreData),
            cancellationToken);
    }

    private async Task RebaseTodoFileAsync(
        string path,
        string stagedDataDirectory,
        string? sourceDataPath,
        IReadOnlyDictionary<string, string>? widgetIdRemaps,
        CancellationToken cancellationToken)
    {
        TodoWidgetData data = JsonSerializer.Deserialize(
                                  await File.ReadAllTextAsync(path, cancellationToken),
                                  s_todoDataJsonContext.StoreData) ??
                              throw new InvalidDataException("Todo backup data is invalid.");
        // The store dir may already have been moved to a remapped target id —
        // embedded FilePaths still carry the SOURCE id, so the store-relative
        // fallback lookup must use the source id while the final rewrite
        // (inside TryRebaseManagedPath) points at the target. A merge target
        // collects several source ids — every one is a fallback candidate.
        string currentWidgetId = Path.GetFileName(Path.GetDirectoryName(path)!);
        var storeRelativePaths = new List<string>
        {
            Path.GetRelativePath(stagedDataDirectory, Path.GetDirectoryName(path)!)
                .Replace(Path.DirectorySeparatorChar, '/')
        };
        if (widgetIdRemaps is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> remap in widgetIdRemaps)
            {
                if (string.Equals(remap.Value, currentWidgetId, StringComparison.Ordinal) &&
                    !storeRelativePaths.Contains($"widgets/{remap.Key}", StringComparer.Ordinal))
                {
                    storeRelativePaths.Add($"widgets/{remap.Key}");
                }
            }
        }

        foreach (TodoAttachment attachment in (data.Items ?? [])
                     .SelectMany(item => item.Attachments ?? [])
                     .Where(attachment => attachment is not null && attachment.IsManagedCopy))
        {
            attachment.FilePath = TryRebaseManagedPath(
                                      attachment.FilePath,
                                      sourceDataPath,
                                      stagedDataDirectory,
                                      storeRelativePaths,
                                      widgetIdRemaps) ??
                                  attachment.FilePath;
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(data, s_todoDataJsonContext.StoreData),
            cancellationToken);
    }

    private string? TryRebaseManagedPath(
        string? originalPath,
        string? sourceDataPath,
        string stagedDataDirectory,
        IReadOnlyList<string> fallbackStoreRelativePaths,
        IReadOnlyDictionary<string, string>? widgetIdRemaps = null)
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

        foreach (string fallbackStoreRelativePath in fallbackStoreRelativePaths)
        {
            relativePath ??= TryGetStoreRelativePath(originalPath, fallbackStoreRelativePath);
        }
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        // Embedded paths carry the source widget id; after a remap move the
        // payload lives under the target id — rewrite the leading segment.
        if (widgetIdRemaps is { Count: > 0 })
        {
            string normalized = relativePath.Replace('\\', '/');
            const string widgetsPrefix = "widgets/";
            if (normalized.StartsWith(widgetsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                int idEnd = normalized.IndexOf('/', widgetsPrefix.Length);
                if (idEnd > widgetsPrefix.Length &&
                    widgetIdRemaps.TryGetValue(
                        normalized[widgetsPrefix.Length..idEnd],
                        out string? targetId))
                {
                    relativePath = (widgetsPrefix + targetId + normalized[idEnd..])
                        .Replace('/', Path.DirectorySeparatorChar);
                }
            }
        }

        string stagedPath = Path.GetFullPath(Path.Combine(stagedDataDirectory, relativePath));
        if (!IsPathInsideDirectory(stagedPath, stagedDataDirectory) || !File.Exists(stagedPath))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
    }

    private static JsonSerializerOptions CreateDataJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

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
            return JsonSerializer.Deserialize(
                       json,
                       BackupJsonContext.Default.PendingRestoreMarker) ??
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
                PendingRestoreMarker? marker = JsonSerializer.Deserialize(
                    json,
                    BackupJsonContext.Default.PendingRestoreMarker);
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

    private static async Task WritePendingRestoreMarkerAtomicallyAsync(
        string path,
        PendingRestoreMarker marker,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(
                    marker,
                    BackupJsonContext.Default.PendingRestoreMarker),
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
        // "Source data" means data the archive filter would actually keep:
        // a data directory holding only device.id, sync/ protocol state or
        // cache/ produces an empty snapshot whose validation would fail —
        // and that failure would block the restore the net protects.
        return Directory.Exists(DataDirectory) &&
            Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                .Any(path => ShouldIncludeInBackup(
                    Path.GetRelativePath(DataDirectory, path)
                        .Replace(Path.DirectorySeparatorChar, '/')));
    }

    private async Task CreateArchiveCoreAsync(
        string archivePath,
        string backupKind,
        CancellationToken cancellationToken,
        bool requireSettings = true)
    {
        string snapshotRoot = Path.Combine(
            BackupSnapshotStagingDirectory,
            Guid.NewGuid().ToString("N"));
        string snapshotDataDirectory = Path.Combine(snapshotRoot, "data");
        try
        {
            await CreateDataSnapshotAsync(
                snapshotDataDirectory, cancellationToken, requireSettings);
            ValidateRestoreData(snapshotDataDirectory, requireSettings);
            await CreateArchiveFromSnapshotAsync(
                archivePath,
                backupKind,
                snapshotDataDirectory,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(snapshotRoot);
            TryDeleteEmptyDirectory(BackupSnapshotStagingDirectory);
        }
    }

    public async Task<IReadOnlyList<DeskBoxBackupSnapshotInfo>> GetSnapshotInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Snapshots are intentionally not migrated when the custom folder
            // changes, so both the configured and the default directories can
            // hold restorable copies at the same time.
            var paths = new[]
                {
                    (Directory: EffectiveAutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: AutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: LegacyAutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: PreRestoreBackupDirectory, Kind: "pre-restore")
                }
                .Where(item => Directory.Exists(item.Directory))
                .DistinctBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
                .SelectMany(item => Directory.EnumerateFiles(item.Directory, "*.zip")
                    .Select(path => (path, item.Kind)))
                .OrderByDescending(item => File.GetLastWriteTimeUtc(item.path))
                .ToList();

            var snapshots = new List<DeskBoxBackupSnapshotInfo>(paths.Count);
            foreach ((string path, string kind) in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(path);
                SnapshotManifestSummary summary = await ReadSnapshotManifestSummaryAsync(path, cancellationToken);
                snapshots.Add(new DeskBoxBackupSnapshotInfo(
                    path,
                    kind,
                    summary.CreatedAtUtc ?? file.LastWriteTimeUtc,
                    file.Length,
                    summary.IsReadable,
                    summary.AppVersion,
                    summary.SchemaVersion));
            }

            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finds the newest readable snapshot stored outside the app-data root.
    /// This is used after a reinstall when the local settings file no longer
    /// exists, so DeskBox can point the user to a safe recovery copy.
    /// </summary>
    public async Task<DeskBoxBackupSnapshotInfo?> GetLatestRecoverySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(AutomaticSnapshotDirectory))
            {
                return null;
            }

            foreach (string path in Directory
                         .EnumerateFiles(AutomaticSnapshotDirectory, "DeskBox-Auto-*.zip")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(path);
                SnapshotManifestSummary summary = await ReadSnapshotManifestSummaryAsync(path, cancellationToken);
                if (!summary.IsReadable)
                {
                    continue;
                }

                return new DeskBoxBackupSnapshotInfo(
                    path,
                    "automatic",
                    summary.CreatedAtUtc ?? file.LastWriteTimeUtc,
                    file.Length,
                    true,
                    summary.AppVersion,
                    summary.SchemaVersion);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        snapshotPath = Path.GetFullPath(snapshotPath);

        if (!snapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            (!IsPathInsideDirectory(snapshotPath, EffectiveAutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, AutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, LegacyAutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, PreRestoreBackupDirectory)))
        {
            throw new InvalidOperationException("The selected backup snapshot is not managed by DeskBox.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(snapshotPath))
            {
                return false;
            }

            File.Delete(snapshotPath);
            App.Log($"[DataBackup] Deleted snapshot '{snapshotPath}'.");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<SnapshotManifestSummary> ReadSnapshotManifestSummaryAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? manifestEntry = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal));
            if (manifestEntry is null || manifestEntry.Length > 1024 * 1024)
            {
                return SnapshotManifestSummary.Unreadable;
            }

            await using Stream manifestStream = manifestEntry.Open();
            // The declared Length is only the cheap pre-check — a malformed
            // entry can inflate past it, so cap the actual bytes read.
            var manifestBytes = new MemoryStream();
            await CopyAndHashAsync(
                manifestStream,
                manifestBytes,
                cancellationToken,
                maxBytes: 1024 * 1024);
            manifestBytes.Position = 0;
            DeskBoxBackupManifest? manifest = await JsonSerializer.DeserializeAsync(
                manifestBytes,
                BackupJsonContext.Default.BackupManifest,
                cancellationToken);
            return manifest is null
                ? SnapshotManifestSummary.Unreadable
                : new SnapshotManifestSummary(
                    true,
                    manifest.CreatedAtUtc,
                    manifest.AppVersion,
                    manifest.SchemaVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return SnapshotManifestSummary.Unreadable;
        }
    }

    private async Task CreateDataSnapshotAsync(
        string snapshotDataDirectory,
        CancellationToken cancellationToken,
        bool requireSettings = true)
    {
        string settingsPath = Path.Combine(DataDirectory, "settings.json");
        // The pre-restore safety net must not require settings.json: a device
        // whose settings were lost or quarantined still has widget stores
        // worth preserving before a restore overwrites them.
        if (requireSettings && !File.Exists(settingsPath))
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

        Directory.CreateDirectory(snapshotDataDirectory);

        // FileSafety metadata must come from a single transaction epoch:
        // settings, the device-layout store, the organization-history store
        // and the recovery journal are committed as a unit under
        // OperationGate. The file SET is resolved inside the gate — a journal
        // created while we waited for the gate must land in the snapshot, or
        // the backup would hold settings@T1 with history@T0 and no WAL to
        // converge them. Hold the gate only for these few small files; the
        // rest still copies one by one from the pre-enumerated list.
        //
        // settings.json and widget-layout.json additionally commit under the
        // settings write gate — a plain settings save does not take
        // OperationGate, so without it the snapshot could tear mid-save into
        // a pair that never existed on disk (settings@S0 with layout@S1).
        // Lock order is OperationGate-then-write-gate everywhere, matching
        // DesktopOrganizationTransaction's own save path.
        string[] fileSafetyMetadata =
        [
            "settings.json",
            "widget-layout.json",
            "desktop-organization-history.json",
            "desktop-organization-recovery.json"
        ];
        var metadataSet = new HashSet<string>(fileSafetyMetadata, StringComparer.OrdinalIgnoreCase);
        await DesktopOrganizationTransaction.OperationGate.WaitAsync(cancellationToken);
        try
        {
            SemaphoreSlim settingsWriteLock = SettingsService.FileWriteLockFor(DataDirectory);
            await settingsWriteLock.WaitAsync(cancellationToken);
            try
            {
                foreach (string relativePath in fileSafetyMetadata)
                {
                    string sourcePath = Path.Combine(DataDirectory, relativePath);
                    if (!File.Exists(sourcePath)) continue;
                    await CopyStableSnapshotFileAsync(
                        sourcePath,
                        Path.Combine(snapshotDataDirectory, relativePath),
                        cancellationToken);
                }
            }
            finally
            {
                settingsWriteLock.Release();
            }
        }
        finally
        {
            DesktopOrganizationTransaction.OperationGate.Release();
        }

        foreach ((string sourcePath, string relativePath) in sourceFiles)
        {
            if (metadataSet.Contains(relativePath)) continue;
            // A multi-gigabyte snapshot legitimately runs longer than the
            // startup watchdog's stall window; each file proves progress.
            App.MarkStartupProgress();
            cancellationToken.ThrowIfCancellationRequested();
            string destinationPath = Path.Combine(
                snapshotDataDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyStableSnapshotFileAsync(sourcePath, destinationPath, cancellationToken);
        }
    }

    private async Task CreateArchiveFromSnapshotAsync(
        string archivePath,
        string backupKind,
        string snapshotDataDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? manifestDomains = null,
        IReadOnlyDictionary<string, byte[]>? rootEntries = null,
        string? sourceDeviceId = null)
    {
        (string SourcePath, string RelativePath)[] sourceFiles = Directory
            .EnumerateFiles(snapshotDataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (
                SourcePath: path,
                RelativePath: Path.GetRelativePath(snapshotDataDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')))
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
                            FileShare.Read,
                            bufferSize: 81920,
                            useAsync: true);
                        await using Stream destination = entry.Open();
                        (long length, string sha256) = await CopyAndHashAsync(
                            source,
                            destination,
                            cancellationToken);
                        fileManifest.Add(new DeskBoxBackupFileManifest(relativePath, length, sha256));
                    }

                    // Root-level entries (e.g. widget-style.json) live beside
                    // manifest.json — they are backup artifacts, not data
                    // files, and scoped restore reads them from staging root.
                    DeskBoxBackupFileManifest? widgetStyleManifest = null;
                    if (rootEntries is not null)
                    {
                        foreach ((string entryName, byte[] content) in rootEntries)
                        {
                            ZipArchiveEntry rootEntry = archive.CreateEntry(
                                entryName,
                                CompressionLevel.Fastest);
                            await using Stream entryStream = rootEntry.Open();
                            await entryStream.WriteAsync(content, cancellationToken);
                            if (string.Equals(
                                    entryName,
                                    CloudBackupDomains.WidgetStyleEntryName,
                                    StringComparison.Ordinal))
                            {
                                widgetStyleManifest = new DeskBoxBackupFileManifest(
                                    entryName,
                                    content.Length,
                                    Convert.ToHexString(SHA256.HashData(content)));
                            }
                        }
                    }

                    var manifest = new DeskBoxBackupManifest(
                        BackupSchemaVersion,
                        backupKind,
                        DateTimeOffset.UtcNow,
                        typeof(DeskBoxDataBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                        DataDirectory,
                        fileManifest,
                        manifestDomains,
                        sourceDeviceId,
                        widgetStyleManifest);
                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using (Stream manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            manifest,
                            BackupJsonContext.Default.BackupManifest,
                            cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            // The final rename commits the archive. Cancellation is accepted
            // before it; after it succeeds the completed backup is retained.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempArchivePath, archivePath, overwrite: false);
        }
        finally
        {
            TryDeleteFile(tempArchivePath);
        }
    }

    private static async Task CopyStableSnapshotFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxSnapshotCopyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(destinationPath);

            try
            {
                var before = new FileInfo(sourcePath);
                long expectedLength = before.Length;
                DateTime expectedLastWriteUtc = before.LastWriteTimeUtc;

                long copiedLength;
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete,
                                 bufferSize: 81920,
                                 useAsync: true))
                await using (var destination = new FileStream(
                                 destinationPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 useAsync: true))
                {
                    (copiedLength, _) = await CopyAndHashAsync(source, destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }

                var after = new FileInfo(sourcePath);
                if (after.Exists &&
                    copiedLength == expectedLength &&
                    after.Length == expectedLength &&
                    after.LastWriteTimeUtc == expectedLastWriteUtc)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MaxSnapshotCopyAttempts)
                {
                    break;
                }
            }

            if (attempt < MaxSnapshotCopyAttempts)
            {
                await Task.Yield();
            }
        }

        TryDeleteFile(destinationPath);
        throw new IOException($"DeskBox data file changed while creating a backup snapshot: '{sourcePath}'.");
    }

    private void PruneAutomaticSnapshots(string directory, int retentionCount)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string obsoletePath in Directory
                     .EnumerateFiles(directory, "DeskBox-Auto-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(Math.Max(1, retentionCount)))
        {
            TryDeleteFile(obsoletePath);
        }
    }

    /// <summary>
    /// Creates the pre-restore safety net once per pending-restore
    /// transaction. A scoped restore retries on every launch until it
    /// converges; creating a fresh archive per attempt would stack
    /// post-restore states and eventually prune the FIRST snapshot — the
    /// only one that actually holds pre-restore data. The pinned path is
    /// written into the marker BEFORE the archive is created so a crash
    /// can never leave an unbounded accumulation, and the pin exempts the
    /// archive from pruning while the marker lives.
    /// </summary>
    private async Task<PendingRestoreMarker> EnsurePreRestoreSafetyBackupAsync(
        PendingRestoreMarker marker,
        CancellationToken cancellationToken)
    {
        if (marker.SafetyBackupPath is { } pinned && File.Exists(pinned))
        {
            return marker;
        }

        if (!HasBackupSourceData())
        {
            return marker;
        }

        Directory.CreateDirectory(PreRestoreBackupDirectory);
        string preRestorePath = GetAvailableArchivePath(
            PreRestoreBackupDirectory,
            $"DeskBox-PreRestore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await WritePendingRestoreMarkerAtomicallyAsync(
            PendingRestoreMarkerPath,
            marker with { SafetyBackupPath = preRestorePath },
            cancellationToken);
        await CreateArchiveCoreAsync(
            preRestorePath, "pre-restore", cancellationToken,
            requireSettings: false);
        PrunePreRestoreBackups(preRestorePath);
        App.Log($"[DataBackup] Created pre-restore backup '{preRestorePath}'.");
        // The pinned path must flow back to the caller's marker instance:
        // the retry-attempt rewrite below does `marker with {...}` and would
        // otherwise clobber the pin from the disk copy, stacking one safety
        // archive per failed launch.
        return marker with { SafetyBackupPath = preRestorePath };
    }

    private void PrunePreRestoreBackups(string? pinnedPath = null)
    {
        int kept = 0;
        foreach (string candidatePath in Directory
                     .EnumerateFiles(PreRestoreBackupDirectory, "DeskBox-PreRestore-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (pinnedPath is not null &&
                string.Equals(candidatePath, pinnedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++kept > MaxPreRestoreBackupCount)
            {
                TryDeleteFile(candidatePath);
            }
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

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
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

    /// <summary>
    /// ResilientJsonStore sidecars only ever sit next to a DeskBox *.json
    /// store: "&lt;store&gt;.json.bak" and
    /// "&lt;store&gt;.json.corrupt-&lt;timestamp&gt;-&lt;guid&gt;". Scoped to
    /// that exact namespace so user files under attachments/ (which keep
    /// their original names) are never filtered out.
    /// </summary>
    private static bool IsInternalStoreRecoveryArtifact(string relativePath) =>
        relativePath.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains(".json.corrupt-", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldIncludeInBackup(string relativePath)
    {
        // DeskBox-managed disposable subtrees first: nothing under them is
        // user data, and they can never contain a managed-attachments dir.
        // cache/ (widget image caches) and weather-cache.json regenerate on
        // next use; quick-capture exports/thumbnails are derived artifacts.
        //
        // device.id is excluded deliberately: it is installation-local
        // identity, not user data. Carrying it into a backup would clone the
        // device identity onto every machine that restores it — silently
        // misattributing sync-layer provenance. DeviceIdentity.GetOrCreate
        // regenerates a fresh ID on first use after a restore.
        //
        // sync/ is excluded deliberately too: it is device-local protocol
        // state (outbox queue, pull cursors, revision map, conflict log).
        // Restoring it onto another machine would replay stale cursors and
        // push intents against data the restore also rewrote — the sync
        // contract assigns it no backup semantics at all.
        if (relativePath.StartsWith("quick-capture/thumbnails/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("quick-capture/exports/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("cache/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("sync/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "weather-cache.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "device.id", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Managed attachments are user data with their ORIGINAL filenames —
        // no extension or sidecar heuristic may ever drop them (a user file
        // literally named "config.json.bak" or "file.tmp" must still be
        // backed up, or restore leaves dangling attachment metadata).
        if (IsAttachmentPath(relativePath))
        {
            return true;
        }

        // .tmp files and ResilientJsonStore sidecars are machine-local
        // recovery artifacts, not user data: a stale recovery .bak inside a
        // backup would resurrect a ghost pending journal on the restore
        // machine, and .corrupt-* quarantines are dead forensics. The store
        // regenerates its .bak on the next save anyway. The artifact check
        // is scoped to the ResilientJsonStore naming convention itself
        // ("<store>.json.bak" / "<store>.json.corrupt-*").
        // The style-restore journal is in-flight transaction state, not user
        // data: carrying pending/committed/orig files into a backup would
        // land a half-applied restore on the target machine and let a stale
        // journal fire the first time a style restore runs there.
        if (relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".style-restore.", StringComparison.OrdinalIgnoreCase) ||
            IsInternalStoreRecoveryArtifact(relativePath))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Managed attachment directories hold user files under their original
    /// names — "attachments/" as a path segment anywhere under the data
    /// root marks user content (widgets/&lt;id&gt;/attachments/,
    /// quick-capture/attachments/, ...), which must never be filtered by
    /// extension or store-sidecar heuristics.
    /// </summary>
    private static bool IsAttachmentPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        long maxBytes = long.MaxValue)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long totalBytes = 0;
        long bytesSinceProgressMark = 0;
        const long progressMarkIntervalBytes = 32L * 1024 * 1024;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            // Enforce the byte budget on what the stream ACTUALLY yields —
            // the zip central directory's declared Length is attacker-
            // controlled and cannot be trusted to bound inflation.
            if (totalBytes + bytesRead > maxBytes)
            {
                throw new InvalidDataException(
                    "The backup contains too many files or an oversized file.");
            }

            hash.AppendData(buffer, 0, bytesRead);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytes = checked(totalBytes + bytesRead);
            // A single multi-gigabyte file can outlast the startup watchdog's
            // stall window on slow storage; mark progress per chunk so the
            // copy itself proves the process is alive.
            bytesSinceProgressMark += bytesRead;
            if (bytesSinceProgressMark >= progressMarkIntervalBytes)
            {
                bytesSinceProgressMark = 0;
                App.MarkStartupProgress();
            }
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
        IReadOnlyList<DeskBoxBackupFileManifest>? Files = null,
        // Cloud-backup domain names (CloudBackupDomains.ToManifestName).
        // Absent on classic full backups; a non-empty list means the archive
        // holds ONLY those domains and must go through the scoped restore —
        // a whole-directory swap would wipe everything else. Null stays
        // unwritten so classic manifests keep their canonical key set.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Domains = null,
        // Scoped cloud backups only: the full device id that produced the
        // archive (the file name only carries the 8-char suffix). Surfaced
        // in the restore confirmation; never used to gate restores — a
        // wiped device regenerates its id and still owns its backups.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceDeviceId = null,
        // Integrity entry for the root-level widget-style.json — it lives
        // outside data/ so it cannot ride in Files. Optional: archives
        // written before this field existed simply skip the check.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DeskBoxBackupFileManifest? WidgetStyleFile = null);

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
        string AppVersion,
        // Non-empty → scoped cloud restore: replace only the listed domains'
        // files instead of swapping the whole data directory. Null stays
        // unwritten so classic markers keep their canonical key set.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Domains = null,
        // A "pre-restore" safety archive legitimately lacks settings.json —
        // it captured whatever survived on a device that lost it. Refusing
        // to restore it would strand the very data it exists to protect.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool AllowMissingSettings = false,
        // The safety archive belongs to this pending-restore transaction,
        // not to a single apply attempt: retries reuse the FIRST snapshot
        // (the only one holding pre-restore data) and pruning must never
        // age it out while the marker lives.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SafetyBackupPath = null,
        // Scoped restores only: the item-restore mode the user picked in
        // the confirm dialog. True = snapshot-faithful domain replace;
        // false/absent = additive merge (nothing local is deleted). The
        // Existing markers without a confirmation field keep their original
        // additive-merge fallback for compatibility.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool ReplaceItemData = false,
        // Bounded retries: a deterministically failing scoped restore
        // (e.g. an archive from a future schema) must not retry on every
        // boot forever — the pending marker also blocks scheduled uploads.
        // Rewritten atomically on each failed apply.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int ApplyAttemptCount = 0,
        // New scoped markers are explicitly unconfirmed until the user
        // chooses an item mode. Null means a pre-existing marker or full
        // restore and retains its historical apply behavior.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? ScopedRestoreConfirmed = null);

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(
        typeof(DeskBoxBackupManifest),
        TypeInfoPropertyName = "BackupManifest")]
    [JsonSerializable(
        typeof(DeskBoxBackupFileManifest),
        TypeInfoPropertyName = "BackupFileManifest")]
    [JsonSerializable(
        typeof(PendingRestoreMarker),
        TypeInfoPropertyName = "PendingRestoreMarker")]
    private sealed partial class BackupJsonContext : JsonSerializerContext
    {
    }
}

public sealed record DeskBoxBackupSnapshotInfo(
    string Path,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    bool IsReadable,
    string? AppVersion,
    int SchemaVersion);

/// <summary>
/// Effective automatic-snapshot folder state for the settings UI. A configured
/// directory that is not active means snapshots currently fall back to the
/// default recovery directory.
/// </summary>
public sealed record AutomaticBackupDirectoryStatus(
    string? ConfiguredDirectory,
    string EffectiveDirectory,
    bool IsCustomDirectoryActive);

internal sealed record SnapshotManifestSummary(
    bool IsReadable,
    DateTimeOffset? CreatedAtUtc,
    string? AppVersion,
    int SchemaVersion)
{
    public static SnapshotManifestSummary Unreadable { get; } = new(false, null, null, 0);
}

public sealed record DeskBoxRestorePreparation(
    DateTimeOffset BackupCreatedAtUtc,
    string AppVersion,
    int FileCount,
    long TotalUncompressedBytes,
    int BackupSchemaVersion,
    bool HasIntegrityManifest,
    // Non-empty on scoped cloud restores — the domains that will be applied.
    IReadOnlyList<string>? Domains = null,
    // Scoped cloud restores only: full device id recorded in the archive
    // manifest (informational — never a restore gate).
    string? SourceDeviceId = null,
    // Scoped restores only: staged todo stores whose source widget id does
    // not exist on this device were remapped onto live todo widgets so the
    // restored data stays visible.
    IReadOnlyList<DeskBoxTodoWidgetRemap>? TodoWidgetRemaps = null,
    // Source widget ids that could not be remapped (no free todo widget on
    // this device). Their files are still restored under the source id —
    // preserved on disk even though no widget currently reads them.
    IReadOnlyList<string>? UnmappedTodoWidgetIds = null,
    // Scoped cloud restores only: live-item count per applied item domain
    // (todo-data, quick-capture-data), so the confirm dialog can say "this
    // domain holds 0 items" instead of letting an empty domain silently
    // wipe local data. WidgetStyle is a settings projection, not item
    // data, and never appears here.
    IReadOnlyList<DeskBoxDomainItemCount>? DomainItemCounts = null,
    // Scoped restores only: attachment/image references inside the staged
    // item data. Attachment FILES never ship in a cloud backup, so the
    // confirm dialog warns that these references dangle after a
    // cross-device restore.
    int AttachmentReferenceCount = 0,
    // True when the archive was written by a newer DeskBox build. Restore
    // is allowed — the schema version is the real compatibility gate — but
    // the confirm dialog warns that newer-version-only fields may not
    // survive being saved again by this older build.
    bool IsFromNewerAppVersion = false);

/// <summary>One todo-store directory remapped from source to target widget id.</summary>
public sealed record DeskBoxTodoWidgetRemap(string SourceWidgetId, string TargetWidgetId);

/// <summary>
/// Planned staging mutations for a scoped todo restore: directory remaps,
/// item-level merges (source store folded into a target when the snapshot
/// has more lists than this device has free widgets), source ids that could
/// not land on any live widget, and staged dirs the archive's own style
/// document does not list as live on the source device (orphan debris).
/// </summary>
internal sealed record TodoRestorePlan(
    IReadOnlyList<DeskBoxTodoWidgetRemap> Remaps,
    IReadOnlyList<DeskBoxTodoWidgetRemap> Merges,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> JunkSourceIds)
{
    internal static TodoRestorePlan Empty { get; } = new(
        Array.Empty<DeskBoxTodoWidgetRemap>(),
        Array.Empty<DeskBoxTodoWidgetRemap>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}

/// <summary>
/// Live-item count for one applied item domain in a scoped restore —
/// <paramref name="Domain"/> is the manifest name
/// (<see cref="CloudBackupDomains.ToManifestName"/>), <paramref name="Items"/>
/// the non-deleted entries the domain will actually restore.
/// </summary>
public sealed record DeskBoxDomainItemCount(string Domain, int Items);

internal sealed record DeskBoxRestoreApplyResult(
    bool HadPendingRestore,
    bool Succeeded,
    string? ErrorMessage)
{
    public static DeskBoxRestoreApplyResult NoPendingRestore { get; } = new(false, true, null);
}
