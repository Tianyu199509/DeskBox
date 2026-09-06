using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan TypeSelectionResetDelay =
        TimeSpan.FromMilliseconds(1100);
    private string _typeSelectionPrefix = string.Empty;
    private DateTimeOffset _lastTypeSelectionAt;

    private void Root_CharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs e)
    {
        object? focused = XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox ||
            focused is DependencyObject focusedObject &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(focusedObject) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu) ||
            e.Character == 0 ||
            e.Character > char.MaxValue ||
            char.IsControl((char)e.Character))
        {
            return;
        }

        char character = (char)e.Character;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool expired = now - _lastTypeSelectionAt > TypeSelectionResetDelay;
        bool cycleSameInitial = !expired &&
            _typeSelectionPrefix.Length == 1 &&
            char.ToUpper(_typeSelectionPrefix[0]) == char.ToUpper(character);
        _typeSelectionPrefix = expired || cycleSameInitial
            ? character.ToString()
            : _typeSelectionPrefix + character;
        _lastTypeSelectionAt = now;

        ListViewBase view = GetActiveItemsView();
        WidgetItem[] items = view.Items
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        int selectedIndex = view.SelectedItem is WidgetItem selected
            ? Array.IndexOf(items, selected)
            : -1;
        IEnumerable<WidgetItem> candidates = cycleSameInitial
            ? items.Skip(selectedIndex + 1).Concat(items.Take(selectedIndex + 1))
            : items;
        WidgetItem? match = candidates.FirstOrDefault(item =>
            item.Name.StartsWith(
                _typeSelectionPrefix,
                StringComparison.CurrentCultureIgnoreCase));
        if (match is null && _typeSelectionPrefix.Length > 1)
        {
            _typeSelectionPrefix = character.ToString();
            match = items.FirstOrDefault(item =>
                item.Name.StartsWith(
                    _typeSelectionPrefix,
                    StringComparison.CurrentCultureIgnoreCase));
        }

        if (match is null)
        {
            return;
        }

        view.SelectedItems.Clear();
        view.SelectedItems.Add(match);
        view.ScrollIntoView(match, ScrollIntoViewAlignment.Default);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (view.ContainerFromItem(match) is Control container)
            {
                container.Focus(FocusState.Keyboard);
            }
        });
        e.Handled = true;
    }

    private void ShowKeyboardContextMenu()
    {
        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        if (_settingsService.Settings.FileItemSystemContextMenuEnabled &&
            selectedItems.Count == 1 &&
            selectedItems[0] is not WidgetStackItem)
        {
            // Same single-item limitation as the right-tap path: the native
            // Shell menu cannot host stacks or multi-selection.
            _ = ShowSystemContextMenuAsync(selectedItems[0]);
            return;
        }

        MenuFlyout flyout = selectedItems.Count switch
        {
            0 => CreateContentAreaFlyout(),
            > 1 => CreateMultiSelectionFlyout(),
            _ => CreateItemFlyout(selectedItems[0])
        };
        FrameworkElement target = selectedItems.FirstOrDefault() is { } item &&
            GetActiveItemsView().ContainerFromItem(item) is FrameworkElement container
                ? container
                : Root;
        flyout.ShowAt(target);
    }
}
