using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public enum CustomWidgetLocaleSyncStatus
{
    BaseReady,
    UpToDate,
    PendingDiff,
    PendingFull
}

public sealed record CustomWidgetLocaleTaskPlan(
    string Culture,
    string NativeName,
    string DisplayName,
    CustomWidgetLocaleSyncStatus Status,
    int TotalRequiredKeys,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> StaleKeys,
    IReadOnlyList<string> DeltaKeys,
    IReadOnlyDictionary<string, string> ExistingValidEntries)
{
    public bool NeedsTranslation =>
        Status is CustomWidgetLocaleSyncStatus.PendingDiff or CustomWidgetLocaleSyncStatus.PendingFull &&
        DeltaKeys.Count > 0;

    public int ReadyKeyCount => Math.Max(0, TotalRequiredKeys - DeltaKeys.Count);
}

public sealed record CustomWidgetI18nSyncPlan(
    string CompiledManifestJson,
    bool ManifestChanged,
    string BaseLocale,
    IReadOnlyList<string> RequiredKeys,
    IReadOnlyDictionary<string, string> BaseEntries,
    CustomWidgetLocaleMeta NextMeta,
    CustomWidgetLocaleCatalog SeededCatalog,
    IReadOnlyList<CustomWidgetLocaleTaskPlan> LocalePlans)
{
    public int TotalLocaleCount => LocalePlans.Count;

    public int ReadyLocaleCount => LocalePlans.Count(static p => !p.NeedsTranslation);

    public int PendingLocaleCount => LocalePlans.Count(static p => p.NeedsTranslation);

    public int TotalDeltaKeyCount => LocalePlans.Sum(static p => p.DeltaKeys.Count);
}

public sealed record CustomWidgetI18nCompileResult(
    string CompiledManifestJson,
    bool ManifestChanged,
    string BaseLocale,
    IReadOnlyList<string> RequiredKeys,
    IReadOnlyDictionary<string, string> BaseEntries,
    CustomWidgetLocaleMeta NextMeta,
    CustomWidgetLocaleCatalog SeededCatalog);

/// <summary>
/// Deterministic AST localization compiler and incremental key/hash diff planner for Custom Widgets.
/// Replaces fragile LLM manifest rewriting with sub-millisecond C# JSON AST rewriting and
/// content-hashed per-locale delta planning.
/// </summary>
public static class CustomWidgetI18nEngine
{
    private static readonly JsonDocumentOptions PermissiveJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonWriterOptions IndentedJsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex DirectI18nWholeTokenRegex = new(
        @"^\$\{\s*\$i18n\.(?<key>[A-Za-z0-9_.\-]+)\s*\}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyI18nPropRegex = new(
        @"\$i18n\.(?<key>[A-Za-z0-9_.\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyTFuncRegex = new(
        @"(?<![A-Za-z0-9_.$])t\(\s*(?:'(?<sq>(?:\\.|[^'])*)'|""(?<dq>(?:\\.|[^""])*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LogicCtxTRegex = new(
        @"\bctx\.t\(\s*(?:'(?<k1>[A-Za-z0-9_.\-]+)'|""(?<k2>[A-Za-z0-9_.\-]+)"")(?:\s*,\s*(?:'(?<f1>(?:\\.|[^'])*)'|""(?<f2>(?:\\.|[^""])*)""))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PositionalPlaceholderRegex = new(
        @"\{(?<idx>\d+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TechnicalUnits = new(StringComparer.Ordinal)
    {
        "%", "°C", "°F", "K", "B", "KB", "MB", "GB", "TB", "PB",
        "bps", "Kbps", "Mbps", "Gbps", "ms", "s", "m", "h", "d",
        "Hz", "kHz", "MHz", "GHz", "W", "kW", "V", "mV", "A", "mA", "mAh",
        "px", "pt", "fps", "RPM", "rpm"
    };

    private static readonly HashSet<string> TechnicalExpressionLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "undefined", "idle", "loading", "ok", "error", "running",
        "auth_required", "default", "subtle", "accent", "muted", "secondary",
        "emerald", "violet", "cyan", "success", "warning", "danger",
        "standard", "ghost", "vertical", "horizontal", "work", "rest", "break",
        "prepend", "append", "replace", "copy", "link", "reference", "auto"
    };

    private static readonly HashSet<string> ViewNodeLocalizableProps = new(StringComparer.Ordinal)
    {
        "text", "title", "subtitle", "label", "placeholder", "tooltip", "unit", "centerTitle"
    };

