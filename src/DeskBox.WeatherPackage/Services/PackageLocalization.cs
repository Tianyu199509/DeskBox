using System.Globalization;
using System.Text.Json;

namespace DeskBox.WeatherPackage.Services;

public sealed class PackageLocalization
{
    private readonly string _packageRoot;
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public string CurrentCultureName => Culture.Name;
    public string ApiLanguageCode => Culture.TwoLetterISOLanguageName;
    public bool IsEnglish => Culture.TwoLetterISOLanguageName == "en";
    public event Action? LanguageChanged;
    public PackageLocalization(string packageRoot, string locale)
    {
        _packageRoot = packageRoot;
        SetCulture(locale);
    }
    public void SetCulture(string locale)
    {
        try { Culture = CultureInfo.GetCultureInfo(locale); }
        catch (CultureNotFoundException) { Culture = CultureInfo.GetCultureInfo("en-US"); }
        _strings.Clear();
        Read("en-US");
        if (Culture.Name != "en-US") Read(Culture.Name);
        LanguageChanged?.Invoke();
    }
    private void Read(string locale)
    {
        string file = Path.Combine(_packageRoot, "strings", locale + ".json");
        if (!File.Exists(file)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var property in doc.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String) _strings[property.Name] = property.Value.GetString()!;
    }
    public string T(string key) => _strings.GetValueOrDefault(key, key);
    public string Format(string key, params object[] args) => string.Format(Culture, T(key), args);
    public static bool IsTraditionalChineseCulture(string? culture) => culture is not null &&
        (culture.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-MO", StringComparison.OrdinalIgnoreCase));
}
