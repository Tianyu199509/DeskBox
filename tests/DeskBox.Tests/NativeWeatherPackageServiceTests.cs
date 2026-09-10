extern alias WeatherPkg;
using System.Net;
using System.Net.Http;
using P = WeatherPkg::DeskBox.WeatherPackage.Services;
using M = WeatherPkg::DeskBox.WeatherPackage.Models;

namespace DeskBox.Tests;

public sealed class NativeWeatherPackageServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskbox-weather-tests-").FullName;
    private P.WeatherCacheStore Store() => new(Path.Combine(_root, "cache.json"));
    private const string Forecast = """{"latitude":39.9,"longitude":116.4,"current":{"temperature_2m":23,"relative_humidity_2m":65,"weather_code":1,"is_day":1}}""";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task PreferredSourceFailureUsesOtherSourceAndRecordsProvenance()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "api.msn.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Forecast));
        }));
        using var service = new P.WeatherService(Store(), client);
        var result = await service.GetWeatherAsync(39.9, 116.4, "北京", dataSource: "MSN");
        Assert.NotNull(result);
        Assert.Equal(23, result.Current!.Temperature);
        Assert.True(result.IsFallback);
        Assert.Equal(new[] { "api.msn.com", "api.open-meteo.com" }, hosts);
        var restored = await Store().LoadAsync();
        Assert.Equal("MSN", restored.LastForecast!.RequestedSource);
        Assert.Equal("OpenMeteo", restored.LastForecast.ActualSource);
    }

    [Fact]
    public async Task ConcurrentInstancesShareFreshForecastFetch()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await release.Task.WaitAsync(cancellation);
            return Json(Forecast);
        }));
        using var service = new P.WeatherService(Store(), client);
        var a = service.GetWeatherAsync(39.9, 116.4, dataSource: "OpenMeteo");
        await started.Task;
        var b = service.GetWeatherAsync(39.9, 116.4, dataSource: "OpenMeteo");
        release.SetResult();
        var values = await Task.WhenAll(a, b);
        Assert.All(values, result => Assert.Equal(23, result!.Current!.Temperature));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellationDoesNotFallbackOrCommitPartialCache()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json(Forecast);
        }));
        using var service = new P.WeatherService(Store(), client);
        using var cancel = new CancellationTokenSource();
        var running = service.GetWeatherAsync(39.9, 116.4, dataSource: "MSN", cancellationToken: cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, requests);
        Assert.Null((await Store().LoadAsync()).LastForecast);
    }

    [Fact]
    public async Task SessionShutdownCancelsOutstandingNetwork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json(Forecast);
        }));
        using var service = new P.WeatherService(Store(), client);
        var running = service.GetWeatherAsync(39.9, 116.4, dataSource: "OpenMeteo");
        await started.Task;
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetWeatherAsync(39.9, 116.4));
    }

    [Fact]
    public async Task OfflineCacheNeverShowsAnotherCityAsCurrentLocation()
    {
        var store = Store();
        await Seed(store, DateTimeOffset.UtcNow.AddDays(-1));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var service = new P.WeatherService(store, client);
        var stale = await service.GetWeatherAsync(39.9, 116.4, dataSource: "MSN");
        Assert.True(stale!.IsStale);
        Assert.Null(await service.GetWeatherAsync(31.2, 121.5, "上海", dataSource: "MSN"));
    }

    [Fact]
    public async Task FreshDiskCacheNeedsNoNetworkAndKeepsLocationAge()
    {
        var store = Store();
        await Seed(store, DateTimeOffset.UtcNow);
        DateTimeOffset resolved = DateTimeOffset.UtcNow.AddHours(-2);
        await store.SaveLocationAsync(new P.WeatherCachedLocation { Latitude = 39.9, Longitude = 116.4, Name = "北京", ResolvedAtUtc = resolved });
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Network must not run")));
        using var service = new P.WeatherService(store, client);
        var result = await service.GetWeatherAsync(39.9, 116.4, dataSource: "MSN");
        Assert.Equal(23, result!.Current!.Temperature);
        Assert.Equal(resolved, (await Store().LoadAsync()).LastLocation!.ResolvedAtUtc);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversBackupAndRestoresReadablePrimary()
    {
        var store = Store();
        await Seed(store, DateTimeOffset.UtcNow);
        File.Copy(store.StorePath, store.StorePath + ".bak");
        await File.WriteAllTextAsync(store.StorePath, "{torn");
        var restored = await Store().LoadAsync();
        Assert.Equal(23, restored.LastForecast!.Data!.Current!.Temperature);
        Assert.NotNull(P.WeatherService.DeserializeCacheState(await File.ReadAllTextAsync(store.StorePath)));
    }

    [Theory]
    [InlineData("zh-CN", "北京")]
    [InlineData("zh-TW", "臺北")]
    [InlineData("en-US", "Paris")]
    public void CityResourceResolvesFromPackageAssembly(string locale, string query)
    {
        var results = P.CitySearchService.SearchLocal(query, locale == "en-US", P.PackageLocalization.IsTraditionalChineseCulture(locale));
        Assert.NotEmpty(results);
    }

    private static Task<bool> Seed(P.WeatherCacheStore store, DateTimeOffset fetched) => store.SaveForecastAsync(new P.WeatherCachedForecast
    {
        Latitude = 39.9, Longitude = 116.4, LocationName = "北京", RequestedSource = "MSN", ActualSource = "MSN", FetchedAtUtc = fetched,
        Data = new M.WeatherData { Latitude = 39.9, Longitude = 116.4, Current = new M.WeatherCurrent { Temperature = 23, WeatherCode = 1 } }
    });
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
