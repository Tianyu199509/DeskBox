using System.Net;
using System.Text.Json;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Queries the official public LRCLIB API (<c>https://lrclib.net/api/search</c>) identically across Store and Direct builds.
/// </summary>
public sealed class LrclibLyricsProvider
{
    private const string SearchEndpoint = "https://lrclib.net/api/search";
    private const string ClientUserAgent = "DeskBox/1.6.0 (https://deskbox.fun)";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;

    public LrclibLyricsProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <summary>
    /// Searches LRCLIB for the best matching track. Returns <c>(MatchedCandidate, FetchSucceeded)</c>;
    /// when <c>FetchSucceeded</c> is <see langword="false"/> (network offline or timeout), callers must not negative-cache.
    /// </summary>
    public async Task<(LrclibTrackDto? Match, bool FetchSucceeded)> SearchBestMatchAsync(
        LyricsQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid)
        {
            return (null, true);
        }

        string rawTitle = query.Title.Trim();
        string rawArtist = query.Artist.Trim();
        string stage1Url = BuildTrackSearchUrl(rawTitle, rawArtist);

        (LrclibTrackDto[]? stage1Results, bool stage1Ok) = await FetchCandidatesAsync(stage1Url, cancellationToken)
            .ConfigureAwait(false);
        if (!stage1Ok)
        {
            return (null, false);
        }

        LrclibTrackDto? best = null;
        double bestScore = 0.0;
        if (stage1Results is { Length: > 0 })
        {
            best = LyricTrackMatcher.SelectBestCandidate(stage1Results, query, out bestScore);
            if (best is not null && bestScore >= 0.82)
            {
                return (best, true);
            }
        }

        string cleanTitle = LyricTrackMatcher.CleanTitle(rawTitle);
        string primaryArtist = LyricTrackMatcher.ExtractPrimaryArtist(rawArtist);
        bool differsFromRaw = !string.Equals(cleanTitle, rawTitle, StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(primaryArtist, rawArtist, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(cleanTitle) && (differsFromRaw || stage1Results is not { Length: > 0 }))
        {
            string stage2Query = string.IsNullOrWhiteSpace(primaryArtist)
                ? cleanTitle
                : $"{cleanTitle} {primaryArtist}";
            string stage2Url = $"{SearchEndpoint}?q={Uri.EscapeDataString(stage2Query)}";

            (LrclibTrackDto[]? stage2Results, bool stage2Ok) = await FetchCandidatesAsync(stage2Url, cancellationToken)
                .ConfigureAwait(false);
            if (stage2Ok && stage2Results is { Length: > 0 })
            {
                LrclibTrackDto? stage2Best = LyricTrackMatcher.SelectBestCandidate(
                    stage2Results,
                    query,
                    out double stage2Score);
                if (stage2Best is not null && stage2Score > bestScore)
                {
                    best = stage2Best;
                }
            }
        }

        return (best, true);
    }

    private async Task<(LrclibTrackDto[]? Candidates, bool Succeeded)> FetchCandidatesAsync(
        string requestUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ([], true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            LrclibTrackDto[]? items = await JsonSerializer.DeserializeAsync(
                    stream, LyricsJsonContext.Default.LrclibTrackDtoArray, cancellationToken)
                .ConfigureAwait(false);
            return (items ?? [], true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Offline, DNS failure, TLS error, or 4s request timeout.
            return (null, false);
        }
    }

    private static string BuildTrackSearchUrl(string title, string artist)
    {
        string encodedTitle = Uri.EscapeDataString(title);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return $"{SearchEndpoint}?track_name={encodedTitle}";
        }

        return $"{SearchEndpoint}?track_name={encodedTitle}&artist_name={Uri.EscapeDataString(artist)}";
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientUserAgent);
        return client;
    }
}
