extern alias GlancePkg;

using System.Net;
using System.Text;
using System.Text.Json;
using ImageService = GlancePkg::DeskBox.Services.GlanceImageService;
using CatalogContext = GlancePkg::DeskBox.Services.GlanceImageCatalogJsonContext;
using ImageInfo = GlancePkg::DeskBox.Models.GlanceImageInfo;
using Settings = GlancePkg::DeskBox.Models.GlanceWidgetData;
using Source = GlancePkg::DeskBox.Models.GlanceBackgroundSource;
using Provider = GlancePkg::DeskBox.Models.GlanceOnlineImageProvider;
using Category = GlancePkg::DeskBox.Models.GlanceOnlineImageCategory;

namespace DeskBox.Tests;

public sealed class NativeGlanceImageServiceTests : IDisposable
{
    // Enough JPEG bytes for the service's header check; decoding belongs to rendering tests.
    private static readonly byte[] JpegBytes = [0xff, 0xd8, 0xff, 0xe0, 0, 16, 74, 70, 73, 70, 0, 1];
    private readonly string _root = Directory.CreateTempSubdirectory("DeskBox-native-glance-images-").FullName;
    private string Cache => Path.Combine(_root, "cache", "glance");
    private string Images => Path.Combine(Cache, "images");

    [Fact]
    public async Task BingParsesRelativeUrlsMetadataAndWallpaperPermissionAndDeduplicatesArchiveBatches()
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("HPImageArchive", StringComparison.Ordinal)
                ? Json("""
                    {"images":[
                      {"hsh":"allowed","url":"/th?id=allowed.jpg","title":"<b>A &amp; B</b>",
                       "copyright":"Place (© Photographer)","copyrightlink":"/search?q=place","wp":true},
                      {"hsh":"denied","url":"/th?id=denied.jpg","wp":false},
                      {"hsh":"file","url":"file:///C:/private.jpg"},
                      {"hsh":"missing"},null]}
                    """)
                : Bytes()));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        ImageInfo image = Assert.Single(await service.RefreshOnlineImagesAsync(new Settings
        {
            BackgroundSource = Source.Bing,
            OnlineImageCategory = Category.Animals
        }));

        Assert.Equal("A & B", image.Title);
        Assert.Equal("Place (© Photographer)", image.Author);
        Assert.Equal("Bing", image.License);
        Assert.Equal("https://www.microsoft.com/zh-cn/bing/bing-wallpaper", image.LicenseUrl);
        Assert.Equal("https://cn.bing.com/search?q=place", image.SourcePageUrl);
        Assert.Equal("https://cn.bing.com/th?id=allowed.jpg", image.RemoteImageUrl);
        Assert.Equal(1920, image.PixelWidth);
        Assert.Equal(1080, image.PixelHeight);
        Assert.Equal(Provider.Bing, image.OnlineProvider);
        Assert.Equal(Category.Featured, image.OnlineCategory);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("idx=16&n=8", StringComparison.Ordinal));
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(image.LocalPath));
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(Provider.Wikimedia, Category.Featured));

        // A fresh instance must deserialize package-generated metadata without reflection or host state.
        var offline = new ImageService(Cache, client, () => false);
        ImageInfo cached = Assert.Single(await offline.GetAvailableImagesAsync(new Settings { BackgroundSource = Source.Bing }));
        Assert.Equal(image.Id, cached.Id);
        Assert.Equal(image.Title, cached.Title);
        Assert.Equal(image.Author, cached.Author);
        Assert.Equal(image.LicenseUrl, cached.LicenseUrl);
        Assert.Equal(image.SourcePageUrl, cached.SourcePageUrl);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task WikimediaParsesDescriptionAndAttributionFallbackAndRejectsPortraitsAndUnsupportedMime()
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("list=categorymembers", StringComparison.Ordinal)
                ? Json("""{"query":{"categorymembers":[{"title":"File:landscape.jpg"},{"title":"Category:ignored"},null]}}""")
                : request.RequestUri.Query.Contains("prop=imageinfo", StringComparison.Ordinal)
                    ? Json("""
                        {"query":{"pages":[
                          {"imageinfo":[{"width":2400,"height":1200,"thumbwidth":1600,"thumbheight":800,
                            "mime":"image/jpeg","descriptionurl":"https://commons.wikimedia.org/wiki/File:landscape.jpg",
                            "url":"https://images.test/original.jpg","thumburl":"https://images.test/thumb.jpg",
                            "extmetadata":{"ImageDescription":{"value":"<p>Mountain &amp; lake</p>"},
                              "Attribution":{"value":"<a href='artist'>Example Artist</a>"},
                              "LicenseShortName":{"value":"CC BY-SA 4.0"},
                              "LicenseUrl":{"value":"https://creativecommons.org/licenses/by-sa/4.0/"}}}]},
                          {"imageinfo":[{"width":900,"height":1600,"mime":"image/jpeg"}]},
                          {"imageinfo":[{"width":1600,"height":900,"mime":"image/svg+xml"}]},
                          {"imageinfo":[{"width":1600,"height":900,"mime":"image/jpeg","thumburl":"file:///C:/private.jpg"}]},
                          {"imageinfo":[]},null]}}
                        """)
                    : Bytes()));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        ImageInfo image = Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Landscapes)));

        Assert.Equal("Mountain & lake", image.Title);
        Assert.Equal("Example Artist", image.Author);
        Assert.Equal("CC BY-SA 4.0", image.License);
        Assert.Equal("https://creativecommons.org/licenses/by-sa/4.0/", image.LicenseUrl);
        Assert.Equal("https://commons.wikimedia.org/wiki/File:landscape.jpg", image.SourcePageUrl);
        Assert.Equal("https://images.test/thumb.jpg", image.RemoteImageUrl);
        Assert.Equal(1600, image.PixelWidth);
        Assert.Equal(800, image.PixelHeight);
        Assert.Equal(Category.Landscapes, image.OnlineCategory);
        Assert.Equal(Provider.Wikimedia, image.OnlineProvider);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => Uri.UnescapeDataString(uri.Query).Contains(
            "Category:Featured pictures of landscapes", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath == "/original.jpg");
    }

    [Fact]
    public async Task SwitchingCategoriesPreservesTheSamePictureInBothAndNeverMixesBing()
    {
        using var handler = WikimediaHandler([new("shared.jpg")]);
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);
        ImageInfo city = Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Cities)));
        ImageInfo landscape = Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Landscapes)));

        Assert.Equal(city.Id, landscape.Id);
        Assert.Single(await service.GetAvailableImagesAsync(Online(Category.Cities)));
        Assert.Single(await service.GetAvailableImagesAsync(Online(Category.Landscapes)));
        Assert.Empty(await service.GetAvailableImagesAsync(new Settings { BackgroundSource = Source.Bing }));
        Assert.Empty(await service.GetAvailableImagesAsync(Online(Category.Animals)));
        Assert.Contains(handler.Requests, uri => Uri.UnescapeDataString(uri.Query).Contains(
            "Category:Quality images of cityscapes", StringComparison.Ordinal));
        int downloads = handler.Requests.Count(uri => uri.Host == "images.test");
        await service.RefreshOnlineImagesAsync(Online(Category.Cities));
        Assert.Equal(downloads, handler.Requests.Count(uri => uri.Host == "images.test"));
    }

    [Fact]
    public async Task OfflineAndMeteredPolicyReturnsOnlySelectedCacheWithoutHttp()
    {
        ImageInfo city = Seed("city", Provider.Wikimedia, Category.Cities);
        ImageInfo bing = Seed("bing", Provider.Bing, Category.Featured);
        ImageInfo missing = Seed("missing", Provider.Wikimedia, Category.Cities);
        File.Delete(missing.LocalPath);
        WriteCatalog([city, bing, missing]);
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No HTTP allowed"));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => false);

        Assert.Equal(city.Id, Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Cities))).Id);
        Assert.Equal(bing.Id, Assert.Single(await service.RefreshOnlineImagesAsync(new Settings
        {
            BackgroundSource = Source.Bing, OnlineImageCategory = Category.People
        })).Id);
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(Provider.Bing, Category.People));
        Assert.Empty(await service.GetAvailableImagesAsync(Online(Category.Featured)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NetworkPolicyIsRecheckedBeforeDownloads()
    {
        bool networkAllowed = true;
        using var handler = WikimediaHandler([new("new.jpg")], beforeResponse: request =>
        {
            if (request.RequestUri!.Query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                networkAllowed = false;
            }
        });
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => networkAllowed);

        Assert.Empty(await service.RefreshOnlineImagesAsync(Online(Category.Cities)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "images.test");
    }

    [Fact]
    public async Task RefreshExecutesPolicyAndHttpOnWorkerWithoutCallingSynchronizationContext()
    {
        SynchronizationContext? policyContext = new();
        SynchronizationContext? httpContext = new();
        using var handler = WikimediaHandler([], beforeResponse: _ => httpContext = SynchronizationContext.Current);
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () =>
        {
            policyContext = SynchronizationContext.Current;
            return true;
        });
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<IReadOnlyList<ImageInfo>> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            operation = service.RefreshOnlineImagesAsync(Online(Category.Cities));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.Empty(await operation);
        Assert.Single(handler.Requests);
        Assert.Null(policyContext);
        Assert.Null(httpContext);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task RemoteCatalogFailureIncludingHttpTimeoutReturnsOldCache(string failure)
    {
        ImageInfo old = Seed("old", Provider.Wikimedia, Category.Cities);
        WriteCatalog([old]);
        using var handler = new FakeHandler((_, _) => failure switch
        {
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP timeout")),
            "json" => Task.FromResult(Json("{broken")),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        });
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        Assert.Equal(old.Id, Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Cities))).Id);
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(old.LocalPath));
        Assert.Single(handler.Requests);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedDownloadsDoNotConsumeAnUnboundedNumberOfRequestsOrDiscardOldCache()
    {
        ImageInfo old = Seed("old", Provider.Wikimedia, Category.Cities);
        WriteCatalog([old]);
        using var handler = WikimediaHandler(Enumerable.Range(0, 8).Select(i => new Picture($"{i}.jpg")).ToArray(),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        Assert.Equal(old.Id, Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Cities))).Id);
        Assert.Equal(3, handler.Requests.Count(uri => uri.Host == "images.test"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task IncrementalDownloadBudgetStopsAtThreeAndTargetCatalogAtTwelve()
    {
        using var handler = WikimediaHandler(Enumerable.Range(0, 16).Select(i => new Picture($"{i}.jpg")).ToArray());
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        for (int refresh = 1; refresh <= 5; refresh++)
        {
            IReadOnlyList<ImageInfo> images = await service.RefreshOnlineImagesAsync(Online(Category.Cities));
            Assert.Equal(Math.Min(refresh * 3, 12), images.Count);
            Assert.Equal(Math.Min(refresh * 3, 12), handler.Requests.Count(uri => uri.Host == "images.test"));
        }
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("declared-size")]
    [InlineData("stream-size")]
    [InlineData("html")]
    [InlineData("fake-jpeg")]
    [InlineData("empty")]
    public async Task InvalidOrOversizedDownloadDoesNotReplaceOldCacheOrLeaveTemporaryFiles(string kind)
    {
        ImageInfo old = Seed("old", Provider.Wikimedia, Category.Cities);
        WriteCatalog([old]);
        using var handler = WikimediaHandler([new("invalid.jpg")], _ =>
        {
            HttpResponseMessage response = Bytes();
            switch (kind)
            {
                case "declared-size":
                    response.Content.Headers.ContentLength = 18L * 1024 * 1024 + 1;
                    break;
                case "stream-size":
                    response.Content.Dispose();
                    response.Content = new UnknownLengthContent(new MemoryStream(new byte[18 * 1024 * 1024 + 1]));
                    response.Content.Headers.ContentType = new("image/jpeg");
                    break;
                case "html":
                    response.Content.Headers.ContentType = new("text/html");
                    break;
                case "fake-jpeg":
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent("this is not a JPEG"u8.ToArray());
                    response.Content.Headers.ContentType = new("image/jpeg");
                    break;
                case "empty":
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent([]);
                    response.Content.Headers.ContentType = new("image/jpeg");
                    break;
            }
            return response;
        });
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        Assert.Equal(old.Id, Assert.Single(await service.RefreshOnlineImagesAsync(Online(Category.Cities))).Id);
        Assert.Single(Directory.EnumerateFiles(Images));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CancellationDuringDownloadPropagatesAndCleansTemporaryFileAndReleasesRefreshGate()
    {
        ImageInfo old = Seed("old", Provider.Wikimedia, Category.Cities);
        WriteCatalog([old]);
        using var cancellation = new CancellationTokenSource();
        using var handler = WikimediaHandler([new("cancel.jpg")], _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new CancelOnReadStream(cancellation))
            {
                Headers = { ContentType = new("image/jpeg") }
            }
        });
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshOnlineImagesAsync(Online(Category.Cities), cancellation.Token));
        Assert.Equal(old.Id, Assert.Single(await service.GetAvailableImagesAsync(Online(Category.Cities))).Id);
        AssertNoTemporaryFiles();
        using var recoveryHandler = WikimediaHandler([new("recovery.jpg")]);
        using var recoveryClient = new HttpClient(recoveryHandler);
        var recovery = new ImageService(Cache, recoveryClient, () => true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await recovery.RefreshOnlineImagesAsync(Online(Category.Cities), deadline.Token)).Count);
    }

    [Fact]
    public async Task BodyTimeoutAfterHeadersReturnsCacheAndRemovesPartialDownload()
    {
        ImageInfo old = Seed("old", Provider.Wikimedia, Category.Cities);
        WriteCatalog([old]);
        bool bodyReadStarted = false;
        using var handler = WikimediaHandler([new("slow.jpg")], _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new WaitingReadStream(() => bodyReadStarted = true))
            {
                Headers = { ContentType = new("image/jpeg") }
            }
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var service = new ImageService(Cache, client, () => true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.Equal(old.Id, Assert.Single(await service.RefreshOnlineImagesAsync(
            Online(Category.Cities), deadline.Token)).Id);
        Assert.True(bodyReadStarted);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{}")]
    public async Task UnreadableOrNullCatalogEntriesAreTreatedAsEmpty(string json)
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllText(Path.Combine(Cache, "catalog.json"), json);
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No HTTP allowed"));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => false);

        Assert.Empty(await service.GetAvailableImagesAsync(Online(Category.Cities)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task QuotasApplyPerProviderAndCategoryAndGloballyWithoutReadingOrDeletingOutsideCache()
    {
        var catalog = new List<ImageInfo>();
        foreach (Category category in Enum.GetValues<Category>())
        {
            for (int i = 0; i < 20; i++)
            {
                catalog.Add(Seed($"{category}-{i}", Provider.Wikimedia, category));
            }
        }
        for (int i = 0; i < 20; i++)
        {
            catalog.Add(Seed($"bing-{i}", Provider.Bing, Category.Featured));
        }
        string outside = Path.Combine(_root, "outside.jpg");
        File.WriteAllBytes(outside, JpegBytes);
        catalog.Add(new ImageInfo { Id = "outside", LocalPath = outside, OnlineCategory = Category.Cities });
        string sibling = Path.Combine(Cache, "images-other", "sibling.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllBytes(sibling, JpegBytes);
        catalog.Add(new ImageInfo { Id = "sibling", LocalPath = sibling, OnlineCategory = Category.Cities });
        WriteCatalog(catalog);
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"images\":[]}")));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);

        IReadOnlyList<ImageInfo> cities = await service.GetAvailableImagesAsync(Online(Category.Cities));
        Assert.Equal(18, cities.Count);
        Assert.DoesNotContain(cities, image => image.Id is "outside" or "sibling");
        await service.RefreshOnlineImagesAsync(new Settings { BackgroundSource = Source.Bing });
        List<ImageInfo> saved = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Cache, "catalog.json")),
            CatalogContext.Default.ImageCatalog)!;

        Assert.Equal(72, saved.Count);
        Assert.All(saved.GroupBy(image => (image.OnlineProvider, image.OnlineCategory)),
            group => Assert.InRange(group.Count(), 1, 18));
        Assert.Equal(72, Directory.EnumerateFiles(Images).Count());
        Assert.Equal(JpegBytes, File.ReadAllBytes(outside));
        Assert.Equal(JpegBytes, File.ReadAllBytes(sibling));
        Assert.True(service.GetCacheSizeBytes() > 0);
        await service.ClearCacheAsync();
        Assert.Empty(Directory.EnumerateFiles(Images));
        Assert.Equal(JpegBytes, File.ReadAllBytes(outside));
        Assert.Equal(JpegBytes, File.ReadAllBytes(sibling));
    }

    [Fact]
    public async Task LocalFilesAndFolderUseOnlySupportedExistingFilesAndNeverHttp()
    {
        string folder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        string photo = Path.Combine(folder, "lake.PNG");
        File.WriteAllBytes(photo, [137, 80, 78, 71, 13, 10, 26, 10]);
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [2]);
        File.WriteAllBytes(Path.Combine(folder, "invalid.jpg"), "not an image"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "nested", "hidden.jpg"), JpegBytes);
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No HTTP allowed"));
        using var client = new HttpClient(handler);
        var service = new ImageService(Cache, client, () => true);
        var settings = new Settings
        {
            BackgroundSource = Source.LocalFiles,
            LocalImagePaths = [photo, photo.ToUpperInvariant(), Path.Combine(folder, "missing.jpg"), Path.Combine(folder, "notes.txt")]
        };

        ImageInfo local = Assert.Single(await service.GetAvailableImagesAsync(settings));
        Assert.Equal(photo, local.LocalPath);
        Assert.Equal("lake", local.Title);
        Assert.False(local.IsOnline);
        Assert.Empty(await service.RefreshOnlineImagesAsync(settings));
        settings.BackgroundSource = Source.LocalFolder;
        settings.LocalFolderPath = folder;
        Assert.Equal(local.Id, Assert.Single(await service.GetAvailableImagesAsync(settings)).Id);
        settings.LocalFolderPath = Path.Combine(_root, "missing");
        Assert.Empty(await service.GetAvailableImagesAsync(settings));
        Assert.Empty(handler.Requests);
    }

    private static Settings Online(Category category) => new()
    {
        BackgroundSource = Source.Online, OnlineImageCategory = category
    };

    private ImageInfo Seed(string id, Provider provider, Category category)
    {
        Directory.CreateDirectory(Images);
        string path = Path.Combine(Images, id + ".jpg");
        File.WriteAllBytes(path, JpegBytes);
        return new ImageInfo
        {
            Id = id, LocalPath = path, OnlineProvider = provider, OnlineCategory = category,
            SourcePageUrl = "https://source.test/" + id, CachedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void WriteCatalog(List<ImageInfo> catalog)
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllText(Path.Combine(Cache, "catalog.json"),
            JsonSerializer.Serialize(catalog, CatalogContext.Default.ImageCatalog));
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(
        Directory.Exists(Cache) ? Directory.EnumerateFiles(Cache, "*.tmp", SearchOption.AllDirectories) : []);

    private sealed record Picture(string FileName);

    private static FakeHandler WikimediaHandler(
        Picture[] pictures,
        Func<HttpRequestMessage, HttpResponseMessage>? imageResponse = null,
        Action<HttpRequestMessage>? beforeResponse = null) => new((request, _) =>
    {
        beforeResponse?.Invoke(request);
        string query = request.RequestUri!.Query;
        if (query.Contains("list=categorymembers", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                query = new { categorymembers = pictures.Select(picture => new { title = "File:" + picture.FileName }) }
            })));
        }
        if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                query = new
                {
                    pages = pictures.Select(picture => new
                    {
                        imageinfo = new[]
                        {
                            new
                            {
                                width = 1600, height = 900, mime = "image/jpeg",
                                descriptionurl = "https://commons.wikimedia.org/wiki/File:" + picture.FileName,
                                url = "https://images.test/" + picture.FileName
                            }
                        }
                    })
                }
            })));
        }
        return Task.FromResult(imageResponse?.Invoke(request) ?? Bytes());
    });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JpegBytes) { Headers = { ContentType = new("image/jpeg") } }
    };

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return respond(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing) stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class WaitingReadStream(Action onRead) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
