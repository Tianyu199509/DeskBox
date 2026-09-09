namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Locale classification rules owned by the package. D3 Phase 2: replaces the
/// host-mimicking LocalizationService seam - the package must not depend on
/// host namespaces for its own behavior.
/// </summary>
internal static class CultureRules
{
    internal static bool IsTraditionalChineseCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return false;
        string normalized = cultureName.Replace('_', '-');
        return normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
    }
}
