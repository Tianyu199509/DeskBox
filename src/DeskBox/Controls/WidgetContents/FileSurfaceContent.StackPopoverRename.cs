using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private TextBox? _stackPopoverItemRenameEditor;
    private WidgetItem? _stackPopoverItemRenameTarget;
    private TextBlock? _stackPopoverItemRenameNameText;
    private Visibility _stackPopoverItemRenameOriginalVisibility =
        Visibility.Visible;
    private bool _stackPopoverItemRenameCommitInProgress;
    private bool _stackPopoverItemRenameCancelling;
    private long _stackPopoverItemRenameOpenedAtTick;

    private bool IsStackPopoverItemRenameEditing =>
        _stackPopoverItemRenameTarget is not null;

    private TextBox CreateStackPopoverItemRenameEditor(Grid itemsHost)
    {
        var editor = new TextBox
        {
            MaxWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Style = Application.Current.Resources.TryGetValue(
                "WidgetInlineRenameTextBoxStyle",
                out object? styleValue)
                ? styleValue as Style
                : null
        };
        if (ResolveBrush("TextFillColorPrimaryBrush") is { } foreground)
        {
            editor.Foreground = foreground;
        }

        Canvas.SetZIndex(editor, 40);
        editor.KeyDown += StackPopoverItemRenameEditor_KeyDown;
        editor.LostFocus += StackPopoverItemRenameEditor_LostFocus;
        AutomationProperties.SetName(editor, T("Common.Rename"));
        itemsHost.Children.Add(editor);
        _stackPopoverItemRenameEditor = editor;
        return editor;
    }

    private async Task StartStackPopoverItemRenameAsync(WidgetItem item)
    {
        // The context menu hides itself before this callback resumes. Giving
        // WinUI one turn here keeps the editor focus in the popover's HWND
        // instead of returning it to the menu's transient presenter.
        await Task.Yield();
        if (_isDisposed ||
            !IsStackPopoverInteractionActive ||
            _stackPopoverItemsView is not { } activeView ||
            _stackPopoverSelectionHost is not { } itemsHost ||
            _stackPopoverItemRenameEditor is not { } editor ||
            !IsItemInStackPopover(item))
        {
            return;
        }

        if (IsStackPopoverItemRenameEditing)
        {
            if (ReferenceEquals(_stackPopoverItemRenameTarget, item))
            {
                SelectItemNameForRename(editor, item.IsFolder);
            }
            return;
        }

        // A normal-surface editor and a popover editor are mutually exclusive.
        // This is normally impossible through the UI, but cancelling the old
        // one here prevents a stale root editor when a keyboard command races a
        // context-menu callback.
        if (_itemRenameTarget is not null)
        {
            CancelItemRename();
        }

        WidgetItem? renameItem = FindDisplayedItem(item);
        if (renameItem is null || !IsItemInStackPopover(renameItem))
        {
            App.Log(
                $"[WidgetSurface] Stack popover inline rename target " +
                $"unavailable id={WidgetId} target={item.Name}");
            return;
        }

        FrameworkElement? target =
            await FindOrRealizeStackPopoverItemRenameTargetAsync(renameItem);
        renameItem = target?.DataContext as WidgetItem ??
            FindDisplayedItem(renameItem) ??
            renameItem;
        TextBlock? nameText = target as TextBlock;
        if (target is null)
        {
            App.Log(
                $"[WidgetSurface] Stack popover inline rename visual " +
                $"unavailable id={WidgetId} target={renameItem.Name}");
            return;
        }

        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(renameItem);
        _stackPopoverItemRenameTarget = renameItem;
        _stackPopoverItemRenameNameText = nameText;
        _stackPopoverItemRenameOriginalVisibility = nameText?.Visibility ??
            Visibility.Visible;
        _stackPopoverItemRenameCancelling = false;
        _stackPopoverCleanupPending = false;
        editor.Text = renameItem.Name;

        if (nameText is not null)
        {
            nameText.Visibility = Visibility.Collapsed;
            editor.FontSize = nameText.FontSize > 0
                ? nameText.FontSize
                : ResolveStackPopoverRenameFontSize();
            editor.TextAlignment = nameText.TextAlignment;
            editor.HorizontalContentAlignment =
                nameText.HorizontalAlignment switch
                {
                    HorizontalAlignment.Center => HorizontalAlignment.Center,
                    HorizontalAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
            editor.TextWrapping = nameText.TextWrapping;
        }
        else
        {
            editor.FontSize = ResolveStackPopoverRenameFontSize();
            editor.TextAlignment = ViewModel.IsListMode
                ? TextAlignment.Left
                : TextAlignment.Center;
            editor.HorizontalContentAlignment = ViewModel.IsListMode
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Center;
            editor.TextWrapping = TextWrapping.NoWrap;
        }

        PositionStackPopoverItemRenameEditor(
            editor,
            target,
            itemsHost,
            ViewModel.IsListMode);
        editor.Visibility = Visibility.Visible;
        editor.IsHitTestVisible = true;
        _stackPopoverItemRenameOpenedAtTick = Environment.TickCount64;
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-stack-popover-item-rename-opened");
        SelectItemNameForRename(editor, renameItem.IsFolder);
    }

    private double ResolveStackPopoverRenameFontSize() =>
        ViewModel.IsListMode
            ? ViewModel.ListLabelFontSize
            : ViewModel.IconLabelFontSize;

    private async Task<FrameworkElement?>
        FindOrRealizeStackPopoverItemRenameTargetAsync(WidgetItem item)
    {
        const int realizationPasses = 5;
        for (int pass = 0; pass < realizationPasses; pass++)
        {
            if (_isDisposed ||
                !IsStackPopoverInteractionActive ||
                _stackPopoverItemsView is not { } activeView)
            {
                return null;
            }

            WidgetItem? displayedItem = FindDisplayedItem(item);
            if (displayedItem is not null)
            {
                activeView.ScrollIntoView(displayedItem);
                activeView.UpdateLayout();
                FrameworkElement? target =
                    FindItemNameElement(displayedItem) ??
                    FindItemSurface(displayedItem);
                if (target is not null)
                {
                    return target;
                }
            }

            if (!await YieldForItemContainerRealizationAsync())
            {
                break;
            }
        }

        WidgetItem? finalItem = FindDisplayedItem(item);
        return finalItem is null
            ? null
            : FindItemNameElement(finalItem) ??
              FindItemSurface(finalItem);
    }

    private static void PositionStackPopoverItemRenameEditor(
        TextBox editor,
        FrameworkElement target,
        FrameworkElement contentHost,
        bool isListMode)
    {
        Windows.Foundation.Point topLeft = target
            .TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        const double border = 1;
        const double horizontalPadding = 2;
        double offsetX = topLeft.X - border - horizontalPadding;
        double offsetY = topLeft.Y - border;
        double hostWidth = Math.Max(60, contentHost.ActualWidth);
        double hostHeight = Math.Max(20, contentHost.ActualHeight);
        double availableWidth = Math.Max(60, hostWidth - offsetX - 8);
        double maxEditorWidth = Math.Max(60, Math.Min(280, hostWidth));
        double width = isListMode
            ? Math.Min(maxEditorWidth, Math.Max(80, availableWidth))
            : Math.Clamp(
                target.ActualWidth +
                    (2 * (border + horizontalPadding)),
                60,
                Math.Min(maxEditorWidth, availableWidth));

        if (!isListMode)
        {
            offsetX = topLeft.X + ((target.ActualWidth - width) / 2);
            offsetX = Math.Clamp(offsetX, 0, Math.Max(0, hostWidth - width));
        }

        double height = Math.Max(target.ActualHeight + (2 * border), 20);
        height = Math.Min(
            height,
            Math.Max(20, hostHeight - offsetY - 4));
        editor.Width = width;
        editor.Height = height;
        editor.Margin = new Thickness(offsetX, offsetY, 0, 0);

    }

    private async void StackPopoverItemRenameEditor_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitStackPopoverItemRenameAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelStackPopoverItemRename();
        }
    }

    private async void StackPopoverItemRenameEditor_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_stackPopoverItemRenameCancelling)
        {
            _stackPopoverItemRenameCancelling = false;
            return;
        }

        if (InlineEditorFocus.TryRecoverFocusWithinGrace(
                _stackPopoverItemRenameOpenedAtTick,
                sender as TextBox))
        {
            return;
        }

        await CommitStackPopoverItemRenameAsync();
    }

    private async Task CommitStackPopoverItemRenameAsync()
    {
        if (_stackPopoverItemRenameCommitInProgress ||
            _stackPopoverItemRenameTarget is not { } target ||
            _stackPopoverItemRenameEditor is not { } editor ||
            editor.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = editor.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelStackPopoverItemRename();
            return;
        }

        _stackPopoverItemRenameCommitInProgress = true;
        try
        {
            if (!string.Equals(target.Name, newName, StringComparison.Ordinal))
            {
                if (TryBlockTransferMutation(target))
                {
                    CompleteStackPopoverItemRename();
                    return;
                }

                await ViewModel.RenameItemAsync(target, newName);
            }

            CompleteStackPopoverItemRename();
            QueueStackPopoverItemRenameRefresh();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Stack popover inline rename failed " +
                $"id={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "file-rename-error"));
            CompleteStackPopoverItemRename();
        }
        finally
        {
            _stackPopoverItemRenameCommitInProgress = false;
            TryFinishStackPopoverCleanup();
        }
    }

    private void CancelStackPopoverItemRename(bool finishPendingClose = true)
    {
        if (!IsStackPopoverItemRenameEditing)
        {
            return;
        }

        _stackPopoverItemRenameCancelling = true;
        CompleteStackPopoverItemRename(finishPendingClose);
    }

    private void CompleteStackPopoverItemRename(bool finishPendingClose = true)
    {
        if (_stackPopoverItemRenameEditor is { } editor)
        {
            editor.Visibility = Visibility.Collapsed;
            editor.IsHitTestVisible = false;
            editor.Text = string.Empty;
            editor.Margin = new Thickness(0);
        }

        if (_stackPopoverItemRenameNameText is { } nameText)
        {
            nameText.Visibility = _stackPopoverItemRenameOriginalVisibility;
        }

        bool hadTarget = _stackPopoverItemRenameTarget is not null;
        _stackPopoverItemRenameTarget = null;
        _stackPopoverItemRenameNameText = null;
        _stackPopoverItemRenameOriginalVisibility = Visibility.Visible;
        if (hadTarget)
        {
            App.Current?.WidgetManager?.EndWidgetInteraction(
                "surface-stack-popover-item-rename-closed");
        }

        if (finishPendingClose)
        {
            TryFinishStackPopoverCleanup();
        }
    }

    private void QueueStackPopoverItemRenameRefresh()
    {
        if (_stackPopoverHostWindow is null || !_stackPopoverPopupOpen)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (!_isDisposed &&
                    _stackPopoverHostWindow is not null &&
                    _stackPopoverPopupOpen)
                {
                    ReconcileStackPopover();
                    _stackPopoverSurface?.UpdateLayout();
                }
            });
    }

    private void TryFinishStackPopoverCleanup()
    {
        if (!_stackPopoverCleanupPending ||
            IsStackPopoverItemRenameEditing ||
            _stackPopoverContextMenuOpen ||
            _stackPopoverDragActive ||
            _stackPopoverTitleEditing ||
            _stackPopoverHostWindow is null)
        {
            return;
        }

        HideStackPopoverForReuse();
    }

    private void ReleaseStackPopoverItemRenameEditor()
    {
        CancelStackPopoverItemRename(finishPendingClose: false);
        if (_stackPopoverItemRenameEditor is { } editor)
        {
            editor.KeyDown -= StackPopoverItemRenameEditor_KeyDown;
            editor.LostFocus -= StackPopoverItemRenameEditor_LostFocus;
            _stackPopoverSelectionHost?.Children.Remove(editor);
        }

        _stackPopoverItemRenameEditor = null;
        _stackPopoverItemRenameTarget = null;
        _stackPopoverItemRenameNameText = null;
        _stackPopoverItemRenameOriginalVisibility = Visibility.Visible;
        _stackPopoverItemRenameCommitInProgress = false;
        _stackPopoverItemRenameCancelling = false;
    }
}
