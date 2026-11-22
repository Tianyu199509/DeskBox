using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Platform;

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
/// 1) Inspecting imported widget bundles (<c>.dbxwidget</c> / <c>.deskbox-widget</c> / <c>.dbbox</c> / <c>deskbox://install-widget</c>)
///    to detect unconfigured DPAPI secrets and sanitized <c>%USERPROFILE%</c> / path settings for First-Run Setup;
/// 2) Producing clean, non-colliding One-Click Fork bundles with full <c>logic.js</c> + 13-locale preservation
///    and <c>forkedFromId</c> / <c>forkedFromVersion</c> lineage metadata.
/// </summary>
public static partial class CustomWidgetOnboardingHelper
{
    [GeneratedRegex(@"^\s*\$\{\s*\$i18n\.([A-Za-z0-9_.\-]+)(?:\s*\?\?[^}]*)?\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingI18nBindingRegex { get; }

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

    private static string ResolveDownloadsFolder(string userProfile) =>
        Win32Helper.TryGetDownloadsKnownFolderPath() ?? Path.Combine(userProfile, "Downloads");

    public static string ExpandUserProfilePlaceholder(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        string trimmed = rawValue.Trim().Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return trimmed;
        }

        if (trimmed.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(trimmed, "~", StringComparison.Ordinal))
        {
            return userProfile;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            string sub = trimmed[2..].TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(sub) ? userProfile : Path.Combine(userProfile, sub);
        }

        int sepIdx = trimmed.IndexOfAny(['/', '\\']);
        string head = sepIdx >= 0 ? trimmed[..sepIdx] : trimmed;
        string tail = sepIdx >= 0 ? trimmed[(sepIdx + 1)..] : string.Empty;
        if (!Path.IsPathRooted(trimmed) && !head.Contains(':') && !head.Contains('%'))
        {
            string? baseDir = head.Trim().ToLowerInvariant() switch
            {
                "downloads" or "download" or "下载" => ResolveDownloadsFolder(userProfile),
                "desktop" or "桌面" => ResolveKnownSpecialFolder(Environment.SpecialFolder.DesktopDirectory, userProfile, "Desktop"),
                "documents" or "my documents" or "文档" => ResolveKnownSpecialFolder(Environment.SpecialFolder.MyDocuments, userProfile, "Documents"),
                "pictures" or "图片" => ResolveKnownSpecialFolder(Environment.SpecialFolder.MyPictures, userProfile, "Pictures"),
                "music" or "音乐" => ResolveKnownSpecialFolder(Environment.SpecialFolder.MyMusic, userProfile, "Music"),
                "videos" or "视频" => ResolveKnownSpecialFolder(Environment.SpecialFolder.MyVideos, userProfile, "Videos"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                string cleanTail = tail.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                return string.IsNullOrWhiteSpace(cleanTail) ? baseDir : Path.Combine(baseDir, cleanTail);
            }
        }

        return trimmed;

        static string ResolveKnownSpecialFolder(Environment.SpecialFolder folder, string profileDir, string fallbackChild)
        {
            string dir = Environment.GetFolderPath(folder);
            return !string.IsNullOrWhiteSpace(dir) ? dir : Path.Combine(profileDir, fallbackChild);
        }
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

        string? effectiveLogicJs = string.IsNullOrWhiteSpace(logicJs) ? null : logicJs;
        CustomWidgetLocaleCatalog? effectiveLocales = locales;
        string upgradedJson = !string.IsNullOrWhiteSpace(effectiveLogicJs)
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(rawManifestJson, effectiveLogicJs)
            : rawManifestJson;

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
        string[] prefixes = ["deskbox.", "user.fork-", "fork-"];
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

        if (!cultureCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return " (Fork)";
        }

        return LocalizationService.IsTraditionalChineseCulture(cultureCode) ? " (二創)" : " (二创)";
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

    public static IReadOnlyList<string> ParseAcceptFileTypeFilters(string? rawAccept)
    {
        if (string.IsNullOrWhiteSpace(rawAccept))
        {
            return [];
        }

        string[] parts = rawAccept.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filters = new List<string>(parts.Length);
        foreach (string p in parts)
        {
            if (p is "*" or "*.*")
            {
                return [];
            }

            string token = p;
            if (token.StartsWith("*.", StringComparison.Ordinal))
            {
                token = token[1..];
            }
            else if (!token.StartsWith('.'))
            {
                if (token.Contains('/'))
                {
                    // Expand common MIME wildcards gracefully
                    switch (token.ToLowerInvariant())
                    {
                        case "video/*":
                            filters.AddRange([".mp4", ".mkv", ".mov", ".webm", ".avi"]);
                            continue;
                        case "audio/*":
                            filters.AddRange([".mp3", ".wav", ".flac", ".m4a", ".ogg"]);
                            continue;
                        case "image/*":
                            filters.AddRange([".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"]);
                            continue;
                        default:
                            continue;
                    }
                }

                token = "." + token;
            }

            if (token.Length is >= 2 and <= 16 && !filters.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                filters.Add(token.ToLowerInvariant());
            }
        }

        return filters;
    }

    public static async Task<string?> PickPathForSettingAsync(
        IntPtr ownerHwnd,
        CustomWidgetSettingDefinition def,
        bool pickFile,
        string? currentPath = null)
    {
        if (ownerHwnd == IntPtr.Zero || !DeskBox.Platform.Win32Helper.IsWindow(ownerHwnd))
        {
            return null;
        }

        if (!pickFile)
        {
            return await FolderPickerService.PickFolderAsync(ownerHwnd, "widget-setting");
        }

        IReadOnlyList<string> filters = ParseAcceptFileTypeFilters(def.Accept);
        if (filters.Count > 0)
        {
            return await FileOpenPickerService.PickSingleFileAsync(
                ownerHwnd,
                filters,
                Microsoft.Windows.Storage.Pickers.PickerLocationId.Desktop);
        }

        string? suggestedFolder = null;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                string? dir = Directory.Exists(currentPath) ? currentPath : Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    suggestedFolder = dir;
                }
            }
            catch
            {
            }
        }

        IReadOnlyList<string> picked = await FileOpenPickerService.PickFilesAsync(ownerHwnd, suggestedFolder);
        return picked.Count > 0 && !string.IsNullOrWhiteSpace(picked[0]) ? picked[0] : null;
    }

