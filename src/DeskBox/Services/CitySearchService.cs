using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Pre-defined city entry loaded from the external cities.json data file.
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
/// with dynamic results from the Open-Meteo geocoding API. Supports
/// location-based "nearby popular cities" by sorting the local list by
/// haversine distance to the user's coordinates.
/// </summary>
public sealed class CitySearchService : IDisposable
{
    private sealed class PreparedCityEntry(PredefinedCity city)
    {
        public PredefinedCity City { get; } = city;
        public string ZhNorm { get; } = NormalizeSearchText(city.Zh);
        public string EnNorm { get; } = NormalizeSearchText(city.En);
        public string PinyinNorm { get; } = NormalizeSearchText(city.Pinyin);
        public string CountryZhNorm { get; } = NormalizeSearchText(city.CountryZh);
        public string CountryEnNorm { get; } = NormalizeSearchText(city.CountryEn);
        public string Admin1ZhNorm { get; } = NormalizeSearchText(city.Admin1Zh);
        public string Admin1EnNorm { get; } = NormalizeSearchText(city.Admin1En);
    }

    private static readonly object s_instancesLock = new();
    private static readonly List<WeakReference<CitySearchService>> s_liveInstances = [];

    private static Stream? OpenCitiesDataStream()
    {
        string primaryPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Cities", "cities.json");
        if (File.Exists(primaryPath))
        {
            return new FileStream(primaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        string? directory = AppContext.BaseDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            string candidate = Path.Combine(directory, "Assets", "Cities", "cities.json");
            if (File.Exists(candidate))
            {
                return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            string repoCandidate = Path.Combine(directory, "src", "DeskBox", "Assets", "Cities", "cities.json");
            if (File.Exists(repoCandidate))
            {
                return new FileStream(repoCandidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        }

        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream("DeskBox.Assets.Cities.cities.json");
    }

    private static List<PredefinedCity> LoadPredefinedCities()
    {
        using Stream? stream = OpenCitiesDataStream();
        if (stream is null)
        {
            App.Log("[CitySearchService] cities.json not found");
            return [];
        }

        return JsonSerializer.Deserialize(
            stream,
            WeatherJsonContext.Default.PredefinedCityList) ?? [];
    }

    private static PreparedCityEntry[] BuildPreparedCities(IReadOnlyList<PredefinedCity> cities)
    {
        var entries = new PreparedCityEntry[cities.Count];
        for (int i = 0; i < cities.Count; i++)
        {
            entries[i] = new PreparedCityEntry(cities[i]);
        }

        return entries;
    }

    public static void ReleaseIdleCache(bool force = false)
    {
        _ = force;
        lock (s_instancesLock)
        {
            for (int i = s_liveInstances.Count - 1; i >= 0; i--)
            {
                if (!s_liveInstances[i].TryGetTarget(out CitySearchService? instance) || instance._isDisposed)
                {
                    s_liveInstances.RemoveAt(i);
                    continue;
                }

                Volatile.Write(ref instance._cachedCities, null);
            }
        }
    }

    private readonly WeatherService _weatherService;
    private PreparedCityEntry[]? _cachedCities;
    private volatile bool _isDisposed;

    public CitySearchService()
    {
        _weatherService = new WeatherService();
        lock (s_instancesLock)
        {
            for (int i = s_liveInstances.Count - 1; i >= 0; i--)
            {
                if (!s_liveInstances[i].TryGetTarget(out CitySearchService? existing) || existing._isDisposed)
                {
                    s_liveInstances.RemoveAt(i);
                }
            }

            s_liveInstances.Add(new WeakReference<CitySearchService>(this));
        }
    }

    internal bool HasCachedCitiesForTesting => Volatile.Read(ref _cachedCities) is not null;

    private PreparedCityEntry[] GetOrLoadPreparedCities()
    {
        if (_isDisposed)
        {
            return [];
        }

        PreparedCityEntry[]? cached = Volatile.Read(ref _cachedCities);
        if (cached is not null)
        {
            return cached;
        }

        PreparedCityEntry[] loaded = BuildPreparedCities(LoadPredefinedCities());
        if (!_isDisposed)
        {
            Volatile.Write(ref _cachedCities, loaded);
        }

        return loaded;
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
        bool useTraditional = LocalizationService.IsTraditionalChineseCulture(language);
        PredefinedCity? best = null;
        double bestDist = double.MaxValue;

        foreach (var c in LoadPredefinedCities())
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
        bool useTraditional = LocalizationService.IsTraditionalChineseCulture(language);

        // 1. Search local predefined cities (instant, no network)
        PreparedCityEntry[] preparedCities = GetOrLoadPreparedCities();
        var localResults = SearchLocal(preparedCities, query, isEn, useTraditional);

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
            App.Log($"[CitySearchService] API search failed: {ex.Message}");
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
                    GetLocalResultRelevance(preparedCities, r, query),
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
        bool useTraditional = LocalizationService.IsTraditionalChineseCulture(language);

        IEnumerable<PreparedCityEntry> cities = GetOrLoadPreparedCities();

        if (lat.HasValue && lon.HasValue)
        {
            cities = cities
                .OrderBy(c => HaversineDistance(lat.Value, lon.Value, c.City.Lat, c.City.Lon));
        }

        return cities
            .Take(maxCount)
            .Select(c => ToSearchResult(c.City, isEn, useTraditional))
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
        bool useTraditional = LocalizationService.IsTraditionalChineseCulture(language);

        PreparedCityEntry[] predefined = GetOrLoadPreparedCities();
        // Pick a spread of globally representative cities
        var indices = new[] { 0, 1, 2, 3, 4, 39, 53, 59, 78, 99, 113, 122, 139, 145, 153 };

        return indices
            .Where(i => i < predefined.Length)
            .Take(maxCount)
            .Select(i => ToSearchResult(predefined[i].City, isEn, useTraditional))
            .ToList();
    }

    // ─── Private helpers ───

    internal static List<WeatherCitySearchResult> SearchLocal(
        string query,
        bool isEn,
        bool useTraditional = false) =>
        SearchLocal(BuildPreparedCities(LoadPredefinedCities()), query, isEn, useTraditional);

    private static List<WeatherCitySearchResult> SearchLocal(
        IReadOnlyList<PreparedCityEntry> predefined,
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

        var matches = predefined
            .Where(c =>
            {
                // Search across all name variants
                return c.ZhNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.EnNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.PinyinNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.CountryZhNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.CountryEnNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.Admin1ZhNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || c.Admin1EnNorm.Contains(normalizedQuery, StringComparison.Ordinal)
                    || (isPinyinInitials && MatchesPinyinInitials(c.PinyinNorm, lower));
            })
            .OrderByDescending(c => GetSearchRelevance(c, normalizedQuery))
            .Take(8)
            .Select(c => ToSearchResult(c.City, isEn, useTraditional))
            .ToList();

        return matches;
    }

    /// <summary>
    /// Matches pinyin initials: "hz" matches "hangzhou", "sjz" matches "shijiazhuang",
    /// "heb" matches "harbin"/"haerbin", "wlmq" matches "wulumuqi".
    /// </summary>
    internal static bool MatchesPinyinInitials(string pinyin, string initials)
    {
        if (string.IsNullOrWhiteSpace(pinyin) || string.IsNullOrWhiteSpace(initials))
        {
            return false;
        }

        string normalizedPinyin = NormalizeSearchText(pinyin);
        string normalizedInitials = NormalizeSearchText(initials);
        if (normalizedPinyin.Length == 0 || normalizedInitials.Length == 0)
        {
            return false;
        }

        if (MatchesPinyinInitialsNormalized(normalizedPinyin, normalizedInitials))
        {
            return true;
        }

        string? alias = GetStandardPinyinAlias(normalizedPinyin);
        return alias is not null && MatchesPinyinInitialsNormalized(alias, normalizedInitials);
    }

    private static bool MatchesPinyinInitialsNormalized(string pinyin, string initials)
    {
        if (initials.Length > pinyin.Length)
        {
            return false;
        }

        if (pinyin.StartsWith(initials, StringComparison.Ordinal))
        {
            return true;
        }

        if (initials.Length is >= 2 and <= 6)
        {
            return MatchesSyllableInitials(pinyin.AsSpan(), initials.AsSpan());
        }

        return false;
    }

    private static string? GetStandardPinyinAlias(string normalizedPinyin) =>
        normalizedPinyin switch
        {
            "harbin" => "haerbin",
            "qiqihar" => "qiqihaer",
            "urumqi" => "wulumuqi",
            "hohhot" => "huhehaote",
            _ => null
        };

    private static bool MatchesSyllableInitials(ReadOnlySpan<char> pinyin, ReadOnlySpan<char> initials)
    {
        if (initials.IsEmpty)
        {
            return pinyin.IsEmpty;
        }

        if (pinyin.Length < initials.Length || pinyin[0] != initials[0])
        {
            return false;
        }

        if (initials.Length == 1)
        {
            return IsPlausiblePinyinSyllable(pinyin);
        }

        int maxSyllableLen = Math.Min(6, pinyin.Length - (initials.Length - 1));
        for (int len = 1; len <= maxSyllableLen; len++)
        {
            if (IsPlausiblePinyinSyllable(pinyin[..len]) &&
                MatchesSyllableInitials(pinyin[len..], initials[1..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausiblePinyinSyllable(ReadOnlySpan<char> part)
    {
        if (part.IsEmpty || part.Length > 6)
        {
            return false;
        }

        int idx = 0;
        if (part.Length >= 2 &&
            (part[0] is 'z' or 'c' or 's') &&
            part[1] == 'h')
        {
            idx = 2;
        }
        else if (!IsPinyinVowel(part[0]))
        {
            idx = 1;
        }

        int vowelCount = 0;
        while (idx < part.Length && IsPinyinVowel(part[idx]))
        {
            vowelCount++;
            idx++;
        }

        if (vowelCount is < 1 or > 3)
        {
            return false;
        }

        ReadOnlySpan<char> coda = part[idx..];
        return coda.IsEmpty ||
               coda.SequenceEqual("n") ||
               coda.SequenceEqual("ng") ||
               coda.SequenceEqual("r");
    }

    private static bool IsPinyinVowel(char c) =>
        c is 'a' or 'e' or 'i' or 'o' or 'u' or 'v';

    /// <summary>
    /// Scores search relevance: exact name match > prefix match > contains > pinyin.
    /// </summary>
    private static int GetSearchRelevance(PreparedCityEntry city, string normalizedQuery)
    {
        string zh = city.ZhNorm;
        string en = city.EnNorm;
        string pinyin = city.PinyinNorm;

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

    private static int GetLocalResultRelevance(
        IReadOnlyList<PreparedCityEntry> predefined,
        WeatherCitySearchResult result,
        string query)
    {
        PreparedCityEntry? city = predefined.FirstOrDefault(candidate =>
            Math.Abs(candidate.City.Lat - result.Latitude) < 0.0001 &&
            Math.Abs(candidate.City.Lon - result.Longitude) < 0.0001);
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
        _isDisposed = true;
        Volatile.Write(ref _cachedCities, null);
        lock (s_instancesLock)
        {
            for (int i = s_liveInstances.Count - 1; i >= 0; i--)
            {
                if (!s_liveInstances[i].TryGetTarget(out CitySearchService? instance) ||
                    ReferenceEquals(instance, this) ||
                    instance._isDisposed)
                {
                    s_liveInstances.RemoveAt(i);
                }
            }
        }

        _weatherService.Dispose();
    }
}
