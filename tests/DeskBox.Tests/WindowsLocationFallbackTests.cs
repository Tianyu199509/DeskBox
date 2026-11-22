using System.Net;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsLocationFallbackTests
{
    [Fact]
    public async Task ResolveIpLocationCoreAsync_UsesHttpsOnlyAndStopsAfterPrimarySuccess()
    {
        var handler = new StubLocationHandler(request =>
        {
            if (request.RequestUri?.Host == "api.ip.sb")
            {
                return JsonResponse("""{"latitude":39.9042,"longitude":116.4074,"city":"Beijing"}""");
            }

            throw new InvalidOperationException($"Unexpected fallback call to {request.RequestUri}");
        });
        using var httpClient = new HttpClient(handler);

        var zhLoc = TestServices.CreateLocalizationService(SettingsService.LanguageChinese);
        var zhResult = await WindowsLocationHelper.ResolveIpLocationCoreAsync(httpClient, zhLoc);

        Assert.NotNull(zhResult);
        Assert.Equal(39.9042, zhResult.Value.Lat, precision: 4);
        Assert.Equal(116.4074, zhResult.Value.Lon, precision: 4);
        Assert.Equal("北京", zhResult.Value.Name);

        Uri singleRequest = Assert.Single(handler.RecordedUris);
        Assert.Equal(Uri.UriSchemeHttps, singleRequest.Scheme);
        Assert.NotEqual("ip-api.com", singleRequest.Host);
        Assert.Equal("api.ip.sb", singleRequest.Host);

        handler.RecordedUris.Clear();

        var enLoc = TestServices.CreateLocalizationService(SettingsService.LanguageEnglish);
        var enResult = await WindowsLocationHelper.ResolveIpLocationCoreAsync(httpClient, enLoc);

        Assert.NotNull(enResult);
        Assert.Equal("Beijing", enResult.Value.Name);
        Uri secondRunRequest = Assert.Single(handler.RecordedUris);
        Assert.Equal(Uri.UriSchemeHttps, secondRunRequest.Scheme);
        Assert.NotEqual("ip-api.com", secondRunRequest.Host);
    }

    [Fact]
    public async Task ResolveIpLocationCoreAsync_FallsBackToSecondaryHttpsProviderWhenPrimaryFailsOrTimesOut()
    {
        // Case 1: Primary returns HTTP 503, secondary HTTPS provider succeeds.
        var failureHandler = new StubLocationHandler(request =>
        {
            if (request.RequestUri?.Host == "api.ip.sb")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (request.RequestUri?.Host == "ipapi.co")
            {
                return JsonResponse("""{"latitude":31.2304,"longitude":121.4737,"city":"Shanghai"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var failureClient = new HttpClient(failureHandler);

        var zhLoc = TestServices.CreateLocalizationService(SettingsService.LanguageChinese);
        var fallbackFromHttpError = await WindowsLocationHelper.ResolveIpLocationCoreAsync(
            failureClient,
            zhLoc);

        Assert.NotNull(fallbackFromHttpError);
        Assert.Equal(31.2304, fallbackFromHttpError.Value.Lat, precision: 4);
        Assert.Equal(121.4737, fallbackFromHttpError.Value.Lon, precision: 4);
        Assert.Equal("上海", fallbackFromHttpError.Value.Name);
        Assert.Equal(2, failureHandler.RecordedUris.Count);
        Assert.Equal("api.ip.sb", failureHandler.RecordedUris[0].Host);
        Assert.Equal("ipapi.co", failureHandler.RecordedUris[1].Host);
        Assert.All(failureHandler.RecordedUris, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));

        // Case 2: Primary stalls past providerTimeout, secondary HTTPS provider succeeds.
        var timeoutHandler = new StubLocationHandler(async (request, ct) =>
        {
            if (request.RequestUri?.Host == "api.ip.sb")
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return JsonResponse("""{"latitude":39.9042,"longitude":116.4074,"city":"Beijing"}""");
            }

            if (request.RequestUri?.Host == "ipapi.co")
            {
                return JsonResponse("""{"latitude":31.2304,"longitude":121.4737,"city":"Shanghai"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var timeoutClient = new HttpClient(timeoutHandler);

        var enLoc = TestServices.CreateLocalizationService(SettingsService.LanguageEnglish);
        var fallbackFromTimeout = await WindowsLocationHelper.ResolveIpLocationCoreAsync(
            timeoutClient,
            enLoc,
            providerTimeout: TimeSpan.FromMilliseconds(60));

        Assert.NotNull(fallbackFromTimeout);
        Assert.Equal(31.2304, fallbackFromTimeout.Value.Lat, precision: 4);
        Assert.Equal(121.4737, fallbackFromTimeout.Value.Lon, precision: 4);
        Assert.Equal("Shanghai", fallbackFromTimeout.Value.Name);
        Assert.Equal(2, timeoutHandler.RecordedUris.Count);
        Assert.Equal("api.ip.sb", timeoutHandler.RecordedUris[0].Host);
        Assert.Equal("ipapi.co", timeoutHandler.RecordedUris[1].Host);
        Assert.All(timeoutHandler.RecordedUris, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));
    }

    [Fact]
    public async Task ResolveIpLocationCoreAsync_RejectsErrorPayloadsAndInvalidCoordinates()
    {
        var handler = new StubLocationHandler(request =>
        {
            if (request.RequestUri?.Host == "api.ip.sb")
            {
                return JsonResponse("""{"latitude":999,"longitude":0,"city":"Invalid"}""");
            }

            if (request.RequestUri?.Host == "ipapi.co")
            {
                return JsonResponse("""{"error":true,"reason":"RateLimited","latitude":35.6762,"longitude":139.6503}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler);

        var result = await WindowsLocationHelper.ResolveIpLocationCoreAsync(
            httpClient,
            TestServices.CreateLocalizationService(SettingsService.LanguageEnglish));

        Assert.Null(result);
        Assert.Equal(2, handler.RecordedUris.Count);
        Assert.All(handler.RecordedUris, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));
    }

    [Fact]
    public void NormalizeLocationName_PreservesProviderCityOutsideLocalDatabaseRadius()
    {
        var zhLoc = TestServices.CreateLocalizationService(SettingsService.LanguageChinese);
        var enLoc = TestServices.CreateLocalizationService(SettingsService.LanguageEnglish);

        string preservedZh = WindowsLocationHelper.NormalizeLocationName(
            lat: -54.8019,
            lon: -68.3030,
            rawName: "Ushuaia",
            zhLoc);
        Assert.Equal("Ushuaia", preservedZh);

        string fallbackZh = WindowsLocationHelper.NormalizeLocationName(
            lat: -54.8019,
            lon: -68.3030,
            rawName: "   ",
            zhLoc);
        Assert.Equal(zhLoc.T("Weather.CurrentLocation"), fallbackZh);

        string fallbackEn = WindowsLocationHelper.NormalizeLocationName(
            lat: -54.8019,
            lon: -68.3030,
            rawName: null,
            enLoc);
        Assert.Equal("Current Location", fallbackEn);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubLocationHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubLocationHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public StubLocationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<Uri> RecordedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                RecordedUris.Add(request.RequestUri);
            }

            return _responder(request, cancellationToken);
        }
    }
}
