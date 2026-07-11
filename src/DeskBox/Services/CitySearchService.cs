using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

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
public sealed class CitySearchService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
                var resourceName = "DeskBox.Assets.Cities.cities.json";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    App.Log("[CitySearchService] Embedded cities.json not found");
                    s_predefined = [];
                    return s_predefined;
                }

                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                s_predefined = JsonSerializer.Deserialize<List<PredefinedCity>>(json, s_jsonOptions) ?? [];
                App.Log($"[CitySearchService] Loaded {s_predefined.Count} predefined cities");
                return s_predefined;
            }
        }
    }

    private readonly WeatherService _weatherService;

    public CitySearchService()
    {
        _weatherService = new WeatherService();
    }

    /// <summary>
    /// Search cities by query string.
    /// Returns merged results from the local pre-defined list (instant)
    /// and the Open-Meteo geocoding API (broader coverage).
    /// Results are deduplicated by coordinate proximity.
    /// </summary>
    public async Task<List<WeatherCitySearchResult>> SearchAsync(
        string query,
        string language = "zh",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return [];
        }

        query = query.Trim();
        bool isEn = language is "en" or "en-US";

        // 1. Search local predefined cities (instant, no network)
        var localResults = SearchLocal(query, isEn);

        // 2. Search via Open-Meteo API (parallel, with cancellation)
        List<WeatherGeocodingItem>? apiResults = null;
        try
        {
            apiResults = await _weatherService.SearchCityAsync(query, language);
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
        var merged = new List<WeatherCitySearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add local results first (they have proper zh/en names)
        foreach (var r in localResults)
        {
            var key = $"{r.Latitude:F2},{r.Longitude:F2}";
            if (seen.Add(key))
            {
                merged.Add(r);
            }
        }

        // Add API results not already in the list
        if (apiResults is not null)
        {
            foreach (var item in apiResults)
            {
                var key = $"{item.Latitude:F2},{item.Longitude:F2}";
                if (seen.Add(key))
                {
                    merged.Add(new WeatherCitySearchResult
                    {
                        Name = item.Name ?? string.Empty,
                        DisplayName = BuildDisplayName(item),
                        Latitude = item.Latitude,
                        Longitude = item.Longitude,
                        Country = item.Country ?? string.Empty,
                        Admin1 = item.Admin1 ?? string.Empty
                    });
                }
            }
        }

        return merged.Take(10).ToList();
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
        bool isEn = language is "en" or "en-US";

        IEnumerable<PredefinedCity> cities = Predefined;

        if (lat.HasValue && lon.HasValue)
        {
            cities = cities
                .OrderBy(c => HaversineDistance(lat.Value, lon.Value, c.Lat, c.Lon));
        }

        return cities
            .Take(maxCount)
            .Select(c => ToSearchResult(c, isEn))
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
        bool isEn = language is "en" or "en-US";

        // Pick a spread of globally representative cities
        var indices = new[] { 0, 1, 2, 3, 4, 39, 53, 59, 78, 99, 113, 122, 139, 145, 153 };

        return indices
            .Where(i => i < Predefined.Count)
            .Take(maxCount)
            .Select(i => ToSearchResult(Predefined[i], isEn))
            .ToList();
    }

    // ─── Private helpers ───

    private static List<WeatherCitySearchResult> SearchLocal(string query, bool isEn)
    {
        var lower = query.ToLowerInvariant();

        var matches = Predefined
            .Where(c =>
            {
                // Search across all name variants
                return c.Zh.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.En.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.Pinyin.Contains(lower, StringComparison.OrdinalIgnoreCase)
                    || c.CountryZh.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.CountryEn.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.Admin1Zh.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.Admin1En.Contains(query, StringComparison.OrdinalIgnoreCase);
            })
            .Take(5)
            .Select(c => ToSearchResult(c, isEn))
            .ToList();

        return matches;
    }

    private static WeatherCitySearchResult ToSearchResult(PredefinedCity c, bool isEn)
    {
        var name = isEn ? c.En : c.Zh;
        var admin1 = isEn ? c.Admin1En : c.Admin1Zh;
        var country = isEn ? c.CountryEn : c.CountryZh;

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

    private static string BuildDisplayName(WeatherGeocodingItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.Name)) parts.Add(item.Name);
        if (!string.IsNullOrEmpty(item.Admin1) && item.Admin1 != item.Name) parts.Add(item.Admin1);
        if (!string.IsNullOrEmpty(item.Country)) parts.Add(item.Country);
        return string.Join(", ", parts);
    }

    private static string BuildDisplayNameFromParts(string name, string admin1, string country)
    {
        var parts = new List<string> { name };
        if (!string.IsNullOrEmpty(admin1) && admin1 != name) parts.Add(admin1);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        return string.Join(", ", parts);
    }

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
