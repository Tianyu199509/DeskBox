using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

/// <summary>
/// Fetches the Bing daily homepage image via the unofficial HPImageArchive endpoint.
/// Zero API key, zero cost. Community-stable since ~2017.
/// Docs: https://www.bing.com/HPImageArchive.aspx?format=js&amp;idx=0&amp;n=1&amp;mkt=zh-CN
/// </summary>
public sealed partial class BingWallpaperService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;

    private static readonly string[] s_resolutionSegments = ["UHD", "1920x1080", "1366x768"];

    private const string ApiEndpointTemplate =
        "https://www.bing.com/HPImageArchive.aspx?format=js&idx={0}&n={1}&mkt={2}";

    private const int MaxHistoryDays = 7;
    private const string DefaultMarket = "zh-CN";

    public BingWallpaperService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        _cacheDirectory = cacheDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox", "data", "bing-wallpaper");

        System.IO.Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Returns the local file path to the cached Bing image for the requested day.
    /// Downloads if not present. Falls back through requested resolutions to past days.
    /// Returns null on total failure (caller should use a previously cached image).
    /// </summary>
    public async Task<string?> GetImagePathAsync(
        int dayOffset = 0,
        string resolution = "1920x1080",
        CancellationToken ct = default)
    {
        string[] sizesToTry = resolution == "UHD"
            ? s_resolutionSegments
            : [resolution, .. s_resolutionSegments];

        int maxOffset = Math.Min(dayOffset + 2, MaxHistoryDays - 1);

        for (int offset = dayOffset; offset <= maxOffset; offset++)
        {
            string? path = await TryGetImageAsync(offset, sizesToTry, ct);
            if (path is not null) return path;
        }

        return null;
    }

    private async Task<string?> TryGetImageAsync(
        int offset,
        string[] sizesToTry,
        CancellationToken ct)
    {
        BingImageInfo? info = await FetchImageInfoAsync(offset, ct);
        if (info is null) return null;

        foreach (string size in sizesToTry)
        {
            string imageUrl = BuildImageUrl(info.UrlBase, size);
            string localPath = GetCachePath(info.Date, size);

            if (System.IO.File.Exists(localPath)) return localPath;

            try
            {
                using HttpResponseMessage resp = await _httpClient.GetAsync(imageUrl, ct);
                if (!resp.IsSuccessStatusCode) continue;

                await using var fs = System.IO.File.Create(localPath);
                await resp.Content.CopyToAsync(fs, ct);

                // sidecar metadata
                string metaPath = localPath + ".meta.json";
                var meta = new BingWallpaperMetaDto
                {
                    Date = info.Date,
                    Title = info.Title,
                    Copyright = info.Copyright,
                    CopyrightUrl = info.CopyrightUrl,
                    Resolution = size,
                    Source = "Bing"
                };
                await System.IO.File.WriteAllTextAsync(metaPath,
                    JsonSerializer.Serialize(meta, BingWallpaperJsonOptions.Writing), ct);

                return localPath;
            }
            catch (Exception ex)
            {
                App.Log($"[BingWallpaper] Download failed for {imageUrl}: {ex}");
            }
        }

        return null;
    }

    private async Task<BingImageInfo?> FetchImageInfoAsync(int dayOffset, CancellationToken ct)
    {
        string url = string.Format(ApiEndpointTemplate, dayOffset, 1, DefaultMarket);

        try
        {
            using HttpResponseMessage resp = await _httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var archive = await JsonSerializer.DeserializeAsync<BingArchive>(
                stream, BingWallpaperJsonOptions.Deserializing, ct);

            var item = archive?.Images?.FirstOrDefault(i =>
                !string.IsNullOrEmpty(i.Date) && !string.IsNullOrEmpty(i.UrlBase));

            if (item is null) return null;

            return new BingImageInfo(item.Date, item.UrlBase, item.Title ?? "", item.Copyright ?? "", item.HoverUrl ?? "");
        }
        catch (Exception ex)
        {
            App.Log($"[BingWallpaper] Fetch info failed for idx={dayOffset}: {ex}");
            return null;
        }
    }

    private static string BuildImageUrl(string urlBase, string resolution) =>
        $"https://www.bing.com{urlBase}_{resolution}.jpg";

    private string GetCachePath(string date, string resolution) =>
        System.IO.Path.Combine(_cacheDirectory, $"{date}_{resolution}.jpg");

    /// <summary>
    /// Reads the sidecar metadata for a cached Bing wallpaper, if present.
    /// </summary>
    public static BingWallpaperMetaDto? ReadMetaForImage(string imagePath)
    {
        string metaPath = imagePath + ".meta.json";
        if (!System.IO.File.Exists(metaPath)) return null;

        try
        {
            return JsonSerializer.Deserialize<BingWallpaperMetaDto>(
                System.IO.File.ReadAllText(metaPath),
                BingWallpaperJsonOptions.Deserializing);
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes cached images older than the given age.
    /// </summary>
    public void PruneOldCache(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.Now - maxAge;
            foreach (string f in System.IO.Directory.GetFiles(_cacheDirectory, "*.jpg"))
            {
                if (System.IO.File.GetLastWriteTime(f) < cutoff)
                {
                    System.IO.File.Delete(f);
                    string meta = f + ".meta.json";
                    if (System.IO.File.Exists(meta)) System.IO.File.Delete(meta);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[BingWallpaper] Cache prune failed: {ex}");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record BingImageInfo(string Date, string UrlBase, string Title, string Copyright, string CopyrightUrl);
}

// ─── Internal JSON DTOs ─────────────────────────────────────

internal sealed class BingArchive
{
    [JsonPropertyName("images")]
    public List<BingImageEntry>? Images { get; set; }
}

internal sealed class BingImageEntry
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("urlbase")] public string UrlBase { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("copyright")] public string? Copyright { get; set; }
    [JsonPropertyName("hoverurl")] public string? HoverUrl { get; set; }
}

public sealed class BingWallpaperMetaDto
{
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public string Copyright { get; set; } = "";
    public string CopyrightUrl { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Source { get; set; } = "Bing";
}

internal static class BingWallpaperJsonOptions
{
    public static readonly JsonSerializerOptions Deserializing = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions Writing = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
