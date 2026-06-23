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
    private static readonly ConcurrentDictionary<string, byte[]?> s_iconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_bitmapImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_iconLoadSemaphore = new(4, 4);

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
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(string path, bool hideShortcutArrowOverlay = false)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.GetIcon", $"path={path}");
        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
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

    public static void ClearIconCache(string path, bool hideShortcutArrowOverlay = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IconSource iconSource = ResolveIconSource(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return;
        }

        string cacheKey = BuildCacheKey(path, iconSource);
        s_bitmapImageCache.TryRemove(cacheKey, out _);
        s_iconBytesCache.TryRemove(cacheKey, out _);
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
                    }
                }
            }
            finally
            {
                s_iconLoadSemaphore.Release();
            }
        }

        var image = await CreateBitmapImageAsync(dispatcher, bytes);
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
        byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Task.FromResult<BitmapImage?>(null);
        }

        if (dispatcher.HasThreadAccess)
        {
            return CreateBitmapImageOnUiThreadAsync(bytes);
        }

        var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await CreateBitmapImageOnUiThreadAsync(bytes));
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

    private static async Task<BitmapImage?> CreateBitmapImageOnUiThreadAsync(byte[] bytes)
    {
        var bmp = new BitmapImage();
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
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
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
            sourcePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string sourceVersion = sourcePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
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
}
