using System.Collections.Concurrent;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves Windows .lnk shortcut files to extract target path, arguments,
/// working directory, and icon location via COM shell interfaces.
/// </summary>
public static class ShortcutHelper
{
    private const int MaxStoredMetadataCacheEntries = 512;
    private const long FailedMetadataRetryDelayMilliseconds = 2_000;
    private static readonly ConcurrentDictionary<string, StoredShortcutCacheEntry>
        s_storedMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_storedMetadataCacheLock = new();
    private static readonly LinkedList<string> s_storedMetadataLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>>
        s_storedMetadataLruNodes = new(StringComparer.OrdinalIgnoreCase);

    internal static int StoredMetadataCacheEntryCount =>
        s_storedMetadataCache.Count;

    internal static int ReleaseHiddenMetadataCache()
    {
        lock (s_storedMetadataCacheLock)
        {
            int released = s_storedMetadataCache.Count;
            s_storedMetadataCache.Clear();
            s_storedMetadataLru.Clear();
            s_storedMetadataLruNodes.Clear();
            return released;
        }
    }

    private sealed record StoredShortcutCacheEntry(
        long Length,
        long LastWriteTimeUtcTicks,
        bool MetadataLoaded,
        ShortcutInfo? Metadata,
        long ReadAtTickCount,
        bool EffectiveTargetEvaluated = false,
        string? EffectiveTargetPath = null);

