using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class LocalizationService
{
    public const string LanguageSystem = SettingsService.LanguageSystem;
    public const string LanguageChinese = SettingsService.LanguageChinese;
    public const string LanguageChineseTraditional = SettingsService.LanguageChineseTraditional;
    public const string LanguageEnglish = SettingsService.LanguageEnglish;
    public const string LanguageJapanese = "ja-JP";
    public const string LanguageGerman = "de-DE";
    public const string LanguagePortuguese = "pt-BR";
    public const string LanguageHindi = SettingsService.LanguageHindi;
    public const string LanguageSpanish = SettingsService.LanguageSpanish;
    public const string LanguageFrench = SettingsService.LanguageFrench;
    public const string LanguageArabic = SettingsService.LanguageArabic;
    public const string LanguageBengali = SettingsService.LanguageBengali;
    public const string LanguageRussian = SettingsService.LanguageRussian;
    public const string LanguageKorean = SettingsService.LanguageKorean;

    private readonly SettingsService _settingsService;

    public event Action? LanguageChanged;

    public LocalizationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string LanguageSetting => NormalizeLanguageSetting(_settingsService.Settings.Language);

    // Deep review R15 (F8 audit): T() runs on the UI thread ~1000x across
    // tray menus, notifications, and settings construction, and the default
    // Language=System setting made EVERY call hit the registry (twice for
    // locales falling through IsEnglish). Cache the effective culture; it
    // only changes at SetLanguage/NotifyExternalLanguageChange, both of
    // which bust the cache.
    private string? _cachedCultureName;

    public string CurrentCultureName
    {
        get
        {
            if (_cachedCultureName is { } cached)
            {
                return cached;
            }

            string language = LanguageSetting;
            if (language == LanguageSystem)
            {
                language = ResolveDefaultLanguage();
            }

            _cachedCultureName = language;
            return language;
        }
    }

    public bool IsEnglish => string.Equals(CurrentCultureName, LanguageEnglish, StringComparison.OrdinalIgnoreCase);
    public bool IsChinese => CurrentCultureName is LanguageChinese or LanguageChineseTraditional;
    public bool IsTraditionalChinese => CurrentCultureName == LanguageChineseTraditional;

    /// <summary>
    /// Returns a 2-letter language code ("en", "zh", "ja", "de", "pt", "hi", "es", "fr", "ar", "bn", "ru", "ko")
    /// suitable for passing to weather and location APIs.
    /// </summary>
    public string ApiLanguageCode => CurrentCultureName switch
    {
        LanguageChinese or LanguageChineseTraditional => "zh",
        LanguageJapanese => "ja",
        LanguageGerman => "de",
        LanguagePortuguese => "pt",
        LanguageHindi => "hi",
        LanguageSpanish => "es",
        LanguageFrench => "fr",
        LanguageArabic => "ar",
        LanguageBengali => "bn",
        LanguageRussian => "ru",
        LanguageKorean => "ko",
        _ => "en"
    };

    public static IReadOnlyList<string> SupportedLanguageCodes { get; } =
    [
        LanguageChinese,
        LanguageChineseTraditional,
        LanguageEnglish,
        LanguageJapanese,
        LanguageGerman,
        LanguagePortuguese,
        LanguageHindi,
        LanguageSpanish,
        LanguageFrench,
        LanguageArabic,
        LanguageBengali,
        LanguageRussian,
        LanguageKorean
    ];

    public IReadOnlyList<string> AvailableLanguageSettings { get; } =
    [
        LanguageSystem,
        LanguageChinese,
        LanguageChineseTraditional,
        LanguageEnglish,
        LanguageJapanese,
        LanguageGerman,
        LanguagePortuguese,
        LanguageHindi,
        LanguageSpanish,
        LanguageFrench,
        LanguageArabic,
        LanguageBengali,
        LanguageRussian,
        LanguageKorean
    ];

    public string GetLanguageDisplayName(string language)
    {
        return NormalizeLanguageSetting(language) switch
        {
            LanguageChinese => "简体中文",
            LanguageChineseTraditional => "繁體中文",
            LanguageEnglish => T("Language.English"),
            LanguageJapanese => T("Language.Japanese"),
            LanguageGerman => T("Language.German"),
            LanguagePortuguese => T("Language.Portuguese"),
            LanguageHindi => "हिन्दी",
            LanguageSpanish => "Español",
            LanguageFrench => "Français",
            LanguageArabic => "العربية",
            LanguageBengali => "বাংলা",
            LanguageRussian => "Русский",
            LanguageKorean => "한국어",
            _ => T("Language.System")
        };
    }

    public void SetLanguage(string language)
    {
        string normalizedLanguage = NormalizeLanguageSetting(language);
        if (string.Equals(_settingsService.Settings.Language, normalizedLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.Language = normalizedLanguage;
        try
        {
            _settingsService.SaveDebounced();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] SaveDebounced failed during language switch: {ex.Message}");
        }
        // RaiseLanguageChanged must always be called, even if SaveDebounced
        // failed, so that the UI updates to the new language immediately.
        _cachedCultureName = null;
        RaiseLanguageChanged();
        TrimInactiveLocaleDictionaries();
    }

    /// <summary>
    /// Re-raises <see cref="LanguageChanged"/> after an external snapshot
    /// (the settings host's commit) changed the persisted language: new
    /// string reads pick the language up from Settings directly, but every
    /// already-rendered surface only refreshes through this event.
    /// </summary>
    internal void NotifyExternalLanguageChange()
    {
        _cachedCultureName = null;
        RaiseLanguageChanged();
        TrimInactiveLocaleDictionaries();
    }

    /// <summary>
    /// Invokes all LanguageChanged handlers, catching exceptions per-handler
    /// so that one failing subscriber does not prevent subsequent subscribers
    /// from receiving the notification.
    /// </summary>
    private void RaiseLanguageChanged()
    {
        if (LanguageChanged is null)
        {
            return;
        }

        foreach (var handler in LanguageChanged.GetInvocationList())
        {
            try
            {
                ((Action)handler).Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalizationService] LanguageChanged handler '{handler.Method.DeclaringType?.Name}.{handler.Method.Name}' threw: {ex.Message}");
            }
        }
    }

    public string T(string key) => TranslateForCulture(CurrentCultureName, key);

    public static string ResolveAmbientCultureName() => ResolveDefaultLanguage();

    public static string ResolvePersistedOrAmbientCultureName()
    {
        try
        {
            string settingsPath = Path.Combine(DeskBoxDataPathService.Current.RootPath, "settings.json");
            if (File.Exists(settingsPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (root.TryGetProperty("language", out JsonElement langEl) ||
                     root.TryGetProperty("Language", out langEl)) &&
                    langEl.ValueKind == JsonValueKind.String)
                {
                    string normalized = NormalizeLanguageSetting(langEl.GetString());
                    if (normalized != LanguageSystem)
                    {
                        return normalized;
                    }
                }
            }
        }
        catch
        {
        }

        return ResolveDefaultLanguage();
    }

    public static string TranslateForCulture(string? cultureName, string key)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? ResolveDefaultLanguage()
            : NormalizeCultureCode(cultureName);
        var table = culture switch
        {
            LanguageChineseTraditional => ZhTw,
            LanguageJapanese => JaJp,
            LanguageGerman => DeDe,
            LanguagePortuguese => PtBr,
            LanguageHindi => HiIn,
            LanguageSpanish => EsEs,
            LanguageFrench => FrFr,
            LanguageArabic => ArSa,
            LanguageBengali => BnBd,
            LanguageRussian => RuRu,
            LanguageKorean => KoKr,
            _ => string.Equals(culture, LanguageEnglish, StringComparison.OrdinalIgnoreCase) ? EnUs : ZhCn
        };

        if (table.TryGetValue(key, out string? value))
        {
            return value;
        }

        // All shipped locale files are expected to be complete. Keep English
        // and Simplified Chinese as defensive fallbacks for unknown keys without
        // materializing full fallback locale dictionaries in memory.
        if (!string.Equals(culture, LanguageEnglish, StringComparison.OrdinalIgnoreCase) &&
            TryResolveFallbackKey(LanguageEnglish, _enUs, key, out value))
        {
            return value!;
        }

        if (!string.Equals(culture, LanguageChinese, StringComparison.OrdinalIgnoreCase) &&
            TryResolveFallbackKey(LanguageChinese, _zhCn, key, out value))
        {
            return value!;
        }

        return key;
    }

    public static string FormatForCulture(string? cultureName, string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, TranslateForCulture(cultureName, key), args);

    public static string NormalizeCultureCode(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return LanguageEnglish;
        }

        string trimmed = cultureName.Trim().Replace('_', '-');
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return IsTraditionalChineseCulture(trimmed) ? LanguageChineseTraditional : LanguageChinese;
        }
        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return LanguageJapanese;
        if (trimmed.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return LanguageGerman;
        if (trimmed.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return LanguagePortuguese;
        if (trimmed.StartsWith("hi", StringComparison.OrdinalIgnoreCase)) return LanguageHindi;
        if (trimmed.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return LanguageSpanish;
        if (trimmed.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return LanguageFrench;
        if (trimmed.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) return LanguageArabic;
        if (trimmed.StartsWith("bn", StringComparison.OrdinalIgnoreCase)) return LanguageBengali;
        if (trimmed.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return LanguageRussian;
        if (trimmed.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return LanguageKorean;
        return LanguageEnglish;
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    public static string DefaultText(string key)
    {
        if (_zhCn is { } loadedZhCn)
        {
            return loadedZhCn.TryGetValue(key, out string? loadedValue) ? loadedValue : key;
        }

        return TryResolveFallbackKey(LanguageChinese, null, key, out string? fallbackValue)
            ? fallbackValue!
            : key;
    }

    public static string DefaultFormat(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, DefaultText(key), args);
    }

    public static string NormalizeLanguageSetting(string? language)
    {
        return language is LanguageChinese or LanguageChineseTraditional or LanguageEnglish or LanguageJapanese or LanguageGerman or LanguagePortuguese
            or LanguageHindi or LanguageSpanish or LanguageFrench or LanguageArabic or LanguageBengali or LanguageRussian or LanguageKorean
            ? language
            : LanguageSystem;
    }

    private static string ResolveSystemLanguage()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return IsTraditionalChineseCulture(name)
                ? LanguageChineseTraditional
                : LanguageChinese;
        }
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return LanguageJapanese;
        if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            return LanguageGerman;
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            return LanguagePortuguese;
        if (name.StartsWith("hi", StringComparison.OrdinalIgnoreCase))
            return LanguageHindi;
        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            return LanguageSpanish;
        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            return LanguageFrench;
        if (name.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
            return LanguageArabic;
        if (name.StartsWith("bn", StringComparison.OrdinalIgnoreCase))
            return LanguageBengali;
        if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            return LanguageRussian;
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return LanguageKorean;
        return LanguageEnglish;
    }

    internal static bool IsTraditionalChineseCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        string normalized = cultureName.Replace('_', '-');
        return normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the default language used when the user has not explicitly
    /// picked one in the app. Honors the language chosen at install time
    /// (per-user HKCU\Software\DeskBox\InstallLanguage first, then the direct
    /// installer's HKA\Software\DeskBox\DirectInstall\InstallLanguage across
    /// HKCU and HKLM), falling back to the OS UI culture when absent or
    /// unrecognized.
    /// </summary>
    private static string ResolveDefaultLanguage()
    {
#if !DESKBOX_STORE
        string? installLanguage = TryResolveSupportedInstallLanguage(
            static (keyName, valueName) =>
                Registry.GetValue(keyName, valueName, null) as string);
        if (installLanguage is not null)
        {
            return installLanguage;
        }
#endif

        return ResolveSystemLanguage();
    }

    internal static string? TryResolveSupportedInstallLanguage(
        Func<string, string, string?> registryReader)
    {
        ReadOnlySpan<string> candidateKeys =
        [
            @"HKEY_CURRENT_USER\Software\DeskBox",
            @"HKEY_CURRENT_USER\Software\DeskBox\DirectInstall",
            @"HKEY_LOCAL_MACHINE\Software\DeskBox\DirectInstall"
        ];

        foreach (string keyPath in candidateKeys)
        {
            try
            {
                string? value = registryReader(keyPath, "InstallLanguage")?.Trim();
                if (IsSupportedExplicitLanguage(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalizationService] Failed reading InstallLanguage from {keyPath}: {ex.Message}");
            }
        }

        return null;
    }

    private static bool IsSupportedExplicitLanguage(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value == LanguageChinese
            || value == LanguageChineseTraditional
            || value == LanguageEnglish
            || value == LanguageJapanese
            || value == LanguageGerman
            || value == LanguagePortuguese
            || value == LanguageHindi
            || value == LanguageSpanish
            || value == LanguageFrench
            || value == LanguageArabic
            || value == LanguageBengali
            || value == LanguageRussian
            || value == LanguageKorean);

    private static Dictionary<string, string>? _zhCn;
    private static Dictionary<string, string>? _zhTw;
    private static Dictionary<string, string>? _enUs;
    private static Dictionary<string, string>? _jaJp;
    private static Dictionary<string, string>? _deDe;
    private static Dictionary<string, string>? _ptBr;
    private static Dictionary<string, string>? _hiIn;
    private static Dictionary<string, string>? _esEs;
    private static Dictionary<string, string>? _frFr;
    private static Dictionary<string, string>? _arSa;
    private static Dictionary<string, string>? _bnBd;
    private static Dictionary<string, string>? _ruRu;
    private static Dictionary<string, string>? _koKr;
    private static readonly object s_loadLock = new();
    private static volatile bool s_residentHostKeyFilterEnabled;

    private static readonly string[] s_residentHostAllowedSettingsSubtrees =
    [
        "AutoStart",
        "Capsule",
        "Clipboard",
        "CloudBackup",
        "ContentEditor",
        "DataBackup",
        "Dialog",
        "DragDropPermission",
        "DropAction",
        "FileStacks",
        "FileWidget",
        "GlobalHotkey",
        "Hotkey",
        "HotkeyRecorder",
        "HoverButtonActions",
        "IconSize",
        "ManagedPath",
        "Music.DisplayMode",
        "Music.SpectrumStyle",
        "Persistence",
        "Recovery",
        "ReleaseNotes",
        "Search",
        "Update",
        "WidgetForeground",
        "WidgetGroups",
        "Windows10VisualCompatibility",
        "WindowTitle",
    ];

    private static readonly byte[][] s_residentHostAllowedSettingsSubtreesUtf8 =
        Array.ConvertAll(s_residentHostAllowedSettingsSubtrees, static s => Encoding.UTF8.GetBytes(s));

    private static readonly JsonReaderOptions s_localeJsonReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const int MaxMissingKeyFallbackCacheEntries = 128;
    private static Dictionary<(string Locale, string Key), string?>? s_missingKeyFallbackCache;

    internal static void EnableResidentHostKeyFilter()
    {
        lock (s_loadLock)
        {
            if (s_residentHostKeyFilterEnabled)
            {
                return;
            }

            s_residentHostKeyFilterEnabled = true;
            s_missingKeyFallbackCache = null;
            FilterDictionaryInPlace(_zhCn);
            FilterDictionaryInPlace(_zhTw);
            FilterDictionaryInPlace(_enUs);
            FilterDictionaryInPlace(_jaJp);
            FilterDictionaryInPlace(_deDe);
            FilterDictionaryInPlace(_ptBr);
            FilterDictionaryInPlace(_hiIn);
            FilterDictionaryInPlace(_esEs);
            FilterDictionaryInPlace(_frFr);
            FilterDictionaryInPlace(_arSa);
            FilterDictionaryInPlace(_bnBd);
            FilterDictionaryInPlace(_ruRu);
            FilterDictionaryInPlace(_koKr);
        }
    }

    internal static bool IsPureSettingsUiKey(string key)
    {
        if (!key.StartsWith("Settings.", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> rest = key.AsSpan("Settings.".Length);
        foreach (string subtree in s_residentHostAllowedSettingsSubtrees)
        {
            if (rest.StartsWith(subtree, StringComparison.Ordinal) &&
                (rest.Length == subtree.Length || rest[subtree.Length] == '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPureSettingsUiKeyUtf8(ReadOnlySpan<byte> utf8Key)
    {
        ReadOnlySpan<byte> settingsPrefix = "Settings."u8;
        if (!utf8Key.StartsWith(settingsPrefix))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = utf8Key[settingsPrefix.Length..];
        foreach (byte[] subtree in s_residentHostAllowedSettingsSubtreesUtf8)
        {
            if (rest.StartsWith(subtree) &&
                (rest.Length == subtree.Length || rest[subtree.Length] == (byte)'.'))
            {
                return false;
            }
        }

        return true;
    }

    internal static void FilterDictionaryInPlace(Dictionary<string, string>? dictionary)
    {
        if (dictionary is null || dictionary.Count == 0)
        {
            return;
        }

        List<string>? toRemove = null;
        foreach (string key in dictionary.Keys)
        {
            if (IsPureSettingsUiKey(key))
            {
                (toRemove ??= []).Add(key);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (string key in toRemove)
        {
            dictionary.Remove(key);
        }

        dictionary.TrimExcess();
    }

    private static Dictionary<string, string> ZhCn
    {
        get
        {
            if (_zhCn is not null) return _zhCn;
            lock (s_loadLock)
            {
                _zhCn ??= LoadLocaleStrings("zh-CN");
            }
            return _zhCn;
        }
    }

    private static Dictionary<string, string> ZhTw
    {
        get
        {
            if (_zhTw is not null) return _zhTw;
            lock (s_loadLock)
            {
                _zhTw ??= LoadLocaleStrings("zh-TW");
            }
            return _zhTw;
        }
    }

    private static Dictionary<string, string> EnUs
    {
        get
        {
            if (_enUs is not null) return _enUs;
            lock (s_loadLock)
            {
                _enUs ??= LoadLocaleStrings("en-US");
            }
            return _enUs;
        }
    }

    private static Dictionary<string, string> JaJp
    {
        get
        {
            if (_jaJp is not null) return _jaJp;
            lock (s_loadLock)
            {
                _jaJp ??= LoadLocaleStrings("ja-JP");
            }
            return _jaJp;
        }
    }

    private static Dictionary<string, string> DeDe
    {
        get
        {
            if (_deDe is not null) return _deDe;
            lock (s_loadLock)
            {
                _deDe ??= LoadLocaleStrings("de-DE");
            }
            return _deDe;
        }
    }

    private static Dictionary<string, string> PtBr
    {
        get
        {
            if (_ptBr is not null) return _ptBr;
            lock (s_loadLock)
            {
                _ptBr ??= LoadLocaleStrings("pt-BR");
            }
            return _ptBr;
        }
    }

    private static Dictionary<string, string> HiIn
    {
        get
        {
            if (_hiIn is not null) return _hiIn;
            lock (s_loadLock)
            {
                _hiIn ??= LoadLocaleStrings("hi-IN");
            }
            return _hiIn;
        }
    }

    private static Dictionary<string, string> EsEs
    {
        get
        {
            if (_esEs is not null) return _esEs;
            lock (s_loadLock)
            {
                _esEs ??= LoadLocaleStrings("es-ES");
            }
            return _esEs;
        }
    }

    private static Dictionary<string, string> FrFr
    {
        get
        {
            if (_frFr is not null) return _frFr;
            lock (s_loadLock)
            {
                _frFr ??= LoadLocaleStrings("fr-FR");
            }
            return _frFr;
        }
    }

    private static Dictionary<string, string> ArSa
    {
        get
        {
            if (_arSa is not null) return _arSa;
            lock (s_loadLock)
            {
                _arSa ??= LoadLocaleStrings("ar-SA");
            }
            return _arSa;
        }
    }

    private static Dictionary<string, string> BnBd
    {
        get
        {
            if (_bnBd is not null) return _bnBd;
            lock (s_loadLock)
            {
                _bnBd ??= LoadLocaleStrings("bn-BD");
            }
            return _bnBd;
        }
    }

    private static Dictionary<string, string> RuRu
    {
        get
        {
            if (_ruRu is not null) return _ruRu;
            lock (s_loadLock)
            {
                _ruRu ??= LoadLocaleStrings("ru-RU");
            }
            return _ruRu;
        }
    }

    private static Dictionary<string, string> KoKr
    {
        get
        {
            if (_koKr is not null) return _koKr;
            lock (s_loadLock)
            {
                _koKr ??= LoadLocaleStrings("ko-KR");
            }
            return _koKr;
        }
    }

    public void TrimInactiveLocaleDictionaries()
    {
        string active = CurrentCultureName;
        lock (s_loadLock)
        {
            if (active != LanguageChinese) _zhCn = null;
            if (active != LanguageEnglish) _enUs = null;
            if (active != LanguageChineseTraditional) _zhTw = null;
            if (active != LanguageJapanese) _jaJp = null;
            if (active != LanguageGerman) _deDe = null;
            if (active != LanguagePortuguese) _ptBr = null;
            if (active != LanguageHindi) _hiIn = null;
            if (active != LanguageSpanish) _esEs = null;
            if (active != LanguageFrench) _frFr = null;
            if (active != LanguageArabic) _arSa = null;
            if (active != LanguageBengali) _bnBd = null;
            if (active != LanguageRussian) _ruRu = null;
            if (active != LanguageKorean) _koKr = null;
            s_missingKeyFallbackCache = null;
        }
    }

    private static Stream? OpenLocaleStream(string locale)
    {
        string fileName = $"{locale}.json";
        string primaryPath = Path.Combine(AppContext.BaseDirectory, "Strings", fileName);
        if (File.Exists(primaryPath))
        {
            return new FileStream(primaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        string? directory = AppContext.BaseDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            string candidate = Path.Combine(directory, "Strings", fileName);
            if (File.Exists(candidate))
            {
                return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            string repoCandidate = Path.Combine(directory, "src", "DeskBox", "Strings", fileName);
            if (File.Exists(repoCandidate))
            {
                return new FileStream(repoCandidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        }

        return Assembly.GetExecutingAssembly().GetManifestResourceStream($"DeskBox.Strings.{locale}.json");
    }

    private static int ReadIntoBuffer(Stream stream, Span<byte> destination)
    {
        int totalRead = 0;
        while (!destination.IsEmpty)
        {
            int read = stream.Read(destination);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
            destination = destination[read..];
        }

        return totalRead;
    }

    internal Dictionary<string, string> LoadUnfilteredLocaleStringsForCurrentCulture()
    {
        string culture = NormalizeCultureCode(CurrentCultureName);
        Dictionary<string, string> primary = LoadLocaleStringsCore(culture, filterSettingsKeys: false);
        if (!string.Equals(culture, LanguageEnglish, StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, string> englishFallback = LoadLocaleStringsCore(LanguageEnglish, filterSettingsKeys: false);
            foreach (KeyValuePair<string, string> pair in englishFallback)
            {
                primary.TryAdd(pair.Key, pair.Value);
            }
        }

        return primary;
    }

    private static Dictionary<string, string> LoadLocaleStrings(string locale) =>
        LoadLocaleStringsCore(locale, s_residentHostKeyFilterEnabled);

    private static Dictionary<string, string> LoadLocaleStringsCore(string locale, bool filterSettingsKeys)
    {
        using Stream? stream = OpenLocaleStream(locale);
        if (stream is null)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Locale resource not found: {locale}");
            return [];
        }

        var loaded = new Dictionary<string, string>(filterSettingsKeys ? 640 : 2300, StringComparer.Ordinal);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int bytesInBuffer = 0;
            bool firstChunk = true;
            bool isFinalBlock = false;
            JsonReaderState state = new(s_localeJsonReaderOptions);
            int containerDepth = 0;
            string? pendingKey = null;
            bool skipPendingValue = false;

            while (!isFinalBlock)
            {
                int bytesRead = ReadIntoBuffer(stream, buffer.AsSpan(bytesInBuffer));
                bytesInBuffer += bytesRead;
                isFinalBlock = bytesRead == 0;

                ReadOnlySpan<byte> span = buffer.AsSpan(0, bytesInBuffer);
                int bomOffset = 0;
                if (firstChunk)
                {
                    firstChunk = false;
                    if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                    {
                        bomOffset = 3;
                        span = span[3..];
                    }
                }

                var reader = new Utf8JsonReader(span, isFinalBlock, state);
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            containerDepth++;
                            break;

                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            containerDepth--;
                            if (containerDepth <= 1)
                            {
                                pendingKey = null;
                                skipPendingValue = false;
                            }
                            break;

                        case JsonTokenType.PropertyName when containerDepth == 1:
                            if (filterSettingsKeys)
                            {
                                if (!reader.ValueIsEscaped)
                                {
                                    if (IsPureSettingsUiKeyUtf8(reader.ValueSpan))
                                    {
                                        pendingKey = null;
                                        skipPendingValue = true;
                                        break;
                                    }
                                }
                                else
                                {
                                    string escapedKey = reader.GetString() ?? string.Empty;
                                    if (IsPureSettingsUiKey(escapedKey))
                                    {
                                        pendingKey = null;
                                        skipPendingValue = true;
                                        break;
                                    }

                                    pendingKey = escapedKey;
                                    skipPendingValue = false;
                                    break;
                                }
                            }

                            pendingKey = reader.GetString() ?? string.Empty;
                            skipPendingValue = false;
                            break;

                        case JsonTokenType.String when containerDepth == 1:
                            if (!skipPendingValue && pendingKey is not null)
                            {
                                loaded[pendingKey] = reader.GetString() ?? string.Empty;
                            }

                            pendingKey = null;
                            skipPendingValue = false;
                            break;

                        case JsonTokenType.Null or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Number when containerDepth == 1:
                            pendingKey = null;
                            skipPendingValue = false;
                            break;
                    }
                }

                state = reader.CurrentState;
                int consumed = bomOffset + (int)reader.BytesConsumed;
                int leftover = bytesInBuffer - consumed;
                if (leftover > 0)
                {
                    if (leftover == buffer.Length)
                    {
                        byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, leftover).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                    else if (consumed > 0)
                    {
                        buffer.AsSpan(consumed, leftover).CopyTo(buffer);
                    }
                }

                bytesInBuffer = leftover;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Failed to parse locale '{locale}': {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        loaded.TrimExcess();
        return loaded;
    }

    private static bool TryResolveFallbackKey(
        string locale,
        Dictionary<string, string>? alreadyLoadedLocale,
        string key,
        out string? value)
    {
        if (alreadyLoadedLocale is not null)
        {
            return alreadyLoadedLocale.TryGetValue(key, out value);
        }

        if (s_residentHostKeyFilterEnabled && IsPureSettingsUiKey(key))
        {
            value = null;
            return false;
        }

        lock (s_loadLock)
        {
            if (s_missingKeyFallbackCache is not null &&
                s_missingKeyFallbackCache.TryGetValue((locale, key), out string? cached))
            {
                value = cached;
                return cached is not null;
            }
        }

        string? lookedUp = TryScanSingleKeyFromLocaleStream(locale, key);

        lock (s_loadLock)
        {
            s_missingKeyFallbackCache ??= new Dictionary<(string Locale, string Key), string?>(16);
            if (s_missingKeyFallbackCache.Count >= MaxMissingKeyFallbackCacheEntries)
            {
                s_missingKeyFallbackCache.Clear();
            }

            s_missingKeyFallbackCache[(locale, key)] = lookedUp;
        }

        value = lookedUp;
        return lookedUp is not null;
    }

    private static string? TryScanSingleKeyFromLocaleStream(string locale, string targetKey)
    {
        using Stream? stream = OpenLocaleStream(locale);
        if (stream is null)
        {
            return null;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int bytesInBuffer = 0;
            bool firstChunk = true;
            bool isFinalBlock = false;
            JsonReaderState state = new(s_localeJsonReaderOptions);
            int containerDepth = 0;
            bool matchedProperty = false;

            while (!isFinalBlock)
            {
                int bytesRead = ReadIntoBuffer(stream, buffer.AsSpan(bytesInBuffer));
                bytesInBuffer += bytesRead;
                isFinalBlock = bytesRead == 0;

                ReadOnlySpan<byte> span = buffer.AsSpan(0, bytesInBuffer);
                int bomOffset = 0;
                if (firstChunk)
                {
                    firstChunk = false;
                    if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                    {
                        bomOffset = 3;
                        span = span[3..];
                    }
                }

                var reader = new Utf8JsonReader(span, isFinalBlock, state);
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            containerDepth++;
                            break;

                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            containerDepth--;
                            if (containerDepth <= 1)
                            {
                                matchedProperty = false;
                            }
                            break;

                        case JsonTokenType.PropertyName when containerDepth == 1:
                            matchedProperty = reader.ValueTextEquals(targetKey.AsSpan());
                            break;

                        case JsonTokenType.String when containerDepth == 1:
                            if (matchedProperty)
                            {
                                return reader.GetString();
                            }
                            break;

                        case JsonTokenType.Null or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Number when containerDepth == 1:
                            matchedProperty = false;
                            break;
                    }
                }

                state = reader.CurrentState;
                int consumed = bomOffset + (int)reader.BytesConsumed;
                int leftover = bytesInBuffer - consumed;
                if (leftover > 0)
                {
                    if (leftover == buffer.Length)
                    {
                        byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, leftover).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                    else if (consumed > 0)
                    {
                        buffer.AsSpan(consumed, leftover).CopyTo(buffer);
                    }
                }

                bytesInBuffer = leftover;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Fallback scan failed for '{locale}': {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return null;
    }

#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
    internal static IReadOnlyList<AotLocaleResourceDiagnostic> CaptureAotSmokeResourceDiagnostics()
    {
        return
        [
            CaptureAotSmokeResource("zh-CN", ZhCn),
            CaptureAotSmokeResource("zh-TW", ZhTw),
            CaptureAotSmokeResource("en-US", EnUs),
            CaptureAotSmokeResource("ja-JP", JaJp),
            CaptureAotSmokeResource("de-DE", DeDe),
            CaptureAotSmokeResource("pt-BR", PtBr),
            CaptureAotSmokeResource("hi-IN", HiIn),
            CaptureAotSmokeResource("es-ES", EsEs),
            CaptureAotSmokeResource("fr-FR", FrFr),
            CaptureAotSmokeResource("ar-SA", ArSa),
            CaptureAotSmokeResource("bn-BD", BnBd),
            CaptureAotSmokeResource("ru-RU", RuRu),
            CaptureAotSmokeResource("ko-KR", KoKr)
        ];
    }

    private static AotLocaleResourceDiagnostic CaptureAotSmokeResource(
        string locale,
        IReadOnlyDictionary<string, string> resources)
    {
        return new AotLocaleResourceDiagnostic(
            locale,
            resources.Count,
            // The settings window title key was renamed by the settings-entry
            // consolidation (bc8fd2eb retired "Window.Settings.Title"); the
            // smoke keeps requiring the live key, "Settings.WindowTitle".
            resources.TryGetValue("Settings.WindowTitle", out string? settingsTitle) &&
                !string.IsNullOrWhiteSpace(settingsTitle),
            resources.TryGetValue("Search.Action.OpenSettings", out string? openSettings) &&
                !string.IsNullOrWhiteSpace(openSettings));
    }

    internal static Dictionary<string, string>? DeserializeAotSmokeLocaleDictionary(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, LocalizationJsonContext.Default.LocalizedStrings);
#endif
}

#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
internal sealed record AotLocaleResourceDiagnostic(
    string Locale,
    int ResourceCount,
    bool HasSettingsTitle,
    bool HasOpenSettingsAction);
#endif

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(
    typeof(Dictionary<string, string>),
    TypeInfoPropertyName = "LocalizedStrings")]
internal sealed partial class LocalizationJsonContext : JsonSerializerContext
{
}
