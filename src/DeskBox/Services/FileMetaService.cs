using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Services;

/// <summary>
/// Lazily enriches file/folder search results with shell metadata:
/// the real system icon (cached per extension — no per-file disk access),
/// file size, and creation time.
///
/// Icons use SHGetFileInfo with SHGFI_USEFILEATTRIBUTES, which resolves the icon
/// purely from the file name/attributes, so stale index entries (deleted files)
/// still render a correct icon without touching the disk.
/// </summary>
public sealed class FileMetaService
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private const string FolderCacheKey = "<folder>";
    private const string NoExtensionCacheKey = "<noext>";

    // Extension → icon task. A single icon instance is shared by every result with
    // the same extension, so a list of 200 .pdf files costs exactly one extraction.
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _iconCache = new(StringComparer.OrdinalIgnoreCase);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Enriches file/folder results with icon, size, and creation time.
    /// Non-file items and entries without a path are skipped. Safe to call from any
    /// thread; the BitmapImage creation is marshaled to the UI thread internally.
    /// </summary>
    public async Task EnrichAsync(
        IReadOnlyList<SearchResultItem> items,
        CancellationToken token = default,
        bool hideShortcutArrowOverlay = false)
    {
        var targets = items
            .Where(i => (i.Kind == SearchResultKind.File || i.Kind == SearchResultKind.Folder)
                        && !string.IsNullOrWhiteSpace(i.DetailPath))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        // File stats involve disk I/O — keep them off the UI thread.
        try
        {
            await Task.Run(() =>
            {
                foreach (var item in targets)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    FillStats(item);
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Icons: one cached extraction per extension/folder, shared across rows.
        var iconTasks = new List<Task>(targets.Count);
        foreach (var item in targets)
        {
            iconTasks.Add(ResolveIconAsync(item, token, hideShortcutArrowOverlay));
        }

        try
        {
            await Task.WhenAll(iconTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Partial enrichment is fine; rows keep their glyph fallback.
        }
    }

    private async Task ResolveIconAsync(
        SearchResultItem item,
        CancellationToken token,
        bool hideShortcutArrowOverlay)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            item.Icon = await GetIconAsync(item, hideShortcutArrowOverlay).ConfigureAwait(false);
        }
        finally
        {
            item.IconResolved = true;
        }
    }

    private async Task<ImageSource?> GetIconAsync(
        SearchResultItem item,
        bool hideShortcutArrowOverlay)
    {
        // Shortcuts keep their resolved shell icon. Image files request a thumbnail
        // instead of the icon of the user's default image viewer.
        string extension = Path.GetExtension(item.Title ?? item.DetailPath ?? "").ToLowerInvariant();
        if (extension == ".lnk")
        {
            return await IconHelper.GetIconAsync(
                item.DetailPath!,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons: true);
        }

        if (IconHelper.IsImageFile(item.DetailPath!))
        {
            return await IconHelper.GetIconAsync(
                item.DetailPath!,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons: false);
        }

        // Fallback: use extension-cached SHGetFileInfo path.
        string key;
        if (item.Kind == SearchResultKind.Folder)
        {
            key = FolderCacheKey;
        }
        else
        {
            key = string.IsNullOrEmpty(extension) ? NoExtensionCacheKey : extension;
        }

        return await _iconCache.GetOrAdd(key, k => LoadIconAsync(k));
    }

    private async Task<ImageSource?> LoadIconAsync(string cacheKey)
    {
        byte[]? bytes = await Task.Run(() => LoadIconBytes(cacheKey)).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher is null)
        {
            return null;
        }

        if (dispatcher.HasThreadAccess)
        {
            return await CreateBitmapImageAsync(bytes).ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await CreateBitmapImageAsync(bytes));
            }
            catch (Exception ex)
            {
                App.Log($"[FileMeta] UI thread icon decode failed: {ex.Message}");
                tcs.SetResult(null);
            }
        }))
        {
            tcs.SetResult(null);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task<ImageSource?> CreateBitmapImageAsync(byte[] bytes)
    {
        var bmp = new BitmapImage { DecodePixelWidth = 32 };
        using var winrtStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(winrtStream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        winrtStream.Seek(0);
        await bmp.SetSourceAsync(winrtStream);
        return bmp;
    }

    /// <summary>
    /// Extracts the shell icon for a cache key without touching the disk:
    /// "*.ext" probes resolve the icon registered for the extension, and the
    /// folder key probes the directory icon via the directory attribute.
    /// </summary>
    private static byte[]? LoadIconBytes(string cacheKey)
    {
        bool isFolder = cacheKey == FolderCacheKey;
        string probe = isFolder ? "folder" : cacheKey == NoExtensionCacheKey ? "*" : $"*{cacheKey}";
        uint attributes = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        var shinfo = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(
            probe,
            attributes,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
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
        catch (Exception ex)
        {
            App.Log($"[FileMeta] Icon conversion failed for '{cacheKey}': {ex.Message}");
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static void FillStats(SearchResultItem item)
    {
        try
        {
            if (item.Kind == SearchResultKind.Folder)
            {
                var dirInfo = new DirectoryInfo(item.DetailPath!);
                if (!dirInfo.Exists)
                {
                    return;
                }

                item.ModifiedAt = dirInfo.LastWriteTime;
                item.CreatedAt = dirInfo.CreationTime;
                item.DateDisplay = FormatDate(dirInfo.CreationTime);
                return;
            }

            var fileInfo = new FileInfo(item.DetailPath!);
            if (!fileInfo.Exists)
            {
                return;
            }

            item.FileSize = fileInfo.Length;
            item.SizeDisplay = FormatSize(fileInfo.Length);
            item.ModifiedAt = fileInfo.LastWriteTime;
            item.CreatedAt = fileInfo.CreationTime;
            item.DateDisplay = FormatDate(fileInfo.CreationTime);
        }
        catch (Exception ex)
        {
            App.Log($"[FileMeta] Failed to read stats for '{item.DetailPath}': {ex.Message}");
        }
    }

    /// <summary>Formats a byte count as a compact, culture-aware size string.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{size:0.##} {units[unit]}";
    }

    /// <summary>Formats a date as short date; older years stay visible for sorting context.</summary>
    public static string FormatDate(DateTime date)
    {
        return date.Year == DateTime.Now.Year
            ? date.ToString("M")
            : date.ToString("d");
    }
}
