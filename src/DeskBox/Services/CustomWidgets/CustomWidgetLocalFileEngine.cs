using System.Globalization;
using System.IO.Enumeration;
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

public sealed record CustomWidgetDirectoryListOutcome(
    bool Succeeded,
    string? ErrorMessage,
    string ResolvedDirectory,
    IReadOnlyList<CustomWidgetDropFileContext> Files);

public sealed record CustomWidgetFileManageOutcome(
    bool Succeeded,
    string? ErrorMessage,
    string Operation,
    string SourcePath,
    string TargetPath,
    string Summary);

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

        string candidate = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(rawPath);
        candidate = Environment.ExpandEnvironmentVariables(candidate);
        candidate = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(candidate);

        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
        {
            error = "UNC network paths are blocked for security.";
            return false;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            error = $"Relative file path '{rawPath.Trim()}' is ambiguous. Use a full path, '~/...', '%USERPROFILE%\\...', or a known folder name such as 'Downloads' or 'Desktop'.";
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

        // 8.3 short names (C:\PROGRA~1) and subst/junction aliases defeat a plain
        // prefix comparison, so protected roots are matched against the final identity.
        string identityPath = FileService.TryResolvePathIdentity(fullPath, out string resolvedIdentity)
            ? resolvedIdentity
            : fullPath;

        if (IsDeskBoxInternalProtectedPath(identityPath))
        {
            error = "Accessing internal DeskBox application or configuration directories is forbidden.";
            return false;
        }

        if (forWrite && IsProtectedSystemDirectory(identityPath))
        {
            error = "Writing into protected Windows, Startup, or Program Files directories is forbidden.";
            return false;
        }

        if (forWrite &&
            (AppDistributionService.Current.IsMicrosoftStore || AppDistributionService.Current.IsPackaged) &&
            (IsPackagedVirtualizedAppDataPath(fullPath) || IsPackagedVirtualizedAppDataPath(identityPath)))
        {
            error = "Writing, moving, renaming, or deleting files under %LOCALAPPDATA% or %APPDATA% is blocked in packaged builds to prevent MSIX virtual file system redirection. Choose a user folder such as Documents, Desktop, or Downloads.";
            return false;
        }

        if (!TryValidateNoUnsafeReparsePoint(fullPath, forWrite, out string? reparseErr))
        {
            error = reparseErr;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a filesystem-safe folder name for a widget's managed Documents export directory.
    /// </summary>
    public static string SanitizeWidgetFolderSlug(string? widgetIdOrSlug)
    {
        if (string.IsNullOrWhiteSpace(widgetIdOrSlug))
        {
            return "widget";
        }

        var sb = new StringBuilder(widgetIdOrSlug.Length);
        bool lastWasDash = false;
        foreach (char c in widgetIdOrSlug.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if ((c is '-' or '.' or ' ') && sb.Length > 0 && !lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        string cleaned = sb.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "widget";
        }

        // Guard Windows reserved device names (CON, PRN, AUX, NUL, COM1..9, LPT1..9)
        if (cleaned is "con" or "prn" or "aux" or "nul" ||
            ((cleaned.StartsWith("com", StringComparison.Ordinal) || cleaned.StartsWith("lpt", StringComparison.Ordinal)) &&
             cleaned.Length == 4 &&
             char.IsAsciiDigit(cleaned[3]) &&
             cleaned[3] != '0'))
        {
            cleaned = "w-" + cleaned;
        }

        return cleaned.Length > 64 ? cleaned[..64].TrimEnd('-') : cleaned;
    }

    /// <summary>
    /// Resolves the widget's managed export root under <c>%USERPROFILE%\Documents\DeskBox\&lt;safeSlug&gt;</c>.
    /// Lives under User Documents so both Direct and Microsoft Store MSIX builds bypass AppData VFS redirection.
    /// </summary>
    public static string ResolveWidgetManagedDocumentsRoot(string? widgetFolderSlug)
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            docs = !string.IsNullOrWhiteSpace(profile)
                ? Path.Combine(profile, "Documents")
                : Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(docs, "DeskBox", SanitizeWidgetFolderSlug(widgetFolderSlug)));
    }

    /// <summary>
    /// Unified dual-tier path resolver for widget file actions (<c>file.writeText</c>, <c>file.reveal</c>,
    /// <c>file.open</c>, <c>file.listDir</c>, <c>file.manage</c>).
    /// Bare relative paths (e.g. <c>export.md</c>, <c>reports/week.csv</c>, <c>.</c>) resolve cleanly into
    /// <c>%USERPROFILE%\Documents\DeskBox\&lt;safeSlug&gt;\</c> with strict traversal protection.
    /// Explicit known-folder/rooted paths (<c>Desktop\...</c>, <c>~/...</c>, <c>C:\...</c>) delegate to
    /// <see cref="TryResolveSafeLocalPath"/> and mark <paramref name="isManagedWidgetExportPath"/> as <c>false</c>
    /// unless inside the widget's managed Documents root.
    /// </summary>
    public static bool TryResolveWidgetFilePath(
        string? rawPath,
        string? widgetFolderSlug,
        bool forWrite,
        out string fullPath,
        out bool isManagedWidgetExportPath,
        out string? error)
    {
        fullPath = string.Empty;
        isManagedWidgetExportPath = false;
        error = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "File path cannot be empty.";
            return false;
        }

        string trimmed = rawPath.Trim().Trim('"').Trim();
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            error = "file:// URIs are not valid file paths. Pass a relative filename (e.g. 'export.md') or local folder path (e.g. 'Desktop\\export.md').";
            return false;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            error = "UNC network paths are blocked for security.";
            return false;
        }

        string expanded = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(trimmed);
        expanded = Environment.ExpandEnvironmentVariables(expanded);
        expanded = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(expanded);

        string managedRoot = ResolveWidgetManagedDocumentsRoot(widgetFolderSlug);

        if (Path.IsPathFullyQualified(expanded))
        {
            if (!TryResolveSafeLocalPath(expanded, forWrite, out fullPath, out error))
            {
                return false;
            }

            isManagedWidgetExportPath = !string.IsNullOrWhiteSpace(managedRoot) &&
                                        IsPathInsideRoot(fullPath, managedRoot);
            return true;
        }

        // Reject root-relative (\foo), drive-relative (C:foo), ADS colons, and traversal segments (..).
        if (trimmed.StartsWith('\\') ||
            trimmed.StartsWith('/') ||
            trimmed.Contains(':', StringComparison.Ordinal))
        {
            error = $"Relative file path '{trimmed}' cannot start with a root slash or contain a drive/stream colon.";
            return false;
        }

        string[] segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>(segments.Length);
        foreach (string seg in segments)
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                error = $"Relative file path '{trimmed}' cannot contain parent directory traversal ('..').";
                return false;
            }

            cleanSegments.Add(seg);
        }

        string candidate = cleanSegments.Count == 0
            ? managedRoot
            : Path.Combine([managedRoot, .. cleanSegments]);

        if (!TryResolveSafeLocalPath(candidate, forWrite, out fullPath, out error))
        {
            return false;
        }

        if (!IsPathInsideRoot(fullPath, managedRoot))
        {
            error = $"Relative file path '{trimmed}' resolved outside the widget export directory.";
            fullPath = string.Empty;
            return false;
        }

        isManagedWidgetExportPath = true;
        return true;
    }

    internal static bool IsPackagedVirtualizedAppDataPath(string fullPath)
    {
        try
        {
            string deskBoxRoot = DeskBoxDataPathService.Current.RootPath;
            if (!string.IsNullOrWhiteSpace(deskBoxRoot) && IsPathInsideRoot(fullPath, deskBoxRoot))
            {
                return false;
            }
        }
        catch
        {
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData) && IsPathInsideRoot(fullPath, localAppData))
        {
            return true;
        }

        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData) && IsPathInsideRoot(fullPath, roamingAppData))
        {
            return true;
        }

        return false;
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
                        (forWrite && IsProtectedSystemDirectory(targetFull)) ||
                        (forWrite &&
                         (AppDistributionService.Current.IsMicrosoftStore || AppDistributionService.Current.IsPackaged) &&
                         IsPackagedVirtualizedAppDataPath(targetFull)))
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

    public static FileSystemWatcher? TryCreateWatcher(
        string resolvedFullPath,
        Action onChanged,
        bool includeSubdirectories = false)
    {
        try
        {
            if (Directory.Exists(resolvedFullPath))
            {
                var dirWatcher = new FileSystemWatcher(resolvedFullPath, "*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = includeSubdirectories,
                    EnableRaisingEvents = true
                };
                dirWatcher.Changed += (_, _) => onChanged();
                dirWatcher.Created += (_, _) => onChanged();
                dirWatcher.Deleted += (_, _) => onChanged();
                dirWatcher.Renamed += (_, _) => onChanged();
                return dirWatcher;
            }

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

    public static CustomWidgetDropFileContext BuildDropFileContext(string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return CustomWidgetDropFileContext.Empty;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolvedPath);
        }
        catch
        {
            fullPath = resolvedPath;
        }

        if (Directory.Exists(fullPath))
        {
            var dirInfo = new DirectoryInfo(fullPath);
            string dirName = string.IsNullOrEmpty(dirInfo.Name) ? fullPath : dirInfo.Name;
            string modIso = SafeFormatIsoUtc(dirInfo.LastWriteTimeUtc);
            return new CustomWidgetDropFileContext
            {
                Name = dirName,
                Path = fullPath,
                Extension = string.Empty,
                SizeText = "Folder",
                SizeBytes = 0,
                ModifiedIso = modIso
            };
        }

        var fileInfo = new FileInfo(fullPath);
        long bytes = fileInfo.Exists ? Math.Max(0, fileInfo.Length) : 0;
        string ext = (fileInfo.Extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        string modifiedIso = fileInfo.Exists ? SafeFormatIsoUtc(fileInfo.LastWriteTimeUtc) : string.Empty;
        return new CustomWidgetDropFileContext
        {
            Name = fileInfo.Name,
            Path = fullPath,
            Extension = ext,
            SizeText = FormatByteSize(bytes),
            SizeBytes = bytes,
            ModifiedIso = modifiedIso
        };
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:0.#} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:0.##} MB";
        }

        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    public static Task<CustomWidgetDirectoryListOutcome> ListDirectoryAsync(
        string? rawDirPath,
        string? pattern = null,
        bool recursive = false,
        string? sort = null,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!TryResolveSafeLocalPath(rawDirPath, forWrite: false, out string fullDir, out string? pathErr))
            {
                return new CustomWidgetDirectoryListOutcome(false, pathErr, string.Empty, []);
            }

            if (!Directory.Exists(fullDir))
            {
                return new CustomWidgetDirectoryListOutcome(
                    false,
                    $"Directory does not exist: '{fullDir}'.",
                    fullDir,
                    []);
            }

            int limit = Math.Clamp(maxItems <= 0 ? 100 : maxItems, 1, 500);
            string[] matchGlobs = ParseDirectoryPatterns(pattern);
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                MaxRecursionDepth = recursive ? 2 : 0,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System
            };

            try
            {
                var collected = new List<(FileInfo Info, CustomWidgetDropFileContext Ctx)>(Math.Min(limit, 128));
                bool needsRanking = !string.IsNullOrWhiteSpace(sort);
                int scannedLimit = needsRanking
                    ? Math.Max(limit * 32, 8_000)
                    : Math.Max(limit * 8, 2_000);
                int scanned = 0;

                foreach (string entryPath in Directory.EnumerateFiles(fullDir, "*", enumOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++scanned > scannedLimit)
                    {
                        break;
                    }

                    string fileName = Path.GetFileName(entryPath);
                    if (!MatchesAnyGlob(fileName, matchGlobs))
                    {
                        continue;
                    }

                    if (!TryResolveSafeLocalPath(entryPath, forWrite: false, out string safeEntry, out _))
                    {
                        continue;
                    }

                    var info = new FileInfo(safeEntry);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    long bytes = Math.Max(0, info.Length);
                    var ctx = new CustomWidgetDropFileContext
                    {
                        Name = info.Name,
                        Path = safeEntry,
                        Extension = (info.Extension ?? string.Empty).TrimStart('.').ToLowerInvariant(),
                        SizeText = FormatByteSize(bytes),
                        SizeBytes = bytes,
                        ModifiedIso = SafeFormatIsoUtc(info.LastWriteTimeUtc)
                    };
                    collected.Add((info, ctx));
                }

                string sortKey = (sort ?? string.Empty).Trim().ToLowerInvariant();
                switch (sortKey)
                {
                    case "modifieddesc" or "datedesc" or "timedesc" or "mtimedesc" or "newest" or "recent":
                        collected.Sort(static (a, b) => b.Info.LastWriteTimeUtc.CompareTo(a.Info.LastWriteTimeUtc));
                        break;
                    case "modifiedasc" or "dateasc" or "timeasc" or "mtimeasc" or "oldest":
                        collected.Sort(static (a, b) => a.Info.LastWriteTimeUtc.CompareTo(b.Info.LastWriteTimeUtc));
                        break;
                    case "sizedesc" or "largest":
                        collected.Sort(static (a, b) => b.Ctx.SizeBytes.CompareTo(a.Ctx.SizeBytes));
                        break;
                    case "sizeasc" or "smallest":
                        collected.Sort(static (a, b) => a.Ctx.SizeBytes.CompareTo(b.Ctx.SizeBytes));
                        break;
                    case "namedesc":
                        collected.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(b.Ctx.Name, a.Ctx.Name));
                        break;
                    default:
                        collected.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Ctx.Name, b.Ctx.Name));
                        break;
                }

                var result = new List<CustomWidgetDropFileContext>(Math.Min(limit, collected.Count));
                for (int i = 0; i < collected.Count && i < limit; i++)
                {
                    result.Add(collected[i].Ctx);
                }

                return new CustomWidgetDirectoryListOutcome(true, null, fullDir, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CustomWidgetDirectoryListOutcome(false, ex.Message, fullDir, []);
            }
        }, ct);
    }

    public static async Task<CustomWidgetFileManageOutcome> ManageFileAsync(
        string? rawOperation,
        string? rawSourcePath,
        string? rawDestination = null,
        CancellationToken ct = default)
    {
        string op = (rawOperation ?? string.Empty).Trim().ToLowerInvariant();
        if (op is "delete" or "remove" or "unlink" or "rm")
        {
            return new CustomWidgetFileManageOutcome(
                false,
                "Permanent file deletion is forbidden in widgets; use op: 'recycle' to move items safely to the Recycle Bin.",
                op,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        if (op is not ("copy" or "move" or "rename" or "recycle"))
        {
            return new CustomWidgetFileManageOutcome(
                false,
                $"Unsupported file.manage operation '{rawOperation}'. Supported ops: copy, move, rename, recycle.",
                op,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        bool sourceRequiresWrite = op != "copy";
        if (!TryResolveSafeLocalPath(rawSourcePath, forWrite: sourceRequiresWrite, out string srcFull, out string? srcErr))
        {
            return new CustomWidgetFileManageOutcome(false, srcErr, op, string.Empty, string.Empty, string.Empty);
        }

        bool srcIsFile = File.Exists(srcFull);
        bool srcIsDir = !srcIsFile && Directory.Exists(srcFull);
        if (!srcIsFile && !srcIsDir)
        {
            return new CustomWidgetFileManageOutcome(
                false,
                $"Source path not found: '{srcFull}'.",
                op,
                srcFull,
                string.Empty,
                string.Empty);
        }

        if (op == "recycle")
        {
            if (FileService.IsNetworkVolumePath(srcFull) ||
                !DeskBox.Platform.FileOperationNativeMethods.CanHeadlessRecyclePath(srcFull))
            {
                return new CustomWidgetFileManageOutcome(
                    false,
                    "Recycle Bin is unavailable on this volume; permanent deletion is blocked for safety.",
                    op,
                    srcFull,
                    string.Empty,
                    string.Empty);
            }

            try
            {
                var fileService = new FileService();
                bool ok = await fileService.DeleteEntryAsync(srcFull, recycle: true, ownerHandle: IntPtr.Zero).ConfigureAwait(false);
                bool gone = !File.Exists(srcFull) && !Directory.Exists(srcFull);
                if (!ok || !gone)
                {
                    return new CustomWidgetFileManageOutcome(
                        false,
                        "Failed to move item to the Windows Recycle Bin.",
                        op,
                        srcFull,
                        string.Empty,
                        string.Empty);
                }

                return new CustomWidgetFileManageOutcome(
                    true,
                    null,
                    op,
                    srcFull,
                    string.Empty,
                    $"Recycled '{Path.GetFileName(srcFull)}'.");
            }
            catch (Exception ex)
            {
                return new CustomWidgetFileManageOutcome(false, ex.Message, op, srcFull, string.Empty, string.Empty);
            }
        }

        if (string.IsNullOrWhiteSpace(rawDestination))
        {
            return new CustomWidgetFileManageOutcome(
                false,
                $"Destination is required for file.manage '{op}'.",
                op,
                srcFull,
                string.Empty,
                string.Empty);
        }

        string candidateDest = rawDestination.Trim().Trim('"');
        if (op == "rename" &&
            !candidateDest.Contains(Path.DirectorySeparatorChar) &&
            !candidateDest.Contains(Path.AltDirectorySeparatorChar))
        {
            string? parentDir = Path.GetDirectoryName(srcFull);
            if (string.IsNullOrWhiteSpace(parentDir))
            {
                return new CustomWidgetFileManageOutcome(
                    false,
                    "Cannot rename root directory.",
                    op,
                    srcFull,
                    string.Empty,
                    string.Empty);
            }

            candidateDest = Path.Combine(parentDir, candidateDest);
        }

        if (!TryResolveSafeLocalPath(candidateDest, forWrite: true, out string destFull, out string? destErr))
        {
            return new CustomWidgetFileManageOutcome(false, destErr, op, srcFull, string.Empty, string.Empty);
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string finalTarget = destFull;
                if (op is "copy" or "move" && Directory.Exists(destFull))
                {
                    finalTarget = Path.Combine(destFull, Path.GetFileName(Path.TrimEndingDirectorySeparator(srcFull)));
                }

                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(srcFull),
                        Path.TrimEndingDirectorySeparator(finalTarget),
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (op == "rename" &&
                        !string.Equals(Path.GetFileName(srcFull), Path.GetFileName(finalTarget), StringComparison.Ordinal))
                    {
                        // Case-only rename on Windows NTFS
                        string tempHop = srcFull + $".rename-{Guid.NewGuid():N}.tmp";
                        if (srcIsFile)
                        {
                            DeskBox.Platform.CustomWidgetStorageFileOps.MoveFileWithoutOverwrite(srcFull, tempHop);
                            DeskBox.Platform.CustomWidgetStorageFileOps.MoveFileWithoutOverwrite(tempHop, finalTarget);
                        }
                        else
                        {
                            DeskBox.Platform.CustomWidgetStorageFileOps.MoveDirectory(srcFull, tempHop);
                            DeskBox.Platform.CustomWidgetStorageFileOps.MoveDirectory(tempHop, finalTarget);
                        }

                        return new CustomWidgetFileManageOutcome(
                            true,
                            null,
                            op,
                            srcFull,
                            finalTarget,
                            $"Renamed to '{Path.GetFileName(finalTarget)}'.");
                    }

                    return new CustomWidgetFileManageOutcome(
                        true,
                        null,
                        op,
                        srcFull,
                        finalTarget,
                        "Source and target path are identical.");
                }

                if (srcIsDir && IsPathInsideRoot(finalTarget, srcFull))
                {
                    return new CustomWidgetFileManageOutcome(
                        false,
                        "A folder cannot be copied or moved into itself.",
                        op,
                        srcFull,
                        finalTarget,
                        string.Empty);
                }

                string? targetParent = Path.GetDirectoryName(finalTarget);
                if (!string.IsNullOrWhiteSpace(targetParent))
                {
                    Directory.CreateDirectory(targetParent);
                }

                if (op == "rename")
                {
                    if (File.Exists(finalTarget) || Directory.Exists(finalTarget))
                    {
                        return new CustomWidgetFileManageOutcome(
                            false,
                            $"Target name already exists: '{Path.GetFileName(finalTarget)}'.",
                            op,
                            srcFull,
                            finalTarget,
                            string.Empty);
                    }

                    if (srcIsFile)
                    {
                        DeskBox.Platform.CustomWidgetStorageFileOps.MoveFileWithoutOverwrite(srcFull, finalTarget);
                    }
                    else
                    {
                        DeskBox.Platform.CustomWidgetStorageFileOps.MoveDirectory(srcFull, finalTarget);
                    }

                    return new CustomWidgetFileManageOutcome(
                        true,
                        null,
                        op,
                        srcFull,
                        finalTarget,
                        $"Renamed to '{Path.GetFileName(finalTarget)}'.");
                }

                if (File.Exists(finalTarget) || Directory.Exists(finalTarget))
                {
                    finalTarget = FileService.GetAvailablePath(finalTarget);
                }

                if (op == "copy")
                {
                    if (!srcIsFile)
                    {
                        return new CustomWidgetFileManageOutcome(
                            false,
                            "file.manage 'copy' supports single files; iterate files for directory copies.",
                            op,
                            srcFull,
                            finalTarget,
                            string.Empty);
                    }

                    DeskBox.Platform.CustomWidgetStorageFileOps.CopyFileWithoutOverwrite(srcFull, finalTarget);
                    return new CustomWidgetFileManageOutcome(
                        true,
                        null,
                        op,
                        srcFull,
                        finalTarget,
                        $"Copied to '{Path.GetFileName(finalTarget)}'.");
                }

                // op == "move"
                if (srcIsFile)
                {
                    DeskBox.Platform.CustomWidgetStorageFileOps.MoveFileWithoutOverwrite(srcFull, finalTarget);
                }
                else
                {
                    string srcRoot = Path.GetPathRoot(srcFull) ?? string.Empty;
                    string dstRoot = Path.GetPathRoot(finalTarget) ?? string.Empty;
                    if (!string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CustomWidgetFileManageOutcome(
                            false,
                            "Cross-volume folder move is unsupported in file.manage; move files individually.",
                            op,
                            srcFull,
                            finalTarget,
                            string.Empty);
                    }

                    DeskBox.Platform.CustomWidgetStorageFileOps.MoveDirectory(srcFull, finalTarget);
                }

                return new CustomWidgetFileManageOutcome(
                    true,
                    null,
                    op,
                    srcFull,
                    finalTarget,
                    $"Moved to '{Path.GetFileName(finalTarget)}'.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CustomWidgetFileManageOutcome(false, ex.Message, op, srcFull, destFull, string.Empty);
            }
        }, ct);
    }

    private static string[] ParseDirectoryPatterns(string? rawPattern)
    {
        if (string.IsNullOrWhiteSpace(rawPattern) ||
            string.Equals(rawPattern.Trim(), "*", StringComparison.Ordinal) ||
            string.Equals(rawPattern.Trim(), "*.*", StringComparison.Ordinal))
        {
            return [];
        }

        string[] parts = rawPattern.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var globs = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (part is "*" or "*.*")
            {
                return [];
            }

            if (part.StartsWith('.') && !part.Contains('*') && !part.Contains('?'))
            {
                globs.Add("*" + part);
            }
            else if (!part.Contains('*') && !part.Contains('?') && !part.Contains('.'))
            {
                globs.Add($"*{part}*");
            }
            else
            {
                globs.Add(part);
            }
        }

        return [.. globs];
    }

    private static bool MatchesAnyGlob(string fileName, string[] globs)
    {
        if (globs.Length == 0)
        {
            return true;
        }

        foreach (string glob in globs)
        {
            if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeFormatIsoUtc(DateTime utc)
    {
        try
        {
            return utc.Year <= 1601
                ? string.Empty
                : utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
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
