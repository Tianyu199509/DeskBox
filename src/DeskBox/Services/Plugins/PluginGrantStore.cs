using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>Host grants are bound to both package ID and publisher. Legacy unbound grants are never inherited.</summary>
public sealed class PluginGrantStore
{
    private readonly string _grantsPath;
    private readonly object _lock;
    private sealed record GrantRecord(string Publisher, Dictionary<string, List<string>> Permissions);

    public PluginGrantStore() : this(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins")) { }

    internal PluginGrantStore(string pluginsRoot)
    {
        Directory.CreateDirectory(pluginsRoot);
        _grantsPath = Path.Combine(pluginsRoot, "grants.json");
        _lock = PluginPackageStorage.Gate(pluginsRoot);
    }

    public void SetGrants(string packageId, string publisherFingerprint, string permissionId, IEnumerable<string> hosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherFingerprint);
        if (publisherFingerprint.Length != 64 || publisherFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("publisher must be a lowercase SHA-256 fingerprint", nameof(publisherFingerprint));
        lock (_lock)
        {
            Dictionary<string, GrantRecord> all = Load();
            Dictionary<string, List<string>> permissions =
                all.TryGetValue(packageId, out GrantRecord? previous) && previous.Publisher == publisherFingerprint
                    ? previous.Permissions : [];
            permissions[permissionId] = hosts.Select(h => h.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            all[packageId] = new(publisherFingerprint, permissions);
            Save(all);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetGrants(string packageId, string publisherFingerprint)
    {
        lock (_lock)
        {
            Dictionary<string, GrantRecord> all = Load();
            return all.TryGetValue(packageId, out GrantRecord? package) && package.Publisher == publisherFingerprint
                ? package.Permissions.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value)
                : new Dictionary<string, IReadOnlyList<string>>();
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetGateGrants(VerifiedPluginPackage package) =>
        GetGrants(package.PackageId, package.PublisherFingerprint);

    public void RemovePackage(string packageId)
    {
        lock (_lock)
        {
            Dictionary<string, GrantRecord> all = Load();
            if (all.Remove(packageId)) Save(all);
        }
    }

    private Dictionary<string, GrantRecord> Load()
    {
        if (!File.Exists(_grantsPath)) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(PluginPackageStorage.ReadText(_grantsPath, 4 * 1024 * 1024));
            var all = new Dictionary<string, GrantRecord>();
            foreach (JsonProperty item in document.RootElement.EnumerateObject())
            {
                string publisher = item.Value.GetProperty("publisherFingerprint").GetString()!;
                if (publisher is not { Length: 64 } || publisher.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    return [];
                var permissions = new Dictionary<string, List<string>>();
                foreach (JsonProperty permission in item.Value.GetProperty("permissions").EnumerateObject())
                {
                    var hosts = new List<string>();
                    foreach (JsonElement host in permission.Value.EnumerateArray())
                    {
                        if (host.ValueKind != JsonValueKind.String) return [];
                        hosts.Add(host.GetString()!);
                    }
                    if (!permissions.TryAdd(permission.Name, hosts)) return [];
                }
                if (!all.TryAdd(item.Name, new(publisher, permissions))) return [];
            }
            return all;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or
                                      InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, GrantRecord> grants)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var package in grants.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(package.Key);
                writer.WriteStartObject();
                writer.WriteString("publisherFingerprint", package.Value.Publisher);
                writer.WritePropertyName("permissions");
                writer.WriteStartObject();
                foreach (var permission in package.Value.Permissions.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(permission.Key);
                    writer.WriteStartArray();
                    foreach (string host in permission.Value.OrderBy(h => h, StringComparer.Ordinal)) writer.WriteStringValue(host);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        PluginPackageStorage.WriteAtomically(_grantsPath, output.ToArray());
    }
}
