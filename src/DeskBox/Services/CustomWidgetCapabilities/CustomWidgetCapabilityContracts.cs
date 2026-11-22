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
    internal static bool NodeTreeWhenReferencesToken(CustomWidgetNodeSpec node, string tokenFragment)
    {
        if (!string.IsNullOrEmpty(node.When) &&
            node.When.Contains(tokenFragment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (node.ItemTemplate is not null && NodeTreeWhenReferencesToken(node.ItemTemplate, tokenFragment))
        {
            return true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (NodeTreeWhenReferencesToken(node.Children[i], tokenFragment))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool NodeTreeWhenReferencesNonPresenceToken(CustomWidgetNodeSpec node, string tokenFragment)
    {
        if (!string.IsNullOrEmpty(node.When) &&
            node.When.Contains(tokenFragment, StringComparison.OrdinalIgnoreCase))
        {
            string normalized = node.When.Replace(" ", string.Empty, StringComparison.Ordinal);
            bool onlyPresenceCheck =
                normalized.Contains("$clipboard.text!=''", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("$clipboard.text==''", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("$clipboard.hasText", StringComparison.OrdinalIgnoreCase);
            if (!onlyPresenceCheck)
            {
                return true;
            }
        }

        if (node.ItemTemplate is not null && NodeTreeWhenReferencesNonPresenceToken(node.ItemTemplate, tokenFragment))
        {
            return true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (NodeTreeWhenReferencesNonPresenceToken(node.Children[i], tokenFragment))
            {
                return true;
            }
        }

        return false;
    }
}
