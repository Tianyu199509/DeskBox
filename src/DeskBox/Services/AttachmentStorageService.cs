using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public static class AttachmentStorageService
{
    public static async Task<TodoAttachment?> ImportPathAsync(
        string sourcePath,
        string managedDirectory,
        bool copyToManagedStorage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        string normalizedSourcePath = Path.GetFullPath(sourcePath);
        if (!copyToManagedStorage)
        {
            return CreateAttachment(normalizedSourcePath, TodoAttachment.LinkedStorageMode);
        }

        Directory.CreateDirectory(managedDirectory);
        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(managedDirectory, Path.GetFileName(normalizedSourcePath)));
        await Task.Run(() => File.Copy(normalizedSourcePath, destinationPath, overwrite: false));
        return CreateAttachment(destinationPath, TodoAttachment.ManagedStorageMode);
    }

    public static async Task<TodoAttachment?> SaveStreamAsync(
        Stream source,
        string? fileName,
        string managedDirectory,
        CancellationToken cancellationToken = default)
    {
        if (source is null || !source.CanRead)
        {
            return null;
        }

        Directory.CreateDirectory(managedDirectory);
        string normalizedName = FileService.SanitizeFileSystemName(Path.GetFileName(fileName));
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            normalizedName = $"Attachment-{DateTime.Now:yyyyMMdd-HHmmss}";
        }

        string destinationPath = FileService.GetAvailablePath(Path.Combine(managedDirectory, normalizedName));
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        return CreateAttachment(destinationPath, TodoAttachment.ManagedStorageMode);
    }

    public static string GetAttachmentType(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tiff" or ".tif" or ".heic" or ".heif" => "image",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => "video",
            ".pdf" => "pdf",
            _ => "file"
        };
    }

    /// <summary>
    /// Deletes a managed attachment copy when — and only when — the path is
    /// provably inside the managed attachment tree. The persisted
    /// IsManagedCopy flag alone is not proof of ownership: a stale or
    /// hand-edited store could point that flag at a user's original file, so
    /// anything outside the managed root is always kept. Deletion delegates
    /// to the caller's recycle kernel (the file-safety service recycling the
    /// file); without a kernel the copy is kept — fail-safe.
    /// </summary>
    public static async Task<bool> TryDeleteManagedCopyAsync(
        string filePath,
        string managedRootDirectory,
        Func<string, Task<bool>>? recycleFileAsync)
    {
        try
        {
            string fullRoot = Path.GetFullPath(managedRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(filePath);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                App.Log(
                    $"[AttachmentStorage] Kept attachment file outside the managed tree: '{filePath}'");
                return false;
            }

            if (recycleFileAsync is null)
            {
                App.Log(
                    $"[AttachmentStorage] No recycle kernel available; kept managed attachment copy: '{filePath}'");
                return false;
            }

            return await recycleFileAsync(fullPath);
        }
        catch (Exception ex)
        {
            App.Log($"[AttachmentStorage] Failed to delete managed attachment '{filePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Recycles unreferenced managed attachment copies under
    /// <paramref name="managedRootDirectory"/> via <paramref name="recycleFileAsync"/>
    /// and prunes any emptied <c>&lt;itemId&gt;</c> bucket directories.
    /// </summary>
    public static async Task<int> RecycleUnreferencedManagedCopiesAsync(
        string managedRootDirectory,
        IReadOnlySet<string> referencedPaths,
        Func<string, Task<bool>>? recycleFileAsync,
        IReadOnlySet<string>? knownTombstoneItemIds = null,
        TimeSpan? unknownDirectoryGracePeriod = null)
    {
        if (recycleFileAsync is null || string.IsNullOrWhiteSpace(managedRootDirectory))
        {
            return 0;
        }

        IReadOnlyList<string> candidates = AttachmentCleanupFileOps.FindUnreferencedManagedFiles(
            managedRootDirectory,
            referencedPaths,
            knownTombstoneItemIds,
            unknownDirectoryGracePeriod);
        int recycledCount = 0;
        foreach (string candidate in candidates)
        {
            if (await TryDeleteManagedCopyAsync(candidate, managedRootDirectory, recycleFileAsync))
            {
                recycledCount++;
            }
        }

        AttachmentCleanupFileOps.RemoveEmptyManagedSubdirectories(managedRootDirectory);
        return recycledCount;
    }

    private static TodoAttachment CreateAttachment(string path, string storageMode)
    {
        return new TodoAttachment
        {
            FilePath = path,
            DisplayName = Path.GetFileName(path),
            Type = GetAttachmentType(path),
            StorageMode = storageMode,
            AddedAt = DateTimeOffset.UtcNow
        };
    }
}
