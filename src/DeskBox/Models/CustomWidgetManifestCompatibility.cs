using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Models;

/// <summary>
/// Zero-reflection helpers that apply deterministic manifest metadata and locale updates without LLM round-trips.
/// </summary>
public static class CustomWidgetManifestCompatibility
{
    public static string WithUpdatedMetadataJson(
        string rawJson,
        string? newName = null,
        string? newIcon = null,
        CustomWidgetSizeSpec? newDefaultSize = null,
        string? activeCulture = null,
        bool preserveI18nNameBinding = false)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return rawJson;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return rawJson;
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                bool wroteName = newName is null;
                bool wroteIcon = newIcon is null;
                bool wroteSize = newDefaultSize is null;

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (newName is not null && string.Equals(prop.Name, "name", StringComparison.Ordinal))
                    {
                        string existingNameVal = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : string.Empty;
                        if (preserveI18nNameBinding &&
                            existingNameVal.Contains("$i18n.", StringComparison.Ordinal))
                        {
                            prop.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteString("name", newName);
                        }
                        wroteName = true;
                        continue;
                    }

                    if (newIcon is not null && string.Equals(prop.Name, "icon", StringComparison.Ordinal))
                    {
                        writer.WriteString("icon", newIcon);
                        wroteIcon = true;
                        continue;
                    }

                    if (newDefaultSize is not null && string.Equals(prop.Name, "defaultSize", StringComparison.Ordinal))
                    {
                        writer.WritePropertyName("defaultSize");
                        writer.WriteStartObject();
                        writer.WriteNumber("width", Math.Clamp(newDefaultSize.Width, 180, 1200));
                        writer.WriteNumber("height", Math.Clamp(newDefaultSize.Height, 120, 1200));
                        writer.WriteEndObject();
                        wroteSize = true;
                        continue;
                    }

                    prop.WriteTo(writer);
                }

                if (!wroteName && newName is not null)
                {
                    writer.WriteString("name", newName);
                }

                if (!wroteIcon && newIcon is not null)
                {
                    writer.WriteString("icon", newIcon);
                }

                if (!wroteSize && newDefaultSize is not null)
                {
                    writer.WritePropertyName("defaultSize");
                    writer.WriteStartObject();
                    writer.WriteNumber("width", Math.Clamp(newDefaultSize.Width, 180, 1200));
                    writer.WriteNumber("height", Math.Clamp(newDefaultSize.Height, 120, 1200));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawJson;
        }
    }

    public static CustomWidgetLocaleCatalog? WithUpdatedLocaleName(
        CustomWidgetLocaleCatalog? catalog,
        string? rawManifestName,
        string newName,
        string activeCulture)
    {
        if (catalog is null || catalog.IsEmpty || string.IsNullOrWhiteSpace(newName))
        {
            return catalog;
        }

        string normalizedCulture = CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(activeCulture);
        if (!CustomWidgetLocaleCatalog.IsValidCultureCode(normalizedCulture))
        {
            normalizedCulture = "zh-CN";
        }

        string targetKey = "meta.name";
        if (!string.IsNullOrWhiteSpace(rawManifestName))
        {
            string trimmedExpr = rawManifestName.Trim();
            if (trimmedExpr.StartsWith("${", StringComparison.Ordinal) &&
                trimmedExpr.EndsWith("}", StringComparison.Ordinal))
            {
                string inner = trimmedExpr[2..^1].Trim();
                if (inner.StartsWith("$i18n.", StringComparison.Ordinal) && inner.Length > 6)
                {
                    targetKey = inner[6..].Trim();
                }
            }
        }

        var nextLocales = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            catalog.Locales,
            StringComparer.OrdinalIgnoreCase);
        var updatedCultureDict = nextLocales.TryGetValue(normalizedCulture, out IReadOnlyDictionary<string, string>? existingDict)
            ? new Dictionary<string, string>(existingDict, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        bool wroteAnyKnownNameKey = false;
        foreach (string candidateKey in new[] { targetKey, "meta.name", "widgetName", "name" })
        {
            if (updatedCultureDict.ContainsKey(candidateKey))
            {
                updatedCultureDict[candidateKey] = newName;
                wroteAnyKnownNameKey = true;
            }
        }

        if (!wroteAnyKnownNameKey)
        {
            updatedCultureDict[targetKey] = newName;
        }

        nextLocales[normalizedCulture] = updatedCultureDict;

        CustomWidgetLocaleMeta? nextMeta = catalog.Meta;
        if (nextMeta is not null &&
            string.Equals(nextMeta.BaseLocale, normalizedCulture, StringComparison.OrdinalIgnoreCase))
        {
            var nextHashes = new Dictionary<string, string>(nextMeta.Hashes, StringComparer.Ordinal);
            nextHashes[targetKey] = CustomWidgetLocaleCatalog.ComputeTextHash8(newName);
            nextMeta = nextMeta with { Hashes = nextHashes };
        }

        return new CustomWidgetLocaleCatalog(nextLocales, nextMeta);
    }

    public static CustomWidgetLocaleCatalog? WithUpdatedLocaleKey(
        CustomWidgetLocaleCatalog? catalog,
        string localeKey,
        string newValue,
        string activeCulture)
    {
        if (catalog is null || catalog.IsEmpty || string.IsNullOrWhiteSpace(localeKey))
        {
            return catalog;
        }

        string normalizedCulture = CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(activeCulture);
        if (!CustomWidgetLocaleCatalog.IsValidCultureCode(normalizedCulture))
        {
            normalizedCulture = "zh-CN";
        }

        string trimmedKey = localeKey.Trim();
        var nextLocales = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            catalog.Locales,
            StringComparer.OrdinalIgnoreCase);
        var updatedCultureDict = nextLocales.TryGetValue(normalizedCulture, out IReadOnlyDictionary<string, string>? existingDict)
            ? new Dictionary<string, string>(existingDict, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        updatedCultureDict[trimmedKey] = newValue ?? string.Empty;
        nextLocales[normalizedCulture] = updatedCultureDict;

        CustomWidgetLocaleMeta? nextMeta = catalog.Meta;
        if (nextMeta is not null &&
            string.Equals(nextMeta.BaseLocale, normalizedCulture, StringComparison.OrdinalIgnoreCase))
        {
            var nextHashes = new Dictionary<string, string>(nextMeta.Hashes, StringComparer.Ordinal);
            nextHashes[trimmedKey] = CustomWidgetLocaleCatalog.ComputeTextHash8(newValue ?? string.Empty);
            nextMeta = nextMeta with { Hashes = nextHashes };
        }

        return new CustomWidgetLocaleCatalog(nextLocales, nextMeta);
    }
}
