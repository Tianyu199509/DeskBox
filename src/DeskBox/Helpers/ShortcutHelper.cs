using System.Collections.Concurrent;
using DeskBox.Platform;
#if !DESKBOX_NATIVE_AOT
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
#endif

namespace DeskBox.Helpers;

/// <summary>
/// Resolves Windows .lnk shortcut files to extract target path, arguments,
/// working directory, and icon location via COM shell interfaces.
/// </summary>
public static class ShortcutHelper
{
#if !DESKBOX_NATIVE_AOT
    private const int MAX_PATH = 260;
#endif
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
        ShortcutInfo? Metadata,
        long ReadAtTickCount);

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

    /// <summary>
    /// Resolve a .lnk shortcut file and return its target information.
    /// </summary>
    /// <param name="lnkPath">Absolute path to a .lnk file.</param>
    /// <returns>A <see cref="ShortcutInfo"/> with resolved data, or <c>null</c> on failure.</returns>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
            return null;

        if (Path.GetExtension(shortcutPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return ReadStoredMetadata(shortcutPath);
        }

        if (!IsShellLinkPath(shortcutPath))
        {
            return null;
        }

#if DESKBOX_NATIVE_AOT
        ShortcutNativeCallResult native = ShortcutNativeBackend.ResolveNoUi(shortcutPath);
        if (!native.Success)
        {
            LogNativeFailure("resolve", shortcutPath, native);
        }

        return native.Metadata;
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            ShortcutNativeCallResult native = ShortcutNativeBackend.ResolveNoUi(shortcutPath);
            if (!native.Success)
            {
                LogNativeFailure("resolve", shortcutPath, native);
            }

            return native.Metadata;
        }

        return ResolveWithCSharp(shortcutPath);
#endif
    }

#if !DESKBOX_NATIVE_AOT
    internal static ShortcutInfo? ResolveWithCSharp(string shortcutPath, ushort timeoutMs = 0)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;

            file.Load(shortcutPath, 0); // STGM_READ
            try
            {
                uint resolveFlags = (uint)(SLR_FLAGS.SLR_NO_UI | SLR_FLAGS.SLR_NOSEARCH) |
                    ((uint)timeoutMs << 16);
                link.Resolve(IntPtr.Zero, (SLR_FLAGS)resolveFlags);
            }
            catch
            {
                // Keep reading the stored shortcut metadata even if the target is unavailable.
            }

            return ReadShellLinkMetadata(link);
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>
    /// Reads metadata already stored in a shortcut without asking Windows to
    /// resolve or search for its target. This is safe for list hydration: a
    /// missing target cannot stall the first frame for several seconds.
    /// </summary>
    public static ShortcutInfo? ReadStoredMetadata(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return null;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(shortcutPath);
            var fileInfo = new FileInfo(normalizedPath);
            long length = fileInfo.Length;
            long lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
            if (s_storedMetadataCache.TryGetValue(
                    normalizedPath,
                    out StoredShortcutCacheEntry? cached) &&
                cached.Length == length &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
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
                }
                else
                {
                    TouchStoredMetadataCacheEntry(normalizedPath);
                    return cached.Metadata;
                }
            }

            ShortcutInfo? metadata = ReadStoredMetadataUncached(normalizedPath);
            StoreStoredMetadataCacheEntry(
                normalizedPath,
                new StoredShortcutCacheEntry(
                    length,
                    lastWriteTimeUtcTicks,
                    metadata,
                    Environment.TickCount64));
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

#if DESKBOX_NATIVE_AOT
        ShortcutNativeCallResult native = ShortcutNativeBackend.ReadStoredRaw(shortcutPath);
        if (!native.Success)
        {
            LogNativeFailure("stored read", shortcutPath, native);
        }

        return native.Metadata;
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            ShortcutNativeCallResult native = ShortcutNativeBackend.ReadStoredRaw(shortcutPath);
            if (!native.Success)
            {
                LogNativeFailure("stored read", shortcutPath, native);
            }

            return native.Metadata;
        }

        return ReadStoredMetadataWithCSharpUncached(shortcutPath);
#endif
    }

#if !DESKBOX_NATIVE_AOT
    internal static ShortcutInfo ReadStoredMetadataWithCSharpUncached(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        var file = (IPersistFile)link;
        file.Load(shortcutPath, 0); // STGM_READ
        return ReadShellLinkMetadata(link);
    }
#endif

    private static void LogNativeFailure(
        string operation,
        string shortcutPath,
        ShortcutNativeCallResult result)
    {
        App.Log(
            $"[ShortcutNative] Explicit Rust {operation} failed for '{shortcutPath}': " +
            $"{result.Failure}; {result.Detail}");
    }

#if !DESKBOX_NATIVE_AOT
    private static ShortcutInfo ReadShellLinkMetadata(IShellLinkW link)
    {
        var targetBuilder = new StringBuilder(MAX_PATH);
        var findData = new WIN32_FIND_DATAW();
        link.GetPath(targetBuilder, MAX_PATH, ref findData, SLGP_FLAGS.SLGP_RAWPATH);

        var descriptionBuilder = new StringBuilder(MAX_PATH);
        link.GetDescription(descriptionBuilder, MAX_PATH);

        var argsBuilder = new StringBuilder(MAX_PATH);
        link.GetArguments(argsBuilder, MAX_PATH);

        var workDirBuilder = new StringBuilder(MAX_PATH);
        link.GetWorkingDirectory(workDirBuilder, MAX_PATH);

        var iconBuilder = new StringBuilder(MAX_PATH);
        link.GetIconLocation(iconBuilder, MAX_PATH, out var iconIndex);

        return new ShortcutInfo(
            TargetPath: targetBuilder.ToString(),
            Description: descriptionBuilder.ToString(),
            Arguments: argsBuilder.ToString(),
            WorkingDirectory: workDirBuilder.ToString(),
            IconLocation: iconBuilder.ToString(),
            IconIndex: iconIndex);
    }
