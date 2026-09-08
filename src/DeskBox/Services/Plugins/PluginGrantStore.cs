using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Persists host-side grants (requested != granted, roadmap 16.10):
/// packageId -> permissionId -> granted hosts. Install UI writes grants
/// after the user approves; the capability gate consumes them at runtime.
/// Hand-rolled JsonDocument/Utf8JsonWriter persistence - the frozen
/// JsonSerializer baseline stays untouched.
/// </summary>
public sealed class PluginGrantStore
{
    private readonly string _grantsPath;
    private readonly object _lock = new();

    public PluginGrantStore()
        : this(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "plugins"))
    {
    }

    internal PluginGrantStore(string pluginsRoot)
    {
        Directory.CreateDirectory(pluginsRoot);
        _grantsPath = Path.Combine(pluginsRoot, "grants.json");
    }

    /// <summary>Sets the granted hosts for one package+permission (replaces prior hosts).</summary>
    public void SetGrants(string packageId, string permissionId, IEnumerable<string> hosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);
        lock (_lock)
        {
            Dictionary<string, Dictionary<string, List<string>>> all = Load();
            Dictionary<string, List<string>> packageGrants =
                all.TryGetValue(packageId, out Dictionary<string, List<string>>? existing)
                    ? existing
                    : [];
            packageGrants[permissionId] = hosts.Select(h => h.ToLowerInvariant()).Distinct().ToList();
            all[packageId] = packageGrants;
            Save(all);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetGrants(string packageId)
    {
        lock (_lock)
        {
            Dictionary<string, Dictionary<string, List<string>>> all = Load();
            if (!all.TryGetValue(packageId, out Dictionary<string, List<string>>? packageGrants))
            {
                return new Dictionary<string, IReadOnlyList<string>>();
            }
            return packageGrants.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value);
        }
    }

    /// <summary>Builds the gate input for a package: permissionId -> granted hosts.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetGateGrants(string packageId) => GetGrants(packageId);

    public void RemovePackage(string packageId)
    {
        lock (_lock)
        {
            Dictionary<string, Dictionary<string, List<string>>> all = Load();
            if (all.Remove(packageId))
            {
                Save(all);
            }
        }
    }

    private Dictionary<string, Dictionary<string, List<string>>> Load()
    {
        if (!File.Exists(_grantsPath))
        {
            return [];
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_grantsPath));
            var result = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (JsonProperty packageProperty in document.RootElement.EnumerateObject())
            {
                var permissions = new Dictionary<string, List<string>>();
                foreach (JsonProperty permissionProperty in packageProperty.Value.EnumerateObject())
                {
                    permissions[permissionProperty.Name] = permissionProperty.Value
                        .EnumerateArray()
                        .Where(host => host.ValueKind == JsonValueKind.String)
                        .Select(host => host.GetString()!)
                        .ToList();
                }
                result[packageProperty.Name] = permissions;
            }
            return result;
        }
        catch (JsonException)
        {
            // Corrupt grants file = no grants (fail closed); a fresh write
            // replaces it.
            return [];
        }
    }

    private void Save(Dictionary<string, Dictionary<string, List<string>>> grants)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, Dictionary<string, List<string>>> packagePair in grants.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(packagePair.Key);
                writer.WriteStartObject();
                foreach (KeyValuePair<string, List<string>> permissionPair in packagePair.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(permissionPair.Key);
                    writer.WriteStartArray();
                    foreach (string host in permissionPair.Value.OrderBy(h => h, StringComparer.Ordinal))
                    {
                        writer.WriteStringValue(host);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        string temporaryPath = _grantsPath + ".tmp";
        File.WriteAllBytes(temporaryPath, output.ToArray());
        File.Move(temporaryPath, _grantsPath, overwrite: true);
    }
}
