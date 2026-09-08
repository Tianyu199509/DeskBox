using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using DeskBox.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Helpers;

/// <summary>
/// Extracts native Windows file and folder icons using the Win32 Shell API.
/// </summary>
public static class IconHelper
{
    private const int BaseMaxIconCacheEntries = 200;
    private const long BaseMaxIconCacheBytes = 32L * 1024 * 1024;
    private const int BaseMaxDecodedBitmapCacheEntries = 160;
    private const long BaseMaxDecodedBitmapCacheBytes = 48L * 1024 * 1024;
    private const int BaseMaxThumbnailCacheEntries = 128;
    private const long BaseMaxThumbnailCacheBytes = 32L * 1024 * 1024;
    private const int SmallCacheBudgetPercent = 50;
    private const int BalancedCacheBudgetPercent = 100;
    private const int LargeCacheBudgetPercent = 150;
    private static int s_cacheBudgetPercent = BalancedCacheBudgetPercent;
    private static string s_cacheBudget =
        PerformanceSettingsPolicy.CacheBudgetBalanced;

    private static int MaxIconCacheEntries =>
        ScaleCacheEntryLimit(BaseMaxIconCacheEntries);
    private static long MaxIconCacheBytes =>
        ScaleCacheByteLimit(BaseMaxIconCacheBytes);
    private static int MaxDecodedBitmapCacheEntries =>
        ScaleCacheEntryLimit(BaseMaxDecodedBitmapCacheEntries);
    private static long MaxDecodedBitmapCacheBytes =>
        ScaleCacheByteLimit(BaseMaxDecodedBitmapCacheBytes);
    private static int MaxThumbnailCacheEntries =>
        ScaleCacheEntryLimit(BaseMaxThumbnailCacheEntries);
    private static long MaxThumbnailCacheBytes =>
        ScaleCacheByteLimit(BaseMaxThumbnailCacheBytes);
    private const string SharedCacheScope = "shared";
    private const int MaxIconSourceTimeoutEntries = 256;
    private const long IconSourceTimeoutRetryMs = 30_000;
    private static readonly TimeSpan IconSourceResolutionTimeout =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan IconBytesLoadTimeout =
        TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan IdleCacheMinimumAge =
        TimeSpan.FromMinutes(5);
    private const int PreferredShellItemIconSize = 256;
    private const string InternetShortcutIconStrategyVersion = "url-shell-v2";

    // Icon bytes cache: path → PNG bytes (for shell icons, not image thumbnails)
    private static readonly ConcurrentDictionary<string, byte[]?> s_iconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> s_iconBytesLastAccess = new(StringComparer.OrdinalIgnoreCase);

    // Bitmap cache for shell icons (not image thumbnails)
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_bitmapImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_bitmapCacheLock = new();
    private static readonly LinkedList<string> s_bitmapLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>> s_bitmapLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_bitmapEstimatedBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_bitmapLastAccess = new(StringComparer.OrdinalIgnoreCase);
    private static long s_totalBitmapEstimatedBytes;

    // ── Media thumbnail LRU cache (separate from icon cache) ──────
    // Uses a linked list + dictionary for simple LRU eviction.
    private static readonly object s_thumbLock = new();
    private static readonly LinkedList<string> s_thumbLru = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> s_thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_thumbEstimatedBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_thumbLastAccess = new(StringComparer.OrdinalIgnoreCase);
    private static long s_totalThumbnailEstimatedBytes;

    internal readonly record struct IdleIconCacheReleaseResult(
        int ReleasedThumbnails,
        int ReleasedDecodedBitmaps,
        int ReleasedIconByteEntries,
        long ReleasedEstimatedBytes)
    {
        public bool ReleasedAnything =>
            ReleasedThumbnails > 0 ||
            ReleasedDecodedBitmaps > 0 ||
            ReleasedIconByteEntries > 0;
    }

    private static readonly SemaphoreSlim s_thumbLoadSemaphore = new(2, 2);
    private static readonly SemaphoreSlim s_shellIconLoadSemaphore = new(8, 8);
    private static readonly BoundedBackgroundWorkScheduler s_iconSourceScheduler =
        BoundedBackgroundWorkScheduler.SharedShell;
    private static readonly ConcurrentDictionary<string, long> s_iconSourceTimeouts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> s_iconBytesTimeouts =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record IconSource(
        string Path,
        int IconIndex = 0,
        bool UsesExplicitIconIndex = false,
        bool UsesShellItemIcon = false);
    private sealed record ResolvedIconSource(IconSource Source, string CacheKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SYSICONINDEX = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHDefExtractIcon(
        string pszIconFile,
        int iIcon,
        uint uFlags,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIconSize); // MAKELONG(cxSmall, cxLarge) — low word = small, high word = large

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        ref IntPtr ppv);

    // Image list size flags for SHGetImageList
    private const int SHIL_EXTRALARGE = 0x2; // 48x48
    private const int SHIL_JUMBO = 0x4;      // 256x256 (Vista+)

    private static readonly Guid s_iidIImageList = new("46EB5926-582E-4017-9FDF-E899822AA8B3");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E899822AA8B3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int GetImageCount();

        [PreserveSig]
        int GetImageRect(int i, ref RECT pRect);

        [PreserveSig]
        int GetIcon(int i, uint flags, ref IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private const uint ILD_TRANSPARENT = 0x00000001;

    // Shell change notification for invalidating the icon cache.
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const long ShellInvalidateThrottleMs = 500;
    private static long s_lastShellInvalidateMs;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Asynchronously retrieve the native Windows shell icon for the given path.
    /// For image and video files, returns a thumbnail preview instead of the generic icon.
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        int decodePixelWidth = 0,
        string cacheScope = SharedCacheScope)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.GetIcon", $"path={path}");
        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalizedCacheScope = string.IsNullOrWhiteSpace(cacheScope)
            ? SharedCacheScope
            : cacheScope.Trim();

        if (!showImageFilesAsIcons && IsMediaFile(path))
        {
            var thumbnail = await LoadMediaThumbnailAsync(
                dispatcher,
                path,
                decodePixelWidth,
                normalizedCacheScope);
            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        if (!showImageFilesAsIcons &&
            !IsMediaFile(path) &&
            await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(path))
        {
            var thumbnail = await LoadShellThumbnailAsync(
                dispatcher,
                path,
                decodePixelWidth,
                normalizedCacheScope);
            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        ResolvedIconSource resolvedIconSource =
            await ResolveIconSourceWithCacheKeyAsync(
                path,
                hideShortcutArrowOverlay);
        IconSource iconSource = resolvedIconSource.Source;
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return null;
        }

        string cacheKey = resolvedIconSource.CacheKey;
        TouchCachedIconBytes(cacheKey);
        bool isShortcutPath = ShortcutHelper.IsShortcutPath(path);
        bool isInternetShortcutPath =
            ShortcutHelper.IsInternetShortcutPath(path);
        int normalizedDecodePixelWidth = Math.Clamp(decodePixelWidth, 0, 256);
        string bitmapCacheKey = $"{normalizedCacheScope}|{cacheKey}:decode={normalizedDecodePixelWidth}";
        Task<BitmapImage?> bitmapTask;
        if (s_bitmapImageCache.TryGetValue(bitmapCacheKey, out bitmapTask!))
        {
            PerformanceLogger.RecordDecodedBitmapCacheLookup(hit: true);
        }
        else
        {
            PerformanceLogger.RecordDecodedBitmapCacheLookup(hit: false);
            bitmapTask = s_bitmapImageCache.GetOrAdd(
                bitmapCacheKey,
                _ => LoadBitmapImageWithDiagnosticsAsync(
                    dispatcher,
                    iconSource,
                    cacheKey,
                    bitmapCacheKey,
                    NormalizeSourcePath(path),
                    hideShortcutArrowOverlay,
                    isShortcutPath,
                    isInternetShortcutPath,
                    normalizedDecodePixelWidth));
        }
        TrackDecodedBitmap(
            bitmapCacheKey,
            EstimateDecodedBitmapBytes(normalizedDecodePixelWidth));
        return await bitmapTask;
    }

