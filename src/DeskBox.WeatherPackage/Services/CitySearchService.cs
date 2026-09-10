using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.WeatherPackage.Helpers;
using DeskBox.WeatherPackage.Models;

namespace DeskBox.WeatherPackage.Services;

/// <summary>
/// Pre-defined city entry loaded from the embedded cities.json resource.
/// </summary>
internal sealed class PredefinedCity
{
    [JsonPropertyName("zh")]
    public string Zh { get; set; } = string.Empty;

    [JsonPropertyName("en")]
    public string En { get; set; } = string.Empty;

    [JsonPropertyName("pinyin")]
    public string Pinyin { get; set; } = string.Empty;

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("country_zh")]
    public string CountryZh { get; set; } = string.Empty;

    [JsonPropertyName("country_en")]
    public string CountryEn { get; set; } = string.Empty;

    [JsonPropertyName("admin1_zh")]
    public string Admin1Zh { get; set; } = string.Empty;

    [JsonPropertyName("admin1_en")]
    public string Admin1En { get; set; } = string.Empty;
}

/// <summary>
/// Unified city search service that merges a local pre-defined city list
/// (embedded in the assembly) with dynamic results from the Open-Meteo
/// geocoding API. Supports location-based "nearby popular cities" by
/// sorting the local list by haversine distance to the user's coordinates.
/// </summary>
public sealed class CitySearchService : IDisposable
{
    private static List<PredefinedCity>? s_predefined;
    private static readonly object s_lock = new();

