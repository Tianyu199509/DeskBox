﻿﻿﻿using System.Globalization;
using System.Reflection;
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

    public string CurrentCultureName
    {
        get
        {
            string language = LanguageSetting;
            if (language == LanguageSystem)
            {
                language = ResolveDefaultLanguage();
            }

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
        RaiseLanguageChanged();
    }

    /// <summary>
    /// Re-raises <see cref="LanguageChanged"/> after an external snapshot
    /// (the settings host's commit) changed the persisted language: new
    /// string reads pick the language up from Settings directly, but every
    /// already-rendered surface only refreshes through this event.
    /// </summary>
    internal void NotifyExternalLanguageChange() => RaiseLanguageChanged();

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

    public string T(string key)
    {
        var table = CurrentCultureName switch
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
            _ => IsEnglish ? EnUs : ZhCn
        };
        
        if (table.TryGetValue(key, out string? value))
        {
            return value;
        }

        // All shipped locale files are expected to be complete. Keep English
        // as a defensive fallback for unknown keys in future app versions.
        if (EnUs.TryGetValue(key, out value))
        {
            return value;
        }

        return ZhCn.TryGetValue(key, out value) ? value : key;
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    public static string DefaultText(string key)
    {
        return ZhCn.TryGetValue(key, out string? value) ? value : key;
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
    /// (the Inno Setup installer writes it to
    /// HKCU\Software\DeskBox\InstallLanguage), falling back to the OS UI
    /// culture when that value is absent or unrecognized.
    /// </summary>
    private static string ResolveDefaultLanguage()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\DeskBox",
                "InstallLanguage",
                null) as string;
            if (!string.IsNullOrWhiteSpace(value)
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
                    || value == LanguageKorean))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[LocalizationService] Failed reading InstallLanguage registry key: {ex.Message}");
        }

        return ResolveSystemLanguage();
    }

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

    private static Dictionary<string, string> ZhCn
    {
        get
        {
            if (_zhCn is not null) return _zhCn;
            lock (s_loadLock)
            {
                _zhCn ??= LoadStringResource("DeskBox.Strings.zh-CN.json");
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
                _zhTw ??= LoadStringResource("DeskBox.Strings.zh-TW.json");
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
                _enUs ??= LoadStringResource("DeskBox.Strings.en-US.json");
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
                _jaJp ??= LoadStringResource("DeskBox.Strings.ja-JP.json");
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
                _deDe ??= LoadStringResource("DeskBox.Strings.de-DE.json");
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
                _ptBr ??= LoadStringResource("DeskBox.Strings.pt-BR.json");
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
                _hiIn ??= LoadStringResource("DeskBox.Strings.hi-IN.json");
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
                _esEs ??= LoadStringResource("DeskBox.Strings.es-ES.json");
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
                _frFr ??= LoadStringResource("DeskBox.Strings.fr-FR.json");
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
                _arSa ??= LoadStringResource("DeskBox.Strings.ar-SA.json");
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
                _bnBd ??= LoadStringResource("DeskBox.Strings.bn-BD.json");
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
                _ruRu ??= LoadStringResource("DeskBox.Strings.ru-RU.json");
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
                _koKr ??= LoadStringResource("DeskBox.Strings.ko-KR.json");
            }
            return _koKr;
        }
    }

    private static Dictionary<string, string> LoadStringResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Resource not found: {resourceName}");
            return [];
        }
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize(
            json,
            LocalizationJsonContext.Default.LocalizedStrings) ?? [];
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
