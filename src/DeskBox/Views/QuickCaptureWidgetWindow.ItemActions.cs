using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void ItemsListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel? item = e.Items.OfType<QuickCaptureItemViewModel>().FirstOrDefault();
        if (item is null)
        {
            _draggedQuickCaptureItemId = null;
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragCanReorder = false;
            _quickCaptureTabDropHandled = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
            return;
        }

        bool canReorder = !item.IsRecent &&
                          ViewModel.SelectedView is QuickCaptureViewMode.Records or QuickCaptureViewMode.Pinned &&
                          !ViewModel.HasSearchText;
        _draggedQuickCaptureItemId = item.Id;
        _isInternalQuickCaptureDrag = true;
        _internalQuickCaptureDragCanReorder = canReorder;
        _quickCaptureTabDropHandled = false;
        _internalQuickCaptureDragView = canReorder ? ViewModel.SelectedView : null;
        ItemsListView.CanReorderItems = canReorder;

        try
        {
            if (!TryPrepareQuickCaptureDragPackage(e.Data, item))
            {
                _draggedQuickCaptureItemId = null;
                _isInternalQuickCaptureDrag = false;
                _internalQuickCaptureDragCanReorder = false;
                _internalQuickCaptureDragView = null;
                e.Cancel = true;
                return;
            }

            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to start drag: {ex}");
            _draggedQuickCaptureItemId = null;
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragCanReorder = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
        }
    }

    private async void ItemsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        string? itemId = _draggedQuickCaptureItemId;
        QuickCaptureViewMode? dragView = _internalQuickCaptureDragView;
        bool canReorder = _internalQuickCaptureDragCanReorder;
        bool tabDropHandled = _quickCaptureTabDropHandled;
        _draggedQuickCaptureItemId = null;
        _internalQuickCaptureDragView = null;
        _internalQuickCaptureDragCanReorder = false;
        _quickCaptureTabDropHandled = false;
        ItemsListView.CanReorderItems = true;
        DispatcherQueue.TryEnqueue(() => _isInternalQuickCaptureDrag = false);
        if (string.IsNullOrWhiteSpace(itemId) || tabDropHandled || !canReorder)
        {
            return;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is not null)
        {
            int targetIndex = ViewModel.Items.IndexOf(item);
            if (dragView == QuickCaptureViewMode.Pinned)
            {
                await ViewModel.MovePinnedItemToIndexAsync(item, targetIndex);
            }
            else
            {
                await ViewModel.MoveItemAsync(item, targetIndex);
            }
        }
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            string.IsNullOrWhiteSpace(_draggedQuickCaptureItemId) ||
            sender is not FrameworkElement { Tag: string tag } ||
            ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, _draggedQuickCaptureItemId, StringComparison.Ordinal)) is not { } item ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(item, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = target == QuickCaptureViewMode.Pinned
            ? _localizationService.T("QuickCapture.DropTab.Pin")
            : _localizationService.T("QuickCapture.DropTab.Records");
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            string.IsNullOrWhiteSpace(_draggedQuickCaptureItemId) ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, _draggedQuickCaptureItemId, StringComparison.Ordinal)) is not { } item ||
            !ViewModel.CanApplyTabDrop(item, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        _quickCaptureTabDropHandled = true;
        var deferral = e.GetDeferral();
        try
        {
            bool changed = await ViewModel.ApplyTabDropAsync(item, target);
            e.AcceptedOperation = changed ? DataPackageOperation.Move : DataPackageOperation.None;
            if (changed)
            {
                SelectView(target);
                ShowStatusToast(_localizationService.T(target == QuickCaptureViewMode.Pinned
                    ? "QuickCapture.DropTab.Pinned"
                    : "QuickCapture.DropTab.Saved"));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryGetQuickCaptureTabTarget(string tag, out QuickCaptureViewMode target)
    {
        target = tag switch
        {
            "Pinned" => QuickCaptureViewMode.Pinned,
            "Records" => QuickCaptureViewMode.Records,
            _ => QuickCaptureViewMode.Recent
        };
        return target != QuickCaptureViewMode.Recent;
    }

    private static void ApplyItemMaterialSurface(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<Border>(itemRoot, "ItemMaterialBackground") is not { } surface)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        QuickCaptureAppearancePreset preset = item.IsRecent
            ? QuickCaptureAppearancePreset.Default
            : item.AppearancePreset;
        surface.Background = GetOrUpdateSolidColorBrush(surface.Background, GetMaterialColor(preset, isDark));
        surface.BorderBrush = preset == QuickCaptureAppearancePreset.Default
            ? GetMaterialBorderBrush(preset, isDark)
            : GetOrUpdateSolidColorBrush(
                surface.BorderBrush,
                isDark
                    ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private static Brush GetMaterialBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return new SolidColorBrush(GetMaterialColor(preset, isDark));
    }

    private static Windows.UI.Color GetMaterialColor(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return (preset, isDark) switch
        {
            (QuickCaptureAppearancePreset.Paper, true) => ColorHelper.FromArgb(0xB8, 0x3A, 0x36, 0x30),
            (QuickCaptureAppearancePreset.Paper, false) => ColorHelper.FromArgb(0xEC, 0xFA, 0xF5, 0xEA),
            (QuickCaptureAppearancePreset.StickyYellow, true) => ColorHelper.FromArgb(0xB8, 0x4A, 0x40, 0x25),
            (QuickCaptureAppearancePreset.StickyYellow, false) => ColorHelper.FromArgb(0xEC, 0xFF, 0xF0, 0xB3),
            (QuickCaptureAppearancePreset.Rose, true) => ColorHelper.FromArgb(0xB8, 0x47, 0x2E, 0x38),
            (QuickCaptureAppearancePreset.Rose, false) => ColorHelper.FromArgb(0xEC, 0xFC, 0xE3, 0xEA),
            (QuickCaptureAppearancePreset.Mint, true) => ColorHelper.FromArgb(0xB8, 0x28, 0x42, 0x35),
            (QuickCaptureAppearancePreset.Mint, false) => ColorHelper.FromArgb(0xEC, 0xDD, 0xF3, 0xE3),
            (QuickCaptureAppearancePreset.MistBlue, true) => ColorHelper.FromArgb(0xB8, 0x2B, 0x3D, 0x53),
            (QuickCaptureAppearancePreset.MistBlue, false) => ColorHelper.FromArgb(0xEC, 0xDF, 0xEC, 0xF8),
            _ => Colors.Transparent
        };
    }

    private static Brush GetMaterialBorderBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        if (preset == QuickCaptureAppearancePreset.Default)
        {
            return GetBrushResourceOrFallback(
                "CardStrokeColorDefaultBrush",
                isDark
                    ? ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }

        return new SolidColorBrush(isDark
            ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private void RefreshItemMaterialSurfaces()
    {
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
            {
                ApplyItemMaterialSurface(container, item);
            }
        }

        if (DetailPage.Visibility == Visibility.Visible)
        {
            ApplyDetailMaterialSurface();
        }
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, true);
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, false);
        SetItemHoverState(sender as DependencyObject, false);
    }

    private void QuickCaptureItem_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag || !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsGlyphVisible = true;
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_DragLeave(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetItemHoverState(sender as DependencyObject, false);
        }
    }

    private async void QuickCaptureItem_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            sender is not FrameworkElement { DataContext: QuickCaptureItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        SetItemHoverState(sender as DependencyObject, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(item, batch.Files);
            e.AcceptedOperation = updated is null
                ? DataPackageOperation.None
                : DataPackageOperation.Copy;
            if (updated is not null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to attach dropped files: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void SetItemActionButtonsVisible(DependencyObject? itemRoot, bool isVisible)
    {
        if (itemRoot is null ||
            FindVisualChild<Border>(itemRoot, "ItemActionHost") is not { } actions)
        {
            return;
        }

        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
        ApplyActionButtonHostTheme(actions, itemRoot);
        ElementCompositionPreview.GetElementVisual(actions).StopAnimation("Offset");
    }

    private static void ApplyActionButtonHostTheme(Border actions, DependencyObject itemRoot)
    {
        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        actions.Background = new SolidColorBrush(WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x1E, 0x23, 0x29) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.08,
                overlayMix: isDark ? 0.03 : 0.02),
            0xFF));
        actions.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x4A : (byte)0x30));
        actions.BorderThickness = new Thickness(1);

        foreach (var button in FindVisualChildren<Button>(actions))
        {
            ApplyActionButtonTheme(button, isDark, accentColor);
        }
    }

    private static void ApplyActionButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var foreground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF2 : (byte)0xE2));

        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private static void SetItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "ItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "ImagePreviewBorder") is { } imageBorder)
        {
            imageBorder.BorderBrush = isHovered
                ? new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xE0 : (byte)0xCC))
                : GetBrushResourceOrFallback(
                    "CardStrokeColorDefaultBrush",
                    isDark
                        ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                        : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
    }

    private void SetItemActionButtonsVisibleForItem(object? item, bool isVisible)
    {
        if (item is null)
        {
            return;
        }

        if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
        {
            SetItemActionButtonsVisible(container, isVisible);
            return;
        }

        if (isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ItemsListView.ContainerFromItem(item) is DependencyObject queuedContainer)
                {
                    SetItemActionButtonsVisible(queuedContainer, true);
                }
            });
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            if (item.IsRecent)
            {
                await ViewModel.PinRecentItemAsync(item);
            }
            else
            {
                await ViewModel.TogglePinnedAsync(item);
            }
        }
    }

    private async void SaveRecentItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.SaveRecentItemAsync(item);
        }
    }

    private async void MovePinnedItemUpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, -1);
        }
    }

    private async void MovePinnedItemDownButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, 1);
        }
    }

    private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.Tag is QuickCaptureItemViewModel item)
        {
            ShowQuickCaptureDeleteConfirmFlyout(item, element);
        }
    }

    private async Task DeleteItemWithUndoAsync(QuickCaptureItemViewModel item)
    {
        var snapshot = await ViewModel.DeleteItemAsync(item);
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = snapshot;
        ShowStatusToast(
            _localizationService.T("QuickCapture.Deleted"),
            _localizationService.T("Common.Undo"),
            StatusToastUndoMs);
    }

    private void ShowQuickCaptureDeleteConfirmFlyout(QuickCaptureItemViewModel item, FrameworkElement anchor)
    {
        ShowConfirmMenu(
            anchor,
            _localizationService.T("QuickCapture.DeleteConfirm.Title"),
            _localizationService.T("Common.Delete"),
            async () => await DeleteItemWithUndoAsync(item));
    }

    private void ShowQuickCaptureDeleteSelectedConfirmFlyout(
        IReadOnlyList<string> selectedIds,
        bool isRecent,
        FrameworkElement anchor)
    {
        if (selectedIds.Count == 0)
        {
            return;
        }

        ShowConfirmMenu(
            anchor,
            _localizationService.Format("QuickCapture.DeleteSelectedConfirm.Title", selectedIds.Count),
            _localizationService.T("Common.Delete"),
            async () =>
            {
                ClearQuickCaptureCopySelection();
                var deletedItems = await ViewModel.DeleteItemsAsync(selectedIds, isRecent);
                if (deletedItems.Count > 0)
                {
                    ShowStatusToast(_localizationService.Format("QuickCapture.DeletedCount", deletedItems.Count));
                }
            });
    }

    private string GetDeleteConfirmPreviewText(QuickCaptureItemViewModel item)
    {
        string text = item.Type == QuickCaptureItemType.Image
            ? _localizationService.T("QuickCapture.ImageItem")
            : item.DisplayText;
        text = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = _localizationService.T("QuickCapture.Name");
        }

        return text.Length <= 34 ? text : $"{text[..34].Trim()}...";
    }

    private bool TryPrepareQuickCaptureDragPackage(DataPackage dataPackage, QuickCaptureItemViewModel item)
    {
        dataPackage.RequestedOperation = DataPackageOperation.Copy;
        var selectedItems = GetSelectedQuickCaptureItemsInVisibleOrder();
        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            string text = FormatQuickCaptureBatch(selectedItems);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            DeskBoxDragData.SetText(dataPackage, text, DeskBoxDragData.SourceQuickCapture);
            dataPackage.Properties.Title = _localizationService.Format("QuickCapture.CopiedCount", selectedItems.Count);
            return true;
        }

        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            string imagePath = item.ImagePath;
            Uri? imageUri = TryCreateImageUri(imagePath);
            if (imageUri is not null)
            {
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(imageUri));
            }

            dataPackage.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    request.SetData(new List<IStorageItem> { file });
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCaptureWidget] Failed to provide dragged image: {ex}");
                }
                finally
                {
                    deferral.Complete();
                }
            });
            if (!string.IsNullOrWhiteSpace(item.Body) &&
                !string.Equals(item.Body, "Image", StringComparison.Ordinal))
            {
                DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            }

            dataPackage.Properties.Title = Path.GetFileName(imagePath);
            return true;
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            dataPackage.SetWebLink(uri);
            dataPackage.SetUri(uri);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        return false;
    }
}