    private static List<PredefinedCity> Predefined
    {
        get
        {
            if (s_predefined is not null)
            {
                return s_predefined;
            }

            lock (s_lock)
            {
                if (s_predefined is not null)
                {
                    return s_predefined;
                }

                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "DeskBox.WeatherPackage.Assets.Cities.cities.json";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    PackageLogger.Log("[CitySearchService] Embedded cities.json not found");
                    s_predefined = [];
                    return s_predefined;
                }

                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                s_predefined = JsonSerializer.Deserialize(
                    json,
                    WeatherJsonContext.Default.PredefinedCityList) ?? [];
                PackageLogger.Log($"[CitySearchService] Loaded {s_predefined.Count} predefined cities");
                return s_predefined;
            }
        }
    }

    private readonly WeatherService _weatherService;

    public CitySearchService(WeatherService weatherService)
    {
        _weatherService = weatherService;
    }

    /// <summary>
    /// P2-1: Reverse-geocode coordinates to the nearest known city name
    /// from the local database. Returns null if no city is within maxDistanceKm.
    /// Used to normalize IP-location city names to match the local database.
    /// </summary>
    public static string? GetNearestCityName(
        double lat, double lon, string language = "zh", double maxDistanceKm = 80)
    {
        bool useChinese = IsChineseLanguage(language);
        bool useTraditional = PackageLocalization.IsTraditionalChineseCulture(language);
        PredefinedCity? best = null;
        double bestDist = double.MaxValue;

        foreach (var c in Predefined)
        {
            double d = HaversineDistance(lat, lon, c.Lat, c.Lon);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        if (best is null || bestDist > maxDistanceKm)
        {
            return null;
        }

        if (!useChinese)
        {
            return best.En;
        }

        return LocalizeChineseText(best.Zh, useTraditional);
    }

    /// <summary>
    /// Search cities by query string.
    /// Returns merged results from the local pre-defined list (instant)
    /// and the Open-Meteo geocoding API (broader coverage).
    /// Results are deduplicated by coordinate proximity.
    /// Text relevance and trusted local matches are always ranked before
    /// optional proximity, which is used only as a tie-breaker.
    /// </summary>
    public async Task<List<WeatherCitySearchResult>> SearchAsync(
        string query,
        string language = "zh",
        double? userLat = null,
        double? userLon = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        query = query.Trim();
        if (NormalizeSearchText(query).Length == 0)
        {
            return [];
        }

        // P1-1: Allow single CJK character search (e.g. "京" → 北京).
        // Latin queries still require at least 2 characters.
        bool hasCjk = query.Any(c => c >= '\u4e00' && c <= '\u9fff');
        if (!hasCjk && query.Length < 2)
        {
            return [];
        }

        bool isEn = !IsChineseLanguage(language);
        bool useTraditional = PackageLocalization.IsTraditionalChineseCulture(language);

        // 1. Search local predefined cities (instant, no network)
        var localResults = SearchLocal(query, isEn, useTraditional);

        // 2. Search via Open-Meteo API (parallel, with cancellation)
        List<WeatherGeocodingItem>? apiResults = null;
        try
        {
            apiResults = await _weatherService.SearchCityAsync(query, language, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            PackageLogger.Log($"[CitySearchService] API search failed: {ex.Message}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        // 3. Merge & deduplicate
        var merged = new List<CitySearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sequence = 0;

        // Add local results first (they have proper zh/en names)
        foreach (var r in localResults)
        {
            var key = $"{r.Latitude:F2},{r.Longitude:F2}";
            if (seen.Add(key))
            {
                merged.Add(new CitySearchCandidate(
                    r,
                    GetLocalResultRelevance(r, query),
                    IsLocal: true,
                    Sequence: sequence++));
            }
        }

        // Add API results not already in the list
        if (apiResults is not null)
        {
            foreach (var item in apiResults)
            {
                if (!IsValidCoordinate(item.Latitude, item.Longitude))
                {
                    continue;
                }

                var key = $"{item.Latitude:F2},{item.Longitude:F2}";
                if (seen.Add(key))
                {
                    string name = LocalizeChineseText(item.Name, useTraditional);
                    string admin1 = LocalizeChineseText(item.Admin1, useTraditional);
                    string country = LocalizeChineseText(item.Country, useTraditional);
                    var result = new WeatherCitySearchResult
                    {
                        Name = name,
                        DisplayName = BuildDisplayNameFromParts(name, admin1, country),
                        Latitude = item.Latitude,
                        Longitude = item.Longitude,
                        Country = country,
                        Admin1 = admin1
                    };
                    merged.Add(new CitySearchCandidate(
                        result,
                        GetResultRelevance(result, query),
                        IsLocal: false,
                        Sequence: sequence++));
                }
            }
        }

        return merged
            .OrderByDescending(candidate => candidate.Relevance)
            .ThenByDescending(candidate => candidate.IsLocal)
            .ThenBy(candidate =>
                userLat.HasValue && userLon.HasValue
                    ? HaversineDistance(
                        userLat.Value,
                        userLon.Value,
                        candidate.Result.Latitude,
                        candidate.Result.Longitude)
                    : double.MaxValue)
            .ThenBy(candidate => candidate.Sequence)
            .Take(10)
            .Select(candidate => candidate.Result)
            .ToList();
    }

    /// <summary>
    /// Get nearby popular cities sorted by haversine distance to the given coordinates.
    /// Falls back to a general global list if no coordinates are provided.
    /// </summary>
    public List<WeatherCitySearchResult> GetNearbyPopularCities(
        double? lat = null,
        double? lon = null,
        string language = "zh",
        int maxCount = 8)
    {
        bool isEn = !IsChineseLanguage(language);
        bool useTraditional = PackageLocalization.IsTraditionalChineseCulture(language);

        IEnumerable<PredefinedCity> cities = Predefined;

        if (lat.HasValue && lon.HasValue)
        {
            cities = cities
                .OrderBy(c => HaversineDistance(lat.Value, lon.Value, c.Lat, c.Lon));
        }

        return cities
            .Take(maxCount)
            .Select(c => ToSearchResult(c, isEn, useTraditional))
            .ToList();
    }

    /// <summary>
    /// Get a curated global popular cities list (used as fallback when location
    /// is not available).
    /// </summary>
    public List<WeatherCitySearchResult> GetGlobalPopularCities(
        string language = "zh",
        int maxCount = 8)
    {
        bool isEn = !IsChineseLanguage(language);
        bool useTraditional = PackageLocalization.IsTraditionalChineseCulture(language);

        // Pick a spread of globally representative cities
        var indices = new[] { 0, 1, 2, 3, 4, 39, 53, 59, 78, 99, 113, 122, 139, 145, 153 };

        return indices
            .Where(i => i < Predefined.Count)
            .Take(maxCount)
            .Select(i => ToSearchResult(Predefined[i], isEn, useTraditional))
            .ToList();
    }

    // ─── Private helpers ───

    internal static List<WeatherCitySearchResult> SearchLocal(
        string query,
        bool isEn,
        bool useTraditional = false)
    {
        var lower = query.ToLowerInvariant();
        string matchingQuery = useTraditional
            ? ChineseTextConverter.ToSimplified(query)
            : query;
        string normalizedQuery = NormalizeSearchText(matchingQuery);
        bool isPinyinInitials = lower.Length >= 2 && lower.All(c => c >= 'a' && c <= 'z');

        var matches = Predefined
            .Where(c =>
            {
                // Search across all name variants
                return NormalizeSearchText(c.Zh).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.En).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.Pinyin).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.CountryZh).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.CountryEn).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.Admin1Zh).Contains(normalizedQuery, StringComparison.Ordinal)
                    || NormalizeSearchText(c.Admin1En).Contains(normalizedQuery, StringComparison.Ordinal)
                    || (isPinyinInitials && MatchesPinyinInitials(c.Pinyin, lower));
            })
            .OrderByDescending(c => GetSearchRelevance(c, normalizedQuery))
            .Take(8)
            .Select(c => ToSearchResult(c, isEn, useTraditional))
            .ToList();

        return matches;
    }

    /// <summary>
    /// Matches pinyin initials: "hz" matches "hangzhou", "bj" matches "beijing".
    /// </summary>
    private static bool MatchesPinyinInitials(string pinyin, string initials)
    {
        if (string.IsNullOrWhiteSpace(pinyin) || initials.Length > pinyin.Length)
        {
            return false;
        }

        // Check if the query matches the first letter of each syllable.
        // Pinyin is stored as a single word (e.g. "hangzhou"), so we match
        // the first N characters as a prefix OR try syllable-initial matching.
        if (pinyin.StartsWith(initials, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Syllable initial matching: split pinyin into syllables by common
        // boundaries and check if initials match first letters.
        // For compound names like "hangzhou", try matching "h" + "z" = "hz".
        if (initials.Length >= 2 && initials.Length <= 4)
        {
            // Simple heuristic: try splitting at each position and check
            // if first letters of parts match the initials.
            for (int splitLen = 1; splitLen < pinyin.Length - 1; splitLen++)
            {
                if (pinyin.Length - splitLen < initials.Length - 1)
                {
                    break;
                }

                // Check if first char matches first initial
                if (char.ToLowerInvariant(pinyin[0]) != initials[0])
                {
                    break;
                }

                // For 2-char initials like "hz": check if there's a 'z' later
                if (initials.Length == 2)
                {
                    for (int j = splitLen; j < pinyin.Length; j++)
                    {
                        if (char.ToLowerInvariant(pinyin[j]) == initials[1])
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Scores search relevance: exact name match > prefix match > contains > pinyin.
    /// </summary>
    private static int GetSearchRelevance(PredefinedCity city, string normalizedQuery)
    {
        string zh = NormalizeSearchText(city.Zh);
        string en = NormalizeSearchText(city.En);
        string pinyin = NormalizeSearchText(city.Pinyin);

        if (zh == normalizedQuery || en == normalizedQuery)
        {
            return 500;
        }

        if (pinyin == normalizedQuery)
        {
            return 450;
        }

        if (zh.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
            en.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
            pinyin.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return 350;
        }

        if (zh.Contains(normalizedQuery, StringComparison.Ordinal) ||
            en.Contains(normalizedQuery, StringComparison.Ordinal) ||
            pinyin.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 250;
        }

        return 100;
    }

    private static int GetResultRelevance(WeatherCitySearchResult result, string query)
    {
        string normalizedQuery = NormalizeSearchText(query);
        string name = NormalizeSearchText(result.Name);
        string displayName = NormalizeSearchText(result.DisplayName);

        if (name == normalizedQuery)
        {
            return 500;
        }

        if (name.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return 350;
        }

        if (name.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 250;
        }

        return displayName.Contains(normalizedQuery, StringComparison.Ordinal) ? 100 : 0;
    }

    private static int GetLocalResultRelevance(WeatherCitySearchResult result, string query)
    {
        PredefinedCity? city = Predefined.FirstOrDefault(candidate =>
            Math.Abs(candidate.Lat - result.Latitude) < 0.0001 &&
            Math.Abs(candidate.Lon - result.Longitude) < 0.0001);
        return city is null
            ? GetResultRelevance(result, query)
            : GetSearchRelevance(city, NormalizeSearchText(query));
    }

    internal static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsValidCoordinate(double latitude, double longitude)
    {
        return double.IsFinite(latitude) &&
               double.IsFinite(longitude) &&
               latitude is >= -90 and <= 90 &&
               longitude is >= -180 and <= 180;
    }

    private sealed record CitySearchCandidate(
        WeatherCitySearchResult Result,
        int Relevance,
        bool IsLocal,
        int Sequence);

    private static WeatherCitySearchResult ToSearchResult(
        PredefinedCity c,
        bool isEn,
        bool useTraditional)
    {
        string name = isEn ? c.En : LocalizeChineseText(c.Zh, useTraditional);
        string admin1 = isEn ? c.Admin1En : LocalizeChineseText(c.Admin1Zh, useTraditional);
        string country = isEn ? c.CountryEn : LocalizeChineseText(c.CountryZh, useTraditional);

        return new WeatherCitySearchResult
        {
            Name = name,
            DisplayName = BuildDisplayNameFromParts(name, admin1, country),
            Latitude = c.Lat,
            Longitude = c.Lon,
            Country = country,
            Admin1 = admin1
        };
    }

    private static string BuildDisplayNameFromParts(string name, string admin1, string country)
    {
        var parts = new List<string> { name };
        if (!string.IsNullOrEmpty(admin1) && admin1 != name) parts.Add(admin1);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        return string.Join(", ", parts);
    }

    private static bool IsChineseLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string LocalizeChineseText(string? value, bool useTraditional) =>
        useTraditional
            ? ChineseTextConverter.ToTraditional(value)
            : value ?? string.Empty;

    /// <summary>
    /// Calculate the great-circle distance between two points in kilometers.
    /// </summary>
    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Earth radius in km
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public void Dispose()
    {
        _weatherService.Dispose();
    }
}
