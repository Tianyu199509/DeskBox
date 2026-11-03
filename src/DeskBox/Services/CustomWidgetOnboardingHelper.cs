using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetSetupFieldSpec(
    CustomWidgetSettingDefinition Definition,
    string CurrentValue,
    bool IsSecret,
    bool IsPathLike,
    bool IsFilePathLike,
    bool NeedsImmediateAttention);

public sealed record CustomWidgetSetupInspection(
    IReadOnlyList<CustomWidgetSetupFieldSpec> Fields,
    int UnconfiguredSecretCount,
    int UnconfiguredPathCount)
{
    public bool HasAnyConfigurableFields => Fields.Count > 0;

    public bool RequiresAttention => UnconfiguredSecretCount > 0 || UnconfiguredPathCount > 0;
}

public sealed record CustomWidgetForkBundleResult(
    string NewManifestId,
    string SourceManifestId,
    string SourceVersion,
    string DisplayTitle,
    string ManifestJson,
    string? LogicJs,
    CustomWidgetLocaleCatalog? Locales);

/// <summary>
/// Pure AOT-safe helper for:
/// 1) Inspecting imported widget bundles (<c>.deskbox-widget</c> / <c>.dbbox</c> / <c>deskbox://install-widget</c>)
///    to detect unconfigured DPAPI secrets and sanitized <c>%USERPROFILE%</c> / path settings for First-Run Setup;
/// 2) Producing clean, non-colliding One-Click Fork bundles with full <c>logic.js</c> + 13-locale preservation
///    and <c>forkedFromId</c> / <c>forkedFromVersion</c> lineage metadata.
/// </summary>
public static class CustomWidgetOnboardingHelper
{
    private static readonly Regex LeadingI18nBindingRegex = new(
        @"^\s*\$\{\s*\$i18n\.([A-Za-z0-9_.\-]+)(?:\s*\?\?[^}]*)?\}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] WellKnownTitleLocaleKeys =
    [
        "meta.name",
        "widgetName",
        "name"
    ];

    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonWriterOptions IndentedJsonWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static CustomWidgetSetupInspection InspectFirstRunSetupRequirements(
        CustomWidgetManifest manifest,
        string? activeCulture = null,
        IReadOnlyDictionary<string, string>? currentSettings = null,
        IReadOnlyDictionary<string, string>? currentSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string culture = string.IsNullOrWhiteSpace(activeCulture) ? "zh-CN" : activeCulture;
        IReadOnlyList<CustomWidgetSettingDefinition> localizedSettings = manifest.ResolveLocalizedSettings(culture);
        if (localizedSettings.Count == 0)
        {
            return new CustomWidgetSetupInspection([], 0, 0);
        }

        var fields = new List<CustomWidgetSetupFieldSpec>(localizedSettings.Count);
        int unconfiguredSecrets = 0;
        int unconfiguredPaths = 0;

        foreach (CustomWidgetSettingDefinition def in localizedSettings)
        {
            if (string.IsNullOrWhiteSpace(def.Key) ||
                def.Key.StartsWith("__", StringComparison.Ordinal) ||
                string.Equals(def.Type, "moduleList", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                string secretVal = currentSecrets is not null &&
                                   currentSecrets.TryGetValue(def.Key, out string? existingSec)
                    ? existingSec ?? string.Empty
                    : string.Empty;
                bool missingSecret = string.IsNullOrWhiteSpace(secretVal);
                if (missingSecret)
                {
                    unconfiguredSecrets++;
                }

                fields.Add(new CustomWidgetSetupFieldSpec(
                    Definition: def,
                    CurrentValue: secretVal,
                    IsSecret: true,
                    IsPathLike: false,
                    IsFilePathLike: false,
                    NeedsImmediateAttention: missingSecret));
                continue;
            }

            string? savedVal = null;
            bool hasExplicitSetting = currentSettings is not null &&
                                      currentSettings.TryGetValue(def.Key, out savedVal);
            string currentVal = hasExplicitSetting
                ? savedVal ?? string.Empty
                : def.DefaultValue ?? string.Empty;

            (bool isPathLike, bool isFilePathLike) = ClassifyPathSetting(def, currentVal);
            bool hasSanitizedPlaceholder =
                currentVal.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ||
                currentVal.StartsWith("~/", StringComparison.Ordinal) ||
                currentVal.StartsWith("~\\", StringComparison.Ordinal);
            bool staleDefaultPath =
                !hasExplicitSetting &&
                isPathLike &&
                !string.IsNullOrWhiteSpace(currentVal) &&
                Path.IsPathRooted(currentVal) &&
                !File.Exists(currentVal) &&
                !Directory.Exists(currentVal);
            bool needsPathSetup = hasSanitizedPlaceholder ||
                                  (isPathLike && string.IsNullOrWhiteSpace(currentVal)) ||
                                  staleDefaultPath;
            if (needsPathSetup)
            {
                unconfiguredPaths++;
            }

            fields.Add(new CustomWidgetSetupFieldSpec(
                Definition: def,
                CurrentValue: currentVal,
                IsSecret: false,
                IsPathLike: isPathLike,
                IsFilePathLike: isFilePathLike,
                NeedsImmediateAttention: needsPathSetup));
        }

        // Surface fields needing immediate setup first (secrets -> paths -> optional preferences).
        fields.Sort(static (a, b) =>
        {
            int attn = b.NeedsImmediateAttention.CompareTo(a.NeedsImmediateAttention);
            if (attn != 0)
            {
                return attn;
            }

            int sec = b.IsSecret.CompareTo(a.IsSecret);
            if (sec != 0)
            {
                return sec;
            }

            return b.IsPathLike.CompareTo(a.IsPathLike);
        });

        return new CustomWidgetSetupInspection(fields, unconfiguredSecrets, unconfiguredPaths);
    }

    public static string ExpandUserProfilePlaceholder(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return rawValue.Trim();
        }

        string trimmed = rawValue.Trim();
        if (trimmed.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase);
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(userProfile, trimmed[2..]);
        }

        return trimmed;
    }