    public static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tiff" or ".tif" or ".heic" or ".heif";
    }

    internal static bool IsSolutionFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".m4v" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".webm"
            or ".mpeg" or ".mpg" or ".mpe" or ".3gp" or ".3g2" or ".mts" or ".m2ts"
            or ".ts" or ".vob" or ".flv" or ".ogv";
    }

    public static bool IsMediaFile(string path)
    {
        return IsImageFile(path) || IsVideoFile(path);
    }

    // ── Media thumbnail loading with LRU cache ───────────────────

    private static async Task<BitmapImage?> LoadMediaThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth,
        string cacheScope)
    {
        int normalizedDecodePixelWidth = decodePixelWidth <= 0
            ? 96
            : Math.Clamp(decodePixelWidth, 24, 256);
        string cacheKey = $"thumb:{cacheScope}|{path}:{GetFileIconVersion(path)}:decode={normalizedDecodePixelWidth}";

        return await LoadCachedThumbnailAsync(
            cacheKey,
            $"thumb:{cacheScope}|{path}:",
            normalizedDecodePixelWidth,
            () => CreateMediaThumbnailAsync(
                dispatcher,
                path,
                normalizedDecodePixelWidth));
    }

    private static async Task<BitmapImage?> LoadShellThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth,
        string cacheScope)
    {
        int normalizedDecodePixelWidth = decodePixelWidth <= 0
            ? 96
            : Math.Clamp(decodePixelWidth, 24, 256);
        string cacheKey = $"shell-thumb:{cacheScope}|{path}:{GetFileIconVersion(path)}:decode={normalizedDecodePixelWidth}";

        return await LoadCachedThumbnailAsync(
            cacheKey,
            $"shell-thumb:{cacheScope}|{path}:",
            normalizedDecodePixelWidth,
            () => CreateShellThumbnailAsync(
                dispatcher,
                path,
                normalizedDecodePixelWidth));
    }

    private static async Task<BitmapImage?> LoadCachedThumbnailAsync(
        string cacheKey,
        string pathPrefix,
        int decodePixelWidth,
        Func<Task<BitmapImage?>> createThumbnail)
    {

        Task<BitmapImage?>? cachedTask = null;
        lock (s_thumbLock)
        {
            // Update diagnostics
            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;

            if (s_thumbCache.TryGetValue(cacheKey, out var cached))
            {
                // Move to front of LRU
                RemoveThumbnailLruKey(cacheKey);
                s_thumbLru.AddFirst(cacheKey);
                s_thumbLastAccess[cacheKey] = Stopwatch.GetTimestamp();
                cachedTask = cached;
            }
        }

        if (cachedTask is not null)
        {
            PerformanceLogger.RecordThumbnailCacheLookup(hit: true);
            return await cachedTask;
        }

        // Remove stale entry for the same path but different version
        RemoveStaleThumbnailEntries(pathPrefix, cacheKey);

        Task<BitmapImage?> task;
        bool createdTask = false;
        lock (s_thumbLock)
        {
            if (!s_thumbCache.TryGetValue(cacheKey, out task!))
            {
                task = CreateThumbnailWithDiagnosticsAsync(createThumbnail);
                s_thumbCache[cacheKey] = task;
                createdTask = true;
                long estimatedBytes = EstimateDecodedBitmapBytes(decodePixelWidth);
                s_thumbEstimatedBytes[cacheKey] = estimatedBytes;
                s_totalThumbnailEstimatedBytes += estimatedBytes;
                s_thumbLru.AddFirst(cacheKey);
                s_thumbLastAccess[cacheKey] = Stopwatch.GetTimestamp();
                EvictThumbnailCacheIfNeeded();
            }
            else
            {
                RemoveThumbnailLruKey(cacheKey);
                s_thumbLru.AddFirst(cacheKey);
                s_thumbLastAccess[cacheKey] = Stopwatch.GetTimestamp();
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }

        PerformanceLogger.RecordThumbnailCacheLookup(hit: !createdTask);

        var result = await task;
        if (result is null)
        {
            RemoveThumbnailTaskIfCurrent(cacheKey, task);
        }

        return result;
    }

    /// <summary>
    /// Removes old cache entries for the same file path but different
    /// modification-time version, preventing stale thumbnails from
    /// accumulating when files are edited.
    /// </summary>
    private static void RemoveStaleThumbnailEntries(
        string pathPrefix,
        string currentKey)
    {
        lock (s_thumbLock)
        {
            if (s_thumbCache.Count == 0)
            {
                return;
            }

            var staleKeys = new List<string>();
            foreach (var key in s_thumbCache.Keys)
            {
                if (!key.Equals(currentKey, StringComparison.OrdinalIgnoreCase) &&
                    key.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    staleKeys.Add(key);
                }
            }

            foreach (var staleKey in staleKeys)
            {
                RemoveThumbnailCacheEntry(staleKey);
            }
        }
    }

    /// <summary>
    /// Evicts oldest thumbnail cache entries when the cache exceeds the
    /// maximum size.  Must be called under s_thumbLock.
    /// </summary>
    private static void EvictThumbnailCacheIfNeeded()
    {
        while ((s_thumbCache.Count > MaxThumbnailCacheEntries ||
                s_totalThumbnailEstimatedBytes > MaxThumbnailCacheBytes) &&
               s_thumbLru.Count > 0)
        {
            LinkedListNode<string>? oldest =
                FindOldestEvictableThumbnailNode(coldOnly: false, nowTimestamp: 0);
            if (oldest is null)
            {
                break;
            }

            RemoveThumbnailCacheEntry(oldest.Value);
        }

        PerformanceLogger.ThumbnailEstimatedBytes =
            Math.Max(0, s_totalThumbnailEstimatedBytes);
    }

    private static async Task<BitmapImage?> CreateMediaThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth)
    {
        await s_thumbLoadSemaphore.WaitAsync();
        try
        {
            // Try Windows native thumbnail first — leverages the system
            // thumbnail cache and avoids reading the full image into memory.
            bool isVideo = IsVideoFile(path);
            var image = await TryLoadNativeThumbnailAsync(
                dispatcher,
                path,
                decodePixelWidth,
                isVideo
                    ? Windows.Storage.FileProperties.ThumbnailMode.VideosView
                    : Windows.Storage.FileProperties.ThumbnailMode.PicturesView);
            if (image is not null)
            {
                return image;
            }

            // Video decoding is intentionally left to the Windows thumbnail
            // provider. Reading an entire video as an image would waste memory
            // and cannot produce a valid BitmapImage; the caller falls back to
            // the normal shell icon when Windows has no preview.
            if (isVideo)
            {
                return null;
            }

            // Fallback: decode to the requested display size.
            byte[] bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth);

            return image;
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load media thumbnail for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            s_thumbLoadSemaphore.Release();
        }
    }

    private static async Task<BitmapImage?> CreateShellThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth)
    {
        await s_thumbLoadSemaphore.WaitAsync();
        try
        {
            byte[]? bytes = await ShellThumbnailProxy.TryLoadAsync(
                path,
                decodePixelWidth);
            return bytes is { Length: > 0 }
                ? await CreateBitmapImageAsync(
                    dispatcher,
                    bytes,
                    decodePixelWidth)
                : null;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[IconHelper] Failed to load isolated Shell thumbnail for " +
                $"{path}: {ex.Message}");
            return null;
        }
        finally
        {
            s_thumbLoadSemaphore.Release();
        }
    }

    private static void RemoveThumbnailTaskIfCurrent(string cacheKey, Task<BitmapImage?> task)
    {
        lock (s_thumbLock)
        {
            if (s_thumbCache.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, task))
            {
                RemoveThumbnailCacheEntry(cacheKey);
                PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            }
        }
    }

    private static void RemoveThumbnailCacheEntry(string cacheKey)
    {
        s_thumbCache.Remove(cacheKey);
        RemoveThumbnailLruKey(cacheKey);
        s_thumbLastAccess.Remove(cacheKey);
        if (s_thumbEstimatedBytes.Remove(cacheKey, out long estimatedBytes))
        {
            s_totalThumbnailEstimatedBytes -= estimatedBytes;
        }

        PerformanceLogger.ThumbnailEstimatedBytes =
            Math.Max(0, s_totalThumbnailEstimatedBytes);
    }

    private static void RemoveThumbnailLruKey(string cacheKey)
    {
        for (var node = s_thumbLru.First; node is not null; node = node.Next)
        {
            if (node.Value.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                s_thumbLru.Remove(node);
                return;
            }
        }
    }

    /// <summary>
    /// Tries to load a thumbnail using Windows' built-in thumbnail system
    /// via StorageFile.GetThumbnailAsync().  This avoids reading the full
    /// image into memory and benefits from the OS thumbnail cache.
    /// Returns null if the native path fails (e.g. network paths, special files).
    /// </summary>
    private static async Task<BitmapImage?> TryLoadNativeThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth,
        Windows.Storage.FileProperties.ThumbnailMode thumbnailMode)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await storageFile.GetThumbnailAsync(
                thumbnailMode,
                (uint)decodePixelWidth,
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            if (dispatcher.HasThreadAccess)
            {
                return await CreateBitmapFromStreamOnUiThread(thumbnail, decodePixelWidth);
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    tcs.SetResult(await CreateBitmapFromStreamOnUiThread(thumbnail, decodePixelWidth));
                }
                catch (Exception ex)
                {
                    App.Log($"[IconHelper] Native thumbnail UI thread decode failed: {ex.Message}");
                    tcs.SetResult(null);
                }
            }))
            {
                tcs.SetResult(null);
            }

            return await tcs.Task;
        }
        catch
        {
            // StorageFile.GetFileFromPathAsync can fail for various reasons
            // (network paths, special files, permission issues).  Fall back
            // to the byte-array path.
            return null;
        }
    }

    private static async Task<BitmapImage?> CreateBitmapFromStreamOnUiThread(
        Windows.Storage.Streams.IRandomAccessStream stream,
        int decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = decodePixelWidth;
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    public static void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool resetTransientFailures = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Remove both media and isolated Shell thumbnail entries for this path.
        string pathMarker = $"|{path}:";
        lock (s_thumbLock)
        {
            var keysToRemove = s_thumbCache.Keys
                .Where(key => key.Contains(
                    pathMarker,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in keysToRemove)
            {
                RemoveThumbnailCacheEntry(key);
            }
            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }
        if (resetTransientFailures)
        {
            ShellThumbnailProxy.Invalidate(path);
        }

        if (IsMediaFile(path))
        {
            if (!showImageFilesAsIcons)
            {
                return;
            }
        }

        // Never resolve a shortcut or probe its target while invalidating from
        // the UI thread. Every icon key carries its original source path, so all
        // resolved variants can be removed without filesystem or Shell calls.
        if (resetTransientFailures &&
            ShortcutHelper.IsShortcutPath(path))
        {
            // A shortcut can be replaced in-place while retaining the same
            // path. Drop the stored metadata snapshot so the next icon load
            // cannot keep using the old target/icon location.
            ShortcutHelper.InvalidateStoredMetadataCache(path);
        }

        string normalizedSourcePath = NormalizeSourcePath(path);
        string sourceCachePrefix = BuildSourceCachePrefix(normalizedSourcePath);
        string bitmapCacheMarker = $"|{sourceCachePrefix}";
        string extensionCacheKey = $"ext:{Path.GetExtension(normalizedSourcePath)}";
        string extensionBitmapMarker = $"|{extensionCacheKey}:decode=";
        bool invalidatedDirectoryIcon = false;
        foreach (string bitmapKey in s_bitmapImageCache.Keys.Where(
                     key =>
                         key.Contains(bitmapCacheMarker, StringComparison.OrdinalIgnoreCase) ||
                         key.Contains(extensionBitmapMarker, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveDecodedBitmap(bitmapKey);
        }

        foreach (string cacheKey in s_iconBytesCache.Keys.Where(
                     key => key.StartsWith(sourceCachePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            invalidatedDirectoryIcon |= cacheKey.Contains(
                "|dir:",
                StringComparison.OrdinalIgnoreCase);
            RemoveCachedIconBytes(cacheKey, out _);
        }

        RemoveCachedIconBytes(extensionCacheKey, out _);

        if (resetTransientFailures)
        {
            foreach (string timeoutKey in s_iconBytesTimeouts.Keys.Where(
                         key =>
                             key.StartsWith(sourceCachePrefix, StringComparison.OrdinalIgnoreCase) ||
                             key.Equals(extensionCacheKey, StringComparison.OrdinalIgnoreCase)))
            {
                s_iconBytesTimeouts.TryRemove(timeoutKey, out _);
            }

            s_iconSourceTimeouts.TryRemove(normalizedSourcePath, out _);
            s_iconSourceTimeouts.TryRemove(
                BuildInternetShortcutFallbackTimeoutKey(normalizedSourcePath),
                out _);
        }
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;

        // For directories, also invalidate the Windows shell icon cache.
        // Tools like Folder Painter modify desktop.ini to change folder icons,
        // but SHGetFileInfo/SHGetImageList return stale icons from the shell's
        // internal cache unless SHChangeNotify is called.
        if (invalidatedDirectoryIcon)
        {
            InvalidateShellIconCache();
        }
    }

    /// <summary>
    /// Notifies the shell that file associations (and therefore folder icons)
    /// have changed, forcing it to discard its cached icons and re-read
    /// desktop.ini on the next SHGetFileInfo call.
    /// Throttled: tools like Folder Painter often rewrite several folders in
    /// quick succession, and each broadcast makes Explorer flush its icon cache.
    /// </summary>
    private static void InvalidateShellIconCache()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref s_lastShellInvalidateMs);
        if (now - last < ShellInvalidateThrottleMs ||
            Interlocked.CompareExchange(ref s_lastShellInvalidateMs, now, last) != last)
        {
            return;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Clears all cached thumbnails.  Called when a widget is reset or
    /// all widgets are cleared.
    /// </summary>
    public static void ClearAllThumbnailCaches()
    {
        lock (s_thumbLock)
        {
            s_thumbCache.Clear();
            s_thumbLru.Clear();
            s_thumbEstimatedBytes.Clear();
            s_thumbLastAccess.Clear();
            s_totalThumbnailEstimatedBytes = 0;
            PerformanceLogger.ThumbnailCacheCount = 0;
            PerformanceLogger.ThumbnailEstimatedBytes = 0;
        }

        ShellThumbnailProxy.ClearTransientFailures();
    }

    /// <summary>
    /// Trims only completed, cold recreatable image entries beyond a half-sized
    /// warm LRU. Recently used and in-flight entries stay cached so showing a
    /// widget again does not turn into a burst of disk, Shell, and decode work.
    /// </summary>
    internal static IdleIconCacheReleaseResult ReleaseIdleCaches() =>
        ReleaseIdleCaches(IdleCacheMinimumAge);

    /// <summary>
    /// Trims completed cold entries to the warm half-capacity targets. The
    /// caller supplies the idle age so the setting that arms maintenance is
    /// also the age used to decide whether an entry is cold.
    /// </summary>
    internal static IdleIconCacheReleaseResult ReleaseIdleCaches(
        TimeSpan minimumAge)
    {
        minimumAge = minimumAge < TimeSpan.Zero
            ? TimeSpan.Zero
            : minimumAge;
        long nowTimestamp = Stopwatch.GetTimestamp();
        int thumbnailCountBefore;
        int thumbnailCountAfter;
        long thumbnailBytesBefore;
        long thumbnailBytesAfter;
        lock (s_thumbLock)
        {
            thumbnailCountBefore = s_thumbCache.Count;
            thumbnailBytesBefore = s_totalThumbnailEstimatedBytes;
            int targetCount = Math.Max(1, MaxThumbnailCacheEntries / 2);
            long targetBytes = Math.Max(1, MaxThumbnailCacheBytes / 2);
            while (s_thumbCache.Count > targetCount ||
                   s_totalThumbnailEstimatedBytes > targetBytes)
            {
                LinkedListNode<string>? oldest =
                    FindOldestEvictableThumbnailNode(
                        coldOnly: true,
                        nowTimestamp: nowTimestamp,
                        minimumAge: minimumAge);
                if (oldest is null)
                {
                    break;
                }

                RemoveThumbnailCacheEntry(oldest.Value);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            PerformanceLogger.ThumbnailEstimatedBytes =
                Math.Max(0, s_totalThumbnailEstimatedBytes);
            thumbnailCountAfter = s_thumbCache.Count;
            thumbnailBytesAfter = s_totalThumbnailEstimatedBytes;
        }

        int bitmapCountBefore;
        int bitmapCountAfter;
        long bitmapBytesBefore;
        long bitmapBytesAfter;
        lock (s_bitmapCacheLock)
        {
            bitmapCountBefore = s_bitmapImageCache.Count;
            bitmapBytesBefore = s_totalBitmapEstimatedBytes;
            int targetCount = Math.Max(1, MaxDecodedBitmapCacheEntries / 2);
            long targetBytes = Math.Max(1, MaxDecodedBitmapCacheBytes / 2);
            while (s_bitmapImageCache.Count > targetCount ||
                   s_totalBitmapEstimatedBytes > targetBytes)
            {
                LinkedListNode<string>? oldest =
                    FindOldestEvictableDecodedBitmapNode(
                        coldOnly: true,
                        nowTimestamp: nowTimestamp,
                        minimumAge: minimumAge);
                if (oldest is null)
                {
                    break;
                }

                RemoveDecodedBitmap(oldest.Value);
            }

            bitmapCountAfter = s_bitmapImageCache.Count;
            bitmapBytesAfter = s_totalBitmapEstimatedBytes;
        }

        int iconByteEntriesBefore = s_iconBytesCache.Count;
        long iconBytesBefore = GetIconByteCacheSize();
        TrimIconByteCacheTo(
            Math.Max(1, MaxIconCacheEntries / 2),
            Math.Max(1, MaxIconCacheBytes / 2),
            coldOnly: true,
            nowTimestamp: nowTimestamp,
            minimumAge: minimumAge);
        long iconBytesAfter = GetIconByteCacheSize();

        return new IdleIconCacheReleaseResult(
            Math.Max(0, thumbnailCountBefore - thumbnailCountAfter),
            Math.Max(0, bitmapCountBefore - bitmapCountAfter),
            Math.Max(0, iconByteEntriesBefore - s_iconBytesCache.Count),
            Math.Max(0, thumbnailBytesBefore - thumbnailBytesAfter) +
                Math.Max(0, bitmapBytesBefore - bitmapBytesAfter) +
                Math.Max(0, iconBytesBefore - iconBytesAfter));
    }

    /// <summary>
    /// Releases every completed entry from the recreatable image caches after all
    /// widgets are hidden. In-flight loads are retained so a late completion
    /// cannot invalidate work that is still producing an image. The live XAML
    /// widget trees are owned elsewhere and are intentionally not touched here.
    /// </summary>
    internal static IdleIconCacheReleaseResult ReleaseHiddenCaches()
    {
        int thumbnailCountBefore;
        int thumbnailCountAfter;
        long thumbnailBytesBefore;
        long thumbnailBytesAfter;
        lock (s_thumbLock)
        {
            thumbnailCountBefore = s_thumbCache.Count;
            thumbnailBytesBefore = s_totalThumbnailEstimatedBytes;
            string[] completedKeys = s_thumbCache
                .Where(entry => entry.Value.IsCompleted)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (string key in completedKeys)
            {
                RemoveThumbnailCacheEntry(key);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            PerformanceLogger.ThumbnailEstimatedBytes =
                Math.Max(0, s_totalThumbnailEstimatedBytes);
            thumbnailCountAfter = s_thumbCache.Count;
            thumbnailBytesAfter = s_totalThumbnailEstimatedBytes;
        }

        int bitmapCountBefore;
        int bitmapCountAfter;
        long bitmapBytesBefore;
        long bitmapBytesAfter;
        lock (s_bitmapCacheLock)
        {
            bitmapCountBefore = s_bitmapImageCache.Count;
            bitmapBytesBefore = s_totalBitmapEstimatedBytes;
            string[] completedKeys = s_bitmapImageCache
                .Where(entry => entry.Value.IsCompleted)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (string key in completedKeys)
            {
                RemoveDecodedBitmap(key);
            }

            UpdateDecodedBitmapDiagnostics();
            bitmapCountAfter = s_bitmapImageCache.Count;
            bitmapBytesAfter = s_totalBitmapEstimatedBytes;
        }

        int iconByteEntriesBefore = s_iconBytesCache.Count;
        long iconBytesBefore = GetIconByteCacheSize();
        TrimIconByteCacheTo(0, 0);
        long iconBytesAfter = GetIconByteCacheSize();

        // Timeout/failure markers are cheap but should not survive a full hidden
        // cleanup, otherwise the next visible session inherits stale retry state.
        s_iconSourceTimeouts.Clear();
        s_iconBytesTimeouts.Clear();
        ShellThumbnailProxy.ClearTransientFailures();

        return new IdleIconCacheReleaseResult(
            Math.Max(0, thumbnailCountBefore - thumbnailCountAfter),
            Math.Max(0, bitmapCountBefore - bitmapCountAfter),
            Math.Max(0, iconByteEntriesBefore - s_iconBytesCache.Count),
            Math.Max(0, thumbnailBytesBefore - thumbnailBytesAfter) +
                Math.Max(0, bitmapBytesBefore - bitmapBytesAfter) +
                Math.Max(0, iconBytesBefore - iconBytesAfter));
    }

    internal static string CurrentPerformanceCacheBudget =>
        Volatile.Read(ref s_cacheBudget);

    internal static void ConfigurePerformanceCacheBudget(string? cacheBudget)
    {
        string normalized =
            PerformanceSettingsPolicy.NormalizeCacheBudget(cacheBudget);
        int budgetPercent = normalized switch
        {
            PerformanceSettingsPolicy.CacheBudgetSmall =>
                SmallCacheBudgetPercent,
            PerformanceSettingsPolicy.CacheBudgetLarge =>
                LargeCacheBudgetPercent,
            _ => BalancedCacheBudgetPercent
        };

        Volatile.Write(ref s_cacheBudget, normalized);
        Volatile.Write(ref s_cacheBudgetPercent, budgetPercent);

        lock (s_thumbLock)
        {
            EvictThumbnailCacheIfNeeded();
            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }

        lock (s_bitmapCacheLock)
        {
            while (s_bitmapImageCache.Count > MaxDecodedBitmapCacheEntries ||
                   s_totalBitmapEstimatedBytes > MaxDecodedBitmapCacheBytes)
            {
                LinkedListNode<string>? oldest =
                    FindOldestEvictableDecodedBitmapNode(
                        coldOnly: false,
                        nowTimestamp: 0);
                if (oldest is null)
                {
                    break;
                }

                RemoveDecodedBitmap(oldest.Value);
            }
        }

        EvictIconCachesIfNeeded();
    }

    /// <summary>
    /// Clears decoded icons and thumbnails owned by a transient caller while
    /// preserving the shared file-widget cache.
    /// </summary>
    public static void ClearCacheScope(string cacheScope)
    {
        if (string.IsNullOrWhiteSpace(cacheScope))
        {
            return;
        }

        string normalizedScope = cacheScope.Trim();
        string bitmapPrefix = normalizedScope + "|";
        foreach (string key in s_bitmapImageCache.Keys.Where(
                     key => key.StartsWith(bitmapPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveDecodedBitmap(key);
        }

        string thumbnailPrefix = $"thumb:{normalizedScope}|";
        lock (s_thumbLock)
        {
            foreach (string key in s_thumbCache.Keys.Where(
                         key => key.StartsWith(thumbnailPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveThumbnailCacheEntry(key);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }
    }

    private static async Task<BitmapImage?> LoadBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        IconSource iconSource,
        string iconBytesCacheKey,
        string bitmapCacheKey,
        string originalSourcePath,
        bool hideShortcutArrowOverlay,
        bool isShortcutPath,
        bool isInternetShortcutPath,
        int decodePixelWidth)
    {
        if (!TryGetCachedIconBytes(iconBytesCacheKey, out var bytes))
        {
            bool shortcutProxyAttempted = false;
            IconSource loadIconSource = iconSource;
            if (isInternetShortcutPath)
            {
                // Resolve the original .url as a Shell item before interpreting
                // IconFile ourselves. This mirrors Explorer and preserves Steam
                // game artwork, custom protocol icons and per-user associations.
                shortcutProxyAttempted = true;
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    originalSourcePath,
                    includeOverlays: !hideShortcutArrowOverlay);
                if (bytes is { Length: > 0 })
                {
                    // Steam and other .url shortcuts frequently carry a 32/48 px
                    // IconFile that Shell centres inside the 256 px canvas.
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes);
                }

                if (bytes is { Length: > 0 })
                {
                    App.LogVerbose(
                        $"[IconHelper] Loaded Internet shortcut icon through " +
                        $"Shell proxy path={originalSourcePath} " +
                        $"overlays={!hideShortcutArrowOverlay}");
                }
                else
                {
                    // Only parse IconFile after the isolated Shell route fails.
                    // The fallback resolver is itself bounded because IconFile
                    // can name an unavailable drive or network location.
                    IconSource? fallbackSource =
                        await ResolveInternetShortcutFallbackSourceAsync(
                            originalSourcePath);
                    if (fallbackSource is not null)
                    {
                        loadIconSource = fallbackSource;
                    }
                }
            }

            if (ShouldPreferHighResolutionShellItemIcon(isShortcutPath))
            {
                // Ask the same isolated Shell-item pipeline used by Explorer for
                // every real file and folder. SHGetImageList's Jumbo slot can be
                // a pre-scaled 32/48 px bitmap even when the registered file icon
                // contains a genuine 256 px frame. Keep the in-process image-list
                // path below as the compatibility fallback.
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    originalSourcePath);
                if (bytes is { Length: > 0 })
                {
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes);
                }

                if (bytes is { Length: > 0 })
                {
                    App.LogVerbose(
                        $"[IconHelper] Loaded high-resolution Shell item icon " +
                        $"path={originalSourcePath}");
                }
            }

            bool preferSolutionShortcutProxy =
                isShortcutPath &&
                hideShortcutArrowOverlay &&
                !ShortcutHelper.IsShortcutPath(loadIconSource.Path) &&
                IsSolutionFilePath(loadIconSource.Path);

            if (preferSolutionShortcutProxy)
            {
                // Visual Studio registers .sln/.slnx through a Shell file
                // association. The in-process image-list APIs can return a
                // technically valid generic page icon for that handler. Ask
                // the isolated Shell proxy first for this narrow case; the
                // direct extraction path below remains the compatibility
                // fallback if the handler is unavailable.
                shortcutProxyAttempted = true;
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    originalSourcePath);
                if (bytes is { Length: > 0 })
                {
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes);
                }

                if (bytes is { Length: > 0 })
                {
                    App.LogVerbose(
                        $"[IconHelper] Loaded solution shortcut icon through " +
                        $"Shell proxy path={originalSourcePath}");
                }
            }

            if (bytes is not { Length: > 0 } &&
                loadIconSource.UsesShellItemIcon &&
                !shortcutProxyAttempted)
            {
                // PIDL/AppUserModelID shortcuts can have no filesystem target or
                // explicit icon resource. Ask the Shell item itself for ICONONLY
                // in the isolated proxy so the main DeskBox process never loads
                // third-party Shell code.
                shortcutProxyAttempted = true;
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    loadIconSource.Path);

                if (isShortcutPath && bytes is { Length: > 0 })
                {
                    // Normalize a sparse Shell-item canvas before it reaches
                    // either the byte cache or BitmapImage. The proxy performs
                    // the same operation, while this defensive pass also
                    // protects alternate proxy builds and test doubles.
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes);
                    if (bytes is null)
                    {
                        App.LogVerbose(
                            $"[IconHelper] Invalid shortcut icon payload " +
                            $"path={loadIconSource.Path}");
                    }
                }
            }

            // Apply the padded-canvas check to every icon source, not only
            // shortcuts: SHGetImageList's Jumbo slot can hand back a small glyph
            // centred in a 256 px canvas for plain files and executables too.
            bool rejectPaddedShortcutIcon = true;

            if (bytes is not { Length: > 0 } &&
                !IsRecentTimeout(s_iconBytesTimeouts, iconBytesCacheKey))
            {
                BoundedBackgroundWorkResult<byte[]?> loadResult =
                    await BoundedBackgroundWorkScheduler.SharedShell.RunAsync(
                        () => TryGetCachedIconBytes(
                                iconBytesCacheKey,
                                out byte[]? cachedBytes)
                            ? cachedBytes
                            : LoadIconBytes(
                                loadIconSource,
                                rejectPaddedShortcutIcon),
                        IconBytesLoadTimeout);
                if (loadResult.Status == BoundedBackgroundWorkStatus.Completed)
                {
                    bytes = loadResult.Value;
                    s_iconBytesTimeouts.TryRemove(iconBytesCacheKey, out _);
                }
                else if (loadResult.Status == BoundedBackgroundWorkStatus.ExecutionTimedOut)
                {
                    RecordTimeout(s_iconBytesTimeouts, iconBytesCacheKey);
                    App.Log(
                        $"[IconHelper] Icon byte load timed out " +
                        $"timeoutMs={IconBytesLoadTimeout.TotalMilliseconds:0} " +
                        $"path={loadIconSource.Path}");
                }
                else if (loadResult.Status == BoundedBackgroundWorkStatus.QueueTimedOut)
                {
                    App.LogVerbose(
                        $"[IconHelper] Icon byte load queue timed out " +
                        $"timeoutMs={IconBytesLoadTimeout.TotalMilliseconds:0} " +
                        $"path={loadIconSource.Path}");
                }
                else if (loadResult.Exception is not null)
                {
                    App.Log(
                        $"[IconHelper] Icon byte load failed " +
                        $"path={loadIconSource.Path}: {loadResult.Exception.Message}");
                }
            }

            if (bytes is { Length: > 0 })
            {
                StoreCachedIconBytes(iconBytesCacheKey, bytes);
                EvictIconCachesIfNeeded();
            }

            if (bytes is not { Length: > 0 } &&
                isShortcutPath &&
                hideShortcutArrowOverlay &&
                !ShortcutHelper.IsShortcutPath(loadIconSource.Path) &&
                !shortcutProxyAttempted)
            {
                // A file-association icon handler (notably Visual Studio's
                // .sln/.slnx handler) can return a blank bitmap through the
                // in-process image-list APIs. Retry from the original .lnk in
                // the isolated Shell proxy so a handler failure cannot leave
                // the tile permanently blank.
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    originalSourcePath);
                if (bytes is { Length: > 0 })
                {
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes);
                }

                if (bytes is { Length: > 0 })
                {
                    StoreCachedIconBytes(iconBytesCacheKey, bytes);
                    EvictIconCachesIfNeeded();
                    App.LogVerbose(
                        $"[IconHelper] Recovered shortcut icon through Shell proxy " +
                        $"path={originalSourcePath}");
                }
            }
        }

        // Decode near the intended display size. This keeps large icons sharp while
        // allowing WIC to perform a high-quality downsample for compact icon layouts
        // instead of asking the compositor to shrink a 256 px bitmap to ~24 px.
        var image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth);
        if (image is null)
        {
            RemoveDecodedBitmap(bitmapCacheKey);
            RemoveCachedIconBytes(iconBytesCacheKey, out _);
        }

        return image;
    }

    internal static bool ShouldPreferHighResolutionShellItemIcon(
        bool isShortcutPath) =>
        !isShortcutPath;

    private static async Task<byte[]?> TryLoadHighResolutionShellItemIconAsync(
        string path,
        bool includeOverlays = false)
    {
        await s_shellIconLoadSemaphore.WaitAsync();
        try
        {
            return await ShellThumbnailProxy.TryLoadIconAsync(
                path,
                requestedSize: PreferredShellItemIconSize,
                includeOverlays: includeOverlays);
        }
        finally
        {
            s_shellIconLoadSemaphore.Release();
        }
    }

    private static async Task<BitmapImage?> LoadBitmapImageWithDiagnosticsAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        IconSource iconSource,
        string iconBytesCacheKey,
        string bitmapCacheKey,
        string originalSourcePath,
        bool hideShortcutArrowOverlay,
        bool isShortcutPath,
        bool isInternetShortcutPath,
        int decodePixelWidth)
    {
        bool collectDiagnostics = PerformanceLogger.IsEnabled;
        long started = collectDiagnostics ? Stopwatch.GetTimestamp() : 0;
        bool succeeded = false;
        try
        {
            BitmapImage? image = await LoadBitmapImageAsync(
                dispatcher,
                iconSource,
                iconBytesCacheKey,
                bitmapCacheKey,
                originalSourcePath,
                hideShortcutArrowOverlay,
                isShortcutPath,
                isInternetShortcutPath,
                decodePixelWidth);
            succeeded = image is not null;
            return image;
        }
        finally
        {
            if (collectDiagnostics)
            {
                PerformanceLogger.RecordDecodedBitmapCacheLoad(
                    Stopwatch.GetElapsedTime(started),
                    succeeded);
            }
        }
    }

    private static LinkedListNode<string>? FindOldestEvictableThumbnailNode(
        bool coldOnly,
        long nowTimestamp,
        TimeSpan? minimumAge = null)
    {
        for (LinkedListNode<string>? node = s_thumbLru.Last;
             node is not null;
             node = node.Previous)
        {
            if (!s_thumbCache.TryGetValue(node.Value, out Task<BitmapImage?>? task))
            {
                return node;
            }

            if (!task.IsCompleted)
            {
                continue;
            }

            if (coldOnly &&
                (!s_thumbLastAccess.TryGetValue(node.Value, out long lastAccess) ||
                 !IsCacheEntryCold(lastAccess, nowTimestamp, minimumAge)))
            {
                continue;
            }

            return node;
        }

        return null;
    }

    private static async Task<BitmapImage?> CreateThumbnailWithDiagnosticsAsync(
        Func<Task<BitmapImage?>> createThumbnail)
    {
        bool collectDiagnostics = PerformanceLogger.IsEnabled;
        long started = collectDiagnostics ? Stopwatch.GetTimestamp() : 0;
        bool succeeded = false;
        try
        {
            BitmapImage? image = await createThumbnail();
            succeeded = image is not null;
            return image;
        }
        finally
        {
            if (collectDiagnostics)
            {
                PerformanceLogger.RecordThumbnailCacheLoad(
                    Stopwatch.GetElapsedTime(started),
                    succeeded);
            }
        }
    }

    private static byte[]? LoadIconBytes(
        IconSource iconSource,
        bool rejectPaddedShortcutIcon = false)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.LoadIconBytes", $"path={iconSource.Path}");
        try
        {
            if (iconSource.UsesExplicitIconIndex)
            {
                var indexedBytes = LoadIndexedIconBytes(
                    iconSource,
                    rejectPaddedShortcutIcon);
                if (indexedBytes is not null)
                {
                    return indexedBytes;
                }
            }

            // Shell's Jumbo image list can contain a 32/48 px icon that has already
            // been enlarged to 256 px. Explorer can still render the same executable
            // crisply because it extracts the best resource frame directly. Do that
            // first for executable-backed items (including resolved shortcuts), then
            // retain the image-list path as a compatibility fallback.
            if (Path.GetExtension(iconSource.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? executableBytes = TryExtractHighResIndexedIcon(
                    iconSource.Path,
                    iconSource.IconIndex,
                    256,
                    rejectPaddedShortcutIcon);
                if (executableBytes is not null)
                {
                    return executableBytes;
                }
            }

            // Get the system icon index, then extract the highest-resolution
            // version available via SHGetImageList (Jumbo 256 → ExtraLarge 48 → Large 32).
            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                iconSource.Path,
                0,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                SHGFI_SYSICONINDEX);

            if (hImg == IntPtr.Zero)
            {
                // Fallback: direct large icon via SHGetFileInfo
                return LoadIconBytesFromShGetFileInfo(
                    iconSource.Path,
                    rejectPaddedShortcutIcon);
            }

            int iconIndex = shinfo.iIcon;

            // Try Jumbo (256×256) first — gives crisp icons on high-DPI displays.
            byte[]? bytes = TryGetIconFromImageList(
                SHIL_JUMBO,
                iconIndex,
                rejectPaddedShortcutIcon);
            if (bytes is not null)
            {
                return bytes;
            }

            // Fall back to Extra Large (48×48).
            bytes = TryGetIconFromImageList(
                SHIL_EXTRALARGE,
                iconIndex,
                rejectPaddedShortcutIcon);
            if (bytes is not null)
            {
                return bytes;
            }

            // Final fallback: Large (32×32) via SHGetFileInfo.
            return LoadIconBytesFromShGetFileInfo(
                iconSource.Path,
                rejectPaddedShortcutIcon);
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load icon for {iconSource.Path}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? TryGetIconFromImageList(
        int imageListFlags,
        int iconIndex,
        bool rejectPaddedShortcutIcon = false)
    {
        IntPtr imageListPtr = IntPtr.Zero;
        IntPtr iconHandle = IntPtr.Zero;

        try
        {
            Guid iid = s_iidIImageList;
            int hr = SHGetImageList(imageListFlags, ref iid, ref imageListPtr);
            if (hr != 0 || imageListPtr == IntPtr.Zero)
            {
                return null;
            }

            var imageList = (IImageList)Marshal.GetObjectForIUnknown(imageListPtr);
            int result = imageList.GetIcon(iconIndex, ILD_TRANSPARENT, ref iconHandle);
            if (result != 0 || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            return EncodeIconBitmapAsPng(bitmap, rejectPaddedShortcutIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }

            if (imageListPtr != IntPtr.Zero)
            {
                Marshal.Release(imageListPtr);
            }
        }
    }

    private static byte[]? LoadIconBytesFromShGetFileInfo(
        string path,
        bool rejectPaddedShortcutIcon = false)
    {
        var shinfo = new SHFILEINFO();
        IntPtr hImg = SHGetFileInfo(
            path,
            0,
            ref shinfo,
            (uint)Marshal.SizeOf(shinfo),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(shinfo.hIcon);
            using var bitmap = icon.ToBitmap();
            return EncodeIconBitmapAsPng(bitmap, rejectPaddedShortcutIcon);
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static byte[]? LoadIndexedIconBytes(
        IconSource iconSource,
        bool rejectPaddedShortcutIcon = false)
    {
        // Try SHDefExtractIcon first — it can extract 256×256 icons from exe/dll/ico resources.
        byte[]? hiResBytes = TryExtractHighResIndexedIcon(
            iconSource.Path,
            iconSource.IconIndex,
            256,
            rejectPaddedShortcutIcon);
        if (hiResBytes is not null)
        {
            return hiResBytes;
        }

        // Fallback: 48×48
        hiResBytes = TryExtractHighResIndexedIcon(
            iconSource.Path,
            iconSource.IconIndex,
            48,
            rejectPaddedShortcutIcon);
        if (hiResBytes is not null)
        {
            return hiResBytes;
        }

        // Final fallback: ExtractIconEx (32×32 large / 16×16 small)
        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        uint count = ExtractIconEx(
            iconSource.Path,
            iconSource.IconIndex,
            largeIcons,
            smallIcons,
            1);

        IntPtr iconHandle = largeIcons[0] != IntPtr.Zero
            ? largeIcons[0]
            : smallIcons[0];

        if (count == 0 || iconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            return EncodeIconBitmapAsPng(bitmap, rejectPaddedShortcutIcon);
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero)
            {
                DestroyIcon(largeIcons[0]);
            }

            if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0])
            {
                DestroyIcon(smallIcons[0]);
            }
        }
    }

    private static byte[]? TryExtractHighResIndexedIcon(
        string filePath,
        int iconIndex,
        int size,
        bool rejectPaddedShortcutIcon = false)
    {
        IntPtr hLarge = IntPtr.Zero;
        IntPtr hSmall = IntPtr.Zero;

        try
        {
            // nIconSize: high word = large icon size, low word = small icon size
            uint nIconSize = ((uint)size << 16) | (uint)size;
            int hr = SHDefExtractIcon(filePath, iconIndex, 0, out hLarge, out hSmall, nIconSize);
            if (hr != 0 || hLarge == IntPtr.Zero)
            {
                return null;
            }

            using var icon = Icon.FromHandle(hLarge);
            using var bitmap = icon.ToBitmap();
            return EncodeIconBitmapAsPng(bitmap, rejectPaddedShortcutIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hLarge != IntPtr.Zero)
            {
                DestroyIcon(hLarge);
            }

            if (hSmall != IntPtr.Zero && hSmall != hLarge)
            {
                DestroyIcon(hSmall);
            }
        }
    }

    private static Task<BitmapImage?> CreateBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        byte[]? bytes,
        int decodePixelWidth = 0)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Task.FromResult<BitmapImage?>(null);
        }

        if (dispatcher.HasThreadAccess)
        {
            return CreateBitmapImageOnUiThreadAsync(bytes, decodePixelWidth);
        }

        var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await CreateBitmapImageOnUiThreadAsync(bytes, decodePixelWidth));
            }
            catch (Exception ex)
            {
                App.Log($"[IconHelper] UI thread set source failed: {ex.Message}");
                tcs.SetResult(null);
            }
        }))
        {
            tcs.SetResult(null);
        }

        return tcs.Task;
    }

    private static async Task<BitmapImage?> CreateBitmapImageOnUiThreadAsync(byte[] bytes, int decodePixelWidth = 0)
    {
        var bmp = new BitmapImage();
        if (decodePixelWidth > 0)
        {
            bmp.DecodePixelWidth = decodePixelWidth;
        }

        using var winrtStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(winrtStream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        winrtStream.Seek(0);
        await bmp.SetSourceAsync(winrtStream);
        return bmp;
    }

    private static async Task<ResolvedIconSource> ResolveIconSourceWithCacheKeyAsync(
        string path,
        bool hideShortcutArrowOverlay)
    {
        string normalizedSourcePath = NormalizeSourcePath(path);
        if (ShortcutHelper.IsInternetShortcutPath(normalizedSourcePath))
        {
            // Internet shortcuts are Shell-authored items. Explorer can combine
            // IconFile/IconIndex, protocol registration and per-user icon cache
            // state that a text parser cannot reliably reproduce. Keep metadata
            // parsing out of the primary path; it remains a bounded fallback if
            // the isolated Shell request fails.
            return await ResolveInternetShortcutIconSourceAsync(
                normalizedSourcePath,
                hideShortcutArrowOverlay);
        }

        if (IsRecentTimeout(s_iconSourceTimeouts, normalizedSourcePath))
        {
            return CreateFallbackIconSource(normalizedSourcePath);
        }

        s_iconSourceTimeouts.TryRemove(normalizedSourcePath, out _);
        BoundedBackgroundWorkResult<ResolvedIconSource> result =
            await s_iconSourceScheduler.RunAsync(
                () =>
                {
                    IconSource source = ResolveIconSource(
                        normalizedSourcePath,
                        hideShortcutArrowOverlay);
                    return new ResolvedIconSource(
                        source,
                        BuildCacheKey(normalizedSourcePath, source));
                },
                IconSourceResolutionTimeout);

        if (result.Status == BoundedBackgroundWorkStatus.Completed &&
            result.Value is not null)
        {
            s_iconSourceTimeouts.TryRemove(normalizedSourcePath, out _);
            return result.Value;
        }

        if (result.Status == BoundedBackgroundWorkStatus.ExecutionTimedOut)
        {
            RecordTimeout(s_iconSourceTimeouts, normalizedSourcePath);
            App.Log(
                $"[IconHelper] Icon source resolution timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Status == BoundedBackgroundWorkStatus.QueueTimedOut)
        {
            App.LogVerbose(
                $"[IconHelper] Icon source resolution queue timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Exception is not null)
        {
            App.Log(
                $"[IconHelper] Icon source resolution failed " +
                $"path={normalizedSourcePath}: {result.Exception.Message}");
        }

        return CreateFallbackIconSource(normalizedSourcePath);
    }

    private static async Task<ResolvedIconSource>
        ResolveInternetShortcutIconSourceAsync(
            string normalizedSourcePath,
            bool hideShortcutArrowOverlay)
    {
        if (IsRecentTimeout(s_iconSourceTimeouts, normalizedSourcePath))
        {
            return CreateInternetShortcutIconSource(
                normalizedSourcePath,
                hideShortcutArrowOverlay,
                sourceVersion: "unknown");
        }

        s_iconSourceTimeouts.TryRemove(normalizedSourcePath, out _);
        BoundedBackgroundWorkResult<string> result =
            await s_iconSourceScheduler.RunAsync(
                () => GetFileIconVersion(normalizedSourcePath),
                IconSourceResolutionTimeout);
        if (result.Status == BoundedBackgroundWorkStatus.Completed &&
            result.Value is not null)
        {
            s_iconSourceTimeouts.TryRemove(normalizedSourcePath, out _);
            return CreateInternetShortcutIconSource(
                normalizedSourcePath,
                hideShortcutArrowOverlay,
                result.Value);
        }

        if (result.Status == BoundedBackgroundWorkStatus.ExecutionTimedOut)
        {
            RecordTimeout(s_iconSourceTimeouts, normalizedSourcePath);
            App.Log(
                $"[IconHelper] Internet shortcut version probe timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Status == BoundedBackgroundWorkStatus.QueueTimedOut)
        {
            App.LogVerbose(
                $"[IconHelper] Internet shortcut version probe queue timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Exception is not null)
        {
            App.Log(
                $"[IconHelper] Internet shortcut version probe failed " +
                $"path={normalizedSourcePath}: {result.Exception.Message}");
        }

        return CreateInternetShortcutIconSource(
            normalizedSourcePath,
            hideShortcutArrowOverlay,
            sourceVersion: "unknown");
    }

    private static ResolvedIconSource CreateInternetShortcutIconSource(
        string normalizedSourcePath,
        bool hideShortcutArrowOverlay,
        string sourceVersion) =>
        new(
            new IconSource(
                normalizedSourcePath,
                UsesShellItemIcon: true),
            $"{BuildSourceCachePrefix(normalizedSourcePath)}" +
            $"{InternetShortcutIconStrategyVersion}:" +
            $"overlay={(hideShortcutArrowOverlay ? "hidden" : "shown")}:" +
            sourceVersion);

    private static async Task<IconSource?>
        ResolveInternetShortcutFallbackSourceAsync(
            string normalizedSourcePath)
    {
        string timeoutKey = BuildInternetShortcutFallbackTimeoutKey(
            normalizedSourcePath);
        if (IsRecentTimeout(s_iconSourceTimeouts, timeoutKey))
        {
            return null;
        }

        s_iconSourceTimeouts.TryRemove(timeoutKey, out _);
        BoundedBackgroundWorkResult<IconSource> result =
            await s_iconSourceScheduler.RunAsync(
                () => ResolveIconSource(
                    normalizedSourcePath,
                    hideShortcutArrowOverlay: true),
                IconSourceResolutionTimeout);

        if (result.Status == BoundedBackgroundWorkStatus.Completed &&
            result.Value is not null)
        {
            s_iconSourceTimeouts.TryRemove(timeoutKey, out _);
            return result.Value;
        }

        if (result.Status == BoundedBackgroundWorkStatus.ExecutionTimedOut)
        {
            RecordTimeout(s_iconSourceTimeouts, timeoutKey);
            App.Log(
                $"[IconHelper] Internet shortcut fallback resolution timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Status == BoundedBackgroundWorkStatus.QueueTimedOut)
        {
            App.LogVerbose(
                $"[IconHelper] Internet shortcut fallback queue timed out " +
                $"timeoutMs={IconSourceResolutionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedSourcePath}");
        }
        else if (result.Exception is not null)
        {
            App.Log(
                $"[IconHelper] Internet shortcut fallback resolution failed " +
                $"path={normalizedSourcePath}: {result.Exception.Message}");
        }

        return null;
    }

    private static string BuildInternetShortcutFallbackTimeoutKey(
        string normalizedSourcePath) =>
        $"url-fallback:{normalizedSourcePath}";

    private static bool IsRecentTimeout(
        ConcurrentDictionary<string, long> timeouts,
        string key)
    {
        if (!timeouts.TryGetValue(key, out long lastTimeout))
        {
            return false;
        }

        if (Environment.TickCount64 - lastTimeout < IconSourceTimeoutRetryMs)
        {
            return true;
        }

        timeouts.TryRemove(key, out _);
        return false;
    }

    private static void RecordTimeout(
        ConcurrentDictionary<string, long> timeouts,
        string key)
    {
        if (timeouts.Count >= MaxIconSourceTimeoutEntries &&
            !timeouts.ContainsKey(key))
        {
            timeouts.Clear();
        }

        timeouts[key] = Environment.TickCount64;
    }

    private static ResolvedIconSource CreateFallbackIconSource(
        string normalizedSourcePath) =>
        new(
            new IconSource(
                normalizedSourcePath,
                UsesShellItemIcon: ShortcutHelper.IsShortcutPath(normalizedSourcePath)),
            $"{BuildSourceCachePrefix(normalizedSourcePath)}fallback:" +
            (ShortcutHelper.IsShortcutPath(normalizedSourcePath)
                ? "shell-item"
                : "direct"));

    private static string NormalizeSourcePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static byte[]? EncodeVisibleBitmapAsPng(Bitmap bitmap)
    {
        if (!HasVisiblePixels(bitmap))
        {
            return null;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[]? EncodeVisibleBitmapAsPng(
        Bitmap bitmap,
        bool rejectPaddedShortcutIcon)
    {
        if (!TryGetVisibleBounds(bitmap, out Rectangle visibleBounds))
        {
            return null;
        }

        if (rejectPaddedShortcutIcon &&
            IconBitmapQuality.IsLikelyPadded(
                bitmap.Width,
                bitmap.Height,
                visibleBounds.Width,
                visibleBounds.Height))
        {
            using var cropped = bitmap.Clone(
                visibleBounds,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            return EncodeVisibleBitmapAsPng(cropped);
        }

        return EncodeVisibleBitmapAsPng(bitmap);
    }

    private static byte[]? EncodeIconBitmapAsPng(
        Bitmap bitmap,
        bool rejectPaddedShortcutIcon)
    {
        return rejectPaddedShortcutIcon
            ? EncodeVisibleBitmapAsPng(bitmap, rejectPaddedShortcutIcon: true)
            : EncodeVisibleBitmapAsPng(bitmap);
    }

    private static bool TryGetVisibleBounds(
        Bitmap bitmap,
        out Rectangle visibleBounds)
    {
        visibleBounds = Rectangle.Empty;
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        try
        {
            return TryGetVisibleBoundsWithLockBits(bitmap, out visibleBounds);
        }
        catch
        {
            // A few Shell icon handles expose an indexed or device-dependent
            // pixel format that LockBits cannot convert directly. The fallback
            // is used only for those rare formats and keeps the quality gate
            // from turning a valid icon into a blank result.
            return TryGetVisibleBoundsWithGetPixel(bitmap, out visibleBounds);
        }
    }

    private static unsafe bool TryGetVisibleBoundsWithLockBits(
        Bitmap bitmap,
        out Rectangle visibleBounds)
    {
        visibleBounds = Rectangle.Empty;
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            bounds,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                int stride = data.Stride;
                int rowOffset = stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) * -stride;
                byte* row = (byte*)data.Scan0 + rowOffset;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (row[(x * 4) + 3] == 0)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (maxX < minX || maxY < minY)
        {
            return false;
        }

        visibleBounds = Rectangle.FromLTRB(
            minX,
            minY,
            maxX + 1,
            maxY + 1);
        return true;
    }

    private static bool TryGetVisibleBoundsWithGetPixel(
        Bitmap bitmap,
        out Rectangle visibleBounds)
    {
        visibleBounds = Rectangle.Empty;
        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return false;
        }

        visibleBounds = Rectangle.FromLTRB(
            minX,
            minY,
            maxX + 1,
            maxY + 1);
        return true;
    }

    private static unsafe bool HasVisiblePixels(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            bounds,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int rowOffset = stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) * -stride;
                byte* row = (byte*)data.Scan0 + rowOffset;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (row[(x * 4) + 3] != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IconSource ResolveIconSource(string path, bool hideShortcutArrowOverlay)
    {
        if (!ShortcutHelper.IsShortcutPath(path))
        {
            return new IconSource(path);
        }

        if (!hideShortcutArrowOverlay)
        {
            return new IconSource(path);
        }

        var shortcut = ShortcutHelper.ReadStoredMetadata(path);

        if (shortcut is null)
        {
            return new IconSource(path, UsesShellItemIcon: true);
        }

        // Parse icon location — may contain a comma-separated index (e.g. "steam.exe,0")
        var (iconFilePath, iconFileIndex) = SplitIconLocation(shortcut.IconLocation);
        string? iconLocation = NormalizeIconLocation(iconFilePath);
        int resolvedIconIndex = iconFileIndex ?? shortcut.IconIndex;

        if (!string.IsNullOrWhiteSpace(iconLocation) &&
            TryResolveRelativeIconLocation(
                path,
                iconLocation,
                out string resolvedRelativeIconLocation))
        {
            iconLocation = resolvedRelativeIconLocation;
        }

        if (!string.IsNullOrWhiteSpace(iconLocation) &&
            File.Exists(iconLocation))
        {
            return new IconSource(iconLocation, resolvedIconIndex, UsesExplicitIconIndex: true);
        }

        // For Steam .url shortcuts, the IconFile may point to a steam.exe path
        // that doesn't exist on this machine. Try to locate steam.exe via registry.
        if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) &&
            shortcut.TargetPath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            string? steamExe = TryFindSteamExecutable();
            if (steamExe is not null && File.Exists(steamExe))
            {
                return new IconSource(steamExe, 0, UsesExplicitIconIndex: true);
            }
        }

        // If the icon location references an .exe/.dll/.ico that doesn't exist,
        // but we have the original iconLocation string, try finding the file
        // by searching common locations (e.g., strip path and search PATH).
        if (!string.IsNullOrWhiteSpace(iconLocation))
        {
            string? foundPath = TryFindExecutableInPath(iconLocation);
            if (foundPath is not null)
            {
                return new IconSource(foundPath, resolvedIconIndex, UsesExplicitIconIndex: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) &&
            (File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath)))
        {
            return new IconSource(shortcut.TargetPath);
        }

        return new IconSource(path, UsesShellItemIcon: true);
    }

    internal static bool TryResolveRelativeIconLocation(
        string shortcutPath,
        string iconLocation,
        out string resolvedIconLocation)
    {
        resolvedIconLocation = string.Empty;
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            string.IsNullOrWhiteSpace(iconLocation) ||
            Path.IsPathRooted(iconLocation) ||
            iconLocation.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string? shortcutDirectory = Path.GetDirectoryName(shortcutPath);
            if (string.IsNullOrWhiteSpace(shortcutDirectory))
            {
                return false;
            }

            string candidate = Path.GetFullPath(
                Path.Combine(shortcutDirectory, iconLocation));
            if (!File.Exists(candidate))
            {
                return false;
            }

            resolvedIconLocation = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Splits an icon location string into (path, index).
    /// Handles formats like "C:\\path\\to\\file.exe,0" or "file.exe,-5".
    /// </summary>
    internal static (string Path, int? Index) SplitIconLocation(
        string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return (string.Empty, null);
        }

        string trimmed = iconLocation.Trim();
        int lastComma = trimmed.LastIndexOf(',');
        if (lastComma <= 0 || lastComma == trimmed.Length - 1)
        {
            return (trimmed.Trim('"'), null);
        }

        string indexPart = trimmed[(lastComma + 1)..].Trim();
        if (int.TryParse(indexPart, out int index))
        {
            return (trimmed[..lastComma].Trim().Trim('"'), index);
        }

        return (trimmed.Trim('"'), null);
    }

    private static string? TryFindSteamExecutable()
    {
        try
        {
            // Check HKCU\Software\Valve\Steam\SteamPath
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                string exePath = Path.Combine(steamPath, "steam.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }

            // Check HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath
            using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key2?.GetValue("InstallPath") is string installPath)
            {
                string exePath = Path.Combine(installPath, "steam.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }
        }
        catch
        {
            // Ignore registry access errors
        }

        // Try common install locations
        string[] commonPaths =
        {
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Steam\steam.exe",
        };

        foreach (string p in commonPaths)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string? TryFindExecutableInPath(string filePath)
    {
        try
        {
            // If the path is just a filename (no directory), search system PATH
            if (!Path.IsPathRooted(filePath) && filePath.IndexOf(Path.DirectorySeparatorChar) < 0)
            {
                string? fullPath = FindInPath(filePath);
                if (fullPath is not null)
                {
                    return fullPath;
                }
            }

            // Try common Program Files locations
            string fileName = Path.GetFileName(filePath);
            string[] searchDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };

            foreach (string dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    private static string? FindInPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid path entries
            }
        }

        return null;
    }

    private static string? NormalizeIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return null;
        }

        // Strip any trailing comma-separated icon index (e.g. "steam.exe,0" → "steam.exe")
        string trimmed = iconLocation.Trim().Trim('"');
        int lastComma = trimmed.LastIndexOf(',');
        if (lastComma > 0 && lastComma < trimmed.Length - 1)
        {
            string afterComma = trimmed[(lastComma + 1)..];
            if (int.TryParse(afterComma, out _))
            {
                trimmed = trimmed[..lastComma];
            }
        }

        string expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }

    private static string BuildCacheKey(string sourcePath, IconSource iconSource)
    {
        string sourceCachePrefix = BuildSourceCachePrefix(sourcePath);
        string resolvedPath = iconSource.Path;
        if (Directory.Exists(resolvedPath))
        {
            return $"{sourceCachePrefix}dir:{resolvedPath}:{GetDirectoryIconVersion(resolvedPath)}";
        }

        string extension = Path.GetExtension(resolvedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return $"{sourceCachePrefix}path:{resolvedPath}";
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ShortcutHelper.IsShortcutPath(sourcePath))
        {
            string sourceVersion = ShortcutHelper.IsShortcutPath(sourcePath)
                ? GetFileIconVersion(sourcePath)
                : "source";
            return $"{sourceCachePrefix}path:{resolvedPath}:{iconSource.IconIndex}:{iconSource.UsesExplicitIconIndex}:{iconSource.UsesShellItemIcon}:{GetFileIconVersion(resolvedPath)}:{sourceVersion}";
        }

        // Generic file-type icons remain shared across every file with the same
        // extension. Path-specific prefixes are only needed for directories,
        // executables, icon files and shortcuts whose icon source can vary.
        return $"ext:{extension}";
    }

    private static string BuildSourceCachePrefix(string normalizedSourcePath) =>
        $"source:{normalizedSourcePath}|";

    private static string GetDirectoryIconVersion(string directoryPath)
    {
        try
        {
            string desktopIniPath = Path.Combine(directoryPath, "desktop.ini");
            long directoryTicks = Directory.GetLastWriteTimeUtc(directoryPath).Ticks;
            long desktopIniTicks = File.Exists(desktopIniPath)
                ? File.GetLastWriteTimeUtc(desktopIniPath).Ticks
                : 0;
            return $"{directoryTicks:x}:{desktopIniTicks:x}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetFileIconVersion(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return "missing";
            }

            var fileInfo = new FileInfo(filePath);
            return $"{fileInfo.Length:x}:{fileInfo.LastWriteTimeUtc.Ticks:x}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static long EstimateDecodedBitmapBytes(int decodePixelWidth)
    {
        int width = decodePixelWidth <= 0 ? 256 : decodePixelWidth;
        return (long)width * width * 4;
    }

    private static void TrackDecodedBitmap(string cacheKey, long estimatedBytes)
    {
        lock (s_bitmapCacheLock)
        {
            if (!s_bitmapImageCache.ContainsKey(cacheKey))
            {
                return;
            }

            s_bitmapLastAccess[cacheKey] = Stopwatch.GetTimestamp();
            if (s_bitmapLruNodes.TryGetValue(cacheKey, out var existingNode))
            {
                s_bitmapLru.Remove(existingNode);
                s_bitmapLru.AddFirst(existingNode);
            }
            else
            {
                var node = s_bitmapLru.AddFirst(cacheKey);
                s_bitmapLruNodes[cacheKey] = node;
                s_bitmapEstimatedBytes[cacheKey] = estimatedBytes;
                s_totalBitmapEstimatedBytes += estimatedBytes;
            }

            while (s_bitmapImageCache.Count > MaxDecodedBitmapCacheEntries ||
                   s_totalBitmapEstimatedBytes > MaxDecodedBitmapCacheBytes)
            {
                LinkedListNode<string>? oldestNode =
                    FindOldestEvictableDecodedBitmapNode(
                        coldOnly: false,
                        nowTimestamp: 0);
                if (oldestNode is null)
                {
                    break;
                }

                string oldestKey = oldestNode.Value;
                s_bitmapLru.Remove(oldestNode);
                s_bitmapLruNodes.Remove(oldestKey);
                s_bitmapLastAccess.Remove(oldestKey);
                if (s_bitmapEstimatedBytes.Remove(oldestKey, out long removedBytes))
                {
                    s_totalBitmapEstimatedBytes -= removedBytes;
                }

                s_bitmapImageCache.TryRemove(oldestKey, out _);
            }

            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void RemoveDecodedBitmap(string cacheKey)
    {
        lock (s_bitmapCacheLock)
        {
            s_bitmapImageCache.TryRemove(cacheKey, out _);
            if (s_bitmapLruNodes.Remove(cacheKey, out var node))
            {
                s_bitmapLru.Remove(node);
            }

            s_bitmapLastAccess.Remove(cacheKey);
            if (s_bitmapEstimatedBytes.Remove(cacheKey, out long removedBytes))
            {
                s_totalBitmapEstimatedBytes -= removedBytes;
            }

            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void UpdateDecodedBitmapDiagnostics()
    {
        PerformanceLogger.DecodedBitmapCacheCount = s_bitmapImageCache.Count;
        PerformanceLogger.DecodedBitmapEstimatedBytes = Math.Max(0, s_totalBitmapEstimatedBytes);
    }

    private static LinkedListNode<string>? FindOldestEvictableDecodedBitmapNode(
        bool coldOnly,
        long nowTimestamp,
        TimeSpan? minimumAge = null)
    {
        for (LinkedListNode<string>? node = s_bitmapLru.Last;
             node is not null;
             node = node.Previous)
        {
            if (!s_bitmapImageCache.TryGetValue(
                    node.Value,
                    out Task<BitmapImage?>? task))
            {
                return node;
            }

            if (!task.IsCompleted)
            {
                continue;
            }

            if (coldOnly &&
                (!s_bitmapLastAccess.TryGetValue(node.Value, out long lastAccess) ||
                 !IsCacheEntryCold(lastAccess, nowTimestamp, minimumAge)))
            {
                continue;
            }

            return node;
        }

        return null;
    }

    private static void EvictIconCachesIfNeeded()
    {
        long cachedBytes = GetIconByteCacheSize();
        if (s_iconBytesCache.Count > MaxIconCacheEntries ||
            cachedBytes > MaxIconCacheBytes)
        {
            TrimIconByteCacheTo(
                Math.Max(1, MaxIconCacheEntries / 2),
                Math.Max(1, MaxIconCacheBytes / 2));
        }

        // Update diagnostics
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
    }

    private static void TrimIconByteCacheTo(
        int targetCount,
        long targetBytes,
        bool coldOnly = false,
        long nowTimestamp = 0,
        TimeSpan? minimumAge = null)
    {
        long cachedBytes = GetIconByteCacheSize();
        var candidates = s_iconBytesCache
            .Select(entry => (
                Key: entry.Key,
                Bytes: entry.Value,
                LastAccess: s_iconBytesLastAccess.TryGetValue(
                    entry.Key,
                    out long lastAccess)
                        ? lastAccess
                        : 0))
            .OrderBy(entry => entry.LastAccess)
            .ToList();
        foreach (var candidate in candidates)
        {
            if (s_iconBytesCache.Count <= targetCount &&
                cachedBytes <= targetBytes)
            {
                break;
            }

            if (coldOnly &&
                (candidate.LastAccess == 0 ||
                 !IsCacheEntryCold(candidate.LastAccess, nowTimestamp, minimumAge)))
            {
                continue;
            }

            if (RemoveCachedIconBytes(candidate.Key, out byte[]? removedBytes))
            {
                cachedBytes -= removedBytes?.LongLength ?? 0;
            }
        }

        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
    }

    private static long GetIconByteCacheSize() =>
        s_iconBytesCache.Values.Sum(bytes => bytes?.LongLength ?? 0);

    private static bool TryGetCachedIconBytes(
        string cacheKey,
        out byte[]? bytes)
    {
        if (!s_iconBytesCache.TryGetValue(cacheKey, out bytes))
        {
            return false;
        }

        s_iconBytesLastAccess[cacheKey] = Stopwatch.GetTimestamp();
        return true;
    }

    private static void TouchCachedIconBytes(string cacheKey)
    {
        if (s_iconBytesCache.ContainsKey(cacheKey))
        {
            s_iconBytesLastAccess[cacheKey] = Stopwatch.GetTimestamp();
        }
    }

    private static void StoreCachedIconBytes(string cacheKey, byte[] bytes)
    {
        s_iconBytesCache[cacheKey] = bytes;
        s_iconBytesLastAccess[cacheKey] = Stopwatch.GetTimestamp();
    }

    private static bool RemoveCachedIconBytes(
        string cacheKey,
        out byte[]? bytes)
    {
        bool removed = s_iconBytesCache.TryRemove(cacheKey, out bytes);
        s_iconBytesLastAccess.TryRemove(cacheKey, out _);
        return removed;
    }

    private static bool IsCacheEntryCold(
        long lastAccessTimestamp,
        long nowTimestamp,
        TimeSpan? minimumAge = null)
    {
        return lastAccessTimestamp > 0 &&
               nowTimestamp >= lastAccessTimestamp &&
               Stopwatch.GetElapsedTime(lastAccessTimestamp, nowTimestamp) >=
                   (minimumAge ?? IdleCacheMinimumAge);
    }

    private static int ScaleCacheEntryLimit(int baseline)
    {
        int percent = Volatile.Read(ref s_cacheBudgetPercent);
        return Math.Max(1, baseline * percent / BalancedCacheBudgetPercent);
    }

    private static long ScaleCacheByteLimit(long baseline)
    {
        int percent = Volatile.Read(ref s_cacheBudgetPercent);
        return Math.Max(1, baseline * percent / BalancedCacheBudgetPercent);
    }
}
