// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing title-rename and item-rename logic for WidgetWindow.
/// </summary>
public sealed partial class WidgetWindow
{
    private WidgetItem? _itemRenameTarget;
    private TextBlock? _itemRenameNameText;
    private bool _isCommittingTitleRename;
    private bool _isCommittingItemRename;
    private bool _isCancellingTitleRename;
    private bool _isCancellingItemRename;

    // ── Title rename ───────────────────────────────────────────

    private void StartRename()
    {
        _isCancellingTitleRename = false;
        BeginInteractionLayer("file-title-rename-opened", elevate: false);
        PrepareRenameEditor();
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        FocusTextInputEditor(TitleEditBox, selectAll: true);
    }

    private void PrepareRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 36
            : (ViewModel.Name.Length * 9.5) + 36;

        TitleEditBox.Width = Math.Clamp(titleWidth, 120, 220);
        TitleEditBox.Text = ViewModel.Name;
    }

    private async Task CommitRenameAsync()
    {
        if (_isCommittingTitleRename ||
            TitleEditBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = TitleEditBox.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await ViewModel.RenameAsync(newName);
            }

            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            ReleaseInteractionLayer("file-title-rename-committed");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.RenameFailed"), ex.Message);
            TitleEditBox.Focus(FocusState.Programmatic);
            TitleEditBox.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelRename()
    {
        _isCancellingTitleRename = true;
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        ReleaseInteractionLayer("file-title-rename-canceled");
    }

    private async void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitRenameAsync();
    }

    private async void TitleEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    // ── Item rename ────────────────────────────────────────────

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        var nameElement = FindItemNameElement(item);
        var target = nameElement ?? FindItemSurface(item);
        var contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            return;
        }

        SelectSingleItem(item);

        _itemRenameTarget = item;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = item.Name;

        // Match the TextBox style to the TextBlock it replaces for seamless inline look
        if (nameElement is TextBlock tb)
        {
            _itemRenameNameText = tb;
            tb.Visibility = Visibility.Collapsed;
            // TextBlock.FontSize == 0 means "use default" — don't pass 0 to TextBox
            ItemRenameTextBox.FontSize = tb.FontSize > 0 ? tb.FontSize : 14;
            ItemRenameTextBox.TextAlignment = tb.TextAlignment;
            ItemRenameTextBox.HorizontalContentAlignment = tb.HorizontalAlignment switch
            {
                HorizontalAlignment.Center => HorizontalAlignment.Center,
                HorizontalAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
            ItemRenameTextBox.TextWrapping = tb.TextWrapping;
        }
        else
        {
            ItemRenameTextBox.FontSize = ViewModel.IsListMode
                ? ViewModel.ListLabelFontSize
                : ViewModel.IconLabelFontSize;
            ItemRenameTextBox.TextAlignment = TextAlignment.Center;
            ItemRenameTextBox.TextWrapping = TextWrapping.NoWrap;
        }

        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        BeginInteractionLayer("file-item-rename-opened");
        // Ensure window is foreground then select filename without extension
        HoldTemporaryTopMost();
        _appWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(_hWnd);
        SelectFilenameWithoutExtension(ItemRenameTextBox);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Selects the filename portion without the extension, matching Windows Explorer behavior.
    /// For folders or files without extension, selects all.
    /// </summary>
    private static void SelectFilenameWithoutExtension(TextBox textBox)
    {
        textBox.Focus(FocusState.Programmatic);
        string text = textBox.Text;
        int dotIndex = text.LastIndexOf('.');
        // Only treat as extension if dot is not at position 0 (hidden files like .gitignore)
        // and the extension is reasonably short (≤ 8 chars) to avoid selecting ".tar.gz" style names entirely
        if (dotIndex > 0 && text.Length - dotIndex - 1 <= 8)
        {
            textBox.Select(0, dotIndex);
        }
        else
        {
            textBox.SelectAll();
        }
    }

    private void FocusTextInputEditor(TextBox textBox, bool selectAll)
    {
        HoldTemporaryTopMost();
        _appWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(_hWnd);
        FocusTextInputEditorCore(textBox, selectAll);

        DispatcherQueue.TryEnqueue(() =>
        {
            base.Activate();
            Win32Helper.SetForegroundWindow(_hWnd);
            FocusTextInputEditorCore(textBox, selectAll);
        });
    }

    private static void FocusTextInputEditorCore(TextBox textBox, bool selectAll)
    {
        textBox.Focus(FocusState.Programmatic);
        if (selectAll)
        {
            textBox.SelectAll();
        }
    }

    private async void ItemRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (_stackRenameTarget is not null)
            {
                await CommitStackRenameAsync();
            }
            else
            {
                await CommitItemRenameAsync();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_stackRenameTarget is not null)
            {
                CancelStackRename();
            }
            else
            {
                CancelItemRename();
            }
        }
    }

    private async void ItemRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingItemRename)
        {
            _isCancellingItemRename = false;
            return;
        }

        if (_stackRenameTarget is not null)
        {
            await CommitStackRenameAsync();
            return;
        }

        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename ||
            _itemRenameTarget is null ||
            ItemRenameTextBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = ItemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelItemRename();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            CompleteItemRename();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.RenameFailed"), ex.Message);
            ItemRenameTextBox.Focus(FocusState.Programmatic);
            ItemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private void CancelItemRename()
    {
        _isCancellingItemRename = true;
        CompleteItemRename();
    }

    private void CompleteItemRename()
    {
        ItemRenameTextBox.Visibility = Visibility.Collapsed;
        ItemRenameTextBox.IsHitTestVisible = false;
        ItemRenameTextBox.Text = string.Empty;

        // Restore the hidden TextBlock
        if (_itemRenameNameText is not null)
        {
            _itemRenameNameText.Visibility = Visibility.Visible;
            _itemRenameNameText = null;
        }

        _itemRenameTarget = null;
        ReleaseInteractionLayer("file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(FrameworkElement target, UIElement contentHost)
    {
        var topLeft = target.TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));

        // TextBox has 1px border + 2px horizontal padding. We need to offset
        // so the *text inside* the TextBox aligns exactly with the TextBlock text.
        const double border = 1.0;
        const double padX = 2.0;
        double offsetX = topLeft.X - border - padX;
        double offsetY = topLeft.Y - border;

        // The TextBox is a child of the contentHost Grid, which has Padding.
        // TransformToVisual gives coordinates relative to the Grid's visual bounds,
        // but the child is laid out starting from the content area (after padding).
        // We must subtract the host's padding to get the correct Margin.
        double hostPaddingH = 0;
        double hostPaddingV = 0;
        if (contentHost is Grid grid)
        {
            hostPaddingH = grid.Padding.Left + grid.Padding.Right;
            hostPaddingV = grid.Padding.Top + grid.Padding.Bottom;
            offsetX -= grid.Padding.Left;
            offsetY -= grid.Padding.Top;
        }

        double height = Math.Max(target.ActualHeight + 2 * border, 20);

        // Width: adapt to the host's content area. The usable width is
        // host.ActualWidth - hostPadding - |offsetX| - rightMargin.
        // offsetX is relative to content area, so right edge = host.ActualWidth - hostPaddingH + offsetX.
        const double rightMargin = 8;
        double width;
        if (contentHost is FrameworkElement host)
        {
            double contentWidth = host.ActualWidth - hostPaddingH;
            // offsetX is the TextBox's left position relative to the content area.
            // Available width = content area width - TextBox left position - right margin.
            double availableWidth = contentWidth - offsetX - rightMargin;
            if (ViewModel.IsListMode)
            {
                width = Math.Clamp(availableWidth, 80, contentWidth);
            }
            else
            {
                width = Math.Clamp(target.ActualWidth + 2 * (border + padX), 60, availableWidth);
            }

            double contentHeight = host.ActualHeight - hostPaddingV;
            height = Math.Min(height, Math.Max(20, contentHeight - offsetY - 4));
        }
        else
        {
            width = Math.Max(target.ActualWidth + 2 * (border + padX), 60);
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin = new Thickness(offsetX, offsetY, 0, 0);
    }

    private FrameworkElement? FindItemNameElement(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (ViewModel.IsIconMode &&
            TryGetDescendant<TextBlock>(container, out var iconNameText, "IconItemNameText"))
        {
            return iconNameText;
        }

        if (ViewModel.IsListMode &&
            TryGetDescendant<TextBlock>(container, out var listNameText, "ListItemNameText"))
        {
            return listNameText;
        }

        return null;
    }
}