    public static async Task<IReadOnlyList<string>> PickMultipleFilesForSettingAsync(
        IntPtr ownerHwnd,
        CustomWidgetSettingDefinition def)
    {
        if (ownerHwnd == IntPtr.Zero || !DeskBox.Platform.Win32Helper.IsWindow(ownerHwnd))
        {
            return [];
        }

        IReadOnlyList<string> picked = await FileOpenPickerService.PickFilesAsync(ownerHwnd, null);
        if (picked.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> filters = ParseAcceptFileTypeFilters(def.Accept);
        if (filters.Count == 0)
        {
            return picked;
        }

        var filtered = new List<string>(picked.Count);
        foreach (string path in picked)
        {
            string ext = Path.GetExtension(path);
            if (!string.IsNullOrWhiteSpace(ext) &&
                filters.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                filtered.Add(path);
            }
        }

        return filtered.Count > 0 ? filtered : picked;
    }

    public static bool IsMultilineTextSetting(
        CustomWidgetSettingDefinition def,
        string? currentVal = null)
    {
        if (string.Equals(def.Type, "multiline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "textarea", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "lines", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "list", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(def.Type) &&
            !string.Equals(def.Type, "text", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(def.Type, "string", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((def.DefaultValue?.IndexOf('\n') >= 0) ||
            (def.Placeholder?.IndexOf('\n') >= 0) ||
            (!string.IsNullOrEmpty(currentVal) && currentVal.IndexOf('\n') >= 0))
        {
            return true;
        }

        string label = def.Label ?? string.Empty;
        string placeholder = def.Placeholder ?? string.Empty;
        if (label.Contains("每行", StringComparison.Ordinal) ||
            label.Contains("多行", StringComparison.Ordinal) ||
            label.Contains("列表", StringComparison.Ordinal) ||
            placeholder.Contains("每行", StringComparison.Ordinal) ||
            placeholder.Contains("一行", StringComparison.Ordinal) ||
            placeholder.Contains("per line", StringComparison.OrdinalIgnoreCase) ||
            placeholder.Contains("one per line", StringComparison.OrdinalIgnoreCase) ||
            placeholder.Contains("multi-line", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string key = def.Key ?? string.Empty;
        return string.Equals(key, "slides", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "playlist", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "items", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "lines", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("List", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("Lines", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMultilineMediaOrPathListSetting(
        CustomWidgetSettingDefinition def,
        string? currentVal = null)
    {
        if (!IsMultilineTextSetting(def, currentVal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(def.Accept))
        {
            return true;
        }

        string key = def.Key ?? string.Empty;
        string label = def.Label ?? string.Empty;
        string placeholder = def.Placeholder ?? string.Empty;

        return key.Contains("slide", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("media", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("photo", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("image", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("video", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("playlist", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("file", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("素材", StringComparison.Ordinal) ||
               label.Contains("媒体", StringComparison.Ordinal) ||
               label.Contains("图片", StringComparison.Ordinal) ||
               label.Contains("照片", StringComparison.Ordinal) ||
               label.Contains("视频", StringComparison.Ordinal) ||
               label.Contains("路径", StringComparison.Ordinal) ||
               label.Contains("文件", StringComparison.Ordinal) ||
               placeholder.Contains("路径", StringComparison.Ordinal) ||
               placeholder.Contains("文件", StringComparison.Ordinal) ||
               placeholder.Contains("path", StringComparison.OrdinalIgnoreCase) ||
               placeholder.Contains("file", StringComparison.OrdinalIgnoreCase);
    }

    public static string AppendLinesToMultilineSetting(
        string? existingText,
        IEnumerable<string> newLines)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(existingText))
        {
            foreach (string raw in existingText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length > 0)
                {
                    lines.Add(trimmed);
                }
            }
        }

        foreach (string added in newLines)
        {
            string trimmed = added?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && !lines.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(trimmed);
            }
        }

        return string.Join("\n", lines);
    }

    public static (bool IsPathLike, bool IsFilePathLike) ClassifyPathSetting(
        CustomWidgetSettingDefinition def,
        string currentVal)
    {
        if (IsMultilineTextSetting(def, currentVal))
        {
            return (false, false);
        }

        if (string.Equals(def.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        if (string.Equals(def.Type, "folder", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "directory", StringComparison.OrdinalIgnoreCase))
        {
            return (true, false);
        }

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
