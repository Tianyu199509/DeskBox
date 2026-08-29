using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskBox.Models;
using Windows.Networking.Connectivity;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(List<GlanceImageInfo>),
    TypeInfoPropertyName = "ImageCatalog")]
internal sealed partial class GlanceImageCatalogJsonContext : JsonSerializerContext
{
}

public sealed class GlanceImageService
{
    private const int TargetCatalogSizePerCategory = 12;
    private const int MaximumCacheItemsPerCategory = 18;
    private const int MaximumCacheItemsTotal = 72;
    private const int IncrementalDownloadCount = 3;
    private const int CategoryMemberQueryLimit = 200;
    private const int RemoteCandidateLimit = 80;
    private const int BingArchiveBatchSize = 8;
    private const int BingArchiveBatchCount = 3;
    private const long MaximumDownloadBytes = 18L * 1024 * 1024;
    private static readonly SemaphoreSlim OnlineRefreshGate = new(1, 1);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly string _cacheDirectory;
    private readonly string _imageDirectory;
    private readonly string _catalogPath;
    private readonly HttpClient _httpClient;
    private readonly Func<bool> _canUseBackgroundNetwork;

    public GlanceImageService()
        : this(
            Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "cache", "glance"),
            SharedHttpClient,
            CanUseBackgroundNetwork)
    {
    }

    internal GlanceImageService(string cacheDirectory)
        : this(cacheDirectory, SharedHttpClient, CanUseBackgroundNetwork)
    {
    }

    internal GlanceImageService(
        string cacheDirectory,
        HttpClient httpClient,
        Func<bool> canUseBackgroundNetwork)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(canUseBackgroundNetwork);
        _cacheDirectory = cacheDirectory;
        _imageDirectory = Path.Combine(cacheDirectory, "images");
        _catalogPath = Path.Combine(cacheDirectory, "catalog.json");
        _httpClient = httpClient;
        _canUseBackgroundNetwork = canUseBackgroundNetwork;
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> GetAvailableImagesAsync(
        GlanceWidgetData settings,
        CancellationToken cancellationToken = default)
    {
        if (IsOnlineSource(settings.BackgroundSource))
        {
            return await LoadCachedOnlineImagesAsync(
                GetOnlineProvider(settings.BackgroundSource),
                GetOnlineCategory(settings.BackgroundSource, settings.OnlineImageCategory),
                cancellationToken);
        }

        return await Task.Run<IReadOnlyList<GlanceImageInfo>>(
            () => settings.BackgroundSource == GlanceBackgroundSource.LocalFiles
                ? CreateLocalImages(settings.LocalImagePaths)
                : CreateFolderImages(settings.LocalFolderPath),
            cancellationToken);
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> LoadCachedOnlineImagesAsync(
        CancellationToken cancellationToken = default)
    {
        return await LoadCachedOnlineImagesAsync(
            GlanceOnlineImageCategory.Featured,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> LoadCachedOnlineImagesAsync(
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        return await LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Wikimedia,
            category,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> LoadCachedOnlineImagesAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<GlanceImageInfo> catalog = await LoadCatalogAsync(cancellationToken);
        return catalog
            .Where(image => MatchesOnlineSource(image, provider, category) && IsUsableFile(image.LocalPath))
            .OrderByDescending(image => image.CachedAtUtc)
            .Take(MaximumCacheItemsPerCategory)
            .ToArray();
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesAsync(
        CancellationToken cancellationToken = default)
    {
        return await RefreshOnlineImagesAsync(
            GlanceOnlineImageCategory.Featured,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesAsync(
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        return await RefreshOnlineImagesAsync(
            GlanceOnlineImageProvider.Wikimedia,
            category,
            cancellationToken);
    }

    public Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesAsync(
        GlanceWidgetData settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsOnlineSource(settings.BackgroundSource))
        {
            return Task.FromResult<IReadOnlyList<GlanceImageInfo>>([]);
        }

        return RefreshOnlineImagesAsync(
            GetOnlineProvider(settings.BackgroundSource),
            GetOnlineCategory(settings.BackgroundSource, settings.OnlineImageCategory),
            cancellationToken);
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        if (!_canUseBackgroundNetwork())
        {
            return await LoadCachedOnlineImagesAsync(provider, category, cancellationToken);
        }

        await OnlineRefreshGate.WaitAsync(cancellationToken);
        try
        {
            List<GlanceImageInfo> cached = await LoadCatalogAsync(cancellationToken);
            var bySource = cached
                .Where(image => MatchesOnlineSource(image, provider, category) &&
                    !string.IsNullOrWhiteSpace(image.SourcePageUrl))
                .GroupBy(image => image.SourcePageUrl!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<GlanceImageInfo> remote = await QueryOnlinePicturesAsync(
                provider,
                category,
                cancellationToken);
            Directory.CreateDirectory(_imageDirectory);

            int newDownloads = 0;
            int usableCategoryCount = cached.Count(image =>
                MatchesOnlineSource(image, provider, category) && IsUsableFile(image.LocalPath));
            int downloadLimit = Math.Min(
                IncrementalDownloadCount,
                Math.Max(0, TargetCatalogSizePerCategory - usableCategoryCount));
            foreach (GlanceImageInfo candidate in remote)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bySource.TryGetValue(candidate.SourcePageUrl ?? string.Empty, out GlanceImageInfo? existing) &&
                    IsUsableFile(existing.LocalPath))
                {
                    continue;
                }

                if (newDownloads >= downloadLimit)
                {
                    break;
                }

                GlanceImageInfo? downloaded;
                try
                {
                    downloaded = await DownloadAsync(candidate, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[GlanceImageService] Image download failed for " +
                        $"'{candidate.SourcePageUrl ?? candidate.RemoteImageUrl}': {ex}");
                    continue;
                }

                if (downloaded is not null)
                {
                    cached.RemoveAll(image => string.Equals(image.Id, downloaded.Id, StringComparison.Ordinal));
                    cached.Add(downloaded);
                    newDownloads++;
                }
            }

            cached = cached
                .Where(image => IsUsableFile(image.LocalPath))
                .OrderByDescending(image => image.CachedAtUtc)
                .ToList();
            await TrimAndSaveCatalogAsync(cached, cancellationToken);
            return cached
                .Where(image => MatchesOnlineSource(image, provider, category))
                .Take(MaximumCacheItemsPerCategory)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceImageService] Online refresh failed: {ex}");
            return await LoadCachedOnlineImagesAsync(provider, category, cancellationToken);
        }
        finally
        {
            OnlineRefreshGate.Release();
        }
    }

    public async Task ClearCacheAsync()
    {
        await OnlineRefreshGate.WaitAsync();
        try
        {
            if (Directory.Exists(_imageDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(_imageDirectory))
                {
                    TryDelete(file);
                }
            }

            TryDelete(_catalogPath);
        }
        finally
        {
            OnlineRefreshGate.Release();
        }
    }

    public long GetCacheSizeBytes()
    {
        try
        {
            return !Directory.Exists(_cacheDirectory)
                ? 0
                : Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static List<GlanceImageInfo> CreateLocalImages(IEnumerable<string> paths)
    {
        return paths
            .Where(IsSupportedImagePath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(CreateLocalImage)
            .ToList();
    }

    private static List<GlanceImageInfo> CreateFolderImages(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return [];
        }

        try
        {
            return CreateLocalImages(Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceImageService] Local folder enumeration failed: {ex}");
            return [];
        }
    }

    private static GlanceImageInfo CreateLocalImage(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return new GlanceImageInfo
        {
            Id = CreateStableId(fullPath),
            LocalPath = fullPath,
            Title = Path.GetFileNameWithoutExtension(fullPath)
        };
    }

    private async Task<List<GlanceImageInfo>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
            return JsonSerializer.Deserialize(
                       json,
                       GlanceImageCatalogJsonContext.Default.ImageCatalog) ??
                   [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[GlanceImageService] Catalog load failed: {ex}");
            return [];
        }
    }

    private Task<IReadOnlyList<GlanceImageInfo>> QueryOnlinePicturesAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken)
    {
        return provider == GlanceOnlineImageProvider.Bing
            ? QueryBingPicturesAsync(cancellationToken)
            : QueryCategoryPicturesAsync(category, cancellationToken);
    }

    private async Task<IReadOnlyList<GlanceImageInfo>> QueryBingPicturesAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<GlanceImageInfo>();
        for (int batch = 0; batch < BingArchiveBatchCount; batch++)
        {
            int index = batch * BingArchiveBatchSize;
            string archiveUrl = "https://cn.bing.com/HPImageArchive.aspx?format=js" +
                $"&idx={index}&n={BingArchiveBatchSize}&mkt=zh-CN";
            using JsonDocument document = await GetJsonAsync(archiveUrl, cancellationToken);
            if (!document.RootElement.TryGetProperty("images", out JsonElement images))
            {
                continue;
            }

            foreach (JsonElement image in images.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (image.TryGetProperty("wp", out JsonElement wallpaper) &&
                    wallpaper.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                string? relativeImageUrl = GetString(image, "url");
                if (string.IsNullOrWhiteSpace(relativeImageUrl))
                {
                    continue;
                }

                string stableKey = GetString(image, "hsh") ??
                    GetString(image, "urlbase") ??
                    relativeImageUrl;
                results.Add(new GlanceImageInfo
                {
                    Id = CreateStableId($"bing:{stableKey}"),
                    Title = CleanMetadata(GetString(image, "title")),
                    Author = CleanMetadata(GetString(image, "copyright")),
                    License = "Bing",
                    LicenseUrl = "https://www.microsoft.com/zh-cn/bing/bing-wallpaper",
                    SourcePageUrl = ToAbsoluteBingUrl(GetString(image, "copyrightlink")),
                    RemoteImageUrl = ToAbsoluteBingUrl(relativeImageUrl),
                    PixelWidth = 1920,
                    PixelHeight = 1080,
                    OnlineCategory = GlanceOnlineImageCategory.Featured,
                    OnlineProvider = GlanceOnlineImageProvider.Bing
                });
            }
        }

        return results
            .GroupBy(image => image.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(RemoteCandidateLimit)
            .ToArray();
    }

    private async Task<IReadOnlyList<GlanceImageInfo>> QueryCategoryPicturesAsync(
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken)
    {
        string categoryTitle = category switch
        {
            GlanceOnlineImageCategory.Featured => $"Category:Pictures of the day ({DateTime.UtcNow:yyyy})",
            GlanceOnlineImageCategory.Landscapes => "Category:Featured pictures of landscapes",
            GlanceOnlineImageCategory.Cities => "Category:Quality images of cityscapes",
            GlanceOnlineImageCategory.Architecture => "Category:Featured pictures of architecture",
            GlanceOnlineImageCategory.Animals => "Category:Wildlife photography",
            GlanceOnlineImageCategory.Plants => "Category:Featured pictures of plants",
            GlanceOnlineImageCategory.Astronomy => "Category:Featured pictures of astronomy",
            GlanceOnlineImageCategory.People => "Category:Featured pictures of people",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
        string categoryUrl = "https://commons.wikimedia.org/w/api.php?action=query&list=categorymembers" +
            "&cmtype=file&cmnamespace=6&cmlimit=" + CategoryMemberQueryLimit +
            "&format=json&formatversion=2&cmtitle=" + Uri.EscapeDataString(categoryTitle);

        using JsonDocument document = await GetJsonAsync(categoryUrl, cancellationToken);
        var fileNames = new List<string>();
        if (document.RootElement.TryGetProperty("query", out JsonElement query) &&
            query.TryGetProperty("categorymembers", out JsonElement members))
        {
            foreach (JsonElement member in members.EnumerateArray())
            {
                string? title = GetString(member, "title");
                if (!string.IsNullOrWhiteSpace(title) && title.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                {
                    fileNames.Add(title[5..]);
                }
            }
        }

        return await QueryImageInfoAsync(
            fileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(_ => Random.Shared.NextInt64())
                .Take(RemoteCandidateLimit),
            category,
            cancellationToken);
    }

    private async Task<IReadOnlyList<GlanceImageInfo>> QueryImageInfoAsync(
        IEnumerable<string> fileNames,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken)
    {
        var results = new List<GlanceImageInfo>();
        foreach (string[] chunk in fileNames.Chunk(50))
        {
            string imageUrl = "https://commons.wikimedia.org/w/api.php?action=query&prop=imageinfo" +
                "&iiprop=url%7Csize%7Cmime%7Cextmetadata" +
                "&iiextmetadatafilter=ObjectName%7CImageDescription%7CArtist%7CAttribution%7CLicenseShortName%7CLicenseUrl" +
                "&iiurlwidth=1600&format=json&formatversion=2&titles=" +
                Uri.EscapeDataString(string.Join('|', chunk.Select(name => $"File:{name}")));
            using JsonDocument images = await GetJsonAsync(imageUrl, cancellationToken);
            foreach (JsonElement page in GetPages(images.RootElement))
            {
                if (!page.TryGetProperty("imageinfo", out JsonElement infos) || infos.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement info = infos[0];
                int width = GetInt(info, "thumbwidth", GetInt(info, "width", 0));
                int height = GetInt(info, "thumbheight", GetInt(info, "height", 0));
                string mime = GetString(info, "mime") ?? string.Empty;
                if (width <= 0 || height <= 0 || width < height * 1.1 ||
                    mime is not ("image/jpeg" or "image/png" or "image/webp"))
                {
                    continue;
                }

                JsonElement metadata = info.TryGetProperty("extmetadata", out JsonElement value)
                    ? value
                    : default;
                string sourcePage = GetString(info, "descriptionurl") ?? string.Empty;
                results.Add(new GlanceImageInfo
                {
                    Id = CreateStableId(sourcePage),
                    Title = CleanMetadata(GetMetadata(metadata, "ObjectName") ?? GetMetadata(metadata, "ImageDescription")),
                    Author = CleanMetadata(GetMetadata(metadata, "Artist") ?? GetMetadata(metadata, "Attribution")),
                    License = CleanMetadata(GetMetadata(metadata, "LicenseShortName")),
                    LicenseUrl = GetMetadata(metadata, "LicenseUrl"),
                    SourcePageUrl = sourcePage,
                    RemoteImageUrl = GetString(info, "thumburl") ?? GetString(info, "url"),
                    PixelWidth = width,
                    PixelHeight = height,
                    OnlineCategory = category,
                    OnlineProvider = GlanceOnlineImageProvider.Wikimedia
                });
            }

            if (results.Count >= TargetCatalogSizePerCategory + 8)
            {
                break;
            }
        }

        return results;
    }

    private async Task<GlanceImageInfo?> DownloadAsync(
        GlanceImageInfo candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.RemoteImageUrl))
        {
            return null;
        }

        using HttpResponseMessage response = await _httpClient.GetAsync(
            candidate.RemoteImageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            return null;
        }

        string extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        string destination = Path.Combine(_imageDirectory, $"{candidate.Id}{extension}");
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await CopyWithLimitAsync(input, output, MaximumDownloadBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
            candidate.LocalPath = destination;
            candidate.CachedAtUtc = DateTimeOffset.UtcNow;
            return candidate;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task TrimAndSaveCatalogAsync(
        List<GlanceImageInfo> catalog,
        CancellationToken cancellationToken)
    {
        List<GlanceImageInfo> kept = catalog
            .Where(image => IsUsableFile(image.LocalPath))
            .GroupBy(image => new { image.OnlineProvider, image.OnlineCategory })
            .SelectMany(group => group
                .OrderByDescending(image => image.CachedAtUtc)
                .Take(MaximumCacheItemsPerCategory))
            .OrderByDescending(image => image.CachedAtUtc)
            .Take(MaximumCacheItemsTotal)
            .ToList();
        var keptPaths = kept
            .Select(image => image.LocalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (GlanceImageInfo stale in catalog.Where(image => !kept.Contains(image)).ToArray())
        {
            if (!keptPaths.Contains(stale.LocalPath))
            {
                TryDelete(stale.LocalPath);
            }
        }
        catalog.Clear();
        catalog.AddRange(kept);

        Directory.CreateDirectory(_cacheDirectory);
        string json = JsonSerializer.Serialize(
            catalog,
            GlanceImageCatalogJsonContext.Default.ImageCatalog);
        string temporary = $"{_catalogPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _catalogPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static IEnumerable<JsonElement> GetPages(JsonElement root)
    {
        if (root.TryGetProperty("query", out JsonElement query) &&
            query.TryGetProperty("pages", out JsonElement pages))
        {
            foreach (JsonElement page in pages.EnumerateArray())
            {
                yield return page;
            }
        }
    }

    private static string? GetMetadata(JsonElement metadata, string name)
    {
        return metadata.ValueKind == JsonValueKind.Object &&
               metadata.TryGetProperty(name, out JsonElement item) &&
               item.TryGetProperty("value", out JsonElement value)
            ? value.ToString()
            : null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : fallback;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }

    private static string? CleanMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        string decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string ToAbsoluteBingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "https://cn.bing.com/";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)
            ? absolute.AbsoluteUri
            : new Uri(new Uri("https://cn.bing.com/"), value).AbsoluteUri;
    }

    private static bool IsOnlineSource(GlanceBackgroundSource source)
    {
        return source is GlanceBackgroundSource.Online or GlanceBackgroundSource.Bing;
    }

    private static GlanceOnlineImageProvider GetOnlineProvider(GlanceBackgroundSource source)
    {
        return source == GlanceBackgroundSource.Bing
            ? GlanceOnlineImageProvider.Bing
            : GlanceOnlineImageProvider.Wikimedia;
    }

    private static GlanceOnlineImageCategory GetOnlineCategory(
        GlanceBackgroundSource source,
        GlanceOnlineImageCategory category)
    {
        return source == GlanceBackgroundSource.Bing
            ? GlanceOnlineImageCategory.Featured
            : category;
    }

    private static bool MatchesOnlineSource(
        GlanceImageInfo image,
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category)
    {
        return image.OnlineProvider == provider &&
            image.OnlineCategory == category;
    }

    private static bool CanUseBackgroundNetwork()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile?.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
            {
                return false;
            }

            NetworkCostType cost = profile.GetConnectionCost().NetworkCostType;
            return cost is NetworkCostType.Unrestricted or NetworkCostType.Unknown;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsSupportedImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";
    }

    private static bool IsUsableFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && IsSupportedImagePath(path) && File.Exists(path);
    }

    private static string CreateStableId(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long limit,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("The image exceeds the Glance cache item size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBox/1.4.6 (https://deskbox.fun)");
        return client;
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
