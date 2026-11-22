namespace DeskBox.Services;

/// <summary>
/// Pure decision helpers for the standalone Widget Workshop window. Kept free
/// of UI types so routing and responsive geometry rules stay unit-testable.
/// </summary>
public static class CustomWidgetWorkshopFlow
{
    /// <summary>
    /// Picks the provider for one-click generation, or <c>null</c> when no provider is configured.
    /// </summary>
    public static string? ResolveDirectProvider(
        IReadOnlyList<CustomWidgetAiProviderPreset> configuredProviders,
        string? preferredProviderId)
    {
        if (configuredProviders.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredProviderId))
        {
            foreach (CustomWidgetAiProviderPreset provider in configuredProviders)
            {
                if (string.Equals(provider.Id, preferredProviderId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return provider.Id;
                }
            }
        }

        return configuredProviders[0].Id;
    }

    /// <summary>One-line summary of a refinement request for the change list.</summary>
    public static string SummarizePrompt(string? prompt, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        string compact = prompt.Trim()
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        return compact.Length > maxLength
            ? compact[..maxLength] + "…"
            : compact;
    }

    public static int ResolveGalleryColumnCount(double availableWidth) =>
        availableWidth >= 800 ? 3 : 2;

    /// <summary>
    /// Computes a compact width for the left-hand live widget preview stage so the
    /// widget displays near 1:1 desktop scale without empty surrounding dead space,
    /// leaving the majority (~62–70%) of the window for the primary AI workspace on the right.
    /// </summary>
    public static double ResolvePreviewStageWidth(double availableWidth, double widgetWidth = 320)
    {
        bool wideWidget = widgetWidth > 440;
        double clampedWidget = Math.Clamp(widgetWidth, 240, 640);
        double idealStage = Math.Max(304, clampedWidget + 40);
        if (availableWidth <= 0)
        {
            return Math.Min(idealStage, wideWidget ? 480 : 360);
        }

        double ratio = wideWidget ? 0.44 : 0.38;
        double maxCeiling = wideWidget ? 560 : 460;
        double maxStage = Math.Clamp(Math.Round(availableWidth * ratio), 276, maxCeiling);
        return Math.Clamp(idealStage, 276, maxStage);
    }
}
