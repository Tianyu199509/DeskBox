#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Shared probe helpers for the AOT menu smoke fixtures against the
/// single-column MenuFlyout item menus.
/// </summary>
internal static class AotContextMenuProbe
{
    internal static MenuFlyoutItem FindItem(
        MenuFlyout flyout,
        string text) =>
        flyout.Items
            .OfType<MenuFlyoutItem>()
            .Single(item => string.Equals(
                item.Text,
                text,
                StringComparison.Ordinal));

    internal static int Count(MenuFlyout flyout) => flyout.Items.Count;

    internal static int IndexOf(
        MenuFlyout flyout,
        MenuFlyoutItem target) =>
        flyout.Items.ToList().IndexOf(target);

    internal static IReadOnlyList<AotContextMenuEntrySnapshot> Snapshot(
        MenuFlyout flyout,
        MenuFlyoutItem target) =>
        flyout.Items
            .Select((element, index) => new AotContextMenuEntrySnapshot(
                index,
                element.GetType().Name,
                element switch
                {
                    MenuFlyoutItem item => item.Text,
                    MenuFlyoutSubItem sub => sub.Text,
                    _ => string.Empty
                },
                element is not Control control || control.IsEnabled,
                ReferenceEquals(element, target)))
            .ToArray();

    internal static IInvokeProvider InvokeProvider(Control control)
    {
        if (FrameworkElementAutomationPeer.CreatePeerForElement(control)
                ?.GetPattern(PatternInterface.Invoke)
            is IInvokeProvider provider)
        {
            return provider;
        }

        throw new InvalidOperationException(
            "The product menu entry did not expose Invoke automation.");
    }
}

internal sealed record AotContextMenuEntrySnapshot(
    int Index,
    string ItemType,
    string Text,
    bool IsEnabled,
    bool IsTarget);
#endif
