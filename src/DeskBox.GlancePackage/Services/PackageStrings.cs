using System.Globalization;
using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Package-local string tables (strings/{locale}.json). Exact locale match
/// first, then en-US, then hard-coded defaults. The package cannot reach
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

    private static Dictionary<string, string>? Load(CultureInfo culture, string packageRoot)
    {
        foreach (string candidate in new[] { culture.Name, "en-US" })
        {
            string path = Path.Combine(packageRoot, "strings", $"{candidate}.json");
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                var table = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        table[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
                return table;
            }
            catch
            {
                // Fall through to the next candidate / defaults.
            }
        }
        return null;
    }
}
