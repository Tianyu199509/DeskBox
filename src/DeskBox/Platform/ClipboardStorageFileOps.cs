namespace DeskBox.Platform;

/// <summary>
/// Confines file-system mutations for the owned %DataDirectory%/clipboard/
/// storage root inside <c>DeskBox.Platform</c>.
/// </summary>
internal static class ClipboardStorageFileOps
{
    public static ReadOnlySpan<byte> DpapiMediaMagic => "DBXDPAPI1\0"u8;

    public static bool IsDpapiProtectedMediaFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[DpapiMediaMagic.Length];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return read == header.Length && header.SequenceEqual(DpapiMediaMagic);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 4096);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16384,
            useAsync: true);
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 4096);
        await fs.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    public static async Task<bool> ProtectMediaFileInPlaceAsync(string? targetPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            return true;
        }

        if (IsDpapiProtectedMediaFile(targetPath))
        {
            return true;
        }

        byte[] raw = await ReadAllBytesSharedAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (raw.Length == 0 || raw.AsSpan().StartsWith(DpapiMediaMagic))
        {
            return true;
        }

        byte[] cipher = DpapiSecretProtector.ProtectBytes(raw);
        if (cipher.Length == 0)
        {
            return false;
        }

        byte[] framed = new byte[DpapiMediaMagic.Length + cipher.Length];
        DpapiMediaMagic.CopyTo(framed);
        cipher.AsSpan().CopyTo(framed.AsSpan(DpapiMediaMagic.Length));

        await WriteBytesAtomicallyAsync(targetPath, framed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static async Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static bool TryReadMediaBytes(string? path, out byte[] plainBytes)
    {
        plainBytes = [];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] raw = ReadAllBytesShared(path);
            if (raw.AsSpan().StartsWith(DpapiMediaMagic))
            {
                return DpapiSecretProtector.TryUnprotectBytes(raw.AsSpan(DpapiMediaMagic.Length), out plainBytes);
            }

            plainBytes = raw;
            return true;
        }
        catch
        {
            plainBytes = [];
            return false;
        }
    }

    public static Windows.Storage.Streams.IRandomAccessStream OpenPlainUnvirtualizedReadStream(string path)
    {
        string resolvedPath = Win32Helper.ResolveUnvirtualizedPhysicalPath(path);
        string target = !string.Equals(resolvedPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resolvedPath)
            ? resolvedPath
            : path;
        return new FileStream(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16384,
            useAsync: true).AsRandomAccessStream();
    }

    public static Windows.Storage.Streams.IRandomAccessStream OpenReadRandomAccessStream(string path)
    {
        string resolvedPath = Win32Helper.ResolveUnvirtualizedPhysicalPath(path);
        string target = !string.Equals(resolvedPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resolvedPath)
            ? resolvedPath
            : path;
        var fs = new FileStream(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16384,
            useAsync: true);
        try
        {
            Span<byte> header = stackalloc byte[10];
            int headerRead = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (headerRead == DpapiMediaMagic.Length && header.SequenceEqual(DpapiMediaMagic))
            {
                int cipherLen = checked((int)(fs.Length - DpapiMediaMagic.Length));
                byte[] cipher = new byte[cipherLen];
                fs.ReadExactly(cipher);
                fs.Dispose();
                if (!DpapiSecretProtector.TryUnprotectBytes(cipher, out byte[] plainBytes))
                {
                    throw new System.Security.Cryptography.CryptographicException("Failed to decrypt protected clipboard media file.");
                }

                return new MemoryStream(plainBytes, writable: false).AsRandomAccessStream();
            }

            fs.Seek(0, SeekOrigin.Begin);
            return fs.AsRandomAccessStream();
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static async Task<byte[]> ReadDecodedMediaBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        string resolvedPath = Win32Helper.ResolveUnvirtualizedPhysicalPath(path);
        string target = !string.Equals(resolvedPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resolvedPath)
            ? resolvedPath
            : path;
        byte[] raw = await ReadAllBytesSharedAsync(target, cancellationToken).ConfigureAwait(false);
        if (raw.AsSpan().StartsWith(DpapiMediaMagic))
        {
            if (!DpapiSecretProtector.TryUnprotectBytes(raw.AsSpan(DpapiMediaMagic.Length), out byte[] plainBytes))
            {
                throw new System.Security.Cryptography.CryptographicException("Failed to decrypt protected clipboard media file.");
            }

            return plainBytes;
        }

        return raw;
    }

    public static async Task<Windows.Storage.Streams.RandomAccessStreamReference> CreateInMemoryBitmapStreamRefAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadDecodedMediaBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(
            new MemoryStream(bytes, writable: false).AsRandomAccessStream());
    }

    public static Windows.Media.Core.MediaSource CreateMediaSourceFromUri(
        Uri uri,
        out Windows.Storage.Streams.IRandomAccessStream? ownedStream)
    {
        ownedStream = null;
        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            string localPath = uri.LocalPath;
            if (!File.Exists(localPath) &&
                !uri.OriginalString.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathRooted(uri.OriginalString) &&
                File.Exists(uri.OriginalString))
            {
                localPath = uri.OriginalString;
            }

            string ext = Path.GetExtension(localPath).ToLowerInvariant();
            string contentType = ext switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".ogg" or ".oga" => "audio/ogg",
                ".opus" => "audio/opus",
                ".flac" => "audio/flac",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/vnd.dlna.adts",
                ".wma" => "audio/x-ms-wma",
                ".mp4" or ".m4v" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".wmv" => "video/x-ms-wmv",
                ".webm" => "video/webm",
                ".mkv" => "video/x-matroska",
                _ => string.Empty
            };
            Windows.Storage.Streams.IRandomAccessStream stream = OpenPlainUnvirtualizedReadStream(localPath);
            try
            {
                Windows.Media.Core.MediaSource created = Windows.Media.Core.MediaSource.CreateFromStream(stream, contentType);
                ownedStream = stream;
                return created;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return Windows.Media.Core.MediaSource.CreateFromUri(uri);
    }

    public static void CopyRawFileOverwrite(string sourcePath, string destinationPath)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    public static async Task WriteBytesIfMissingAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static void CopyFileOverwrite(string sourcePath, string destinationPath)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (IsDpapiProtectedMediaFile(sourcePath))
        {
            if (!TryReadMediaBytes(sourcePath, out byte[] plainBytes))
            {
                throw new System.Security.Cryptography.CryptographicException("Failed to decrypt protected clipboard media file.");
            }

            File.WriteAllBytes(destinationPath, plainBytes);
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    public static void WriteTextAtomically(string targetPath, string content)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // Ignore transient file locks on thumbnails or temp exports.
        }

        return false;
    }

    public static (int FileCount, long TotalBytes) MeasureDirectoryFiles(string directoryPath) =>
        MeasureDirectoryFiles(directoryPath, SearchOption.TopDirectoryOnly);

    public static (int FileCount, long TotalBytes) MeasureDirectoryFilesRecursive(string directoryPath) =>
        MeasureDirectoryFiles(directoryPath, SearchOption.AllDirectories);

    private static (int FileCount, long TotalBytes) MeasureDirectoryFiles(
        string directoryPath,
        SearchOption searchOption)
    {
        if (!Directory.Exists(directoryPath))
        {
            return (0, 0L);
        }

        int count = 0;
        long bytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", searchOption))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists)
                    {
                        count++;
                        bytes += info.Length;
                    }
                }
                catch
                {
                    // Skip concurrently deleted files.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration failures.
        }

        return (count, bytes);
    }

    public static int DeleteOrphanedDroppedEntries(
        string droppedDirectoryPath,
        IReadOnlySet<string> referencedFullPaths)
    {
        if (!Directory.Exists(droppedDirectoryPath))
        {
            return 0;
        }

        var normalizedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in referencedFullPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                normalizedRefs.Add(Path.GetFullPath(raw).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(droppedDirectoryPath, "*", SearchOption.AllDirectories))
            {
                string fullFile;
                try
                {
                    fullFile = Path.GetFullPath(file);
                }
                catch
                {
                    continue;
                }

                if (!IsPathReferencedByDroppedEntries(fullFile, normalizedRefs) &&
                    TryDeleteFile(fullFile))
                {
                    deleted++;
                }
            }

            // Prune emptied batch/folder trees deepest-first while keeping droppedDirectoryPath itself.
            string[] dirs = Directory.EnumerateDirectories(droppedDirectoryPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(static d => d.Length)
                .ToArray();
            foreach (string dir in dirs)
            {
                try
                {
                    string fullDir = Path.GetFullPath(dir).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    if (!IsPathReferencedByDroppedEntries(fullDir, normalizedRefs) &&
                        Directory.Exists(dir) &&
                        !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
                catch
                {
                    // Best-effort empty directory cleanup.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }

        return deleted;
    }

    private static bool IsPathReferencedByDroppedEntries(
        string candidateFullPath,
        HashSet<string> normalizedRefs)
    {
        if (normalizedRefs.Contains(candidateFullPath))
        {
            return true;
        }

        foreach (string referenced in normalizedRefs)
        {
            string prefix = referenced.EndsWith(Path.DirectorySeparatorChar)
                ? referenced
                : referenced + Path.DirectorySeparatorChar;
            if (candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int DeleteOrphanedFiles(string directoryPath, IReadOnlySet<string> referencedFileNames)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!referencedFileNames.Contains(name) && TryDeleteFile(file))
                {
                    deleted++;
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }

        return deleted;
    }

    public static void CleanupOldFiles(string directoryPath, TimeSpan maxAge)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        DateTime cutoffUtc = DateTime.UtcNow - maxAge;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        TryDeleteFile(file);
                    }
                }
                catch
                {
                    // Ignore locked export files.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }
    }
}
