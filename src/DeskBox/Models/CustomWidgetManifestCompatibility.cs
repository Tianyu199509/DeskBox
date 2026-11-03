using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Models;

/// <summary>
/// Zero-reflection helpers that automatically stamp <c>minAppVersion</c> when a manifest
/// uses 1.6.0+ primitives, and apply deterministic header/size tweaks without LLM round-trips.
/// </summary>
public static class CustomWidgetManifestCompatibility
{
    public const string V160VersionString = "1.6.0";
    private static readonly Version V160Version = new(1, 6, 0);

    private static readonly HashSet<string> V2ExtendedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Popover", "Markdown", "Table", "DatePicker", "TimePicker",
        "DonutChart", "Chart", "Canvas", "AutoSuggestInput",
        "ColorPicker", "RadioGroup", "Rating"
    };

    public static bool UsesV160Features(CustomWidgetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (ContainsV160Node(manifest.View))
        {
            return true;
        }

        if (manifest.Popovers.Count > 0)
        {
            return true;
        }

        if (ContainsV160Action(manifest.OnDrop?.Actions))
        {
            return true;
        }

        if (manifest.Sources.Any(static s =>
                string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "calendar", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "deviceState", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "clipboard", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "todo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase) ||
                s.Headers is { Count: > 0 }))
        {
            return true;
        }

        if (manifest.Settings.Any(static s => string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static bool UsesV160FeaturesJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        if (rawJson.Contains("$drop.text", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("drop.text", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$clipboard.", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$drives.", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$clock.", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$music.", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$todo.", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("tz(", StringComparison.OrdinalIgnoreCase) ||
            rawJson.Contains("$value", StringComparison.Ordinal))
        {
            return true;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawJson);
        return parsed.Manifest is not null && UsesV160Features(parsed.Manifest);
    }

    public static string StampRequiredMinAppVersion(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || !UsesV160FeaturesJson(rawJson))
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

            if (doc.RootElement.TryGetProperty("minAppVersion", out JsonElement existingEl) &&
                existingEl.ValueKind == JsonValueKind.String)
            {
                string existingStr = (existingEl.GetString() ?? string.Empty).Trim().TrimStart('v', 'V');
                int dashIdx = existingStr.IndexOf('-');
                if (dashIdx > 0)
                {
                    existingStr = existingStr[..dashIdx];
                }

                if (Version.TryParse(existingStr, out Version? parsedVer) &&
                    new Version(parsedVer.Major, parsedVer.Minor, Math.Max(0, parsedVer.Build)) >= V160Version)
                {
                    return rawJson;
                }
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                bool wroteMinVer = false;

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "minAppVersion", StringComparison.Ordinal))
                    {
                        writer.WriteString("minAppVersion", V160VersionString);
                        wroteMinVer = true;
                        continue;
                    }

                    prop.WriteTo(writer);

                    if (!wroteMinVer &&
                        (string.Equals(prop.Name, "version", StringComparison.Ordinal) ||
                         string.Equals(prop.Name, "schemaVersion", StringComparison.Ordinal)))
                    {
                        writer.WriteString("minAppVersion", V160VersionString);
                        wroteMinVer = true;
                    }
                }

                if (!wroteMinVer)
                {
                    writer.WriteString("minAppVersion", V160VersionString);
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

    private static bool ContainsV160Node(CustomWidgetNodeSpec? node)
    {
        if (node is null)
        {
            return false;
        }

        if (CustomWidgetManifestParser.V160IntroducedNodeTypes.Contains(node.Type) ||
            V2ExtendedNodeTypes.Contains(node.Type))
        {
            return true;
        }

        if (ContainsV160Action(node.OnClick) ||
            ContainsV160Action(node.OnSubmit) ||
            ContainsV160Action(node.OnChange) ||
            ContainsV160Action(node.OnDropActions) ||
            ContainsV160Action(node.OnRowClick) ||
            ContainsV160Action(node.OnItemClick) ||
            ContainsV160Action(node.OnReorder) ||
            ContainsV160Action(node.OnInlineCommit))
        {
            return true;
        }

        if (ContainsV160Node(node.ItemTemplate))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            if (ContainsV160Node(h))
            {
                return true;
            }
        }

        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            if (ContainsV160Node(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsV160Action(IReadOnlyList<CustomWidgetActionSpec>? actions)
    {
        if (actions is null)
        {
            return false;
        }

        foreach (CustomWidgetActionSpec act in actions)
        {
            if (string.Equals(act.Type, "ai.run", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "file.transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "file.pick", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "file.writeText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "command.run", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "popover.close", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "popover.toggle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "collection.reorder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "text.transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "launcher.open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "system.control", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "system.openSetting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "reminder.notify", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ContainsV160Action(act.Then))
            {
                return true;
            }
        }

        return false;
    }
}
