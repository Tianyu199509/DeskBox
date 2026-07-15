using System.Drawing;
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
    private const int MaxIconCacheEntries = 200;
    private const int MaxThumbnailCacheEntries = 128;

    // Icon bytes cache: path → PNG bytes (for shell icons, not image thumbnails)
    private static readonly ConcurrentDictionary<string, byte[]?> s_iconBytesCache = new(StringComparer.OrdinalIgnoreCase);

    // Bitmap cache for shell icons (not image thumbnails)
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_bitmapImageCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Image thumbnail LRU cache (separate from icon cache) ──────
    // Uses a linked list + dictionary for simple LRU eviction.
    private static readonly object s_thumbLock = new();
    private static readonly LinkedList<string> s_thumbLru = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> s_thumbCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim s_iconLoadSemaphore = new(4, 4);
    private static readonly SemaphoreSlim s_thumbLoadSemaphore = new(2, 2);

    private sealed record IconSource(string Path, int IconIndex = 0, bool UsesExplicitIconIndex = false);

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
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    /// <summary>
    /// Asynchronously retrieve the native Windows shell icon for the given path.
    /// For image files, returns an actual thumbnail preview instead of the generic icon.
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.GetIcon", $"path={path}");
        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!showImageFilesAsIcons && IsImageFile(path))
        {
            return await LoadImageThumbnailAsync(dispatcher, path);
        }

        IconSource iconSource = ResolveIconSource(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return null;
        }

        string cacheKey = BuildCacheKey(path, iconSource);
        return await s_bitmapImageCache.GetOrAdd(
            cacheKey,
            _ => LoadBitmapImageAsync(dispatcher, iconSource, cacheKey));
    }

    private static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tiff" or ".tif" or ".heic" or ".heif";
    }

    // ── Image thumbnail loading with LRU cache ───────────────────

    private static async Task<BitmapImage?> LoadImageThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path)
    {
        string cacheKey = $"thumb:{path}:{GetFileIconVersion(path)}";

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
                cachedTask = cached;
            }
        }

        if (cachedTask is not null)
        {
            return await cachedTask;
        }

        // Remove stale entry for the same path but different version
        RemoveStaleThumbnailEntries(path, cacheKey);

        Task<BitmapImage?> task;
        lock (s_thumbLock)
        {
            if (!s_thumbCache.TryGetValue(cacheKey, out task!))
            {
                task = CreateImageThumbnailAsync(dispatcher, path, cacheKey);
                s_thumbCache[cacheKey] = task;
                s_thumbLru.AddFirst(cacheKey);
                EvictThumbnailCacheIfNeeded();
            }
            else
            {
                RemoveThumbnailLruKey(cacheKey);
                s_thumbLru.AddFirst(cacheKey);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }

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
    private static void RemoveStaleThumbnailEntries(string path, string currentKey)
    {
        string pathPrefix = $"thumb:{path}:";

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
                s_thumbCache.Remove(staleKey);
                RemoveThumbnailLruKey(staleKey);
            }
        }
    }

    /// <summary>
    /// Evicts oldest thumbnail cache entries when the cache exceeds the
    /// maximum size.  Must be called under s_thumbLock.
    /// </summary>
    private static void EvictThumbnailCacheIfNeeded()
    {
        while (s_thumbCache.Count > MaxThumbnailCacheEntries && s_thumbLru.Count > 0)
        {
            var oldestKey = s_thumbLru.Last!.Value;
            s_thumbLru.RemoveLast();
            s_thumbCache.Remove(oldestKey);
        }
    }

    private static async Task<BitmapImage?> CreateImageThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        string cacheKey)
    {
        await s_thumbLoadSemaphore.WaitAsync();
        try
        {
            // Try Windows native thumbnail first — leverages the system
            // thumbnail cache and avoids reading the full image into memory.
            var image = await TryLoadNativeThumbnailAsync(dispatcher, path);
            if (image is not null)
            {
                return image;
            }

            // Fallback: read file bytes and decode at 80px
            byte[] bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth: 80);

            return image;
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load image thumbnail for {path}: {ex.Message}");
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
                s_thumbCache.Remove(cacheKey);
                RemoveThumbnailLruKey(cacheKey);
                PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            }
        }
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
        string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await storageFile.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.PicturesView,
                96,
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            if (dispatcher.HasThreadAccess)
            {
                return await CreateBitmapFromStreamOnUiThread(thumbnail);
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    tcs.SetResult(await CreateBitmapFromStreamOnUiThread(thumbnail));
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
        Windows.Storage.Streams.IRandomAccessStream stream)
    {
        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = 96;
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    public static void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsImageFile(path))
        {
            // Remove all thumbnail entries for this path (any version)
            string pathPrefix = $"thumb:{path}:";
            lock (s_thumbLock)
            {
                var keysToRemove = s_thumbCache.Keys
                    .Where(k => k.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    s_thumbCache.Remove(key);
                    RemoveThumbnailLruKey(key);
                }
                PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            }

            if (!showImageFilesAsIcons)
            {
                return;
            }
        }

        IconSource iconSource = ResolveIconSource(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return;
        }

        string cacheKey = BuildCacheKey(path, iconSource);
        s_bitmapImageCache.TryRemove(cacheKey, out _);
        s_iconBytesCache.TryRemove(cacheKey, out _);
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
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
            PerformanceLogger.ThumbnailCacheCount = 0;
        }
    }

    private static async Task<BitmapImage?> LoadBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        IconSource iconSource,
        string iconBytesCacheKey)
    {
        if (!s_iconBytesCache.TryGetValue(iconBytesCacheKey, out var bytes))
        {
            await s_iconLoadSemaphore.WaitAsync();
            try
            {
                if (!s_iconBytesCache.TryGetValue(iconBytesCacheKey, out bytes))
                {
                    bytes = await Task.Run(() => LoadIconBytes(iconSource));
                    if (bytes is { Length: > 0 })
                    {
                        s_iconBytesCache[iconBytesCacheKey] = bytes;
                        EvictIconCachesIfNeeded();
                    }
                }
            }
            finally
            {
                s_iconLoadSemaphore.Release();
            }
        }

        var image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth: 48);
        if (image is null)
        {
            s_bitmapImageCache.TryRemove(iconBytesCacheKey, out _);
            s_iconBytesCache.TryRemove(iconBytesCacheKey, out _);
        }

        return image;
    }

    private static byte[]? LoadIconBytes(IconSource iconSource)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.LoadIconBytes", $"path={iconSource.Path}");
        try
        {
            if (iconSource.UsesExplicitIconIndex)
            {
                var indexedBytes = LoadIndexedIconBytes(iconSource);
                if (indexedBytes is not null)
                {
                    return indexedBytes;
                }
            }

            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                iconSource.Path,
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
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                DestroyIcon(shinfo.hIcon);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load icon for {iconSource.Path}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? LoadIndexedIconBytes(IconSource iconSource)
    {
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
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
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

    private static IconSource ResolveIconSource(string path, bool hideShortcutArrowOverlay)
    {
        if (!ShortcutHelper.IsShortcutPath(path))
        {
            return new IconSource(path);
        }

        var shortcut = ShortcutHelper.Resolve(path);
        if (!hideShortcutArrowOverlay)
        {
            return new IconSource(path);
        }

        if (shortcut is null)
        {
            return new IconSource(path);
        }

        string? iconLocation = NormalizeIconLocation(shortcut.IconLocation);
        if (!string.IsNullOrWhiteSpace(iconLocation) &&
            File.Exists(iconLocation))
        {
            return new IconSource(iconLocation, shortcut.IconIndex, UsesExplicitIconIndex: true);
        }

        if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) &&
            (File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath)))
        {
            return new IconSource(shortcut.TargetPath);
        }

        return new IconSource(path);
    }

    private static string? NormalizeIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(iconLocation.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }

    private static string BuildCacheKey(string sourcePath, IconSource iconSource)
    {
        string resolvedPath = iconSource.Path;
        if (Directory.Exists(resolvedPath))
        {
            return $"dir:{resolvedPath}:{GetDirectoryIconVersion(resolvedPath)}";
        }

        string extension = Path.GetExtension(resolvedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return $"path:{resolvedPath}";
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ShortcutHelper.IsShortcutPath(sourcePath))
        {
            string sourceVersion = ShortcutHelper.IsShortcutPath(sourcePath)
                ? GetFileIconVersion(sourcePath)
                : "source";
            return $"path:{resolvedPath}:{iconSource.IconIndex}:{iconSource.UsesExplicitIconIndex}:{GetFileIconVersion(resolvedPath)}:{sourceVersion}";
        }

        return $"ext:{extension}";
    }

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
            return File.Exists(filePath)
                ? File.GetLastWriteTimeUtc(filePath).Ticks.ToString("x")
                : "missing";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void EvictIconCachesIfNeeded()
    {
        if (s_iconBytesCache.Count > MaxIconCacheEntries)
        {
            var keysToRemove = s_iconBytesCache.Keys
                .Take(s_iconBytesCache.Count - MaxIconCacheEntries / 2)
                .ToList();
            foreach (var key in keysToRemove)
            {
                s_iconBytesCache.TryRemove(key, out _);
                s_bitmapImageCache.TryRemove(key, out _);
            }
        }

        // Update diagnostics
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
    }
}
