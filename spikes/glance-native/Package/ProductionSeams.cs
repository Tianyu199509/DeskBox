namespace DeskBox
{
    // Spike-local seam for the host-owned logging surface. The linked production
    // Glance services call App.LogVerbose for diagnostics only; business results
    // do not depend on it. Batch C must replace this with a real host contract.
    internal static class App
    {
        internal static void LogVerbose(string message)
        {
        }
    }
}

namespace DeskBox.Services
{
    // Spike-local seam for one pure classification helper, copied verbatim from
    // LocalizationService.cs (IsTraditionalChineseCulture). The full host
    // LocalizationService drags settings/registry/JSON subsystems that the
    // package must not own; batch C decides where this classification lives.
    internal static class LocalizationService
    {
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
    }
}
