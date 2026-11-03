using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Per-user, per-widget Windows DPAPI (<c>CryptProtectData</c>) secret vault stored in
/// <c>%LOCALAPPDATA%\DeskBox\data\widget-secrets.json</c>.
/// Secrets are strictly isolated from <c>manifest.json</c>, <c>store.json</c>, and <c>.dbbox</c>
/// package exports so sharing a widget never leaks personal API tokens.
/// </summary>
public sealed class CustomWidgetSecretStore
{
    private static readonly Lazy<CustomWidgetSecretStore> SharedInstance =
        new(() => new CustomWidgetSecretStore());

    private readonly object _sync = new();
    private readonly string _filePath;
    private Dictionary<string, Dictionary<string, string>>? _cache;
    private long _lastLoadedWriteUtcTicks = -1;

    public CustomWidgetSecretStore(string? rootDataDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "widget-secrets.json");
    }

    public static CustomWidgetSecretStore Shared => SharedInstance.Value;

    public string FilePath => _filePath;

    public IReadOnlyDictionary<string, string> GetSecretsForWidget(string? widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.TryGetValue(widgetId.Trim(), out Dictionary<string, string>? map))
            {
                return new Dictionary<string, string>(map, StringComparer.Ordinal);
            }

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public string GetSecret(string? widgetId, string? key)
    {
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.TryGetValue(widgetId.Trim(), out Dictionary<string, string>? map) &&
                map.TryGetValue(key.Trim(), out string? val))
            {
                return val;
            }

            return string.Empty;
        }
    }

    public void SetSecret(string widgetId, string key, string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normWidget = widgetId.Trim();
        string normKey = key.Trim();
        string normVal = plaintext?.Trim() ?? string.Empty;

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (string.IsNullOrEmpty(normVal))
            {
                if (_cache!.TryGetValue(normWidget, out Dictionary<string, string>? existing))
                {
                    existing.Remove(normKey);
                    if (existing.Count == 0)
                    {
                        _cache.Remove(normWidget);
                    }
                    SaveNoLock();
                }

                return;
            }

            if (!_cache!.TryGetValue(normWidget, out Dictionary<string, string>? map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                _cache[normWidget] = map;
            }

            map[normKey] = normVal;
            SaveNoLock();
        }
    }

    public void RemoveAllForWidget(string? widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.Remove(widgetId.Trim()))
            {
                SaveNoLock();
            }
        }
    }

    public static string MaskSecret(string? raw) =>
        CustomWidgetAiCredentialStore.MaskApiKey(raw);

    private void EnsureLoadedNoLock()
    {
        long currentTicks = File.Exists(_filePath)
            ? File.GetLastWriteTimeUtc(_filePath).Ticks
            : 0;
        if (_cache is not null && _lastLoadedWriteUtcTicks == currentTicks)
        {
            return;
        }

        _lastLoadedWriteUtcTicks = currentTicks;
        _cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (currentTicks == 0)
        {
            return;
        }

        try
        {
            string raw = File.ReadAllText(_filePath, Encoding.UTF8);
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("widgets", out JsonElement widgetsObj) ||
                widgetsObj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty widgetProp in widgetsObj.EnumerateObject())
            {
                if (widgetProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var secretMap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty keyProp in widgetProp.Value.EnumerateObject())
                {
                    if (keyProp.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string cipher = keyProp.Value.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(cipher))
                    {
                        continue;
                    }

                    string plain = DpapiSecretProtector.UnprotectBase64ToUtf8(cipher);
                    if (!string.IsNullOrEmpty(plain))
                    {
                        secretMap[keyProp.Name] = plain;
                    }
                }

                if (secretMap.Count > 0)
                {
                    _cache[widgetProp.Name] = secretMap;
                }
            }
        }
        catch
        {
            // Corrupted file falls back cleanly to empty vault.
        }
    }

    private void SaveNoLock()
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("widgets");
            if (_cache is not null)
            {
                foreach ((string widgetId, Dictionary<string, string> secrets) in _cache)
                {
                    if (secrets.Count == 0)
                    {
                        continue;
                    }

                    writer.WriteStartObject(widgetId);
                    foreach ((string k, string plain) in secrets)
                    {
                        if (!string.IsNullOrEmpty(plain))
                        {
                            writer.WriteString(k, DpapiSecretProtector.ProtectUtf8ToBase64(plain));
                        }
                    }

                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        string tempPath = _filePath + ".tmp";
        File.WriteAllBytes(tempPath, ms.ToArray());
        CustomWidgetStorageFileOps.ReplaceJsonFileAtomically(tempPath, _filePath);
        _lastLoadedWriteUtcTicks = File.Exists(_filePath)
            ? File.GetLastWriteTimeUtc(_filePath).Ticks
            : 0;
    }
}
