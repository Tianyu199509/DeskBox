using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using DeskBox.Platform;
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
    internal const int MaxNonSharedIconBytePayloadLength = 85_000;
    private const int MaxIconSourceTimeoutEntries = 256;
    private const long IconSourceTimeoutRetryMs = 30_000;
    private static readonly TimeSpan IconSourceResolutionTimeout =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan IconBytesLoadTimeout =
        TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan IdleCacheMinimumAge =
        TimeSpan.FromMinutes(5);
    private const int PreferredShellItemIconSize = 256;
    // Frame sizes of the legacy Shell icons served for file types whose own icon
    // resource is gone. Requesting one of these returns a canvas the artwork
    // actually fills, unlike a Jumbo request that pads it.
    private static readonly int[] s_shellItemIconNativeFrameSizes = { 48, 32 };
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
    private static readonly Dictionary<string, HashSet<string>> s_bitmapKeysByScope = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> s_iconByteKeysByScope = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> s_scopesByIconByteKey = new(StringComparer.OrdinalIgnoreCase);
    private static long s_totalBitmapEstimatedBytes;

    // ── Media thumbnail LRU cache (separate from icon cache) ──────
    // Uses a linked list + dictionary for simple LRU eviction.
    private static readonly object s_thumbLock = new();
    private static readonly LinkedList<string> s_thumbLru = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> s_thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_thumbEstimatedBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_thumbLastAccess = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> s_thumbKeysByScope = new(StringComparer.OrdinalIgnoreCase);
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

    private const long ShellInvalidateThrottleMs = 500;
    private static long s_lastShellInvalidateMs;

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
        bool isShortcutPath = ShortcutHelper.IsShortcutPath(path);
        bool isInternetShortcutPath =
            ShortcutHelper.IsInternetShortcutPath(path);
        int normalizedDecodePixelWidth = Math.Clamp(decodePixelWidth, 0, 256);
        int requestedIconSize = QuantizeShellIconRequestSize(normalizedDecodePixelWidth);
        string iconBytesCacheKey = $"{cacheKey}:size={requestedIconSize}";
        TouchCachedIconBytes(iconBytesCacheKey);
        IndexIconBytesScopeKey(normalizedCacheScope, iconBytesCacheKey);
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
                    iconBytesCacheKey,
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
                IndexThumbnailScopeKey(cacheKey);
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

            // Fallback: decode to the requested display size through the
            // pinned built-in WIC thumbnail decoder so WinUI never retains an
            // arbitrary multi-megabyte original file stream in CBitmapImage.
            byte[]? decodedBytes = await WicBuiltInThumbnailDecoder.DecodeAsync(
                path,
                decodePixelWidth);
            return decodedBytes is { Length: > 0 }
                ? await CreateBitmapImageAsync(
                    dispatcher,
                    decodedBytes,
                    decodePixelWidth)
                : null;
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
            if (bytes is not { Length: > 0 })
            {
                // The proxy failed, or the path sits in its 30 s failure
                // blacklist (feedback 226/452/495: the proxy process could
                // die with CRT runtime error R6016 after sleep/resume).
                // Containers a built-in WIC decoder can read are still
                // decodable in-process; the decoder is pinned by GUID so no
                // third-party codec loads into DeskBox.
                bytes = await WicBuiltInThumbnailDecoder.DecodeAsync(
                    path,
                    decodePixelWidth);
            }

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
        UnindexThumbnailScopeKey(cacheKey);
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
        if (decodePixelWidth > 0)
        {
            bmp.DecodePixelType = DecodePixelType.Physical;
            bmp.DecodePixelWidth = decodePixelWidth;
        }

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
            WicBuiltInThumbnailDecoder.Invalidate(path);
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
        bool invalidatedDirectoryIcon = false;
        foreach (string bitmapKey in s_bitmapImageCache.Keys.Where(
                     key => key.Contains(bitmapCacheMarker, StringComparison.OrdinalIgnoreCase)))
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

        if (resetTransientFailures)
        {
            foreach (string timeoutKey in s_iconBytesTimeouts.Keys.Where(
                         key => key.StartsWith(sourceCachePrefix, StringComparison.OrdinalIgnoreCase)))
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
        // but ShellIconNativeMethods.SHGetFileInfo/ShellIconNativeMethods.SHGetImageList return stale icons from the shell's
        // internal cache unless ShellIconNativeMethods.SHChangeNotify is called.
        if (invalidatedDirectoryIcon)
        {
            InvalidateShellIconCache();
        }
    }

    /// <summary>
    /// Notifies the shell that file associations (and therefore folder icons)
    /// have changed, forcing it to discard its cached icons and re-read
    /// desktop.ini on the next ShellIconNativeMethods.SHGetFileInfo call.
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

        ShellIconNativeMethods.SHChangeNotify(ShellIconNativeMethods.SHCNE_ASSOCCHANGED, ShellIconNativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
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
            s_thumbKeysByScope.Clear();
            s_thumbLru.Clear();
            s_thumbEstimatedBytes.Clear();
            s_thumbLastAccess.Clear();
            s_totalThumbnailEstimatedBytes = 0;
            PerformanceLogger.ThumbnailCacheCount = 0;
            PerformanceLogger.ThumbnailEstimatedBytes = 0;
        }

        ShellThumbnailProxy.ClearTransientFailures();
        WicBuiltInThumbnailDecoder.ClearTransientFailures();
    }

    /// <summary>
    /// Clears only the retry-state markers (timeout blacklists), keeping
    /// every decoded entry. Recovery refreshes call this so previously
    /// timed-out sources get a fresh attempt without the visual cost of the
    /// blanket icon clear the freshness ledger retired.
    /// </summary>
    internal static void ResetTransientFailures()
    {
        s_iconSourceTimeouts.Clear();
        s_iconBytesTimeouts.Clear();
        ShellThumbnailProxy.ClearTransientFailures();
        WicBuiltInThumbnailDecoder.ClearTransientFailures();
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
        WicBuiltInThumbnailDecoder.ClearTransientFailures();

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
        List<string>? exclusiveIconByteKeys = null;
        lock (s_bitmapCacheLock)
        {
            if (s_bitmapKeysByScope.TryGetValue(normalizedScope, out HashSet<string>? bitmapKeys) &&
                bitmapKeys.Count > 0)
            {
                foreach (string key in bitmapKeys.ToArray())
                {
                    RemoveDecodedBitmapCore(key);
                }

                UpdateDecodedBitmapDiagnostics();
            }

            if (s_iconByteKeysByScope.Remove(normalizedScope, out HashSet<string>? iconByteKeys))
            {
                bool allowEvictByteEntries = !string.Equals(
                    normalizedScope,
                    SharedCacheScope,
                    StringComparison.OrdinalIgnoreCase);
                foreach (string iconByteKey in iconByteKeys)
                {
                    if (!s_scopesByIconByteKey.TryGetValue(iconByteKey, out HashSet<string>? scopes))
                    {
                        continue;
                    }

                    scopes.Remove(normalizedScope);
                    if (scopes.Count == 0)
                    {
                        s_scopesByIconByteKey.Remove(iconByteKey);
                        if (allowEvictByteEntries &&
                            !iconByteKey.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                        {
                            (exclusiveIconByteKeys ??= []).Add(iconByteKey);
                        }
                    }
                }
            }
        }

        if (exclusiveIconByteKeys is not null)
        {
            foreach (string iconByteKey in exclusiveIconByteKeys)
            {
                RemoveCachedIconBytes(iconByteKey, out _);
            }

            PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
        }

        lock (s_thumbLock)
        {
            if (s_thumbKeysByScope.TryGetValue(normalizedScope, out HashSet<string>? thumbKeys) &&
                thumbKeys.Count > 0)
            {
                foreach (string key in thumbKeys.ToArray())
                {
                    RemoveThumbnailCacheEntry(key);
                }
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }
    }

    internal static int QuantizeShellIconRequestSize(int decodePixelWidth)
    {
        if (decodePixelWidth <= 0)
        {
            return PreferredShellItemIconSize;
        }

        if (decodePixelWidth <= 48)
        {
            return 48;
        }

        if (decodePixelWidth <= 96)
        {
            return 96;
        }

        return PreferredShellItemIconSize;
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
        int requestedIconSize = QuantizeShellIconRequestSize(decodePixelWidth);
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
                    includeOverlays: !hideShortcutArrowOverlay,
                    requestedSize: requestedIconSize);
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

            // A resolved source with an explicit icon index (a custom icon
            // location such as "shell32.dll,-238" or the Steam executable)
            // names the exact frame the shortcut author picked. The Shell
            // item pipeline ignores that index and answers with the file's
            // default frame, so those sources must keep going to the indexed
            // extraction below instead of taking this branch.
            if (!ShortcutHelper.IsShortcutPath(loadIconSource.Path) &&
                !loadIconSource.UsesExplicitIconIndex)
            {
                // Ask the same isolated Shell-item pipeline used by Explorer for
                // every real file and folder. This also covers a shortcut whose
                // icon source resolved past the .lnk (the target file): the
                // resolved source is an ordinary file, no shortcut overlay is
                // involved, and skipping this pipeline is what made shortcut
                // tiles render as pre-scaled 32/48 px image-list bitmaps. A
                // source that still is the .lnk itself keeps the image-list
                // path so the arrow-overlay rendering the user chose is
                // preserved.
                bytes = await TryLoadFileShellItemIconAsync(
                    loadIconSource.Path,
                    requestedIconSize);
                if (bytes is { Length: > 0 })
                {
                    App.LogVerbose(
                        $"[IconHelper] Loaded high-resolution Shell item icon " +
                        $"path={loadIconSource.Path}");
                }
            }
            else if (isShortcutPath && !hideShortcutArrowOverlay)
            {
                // Arrow-overlay mode: the source is the .lnk itself, so ask the
                // Shell explicitly for the item icon WITH overlays through the
                // proxy (system image list, up to 256 px). The in-process
                // image-list fallback below caps this at pre-scaled 32/48 px,
                // which rendered every arrow shortcut as a blurry tile.
                bytes = await TryLoadHighResolutionShellItemIconAsync(
                    originalSourcePath,
                    includeOverlays: true,
                    requestedSize: requestedIconSize);
                if (bytes is { Length: > 0 })
                {
                    bytes = ShellThumbnailProxy.NormalizeIconPayload(bytes) ?? bytes;
                }

                if (bytes is { Length: > 0 })
                {
                    App.LogVerbose(
                        $"[IconHelper] Loaded high-resolution overlay icon " +
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
                    originalSourcePath,
                    includeOverlays: false,
                    requestedSize: requestedIconSize);
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
                    loadIconSource.Path,
                    includeOverlays: false,
                    requestedSize: requestedIconSize);

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

            bool rejectPaddedShortcutIcon =
                isShortcutPath &&
                (loadIconSource.UsesShellItemIcon ||
                 ShortcutHelper.IsShortcutPath(loadIconSource.Path));

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
                                rejectPaddedShortcutIcon,
                                allowGenericPerInstanceFallback: false),
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
                bytes = CompressBmpPayloadToPngIfPossible(bytes);
                StoreCachedIconBytes(iconBytesCacheKey, bytes);
                EvictIconCachesIfNeeded();
            }

            if (bytes is not { Length: > 0 } &&
                loadIconSource.UsesExplicitIconIndex)
            {
                // An explicit icon index can name a resource no extractor can
                // satisfy (a packed executable, a stripped resource section):
                // both the Shell item route above and the indexed extraction
                // answer null for the same source, and because null results
                // are never cached the tile stays blank across restarts. The
                // Shell item still resolves a default frame for such a file;
                // a default icon beats a permanently blank tile, and only a
                // successful recovery is cached.
                bytes = await TryLoadFileShellItemIconAsync(loadIconSource.Path);
                if (bytes is { Length: > 0 })
                {
                    bytes = CompressBmpPayloadToPngIfPossible(bytes);
                    StoreCachedIconBytes(iconBytesCacheKey, bytes);
                    EvictIconCachesIfNeeded();
                    App.LogVerbose(
                        $"[IconHelper] Recovered explicit index icon through " +
                        $"Shell item path={loadIconSource.Path}");
                }
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
                    bytes = CompressBmpPayloadToPngIfPossible(bytes);
                    StoreCachedIconBytes(iconBytesCacheKey, bytes);
                    EvictIconCachesIfNeeded();
                    App.LogVerbose(
                        $"[IconHelper] Recovered shortcut icon through Shell proxy " +
                        $"path={originalSourcePath}");
                }
            }
        }

        if (bytes is { Length: > 0 })
        {
            bytes = CompressBmpPayloadToPngIfPossible(bytes);
            UpdateDecodedBitmapEstimatedBytes(
                bitmapCacheKey,
                decodePixelWidth,
                bytes.Length);
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

    private static async Task<byte[]?> TryLoadHighResolutionShellItemIconAsync(
        string path,
        bool includeOverlays = false,
        int requestedSize = PreferredShellItemIconSize)
    {
        await s_shellIconLoadSemaphore.WaitAsync();
        try
        {
            return await ShellThumbnailProxy.TryLoadIconAsync(
                path,
                requestedSize: requestedSize > 0
                    ? requestedSize
                    : PreferredShellItemIconSize,
                includeOverlays: includeOverlays);
        }
        finally
        {
            s_shellIconLoadSemaphore.Release();
        }
    }

    /// <summary>
    /// Loads the Shell-item icon of a file or folder and recovers from a canvas
    /// that only holds a small icon frame. A file type whose registered icon
    /// resource no longer exists (an uninstalled app that left its file
    /// association behind) resolves to an icon whose largest frame is 32/48 px;
    /// asking for Jumbo then returns the requested canvas with that artwork
    /// centered, which the fixed file tile renders as a tiny glyph. Re-request
    /// the frame sizes such an icon can fill so it keeps the same framing as
    /// every other tile. Failing that, fall back to the cropped artwork.
    /// </summary>
    private static async Task<byte[]?> TryLoadFileShellItemIconAsync(
        string path,
        int requestedSize = PreferredShellItemIconSize)
    {
        int effectiveSize = requestedSize > 0
            ? requestedSize
            : PreferredShellItemIconSize;
        byte[]? bytes = await TryLoadRawShellItemIconAsync(
            path,
            effectiveSize);
        if (bytes is not { Length: > 0 } ||
            effectiveSize <= 48 ||
            !ShellThumbnailProxy.IsLikelyPaddedIconPayload(bytes))
        {
            return bytes;
        }

        foreach (int frameSize in s_shellItemIconNativeFrameSizes)
        {
            byte[]? nativeFrame = await TryLoadRawShellItemIconAsync(
                path,
                frameSize);
            if (nativeFrame is { Length: > 0 } &&
                !ShellThumbnailProxy.IsLikelyPaddedIconPayload(nativeFrame))
            {
                App.Log(
                    $"[IconHelper] Recovered padded Shell item icon at its " +
                    $"native frame size={frameSize} path={path}");
                return nativeFrame;
            }
        }

        return ShellThumbnailProxy.NormalizeIconPayload(bytes) ?? bytes;
    }

    private static async Task<byte[]?> TryLoadRawShellItemIconAsync(
        string path,
        int requestedSize)
    {
        await s_shellIconLoadSemaphore.WaitAsync();
        try
        {
            return await ShellThumbnailProxy.TryLoadIconPayloadAsync(
                path,
                requestedSize);
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
        bool rejectPaddedShortcutIcon = false) =>
        LoadIconBytes(
            iconSource,
            rejectPaddedShortcutIcon,
            allowGenericPerInstanceFallback: true);

    private static byte[]? LoadIconBytes(
        IconSource iconSource,
        bool rejectPaddedShortcutIcon,
        bool allowGenericPerInstanceFallback)
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

                if (!allowGenericPerInstanceFallback)
                {
                    return null;
                }
            }

            // Shell's Jumbo image list can contain a 32/48 px icon that has already
            // been enlarged to 256 px. Explorer can still render the same executable
            // crisply because it extracts the best resource frame directly. Do that
            // first for executable/icon-backed items (including resolved shortcuts),
            // then retain the image-list path as a compatibility fallback.
            string extension = Path.GetExtension(iconSource.Path);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                int resourceIndex = iconSource.UsesExplicitIconIndex
                    ? iconSource.IconIndex
                    : 0;
                byte[]? executableBytes = LoadIndexedIconBytes(
                    iconSource with { IconIndex = resourceIndex },
                    rejectPaddedShortcutIcon);
                if (executableBytes is not null)
                {
                    return executableBytes;
                }
            }

            bool isExistingDirectory = Directory.Exists(iconSource.Path);
            if (!allowGenericPerInstanceFallback &&
                RequiresPerInstanceShellIcon(
                    iconSource,
                    extension,
                    isExistingDirectory))
            {
                // Never fall back to SHGFI_USEFILEATTRIBUTES for per-instance
                // Shell items (.lnk, .url, 0-byte AppExecutionAlias .exe,
                // PIDL targets, or desktop.ini folders): Windows returns the
                // generic blank white-sheet icon (imageres.dll,-2), which would
                // poison the icon cache and stamp the freshness ledger.
                return null;
            }

            // Get the system icon index, then extract the highest-resolution
            // version available via ShellIconNativeMethods.SHGetImageList (Jumbo 256 → ExtraLarge 48 → Large 32).
            // Deep review R18 (E-1): the index probe used the real path, which
            // binds a PIDL and can invoke a third-party IconHandler in this
            // process (the exact code class the menu/thumbnail proxies exist
            // to keep out). USEFILEATTRIBUTES resolves the index from the
            // registry association alone — no file access, no handler — and
            // the image-list leg below only reads the shared system list.
            // Cost: extensions whose icon is ONLY produced dynamically by a
            // handler show the generic per-type icon in the proxy-fallback
            // window (the same degradation the pinned WIC decoder accepted).
            var shinfo = new ShellIconNativeMethods.SHFILEINFO();
            const uint fileAttributeDirectory = 0x10;
            const uint fileAttributeNormal = 0x80;
            uint fileAttributes = isExistingDirectory
                ? fileAttributeDirectory
                : fileAttributeNormal;
            IntPtr hImg = ShellIconNativeMethods.SHGetFileInfo(
                iconSource.Path,
                fileAttributes,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                ShellIconNativeMethods.SHGFI_SYSICONINDEX |
                ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);

            if (hImg == IntPtr.Zero)
            {
                // Fallback: registry-only direct large icon (same
                // USEFILEATTRIBUTES stance as the index probe).
                return isExistingDirectory
                    ? LoadIconBytesFromShGetFileInfo(
                        iconSource.Path,
                        rejectPaddedShortcutIcon,
                        isExistingDirectory: true)
                    : GetFileTypeIconPngBytes(iconSource.Path);
            }

            int iconIndex = shinfo.iIcon;

            // Try Jumbo (256×256) first — gives crisp icons on high-DPI displays.
            byte[]? bytes = TryGetIconFromImageList(
                ShellIconNativeMethods.SHIL_JUMBO,
                iconIndex,
                rejectPaddedShortcutIcon);
            if (bytes is not null)
            {
                return bytes;
            }

            // Fall back to Extra Large (48×48).
            bytes = TryGetIconFromImageList(
                ShellIconNativeMethods.SHIL_EXTRALARGE,
                iconIndex,
                rejectPaddedShortcutIcon);
            if (bytes is not null)
            {
                return bytes;
            }

            // Final fallback: Large (32×32) via ShellIconNativeMethods.SHGetFileInfo.
            return LoadIconBytesFromShGetFileInfo(
                iconSource.Path,
                rejectPaddedShortcutIcon,
                isExistingDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load icon for {iconSource.Path}: {ex.Message}");
            return null;
        }
    }

    private static bool RequiresPerInstanceShellIcon(
        IconSource iconSource,
        string extension,
        bool isExistingDirectory)
    {
        if (iconSource.UsesShellItemIcon ||
            ShortcutHelper.IsShortcutPath(iconSource.Path))
        {
            return true;
        }

        if (isExistingDirectory)
        {
            try
            {
                return File.Exists(Path.Combine(iconSource.Path, "desktop.ini"));
            }
            catch
            {
                return false;
            }
        }

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".msc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase);
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
            Guid iid = ShellIconNativeMethods.IImageListIid;
            int hr = ShellIconNativeMethods.SHGetImageList(imageListFlags, ref iid, ref imageListPtr);
            if (hr != 0 || imageListPtr == IntPtr.Zero)
            {
                return null;
            }

            int result = ShellIconNativeMethods.ImageListGetIcon(
                imageListPtr,
                iconIndex,
                ShellIconNativeMethods.ILD_TRANSPARENT,
                ref iconHandle);
            if (result != 0 || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            return EncodeIconHandleAsPng(iconHandle, rejectPaddedShortcutIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                ShellIconNativeMethods.DestroyIcon(iconHandle);
            }

            if (imageListPtr != IntPtr.Zero)
            {
                Marshal.Release(imageListPtr);
            }
        }
    }

    private static byte[]? LoadIconBytesFromShGetFileInfo(
        string path,
        bool rejectPaddedShortcutIcon = false,
        bool isExistingDirectory = false)
    {
        // Deep review R18 (E-1): registry-only — the real-path form of this
        // query could invoke a third-party IconHandler in-process. See the
        // SYSICONINDEX probe above for the degradation trade-off.
        const uint fileAttributeDirectory = 0x10;
        const uint fileAttributeNormal = 0x80;
        var shinfo = new ShellIconNativeMethods.SHFILEINFO();
        IntPtr hImg = ShellIconNativeMethods.SHGetFileInfo(
            path,
            isExistingDirectory ? fileAttributeDirectory : fileAttributeNormal,
            ref shinfo,
            (uint)Marshal.SizeOf(shinfo),
            ShellIconNativeMethods.SHGFI_ICON |
            ShellIconNativeMethods.SHGFI_LARGEICON |
            ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);

        if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return EncodeIconHandleAsPng(shinfo.hIcon, rejectPaddedShortcutIcon: false);
        }
        finally
        {
            ShellIconNativeMethods.DestroyIcon(shinfo.hIcon);
        }
    }

    /// <summary>
    /// PNG bytes for a file TYPE's icon, resolved from the extension
    /// association alone — the file does not have to exist. Used by the
    /// New submenu, whose entries have no backing file yet.
    /// </summary>
    internal static byte[]? GetFileTypeIconPngBytes(
        string fileNameWithExtension)
    {
        const uint fileAttributeNormal = 0x80;
        var shinfo = new ShellIconNativeMethods.SHFILEINFO();
        IntPtr hImg = ShellIconNativeMethods.SHGetFileInfo(
            fileNameWithExtension,
            fileAttributeNormal,
            ref shinfo,
            (uint)Marshal.SizeOf(shinfo),
            ShellIconNativeMethods.SHGFI_ICON |
            ShellIconNativeMethods.SHGFI_LARGEICON |
            ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES);

        if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return EncodeIconHandleAsPng(shinfo.hIcon, rejectPaddedShortcutIcon: false);
        }
        finally
        {
            ShellIconNativeMethods.DestroyIcon(shinfo.hIcon);
        }
    }

    private static byte[]? LoadIndexedIconBytes(
        IconSource iconSource,
        bool rejectPaddedShortcutIcon = false)
    {
        // Try ShellIconNativeMethods.SHDefExtractIcon first — it can extract 256×256 icons from exe/dll/ico resources.
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

        // Final fallback: ShellIconNativeMethods.ExtractIconEx (32×32 large / 16×16 small)
        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        try
        {
            uint count = ShellIconNativeMethods.ExtractIconEx(
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

            return EncodeIconHandleAsPng(iconHandle, rejectPaddedShortcutIcon);
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero)
            {
                ShellIconNativeMethods.DestroyIcon(largeIcons[0]);
            }

            if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0])
            {
                ShellIconNativeMethods.DestroyIcon(smallIcons[0]);
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
            int hr = ShellIconNativeMethods.SHDefExtractIcon(filePath, iconIndex, 0, out hLarge, out hSmall, nIconSize);
            if (hr != 0 || hLarge == IntPtr.Zero)
            {
                return null;
            }

            return EncodeIconHandleAsPng(hLarge, rejectPaddedShortcutIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hLarge != IntPtr.Zero)
            {
                ShellIconNativeMethods.DestroyIcon(hLarge);
            }

            if (hSmall != IntPtr.Zero && hSmall != hLarge)
            {
                ShellIconNativeMethods.DestroyIcon(hSmall);
            }
        }
    }

    /// <summary>
    /// Menu-sized indexed icon as PNG bytes, extracted in-process
    /// (SHDefExtractIcon); safe to call from background threads. Used for
    /// Open With app entries, where the extraction starts when the menu is
    /// built so the icons are ready by the time the submenu opens on hover.
    /// </summary>
    internal static byte[]? GetIndexedIconPngBytes(
        string filePath,
        int iconIndex,
        int size) => TryExtractHighResIndexedIcon(filePath, iconIndex, size);

    // GetShellItemIconPngBytes removed (deep review R18, E-1 audit): zero
    // callers in src+tests — and its PIDL path was the one remaining form
    // that could bind third-party icon handlers in-process.
    internal static Task<BitmapImage?> CreateBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        byte[]? bytes,
        int decodePixelWidth = 0)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Task.FromResult<BitmapImage?>(null);
        }

        // Transcode uncompressed 32bpp BMP/BMPv5 payloads to compact PNG off the
        // UI thread before handing the stream to WinUI: CBitmapImage copies and
        // retains the encoded source stream alongside the decoded surface.
        bytes = CompressBmpPayloadToPngIfPossible(bytes);

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
            bmp.DecodePixelType = DecodePixelType.Physical;
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

    private static byte[]? EncodeIconHandleAsPng(
        IntPtr iconHandle,
        bool rejectPaddedShortcutIcon)
    {
        if (!IconPngEncoder.TryExtractIconBitmap(
                iconHandle,
                out IconPngEncoder.IconPixelBuffer bitmap))
        {
            return null;
        }

        using (bitmap)
        {
            return EncodeIconBitmapAsPng(bitmap, rejectPaddedShortcutIcon);
        }
    }

    private static byte[]? EncodeVisibleBitmapAsPng(IconPngEncoder.IconPixelBuffer bitmap)
    {
        if (!IconPngEncoder.HasVisiblePixels(bitmap))
        {
            return null;
        }

        return IconPngEncoder.EncodeRegionAsPng(
            bitmap,
            0,
            0,
            bitmap.Width,
            bitmap.Height);
    }

    private static byte[]? EncodeVisibleBitmapAsPng(
        IconPngEncoder.IconPixelBuffer bitmap,
        bool rejectPaddedShortcutIcon)
    {
        if (!IconPngEncoder.TryGetVisibleBounds(
                bitmap,
                out int minX,
                out int minY,
                out int visibleWidth,
                out int visibleHeight))
        {
            return null;
        }

        if (rejectPaddedShortcutIcon &&
            IconBitmapQuality.IsLikelyPadded(
                bitmap.Width,
                bitmap.Height,
                visibleWidth,
                visibleHeight))
        {
            return IconPngEncoder.EncodeRegionAsPng(
                bitmap,
                minX,
                minY,
                visibleWidth,
                visibleHeight);
        }

        return EncodeVisibleBitmapAsPng(bitmap);
    }

    private static byte[]? EncodeIconBitmapAsPng(
        IconPngEncoder.IconPixelBuffer bitmap,
        bool rejectPaddedShortcutIcon)
    {
        return rejectPaddedShortcutIcon
            ? EncodeVisibleBitmapAsPng(bitmap, rejectPaddedShortcutIcon: true)
            : EncodeVisibleBitmapAsPng(bitmap);
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

        if (ShortcutHelper.IsShellLinkPath(path) &&
            ShellLinkVtableInterop.TryReadExpandedShortcutPaths(
                path,
                out string? expandedIconLocation,
                out int expandedIconIndex,
                out string? longTargetPath))
        {
            if (!string.IsNullOrWhiteSpace(expandedIconLocation))
            {
                var (expandedIconFile, inlineIndex) = SplitIconLocation(expandedIconLocation);
                string? normalizedExpandedIcon = NormalizeIconLocation(expandedIconFile);
                int effectiveIconIndex = inlineIndex ?? expandedIconIndex;
                if (!string.IsNullOrWhiteSpace(normalizedExpandedIcon) &&
                    TryResolveRelativeIconLocation(
                        path,
                        normalizedExpandedIcon,
                        out string resolvedExpandedRelative))
                {
                    normalizedExpandedIcon = resolvedExpandedRelative;
                }

                if (!string.IsNullOrWhiteSpace(normalizedExpandedIcon) &&
                    File.Exists(normalizedExpandedIcon))
                {
                    return new IconSource(
                        normalizedExpandedIcon,
                        effectiveIconIndex,
                        UsesExplicitIconIndex: true);
                }
            }

            if (!string.IsNullOrWhiteSpace(longTargetPath) &&
                (File.Exists(longTargetPath) || Directory.Exists(longTargetPath)))
            {
                return new IconSource(longTargetPath);
            }
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

    internal static long EstimateDecodedBitmapBytes(
        int decodePixelWidth,
        int encodedPayloadBytes = 0)
    {
        int width = decodePixelWidth <= 0 ? 256 : decodePixelWidth;
        long decodedSurfaceBytes = (long)width * width * 4;
        long retainedEncodedBytes = encodedPayloadBytes > 0
            ? encodedPayloadBytes
            : Math.Max(4096L, decodedSurfaceBytes / 8);
        return decodedSurfaceBytes + retainedEncodedBytes;
    }

    private static void UpdateDecodedBitmapEstimatedBytes(
        string cacheKey,
        int decodePixelWidth,
        int encodedPayloadBytes)
    {
        long updatedEstimate = EstimateDecodedBitmapBytes(
            decodePixelWidth,
            encodedPayloadBytes);
        lock (s_bitmapCacheLock)
        {
            if (!s_bitmapImageCache.ContainsKey(cacheKey) ||
                !s_bitmapEstimatedBytes.TryGetValue(cacheKey, out long previousEstimate))
            {
                return;
            }

            s_bitmapEstimatedBytes[cacheKey] = updatedEstimate;
            s_totalBitmapEstimatedBytes += updatedEstimate - previousEstimate;
            UpdateDecodedBitmapDiagnostics();
        }
    }

    internal static void IndexIconBytesScopeKey(
        string cacheScope,
        string iconBytesCacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheScope) ||
            string.IsNullOrWhiteSpace(iconBytesCacheKey))
        {
            return;
        }

        string normalizedScope = cacheScope.Trim();
        lock (s_bitmapCacheLock)
        {
            if (!s_iconByteKeysByScope.TryGetValue(normalizedScope, out HashSet<string>? keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_iconByteKeysByScope[normalizedScope] = keys;
            }

            keys.Add(iconBytesCacheKey);

            if (!s_scopesByIconByteKey.TryGetValue(iconBytesCacheKey, out HashSet<string>? scopes))
            {
                scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_scopesByIconByteKey[iconBytesCacheKey] = scopes;
            }

            scopes.Add(normalizedScope);
        }
    }

    private static void UnindexIconByteKeyAllScopes(string iconBytesCacheKey)
    {
        lock (s_bitmapCacheLock)
        {
            if (!s_scopesByIconByteKey.Remove(iconBytesCacheKey, out HashSet<string>? scopes))
            {
                return;
            }

            foreach (string scope in scopes)
            {
                if (s_iconByteKeysByScope.TryGetValue(scope, out HashSet<string>? keys))
                {
                    keys.Remove(iconBytesCacheKey);
                    if (keys.Count == 0)
                    {
                        s_iconByteKeysByScope.Remove(scope);
                    }
                }
            }
        }
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
                IndexBitmapScopeKey(cacheKey);
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

                RemoveDecodedBitmapCore(oldestNode.Value);
            }

            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void RemoveDecodedBitmap(string cacheKey)
    {
        lock (s_bitmapCacheLock)
        {
            RemoveDecodedBitmapCore(cacheKey);
            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void RemoveDecodedBitmapCore(string cacheKey)
    {
        s_bitmapImageCache.TryRemove(cacheKey, out _);
        UnindexBitmapScopeKey(cacheKey);
        if (s_bitmapLruNodes.Remove(cacheKey, out var node))
        {
            s_bitmapLru.Remove(node);
        }

        s_bitmapLastAccess.Remove(cacheKey);
        if (s_bitmapEstimatedBytes.Remove(cacheKey, out long removedBytes))
        {
            s_totalBitmapEstimatedBytes -= removedBytes;
        }
    }

    private static void IndexBitmapScopeKey(string cacheKey)
    {
        if (!TryExtractBitmapScope(cacheKey, out string scope))
        {
            return;
        }

        if (!s_bitmapKeysByScope.TryGetValue(scope, out HashSet<string>? keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            s_bitmapKeysByScope[scope] = keys;
        }

        keys.Add(cacheKey);
    }

    private static void UnindexBitmapScopeKey(string cacheKey)
    {
        if (!TryExtractBitmapScope(cacheKey, out string scope) ||
            !s_bitmapKeysByScope.TryGetValue(scope, out HashSet<string>? keys))
        {
            return;
        }

        keys.Remove(cacheKey);
        if (keys.Count == 0)
        {
            s_bitmapKeysByScope.Remove(scope);
        }
    }

    private static bool TryExtractBitmapScope(string cacheKey, out string scope)
    {
        int pipeIndex = cacheKey.IndexOf('|');
        if (pipeIndex > 0)
        {
            scope = cacheKey[..pipeIndex];
            return true;
        }

        scope = string.Empty;
        return false;
    }

    private static void IndexThumbnailScopeKey(string cacheKey)
    {
        if (!TryExtractThumbnailScope(cacheKey, out string scope))
        {
            return;
        }

        if (!s_thumbKeysByScope.TryGetValue(scope, out HashSet<string>? keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            s_thumbKeysByScope[scope] = keys;
        }

        keys.Add(cacheKey);
    }

    private static void UnindexThumbnailScopeKey(string cacheKey)
    {
        if (!TryExtractThumbnailScope(cacheKey, out string scope) ||
            !s_thumbKeysByScope.TryGetValue(scope, out HashSet<string>? keys))
        {
            return;
        }

        keys.Remove(cacheKey);
        if (keys.Count == 0)
        {
            s_thumbKeysByScope.Remove(scope);
        }
    }

    private static bool TryExtractThumbnailScope(string cacheKey, out string scope)
    {
        int prefixLength = 0;
        if (cacheKey.StartsWith("thumb:", StringComparison.OrdinalIgnoreCase))
        {
            prefixLength = 6;
        }
        else if (cacheKey.StartsWith("shell-thumb:", StringComparison.OrdinalIgnoreCase))
        {
            prefixLength = 12;
        }

        if (prefixLength > 0)
        {
            int pipeIndex = cacheKey.IndexOf('|', prefixLength);
            if (pipeIndex > prefixLength)
            {
                scope = cacheKey[prefixLength..pipeIndex];
                return true;
            }
        }

        scope = string.Empty;
        return false;
    }

    internal static void IndexThumbnailTaskForTest(
        string cacheKey,
        Task<BitmapImage?> task,
        long estimatedBytes = 1024)
    {
        lock (s_thumbLock)
        {
            RemoveThumbnailCacheEntry(cacheKey);
            s_thumbCache[cacheKey] = task;
            IndexThumbnailScopeKey(cacheKey);
            s_thumbEstimatedBytes[cacheKey] = estimatedBytes;
            s_totalThumbnailEstimatedBytes += estimatedBytes;
            s_thumbLru.AddFirst(cacheKey);
            s_thumbLastAccess[cacheKey] = Stopwatch.GetTimestamp();
        }
    }

    internal static bool HasThumbnailCacheEntryForTest(string cacheKey)
    {
        lock (s_thumbLock)
        {
            return s_thumbCache.ContainsKey(cacheKey);
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
        if (!coldOnly && targetCount <= 0 && targetBytes <= 0)
        {
            s_iconBytesCache.Clear();
            s_iconBytesLastAccess.Clear();
            lock (s_bitmapCacheLock)
            {
                s_iconByteKeysByScope.Clear();
                s_scopesByIconByteKey.Clear();
            }

            PerformanceLogger.IconCacheCount = 0;
            return;
        }

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

    internal static void StoreCachedIconBytes(string cacheKey, byte[] bytes)
    {
        bytes = CompressBmpPayloadToPngIfPossible(bytes);

        // If a non-BMP or uncompressible payload still reaches the Large Object
        // Heap (>= 85,000 bytes), skip pinning per-path LOH byte arrays in
        // s_iconBytesCache while keeping compact shared "ext:" icons.
        if (bytes.Length >= MaxNonSharedIconBytePayloadLength &&
            !cacheKey.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        s_iconBytesCache[cacheKey] = bytes;
        s_iconBytesLastAccess[cacheKey] = Stopwatch.GetTimestamp();
    }

    internal static byte[]? GetCachedIconBytesForTest(string cacheKey) =>
        s_iconBytesCache.TryGetValue(cacheKey, out byte[]? bytes) ? bytes : null;

    internal static bool HasCachedIconBytesForTest(string cacheKey) =>
        s_iconBytesCache.ContainsKey(cacheKey);

    internal static void RemoveCachedIconBytesForTest(string cacheKey) =>
        RemoveCachedIconBytes(cacheKey, out _);

    private static bool RemoveCachedIconBytes(
        string cacheKey,
        out byte[]? bytes)
    {
        bool removed = s_iconBytesCache.TryRemove(cacheKey, out bytes);
        s_iconBytesLastAccess.TryRemove(cacheKey, out _);
        UnindexIconByteKeyAllScopes(cacheKey);
        return removed;
    }

    private static readonly uint[] s_pngCrc32Table = CreatePngCrc32Table();

    private static uint[] CreatePngCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>
    /// Transcodes raw 32-bit BGRA BMP / BMPv5 payloads (such as the 262 KB
    /// 256x256 payloads returned by <see cref="ShellThumbnailProxy"/>) into
    /// compact RGBA PNGs without allocating Large Object Heap buffers.
    /// Already-encoded PNG/JPEG/ICO payloads pass through untouched.
    /// </summary>
    internal static byte[] CompressBmpPayloadToPngIfPossible(byte[] bytes)
    {
        if (bytes.Length < 54 ||
            bytes[0] != (byte)'B' ||
            bytes[1] != (byte)'M')
        {
            return bytes;
        }

        ReadOnlySpan<byte> span = bytes;
        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(10, 4));
        uint dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(14, 4));
        if (dibHeaderSize < 40 || 14u + dibHeaderSize > pixelOffset)
        {
            return bytes;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(26, 2));
        ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(30, 4));
        if (planes != 1 ||
            bitCount != 32 ||
            width is <= 0 or > 1024 ||
            rawHeight == int.MinValue)
        {
            return bytes;
        }

        if (compression == 3)
        {
            if (dibHeaderSize < 52 || span.Length < 14 + 52)
            {
                return bytes;
            }

            uint redMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(54, 4));
            uint greenMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(58, 4));
            uint blueMask = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(62, 4));
            uint alphaMask = dibHeaderSize >= 56 && span.Length >= 14 + 56
                ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(66, 4))
                : 0u;
            if (redMask != 0x00FF0000u ||
                greenMask != 0x0000FF00u ||
                blueMask != 0x000000FFu ||
                (alphaMask != 0u && alphaMask != 0xFF000000u))
            {
                return bytes;
            }
        }
        else if (compression != 0)
        {
            return bytes;
        }

        int height = Math.Abs(rawHeight);
        if (height is <= 0 or > 1024)
        {
            return bytes;
        }

        int rowBytes = width * 4;
        long totalPixelBytes = (long)rowBytes * height;
        if (pixelOffset > (uint)bytes.Length ||
            bytes.Length - (int)pixelOffset < totalPixelBytes)
        {
            return bytes;
        }

        bool topDown = rawHeight < 0;
        ReadOnlySpan<byte> pixels = span.Slice((int)pixelOffset, (int)totalPixelBytes);
        bool hasAnyAlpha = false;
        bool hasAnyColor = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0)
            {
                hasAnyAlpha = true;
                break;
            }

            if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0)
            {
                hasAnyColor = true;
            }
        }

        bool forceOpaqueAlpha = !hasAnyAlpha && hasAnyColor;
        int scanlineLength = 1 + rowBytes;
        byte[] rentedScanline = ArrayPool<byte>.Shared.Rent(scanlineLength);
        try
        {
            using var compressedStream = new MemoryStream(
                Math.Min(16384, Math.Max(512, bytes.Length / 8)));
            using (var zlib = new ZLibStream(
                compressedStream,
                CompressionLevel.Fastest,
                leaveOpen: true))
            {
                rentedScanline[0] = 0; // Filter 0 (None)
                for (int y = 0; y < height; y++)
                {
                    int srcRow = topDown ? y : (height - 1 - y);
                    ReadOnlySpan<byte> src = pixels.Slice(srcRow * rowBytes, rowBytes);
                    Span<byte> dst = rentedScanline.AsSpan(1, rowBytes);
                    for (int x = 0; x < rowBytes; x += 4)
                    {
                        dst[x] = src[x + 2];     // R
                        dst[x + 1] = src[x + 1]; // G
                        dst[x + 2] = src[x];     // B
                        dst[x + 3] = forceOpaqueAlpha ? (byte)255 : src[x + 3];
                    }

                    zlib.Write(rentedScanline, 0, scanlineLength);
                }
            }

            if (!compressedStream.TryGetBuffer(out ArraySegment<byte> idatPayload))
            {
                return bytes;
            }

            // PNG = 8-byte signature + IHDR (25B) + IDAT (12B + payload) + IEND (12B)
            int pngLength = 8 + 25 + 12 + idatPayload.Count + 12;
            if (pngLength >= bytes.Length || pngLength >= MaxNonSharedIconBytePayloadLength)
            {
                // Even if an unusually complex 256x256 icon exceeds 85 KB, still keep
                // PNG whenever it shrinks the BMP payload meaningfully.
                if (pngLength >= bytes.Length)
                {
                    return bytes;
                }
            }

            byte[] png = new byte[pngLength];
            Span<byte> outSpan = png;
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            signature.CopyTo(outSpan);
            int offset = 8;

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
            ihdr[8] = 8; // 8 bits per channel
            ihdr[9] = 6; // RGBA
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            offset += WritePngChunk(outSpan[offset..], "IHDR"u8, ihdr);
            offset += WritePngChunk(
                outSpan[offset..],
                "IDAT"u8,
                idatPayload.AsSpan());
            WritePngChunk(outSpan[offset..], "IEND"u8, ReadOnlySpan<byte>.Empty);
            return png;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedScanline);
        }
    }

    private static int WritePngChunk(
        Span<byte> destination,
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), data.Length);
        chunkType.CopyTo(destination.Slice(4, 4));
        if (!data.IsEmpty)
        {
            data.CopyTo(destination.Slice(8, data.Length));
        }

        uint crc = ComputePngCrc32(destination.Slice(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8 + data.Length, 4), crc);
        return 12 + data.Length;
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        uint[] table = s_pngCrc32Table;
        for (int i = 0; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
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
