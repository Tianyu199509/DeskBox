using System.Text.Json.Serialization;

namespace DeskBox.Services;

public enum ManagedStorageMigrationPhase { Preparing, CheckingExisting, Copying, Verifying, Committing, Completed }

public sealed record ManagedStorageMigrationProgress(
    ManagedStorageMigrationPhase Phase, int CompletedItems, int TotalItems,
    long BytesProcessed, long TotalBytes, string? CurrentItemName, bool CheckingCurrentFile = false);

public sealed record ManagedStorageMigrationOptions(
    IProgress<ManagedStorageMigrationProgress>? Progress = null,
    CancellationToken CancellationToken = default,
    bool RestartIfCopiesRemoved = false);

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount, string OldRootPath, string NewRootPath,
    int CopiedFileCount, long CopiedBytes, bool Resumed, long? TemporaryBytes = null);

internal enum StorageMigrationProblem
{
    Unknown, CopiesRemoved, DestinationChanged, DestinationConflict, RootsOverlap,
    VolumeUnavailable, SourceChanged, Unsupported, InUse, AccessDenied, DiskFull
}

internal sealed class StorageMigrationException(
    StorageMigrationProblem problem, string message, Exception? inner = null) : IOException(message, inner)
{
    internal StorageMigrationProblem Problem { get; } = problem;
    internal long? TemporaryBytes { get; set; }
}

internal sealed class StorageMigrationStoppedException(Exception inner, long? temporaryBytes)
    : OperationCanceledException("Storage migration stopped; originals retained.", inner)
{
    internal long? TemporaryBytes { get; } = temporaryBytes;
}

internal sealed record ManagedStorageMigrationPreview(
    int WidgetCount, int FileCount, long TotalBytes, bool CopiesRemoved, long? TemporaryBytes);

internal sealed record ManagedStorageMigrationFolder(
    string WidgetId, string WidgetName, string ManagedFolderName, string SourceFolder,
    string DestinationFolder, string? OriginalManagedFolderName, string? OriginalMappedFolderPath);

internal sealed class StorageMigrationEntry
{
    public int FolderIndex { get; set; }
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public FileAttributes Attributes { get; set; }
    public FileService.FileTransferSourceIdentity? SourceIdentity { get; set; }
}

internal sealed class StorageMigrationJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OldRootPath { get; set; } = "";
    public string NewRootPath { get; set; } = "";
    public string TemporaryDirectory { get; set; } = "";
    public string State { get; set; } = "Planned";
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
