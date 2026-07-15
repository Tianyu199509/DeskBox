using DeskBox.Models;

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
