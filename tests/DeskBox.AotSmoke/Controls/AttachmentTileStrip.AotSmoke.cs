#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public sealed partial class AttachmentTileStrip
{
    internal AotAttachmentTileSnapshot CaptureAotAttachmentTileSnapshot()
    {
        int itemCount = AttachmentItems.Items.Count;
        AotAttachmentTileObservation? tile = TryGetAotAttachmentTile();
        if (tile is null)
        {
            return new AotAttachmentTileSnapshot(
                itemCount,
                ContainerRealized: false,
                DataContextId: null,
                DisplayName: string.Empty,
                Type: string.Empty,
                StorageMode: string.Empty,
                Exists: false,
                DisplayNameProjected: false,
                Glyph: string.Empty,
                GlyphProjected: false,
                RemoveButtonFound: false,
                OpenAutomationName: string.Empty);
        }

        return new AotAttachmentTileSnapshot(
            itemCount,
            ContainerRealized: true,
            tile.Attachment.Id,
            tile.Attachment.DisplayName,
            tile.Attachment.Type,
            tile.Attachment.StorageMode,
            tile.Attachment.Exists,
            string.Equals(
                tile.DisplayNameText.Text,
                tile.Attachment.DisplayName,
                StringComparison.Ordinal),
            tile.Icon.Glyph,
            string.Equals(
                tile.Icon.Glyph,
                tile.Attachment.Glyph,
                StringComparison.Ordinal) &&
                tile.Icon.Visibility == tile.Attachment.FileIconVisibility,
            tile.RemoveButton is not null,
            AutomationProperties.GetName(tile.OpenButton));
    }

    internal async Task<AotAttachmentTileObservation> WaitForAotAttachmentTileAsync(
        string attachmentId,
        string displayName)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            UpdateLayout();
            AotAttachmentTileObservation? tile = TryGetAotAttachmentTile();
            if (AttachmentItems.Items.Count == 1 &&
                tile is not null &&
                string.Equals(tile.Attachment.Id, attachmentId, StringComparison.Ordinal) &&
                string.Equals(tile.Attachment.DisplayName, displayName, StringComparison.Ordinal) &&
                tile.Attachment.Exists &&
                string.Equals(tile.DisplayNameText.Text, displayName, StringComparison.Ordinal) &&
                string.Equals(tile.Icon.Glyph, tile.Attachment.Glyph, StringComparison.Ordinal) &&
                tile.Icon.Visibility == tile.Attachment.FileIconVisibility &&
                string.Equals(
                    AutomationProperties.GetName(tile.OpenButton),
                    displayName,
                    StringComparison.Ordinal))
            {
                return tile;
            }

            await Task.Delay(50);
        }

        AotAttachmentTileSnapshot snapshot = CaptureAotAttachmentTileSnapshot();
        throw new InvalidOperationException(
            "The real attachment tile did not project the expected AOT state. " +
            $"ItemCount={snapshot.ItemCount}; " +
            $"ContainerRealized={snapshot.ContainerRealized}; " +
            $"DataContextId={snapshot.DataContextId ?? "<null>"}; " +
            $"DisplayName={snapshot.DisplayName}; " +
            $"DisplayNameProjected={snapshot.DisplayNameProjected}; " +
            $"Glyph={snapshot.Glyph}; GlyphProjected={snapshot.GlyphProjected}; " +
            $"RemoveButtonFound={snapshot.RemoveButtonFound}; " +
            $"OpenAutomationName={snapshot.OpenAutomationName}.");
    }

    internal async Task WaitForAotAttachmentTileEmptyAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            UpdateLayout();
            if (AttachmentItems.Items.Count == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            "The deleted Todo attachment remained in the real tile strip.");
    }

    private AotAttachmentTileObservation? TryGetAotAttachmentTile()
    {
        if (AttachmentItems.Items.Count != 1 ||
            AttachmentItems.ContainerFromIndex(0) is not DependencyObject container)
        {
            return null;
        }

        TodoAttachmentViewModel? attachment =
            FindAotAttachmentDataContext(container);
        if (attachment is null)
        {
            return null;
        }

        TextBlock? displayName = FindAotAttachmentDescendant<TextBlock>(
            container,
            element => string.Equals(
                element.Text,
                attachment.DisplayName,
                StringComparison.Ordinal));
        FontIcon? icon = FindAotAttachmentDescendant<FontIcon>(
            container,
            element => string.Equals(
                element.Glyph,
                attachment.Glyph,
                StringComparison.Ordinal));
        Button? removeButton = FindAotAttachmentDescendant<Button>(
            container,
            element => string.Equals(
                element.Name,
                "RemoveAttachmentButton",
                StringComparison.Ordinal));
        Button? openButton = FindAotAttachmentDescendant<Button>(
            container,
            element => string.Equals(
                AutomationProperties.GetName(element),
                attachment.DisplayName,
                StringComparison.Ordinal));
        return displayName is null || icon is null || removeButton is null || openButton is null
            ? null
            : new AotAttachmentTileObservation(
                attachment,
                displayName,
                icon,
                removeButton,
                openButton);
    }

    private static TodoAttachmentViewModel? FindAotAttachmentDataContext(
        DependencyObject root)
    {
        if (root is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return attachment;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            TodoAttachmentViewModel? nested = FindAotAttachmentDataContext(
                VisualTreeHelper.GetChild(root, index));
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindAotAttachmentDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            T? nested = FindAotAttachmentDescendant(
                child,
                predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}

internal sealed record AotAttachmentTileSnapshot(
    int ItemCount,
    bool ContainerRealized,
    string? DataContextId,
    string DisplayName,
    string Type,
    string StorageMode,
    bool Exists,
    bool DisplayNameProjected,
    string Glyph,
    bool GlyphProjected,
    bool RemoveButtonFound,
    string OpenAutomationName);

internal sealed record AotAttachmentTileObservation(
    TodoAttachmentViewModel Attachment,
    TextBlock DisplayNameText,
    FontIcon Icon,
    Button RemoveButton,
    Button OpenButton);
#endif
