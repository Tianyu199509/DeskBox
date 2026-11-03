using System.Text;

namespace DeskBox.Services;

public sealed record CustomWidgetFileReadOutcome(
    bool Succeeded,
    string? ErrorMessage,
    string ResolvedPath,
    string Content,
    int LineCount,
    long LastWriteUtcTicks);

public sealed record CustomWidgetFileWriteOutcome(
    bool Succeeded,
    string? ErrorMessage,
    string ResolvedPath,
    int BytesWritten);

/// <summary>
/// Safe local text/JSON/Markdown file reader, writer, and watcher for <c>fileText</c> sources
/// and <c>file.writeText</c> actions when <see cref="CustomWidgetPermissionKind.LocalFileAccess"/> is granted.
/// Blocks UNC network shares (preventing SMB NTLM credential leaks) and OS system directories.
/// </summary>
public static class CustomWidgetLocalFileEngine
{
    public const int MaxFileBytes = 512 * 1024;

    public static bool TryResolveSafeLocalPath(
        string? rawPath,
        bool forWrite,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "File path cannot be empty.";
            return false;
        }

        string candidate = rawPath.Trim().Trim('"');
        if (candidate.StartsWith("~/", StringComparison.Ordinal) ||
            candidate.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = Path.Combine(home, candidate[2..]);
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate);

        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
        {
            error = "UNC network paths are blocked for security.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = $"Invalid file path: {ex.Message}";
            return false;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "UNC network paths are blocked for security.";
            return false;
        }

        // Block NTFS Alternate Data Streams (any ':' after drive designator "C:")
        int colonIdx = fullPath.IndexOf(':', 2);
        if (colonIdx >= 2)
        {
            error = "NTFS alternate data streams are blocked for security.";
            return false;
        }

        if (IsDeskBoxInternalProtectedPath(fullPath))
        {
            error = "Accessing internal DeskBox application or configuration directories is forbidden.";
            return false;
        }

        if (forWrite && IsProtectedSystemDirectory(fullPath))
        {
            error = "Writing into protected Windows, Startup, or Program Files directories is forbidden.";
            return false;
        }

        if (!TryValidateNoUnsafeReparsePoint(fullPath, forWrite, out string? reparseErr))
        {
            error = reparseErr;
            return false;
        }

