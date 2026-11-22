using System.Text.Json.Serialization;

namespace DeskBox.Services;

public enum ManagedStorageMigrationPhase
{
    Preparing,
    Moving,
    CheckingExisting,
    Copying,
    Verifying,
    Committing,
    CleaningOrigin,
    Completed
}

public enum ManagedStorageMigrationMode
{
    EmptySwitch,
    SameVolumeMove,
    CrossVolumeCopy
}

public sealed record ManagedStorageMigrationProgress(
    ManagedStorageMigrationPhase Phase, int CompletedItems, int TotalItems,
    long BytesProcessed, long TotalBytes, string? CurrentItemName, bool CheckingCurrentFile = false,
    double BytesPerSecond = 0, int StepIndex = 0, int TotalSteps = 0);

public sealed record ManagedStorageMigrationOptions(
    IProgress<ManagedStorageMigrationProgress>? Progress = null,
    CancellationToken CancellationToken = default,
    bool RestartIfCopiesRemoved = false,
    bool RetainOriginals = false,
    bool IncludeOrphanFolders = false);

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount, string OldRootPath, string NewRootPath,
    int CopiedFileCount, long CopiedBytes, bool Resumed, long? TemporaryBytes = null,
    ManagedStorageMigrationMode Mode = ManagedStorageMigrationMode.CrossVolumeCopy,
    bool OriginalsRetained = false,
    int CleanedSourceFileCount = 0,
    int RemainingSourceFileCount = 0,
    int MigratedOrphanFolderCount = 0,
    int SkippedOrphanFolderCount = 0,
    bool OldRootStillExists = false);

public sealed record ManagedStorageMigrationCleanupResult(
    int CleanedFileCount,
    int RemainingFileCount,
    long FreedBytes,
    bool TargetStillExists);

internal enum StorageMigrationProblem
{
    Unknown, CopiesRemoved, DestinationChanged, DestinationConflict, RootsOverlap,
    VolumeUnavailable, SourceChanged, Unsupported, InUse, AccessDenied, DiskFull
}

internal sealed class StorageMigrationException(
    StorageMigrationProblem problem, string message, Exception? inner = null, string? blockingPath = null)
    : IOException(message, inner)
{
    internal StorageMigrationProblem Problem { get; } = problem;
    internal long? TemporaryBytes { get; set; }
    internal string? BlockingPath { get; set; } = blockingPath;
}

internal sealed class StorageMigrationStoppedException(
    Exception inner, long? temporaryBytes, bool hasPartialCopies = false)
    : OperationCanceledException("Storage migration stopped; originals retained.", inner)
{
    internal long? TemporaryBytes { get; } = temporaryBytes;
    internal bool HasPartialCopies { get; } = hasPartialCopies;
}

// Public so the settings widget facade contract can surface the preview to
// a settings host process (the dialog data is plain numbers).
public sealed record ManagedStorageMigrationPreview(
    int WidgetCount, int FileCount, long TotalBytes, bool CopiesRemoved, long? TemporaryBytes,
    ManagedStorageMigrationMode Mode = ManagedStorageMigrationMode.CrossVolumeCopy,
    long? DestinationAvailableBytes = null,
    long RequiredSpaceBytes = 0,
    int OrphanFolderCount = 0,
    int OrphanFileCount = 0,
    long OrphanTotalBytes = 0,
    bool HasPendingResumeCopies = false,
    bool HasStaleResumeConflict = false);

internal sealed record ManagedStorageMigrationFolder(
    string WidgetId, string WidgetName, string ManagedFolderName, string SourceFolder,
    string DestinationFolder, string? OriginalManagedFolderName, string? OriginalMappedFolderPath,
    bool IsOrphan = false);

internal sealed class StorageMigrationEntry
{
    public int FolderIndex { get; set; }
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public FileAttributes Attributes { get; set; }
    public FileService.FileTransferSourceIdentity? SourceIdentity { get; set; }
    public long? SourceUsn { get; set; }
    public FileService.FileTransferSourceIdentity? DestinationIdentity { get; set; }
}

internal sealed class StorageMigrationJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OldRootPath { get; set; } = "";
    public string NewRootPath { get; set; } = "";
    public string TemporaryDirectory { get; set; } = "";
    public string State { get; set; } = "Planned";
    public string Mode { get; set; } = nameof(ManagedStorageMigrationMode.CrossVolumeCopy);
    public bool RetainOriginals { get; set; }
    public bool IncludeOrphanFolders { get; set; }
    public bool CleanupPending { get; set; }
    public int RemainingSourceFiles { get; set; }
    public List<ManagedStorageMigrationFolder> Folders { get; set; } = [];
    public List<StorageMigrationEntry> Entries { get; set; } = [];
    public List<FileService.FileTransferSourceIdentity?> DestinationIdentities { get; set; } = [];
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public ulong? TargetVolumeSerialNumber { get; set; }
    public string? LastStage { get; set; }
    public string? LastOutcome { get; set; }
    public int CompletedFiles { get; set; }
    public long BytesProcessed { get; set; }
    public long? TemporaryBytes { get; set; }
    public int? LastErrorCode { get; set; }
}

public sealed record ManagedStorageMigrationDiagnostic(
    string TaskId, string State, int CompletedFiles, int TotalFiles,
    long BytesProcessed, long TotalBytes, bool OriginalsRetained, int? LastErrorCode = null,
    long? TemporaryBytes = null);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StorageMigrationJournal))]
internal sealed partial class StorageMigrationJsonContext : JsonSerializerContext { }
