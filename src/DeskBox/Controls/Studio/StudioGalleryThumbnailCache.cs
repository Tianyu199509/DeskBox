using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.Studio;

/// <summary>
/// Manages versioned, content-addressed static PNG thumbnails for the Studio Start Gallery
/// (both official/remote templates and user custom widgets) with legacy invalidation and LRU eviction.
/// </summary>
internal static class StudioGalleryThumbnailCache
{
    internal const string CacheSchemaVersionTag = "v2";
    internal const int DefaultMaxCachedFiles = 120;
    internal const long DefaultMaxCachedTotalBytes = 24L * 1024L * 1024L;
    internal const int ThumbnailDecodePixelWidth = 480;
    internal const double GalleryCardRowHeight = 254.0;

    internal static int ComputeRowCount(int itemCount, int columns)
    {
        if (itemCount <= 0)
        {
            return 0;
        }

        int safeCols = Math.Max(1, columns);
        return (itemCount + safeCols - 1) / safeCols;
    }

    internal static (int StartIndex, int Count) ResolveRowSlice(int itemCount, int columns, int rowIndex)
    {
        int safeCols = Math.Max(1, columns);
        if (itemCount <= 0 || rowIndex < 0)
        {
            return (0, 0);
        }

        int start = rowIndex * safeCols;
        if (start >= itemCount)
        {
            return (start, 0);
        }

        int count = Math.Min(safeCols, itemCount - start);
        return (start, count);
    }

    internal static string ResolveDefaultCacheDirectory()
    {
        string widgetsRoot = CustomWidgetDataStore.GetWidgetsRootDirectory();
        string appRoot = Path.GetDirectoryName(widgetsRoot) ?? widgetsRoot;
        return Path.Combine(appRoot, "cache", "studio-gallery-thumbs");
    }

    internal static string BuildMaterialFingerprint(StudioShellVisualSettings shellVisual)
    {
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(shellVisual.WidgetMaterialType);
        if (SettingsService.IsMicaMaterial(materialType))
        {
            return materialType == SettingsService.WidgetMaterialTypeMicaAlt ? "micaalt" : "mica";
        }

        if (SettingsService.IsAcrylicMaterial(materialType))
        {
            return materialType == SettingsService.WidgetMaterialTypeAcrylicBase ? "acrylicbase" : "acrylicthin";
        }

        int opacityPct = (int)Math.Round(Math.Clamp(shellVisual.WidgetOpacity, 0.0, 1.0) * 100.0);
        return $"solid{opacityPct.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static string ResolveTemplateThumbCachePath(
        string templateId,
        string version,
        ElementTheme theme,
        string culture,
        string materialFingerprint,
        string? cacheDirOverride = null)
    {
        string cacheDir = string.IsNullOrWhiteSpace(cacheDirOverride)
            ? ResolveDefaultCacheDirectory()
            : cacheDirOverride!;
        string themeTag = theme == ElementTheme.Light ? "light" : "dark";
        string fileName =
            $"{CacheSchemaVersionTag}__tpl__{SanitizeToken(templateId)}__{SanitizeToken(version)}__{themeTag}__{SanitizeToken(culture)}__{SanitizeToken(materialFingerprint)}.png";
        return Path.Combine(cacheDir, fileName);
    }

    internal static string ComputeUserWidgetContentHash(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string culture)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8Chunk(sha, manifestJson ?? string.Empty);
        AppendUtf8Chunk(sha, logicJs ?? string.Empty);
        if (locales is { HasAnyLocale: true })
        {
            IReadOnlyList<string> lookupOrder = CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes(culture, locales.Meta?.BaseLocale);
            foreach (string code in lookupOrder)
            {
                if (locales.Locales.TryGetValue(code, out IReadOnlyDictionary<string, string>? dict) && dict.Count > 0)
                {
                    AppendUtf8Chunk(sha, code);
                    foreach (KeyValuePair<string, string> kv in dict.OrderBy(static p => p.Key, StringComparer.Ordinal))
                    {
                        AppendUtf8Chunk(sha, kv.Key);
                        AppendUtf8Chunk(sha, kv.Value);
                    }
                }
            }
        }

