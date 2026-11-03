namespace DeskBox.Services;

/// <summary>
/// Pure decision helpers for the standalone Widget Workshop window. Kept free
/// of UI types so the routing rules (which AI path to take, which clipboard
/// text counts as an AI reply) stay unit-testable.
/// </summary>
public static class CustomWidgetWorkshopFlow
{
    /// <summary>
    /// Picks the provider for one-click generation, or <c>null</c> when the
    /// user has no key and the free web-AI path must be used instead.
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

    /// <summary>
    /// Returns the manifest JSON carried by clipboard text when it looks like a
    /// widget reply, ignoring the prompt DeskBox itself just copied and any
    /// unrelated text the user happens to copy while waiting.
    /// </summary>
    public static bool TryExtractManifestReply(
        string? clipboardText,
        string? lastSelfCopiedText,
        out string manifestJson)
    {
        manifestJson = string.Empty;
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(lastSelfCopiedText) &&
            string.Equals(clipboardText.Trim(), lastSelfCopiedText.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string extracted = CustomWidgetTemplates.ExtractJsonFromClipboardText(clipboardText);
        if (string.IsNullOrWhiteSpace(extracted) || !LooksLikeWidgetManifest(extracted))
        {
            return false;
        }

        manifestJson = extracted;
        return true;
    }

    public static bool LooksLikeWidgetManifest(string json) =>
        json.Contains("\"root\"", StringComparison.Ordinal) ||
        json.Contains("\"view\"", StringComparison.Ordinal) ||
        json.Contains("\"manifestVersion\"", StringComparison.Ordinal) ||
        json.Contains("\"schemaVersion\"", StringComparison.Ordinal);

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
        double clampedWidget = Math.Clamp(widgetWidth, 240, 440);
        double idealStage = Math.Max(304, clampedWidget + 40);
        if (availableWidth <= 0)
        {
            return Math.Min(idealStage, 360);
        }

        double maxStage = Math.Clamp(Math.Round(availableWidth * 0.38), 276, 460);
        return Math.Clamp(idealStage, 276, maxStage);
    }
}