#endif

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
                    _ = int.TryParse(value, out iconIndex);
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
#if DESKBOX_NATIVE_AOT
            ShortcutNativeUiResolveCallResult native =
                ShortcutNativeBackend.ResolveWithUi(lnkPath, ownerHwnd);
            if (!native.Success)
            {
                LogNativeUiResolveFailure(lnkPath, native);
            }
#else
            if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
            {
                ShortcutNativeUiResolveCallResult native =
                    ShortcutNativeBackend.ResolveWithUi(lnkPath, ownerHwnd);
                if (!native.Success)
                {
                    LogNativeUiResolveFailure(lnkPath, native);
                }
            }
            else
            {
                var link = (IShellLinkW)new ShellLink();
                var file = (IPersistFile)link;
                file.Load(lnkPath, 0); // STGM_READ

                link.Resolve(
                    ownerHwnd,
                    SLR_FLAGS.SLR_UPDATE |
                    SLR_FLAGS.SLR_NOSEARCH |
                    SLR_FLAGS.SLR_OFFER_DELETE_WITHOUT_FILE);
            }
#endif
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

        Directory.CreateDirectory(shortcutDirectory);

#if DESKBOX_NATIVE_AOT
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
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
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
        }
        else
        {
            CreateOrUpdateFolderShortcutWithCSharp(
                normalizedShortcutPath,
                normalizedTargetPath,
                description);
        }
#endif

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
        string normalizedShortcutPath = Path.GetFullPath(shortcutPath);
        string normalizedAppUserModelId = appUserModelId?.Trim() ?? string.Empty;
        int separatorIndex = normalizedAppUserModelId.IndexOf('!');
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

        string? shortcutDirectory = Path.GetDirectoryName(normalizedShortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            throw new ArgumentException(
                "Shortcut path must include a directory.",
                nameof(shortcutPath));
        }

        Directory.CreateDirectory(shortcutDirectory);
        string parsingName = $"shell:AppsFolder\\{normalizedAppUserModelId}";

#if DESKBOX_NATIVE_AOT
        ShortcutNativeWriteCallResult native =
            ShortcutNativeBackend.WriteShellNamespaceShortcut(
                normalizedShortcutPath,
                parsingName,
                description);
        if (!native.Success)
        {
            LogNativeWriteFailure("AppsFolder write", normalizedShortcutPath, native);
            throw new InvalidOperationException(
                $"Rust AppsFolder shortcut write failed: {native.Failure}; {native.Detail}");
        }
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            ShortcutNativeWriteCallResult native =
                ShortcutNativeBackend.WriteShellNamespaceShortcut(
                    normalizedShortcutPath,
                    parsingName,
                    description);
            if (!native.Success)
            {
                LogNativeWriteFailure("AppsFolder write", normalizedShortcutPath, native);
                throw new InvalidOperationException(
                    $"Rust AppsFolder shortcut write failed: {native.Failure}; {native.Detail}");
            }
        }
        else
        {
            CreateShellNamespaceShortcutWithCSharp(
                normalizedShortcutPath,
                parsingName,
                description);
        }
#endif

        InvalidateStoredMetadataCache(normalizedShortcutPath);
    }

#if !DESKBOX_NATIVE_AOT
    internal static void CreateOrUpdateFolderShortcutWithCSharp(
        string normalizedShortcutPath,
        string normalizedTargetPath,
        string description)
    {
        var link = (IShellLinkW)new ShellLink();
        var file = (IPersistFile)link;
        link.SetPath(normalizedTargetPath);
        link.SetWorkingDirectory(normalizedTargetPath);
        link.SetDescription(description);
        file.Save(normalizedShortcutPath, true);
    }

    internal static void CreateShellNamespaceShortcutWithCSharp(
        string normalizedShortcutPath,
        string parsingName,
        string description)
    {
        IntPtr pidl = IntPtr.Zero;
        int hresult = Shell32NativeMethods.SHParseDisplayName(
            parsingName,
            IntPtr.Zero,
            out pidl,
            0,
            out _);
        if (hresult < 0 || pidl == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hresult < 0 ? hresult : unchecked((int)0x80004005));
        }

        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            link.SetIDList(pidl);
            link.SetDescription(description);
            file.Save(normalizedShortcutPath, true);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }
#endif

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

#if !DESKBOX_NATIVE_AOT
    /// <summary>Shell Link CoClass (CLSID_ShellLink).</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    /// <summary>IShellLinkW COM interface.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            ref WIN32_FIND_DATAW pfd,
            SLGP_FLAGS fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cch);

        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cch);

        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cch);

        void SetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath,
            int iIcon);

        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPathRel,
            uint dwReserved);

        void Resolve(IntPtr hwnd, SLR_FLAGS fFlags);

        void SetPath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [Flags]
    private enum SLR_FLAGS : uint
    {
        SLR_NO_UI = 0x0001,
        SLR_UPDATE = 0x0004,
        SLR_NOSEARCH = 0x0010,
        SLR_OFFER_DELETE_WITHOUT_FILE = 0x0200,
    }

    [Flags]
    private enum SLGP_FLAGS : uint
    {
        SLGP_SHORTPATH = 0x0001,
        SLGP_UNCPRIORITY = 0x0002,
        SLGP_RAWPATH = 0x0004,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
#endif
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
