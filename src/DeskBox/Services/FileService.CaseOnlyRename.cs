using DeskBox.Platform;

namespace DeskBox.Services;

public sealed partial class FileService
{
    private const string CaseOnlyRenameTemporaryPrefix =
        ".deskbox-case-rename-";

    internal static bool IsCaseOnlyPathChange(
        string sourcePath,
        string destinationPath) =>
        !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal) &&
        string.Equals(
            sourcePath,
            destinationPath,
            StringComparison.OrdinalIgnoreCase);

    private static async Task MoveCaseOnlyEntryAsync(
        string sourcePath,
        string destinationPath)
    {
        bool sourceIsFile = File.Exists(sourcePath);
        bool sourceIsDirectory = Directory.Exists(sourcePath);
        if (!sourceIsFile && !sourceIsDirectory)
        {
            throw new FileNotFoundException(
                "The file or folder to rename no longer exists.",
                sourcePath);
        }

        ThrowIfExactDestinationEntryExists(
            sourcePath,
            destinationPath);

        // NTFS and ReFS support single-step in-place case-only renames via
        // MoveFileExW(dwFlags: 0) without exposing an intermediate temporary
        // filename. Fall back to the two-hop rename only on filesystems such
        // as FAT32/exFAT that reject same-case-insensitive target names.
        if (await Task.Run(() => StorageMigrationNativeMethods.TryRenameCaseOnlyInPlace(
                sourcePath,
                destinationPath)))
        {
            return;
        }

        string temporaryPath = CreateCaseOnlyRenameTemporaryPath(
            sourcePath);
        await MoveEntryAtomicallyAsync(sourcePath, temporaryPath);

        try
        {
            await MoveEntryAtomicallyAsync(temporaryPath, destinationPath);
        }
        catch (Exception renameException)
        {
            try
            {
                if ((File.Exists(temporaryPath) ||
                     Directory.Exists(temporaryPath)) &&
                    !File.Exists(sourcePath) &&
                    !Directory.Exists(sourcePath))
                {
                    await MoveEntryAtomicallyAsync(temporaryPath, sourcePath);
                }
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    $"The case-only rename failed and DeskBox could not restore " +
                    $"'{sourcePath}' from its temporary name.",
                    new AggregateException(
                        renameException,
                        rollbackException));
            }

            throw;
        }
    }

    private static async Task MoveEntryAtomicallyAsync(
        string sourcePath,
        string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            await Task.Run(() => File.Move(sourcePath, destinationPath));
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await Task.Run(() => Directory.Move(sourcePath, destinationPath));
            return;
        }

        throw new FileNotFoundException(
            "The file or folder to rename no longer exists.",
            sourcePath);
    }

    private static void ThrowIfExactDestinationEntryExists(
        string sourcePath,
        string destinationPath)
    {
        string? parentDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) ||
            !Directory.Exists(parentDirectory))
        {
            return;
        }

        string destinationName = Path.GetFileName(destinationPath);
        foreach (string entryPath in
                 Directory.EnumerateFileSystemEntries(parentDirectory))
        {
            if (string.Equals(entryPath, sourcePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(
                    Path.GetFileName(entryPath),
                    destinationName,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"The destination '{destinationPath}' already exists.");
            }
        }
    }

    private static string CreateCaseOnlyRenameTemporaryPath(
        string sourcePath)
    {
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new IOException(
                $"Unable to determine the parent folder for '{sourcePath}'.");
        }

        for (int attempt = 0; attempt < 32; attempt++)
        {
            string candidate = Path.Combine(
                parentDirectory,
                $"{CaseOnlyRenameTemporaryPrefix}{Guid.NewGuid():N}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Unable to allocate a temporary name beside '{sourcePath}'.");
    }
}