    public static bool IsShortcutPath(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInternetShortcutPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.GetExtension(path).Equals(
                   ".url",
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsShellLinkPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly byte[] s_shellLinkClsidBytes =
        new Guid("00021401-0000-0000-C000-000000000046").ToByteArray();

    /// <summary>
    /// Reads the <c>[MS-SHLLINK]</c> 24-byte header to check whether the
    /// shortcut carries <c>SLDF_RUNAS_USER</c> (<c>0x00002000</c>) without
    /// instantiating COM shell objects.
    /// </summary>
    internal static bool HasRunAsUserFlag(string? shortcutPath)
    {
        if (!IsShellLinkPath(shortcutPath) || !File.Exists(shortcutPath))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = new FileStream(
                shortcutPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int totalRead = 0;
            while (totalRead < header.Length)
            {
                int read = stream.Read(header[totalRead..]);
                if (read <= 0)
                {
                    return false;
                }

                totalRead += read;
            }

            uint headerSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            if (headerSize != 0x0000004Cu ||
                !header.Slice(4, 16).SequenceEqual(s_shellLinkClsidBytes))
            {
                return false;
            }

            uint linkFlags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
            return (linkFlags & 0x00002000u) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve a .lnk shortcut file and return its target information.
    /// </summary>
    /// <param name="lnkPath">Absolute path to a .lnk file.</param>
    /// <returns>A <see cref="ShortcutInfo"/> with resolved data, or <c>null</c> on failure.</returns>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            (!File.Exists(shortcutPath) &&
             !File.Exists(StorageMigrationNativeMethods.Extended(shortcutPath))))
        {
            return null;
        }

        if (Path.GetExtension(shortcutPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return ReadStoredMetadata(shortcutPath);
        }

        if (!IsShellLinkPath(shortcutPath))
        {
            return null;
        }

        ShortcutNativeCallResult native = ShortcutNativeBackend.ResolveNoUi(shortcutPath);
        if (!native.Success)
        {
            LogNativeFailure("resolve", shortcutPath, native);
        }

        return RecoverLongTargetMetadata(
            shortcutPath,
            native.Metadata,
            native.SourceTruncatedFields);
    }

    /// <summary>
    /// Reads metadata already stored in a shortcut without asking Windows to
    /// resolve or search for its target. This is safe for list hydration: a
    /// missing target cannot stall the first frame for several seconds.
    /// </summary>
    public static ShortcutInfo? ReadStoredMetadata(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            (!File.Exists(shortcutPath) &&
             !File.Exists(StorageMigrationNativeMethods.Extended(shortcutPath))))
        {
            return null;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(shortcutPath);
            string extendedPath = StorageMigrationNativeMethods.Extended(normalizedPath);
            var fileInfo = new FileInfo(
                File.Exists(normalizedPath) ? normalizedPath : extendedPath);
            long length = fileInfo.Length;
            long lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
            if (s_storedMetadataCache.TryGetValue(
                    normalizedPath,
                    out StoredShortcutCacheEntry? cached) &&
                cached.Length == length &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                cached.MetadataLoaded)
            {
                if (cached.Metadata is null &&
                    IsRecentMetadataFailure(cached.ReadAtTickCount))
                {
                    TouchStoredMetadataCacheEntry(normalizedPath);
                    return null;
                }

                // A failed Shell/Rust read must not become a permanent
                // negative cache entry. Retry it after the short backoff so a
                // transient COM/Shell initialization failure cannot force the
                // shortcut down the inferior Shell-item icon path forever.
                if (cached.Metadata is null)
                {
                    RemoveStoredMetadataCacheEntry(normalizedPath);
                    cached = null;
                }
                else
                {
                    TouchStoredMetadataCacheEntry(normalizedPath);
                    return cached.Metadata;
                }
            }

            ShortcutInfo? metadata = ReadStoredMetadataUncached(normalizedPath);
            bool preserveEffectiveEvaluated = cached is not null &&
                cached.Length == length &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                cached.EffectiveTargetEvaluated;
            string? preservedEffectiveTarget = preserveEffectiveEvaluated
                ? cached!.EffectiveTargetPath
                : null;
            StoreStoredMetadataCacheEntry(
                normalizedPath,
                new StoredShortcutCacheEntry(
                    length,
                    lastWriteTimeUtcTicks,
                    MetadataLoaded: true,
                    metadata,
                    Environment.TickCount64,
                    preserveEffectiveEvaluated,
                    preservedEffectiveTarget));
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRecentMetadataFailure(long readAtTickCount)
    {
        long elapsed = Environment.TickCount64 - readAtTickCount;
        return readAtTickCount > 0 &&
               elapsed >= 0 &&
               elapsed < FailedMetadataRetryDelayMilliseconds;
    }

    private static void StoreStoredMetadataCacheEntry(
        string cacheKey,
        StoredShortcutCacheEntry entry)
    {
        lock (s_storedMetadataCacheLock)
        {
            s_storedMetadataCache[cacheKey] = entry;
            TouchStoredMetadataCacheEntryUnderLock(cacheKey);
            while (s_storedMetadataCache.Count > MaxStoredMetadataCacheEntries &&
                   s_storedMetadataLru.Last is { } oldest)
            {
                string oldestKey = oldest.Value;
                s_storedMetadataLru.Remove(oldest);
                s_storedMetadataLruNodes.Remove(oldestKey);
                s_storedMetadataCache.TryRemove(oldestKey, out _);
            }
        }
    }

    private static void TouchStoredMetadataCacheEntry(string cacheKey)
    {
        lock (s_storedMetadataCacheLock)
        {
            if (s_storedMetadataCache.ContainsKey(cacheKey))
            {
                TouchStoredMetadataCacheEntryUnderLock(cacheKey);
            }
        }
    }

    private static void TouchStoredMetadataCacheEntryUnderLock(string cacheKey)
    {
        if (s_storedMetadataLruNodes.TryGetValue(
                cacheKey,
                out LinkedListNode<string>? existing))
        {
            s_storedMetadataLru.Remove(existing);
            s_storedMetadataLru.AddFirst(existing);
            return;
        }

        s_storedMetadataLruNodes[cacheKey] =
            s_storedMetadataLru.AddFirst(cacheKey);
    }

    private static void RemoveStoredMetadataCacheEntry(string cacheKey)
    {
        s_storedMetadataCache.TryRemove(cacheKey, out _);
        lock (s_storedMetadataCacheLock)
        {
            if (s_storedMetadataLruNodes.Remove(
                    cacheKey,
                    out LinkedListNode<string>? node))
            {
                s_storedMetadataLru.Remove(node);
            }
        }
    }

    private static string? TryRecoverLongTargetPath(
        string shortcutPath,
        string? candidateTarget,
        uint truncatedFields = 0)
    {
        string expandedFromShort = string.Empty;
        bool hasShortCompressedLongPath = !string.IsNullOrWhiteSpace(candidateTarget) &&
            StorageMigrationNativeMethods.TryExpandShortPathExceedingMaxPath(
                candidateTarget,
                out expandedFromShort);

        if (!hasShortCompressedLongPath &&
            truncatedFields == 0 &&
            !string.IsNullOrWhiteSpace(candidateTarget))
        {
            string expanded = Environment.ExpandEnvironmentVariables(candidateTarget);
            if (expanded.Length < 258 ||
                File.Exists(StorageMigrationNativeMethods.Extended(expanded)) ||
                Directory.Exists(StorageMigrationNativeMethods.Extended(expanded)))
            {
                return candidateTarget;
            }
        }

        if (ShellLinkVtableInterop.TryReadExpandedShortcutPaths(
                shortcutPath,
                out _,
                out _,
                out string? longTargetPath) &&
            !string.IsNullOrWhiteSpace(longTargetPath))
        {
            if (StorageMigrationNativeMethods.TryExpandShortPathExceedingMaxPath(
                    longTargetPath,
                    out string expandedPidlLong))
            {
                longTargetPath = expandedPidlLong;
            }

            if (hasShortCompressedLongPath ||
                string.IsNullOrWhiteSpace(candidateTarget) ||
                longTargetPath.Length > candidateTarget.Length ||
                File.Exists(StorageMigrationNativeMethods.Extended(longTargetPath)) ||
                Directory.Exists(StorageMigrationNativeMethods.Extended(longTargetPath)))
            {
                return longTargetPath;
            }
        }

        return hasShortCompressedLongPath ? expandedFromShort : candidateTarget;
    }

    private static ShortcutInfo? RecoverLongTargetMetadata(
        string shortcutPath,
        ShortcutInfo? metadata,
        uint truncatedFields = 0)
    {
        string? recoveredTarget = TryRecoverLongTargetPath(
            shortcutPath,
            metadata?.TargetPath,
            truncatedFields);
        if (metadata is null)
        {
            if (string.IsNullOrWhiteSpace(recoveredTarget))
            {
                return null;
            }

            return new ShortcutInfo(
                recoveredTarget,
                string.Empty,
                string.Empty,
                Path.GetDirectoryName(recoveredTarget) ?? string.Empty,
                string.Empty,
                0);
        }

        return string.Equals(recoveredTarget, metadata.TargetPath, StringComparison.Ordinal) ||
               string.IsNullOrWhiteSpace(recoveredTarget)
            ? metadata
            : metadata with { TargetPath = recoveredTarget };
    }

    private static ShortcutInfo? ReadStoredMetadataUncached(string shortcutPath)
    {
        if (Path.GetExtension(shortcutPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveInternetShortcut(shortcutPath);
        }

        if (!IsShellLinkPath(shortcutPath))
        {
            return null;
        }

        ShortcutNativeCallResult native = ShortcutNativeBackend.ReadStoredRaw(shortcutPath);
        if (!native.Success)
        {
            LogNativeFailure("stored read", shortcutPath, native);
        }

        return RecoverLongTargetMetadata(
            shortcutPath,
            native.Metadata,
            native.SourceTruncatedFields);
    }

    /// <summary>
    /// Reads a shortcut's effective target path without <c>SLGP_RAWPATH</c> and
    /// without invoking modal UI or disk search, allowing Windows to apply
    /// <c>KnownFolderDataBlock</c> (for example 32-bit <c>%ProgramFiles%</c>
    /// shortcuts resolving to <c>ProgramFilesX86</c>) or PIDL targets.
    /// </summary>
    internal static string? TryReadEffectiveTargetPath(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !IsShellLinkPath(shortcutPath) ||
            (!File.Exists(shortcutPath) &&
             !File.Exists(StorageMigrationNativeMethods.Extended(shortcutPath))))
        {
            return null;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(shortcutPath);
            string extendedPath = StorageMigrationNativeMethods.Extended(normalizedPath);
            var fileInfo = new FileInfo(
                File.Exists(normalizedPath) ? normalizedPath : extendedPath);
            long length = fileInfo.Length;
            long lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
            if (s_storedMetadataCache.TryGetValue(
                    normalizedPath,
                    out StoredShortcutCacheEntry? cached) &&
                cached.Length == length &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                cached.EffectiveTargetEvaluated)
            {
                TouchStoredMetadataCacheEntry(normalizedPath);
                return cached.EffectiveTargetPath;
            }

            string? resolved = TryReadEffectiveTargetPathUncached(normalizedPath);
            bool preserveMetadata = cached is not null &&
                cached.Length == length &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                cached.MetadataLoaded;
            StoreStoredMetadataCacheEntry(
                normalizedPath,
                new StoredShortcutCacheEntry(
                    length,
                    lastWriteTimeUtcTicks,
                    MetadataLoaded: preserveMetadata,
                    Metadata: preserveMetadata ? cached!.Metadata : null,
                    ReadAtTickCount: preserveMetadata ? cached!.ReadAtTickCount : 0,
                    EffectiveTargetEvaluated: true,
                    EffectiveTargetPath: resolved));
            return resolved;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadEffectiveTargetPathUncached(string shortcutPath)
    {
        try
        {
            ShortcutNativeCallResult native = ShortcutNativeBackend.ReadEffectiveDiagnostic(shortcutPath);
            string? target = native.Success ? native.Metadata?.TargetPath : null;
            return TryRecoverLongTargetPath(shortcutPath, target, native.SourceTruncatedFields);
        }
        catch
        {
            return null;
        }
    }

    private static void LogNativeFailure(
        string operation,
        string shortcutPath,
        ShortcutNativeCallResult result)
    {
        string message =
            $"[ShortcutNative] Explicit Rust {operation} failed for '{shortcutPath}': " +
            $"{result.Failure}; {result.Detail}";
        if (result.IsExpectedPidlOnlyOutcome)
        {
            App.LogVerbose(message);
            return;
        }

        App.Log(message);
    }

    private static ShortcutInfo? ResolveInternetShortcut(string shortcutPath)
    {
        try
        {
            string target = string.Empty;
            string iconLocation = string.Empty;
            int iconIndex = 0;
            bool inInternetShortcutSection = false;

            foreach (string rawLine in File.ReadLines(shortcutPath))
            {
                string line = rawLine.Trim();
                if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
                {
                    inInternetShortcutSection = line.Equals(
                        "[InternetShortcut]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inInternetShortcutSection)
                {
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line[..separatorIndex].Trim();
                string value = line[(separatorIndex + 1)..].Trim().Trim('"');
                if (key.Equals("URL", StringComparison.OrdinalIgnoreCase))
                {
                    target = value;
                }
                else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase))
                {
                    iconLocation = value;
                }
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out iconIndex);
                }
            }

            return string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(iconLocation)
                ? null
                : new ShortcutInfo(
                    TargetPath: target,
                    Description: string.Empty,
                    Arguments: string.Empty,
                    WorkingDirectory: string.Empty,
                    IconLocation: iconLocation,
                    IconIndex: iconIndex);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Let Windows resolve a broken shortcut with its native UI, including the delete offer.
    /// </summary>
    public static BrokenShortcutResolution ResolveBrokenShortcutWithShellUi(string lnkPath, IntPtr ownerHwnd)
    {
        if (!File.Exists(lnkPath))
        {
            return BrokenShortcutResolution.ShortcutDeleted;
        }

        App.Log(
            $"[Shortcut] Broken-link shell UI started " +
            $"owner=0x{ownerHwnd.ToInt64():X} path='{lnkPath}'");
        using IDisposable foregroundMonitor = ShellUiForegroundMonitor.Start(ownerHwnd);
        try
        {
            ShortcutNativeUiResolveCallResult native =
                ShortcutNativeBackend.ResolveWithUi(lnkPath, ownerHwnd);
            if (!native.Success)
            {
                LogNativeUiResolveFailure(lnkPath, native);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Shortcut] Native broken-link resolution failed for '{lnkPath}': {ex}");
        }
        finally
        {
            InvalidateStoredMetadataCache(lnkPath);
        }

        BrokenShortcutResolution resolution = File.Exists(lnkPath)
            ? BrokenShortcutResolution.ResolvedOrKept
            : BrokenShortcutResolution.ShortcutDeleted;
        App.Log(
            $"[Shortcut] Broken-link shell UI completed " +
            $"result={resolution} path='{lnkPath}'");
        return resolution;
    }

    // ────────────────────────────────────────────────────────────────
    //  COM definitions
    // ────────────────────────────────────────────────────────────────

    public static void CreateOrUpdateFolderShortcut(
        string shortcutPath,
        string targetFolderPath,
        string description)
    {
        string normalizedShortcutPath = Path.GetFullPath(shortcutPath);
        string normalizedTargetPath = Path.GetFullPath(targetFolderPath);
        string? shortcutDirectory = Path.GetDirectoryName(normalizedShortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            throw new ArgumentException("Shortcut path must include a directory.", nameof(shortcutPath));
        }

        string creatableDir = shortcutDirectory.Length >= 248
            ? StorageMigrationNativeMethods.Extended(shortcutDirectory)
            : shortcutDirectory;
        Directory.CreateDirectory(creatableDir);

        var metadata = new ShortcutInfo(
            normalizedTargetPath,
            description,
            string.Empty,
            normalizedTargetPath,
            string.Empty,
            0);
        ShortcutNativeWriteCallResult native =
            ShortcutNativeBackend.WriteShortcut(normalizedShortcutPath, metadata);
        if (!native.Success)
        {
            LogNativeWriteFailure("folder write", normalizedShortcutPath, native);
            throw new InvalidOperationException(
                $"Rust shortcut write failed: {native.Failure}; {native.Detail}");
        }

        InvalidateStoredMetadataCache(normalizedShortcutPath);
    }

    /// <summary>
    /// Creates a filesystem .lnk whose target is an application object in the
    /// AppsFolder Shell namespace. Packaged applications do not expose a stable
    /// executable path, so the shortcut stores the Shell PIDL instead.
    /// </summary>
    public static void CreateShellApplicationShortcut(
        string shortcutPath,
        string appUserModelId,
        string description)
    {
        string normalizedAppUserModelId = appUserModelId?.Trim() ?? string.Empty;
        int separatorIndex = normalizedAppUserModelId.IndexOf("!");
        if (string.IsNullOrWhiteSpace(normalizedAppUserModelId) ||
            normalizedAppUserModelId.Length > 1024 ||
            normalizedAppUserModelId.Contains('\0') ||
            normalizedAppUserModelId.Contains('\\') ||
            normalizedAppUserModelId.Contains('/') ||
            normalizedAppUserModelId.Contains(':') ||
            normalizedAppUserModelId.Any(char.IsWhiteSpace) ||
            separatorIndex <= 0 ||
            separatorIndex >= normalizedAppUserModelId.Length - 1)
        {
            throw new ArgumentException(
                "A packaged application AUMID is required.",
                nameof(appUserModelId));
        }

        CreateShellNamespaceShortcut(
            shortcutPath,
            $"shell:AppsFolder\\{normalizedAppUserModelId}",
            description);
    }

    /// <summary>
    /// Creates a filesystem .lnk that targets a Shell namespace object by its
    /// parsing name (for example "::{20D04FE0-...}" for This PC). Virtual
    /// namespace items have no file-system path, so the shortcut stores the
    /// resolved PIDL via IShellLink::SetIDList, which is also how Explorer
    /// materializes pasted namespace objects.
    /// </summary>
    public static void CreateShellNamespaceShortcut(
        string shortcutPath,
        string parsingName,
        string description)
    {
        string normalizedShortcutPath = Path.GetFullPath(shortcutPath);
        string normalizedParsingName = parsingName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedParsingName) ||
            normalizedParsingName.Length > 1024 ||
            normalizedParsingName.Contains('\0'))
        {
            throw new ArgumentException(
                "A Shell parsing name is required.",
                nameof(parsingName));
        }

        string? shortcutDirectory = Path.GetDirectoryName(normalizedShortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            throw new ArgumentException(
                "Shortcut path must include a directory.",
                nameof(shortcutPath));
        }

        Directory.CreateDirectory(shortcutDirectory);

        ShortcutNativeWriteCallResult native =
            ShortcutNativeBackend.WriteShellNamespaceShortcut(
                normalizedShortcutPath,
                normalizedParsingName,
                description);
        if (!native.Success)
        {
            LogNativeWriteFailure("namespace write", normalizedShortcutPath, native);
            throw new InvalidOperationException(
                $"Rust namespace shortcut write failed: {native.Failure}; {native.Detail}");
        }

        InvalidateStoredMetadataCache(normalizedShortcutPath);
    }

    internal static bool TryCreateShortcutFromRawPidl(
        string shortcutPath,
        ReadOnlySpan<byte> rawPidlBytes,
        string? description = null)
    {
        if (!ShellLinkVtableInterop.TryCreateShortcutFromPidlBytes(
                shortcutPath,
                rawPidlBytes,
                description))
        {
            return false;
        }

        InvalidateStoredMetadataCache(shortcutPath);
        return true;
    }

    /// <summary>
    /// Rebases any moved <c>.lnk</c> files in <paramref name="movedPairs"/>
    /// whose target, working directory, or icon location moved in the same
    /// batch (or relied on a relative path from the shortcut's old folder).
    /// Returns the destination paths of shortcuts rewritten on disk.
    /// </summary>
    internal static IReadOnlySet<string> RebaseMovedShortcuts(
        IReadOnlyList<(string SourcePath, string DestinationPath)> movedPairs)
    {
        var rewrittenShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (movedPairs.Count == 0)
        {
            return rewrittenShortcuts;
        }

        foreach ((string sourcePath, string destinationPath) in movedPairs)
        {
            if (!IsShellLinkPath(destinationPath) || !File.Exists(destinationPath))
            {
                continue;
            }

            if (ShellLinkVtableInterop.TryRebaseShortcutPathsInPlace(
                    destinationPath,
                    sourcePath,
                    movedPairs))
            {
                InvalidateStoredMetadataCache(sourcePath);
                InvalidateStoredMetadataCache(destinationPath);
                IconHelper.ClearIconCache(sourcePath);
                IconHelper.ClearIconCache(destinationPath);
                rewrittenShortcuts.Add(destinationPath);
            }
        }

        return rewrittenShortcuts;
    }

    internal static void InvalidateStoredMetadataCache(string shortcutPath)
    {
        try
        {
            RemoveStoredMetadataCacheEntry(Path.GetFullPath(shortcutPath));
        }
        catch
        {
            RemoveStoredMetadataCacheEntry(shortcutPath);
        }
    }

    private static void LogNativeWriteFailure(
        string operation,
        string shortcutPath,
        ShortcutNativeWriteCallResult result)
    {
        App.Log(
            $"[ShortcutNative] Explicit Rust {operation} failed for '{shortcutPath}': " +
            $"{result.Failure}; {result.Detail}");
    }

    private static void LogNativeUiResolveFailure(
        string shortcutPath,
        ShortcutNativeUiResolveCallResult result)
    {
        App.Log(
            $"[ShortcutNative] Explicit Rust Windows UI resolve failed for '{shortcutPath}': " +
            $"{result.Failure}; {result.Detail}");
    }

}

public enum BrokenShortcutResolution
{
    ResolvedOrKept,
    ShortcutDeleted
}

/// <summary>
/// Immutable record holding the resolved information from a .lnk shortcut.
/// </summary>
/// <param name="TargetPath">Absolute path to the shortcut's target.</param>
/// <param name="Description">Description stored in the shortcut.</param>
/// <param name="Arguments">Command-line arguments stored in the shortcut.</param>
/// <param name="WorkingDirectory">Working directory for the target process.</param>
/// <param name="IconLocation">Path to the file containing the shortcut's icon.</param>
/// <param name="IconIndex">Zero-based icon index within <paramref name="IconLocation"/>.</param>
public record ShortcutInfo(
    string TargetPath,
    string Description,
    string Arguments,
    string WorkingDirectory,
    string IconLocation,
    int IconIndex);
