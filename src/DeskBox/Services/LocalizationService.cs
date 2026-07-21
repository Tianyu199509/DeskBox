using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class LocalizationService
{
    public const string LanguageSystem = SettingsService.LanguageSystem;
    public const string LanguageChinese = SettingsService.LanguageChinese;
    public const string LanguageEnglish = SettingsService.LanguageEnglish;

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
                language = ResolveSystemLanguage();
            }

            return language;
        }
    }

    public bool IsEnglish => string.Equals(CurrentCultureName, LanguageEnglish, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> AvailableLanguageSettings { get; } =
    [
        LanguageSystem,
        LanguageChinese,
        LanguageEnglish
    ];

    public string GetLanguageDisplayName(string language)
    {
        return NormalizeLanguageSetting(language) switch
        {
            LanguageChinese => T("Language.Chinese"),
            LanguageEnglish => T("Language.English"),
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
        var table = IsEnglish ? EnUs : ZhCn;
        if (table.TryGetValue(key, out string? value))
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
        return language is LanguageChinese or LanguageEnglish
            ? language
            : LanguageSystem;
    }

    private static string ResolveSystemLanguage()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LanguageChinese
            : LanguageEnglish;
    }

    private static Dictionary<string, string>? _zhCn;
    private static Dictionary<string, string>? _enUs;
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
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }
}
