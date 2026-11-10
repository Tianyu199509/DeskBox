using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Archive formats offered by the grid "Compress to" submenu. 7z has no
/// BCL writer and would need the LZMA SDK (batch debt pending a go).
/// </summary>
public enum ArchiveCompressionFormat
{
    Zip,
    Tar
}

/// <summary>
/// Managed archive support for the grid context menu ("Compress to" /
/// "Extract all"). Runs entirely inside the DeskBox file pipeline, so it
/// works without any third-party archiver installed. Only .zip is
/// extracted; other archive formats belong to the full system menu.
/// </summary>
public static class ArchiveZipService
{
    public const long DefaultMaxExtractedArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const int CopyBufferSize = 81920;
    private static readonly DateTimeOffset ZipMinTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ZipMaxTimestamp = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    static ArchiveZipService()
    {
        // Zips produced by common Chinese archivers store GBK entry names
        // without the UTF-8 flag; the code pages provider is required to
        // decode them with the system ANSI code page.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Fall back to default UTF-8 decoding (names may garble).
        }
    }

    /// <summary>
    /// Creates an archive in the requested format next to the first source
    /// item; entries keep their relative paths from the common container.
    /// Multi-item selections are named after the first item, matching
    /// Explorer's compress behavior.
    /// </summary>
    public static Task<string> CompressAsync(
        IReadOnlyList<string> sourcePaths,
        ArchiveCompressionFormat format) =>
        CompressAsync(sourcePaths, format, CancellationToken.None);

    public static Task<string> CompressAsync(
        IReadOnlyList<string> sourcePaths,
        ArchiveCompressionFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one source path is required.",
                nameof(sourcePaths));
        }

        string container =
            Path.GetDirectoryName(Path.GetFullPath(sourcePaths[0])) ??
            Environment.CurrentDirectory;

        string baseName = GetCompressionBaseName(sourcePaths);
        string archivePath = FileService.GetAvailablePath(Path.Combine(
            container,
            baseName + (format == ArchiveCompressionFormat.Tar
                ? ".tar"
                : ".zip")));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string partialPath = ArchiveFileOps.CreatePartialArchivePath(archivePath);
            var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(archivePath),
                Path.GetFullPath(partialPath)
            };

            try
            {
                if (format == ArchiveCompressionFormat.Tar)
                {
                    WriteTar(sourcePaths, container, partialPath, excludedPaths, cancellationToken);
                }
                else
                {
                    WriteZip(sourcePaths, container, partialPath, excludedPaths, cancellationToken);
                }

                return ArchiveFileOps.CommitPartialArchive(partialPath, archivePath);
            }
            catch
            {
                ArchiveFileOps.TryDeletePartialArchive(partialPath);
                throw;
            }
        }, cancellationToken);
    }

    private static void WriteZip(
        IReadOnlyList<string> sourcePaths,
        string container,
        string partialZipPath,
        IReadOnlySet<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            partialZipPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: false);

        var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(sourcePath);
            if (excludedPaths.Contains(fullPath))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(container, fullPath)
                .Replace('\\', '/');
            if (File.Exists(fullPath))
            {
                if (writtenEntries.Add(relativePath))
                {
                    WriteZipFileEntry(archive, fullPath, relativePath, cancellationToken);
                }
            }
            else if (Directory.Exists(fullPath))
            {
                string rootDirEntry = relativePath.TrimEnd('/') + "/";
                if (writtenEntries.Add(rootDirEntry))
                {
                    archive.CreateEntry(rootDirEntry, CompressionLevel.Optimal);
                }

                var emptyDirs = new List<string>();
                var files = new List<string>();
                ArchiveFileOps.CollectDirectoryEntries(
                    fullPath,
                    excludedPaths,
                    emptyDirs,
                    files,
                    cancellationToken);

                foreach (string emptyDir in emptyDirs)
                {
                    string dirRelative = Path.GetRelativePath(container, emptyDir)
                        .Replace('\\', '/')
                        .TrimEnd('/') + "/";
                    if (writtenEntries.Add(dirRelative))
                    {
                        archive.CreateEntry(dirRelative, CompressionLevel.Optimal);
                    }
                }

                foreach (string file in files)
                {
                    string fileRelative = Path.GetRelativePath(container, file)
                        .Replace('\\', '/');
                    if (writtenEntries.Add(fileRelative))
                    {
                        WriteZipFileEntry(archive, file, fileRelative, cancellationToken);
                    }
                }
            }
        }
    }

    private static void WriteZipFileEntry(
        ZipArchive archive,
        string sourceFilePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        using var sourceStream = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: CopyBufferSize);

        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        try
        {
            DateTimeOffset modified = File.GetLastWriteTime(sourceFilePath);
            if (modified < ZipMinTimestamp)
            {
                modified = ZipMinTimestamp;
            }
            else if (modified > ZipMaxTimestamp)
            {
                modified = ZipMaxTimestamp;
            }

            entry.LastWriteTime = modified;
        }
        catch
        {
            // Keep default timestamp if metadata query fails.
        }

        using Stream entryStream = entry.Open();
        CopyStreamWithCancellation(sourceStream, entryStream, cancellationToken);
    }

    private static void WriteTar(
        IReadOnlyList<string> sourcePaths,
        string container,
        string partialTarPath,
        IReadOnlySet<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            partialTarPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new TarWriter(stream);

        var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateFilesSafe(sourcePaths, excludedPaths, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(container, file)
                .Replace('\\', '/');
            if (!writtenEntries.Add(relativePath))
            {
                continue;
            }

            using var sourceStream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: CopyBufferSize);
            var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, relativePath)
            {
                DataStream = sourceStream
            };
            try
            {
                tarEntry.ModificationTime = File.GetLastWriteTimeUtc(file);
            }
            catch
            {
                // Keep default ModificationTime when unavailable.
            }

            writer.WriteEntry(tarEntry);
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(
        IReadOnlyList<string> sourcePaths,
        IReadOnlySet<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(sourcePath);
            if (File.Exists(fullPath))
            {
                if (!excludedPaths.Contains(fullPath))
                {
                    yield return fullPath;
                }
            }
            else if (Directory.Exists(fullPath))
            {
                var emptyDirs = new List<string>();
                var files = new List<string>();
                ArchiveFileOps.CollectDirectoryEntries(
                    fullPath,
                    excludedPaths,
                    emptyDirs,
                    files,
                    cancellationToken);
                foreach (string file in files)
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// ZIP shorthand for <see cref="CompressAsync(IReadOnlyList{string}, ArchiveCompressionFormat)"/>.
    /// </summary>
    public static Task<string> CompressToZipAsync(
        IReadOnlyList<string> sourcePaths) =>
        CompressAsync(sourcePaths, ArchiveCompressionFormat.Zip, CancellationToken.None);

    public static Task<string> CompressToZipAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken) =>
        CompressAsync(sourcePaths, ArchiveCompressionFormat.Zip, cancellationToken);

    /// <summary>
    /// Extracts every entry into a fresh folder named after the archive
    /// (name collisions get the standard " (2)" suffix), like the system
    /// Extract All default. Rejects archive entries that escape the
    /// destination folder or exceed the cumulative extraction budget.
    /// </summary>
    public static Task<string> ExtractAllAsync(string archivePath) =>
        ExtractAllCoreAsync(archivePath, DefaultMaxExtractedArchiveBytes, CancellationToken.None);

    public static Task<string> ExtractAllAsync(
        string archivePath,
        CancellationToken cancellationToken) =>
        ExtractAllCoreAsync(archivePath, DefaultMaxExtractedArchiveBytes, cancellationToken);

    internal static Task<string> ExtractAllCoreAsync(
        string archivePath,
        long maxExtractedBytes,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The archive does not exist.",
                archivePath);
        }

        if (maxExtractedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        }

        string container =
            Path.GetDirectoryName(Path.GetFullPath(archivePath)) ??
            Environment.CurrentDirectory;
        string baseName = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "archive";
        }

        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(container, baseName));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destinationPath);
            try
            {
                Encoding? entryEncoding = ResolveEntryEncoding(archivePath);
                using var stream = new FileStream(
                    archivePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(
                    stream,
                    ZipArchiveMode.Read,
                    leaveOpen: false,
                    entryNameEncoding: entryEncoding);
                string destinationRoot = Path.GetFullPath(destinationPath);
                long totalExtractedBytes = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExtractEntry(
                        entry,
                        destinationRoot,
                        maxExtractedBytes,
                        ref totalExtractedBytes,
                        cancellationToken);
                }

                return destinationPath;
            }
            catch
            {
                ArchiveFileOps.CleanupFailedExtractionDirectory(destinationPath);
                throw;
            }
        }, cancellationToken);
    }

    internal static string GetCompressionBaseName(
        IReadOnlyList<string> sourcePaths)
    {
        string trimmedFirst = sourcePaths[0].TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string fullFirst = Path.GetFullPath(trimmedFirst);
        string firstName = Path.GetFileName(trimmedFirst);
        string baseName = Directory.Exists(fullFirst)
            ? firstName
            : Path.GetFileNameWithoutExtension(firstName);
        return string.IsNullOrWhiteSpace(baseName) ? "archive" : baseName;
    }

    /// <summary>
    /// Picks the entry name decoding: entries that come back with UTF-8
    /// replacement characters were encoded in the system ANSI code page
    /// (typical for locally produced archives), so the archive is reopened
    /// with that code page.
    /// </summary>
    private static Encoding? ResolveEntryEncoding(string archivePath)
    {
        try
        {
            using var probeStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var probe = new ZipArchive(
                probeStream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            foreach (ZipArchiveEntry entry in probe.Entries)
            {
                if (entry.FullName.Contains('\uFFFD'))
                {
                    try
                    {
                        return Encoding.GetEncoding(0);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            // Not a readable zip; let the extraction pass produce the
            // caller-facing failure.
        }

        return null;
    }

    private static void ExtractEntry(
        ZipArchiveEntry entry,
        string destinationRoot,
        long maxExtractedBytes,
        ref long totalExtractedBytes,
        CancellationToken cancellationToken)
    {
        bool isDirectoryEntry =
            entry.FullName.EndsWith('/') ||
            entry.FullName.EndsWith('\\');
        string entryPath = entry.FullName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.Combine(destinationRoot, entryPath));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            throw new InvalidDataException(
                $"The archive contains an invalid entry name: {entry.FullName}");
        }

        string rootPrefix = destinationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool isInsideRoot = fullPath.StartsWith(
            rootPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (!isInsideRoot)
        {
            throw new InvalidDataException(
                "The archive contains an entry that would extract " +
                "outside the destination folder.");
        }

        if (isDirectoryEntry)
        {
            Directory.CreateDirectory(fullPath);
            return;
        }

        if (entry.Length > maxExtractedBytes ||
            totalExtractedBytes > maxExtractedBytes - entry.Length)
        {
            throw new InvalidDataException(
                "The archive exceeds the maximum allowed extracted size.");
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using Stream entryStream = entry.Open();
        using var outputStream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: CopyBufferSize);

        byte[] buffer = new byte[CopyBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = entryStream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            totalExtractedBytes += bytesRead;
            if (totalExtractedBytes > maxExtractedBytes)
            {
                throw new InvalidDataException(
                    "The archive exceeds the maximum allowed extracted size.");
            }

            outputStream.Write(buffer, 0, bytesRead);
        }

        outputStream.Flush();
        try
        {
            DateTime lastWrite = entry.LastWriteTime.LocalDateTime;
            if (lastWrite.Year >= 1980)
            {
                File.SetLastWriteTime(fullPath, lastWrite);
            }
        }
        catch
        {
            // Keep default timestamp if metadata restore fails.
        }
    }

    private static void CopyStreamWithCancellation(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }
}
