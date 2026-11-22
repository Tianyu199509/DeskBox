using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Unified lifecycle contract implemented by every Custom Widget capability / data-source provider
/// hosted inside <see cref="CustomWidgetCapabilityHost"/>.
/// </summary>
internal interface ICustomWidgetCapabilityProvider : IDisposable
{
    string ProviderId { get; }
    IReadOnlyList<string> CapabilityIds { get; }
    void UpdateSubscription();
    void Stop();
}

internal static class CapabilityProviderHelpers
{
    internal static bool ManifestWhenReferencesToken(CustomWidgetManifest manifest, string tokenFragment) =>
        manifest.EnumerateAllNodes().Any(node =>
            !string.IsNullOrEmpty(node.When) &&
            node.When.Contains(tokenFragment, StringComparison.OrdinalIgnoreCase));

    internal static bool NodeTreeWhenReferencesToken(CustomWidgetNodeSpec node, string tokenFragment) =>
        node.EnumerateAllNodes().Any(n =>
            !string.IsNullOrEmpty(n.When) &&
            n.When.Contains(tokenFragment, StringComparison.OrdinalIgnoreCase));

    internal static bool ManifestWhenReferencesNonPresenceToken(CustomWidgetManifest manifest, string tokenFragment) =>
        manifest.EnumerateAllNodes().Any(node => SingleNodeWhenReferencesNonPresenceToken(node, tokenFragment));

    internal static bool NodeTreeWhenReferencesNonPresenceToken(CustomWidgetNodeSpec node, string tokenFragment) =>
        node.EnumerateAllNodes().Any(n => SingleNodeWhenReferencesNonPresenceToken(n, tokenFragment));

    private static bool SingleNodeWhenReferencesNonPresenceToken(CustomWidgetNodeSpec node, string tokenFragment)
    {
        if (!string.IsNullOrEmpty(node.When) &&
            node.When.Contains(tokenFragment, StringComparison.OrdinalIgnoreCase))
        {
            string normalized = node.When.Replace(" ", string.Empty, StringComparison.Ordinal);
            bool onlyPresenceCheck =
                normalized.Contains("$clipboard.text!=''", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("$clipboard.text==''", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("$clipboard.hasText", StringComparison.OrdinalIgnoreCase);
            return !onlyPresenceCheck;
        }

        return false;
    }
}
