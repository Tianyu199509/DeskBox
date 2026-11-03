using System.Collections;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public sealed class AttachmentTileEventArgs(TodoAttachmentViewModel attachment) : EventArgs
{
    public TodoAttachmentViewModel Attachment { get; } = attachment;
}

public sealed partial class AttachmentTileStrip : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(object),
        typeof(AttachmentTileStrip),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty CanRemoveProperty = DependencyProperty.Register(
        nameof(CanRemove),
        typeof(bool),
        typeof(AttachmentTileStrip),
        new PropertyMetadata(true, OnCanRemoveChanged));

    public static readonly DependencyProperty RemoveAutomationNameProperty = DependencyProperty.Register(
        nameof(RemoveAutomationName),
        typeof(string),
        typeof(AttachmentTileStrip),
        new PropertyMetadata(string.Empty));

    public AttachmentTileStrip()
    {
        InitializeComponent();
    }

    internal void ReleaseVisualHandlers()
    {
        ItemsSource = null;
        AttachmentItems.ItemsSource = null;
        Content = null;
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool CanRemove
    {
        get => (bool)GetValue(CanRemoveProperty);
        set => SetValue(CanRemoveProperty, value);
    }

    public string RemoveAutomationName
    {
        get => (string)GetValue(RemoveAutomationNameProperty);
        set => SetValue(RemoveAutomationNameProperty, value);
    }

    public event EventHandler<AttachmentTileEventArgs>? OpenRequested;

    public event EventHandler<AttachmentTileEventArgs>? RemoveRequested;

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is AttachmentTileStrip strip)
        {
            strip.AttachmentItems.ItemsSource = args.NewValue as IEnumerable;
        }
    }

    private static void OnCanRemoveChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is AttachmentTileStrip strip && args.NewValue is false)
        {
            strip.HideAllRemoveButtons(strip.AttachmentItems);
        }
    }

    private async void AttachmentTile_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureThumbnailAsync((sender as FrameworkElement)?.DataContext);
    }

    private async void AttachmentTile_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        SetRemoveButtonVisible(sender, false);
        await EnsureThumbnailAsync(args.NewValue);
    }

    private static async Task EnsureThumbnailAsync(object? dataContext)
    {
        if (dataContext is TodoAttachmentViewModel attachment)
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private void AttachmentTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement tile)
        {
            SetRemoveButtonVisible(tile, CanRemove);
        }
    }

    private void AttachmentTile_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement tile)
        {
            SetRemoveButtonVisible(tile, CanRemove);
        }
    }

    private void AttachmentTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement tile && !ContainsKeyboardFocus(tile))
        {
            SetRemoveButtonVisible(tile, false);
        }
    }

    private void AttachmentTile_LosingFocus(
        UIElement sender,
        LosingFocusEventArgs args)
    {
        if (sender is FrameworkElement tile &&
            (args.NewFocusedElement is not DependencyObject newFocus ||
             !IsDescendantOf(newFocus, tile)))
        {
            SetRemoveButtonVisible(tile, false);
        }
    }

    private static bool ContainsKeyboardFocus(FrameworkElement tile)
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(tile.XamlRoot) as DependencyObject;
        return focused is not null && IsDescendantOf(focused, tile);
    }

    private static bool IsDescendantOf(DependencyObject descendant, DependencyObject ancestor)
    {
        for (DependencyObject? current = descendant;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void SetRemoveButtonVisible(FrameworkElement tile, bool visible)
    {
        if (FindDescendant<Button>(tile, "RemoveAttachmentButton") is not { } button)
        {
            return;
        }

        button.Opacity = visible ? 1 : 0;
        button.IsHitTestVisible = visible;
        button.IsTabStop = visible;
        AutomationProperties.SetName(button, RemoveAutomationName);
        ToolTipService.SetToolTip(button, RemoveAutomationName);
    }

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            if (FindDescendant<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void HideAllRemoveButtons(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Button { Name: "RemoveAttachmentButton" } button)
            {
                button.Opacity = 0;
                button.IsHitTestVisible = false;
                button.IsTabStop = false;
            }

            HideAllRemoveButtons(child);
        }
    }

    private void OpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            OpenRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));
        }
    }

    private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanRemove && sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            RemoveRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));
        }
    }
}