    public static CustomWidgetForkBundleResult CreateForkBundle(
        string rawManifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? activeCulture = null,
        string? forkTitleSuffix = null,
        bool preserveUntrustedOrigin = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawManifestJson);

        string effectiveCulture = LocalizationService.NormalizeCultureCode(
            string.IsNullOrWhiteSpace(activeCulture) ? "zh-CN" : activeCulture);

        string upgradedJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            rawManifestJson,
            logicJs,
            locales,
            out string? effectiveLogicJs,
            out CustomWidgetLocaleCatalog? effectiveLocales);
        if (!string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            upgradedJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(upgradedJson, effectiveLogicJs);
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(upgradedJson);
        CustomWidgetManifest? sourceManifest = parsed.Manifest;
        string sourceId = !string.IsNullOrWhiteSpace(sourceManifest?.Id)
            ? sourceManifest!.Id.Trim()
            : "custom-widget";
        string sourceVersion = !string.IsNullOrWhiteSpace(sourceManifest?.Version)
            ? sourceManifest!.Version.Trim()
            : "1.0.0";

        string newManifestId = BuildUniqueForkManifestId(sourceId);
        string rawName = sourceManifest?.Name ?? "Widget";

        CustomWidgetLocaleCatalog? clonedLocales = CloneLocaleCatalog(effectiveLocales);
        string updatedRawName = rawName;
        string? updatedCapsuleTitle = null;
        string activeSuffix = ResolveForkSuffixForCulture(effectiveCulture, forkTitleSuffix);

        Match i18nMatch = LeadingI18nBindingRegex.Match(rawName);
        var titleKeysToUpdate = new HashSet<string>(WellKnownTitleLocaleKeys, StringComparer.Ordinal);
        if (i18nMatch.Success)
        {
            titleKeysToUpdate.Add(i18nMatch.Groups[1].Value);
        }
        else
        {
            updatedRawName = AppendSuffixOnce(rawName, activeSuffix);
        }

        if (sourceManifest?.Capsule?.Title is { Length: > 0 } rawCapsuleTitle &&
            !string.IsNullOrWhiteSpace(rawCapsuleTitle))
        {
            Match capsuleI18nMatch = LeadingI18nBindingRegex.Match(rawCapsuleTitle);
            if (capsuleI18nMatch.Success)
            {
                titleKeysToUpdate.Add(capsuleI18nMatch.Groups[1].Value);
            }
            else if (!rawCapsuleTitle.Contains("${", StringComparison.Ordinal))
            {
                updatedCapsuleTitle = AppendSuffixOnce(rawCapsuleTitle, activeSuffix);
            }
        }

        if (clonedLocales is { HasAnyLocale: true })
        {
            clonedLocales = AppendForkSuffixToLocaleKeys(clonedLocales, titleKeysToUpdate, effectiveCulture, forkTitleSuffix);
        }

        string rewrittenJson = RewriteManifestForFork(
            upgradedJson,
            newManifestId,
            updatedRawName,
            "1.0.0",
            updatedCapsuleTitle);
        bool isUntrustedSource = preserveUntrustedOrigin ||
                                 string.Equals(sourceManifest?.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase);
        rewrittenJson = CustomWidgetManifestMetadataStamper.StampProvenance(
            rewrittenJson,
            origin: isUntrustedSource ? CustomWidgetOriginKinds.Community : CustomWidgetOriginKinds.Local,
            isCustomized: true,
            forkedFromId: sourceId,
            forkedFromVersion: sourceVersion);
        rewrittenJson = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(rewrittenJson);

        CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(rewrittenJson);
        CustomWidgetManifest? finalManifest = reparsed.Manifest;
        if (finalManifest is not null && clonedLocales is { HasAnyLocale: true })
        {
            finalManifest = finalManifest.WithLocales(clonedLocales);
        }

        string displayTitle = finalManifest?.ResolveLocalizedName(effectiveCulture) ?? updatedRawName;

        return new CustomWidgetForkBundleResult(
            NewManifestId: newManifestId,
            SourceManifestId: sourceId,
            SourceVersion: sourceVersion,
            DisplayTitle: displayTitle,
            ManifestJson: rewrittenJson,
            LogicJs: string.IsNullOrWhiteSpace(effectiveLogicJs) ? null : effectiveLogicJs,
            Locales: clonedLocales);
    }

    private static string BuildUniqueForkManifestId(string sourceId)
    {
        string working = sourceId.Trim().ToLowerInvariant();
        string[] prefixes = ["deskbox.", "starter.", "user.fork-", "fork-"];
        foreach (string prefix in prefixes)
        {
            if (working.StartsWith(prefix, StringComparison.Ordinal))
            {
                working = working[prefix.Length..];
                break;
            }
        }

        // Strip trailing 6-hex fork suffix when re-forking a fork so IDs stay tidy.
        if (working.Length > 7 && working[^7] == '-')
        {
            bool tailHex = true;
            for (int i = working.Length - 6; i < working.Length; i++)
            {
                char ch = working[i];
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                {
                    tailHex = false;
                    break;
                }
            }

            if (tailHex)
            {
                working = working[..^7];
            }
        }

        var slug = new StringBuilder(working.Length);
        bool dashPending = false;
        foreach (char ch in working)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                if (dashPending && slug.Length > 0)
                {
                    slug.Append('-');
                }
                dashPending = false;
                slug.Append(ch);
            }
            else
            {
                dashPending = true;
            }
        }

        string baseSlug = slug.Length == 0 ? "widget" : slug.ToString();
        if (baseSlug.Length > 28)
        {
            baseSlug = baseSlug[..28].TrimEnd('-');
        }
        if (baseSlug.Length == 0)
        {
            baseSlug = "widget";
        }

        Span<byte> rand = stackalloc byte[3];
        RandomNumberGenerator.Fill(rand);
        string suffix6 = Convert.ToHexStringLower(rand);
        return $"user.fork-{baseSlug}-{suffix6}";
    }

    private static CustomWidgetLocaleCatalog? CloneLocaleCatalog(CustomWidgetLocaleCatalog? source)
    {
        if (source is not { HasAnyLocale: true })
        {
            return null;
        }

        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string code, IReadOnlyDictionary<string, string> entries) in source.Locales)
        {
            copy[code] = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        }

        CustomWidgetLocaleMeta? metaCopy = source.Meta is null
            ? null
            : new CustomWidgetLocaleMeta(
                source.Meta.SchemaVersion,
                source.Meta.BaseLocale,
                new Dictionary<string, string>(source.Meta.Hashes, StringComparer.Ordinal));

        return new CustomWidgetLocaleCatalog(copy, metaCopy);
    }

    private static CustomWidgetLocaleCatalog AppendForkSuffixToLocaleKeys(
        CustomWidgetLocaleCatalog catalog,
        IReadOnlySet<string> i18nKeys,
        string activeCulture,
        string? explicitSuffix)
    {
        var updatedLocales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        bool anyMutated = false;

        foreach ((string code, IReadOnlyDictionary<string, string> entries) in catalog.Locales)
        {
            var dict = new Dictionary<string, string>(entries, StringComparer.Ordinal);
            string localeSuffix = string.Equals(code, activeCulture, StringComparison.OrdinalIgnoreCase) &&
                                  !string.IsNullOrWhiteSpace(explicitSuffix)
                ? explicitSuffix!
                : ResolveForkSuffixForCulture(code, null);

            foreach (string key in i18nKeys)
            {
                if (dict.TryGetValue(key, out string? curTitle) && !string.IsNullOrWhiteSpace(curTitle))
                {
                    dict[key] = AppendSuffixOnce(curTitle, localeSuffix);
                    anyMutated = true;
                }
            }

            updatedLocales[code] = dict;
        }

        if (!anyMutated)
        {
            return catalog;
        }

        string baseLocale = catalog.Meta?.BaseLocale ?? activeCulture;
        if (!updatedLocales.TryGetValue(baseLocale, out IReadOnlyDictionary<string, string>? baseEntries))
        {
            KeyValuePair<string, IReadOnlyDictionary<string, string>> first = updatedLocales.First();
            baseLocale = first.Key;
            baseEntries = first.Value;
        }

        CustomWidgetLocaleMeta updatedMeta = CustomWidgetLocaleMeta.BuildFromBaseDictionary(baseLocale, baseEntries);
        return new CustomWidgetLocaleCatalog(updatedLocales, updatedMeta);
    }

    private static string ResolveForkSuffixForCulture(string cultureCode, string? explicitSuffix)
    {
        if (!string.IsNullOrWhiteSpace(explicitSuffix))
        {
            return explicitSuffix!;
        }

        string resolved = LocalizationService.TranslateForCulture(cultureCode, "CustomWidget.Fork.TitleSuffix");
        if (!string.IsNullOrWhiteSpace(resolved) &&
            !string.Equals(resolved, "CustomWidget.Fork.TitleSuffix", StringComparison.Ordinal))
        {
            return resolved;
        }

        return cultureCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? " (二创)" : " (Fork)";
    }

    private static readonly string[] SupportedForkLocales =
    [
        "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR",
        "de-DE", "es-ES", "fr-FR", "pt-BR", "ru-RU",
        "ar-SA", "hi-IN", "bn-BD"
    ];

    private static string AppendSuffixOnce(string baseTitle, string suffix)
    {
        string trimmedBase = baseTitle.Trim();
        string normSuffix = suffix.StartsWith(' ') || suffix.StartsWith('（') ? suffix : " " + suffix;
        if (trimmedBase.EndsWith(normSuffix.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return trimmedBase;
        }

        string strippedBase = StripKnownTrailingForkSuffix(trimmedBase);
        return strippedBase + normSuffix;
    }

    private static string StripKnownTrailingForkSuffix(string title)
    {
        foreach (string code in SupportedForkLocales)
        {
            string candidate = ResolveForkSuffixForCulture(code, null).Trim();
            if (!string.IsNullOrEmpty(candidate) &&
                title.Length > candidate.Length &&
                title.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return title[..^candidate.Length].TrimEnd();
            }
        }

        foreach (string fallback in new[] { "(Fork)", "(二创)", "（二创）", "(二創)", "（二創）" })
        {
            if (title.Length > fallback.Length &&
                title.EndsWith(fallback, StringComparison.OrdinalIgnoreCase))
            {
                return title[..^fallback.Length].TrimEnd();
            }
        }

        return title;
    }

    private static string RewriteManifestForFork(
        string rawManifestJson,
        string newId,
        string newRawName,
        string newVersion,
        string? newCapsuleTitle = null)
    {
        using JsonDocument doc = JsonDocument.Parse(rawManifestJson, LenientJsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return rawManifestJson;
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonWriterOptions))
        {
            writer.WriteStartObject();
            bool wroteId = false;
            bool wroteName = false;
            bool wroteVersion = false;

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "id", StringComparison.Ordinal))
                {
                    writer.WriteString("id", newId);
                    wroteId = true;
                    continue;
                }

                if (string.Equals(prop.Name, "name", StringComparison.Ordinal))
                {
                    writer.WriteString("name", newRawName);
                    wroteName = true;
                    continue;
                }

                if (string.Equals(prop.Name, "version", StringComparison.Ordinal))
                {
                    writer.WriteString("version", newVersion);
                    wroteVersion = true;
                    continue;
                }

                if (newCapsuleTitle is not null &&
                    string.Equals(prop.Name, "capsule", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("capsule");
                    writer.WriteStartObject();
                    foreach (JsonProperty capProp in prop.Value.EnumerateObject())
                    {
                        if (string.Equals(capProp.Name, "title", StringComparison.Ordinal))
                        {
                            writer.WriteString("title", newCapsuleTitle);
                        }
                        else
                        {
                            capProp.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!wroteId)
            {
                writer.WriteString("id", newId);
            }

            if (!wroteName)
            {
                writer.WriteString("name", newRawName);
            }

            if (!wroteVersion)
            {
                writer.WriteString("version", newVersion);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static (bool IsPathLike, bool IsFilePathLike) ClassifyPathSetting(
        CustomWidgetSettingDefinition def,
        string currentVal)
    {
        if (!string.IsNullOrWhiteSpace(def.Type) &&
            !string.Equals(def.Type, "text", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(def.Type, "string", StringComparison.OrdinalIgnoreCase))
        {
            return (false, false);
        }

        string key = def.Key ?? string.Empty;
        string label = def.Label ?? string.Empty;

        // Avoid misclassifying URL/API endpoint settings as local filesystem paths.
        if (key.Contains("url", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
            currentVal.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            currentVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (false, false);
        }

        bool isFileKey =
            key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("exe", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("binary", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("文件", StringComparison.Ordinal) ||
            label.Contains("file", StringComparison.OrdinalIgnoreCase);

        bool isFolderKey =
            key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("dir", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("vault", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("路径", StringComparison.Ordinal) ||
            label.Contains("目录", StringComparison.Ordinal) ||
            label.Contains("文件夹", StringComparison.Ordinal) ||
            label.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("directory", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("path", StringComparison.OrdinalIgnoreCase);

        bool valueLooksLikePath =
            currentVal.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ||
            currentVal.StartsWith("~/", StringComparison.Ordinal) ||
            currentVal.StartsWith("~\\", StringComparison.Ordinal) ||
            (currentVal.Length >= 3 && char.IsLetter(currentVal[0]) && currentVal[1] == ':' &&
             (currentVal[2] == '\\' || currentVal[2] == '/'));

        if (!isFileKey && !isFolderKey && !valueLooksLikePath)
        {
            return (false, false);
        }

        bool hasFileExt = false;
        if (valueLooksLikePath)
        {
            string ext = Path.GetExtension(currentVal.Trim());
            hasFileExt = ext.Length is >= 2 and <= 8;
        }

        bool isFile = isFileKey || (hasFileExt && !key.Contains("dir", StringComparison.OrdinalIgnoreCase) && !key.Contains("folder", StringComparison.OrdinalIgnoreCase));
        return (true, isFile);
    }
}