    private static readonly HashSet<string> ValueDisplayNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "StatCard", "ProgressRow", "KeyValueRow"
    };

    private static readonly HashSet<string> ToastActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "toast.show", "reminder.notify"
    };

    public static CustomWidgetI18nCompileResult Compile(
        string manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? existingCatalog = null,
        string fallbackCulture = "zh-CN")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        using JsonDocument doc = JsonDocument.Parse(manifestJson, PermissiveJsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("manifest.json root must be a JSON object.");
        }

        var state = new CompilerContext(existingCatalog, fallbackCulture);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            WriteRootManifestObject(writer, doc.RootElement, state);
        }

        string compiledJson = Encoding.UTF8.GetString(ms.ToArray());
        using (JsonDocument compiledDoc = JsonDocument.Parse(compiledJson, PermissiveJsonOptions))
        {
            ScanCompiledElementReferences(compiledDoc.RootElement, state);
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            ScanLogicJsKeys(logicJs, state);
        }

        string normalizedBefore = NormalizeJsonForComparison(manifestJson);
        string normalizedAfter = NormalizeJsonForComparison(compiledJson);
        bool manifestChanged = !string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal);

        string baseLocale = state.ResolveFinalBaseLocale();
        var baseEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string key in state.RequiredKeys)
        {
            string sourceText = state.ResolveBaseSourceText(key, baseLocale);
            baseEntries[key] = sourceText;
            hashes[key] = CustomWidgetLocaleCatalog.ComputeTextHash8(sourceText);
        }

        var nextMeta = new CustomWidgetLocaleMeta(
            CustomWidgetLocaleCatalog.CurrentMetaSchemaVersion,
            baseLocale,
            hashes);

        var staleKeys = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string>? previousHashes = existingCatalog?.Meta?.Hashes;
        IReadOnlyDictionary<string, string>? existingBaseDict = null;
        existingCatalog?.Locales.TryGetValue(baseLocale, out existingBaseDict);

        if (previousHashes is { Count: > 0 })
        {
            foreach (string key in state.RequiredKeys)
            {
                if (TryGetHashWithLegacyMetaFallback(previousHashes, key, out string? prevHash) &&
                    hashes.TryGetValue(key, out string? nextHash) &&
                    !string.Equals(prevHash, nextHash, StringComparison.OrdinalIgnoreCase))
                {
                    staleKeys.Add(key);
                }
            }
        }

        var nextLocales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (existingCatalog is not null)
        {
            foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> kv in existingCatalog.Locales)
            {
                if (kv.Value.Count == 0 || !CustomWidgetLocaleCatalog.IsValidCultureCode(kv.Key))
                {
                    continue;
                }

                bool isBaseLocale = string.Equals(kv.Key, baseLocale, StringComparison.OrdinalIgnoreCase);
                var pruned = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string reqKey in state.RequiredKeys)
                {
                    string expectedBaseText = baseEntries[reqKey];
                    if (!TryGetLocaleValueWithLegacyMetaFallback(
                            kv.Value,
                            reqKey,
                            existingBaseDict,
                            expectedBaseText,
                            out string? existingValue) ||
                        existingValue is null)
                    {
                        continue;
                    }

                    bool hasValidContent = !string.IsNullOrWhiteSpace(existingValue) ||
                        (!isBaseLocale && IsIntentionalEmptyTranslation(reqKey, expectedBaseText, existingValue, previousHashes, staleKeys));

                    if (hasValidContent && (isBaseLocale || !staleKeys.Contains(reqKey)))
                    {
                        pruned[reqKey] = existingValue;
                    }
                }

                if (pruned.Count > 0)
                {
                    nextLocales[kv.Key] = pruned;
                }
            }
        }

        if (baseEntries.Count > 0)
        {
            nextLocales[baseLocale] = baseEntries;
        }

        var seededCatalog = new CustomWidgetLocaleCatalog(nextLocales, nextMeta);
        return new CustomWidgetI18nCompileResult(
            compiledJson,
            manifestChanged,
            baseLocale,
            state.RequiredKeys,
            baseEntries,
            nextMeta,
            seededCatalog);
    }

    /// <summary>
    /// When a widget already carries a localized catalog (<c>locales/*.json</c>), automatically compiles
    /// newly added or edited manifest copy into <c>${$i18n.*}</c> bindings, reuses existing translations
    /// at zero token cost, and updates <c>locales/&lt;base&gt;.json</c> + <c>_meta.json</c> hashes so multi-turn
    /// Studio edits stay consistent and the locale readiness badge reflects un-translated delta keys immediately.
    /// </summary>
    public static string AutoCompileIfLocalized(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? existingCatalog,
        string fallbackCulture,
        out CustomWidgetLocaleCatalog? updatedCatalog)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || existingCatalog is not { HasAnyLocale: true })
        {
            updatedCatalog = existingCatalog;
            return manifestJson;
        }

        try
        {
            CustomWidgetI18nCompileResult compiled = Compile(
                manifestJson,
                logicJs,
                existingCatalog,
                string.IsNullOrWhiteSpace(fallbackCulture) ? "zh-CN" : fallbackCulture);
            updatedCatalog = compiled.SeededCatalog.HasAnyLocale ? compiled.SeededCatalog : existingCatalog;
            return compiled.CompiledManifestJson;
        }
        catch
        {
            updatedCatalog = existingCatalog;
            return manifestJson;
        }
    }

    public static CustomWidgetI18nSyncPlan BuildSyncPlan(
        string manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? existingCatalog = null,
        string fallbackCulture = "zh-CN",
        IReadOnlyList<string>? targetLocales = null)
    {
        CustomWidgetI18nCompileResult compiled = Compile(
            manifestJson,
            logicJs,
            existingCatalog,
            fallbackCulture);

        IReadOnlyList<string> localesToPlan = targetLocales is { Count: > 0 }
            ? targetLocales
            : CustomWidgetLocaleCatalog.SupportedLocales.Select(static s => s.Code).ToArray();

        IReadOnlyDictionary<string, string>? previousHashes = existingCatalog?.Meta?.Hashes;
        IReadOnlyDictionary<string, string>? existingBaseDict = null;
        existingCatalog?.Locales.TryGetValue(compiled.BaseLocale, out existingBaseDict);
        bool baseContainsCjk = compiled.BaseEntries.Values.Any(ContainsCjk);

        var staleSet = new HashSet<string>(StringComparer.Ordinal);
        if (previousHashes is { Count: > 0 })
        {
            foreach (string key in compiled.RequiredKeys)
            {
                if (TryGetHashWithLegacyMetaFallback(previousHashes, key, out string? prevHash) &&
                    compiled.NextMeta.Hashes.TryGetValue(key, out string? nextHash) &&
                    !string.Equals(prevHash, nextHash, StringComparison.OrdinalIgnoreCase))
                {
                    staleSet.Add(key);
                }
            }
        }

        var plans = new List<CustomWidgetLocaleTaskPlan>(localesToPlan.Count);
        foreach (string rawCulture in localesToPlan)
        {
            if (!CustomWidgetLocaleCatalog.IsValidCultureCode(rawCulture))
            {
                continue;
            }

            CustomWidgetSupportedLocaleInfo info = ResolveLocaleInfo(rawCulture);
            if (string.Equals(info.Code, compiled.BaseLocale, StringComparison.OrdinalIgnoreCase))
            {
                plans.Add(new CustomWidgetLocaleTaskPlan(
                    Culture: info.Code,
                    NativeName: info.NativeName,
                    DisplayName: info.EnglishName,
                    Status: CustomWidgetLocaleSyncStatus.BaseReady,
                    TotalRequiredKeys: compiled.RequiredKeys.Count,
                    MissingKeys: [],
                    StaleKeys: [],
                    DeltaKeys: [],
                    ExistingValidEntries: compiled.BaseEntries));
                continue;
            }

            IReadOnlyDictionary<string, string>? existingDict = null;
            if (existingCatalog is not null)
            {
                existingCatalog.Locales.TryGetValue(info.Code, out existingDict);
            }
            var validExisting = new Dictionary<string, string>(StringComparer.Ordinal);
            var missing = new List<string>();
            var stale = new List<string>();
            var delta = new List<string>();

            foreach (string key in compiled.RequiredKeys)
            {
                string sourceVal = compiled.BaseEntries[key];
                if (staleSet.Contains(key))
                {
                    stale.Add(key);
                    delta.Add(key);
                    continue;
                }

                if (existingDict is null ||
                    !TryGetLocaleValueWithLegacyMetaFallback(existingDict, key, existingBaseDict, sourceVal, out string? translated) ||
                    translated is null ||
                    (string.IsNullOrWhiteSpace(translated) &&
                     !IsIntentionalEmptyTranslation(key, sourceVal, translated, previousHashes, staleSet)) ||
                    !HasMatchingPlaceholders(sourceVal, translated) ||
                    HasUntranslatedCjkResidue(info.Code, baseContainsCjk, sourceVal, translated))
                {
                    missing.Add(key);
                    delta.Add(key);
                    continue;
                }

                validExisting[key] = translated;
            }

            CustomWidgetLocaleSyncStatus status = delta.Count == 0
                ? CustomWidgetLocaleSyncStatus.UpToDate
                : (validExisting.Count == 0
                    ? CustomWidgetLocaleSyncStatus.PendingFull
                    : CustomWidgetLocaleSyncStatus.PendingDiff);

            plans.Add(new CustomWidgetLocaleTaskPlan(
                Culture: info.Code,
                NativeName: info.NativeName,
                DisplayName: info.EnglishName,
                Status: status,
                TotalRequiredKeys: compiled.RequiredKeys.Count,
                MissingKeys: missing,
                StaleKeys: stale,
                DeltaKeys: delta,
                ExistingValidEntries: validExisting));
        }

        return new CustomWidgetI18nSyncPlan(
            compiled.CompiledManifestJson,
            compiled.ManifestChanged,
            compiled.BaseLocale,
            compiled.RequiredKeys,
            compiled.BaseEntries,
            compiled.NextMeta,
            compiled.SeededCatalog,
            plans);
    }

    public static IReadOnlySet<int> ExtractPositionalPlaceholders(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{'))
        {
            return EmptyPlaceholderSet;
        }

        MatchCollection matches = PositionalPlaceholderRegex.Matches(text);
        if (matches.Count == 0)
        {
            return EmptyPlaceholderSet;
        }

        var set = new HashSet<int>();
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups["idx"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
            {
                set.Add(idx);
            }
        }

        return set;
    }

    public static bool HasMatchingPlaceholders(string sourceTemplate, string candidateTranslation)
    {
        IReadOnlySet<int> expected = ExtractPositionalPlaceholders(sourceTemplate);
        IReadOnlySet<int> actual = ExtractPositionalPlaceholders(candidateTranslation);
        return expected.SetEquals(actual);
    }

    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsKana(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char ch in text)
        {
            if ((ch is >= '\u3040' and <= '\u30FF') ||
                (ch is >= '\u31F0' and <= '\u31FF'))
            {
                return true;
            }
        }

        return false;
    }

    public static CustomWidgetSupportedLocaleInfo ResolveLocaleInfo(string cultureCode)
    {
        foreach (CustomWidgetSupportedLocaleInfo item in CustomWidgetLocaleCatalog.SupportedLocales)
        {
            if (string.Equals(item.Code, cultureCode, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return new CustomWidgetSupportedLocaleInfo(cultureCode, cultureCode, cultureCode);
    }

    private static readonly HashSet<int> EmptyPlaceholderSet = [];

    public static bool HasUntranslatedCjkResidue(
        string targetCulture,
        bool baseContainsCjk,
        string sourceText,
        string translatedText)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        // Japanese Hiragana/Katakana residue in any non-Japanese target locale
        if (!targetCulture.StartsWith("ja", StringComparison.OrdinalIgnoreCase) &&
            ContainsKana(sourceText) &&
            ContainsKana(translatedText))
        {
            return true;
        }

        if (!baseContainsCjk || !ContainsCjk(sourceText))
        {
            return false;
        }

        // zh-CN, zh-TW, and ja-JP legitimately contain CJK ideographs / Kanji.
        if (targetCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            targetCulture.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(sourceText.Trim(), translatedText.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return ContainsCjk(translatedText);
    }

    private static void WriteRootManifestObject(
        Utf8JsonWriter writer,
        JsonElement root,
        CompilerContext ctx)
    {
        writer.WriteStartObject();
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            // Strip any forbidden inline "i18n" property during compilation
            if (string.Equals(prop.Name, "i18n", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(prop.Name, "name", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                writer.WriteString(
                    "name",
                    CompileCopyValue(prop.Value.GetString() ?? string.Empty, "meta.name", allowReuseExistingValueKey: false, isUnit: false, ctx));
                continue;
            }

            if (string.Equals(prop.Name, "description", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                writer.WriteString(
                    "description",
                    CompileCopyValue(prop.Value.GetString() ?? string.Empty, "meta.description", allowReuseExistingValueKey: false, isUnit: false, ctx));
                continue;
            }

            if (string.Equals(prop.Name, "settings", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("settings");
                    WriteSettingsArray(writer, prop.Value, ctx);
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("settings");
                    WriteSettingsObject(writer, prop.Value, ctx);
                    continue;
                }
            }

            if (string.Equals(prop.Name, "capsule", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("capsule");
                WriteCapsuleObject(writer, prop.Value, ctx);
                continue;
            }

            if (string.Equals(prop.Name, "onDrop", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("onDrop");
                WriteRootOnDropObject(writer, prop.Value, ctx);
                continue;
            }

            if (string.Equals(prop.Name, "popovers", StringComparison.Ordinal) &&
                prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                writer.WritePropertyName("popovers");
                WriteRootPopovers(writer, prop.Value, ctx);
                continue;
            }

            if (string.Equals(prop.Name, "view", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("view");
                WriteViewNodeObject(writer, prop.Value, "view", ctx);
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteRootPopovers(
        Utf8JsonWriter writer,
        JsonElement popoversEl,
        CompilerContext ctx)
    {
        if (popoversEl.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            int idx = 0;
            foreach (JsonProperty popProp in popoversEl.EnumerateObject())
            {
                if (popProp.Value.ValueKind != JsonValueKind.Object)
                {
                    popProp.WriteTo(writer);
                    idx++;
                    continue;
                }

                string popSlug = SlugifySegment(popProp.Name, $"p{idx + 1}");
                writer.WritePropertyName(popProp.Name);
                WriteSinglePopoverDefinitionObject(writer, popProp.Value, $"popover.{popSlug}", ctx);
                idx++;
            }
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartArray();
        int arrIdx = 0;
        foreach (JsonElement item in popoversEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                item.WriteTo(writer);
                arrIdx++;
                continue;
            }

            string popSlug = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? SlugifySegment(idEl.GetString(), $"p{arrIdx + 1}")
                : $"p{arrIdx + 1}";
            WriteSinglePopoverDefinitionObject(writer, item, $"popover.{popSlug}", ctx);
            arrIdx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteSinglePopoverDefinitionObject(
        Utf8JsonWriter writer,
        JsonElement popObj,
        string keyPrefix,
        CompilerContext ctx)
    {
        writer.WriteStartObject();
        foreach (JsonProperty prop in popObj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String &&
                prop.Name is "title" or "subtitle")
            {
                writer.WriteString(
                    prop.Name,
                    CompileCopyValue(
                        prop.Value.GetString() ?? string.Empty,
                        $"{keyPrefix}.{prop.Name}",
                        allowReuseExistingValueKey: true,
                        isUnit: false,
                        ctx));
                continue;
            }

            if (string.Equals(prop.Name, "view", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("view");
                WriteViewNodeObject(writer, prop.Value, $"{keyPrefix}.view", ctx);
                continue;
            }

            if (string.Equals(prop.Name, "children", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("children");
                writer.WriteStartArray();
                int cIdx = 0;
                foreach (JsonElement child in prop.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        string childType = child.TryGetProperty("type", out JsonElement ctEl) && ctEl.ValueKind == JsonValueKind.String
                            ? SlugifySegment(ctEl.GetString(), "n")
                            : "n";
                        WriteViewNodeObject(writer, child, $"{keyPrefix}.{childType}{cIdx + 1}", ctx);
                    }
                    else
                    {
                        child.WriteTo(writer);
                    }
                    cIdx++;
                }
                writer.WriteEndArray();
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteSettingsArray(Utf8JsonWriter writer, JsonElement settingsArr, CompilerContext ctx)
    {
        writer.WriteStartArray();
        int idx = 0;
        foreach (JsonElement item in settingsArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                item.WriteTo(writer);
                idx++;
                continue;
            }

            string settingKey = item.TryGetProperty("key", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String
                ? SlugifySegment(kEl.GetString(), $"s{idx + 1}")
                : $"s{idx + 1}";

            writer.WriteStartObject();
            foreach (JsonProperty prop in item.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    prop.Name is "label" or "description" or "placeholder")
                {
                    string suffix = prop.Name == "description" ? "desc" : prop.Name;
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"settings.{settingKey}.{suffix}",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                if (string.Equals(prop.Name, "options", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("options");
                    WriteOptionsArray(writer, prop.Value, $"settings.{settingKey}.opt", ctx);
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            idx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteSettingsObject(Utf8JsonWriter writer, JsonElement settingsObj, CompilerContext ctx)
    {
        writer.WriteStartObject();
        int idx = 0;
        foreach (JsonProperty settingProp in settingsObj.EnumerateObject())
        {
            if (settingProp.Value.ValueKind != JsonValueKind.Object)
            {
                settingProp.WriteTo(writer);
                idx++;
                continue;
            }

            string rawKey = settingProp.Value.TryGetProperty("key", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(kEl.GetString())
                ? kEl.GetString()!
                : settingProp.Name;
            string settingKey = SlugifySegment(rawKey, $"s{idx + 1}");

            writer.WritePropertyName(settingProp.Name);
            writer.WriteStartObject();
            foreach (JsonProperty prop in settingProp.Value.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    prop.Name is "label" or "description" or "placeholder")
                {
                    string suffix = prop.Name == "description" ? "desc" : prop.Name;
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"settings.{settingKey}.{suffix}",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                if (string.Equals(prop.Name, "options", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("options");
                    WriteOptionsArray(writer, prop.Value, $"settings.{settingKey}.opt", ctx);
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            idx++;
        }
        writer.WriteEndObject();
    }

    private static void WriteOptionsArray(
        Utf8JsonWriter writer,
        JsonElement optionsArr,
        string keyPrefix,
        CompilerContext ctx)
    {
        writer.WriteStartArray();
        int optIdx = 0;
        foreach (JsonElement opt in optionsArr.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object)
            {
                opt.WriteTo(writer);
                optIdx++;
                continue;
            }

            string optValSlug = opt.TryGetProperty("value", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String
                ? SlugifySegment(vEl.GetString(), $"o{optIdx + 1}")
                : $"o{optIdx + 1}";

            writer.WriteStartObject();
            foreach (JsonProperty prop in opt.EnumerateObject())
            {
                if (string.Equals(prop.Name, "label", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    writer.WriteString(
                        "label",
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"{keyPrefix}.{optValSlug}",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                // Never mutate option "value"
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            optIdx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteCapsuleObject(Utf8JsonWriter writer, JsonElement capsuleObj, CompilerContext ctx)
    {
        writer.WriteStartObject();
        foreach (JsonProperty prop in capsuleObj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String &&
                prop.Name is "title" or "summary" or "badge")
            {
                writer.WriteString(
                    prop.Name,
                    CompileCopyValue(
                        prop.Value.GetString() ?? string.Empty,
                        $"capsule.{prop.Name}",
                        allowReuseExistingValueKey: true,
                        isUnit: false,
                        ctx));
                continue;
            }

            if (string.Equals(prop.Name, "primaryAction", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("primaryAction");
                writer.WriteStartObject();
                foreach (JsonProperty paProp in prop.Value.EnumerateObject())
                {
                    if (string.Equals(paProp.Name, "tooltip", StringComparison.Ordinal) &&
                        paProp.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteString(
                            "tooltip",
                            CompileCopyValue(
                                paProp.Value.GetString() ?? string.Empty,
                                "capsule.tooltip",
                                allowReuseExistingValueKey: true,
                                isUnit: false,
                                ctx));
                        continue;
                    }

                    if (string.Equals(paProp.Name, "onClick", StringComparison.Ordinal))
                    {
                        writer.WritePropertyName("onClick");
                        WriteActionOrArray(writer, paProp.Value, "capsule.action", ctx);
                        continue;
                    }

                    paProp.WriteTo(writer);
                }
                writer.WriteEndObject();
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteRootOnDropObject(Utf8JsonWriter writer, JsonElement dropObj, CompilerContext ctx)
    {
        writer.WriteStartObject();
        foreach (JsonProperty prop in dropObj.EnumerateObject())
        {
            if (string.Equals(prop.Name, "action", StringComparison.Ordinal))
            {
                writer.WritePropertyName("action");
                WriteActionOrArray(writer, prop.Value, "drop.action", ctx);
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteViewNodeObject(
        Utf8JsonWriter writer,
        JsonElement node,
        string pathPrefix,
        CompilerContext ctx)
    {
        string nodeType = node.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() ?? "node"
            : "node";
        string? explicitId = node.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;
        string nodeKeyBase = !string.IsNullOrWhiteSpace(explicitId)
            ? $"ui.{SlugifySegment(explicitId, "node")}"
            : pathPrefix;

        writer.WriteStartObject();
        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                bool isLocalizableCopyProp = ViewNodeLocalizableProps.Contains(prop.Name) ||
                    (string.Equals(prop.Name, "value", StringComparison.Ordinal) &&
                     ValueDisplayNodeTypes.Contains(nodeType));

                if (isLocalizableCopyProp)
                {
                    bool isUnit = string.Equals(prop.Name, "unit", StringComparison.Ordinal);
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"{nodeKeyBase}.{prop.Name}",
                            allowReuseExistingValueKey: true,
                            isUnit: isUnit,
                            ctx));
                    continue;
                }
            }

            if (string.Equals(prop.Name, "options", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("options");
                WriteOptionsArray(writer, prop.Value, $"{nodeKeyBase}.opt", ctx);
                continue;
            }

            if (string.Equals(prop.Name, "columns", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Array &&
                string.Equals(nodeType, "Table", StringComparison.OrdinalIgnoreCase))
            {
                writer.WritePropertyName("columns");
                WriteTableColumnsArray(writer, prop.Value, $"{nodeKeyBase}.col", ctx);
                continue;
            }

            if (string.Equals(prop.Name, "series", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("series");
                WriteChartSeriesArray(writer, prop.Value, $"{nodeKeyBase}.series", ctx);
                continue;
            }

            if (string.Equals(prop.Name, "elements", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Array &&
                string.Equals(nodeType, "Canvas", StringComparison.OrdinalIgnoreCase))
            {
                writer.WritePropertyName("elements");
                WriteCanvasElementsArray(writer, prop.Value, $"{nodeKeyBase}.el", ctx);
                continue;
            }

            if (string.Equals(prop.Name, "emptyState", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("emptyState");
                writer.WriteStartObject();
                foreach (JsonProperty esProp in prop.Value.EnumerateObject())
                {
                    if (esProp.Value.ValueKind == JsonValueKind.String &&
                        esProp.Name is "title" or "subtitle")
                    {
                        writer.WriteString(
                            esProp.Name,
                            CompileCopyValue(
                                esProp.Value.GetString() ?? string.Empty,
                                $"{nodeKeyBase}.empty.{esProp.Name}",
                                allowReuseExistingValueKey: true,
                                isUnit: false,
                                ctx));
                        continue;
                    }

                    esProp.WriteTo(writer);
                }
                writer.WriteEndObject();
                continue;
            }

            if (string.Equals(prop.Name, "itemTemplate", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("itemTemplate");
                WriteViewNodeObject(writer, prop.Value, $"{nodeKeyBase}.item", ctx);
                continue;
            }

            if (prop.Name is "children" or "tabs" or "hoverActions" &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(prop.Name);
                writer.WriteStartArray();
                int childIdx = 0;
                foreach (JsonElement child in prop.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        string childType = child.TryGetProperty("type", out JsonElement ctEl) && ctEl.ValueKind == JsonValueKind.String
                            ? SlugifySegment(ctEl.GetString(), "n")
                            : "n";
                        WriteViewNodeObject(writer, child, $"{nodeKeyBase}.{childType}{childIdx + 1}", ctx);
                    }
                    else
                    {
                        child.WriteTo(writer);
                    }
                    childIdx++;
                }
                writer.WriteEndArray();
                continue;
            }

            if (prop.Name is "onClick" or "onChange" or "onToggle" or "onSubmit" or "onDrop" or
                             "onRowClick" or "onItemClick" or "onReorder" or "onInlineCommit")
            {
                writer.WritePropertyName(prop.Name);
                WriteActionOrArray(writer, prop.Value, $"{nodeKeyBase}.{prop.Name}", ctx);
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteTableColumnsArray(
        Utf8JsonWriter writer,
        JsonElement columnsArr,
        string keyPrefix,
        CompilerContext ctx)
    {
        writer.WriteStartArray();
        int colIdx = 0;
        foreach (JsonElement col in columnsArr.EnumerateArray())
        {
            if (col.ValueKind != JsonValueKind.Object)
            {
                col.WriteTo(writer);
                colIdx++;
                continue;
            }

            string colSlug = col.TryGetProperty("key", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String
                ? SlugifySegment(kEl.GetString(), $"c{colIdx + 1}")
                : col.TryGetProperty("field", out JsonElement fEl) && fEl.ValueKind == JsonValueKind.String
                    ? SlugifySegment(fEl.GetString(), $"c{colIdx + 1}")
                    : $"c{colIdx + 1}";

            writer.WriteStartObject();
            foreach (JsonProperty prop in col.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    prop.Name is "header" or "title")
                {
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"{keyPrefix}.{colSlug}",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            colIdx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteChartSeriesArray(
        Utf8JsonWriter writer,
        JsonElement seriesArr,
        string keyPrefix,
        CompilerContext ctx)
    {
        writer.WriteStartArray();
        int sIdx = 0;
        foreach (JsonElement ser in seriesArr.EnumerateArray())
        {
            if (ser.ValueKind != JsonValueKind.Object)
            {
                ser.WriteTo(writer);
                sIdx++;
                continue;
            }

            string serSlug = ser.TryGetProperty("field", out JsonElement fEl) && fEl.ValueKind == JsonValueKind.String
                ? SlugifySegment(fEl.GetString(), $"s{sIdx + 1}")
                : ser.TryGetProperty("valueField", out JsonElement vfEl) && vfEl.ValueKind == JsonValueKind.String
                    ? SlugifySegment(vfEl.GetString(), $"s{sIdx + 1}")
                    : $"s{sIdx + 1}";

            writer.WriteStartObject();
            foreach (JsonProperty prop in ser.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    prop.Name is "label" or "name" or "title")
                {
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"{keyPrefix}.{serSlug}",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            sIdx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteCanvasElementsArray(
        Utf8JsonWriter writer,
        JsonElement elementsArr,
        string keyPrefix,
        CompilerContext ctx)
    {
        writer.WriteStartArray();
        int elIdx = 0;
        foreach (JsonElement el in elementsArr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                el.WriteTo(writer);
                elIdx++;
                continue;
            }

            string elType = el.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? string.Empty
                : el.TryGetProperty("kind", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String
                    ? kEl.GetString() ?? string.Empty
                    : string.Empty;
            string elSlug = el.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(idEl.GetString())
                ? SlugifySegment(idEl.GetString(), $"e{elIdx + 1}")
                : $"e{elIdx + 1}";

            writer.WriteStartObject();
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                if (string.Equals(elType, "text", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(prop.Name, "text", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    writer.WriteString(
                        "text",
                        CompileCopyValue(
                            prop.Value.GetString() ?? string.Empty,
                            $"{keyPrefix}.{elSlug}.text",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            elIdx++;
        }
        writer.WriteEndArray();
    }

    private static void WriteActionOrArray(
        Utf8JsonWriter writer,
        JsonElement actionOrArr,
        string pathPrefix,
        CompilerContext ctx)
    {
        if (actionOrArr.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            int idx = 0;
            foreach (JsonElement el in actionOrArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    WriteSingleActionObject(writer, el, $"{pathPrefix}{idx + 1}", ctx);
                }
                else
                {
                    el.WriteTo(writer);
                }
                idx++;
            }
            writer.WriteEndArray();
        }
        else if (actionOrArr.ValueKind == JsonValueKind.Object)
        {
            WriteSingleActionObject(writer, actionOrArr, pathPrefix, ctx);
        }
        else
        {
            actionOrArr.WriteTo(writer);
        }
    }

    private static void WriteSingleActionObject(
        Utf8JsonWriter writer,
        JsonElement actionObj,
        string pathPrefix,
        CompilerContext ctx)
    {
        string actionType = actionObj.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() ?? string.Empty
            : string.Empty;
        bool isToastOrNotify = ToastActionTypes.Contains(actionType);
        bool isClipboardCopy = string.Equals(actionType, "clipboard.copy", StringComparison.OrdinalIgnoreCase);

        writer.WriteStartObject();
        foreach (JsonProperty prop in actionObj.EnumerateObject())
        {
            if (isToastOrNotify &&
                prop.Value.ValueKind == JsonValueKind.String &&
                prop.Name is "title" or "message" or "text")
            {
                writer.WriteString(
                    prop.Name,
                    CompileCopyValue(
                        prop.Value.GetString() ?? string.Empty,
                        $"{pathPrefix}.{prop.Name}",
                        allowReuseExistingValueKey: true,
                        isUnit: false,
                        ctx));
                continue;
            }

            if (isClipboardCopy &&
                prop.Value.ValueKind == JsonValueKind.String &&
                string.Equals(prop.Name, "text", StringComparison.Ordinal))
            {
                string rawCopyText = prop.Value.GetString() ?? string.Empty;
                if (rawCopyText.Contains("$i18n.", StringComparison.Ordinal) ||
                    AnyTFuncRegex.IsMatch(rawCopyText) ||
                    (!rawCopyText.Contains("${", StringComparison.Ordinal) && IsTranslatableClipboardProse(rawCopyText)))
                {
                    writer.WriteString(
                        prop.Name,
                        CompileCopyValue(
                            rawCopyText,
                            $"{pathPrefix}.text",
                            allowReuseExistingValueKey: true,
                            isUnit: false,
                            ctx));
                    continue;
                }
            }

            if (string.Equals(prop.Name, "then", StringComparison.Ordinal))
            {
                writer.WritePropertyName("then");
                WriteActionOrArray(writer, prop.Value, $"{pathPrefix}.then", ctx);
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static string CompileCopyValue(
        string raw,
        string suggestedKey,
        bool allowReuseExistingValueKey,
        bool isUnit,
        CompilerContext ctx)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        // 1. Exact ${$i18n.key} binding
        Match directMatch = DirectI18nWholeTokenRegex.Match(raw.Trim());
        if (directMatch.Success)
        {
            string existingKey = directMatch.Groups["key"].Value;
            ctx.RegisterExistingKey(existingKey);
            return raw;
        }

        // 2. Already contains $i18n.* or t('...') references anywhere -> register referenced keys & keep expression intact
        if (raw.Contains("$i18n.", StringComparison.Ordinal) ||
            AnyTFuncRegex.IsMatch(raw))
        {
            RegisterReferencedKeysInText(raw, ctx);
            return raw;
        }

        // 3. Pure static string (no ${...})
        if (!raw.Contains("${", StringComparison.Ordinal))
        {
            if (!IsTranslatableStaticText(raw, isUnit))
            {
                return raw;
            }

            ctx.ObserveHumanSample(raw);
            string key = ctx.AllocateKeyForSourceText(suggestedKey, raw, allowReuseExistingValueKey);
            return $"${{$i18n.{key}}}";
        }

        // 4. Parse segments of text and ${...} expressions
        if (!TryParseTemplateSegments(raw, out List<TemplateSegment> segments))
        {
            return raw;
        }

        // Single ${expr} spanning the whole string
        if (segments.Count == 1 && segments[0].IsExpression)
        {
            string rewrittenExpr = RewriteUiLiteralsInsideExpression(segments[0].Content, suggestedKey, ctx);
            return $"${{{rewrittenExpr}}}";
        }

        // Mixed literal + ${expr} segments: check if outside literals contain any translatable human letters
        bool hasOutsideHumanLetters = segments
            .Where(static s => !s.IsExpression)
            .Any(s => ContainsUnicodeLetter(s.Content) && (!isUnit || !TechnicalUnits.Contains(s.Content.Trim())));

        if (!hasOutsideHumanLetters)
        {
            // Still rewrite any ternary/coalescing UI literals inside ${...} segments
            var unchangedBuilder = new StringBuilder(raw.Length + 16);
            int exprCounter = 0;
            foreach (TemplateSegment seg in segments)
            {
                if (!seg.IsExpression)
                {
                    unchangedBuilder.Append(seg.Content);
                }
                else
                {
                    exprCounter++;
                    string inner = RewriteUiLiteralsInsideExpression(seg.Content, $"{suggestedKey}.e{exprCounter}", ctx);
                    unchangedBuilder.Append("${").Append(inner).Append('}');
                }
            }
            return unchangedBuilder.ToString();
        }

        // Build parameterized t('key', arg0, arg1, ...) template with {0}, {1}, ...
        var templateSb = new StringBuilder(raw.Length);
        var argExprs = new List<string>();
        foreach (TemplateSegment seg in segments)
        {
            if (!seg.IsExpression)
            {
                templateSb.Append(seg.Content);
                ctx.ObserveHumanSample(seg.Content);
            }
            else
            {
                int argIdx = argExprs.Count;
                string rewrittenArg = RewriteUiLiteralsInsideExpression(
                    seg.Content.Trim(),
                    $"{suggestedKey}.arg{argIdx}",
                    ctx);
                argExprs.Add(rewrittenArg);
                templateSb.Append('{')
                          .Append(argIdx.ToString(CultureInfo.InvariantCulture))
                          .Append('}');
            }
        }

        string templateText = templateSb.ToString();
        string templateKey = ctx.AllocateKeyForSourceText(suggestedKey, templateText, allowReuseExistingValueKey);
        string escapedKey = templateKey.Replace("'", "\\'", StringComparison.Ordinal);
        return $"${{t('{escapedKey}', {string.Join(", ", argExprs)})}}";
    }

    private static string RewriteUiLiteralsInsideExpression(
        string expr,
        string suggestedKeyPrefix,
        CompilerContext ctx)
    {
        if (string.IsNullOrWhiteSpace(expr) ||
            (!expr.Contains('\'') && !expr.Contains('"')))
        {
            return expr;
        }

        var sb = new StringBuilder(expr.Length + 24);
        int i = 0;
        int parenDepth = 0;
        int litIdx = 0;

        while (i < expr.Length)
        {
            char c = expr[i];
            if (c == '(')
            {
                parenDepth++;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
                sb.Append(c);
                i++;
                continue;
            }

            if (c is '\'' or '"')
            {
                int quoteStart = i;
                char quoteChar = c;
                i++;
                var litSb = new StringBuilder();
                bool closed = false;
                while (i < expr.Length)
                {
                    char qc = expr[i];
                    if (qc == '\\' && i + 1 < expr.Length)
                    {
                        litSb.Append(expr[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (qc == quoteChar)
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    litSb.Append(qc);
                    i++;
                }

                string rawLiteralSlice = expr[quoteStart..i];
                if (!closed || parenDepth > 0)
                {
                    sb.Append(rawLiteralSlice);
                    continue;
                }

                // Never rewrite comparison literals around ==, !=, >=, <=, >, <
                if (IsAdjacentToComparisonOperator(expr, quoteStart, i))
                {
                    sb.Append(rawLiteralSlice);
                    continue;
                }

                string literalVal = litSb.ToString();
                if (!IsTranslatableExpressionBranchLiteral(literalVal))
                {
                    sb.Append(rawLiteralSlice);
                    continue;
                }

                litIdx++;
                ctx.ObserveHumanSample(literalVal);
                string branchKey = ctx.AllocateKeyForSourceText(
                    $"{suggestedKeyPrefix}.b{litIdx}",
                    literalVal,
                    allowReuseExistingValueKey: true);
                sb.Append("$i18n.").Append(branchKey);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsAdjacentToComparisonOperator(string expr, int quoteStart, int afterQuoteEnd)
    {
        int left = quoteStart - 1;
        while (left >= 0 && char.IsWhiteSpace(expr[left]))
        {
            left--;
        }
        if (left >= 0 && expr[left] is '=' or '>' or '<')
        {
            return true;
        }

        int right = afterQuoteEnd;
        while (right < expr.Length && char.IsWhiteSpace(expr[right]))
        {
            right++;
        }
        if (right < expr.Length && expr[right] is '=' or '!' or '>' or '<')
        {
            return true;
        }

        return false;
    }

    private static bool IsTranslatableExpressionBranchLiteral(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith('#') ||
            TechnicalExpressionLiterals.Contains(trimmed) ||
            TechnicalUnits.Contains(trimmed))
        {
            return false;
        }

        return ContainsUnicodeLetter(trimmed);
    }

    private static bool IsTranslatableStaticText(string text, bool isUnit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (isUnit && TechnicalUnits.Contains(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsUnicodeLetter(trimmed);
    }

    private static bool IsTranslatableClipboardProse(string rawCopyText)
    {
        if (string.IsNullOrWhiteSpace(rawCopyText))
        {
            return false;
        }

        string trimmed = rawCopyText.Trim();
        if (!IsTranslatableStaticText(trimmed, isUnit: false))
        {
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(":\\", StringComparison.Ordinal) ||
            trimmed.StartsWith('/') ||
            trimmed.StartsWith("./", StringComparison.Ordinal) ||
            trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            trimmed.StartsWith("--", StringComparison.Ordinal) ||
            trimmed.Contains(" --", StringComparison.Ordinal) ||
            trimmed.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("npx ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("uvx ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("git ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("pwsh ", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')) ||
            trimmed.Contains("\":", StringComparison.Ordinal))
        {
            return false;
        }

        bool isAsciiOnly = true;
        foreach (char c in trimmed)
        {
            if (c > 0x7F)
            {
                isAsciiOnly = false;
                break;
            }
        }

        if (isAsciiOnly)
        {
            // Pure ASCII clipboard strings must look like multi-word natural prose rather than
            // single identifier tokens, API keys, or shell/KV assignments.
            if (!trimmed.Any(char.IsWhiteSpace) ||
                trimmed.Contains('=') ||
                trimmed.Contains(';') ||
                trimmed.Contains('|'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUnicodeLetter(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct TemplateSegment(bool IsExpression, string Content);

    private static bool TryParseTemplateSegments(string raw, out List<TemplateSegment> segments)
    {
        segments = [];
        int cursor = 0;
        while (cursor < raw.Length)
        {
            int start = raw.IndexOf("${", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                segments.Add(new TemplateSegment(false, raw[cursor..]));
                break;
            }

            if (start > cursor)
            {
                segments.Add(new TemplateSegment(false, raw[cursor..start]));
            }

            int end = FindMatchingBrace(raw, start + 2);
            if (end < 0)
            {
                return false;
            }

            segments.Add(new TemplateSegment(true, raw[(start + 2)..end]));
            cursor = end + 1;
        }

        return segments.Count > 0;
    }

    private static int FindMatchingBrace(string text, int startIndex)
    {
        int depth = 1;
        char quote = '\0';
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string? GetLegacyMetaAliasKey(string key) => key switch
    {
        "meta.name" => "widgetName",
        "meta.description" => "widgetDescription",
        _ => null
    };

    private static bool TryGetHashWithLegacyMetaFallback(
        IReadOnlyDictionary<string, string>? hashes,
        string key,
        out string? hash)
    {
        if (hashes is null)
        {
            hash = null;
            return false;
        }

        if (hashes.TryGetValue(key, out hash))
        {
            return true;
        }

        if (GetLegacyMetaAliasKey(key) is { } legacyKey && hashes.TryGetValue(legacyKey, out hash))
        {
            return true;
        }

        hash = null;
        return false;
    }

    private static bool TryGetLocaleValueWithLegacyMetaFallback(
        IReadOnlyDictionary<string, string> dict,
        string key,
        IReadOnlyDictionary<string, string>? existingBaseDict,
        string expectedBaseText,
        out string? value)
    {
        if (dict.TryGetValue(key, out value))
        {
            return true;
        }

        if (GetLegacyMetaAliasKey(key) is { } legacyKey && dict.TryGetValue(legacyKey, out string? legacyValue))
        {
            if (existingBaseDict is not null &&
                existingBaseDict.TryGetValue(legacyKey, out string? existingBaseValue) &&
                !string.Equals(existingBaseValue, expectedBaseText, StringComparison.Ordinal))
            {
                value = null;
                return false;
            }

            value = legacyValue;
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsIntentionalEmptyTranslation(
        string key,
        string sourceText,
        string candidateTranslation,
        IReadOnlyDictionary<string, string>? previousHashes,
        HashSet<string> staleKeys) =>
        candidateTranslation.Length == 0 &&
        !string.IsNullOrEmpty(sourceText) &&
        previousHashes is not null &&
        TryGetHashWithLegacyMetaFallback(previousHashes, key, out _) &&
        !staleKeys.Contains(key) &&
        HasMatchingPlaceholders(sourceText, candidateTranslation);

    private static void ScanCompiledElementReferences(JsonElement element, CompilerContext ctx)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string? text = element.GetString();
                if (!string.IsNullOrEmpty(text) &&
                    (text.Contains("$i18n.", StringComparison.Ordinal) || AnyTFuncRegex.IsMatch(text)))
                {
                    RegisterReferencedKeysInText(text, ctx);
                }
                break;

            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    ScanCompiledElementReferences(prop.Value, ctx);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ScanCompiledElementReferences(item, ctx);
                }
                break;
        }
    }

    private static void RegisterReferencedKeysInText(string text, CompilerContext ctx)
    {
        foreach (Match m in AnyI18nPropRegex.Matches(text))
        {
            string key = m.Groups["key"].Value;
            if (!string.IsNullOrWhiteSpace(key))
            {
                ctx.RegisterExistingKey(key);
            }
        }

        foreach (Match m in AnyTFuncRegex.Matches(text))
        {
            string rawKey = m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["dq"].Value;
            string key = rawKey.Replace("\\'", "'", StringComparison.Ordinal)
                               .Replace("\\\"", "\"", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(key))
            {
                ctx.RegisterExistingKey(key);
            }
        }
    }

    private static void ScanLogicJsKeys(string logicJs, CompilerContext ctx)
    {
        foreach (Match m in LogicCtxTRegex.Matches(logicJs))
        {
            string key = m.Groups["k1"].Success ? m.Groups["k1"].Value : m.Groups["k2"].Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string? fallback = m.Groups["f1"].Success
                ? m.Groups["f1"].Value
                : (m.Groups["f2"].Success ? m.Groups["f2"].Value : null);
            if (!string.IsNullOrEmpty(fallback))
            {
                fallback = fallback
                    .Replace("\\'", "'", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\n", "\n", StringComparison.Ordinal);
                ctx.ObserveHumanSample(fallback);
            }

            ctx.RegisterExistingKey(key, fallback);
        }
    }

    private static string SlugifySegment(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new StringBuilder(Math.Min(raw.Length, 24));
        bool prevSep = false;
        foreach (char c in raw.Trim())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                prevSep = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSep = false;
            }
            else if (!prevSep && sb.Length > 0)
            {
                sb.Append('_');
                prevSep = true;
            }

            if (sb.Length >= 20)
            {
                break;
            }
        }

        string slug = sb.ToString().Trim('_');
        return slug.Length > 0 ? slug : fallback;
    }

    private static string NormalizeJsonForComparison(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, PermissiveJsonOptions);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }
    }

    private sealed class CompilerContext
    {
        private readonly CustomWidgetLocaleCatalog? _existingCatalog;
        private readonly string _fallbackCulture;
        private readonly HashSet<string> _requiredKeySet = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _allocatedKeyToSource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sourceTextToReusableKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _scriptFallbacks = new(StringComparer.Ordinal);
        private bool _observedAnyHumanSample;
        private bool _observedCjkSample;
        private bool _observedKanaSample;
        private bool _observedHangulSample;
        private bool _observedArabicSample;
        private bool _observedCyrillicSample;
        private bool _observedDevanagariSample;
        private bool _observedBengaliSample;

        public CompilerContext(CustomWidgetLocaleCatalog? existingCatalog, string fallbackCulture)
        {
            _existingCatalog = existingCatalog;
            _fallbackCulture = CustomWidgetLocaleCatalog.IsValidCultureCode(fallbackCulture)
                ? fallbackCulture
                : "zh-CN";

            // Seed reverse map from existing base locale so identical unchanged text reuses existing keys
            string? seedLocale = existingCatalog?.Meta?.BaseLocale;
            if (!string.IsNullOrWhiteSpace(seedLocale) &&
                existingCatalog!.Locales.TryGetValue(seedLocale, out IReadOnlyDictionary<string, string>? seedDict))
            {
                foreach ((string k, string v) in seedDict)
                {
                    if (!string.IsNullOrWhiteSpace(v) &&
                        !string.Equals(k, "meta.name", StringComparison.Ordinal) &&
                        !string.Equals(k, "meta.description", StringComparison.Ordinal) &&
                        !string.Equals(k, "widgetName", StringComparison.Ordinal) &&
                        !string.Equals(k, "widgetDescription", StringComparison.Ordinal))
                    {
                        _sourceTextToReusableKey.TryAdd(v, k);
                    }
                }
            }
        }

        public List<string> RequiredKeys { get; } = [];

        public void ObserveHumanSample(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _observedAnyHumanSample = true;
            foreach (char ch in text)
            {
                if ((ch is >= '\u3040' and <= '\u30FF') || (ch is >= '\u31F0' and <= '\u31FF'))
                {
                    _observedKanaSample = true;
                }
                else if ((ch is >= '\uAC00' and <= '\uD7AF') || (ch is >= '\u1100' and <= '\u11FF'))
                {
                    _observedHangulSample = true;
                }
                else if ((ch is >= '\u0600' and <= '\u06FF') || (ch is >= '\u0750' and <= '\u077F'))
                {
                    _observedArabicSample = true;
                }
                else if (ch is >= '\u0400' and <= '\u04FF')
                {
                    _observedCyrillicSample = true;
                }
                else if (ch is >= '\u0900' and <= '\u097F')
                {
                    _observedDevanagariSample = true;
                }
                else if (ch is >= '\u0980' and <= '\u09FF')
                {
                    _observedBengaliSample = true;
                }
                else if (ch is >= '\u4E00' and <= '\u9FFF')
                {
                    _observedCjkSample = true;
                }
            }
        }

        public void RegisterExistingKey(string key, string? scriptFallback = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (_requiredKeySet.Add(key))
            {
                RequiredKeys.Add(key);
            }

            if (!string.IsNullOrWhiteSpace(scriptFallback))
            {
                _scriptFallbacks.TryAdd(key, scriptFallback);
            }
        }

        public string AllocateKeyForSourceText(
            string suggestedKey,
            string sourceText,
            bool allowReuseExistingValueKey)
        {
            if (allowReuseExistingValueKey &&
                _sourceTextToReusableKey.TryGetValue(sourceText, out string? reusedKey))
            {
                if (_requiredKeySet.Add(reusedKey))
                {
                    RequiredKeys.Add(reusedKey);
                }
                _allocatedKeyToSource[reusedKey] = sourceText;
                return reusedKey;
            }

            string candidate = suggestedKey;
            int suffix = 2;
            while (_allocatedKeyToSource.TryGetValue(candidate, out string? prevSource) &&
                   !string.Equals(prevSource, sourceText, StringComparison.Ordinal) ||
                   (!_allocatedKeyToSource.ContainsKey(candidate) && ExistingCatalogHasDifferentText(candidate, sourceText)))
            {
                candidate = $"{suggestedKey}_{suffix}";
                suffix++;
            }

            _allocatedKeyToSource[candidate] = sourceText;
            if (allowReuseExistingValueKey)
            {
                _sourceTextToReusableKey.TryAdd(sourceText, candidate);
            }
            if (_requiredKeySet.Add(candidate))
            {
                RequiredKeys.Add(candidate);
            }

            return candidate;
        }

        public string ResolveFinalBaseLocale()
        {
            if (_existingCatalog?.Meta?.BaseLocale is { Length: > 0 } metaBase &&
                CustomWidgetLocaleCatalog.IsValidCultureCode(metaBase))
            {
                return metaBase;
            }

            if (_observedKanaSample)
            {
                return "ja-JP";
            }

            if (_observedHangulSample)
            {
                return "ko-KR";
            }

            if (_observedArabicSample)
            {
                return "ar-SA";
            }

            if (_observedDevanagariSample)
            {
                return "hi-IN";
            }

            if (_observedBengaliSample)
            {
                return "bn-BD";
            }

            if (_observedCyrillicSample)
            {
                return "ru-RU";
            }

            if (_observedCjkSample)
            {
                if (_fallbackCulture.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                {
                    return "ja-JP";
                }

                if (_fallbackCulture.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-TW";
                }

                return "zh-CN";
            }

            if (_observedAnyHumanSample)
            {
                if (_fallbackCulture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("de", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("es", StringComparison.OrdinalIgnoreCase) ||
                    _fallbackCulture.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
                {
                    return CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(_fallbackCulture);
                }

                // Latin-script prose extracted while UI is CJK / Kana / Hangul / Arabic / Cyrillic / Indic -> English source copy
                return "en-US";
            }

            if (_existingCatalog is { HasAnyLocale: true })
            {
                if (_existingCatalog.Locales.ContainsKey(_fallbackCulture))
                {
                    return _fallbackCulture;
                }
                if (_existingCatalog.Locales.ContainsKey("zh-CN"))
                {
                    return "zh-CN";
                }
                if (_existingCatalog.Locales.ContainsKey("en-US"))
                {
                    return "en-US";
                }
            }

            return _fallbackCulture;
        }

        public string ResolveBaseSourceText(string key, string baseLocale)
        {
            if (_allocatedKeyToSource.TryGetValue(key, out string? allocated))
            {
                return allocated;
            }

            if (_existingCatalog is not null)
            {
                string? legacyKey = GetLegacyMetaAliasKey(key);

                if (_existingCatalog.Locales.TryGetValue(baseLocale, out IReadOnlyDictionary<string, string>? baseDict) &&
                    (baseDict.TryGetValue(key, out string? bVal) || (legacyKey is not null && baseDict.TryGetValue(legacyKey, out bVal))) &&
                    !string.IsNullOrEmpty(bVal))
                {
                    return bVal;
                }

                if (_existingCatalog.Locales.TryGetValue(_fallbackCulture, out IReadOnlyDictionary<string, string>? fbDict) &&
                    (fbDict.TryGetValue(key, out string? fVal) || (legacyKey is not null && fbDict.TryGetValue(legacyKey, out fVal))) &&
                    !string.IsNullOrEmpty(fVal))
                {
                    return fVal;
                }

                if (_existingCatalog.Locales.TryGetValue("zh-CN", out IReadOnlyDictionary<string, string>? zhDict) &&
                    (zhDict.TryGetValue(key, out string? zVal) || (legacyKey is not null && zhDict.TryGetValue(legacyKey, out zVal))) &&
                    !string.IsNullOrEmpty(zVal))
                {
                    return zVal;
                }

                if (_existingCatalog.Locales.TryGetValue("en-US", out IReadOnlyDictionary<string, string>? enDict) &&
                    (enDict.TryGetValue(key, out string? eVal) || (legacyKey is not null && enDict.TryGetValue(legacyKey, out eVal))) &&
                    !string.IsNullOrEmpty(eVal))
                {
                    return eVal;
                }

                foreach (IReadOnlyDictionary<string, string> anyDict in _existingCatalog.Locales.Values)
                {
                    if ((anyDict.TryGetValue(key, out string? anyVal) || (legacyKey is not null && anyDict.TryGetValue(legacyKey, out anyVal))) &&
                        !string.IsNullOrEmpty(anyVal))
                    {
                        return anyVal;
                    }
                }
            }

            if (_scriptFallbacks.TryGetValue(key, out string? scriptFb) && !string.IsNullOrEmpty(scriptFb))
            {
                return scriptFb;
            }

            return key;
        }

        private bool ExistingCatalogHasDifferentText(string key, string newText)
        {
            // For dedicated canonical keys (like meta.name, meta.description, capsule.title),
            // allow overwriting the existing key in-place so edits to a widget's title update meta.name
            // (triggering staleness re-translation) rather than spawning meta.name_2!
            if (key.StartsWith("meta.", StringComparison.Ordinal) ||
                key.StartsWith("capsule.", StringComparison.Ordinal) ||
                key.StartsWith("settings.", StringComparison.Ordinal))
            {
                return false;
            }

            if (_existingCatalog is null || !_requiredKeySet.Contains(key))
            {
                return false;
            }

            foreach (IReadOnlyDictionary<string, string> dict in _existingCatalog.Locales.Values)
            {
                if (dict.TryGetValue(key, out string? existingVal) &&
                    !string.Equals(existingVal, newText, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
