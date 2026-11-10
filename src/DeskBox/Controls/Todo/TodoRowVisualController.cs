using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeskBox.Helpers;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.Todo;

/// <summary>
/// Caches realized visual parts per row container and manages row subscription,
/// hover overlay, completion checkbox sync, and manual-reorder drop borders.
/// </summary>
internal sealed class TodoRowVisualController
{
    internal sealed record RowVisualParts(
        Border? HoverBackground,
        Border? ActionHost,
        CheckBox? CompletionCheckBox);

    private readonly ConditionalWeakTable<DependencyObject, RowVisualParts> _partsCache = new();
    private readonly Dictionary<FrameworkElement, TodoItemViewModel> _subscribedRowItems = [];
    private Border? _reorderDropTarget;

    public RowVisualParts GetOrResolveParts(
        DependencyObject container,
        Func<DependencyObject, RowVisualParts> resolver)
    {
        if (_partsCache.TryGetValue(container, out RowVisualParts? cached) &&
            (cached.HoverBackground is not null ||
             cached.ActionHost is not null ||
             cached.CompletionCheckBox is not null))
        {
            return cached;
        }

        RowVisualParts resolved = resolver(container);
        _partsCache.AddOrUpdate(container, resolved);
        return resolved;
    }

    public bool AttachRow(
        FrameworkElement sender,
        PropertyChangedEventHandler propertyChangedHandler,
        out TodoItemViewModel? item)
    {
        if (_subscribedRowItems.Remove(sender, out TodoItemViewModel? previousItem))
        {
            previousItem.PropertyChanged -= propertyChangedHandler;
        }

        if (sender.DataContext is not TodoItemViewModel currentItem)
        {
            item = null;
            return false;
        }

        currentItem.PropertyChanged -= propertyChangedHandler;
        currentItem.PropertyChanged += propertyChangedHandler;
        _subscribedRowItems[sender] = currentItem;
        item = currentItem;
        return true;
    }

    public void ClearSubscribedRows(PropertyChangedEventHandler propertyChangedHandler)
    {
        foreach (TodoItemViewModel item in _subscribedRowItems.Values)
        {
            item.PropertyChanged -= propertyChangedHandler;
        }

        _subscribedRowItems.Clear();
    }

    public void ApplyReorderDropState(Border border, bool active, bool insertAfter)
    {
        if (active &&
            _reorderDropTarget is { } previousBorder &&
            !ReferenceEquals(previousBorder, border))
        {
            ResetReorderDropBorder(previousBorder);
        }

        if (active)
        {
            border.BorderBrush = new SolidColorBrush(
                NeutralInteractionBrush.Line(border));
            border.BorderThickness = insertAfter
                ? new Thickness(0, 0, 0, 2)
                : new Thickness(0, 2, 0, 0);
            _reorderDropTarget = border;
            return;
        }

        ResetReorderDropBorder(border);
        if (ReferenceEquals(_reorderDropTarget, border))
        {
            _reorderDropTarget = null;
        }
    }

    public void ClearReorderDropState()
    {
        if (_reorderDropTarget is not { } border)
        {
            return;
        }

        _reorderDropTarget = null;
        ResetReorderDropBorder(border);
    }

    private static void ResetReorderDropBorder(Border border)
    {
        border.BorderBrush = new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(0);
    }
}
