using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.GlancePackage.Services;
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

/// <summary>
/// Package-owned image discovery and bounded online cache. Pass the package's
/// cache/glance directory; no host data-path or logging dependencies are used.
/// Discovery and refresh perform their filesystem/network work off the UI thread.
/// </summary>
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
    private const long MaximumMetadataBytes = 4L * 1024 * 1024;
    private static readonly SemaphoreSlim OnlineRefreshGate = new(1, 1);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly string _cacheDirectory;
    private readonly string _imageDirectory;
    private readonly string _catalogPath;
    private readonly HttpClient _httpClient;
    private readonly Func<bool> _canUseBackgroundNetwork;

    public GlanceImageService(string cacheDirectory)
        : this(cacheDirectory, SharedHttpClient, CanUseBackgroundNetwork)
    {
    }

    public GlanceImageService(
        string cacheDirectory,
        HttpClient httpClient,
        Func<bool> canUseBackgroundNetwork)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(canUseBackgroundNetwork);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _imageDirectory = Path.Combine(_cacheDirectory, "images");
        _catalogPath = Path.Combine(_cacheDirectory, "catalog.json");
        _httpClient = httpClient;
        _canUseBackgroundNetwork = canUseBackgroundNetwork;
    }

    public async Task<IReadOnlyList<GlanceImageInfo>> GetAvailableImagesAsync(
        GlanceWidgetData settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOnlineSource(settings.BackgroundSource))
        {
            return await LoadCachedOnlineImagesAsync(
                GetOnlineProvider(settings.BackgroundSource),
                GetOnlineCategory(settings.BackgroundSource, settings.OnlineImageCategory),
                cancellationToken);
        }

        GlanceBackgroundSource source = settings.BackgroundSource;
        string[] localPaths = settings.LocalImagePaths.ToArray();
        string? folderPath = settings.LocalFolderPath;
        return await Task.Run<IReadOnlyList<GlanceImageInfo>>(
            () => source == GlanceBackgroundSource.LocalFiles
                ? CreateLocalImages(localPaths, cancellationToken)
                : CreateFolderImages(folderPath, cancellationToken),
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

    public Task<IReadOnlyList<GlanceImageInfo>> LoadCachedOnlineImagesAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        // Even warm-cache checks touch disk; callers may be on the WinUI dispatcher.
        return Task.Run(() => LoadCachedOnlineImagesCoreAsync(provider, category, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<GlanceImageInfo>> LoadCachedOnlineImagesCoreAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<GlanceImageInfo> catalog = await LoadCatalogAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return catalog
            .Where(image => MatchesOnlineSource(image, provider, category) && IsUsableCachedFile(image.LocalPath))
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOnlineSource(settings.BackgroundSource))
        {
            return Task.FromResult<IReadOnlyList<GlanceImageInfo>>([]);
        }

        return RefreshOnlineImagesAsync(
            GetOnlineProvider(settings.BackgroundSource),
            GetOnlineCategory(settings.BackgroundSource, settings.OnlineImageCategory),
            cancellationToken);
    }

    public Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RefreshOnlineImagesCoreAsync(provider, category, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineImagesCoreAsync(
        GlanceOnlineImageProvider provider,
        GlanceOnlineImageCategory category,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_canUseBackgroundNetwork())
        {
            return await LoadCachedOnlineImagesCoreAsync(provider, category, cancellationToken);
        }

        await OnlineRefreshGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeCacheDirectory();
            EnsureBackgroundNetworkAllowed();
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
            EnsureSafeCacheDirectory();
            Directory.CreateDirectory(_imageDirectory);

            int downloadAttempts = 0;
            int usableCategoryCount = cached.Count(image =>
                MatchesOnlineSource(image, provider, category) && IsUsableCachedFile(image.LocalPath));
            int downloadLimit = Math.Min(
                IncrementalDownloadCount,
                Math.Max(0, TargetCatalogSizePerCategory - usableCategoryCount));
            foreach (GlanceImageInfo candidate in remote)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bySource.TryGetValue(candidate.SourcePageUrl ?? string.Empty, out GlanceImageInfo? existing) &&
                    IsUsableCachedFile(existing.LocalPath))
                {
                    continue;
                }

                if (downloadAttempts >= downloadLimit)
                {
                    break;
                }

                GlanceImageInfo? downloaded;
                downloadAttempts++;
                try
                {
                    downloaded = await DownloadAsync(candidate, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PackageLogger.LogVerbose(
                        $"[GlanceImageService] Image download failed for " +
                        $"'{candidate.SourcePageUrl ?? candidate.RemoteImageUrl}': {ex}");
                    continue;
                }

                if (downloaded is not null)
                {
                    // The same Commons picture can belong to multiple categories.
                    cached.RemoveAll(image => MatchesOnlineSource(image, provider, category) &&
                        string.Equals(image.Id, downloaded.Id, StringComparison.Ordinal));
                    cached.Add(downloaded);
                }
            }

            cached = cached
                .Where(image => IsUsableCachedFile(image.LocalPath))
                .OrderByDescending(image => image.CachedAtUtc)
                .ToList();
            await TrimAndSaveCatalogAsync(cached, cancellationToken);
            return cached
                .Where(image => MatchesOnlineSource(image, provider, category))
                .Take(MaximumCacheItemsPerCategory)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PackageLogger.LogVerbose($"[GlanceImageService] Online refresh failed: {ex}");
            return await LoadCachedOnlineImagesCoreAsync(provider, category, cancellationToken);
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
            EnsureSafeCacheDirectory();
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
            EnsureSafeCacheDirectory();
            return !Directory.Exists(_cacheDirectory)
                ? 0
                : Directory.EnumerateFiles(_cacheDirectory)
                    .Concat(Directory.Exists(_imageDirectory) ? Directory.EnumerateFiles(_imageDirectory) : [])
                    .Where(path => !HasReparsePoint(path))
                    .Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static List<GlanceImageInfo> CreateLocalImages(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var results = new List<GlanceImageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (IsUsableFile(path))
                {
                    string fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                    {
                        results.Add(CreateLocalImage(fullPath));
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                PackageLogger.LogVerbose($"[GlanceImageService] Local image unavailable: {ex.Message}");
            }
        }
        return results;
    }

    private static List<GlanceImageInfo> CreateFolderImages(string? folderPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return [];
        }

        try
        {
            return CreateLocalImages(Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PackageLogger.LogVerbose($"[GlanceImageService] Local folder enumeration failed: {ex}");
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
        try
        {
            EnsureSafeCacheDirectory();
            if (!File.Exists(_catalogPath))
            {
                return [];
            }
            string json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
            List<GlanceImageInfo> catalog = JsonSerializer.Deserialize(
                       json,
                       GlanceImageCatalogJsonContext.Default.ImageCatalog) ??
                   [];
            // An imported/corrupt catalog is data, never authority to read or delete arbitrary files.
            var usable = new List<GlanceImageInfo>();
            foreach (GlanceImageInfo image in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (image is not null && IsUsableCachedFile(image.LocalPath))
                {
                    image.LocalPath = Path.GetFullPath(image.LocalPath);
                    usable.Add(image);
                }
            }
            return usable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PackageLogger.LogVerbose($"[GlanceImageService] Catalog load failed: {ex}");
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
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("images", out JsonElement images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement image in images.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (image.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
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
                string remoteImageUrl = ToAbsoluteBingUrl(relativeImageUrl);
                if (!IsHttpUrl(remoteImageUrl))
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
                    RemoteImageUrl = remoteImageUrl,
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
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("query", out JsonElement query) &&
            query.ValueKind == JsonValueKind.Object &&
            query.TryGetProperty("categorymembers", out JsonElement members) &&
            members.ValueKind == JsonValueKind.Array)
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
                if (page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("imageinfo", out JsonElement infos) ||
                    infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0 ||
                    infos[0].ValueKind != JsonValueKind.Object)
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
                string? remoteImage = GetString(info, "thumburl") ?? GetString(info, "url");
                if (!IsHttpUrl(sourcePage) || !IsHttpUrl(remoteImage))
                {
                    continue;
                }
                results.Add(new GlanceImageInfo
                {
                    Id = CreateStableId(sourcePage),
                    Title = CleanMetadata(GetMetadata(metadata, "ObjectName") ?? GetMetadata(metadata, "ImageDescription")),
                    Author = CleanMetadata(GetMetadata(metadata, "Artist") ?? GetMetadata(metadata, "Attribution")),
                    License = CleanMetadata(GetMetadata(metadata, "LicenseShortName")),
                    LicenseUrl = GetMetadata(metadata, "LicenseUrl"),
                    SourcePageUrl = sourcePage,
                    RemoteImageUrl = remoteImage,
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
        if (!IsHttpUrl(candidate.RemoteImageUrl))
        {
            return null;
        }

        EnsureSafeCacheDirectory();
        EnsureBackgroundNetworkAllowed();
        // ResponseHeadersRead does not apply HttpClient.Timeout to the response body.
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_httpClient.Timeout);
        cancellationToken = requestCancellation.Token;
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
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => string.Empty
        };
        if (extension.Length == 0)
        {
            return null;
        }
        string destination = Path.Combine(_imageDirectory, $"{candidate.Id}{extension}");
        if (HasReparsePoint(destination))
        {
            return null;
        }
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

            if (!HasImageHeader(temporary, extension))
            {
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeCacheDirectory();
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
            .Where(image => IsUsableCachedFile(image.LocalPath))
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
        catalog.Clear();
        catalog.AddRange(kept);

        EnsureSafeCacheDirectory();
        Directory.CreateDirectory(_cacheDirectory);
        string json = JsonSerializer.Serialize(
            catalog,
            GlanceImageCatalogJsonContext.Default.ImageCatalog);
        string temporary = $"{_catalogPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeCacheDirectory();
            File.Move(temporary, _catalogPath, overwrite: true);
            // Publish first: a failed save must leave the previous catalog's files intact.
            // Also collect completed downloads orphaned by a previously cancelled refresh.
            foreach (string path in Directory.EnumerateFiles(_imageDirectory))
            {
                if (IsSupportedImagePath(path) && !keptPaths.Contains(path))
                {
                    TryDelete(path);
                }
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        EnsureBackgroundNetworkAllowed();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_httpClient.Timeout);
        cancellationToken = requestCancellation.Token;
        using HttpResponseMessage response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
        {
            throw new InvalidDataException("The Glance remote catalog exceeds the metadata size limit.");
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(stream, buffer, MaximumMetadataBytes, cancellationToken);
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static IEnumerable<JsonElement> GetPages(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("query", out JsonElement query) &&
            query.ValueKind == JsonValueKind.Object &&
            query.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array)
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
               item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty("value", out JsonElement value)
            ? value.ToString()
            : null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : fallback;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
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

        // Root-relative Bing URLs must not be interpreted as local file URIs on Windows.
        return Uri.TryCreate(new Uri("https://cn.bing.com/"), value, out Uri? absolute) &&
               IsHttpUrl(absolute.AbsoluteUri)
            ? absolute.AbsoluteUri
            : string.Empty;
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
            return false;
        }
    }

    private void EnsureBackgroundNetworkAllowed()
    {
        if (!_canUseBackgroundNetwork())
        {
            throw new InvalidOperationException("Background network access is unavailable or metered.");
        }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private void EnsureSafeCacheDirectory()
    {
        if (HasReparsePoint(_imageDirectory) || HasReparsePoint(_catalogPath))
        {
            throw new IOException("The Glance cache must not traverse reparse points.");
        }
    }

    private bool IsUsableCachedFile(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _imageDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).Contains(':') &&
                !HasReparsePoint(path) && IsUsableFile(path) &&
                new FileInfo(path).Length is > 0 and <= MaximumDownloadBytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
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
        return !string.IsNullOrWhiteSpace(path) && IsSupportedImagePath(path) && File.Exists(path) &&
            HasImageHeader(path, Path.GetExtension(path));
    }

    // Lightweight validation on the worker, not a UI-thread decoder. The renderer
    // still handles decode failures (including a file disappearing after discovery).
    private static bool HasImageHeader(string path, string extension)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            int length = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
                ".png" => length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                ".webp" => length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
                ".bmp" => length >= 2 && header[..2].SequenceEqual("BM"u8),
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBox/1.4.8 (https://deskbox.fun)");
        return client;
    }

    private void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _cacheDirectory,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _imageDirectory,
                     StringComparison.OrdinalIgnoreCase)) &&
                !HasReparsePoint(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
