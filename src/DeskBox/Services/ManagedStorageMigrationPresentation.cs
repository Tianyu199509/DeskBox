namespace DeskBox.Services;

/// <summary>Pure presentation policy shared by the dialog and its behavioral tests.</summary>
internal static class ManagedStorageMigrationPresentation
{
    internal static double? Percentage(ManagedStorageMigrationProgress progress)
    {
        if (progress.Phase is ManagedStorageMigrationPhase.Preparing or ManagedStorageMigrationPhase.Committing)
            return null;
        if (progress.Phase is ManagedStorageMigrationPhase.Moving or ManagedStorageMigrationPhase.CleaningOrigin)
            return progress.TotalItems > 0 ? Math.Clamp(progress.CompletedItems * 100d / progress.TotalItems, 0, 100) : null;
        if (progress.TotalBytes > 0)
            return Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytes, 0, 100);
        return progress.TotalItems > 0 ? Math.Clamp(progress.CompletedItems * 100d / progress.TotalItems, 0, 100) : null;
    }

    internal static string FormatTransferRate(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 1024d)
            return string.Empty;
        return FileMetaService.FormatSize((long)Math.Round(bytesPerSecond)) + "/s";
    }

    internal static bool NeedsNewLocation(StorageMigrationProblem problem) => problem is
        StorageMigrationProblem.DestinationChanged or StorageMigrationProblem.DestinationConflict or
        StorageMigrationProblem.RootsOverlap or
        StorageMigrationProblem.SourceChanged or StorageMigrationProblem.Unsupported;

    internal static StorageMigrationProblem Classify(Exception exception)
    {
        if (exception is StorageMigrationException known) return known.Problem;
        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode switch
            {
                32 or 33 => StorageMigrationProblem.InUse,
                5 => StorageMigrationProblem.AccessDenied,
                112 or 39 => StorageMigrationProblem.DiskFull,
                21 or 1167 => StorageMigrationProblem.VolumeUnavailable,
                _ => StorageMigrationProblem.Unknown
            };
        }
        if (exception.InnerException is { } inner)
        {
            var nested = Classify(inner);
            if (nested != StorageMigrationProblem.Unknown) return nested;
        }
        return FileService.ClassifyTransferError(exception) switch
        {
            FileService.FileTransferItemErrorKind.InUse => StorageMigrationProblem.InUse,
            FileService.FileTransferItemErrorKind.AccessDenied => StorageMigrationProblem.AccessDenied,
            FileService.FileTransferItemErrorKind.DiskFull => StorageMigrationProblem.DiskFull,
            FileService.FileTransferItemErrorKind.NotFound => StorageMigrationProblem.VolumeUnavailable,
            _ => StorageMigrationProblem.Unknown
        };
    }

    internal static string? ExtractBlockingPath(Exception exception)
    {
        if (exception is StorageMigrationException { BlockingPath: { Length: > 0 } direct })
            return direct;
        return exception.InnerException is { } inner ? ExtractBlockingPath(inner) : null;
    }
}
