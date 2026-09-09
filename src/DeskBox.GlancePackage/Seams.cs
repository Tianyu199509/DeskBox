namespace DeskBox
{
    // D3 transition seams for host-owned surfaces the linked production
    // Glance sources reference. D3 phase 2 replaces these with proper package
    // contracts when source files are copied and host code is removed.
    internal static class App
    {
        internal static void LogVerbose(string message) { }
    }
}

namespace DeskBox.Services
{
    internal static class LocalizationService
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
}
