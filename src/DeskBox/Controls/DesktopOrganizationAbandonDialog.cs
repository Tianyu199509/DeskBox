using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

/// <summary>
/// The confirmation shown before abandoning an interrupted restore. It names
/// the stuck items, why each cannot be restored, and where the copies live,
/// because "some items were not restored" alone gives the user nothing to act on.
/// </summary>
internal static class DesktopOrganizationAbandonDialog
{
    public static async Task<bool> ConfirmAsync(
        XamlRoot? xamlRoot,
        LocalizationService localization,
        string? itemDetails)
    {
        if (xamlRoot is null)
        {
            return true;
        }

        var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = localization.T("DesktopOrganization.Public.AbandonConfirmBody"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(itemDetails))
        {
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = itemDetails,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    // Application-scope themed lookups follow the system
                    // theme; resolve against the host window's root instead.
                    Foreground = NeutralInteractionBrush.ResolveThemedResource(
                        "TextFillColorSecondaryBrush", xamlRoot.Content) ??
                        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E))
                }
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = localization.T("DesktopOrganization.Public.AbandonConfirmTitle"),
            PrimaryButtonText = localization.T("DesktopOrganization.Public.AbandonRestore"),
            CloseButtonText = localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static string BuildItemDetails(
        LocalizationService localization,
        OrganizationHistoryEntry history)
    {
        List<OrganizationHistoryItem> stuck = history.Items.Where(item => !item.IsRestored).ToList();
        if (stuck.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(localization.Format("DesktopOrganization.Public.AbandonListHeader", stuck.Count));
        const int shownLimit = 8;
        for (int index = 0; index < Math.Min(stuck.Count, shownLimit); index++)
        {
            OrganizationHistoryItem item = stuck[index];
            builder.Append("• ").AppendLine(item.Name);
            builder.AppendLine("  " + localization.T(DesktopOrganizationTransaction.GetUndoBlockReasonKey(item)));
            builder.AppendLine("  " + localization.Format("DesktopOrganization.Public.AbandonItemSource", item.SourcePath));
            builder.AppendLine("  " + localization.Format("DesktopOrganization.Public.AbandonItemDestination", item.DestinationPath));
        }

        if (stuck.Count > shownLimit)
        {
            builder.AppendLine(localization.Format("DesktopOrganization.Public.AbandonMoreItems", stuck.Count - shownLimit));
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildCompactStuckSummary(
        LocalizationService localization,
        OrganizationHistoryEntry? history)
    {
        if (history is null)
        {
            return string.Empty;
        }

        List<OrganizationHistoryItem> stuck = history.Items.Where(item => !item.IsRestored).ToList();
        if (stuck.Count == 0)
        {
            return string.Empty;
        }

        const int shownLimit = 4;
        IEnumerable<string> preview = stuck
            .Take(shownLimit)
            .Select(item => $"{item.Name}（{localization.T(DesktopOrganizationTransaction.GetUndoBlockReasonKey(item))}）");
        string suffix = stuck.Count > shownLimit
            ? $" +{stuck.Count - shownLimit}"
            : string.Empty;
        return "\n" + string.Join("；", preview) + suffix;
    }

    public static string BuildRecoveryItemDetails(
        LocalizationService localization,
        DesktopOrganizationRecoveryJournal? journal)
    {
        if (journal is null || journal.Items.Count == 0)
        {
            return string.Empty;
        }

        List<DesktopOrganizationRecoveryItem> items = journal.Items
            .Where(item =>
                item.Completed ||
                File.Exists(item.DestinationPath) ||
                Directory.Exists(item.DestinationPath))
            .ToList();
        if (items.Count == 0)
        {
            items = journal.Items.ToList();
        }

        var builder = new StringBuilder();
        builder.AppendLine(localization.Format("DesktopOrganization.Public.AbandonListHeader", items.Count));
        const int shownLimit = 8;
        for (int index = 0; index < Math.Min(items.Count, shownLimit); index++)
        {
            DesktopOrganizationRecoveryItem item = items[index];
            string displayName = Path.GetFileName(
                !string.IsNullOrWhiteSpace(item.DestinationPath) ? item.DestinationPath : item.SourcePath);
            builder.Append("• ").AppendLine(displayName);
            builder.AppendLine("  " + localization.Format("DesktopOrganization.Public.AbandonItemSource", item.SourcePath));
            builder.AppendLine("  " + localization.Format("DesktopOrganization.Public.AbandonItemDestination", item.DestinationPath));
        }

        if (items.Count > shownLimit)
        {
            builder.AppendLine(localization.Format("DesktopOrganization.Public.AbandonMoreItems", items.Count - shownLimit));
        }

        return builder.ToString().TrimEnd();
    }
}
