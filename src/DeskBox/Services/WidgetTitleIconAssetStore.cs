using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(WidgetTitleIconRecentState), TypeInfoPropertyName = "RecentState")]
internal sealed partial class WidgetTitleIconAssetJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Recently picked emojis for the title-icon customizer. A tiny standalone
/// file keeps this state out of settings.json (the bloated-settings red line)
/// and bounded, so it never needs a schema migration.
/// </summary>
public sealed class WidgetTitleIconRecentState
{
    public List<string> RecentEmojis { get; set; } = [];
}

public enum WidgetTitleIconAssetResult
{
    Copied,
    FileMissing,
    UnsupportedFormat,
    TooLarge,
}

/// <summary>
/// Owns the per-widget asset directory (<c>data/widget-assets/{id}/</c>)
/// that stores the custom title-icon image copy. Copying (instead of
/// referencing the picked path) keeps the icon alive when the source file,
/// OneDrive placeholder, or removable drive disappears — the same rule the
/// Glance image cache follows.
/// </summary>
public sealed class WidgetTitleIconAssetStore
{
    public const int MaxRecentEmojis = 12;

    private static readonly WidgetTitleIconAssetStore s_default = new(
        DeskBoxDataPathService.Current.DataDirectory);

    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _recentGate = new(1, 1);

    public static WidgetTitleIconAssetStore Current => s_default;

    public WidgetTitleIconAssetStore(string dataDirectory)
    {
        _rootDirectory = Path.Combine(dataDirectory, "widget-assets");
    }

    /// <summary>Deterministic stored name for a picked source path.</summary>
    public static string GetStoredFileName(string sourcePath)
    {
        return WidgetTitleIconCustomization.ImageFileStem +
            Path.GetExtension(sourcePath).ToLowerInvariant();
    }

    /// <summary>Deterministic stored name for a background pick.</summary>
    public static string GetStoredBackgroundFileName(string sourcePath)
    {
        return WidgetBackgroundCustomization.ImageFileStem +
            Path.GetExtension(sourcePath).ToLowerInvariant();
    }

    /// <summary>Deterministic stored name for a panorama pick.</summary>
    public static string GetStoredPanoramaFileName(string sourcePath)
    {
        return "panorama" + Path.GetExtension(sourcePath).ToLowerInvariant();
    }

    /// <summary>Directory for app-level shared assets (global backgrounds).</summary>
    public string GetSharedDirectory() => Path.Combine(_rootDirectory, "shared");

    public Task<WidgetTitleIconAssetResult> CopyUnifiedBackgroundImageAsync(string sourcePath)
    {
        return CopyAssetAsync(
            SharedAssetWidgetId,
            sourcePath,
            WidgetBackgroundCustomization.ImageFileStem);
    }

    public Task<WidgetTitleIconAssetResult> CopyPanoramaBackgroundImageAsync(string sourcePath)
    {
        return CopyAssetAsync(
            SharedAssetWidgetId,
            sourcePath,
            PanoramaFileStem);
    }

    /// <summary>
    /// Reserved pseudo-widget id whose asset directory is
    /// <c>data/widget-assets/shared/</c> for the global background images.
    /// </summary>
    private const string SharedAssetWidgetId = "shared";

    private const string PanoramaFileStem = "panorama";

    public string? ResolveUnifiedBackgroundPath(string? fileName)
    {
        return ResolveAssetPath(SharedAssetWidgetId, fileName);
    }

    public string? ResolvePanoramaBackgroundPath(string? fileName)
    {
        return ResolveAssetPath(SharedAssetWidgetId, fileName);
    }

    public string GetAssetDirectory(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        return Path.Combine(_rootDirectory, GetSafeWidgetDirectoryName(widgetId));
    }

    /// <summary>Returns the absolute path of the stored icon, or null when absent.</summary>
    public string? ResolveImagePath(string widgetId, string? fileName)
    {
        return ResolveAssetPath(widgetId, fileName);
    }

    /// <summary>Returns the absolute path of the stored background, or null when absent.</summary>
    public string? ResolveBackgroundPath(string widgetId, string? fileName)
    {
        return ResolveAssetPath(widgetId, fileName);
    }

    private string? ResolveAssetPath(string widgetId, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0 ||
            !WidgetTitleIconCustomization.IsSupportedImageExtension(Path.GetExtension(fileName)))
        {
            return null;
        }