        byte[] digest = sha.GetHashAndReset();
        return Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }

    internal static string ResolveUserWidgetThumbCachePath(
        string widgetId,
        string contentHash,
        ElementTheme theme,
        string culture,
        string materialFingerprint,
        string? cacheDirOverride = null)
    {
        string cacheDir = string.IsNullOrWhiteSpace(cacheDirOverride)
            ? ResolveDefaultCacheDirectory()
            : cacheDirOverride!;
        string themeTag = theme == ElementTheme.Light ? "light" : "dark";
        string fileName =
            $"{CacheSchemaVersionTag}__usr__{SanitizeToken(widgetId)}__{SanitizeToken(contentHash)}__{themeTag}__{SanitizeToken(culture)}__{SanitizeToken(materialFingerprint)}.png";
        return Path.Combine(cacheDir, fileName);
    }

    internal static void DeleteStaleUserWidgetThumbs(
        string widgetId,
        string keepFilePath,
        string? cacheDirOverride = null)
    {
        try
        {
            string cacheDir = string.IsNullOrWhiteSpace(cacheDirOverride)
                ? ResolveDefaultCacheDirectory()
                : cacheDirOverride!;
            if (!Directory.Exists(cacheDir))
            {
                return;
            }

            string prefix = $"{CacheSchemaVersionTag}__usr__{SanitizeToken(widgetId)}__";
            string keepName = Path.GetFileName(keepFilePath);
            foreach (string path in Directory.EnumerateFiles(cacheDir, "*.png"))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fileName, keepName, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteQuietly(path);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    internal static void DeleteAllUserWidgetThumbs(string widgetId, string? cacheDirOverride = null)
    {
        try
        {
            string cacheDir = string.IsNullOrWhiteSpace(cacheDirOverride)
                ? ResolveDefaultCacheDirectory()
                : cacheDirOverride!;
            if (!Directory.Exists(cacheDir))
            {
                return;
            }

            string prefix = $"{CacheSchemaVersionTag}__usr__{SanitizeToken(widgetId)}__";
            foreach (string path in Directory.EnumerateFiles(cacheDir, "*.png"))
            {
                if (Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteQuietly(path);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    internal static int PruneCacheDirectory(
        string? cacheDirOverride = null,
        int maxFiles = DefaultMaxCachedFiles,
        long maxTotalBytes = DefaultMaxCachedTotalBytes)
    {
        int deleted = 0;
        try
        {
            string cacheDir = string.IsNullOrWhiteSpace(cacheDirOverride)
                ? ResolveDefaultCacheDirectory()
                : cacheDirOverride!;
            if (!Directory.Exists(cacheDir))
            {
                return 0;
            }

            string currentPrefix = $"{CacheSchemaVersionTag}__";
            var currentEntries = new List<FileInfo>();
            long totalBytes = 0;

            foreach (string path in Directory.EnumerateFiles(cacheDir))
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                // Evict stale cache-version thumbnails and stray interrupted temp files older than 5 minutes.
                bool isStaleVersionThumb = info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                    !info.Name.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase);
                bool isOrphanTmp = info.Name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - info.LastWriteTimeUtc) > TimeSpan.FromMinutes(5);
                if (isStaleVersionThumb || isOrphanTmp)
                {
                    if (TryDeleteQuietly(info.FullName))
                    {
                        deleted++;
                    }
                    continue;
                }

                if (info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    currentEntries.Add(info);
                    totalBytes += info.Length;
                }
            }

            int safeMaxFiles = Math.Max(1, maxFiles);
            long safeMaxBytes = Math.Max(1024L, maxTotalBytes);
            if (currentEntries.Count <= safeMaxFiles && totalBytes <= safeMaxBytes)
            {
                return deleted;
            }

            currentEntries.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            int remainingCount = currentEntries.Count;
            foreach (FileInfo entry in currentEntries)
            {
                if (remainingCount <= safeMaxFiles && totalBytes <= safeMaxBytes)
                {
                    break;
                }

                if (TryDeleteQuietly(entry.FullName))
                {
                    deleted++;
                    remainingCount--;
                    totalBytes -= entry.Length;
                }
            }
        }
        catch
        {
            // Best-effort cache maintenance only.
        }

        return deleted;
    }

    private static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "default";
        }

        string trimmed = raw.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return sb.ToString();
    }

    private static void AppendUtf8Chunk(IncrementalHash sha, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        sha.AppendData(bytes);
        sha.AppendData([(byte)0]);
    }

    private static bool TryDeleteQuietly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            CustomWidgetStorageFileOps.DeleteFileIfExists(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
