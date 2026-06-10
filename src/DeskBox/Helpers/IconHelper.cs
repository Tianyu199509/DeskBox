using System.Drawing;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Helpers;

/// <summary>
/// Extracts native Windows file and folder icons using the Win32 Shell API.
/// </summary>
public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, byte[]?> s_iconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_bitmapImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_resolvedIconPathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_iconLoadSemaphore = new(4, 4);

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

    /// <summary>
    /// Asynchronously retrieve the native Windows shell icon for the given path.
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(string path, bool hideShortcutArrowOverlay = false)
    {
        var dispatcher = App.MainWindowDispatcherQueue;
        if (dispatcher == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string resolvedPath = ResolveIconPathCached(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        string cacheKey = BuildCacheKey(path, resolvedPath);
        return await s_bitmapImageCache.GetOrAdd(
            cacheKey,
            _ => LoadBitmapImageAsync(dispatcher, resolvedPath));
    }

    private static async Task<BitmapImage?> LoadBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string resolvedPath)
    {
        if (!s_iconBytesCache.TryGetValue(resolvedPath, out var bytes))
        {
            await s_iconLoadSemaphore.WaitAsync();
            try
            {
                if (!s_iconBytesCache.TryGetValue(resolvedPath, out bytes))
                {
                    bytes = await Task.Run(() => LoadIconBytes(resolvedPath));
                    s_iconBytesCache[resolvedPath] = bytes;
                }
            }
            finally
            {
                s_iconLoadSemaphore.Release();
            }
        }

        return await CreateBitmapImageAsync(dispatcher, bytes);
    }

    private static byte[]? LoadIconBytes(string resolvedPath)
    {
        try
        {
            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                resolvedPath,
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
            App.Log($"[IconHelper] Failed to load icon for {resolvedPath}: {ex.Message}");
            return null;
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

    private static string ResolveIconPath(string path, bool hideShortcutArrowOverlay)
    {
        if (!hideShortcutArrowOverlay || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var shortcut = ShortcutHelper.Resolve(path);
        if (!string.IsNullOrWhiteSpace(shortcut?.TargetPath) &&
            (File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath)))
        {
            return shortcut.TargetPath;
        }

        return path;
    }

    private static string ResolveIconPathCached(string path, bool hideShortcutArrowOverlay)
    {
        if (!hideShortcutArrowOverlay || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string cacheKey = $"shortcut:{path}";
        return s_resolvedIconPathCache.GetOrAdd(cacheKey, _ => ResolveIconPath(path, hideShortcutArrowOverlay));
    }

    private static string BuildCacheKey(string sourcePath, string resolvedPath)
    {
        if (Directory.Exists(resolvedPath))
        {
            return $"dir:{resolvedPath}";
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
            return $"path:{resolvedPath}";
        }

        return $"ext:{extension}";
    }
}