        string path = Path.Combine(
            _rootDirectory,
            GetSafeWidgetDirectoryName(widgetId),
            fileName);
        return File.Exists(path) ? path : null;
    }

    public Task<WidgetTitleIconAssetResult> CopyImageAsync(
        string widgetId,
        string sourcePath)
    {
        return CopyAssetAsync(widgetId, sourcePath, WidgetTitleIconCustomization.ImageFileStem);
    }

    public Task<WidgetTitleIconAssetResult> CopyBackgroundImageAsync(
        string widgetId,
        string sourcePath)
    {
        return CopyAssetAsync(widgetId, sourcePath, WidgetBackgroundCustomization.ImageFileStem);
    }

    private async Task<WidgetTitleIconAssetResult> CopyAssetAsync(
        string widgetId,
        string sourcePath,
        string fileStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        string? extension = Path.GetExtension(sourcePath);
        if (!WidgetTitleIconCustomization.IsSupportedImageExtension(extension))
        {
            return WidgetTitleIconAssetResult.UnsupportedFormat;
        }

        if (!File.Exists(sourcePath))
        {
            return WidgetTitleIconAssetResult.FileMissing;
        }

        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length > WidgetTitleIconCustomization.MaxImageFileBytes)
        {
            return WidgetTitleIconAssetResult.TooLarge;
        }

        string directory = GetAssetDirectory(widgetId);
        Directory.CreateDirectory(directory);
        string targetPath = Path.Combine(
            directory,
            fileStem + extension!.ToLowerInvariant());

        // Replace semantics: a previous pick with a different extension must
        // not survive as a second {stem}.* file. Best-effort with the same
        // decode-handle tolerance as the swap below (deep review R15, F8
        // audit): the stale file can equally be held by an in-flight decode,
        // and a hard failure here aborted the whole copy with no user
        // feedback — keeping the stale file is harmless (the next pick or
        // cleanup removes it; only the fresh target path matters).
        foreach (string stale in Directory.EnumerateFiles(directory, fileStem + ".*"))
        {
            if (!string.Equals(stale, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Delete(stale);
                        break;
                    }
                    catch (IOException) when (attempt == 0)
                    {
                        await Task.Delay(50);
                    }
                    catch (Exception deleteEx)
                    {
                        App.Log($"[AssetStore] Stale asset delete failed (kept, harmless): {deleteEx.Message}");
                        break;
                    }
                }
            }
        }

        // Atomic replace: the scheduled backup snapshots data/ while the
        // app runs, and an in-flight streamed decode can hold the current
        // file open — write a sibling temp file (the backup filter skips
        // .tmp suffixes) and swap it in, so a snapshot never captures a
        // truncated image and a concurrent reader never sees a half
        // written one. The swap retries once for a decode handle released
        // mid-swap.
        string tempPath = targetPath + ".tmp";
        try
        {
            using (FileStream target = File.Create(tempPath))
            {
                using FileStream source = sourceInfo.OpenRead();
                await source.CopyToAsync(target);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    return WidgetTitleIconAssetResult.Copied;
                }
                catch (IOException) when (attempt == 0)
                {
                    await Task.Delay(50);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                App.Log($"[TitleIconAsset] Failed to clean temp copy: {ex.Message}");
            }
        }
    }

    public void DeleteImage(string widgetId, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0 ||
            !WidgetTitleIconCustomization.IsSupportedImageExtension(Path.GetExtension(fileName)))
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(GetAssetDirectory(widgetId), fileName));
        }
        catch (Exception ex)
        {
            App.Log($"[TitleIconAsset] Failed to delete '{fileName}': {ex.Message}");
        }
    }

    /// <summary>Removes the whole asset directory when a widget is deleted.</summary>
    public void DeleteWidgetAssets(string widgetId)
    {
        try
        {
            string directory = GetAssetDirectory(widgetId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TitleIconAsset] Failed to clean assets for widget: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> LoadRecentEmojisAsync()
    {
        await _recentGate.WaitAsync();
        try
        {
            string path = GetRecentStatePath();
            if (!File.Exists(path))
            {
                return [];
            }

            string json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize(
                json,
                WidgetTitleIconAssetJsonContext.Default.RecentState);
            return state?.RecentEmojis is { Count: > 0 } recent
                ? recent.Take(MaxRecentEmojis).ToArray()
                : [];
        }
        catch (Exception ex)
        {
            App.Log($"[TitleIconAsset] Failed to load recent emojis: {ex.Message}");
            return [];
        }
        finally
        {
            _recentGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> AddRecentEmojiAsync(string emoji)
    {
        if (!WidgetTitleIconCustomization.IsValidEmoji(emoji))
        {
            return await LoadRecentEmojisAsync();
        }

        await _recentGate.WaitAsync();
        try
        {
            var state = new WidgetTitleIconRecentState();
            if (File.Exists(GetRecentStatePath()))
            {
                string json = await File.ReadAllTextAsync(GetRecentStatePath());
                state = JsonSerializer.Deserialize(
                    json,
                    WidgetTitleIconAssetJsonContext.Default.RecentState) ?? state;
            }

            string picked = emoji.Trim();
            state.RecentEmojis.RemoveAll(existing =>
                string.Equals(existing, picked, StringComparison.Ordinal));
            state.RecentEmojis.Insert(0, picked);
            if (state.RecentEmojis.Count > MaxRecentEmojis)
            {
                state.RecentEmojis.RemoveRange(
                    MaxRecentEmojis,
                    state.RecentEmojis.Count - MaxRecentEmojis);
            }

            await WriteRecentStateAtomicAsync(state);
            return state.RecentEmojis.ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"[TitleIconAsset] Failed to store recent emoji: {ex.Message}");
            return [];
        }
        finally
        {
            _recentGate.Release();
        }
    }

    private async Task WriteRecentStateAtomicAsync(WidgetTitleIconRecentState state)
    {
        Directory.CreateDirectory(_rootDirectory);
        string path = GetRecentStatePath();
        string tempPath = path + ".tmp";
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(state, WidgetTitleIconAssetJsonContext.Default.RecentState));
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetRecentStatePath() => Path.Combine(_rootDirectory, "recent-title-icons.json");

    private static string GetSafeWidgetDirectoryName(string widgetId)
    {
        var builder = new System.Text.StringBuilder(widgetId.Length);
        foreach (char c in widgetId)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
