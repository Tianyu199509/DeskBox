using System.Globalization;
using System.Text.Json;
namespace DeskBox.MusicPackage.Services;

public sealed class MusicStrings
{
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _locale = "";
    public event Action? LanguageChanged;
    public string T(string key) => _strings.GetValueOrDefault(key, key);
    public void Configure(string locale, string packageRoot)
    {
        try { locale = CultureInfo.GetCultureInfo(locale).Name; } catch (CultureNotFoundException) { locale = "en-US"; }
        if (_locale == locale) return;
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in new[] { "en-US", locale }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(packageRoot, "strings", name + ".json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in document.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) next[p.Name] = p.Value.GetString()!;
        }
        _strings = next; _locale = locale;
        LanguageChanged?.Invoke();
    }
}
internal static class PackageLog
{
    internal static Action<string>? Sink { get; set; }
    internal static void Write(string message) => Sink?.Invoke(message);
}

