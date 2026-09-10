using System.Globalization;
using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Package-local string tables (strings/{locale}.json). The en-US base table
/// is overlaid per key by the exact locale; caller defaults are a last resort.
/// The package cannot reach
/// host resources (batch B/C findings: library packages produce no PRI and
/// MrtCore has no file-level loading), so localizations ship inside the
/// package directory.
/// </summary>
internal static class PackageStrings
{
    private static Dictionary<string, string>? _table;

    internal static void Configure(CultureInfo culture, string packageRoot) =>
        _table = Load(culture, packageRoot);

    internal static string Get(string key, string fallback)
    {
        Dictionary<string, string>? table = _table;
        return table is not null && table.TryGetValue(key, out string? value)
            ? value
            : fallback;
    }

    private static Dictionary<string, string> Load(CultureInfo culture, string packageRoot)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        Overlay("en-US");
        if (!string.Equals(culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            Overlay(culture.Name);
        }
        return table;

        void Overlay(string locale)
        {
            string path = Path.Combine(packageRoot, "strings", $"{locale}.json");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        table[property.Name] = property.Value.GetString()!;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A missing/unreadable/malformed table cannot discard the base
                // translations. JsonDocument parses fully before any overlay.
            }
        }
    }
}