        return true;
    }

    private static bool IsDeskBoxInternalProtectedPath(string fullPath)
    {
        try
        {
            string dataDir = DeskBoxDataPathService.Current.DataDirectory;
            if (!string.IsNullOrWhiteSpace(dataDir) && IsPathInsideRoot(fullPath, dataDir))
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir) && IsPathInsideRoot(fullPath, baseDir))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsProtectedSystemDirectory(string fullPath)
    {
        ReadOnlySpan<Environment.SpecialFolder> protectedRoots =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartup
        ];

        foreach (Environment.SpecialFolder folder in protectedRoots)
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(root) && IsPathInsideRoot(fullPath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPathInsideRoot(string candidateFullPath, string rootPath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(candidateFullPath);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateNoUnsafeReparsePoint(string fullPath, bool forWrite, out string? error)
    {
        error = null;
        try
        {
            string? current = fullPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                FileSystemInfo? fsi = File.Exists(current)
                    ? new FileInfo(current)
                    : Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : null;

                if (fsi is not null && (fsi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo? resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is null)
                    {
                        error = "Symbolic link or directory junction target could not be verified.";
                        return false;
                    }

                    string targetFull = Path.GetFullPath(resolved.FullName);
                    if (targetFull.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        error = "Symbolic link pointing to a UNC network share is blocked for security.";
                        return false;
                    }

                    if (IsDeskBoxInternalProtectedPath(targetFull) ||
                        (forWrite && IsProtectedSystemDirectory(targetFull)))
                    {
                        error = "Symbolic link or junction pointing into a protected directory is forbidden.";
                        return false;
                    }
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
        catch (Exception ex)
        {
            error = $"Path verification failed: {ex.Message}";
            return false;
        }

        return true;
    }

    public static async Task<CustomWidgetFileReadOutcome> ReadTextSourceAsync(
        string? rawPath,
        int tailLines = 0,
        CancellationToken ct = default)
    {
        if (!TryResolveSafeLocalPath(rawPath, forWrite: false, out string fullPath, out string? pathErr))
        {
            return new CustomWidgetFileReadOutcome(false, pathErr, string.Empty, string.Empty, 0, 0);
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new CustomWidgetFileReadOutcome(
                    false,
                    $"File not found: '{fullPath}'.",
                    fullPath,
                    string.Empty,
                    0,
                    0);
            }

            await using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: true);

            long length = fs.Length;
            if (length > MaxFileBytes)
            {
                if (tailLines > 0)
                {
                    fs.Seek(-MaxFileBytes, SeekOrigin.End);
                }
                else
                {
                    return new CustomWidgetFileReadOutcome(
                        false,
                        $"File exceeds {MaxFileBytes / 1024} KB limit ({length} bytes). Set 'tailLines' to tail large log files.",
                        fullPath,
                        string.Empty,
                        0,
                        info.LastWriteTimeUtc.Ticks);
                }
            }

            int bytesToRead = (int)Math.Min(MaxFileBytes, length - fs.Position);
            byte[] buffer = new byte[bytesToRead];
            int offset = 0;
            while (offset < bytesToRead)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(offset, bytesToRead - offset), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                offset += read;
            }

            string content = DecodeTextWithBomSupport(buffer.AsSpan(0, offset));
            string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            int lineCount = lines.Length;
            if (tailLines > 0 && lines.Length > tailLines)
            {
                content = string.Join("\n", lines[^tailLines..]);
                lineCount = tailLines;
            }

            return new CustomWidgetFileReadOutcome(
                true,
                null,
                fullPath,
                content,
                lineCount,
                info.LastWriteTimeUtc.Ticks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CustomWidgetFileReadOutcome(false, ex.Message, fullPath, string.Empty, 0, 0);
        }
    }

    public static async Task<CustomWidgetFileWriteOutcome> WriteTextFileAsync(
        string? rawPath,
        string? content,
        bool append = false,
        CancellationToken ct = default)
    {
        if (!TryResolveSafeLocalPath(rawPath, forWrite: true, out string fullPath, out string? pathErr))
        {
            return new CustomWidgetFileWriteOutcome(false, pathErr, string.Empty, 0);
        }

        string text = content ?? string.Empty;
        byte[] utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (utf8.Length > MaxFileBytes)
        {
            return new CustomWidgetFileWriteOutcome(
                false,
                $"Write payload ({utf8.Length} bytes) exceeds {MaxFileBytes / 1024} KB limit.",
                fullPath,
                0);
        }

        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (append)
            {
                if (File.Exists(fullPath))
                {
                    var existingInfo = new FileInfo(fullPath);
                    if (existingInfo.Length + utf8.Length > MaxFileBytes * 4L)
                    {
                        return new CustomWidgetFileWriteOutcome(
                            false,
                            $"Target file exceeds maximum append size ({MaxFileBytes * 4 / 1024} KB).",
                            fullPath,
                            0);
                    }
                }

                await using var fs = new FileStream(
                    fullPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                await fs.WriteAsync(utf8, ct).ConfigureAwait(false);
            }
            else
            {
                string tempPath = fullPath + $".{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(tempPath, utf8, ct).ConfigureAwait(false);
                DeskBox.Platform.CustomWidgetStorageFileOps.MoveFileOverwrite(tempPath, fullPath);
            }

            return new CustomWidgetFileWriteOutcome(true, null, fullPath, utf8.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CustomWidgetFileWriteOutcome(false, ex.Message, fullPath, 0);
        }
    }

    public static FileSystemWatcher? TryCreateWatcher(string resolvedFullPath, Action onChanged)
    {
        try
        {
            string? dir = Path.GetDirectoryName(resolvedFullPath);
            string fileName = Path.GetFileName(resolvedFullPath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(dir))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => onChanged();
            watcher.Created += (_, _) => onChanged();
            watcher.Renamed += (_, _) => onChanged();
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeTextWithBomSupport(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
