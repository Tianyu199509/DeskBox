// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing widget item surface styling, brush cache, and pointer state logic.
/// </summary>
public sealed partial class WidgetWindow
{
    private enum ItemSurfaceState
    {
        Normal,
        Hover,
        Pressed,
        DropTarget
    }

    private SolidColorBrush? _normalItemSurfaceBrush;
    private SolidColorBrush? _selectedItemSurfaceBrush;
    private SolidColorBrush? _hoverItemSurfaceBrush;
    private SolidColorBrush? _pressedItemSurfaceBrush;
    private SolidColorBrush? _selectedHoverItemSurfaceBrush;
    private SolidColorBrush? _dropTargetItemSurfaceBrush;
    private SolidColorBrush? _normalItemBorderBrush;
    private SolidColorBrush? _dropTargetItemBorderBrush;
    private bool? _itemSurfaceBrushesAreDark;
    private Windows.UI.Color? _itemSurfaceBrushesAccentColor;
    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _settingsService.Settings.WidgetMaterialType;

        // Simplified layering: only apply surface color overlay for Solid mode.
        // For Acrylic/Mica/Transparent, BackgroundPlate is transparent to let the system backdrop show through.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var solidColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, solidColor);
        }
        else
        {
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, Colors.Transparent);
        }

        var (borderThickness, borderColor, dividerColor) = GetWidgetBorderVisuals(isDark, accentColor);

        var iconForeground = Windows.UI.Color.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        var editorBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x1A, 0x1C, 0x21)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.14 : 0.08,
            overlayMix: isDark ? 0.12 : 0.08);

        var editorBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);

        var secondaryText = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.BorderThickness = new Thickness(borderThickness);
        BackgroundPlate.BorderBrush = GetOrUpdateSolidColorBrush(BackgroundPlate.BorderBrush, borderColor);
        BackgroundPlate.CornerRadius = new CornerRadius(GetCurrentSurfaceCornerRadius());
        HeaderDivider.Background = GetOrUpdateSolidColorBrush(HeaderDivider.Background, dividerColor);
        FileTitleIcon.AccentColor = iconForeground;
        FileTitleIcon.Mode = _settingsService.Settings.WidgetTitleIconMode;
        FileWidgetShell.TitleIconAccentColor = iconForeground;
        FileWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        TitleEditBox.Background = GetOrUpdateSolidColorBrush(TitleEditBox.Background, editorBackground);
        TitleEditBox.BorderBrush = GetOrUpdateSolidColorBrush(TitleEditBox.BorderBrush, editorBorder);
        TitleEditBox.Foreground = GetOrUpdateSolidColorBrush(TitleEditBox.Foreground, isDark ? Colors.White : Colors.Black);
        EmptyStateTitleText.Foreground = GetOrUpdateSolidColorBrush(
            EmptyStateTitleText.Foreground,
            isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        EmptyStateDescriptionText.Foreground = GetOrUpdateSolidColorBrush(EmptyStateDescriptionText.Foreground, secondaryText);
        EmptyStateIcon.Foreground = GetOrUpdateSolidColorBrush(EmptyStateIcon.Foreground, secondaryText);
        SelectionRectangle.Background = GetOrUpdateSolidColorBrush(
            SelectionRectangle.Background,
            WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = GetOrUpdateSolidColorBrush(
            SelectionRectangle.BorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        StatusToastText.Foreground = GetOrUpdateSolidColorBrush(StatusToastText.Foreground, isDark ? Colors.White : Colors.Black);

        UpdateInteractiveSurfaces();
        UpdateStackSurfaces();

        // Keep the drag highlight border's corner radius in sync with the window's
        // corner preference so the breathing border matches the widget shape.
        DragHighlightBorder.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());
    }

    private void UpdateInteractiveSurfaces()
    {
        foreach (var border in _interactiveSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _interactiveSurfaces.Remove(border);
                continue;
            }

            ApplyWidgetItemLayout(border);
            ApplyWidgetItemTooltip(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void ApplyWidgetItemSurfaceState(Border border, ItemSurfaceState state)
    {
        if (ReferenceEquals(border, _folderDropTarget) && state != ItemSurfaceState.DropTarget)
        {
            state = ItemSurfaceState.DropTarget;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var item = border.DataContext as WidgetItem;
        bool isSelected = item?.IsSelected == true;
        bool isCut = item?.IsCut == true;

        EnsureItemSurfaceBrushCache(isDark, accentColor);

        border.Background = state switch
        {
            ItemSurfaceState.DropTarget => _dropTargetItemSurfaceBrush,
            ItemSurfaceState.Hover when isSelected => _selectedHoverItemSurfaceBrush,
            ItemSurfaceState.Pressed when isSelected => _selectedHoverItemSurfaceBrush,
            ItemSurfaceState.Hover => _hoverItemSurfaceBrush,
            ItemSurfaceState.Pressed => _pressedItemSurfaceBrush,
            _ when isSelected => _selectedItemSurfaceBrush,
            _ => _normalItemSurfaceBrush
        };
        border.BorderBrush = state == ItemSurfaceState.DropTarget
            ? _dropTargetItemBorderBrush
            : _normalItemBorderBrush;
        border.BorderThickness = state == ItemSurfaceState.DropTarget
            ? new Thickness(1)
            : new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void ResetItemSurfaceBrushCache()
    {
        _normalItemSurfaceBrush = null;
        _selectedItemSurfaceBrush = null;
        _hoverItemSurfaceBrush = null;
        _pressedItemSurfaceBrush = null;
        _selectedHoverItemSurfaceBrush = null;
        _dropTargetItemSurfaceBrush = null;
        _normalItemBorderBrush = null;
        _dropTargetItemBorderBrush = null;
        _itemSurfaceBrushesAreDark = null;
        _itemSurfaceBrushesAccentColor = null;
    }

    private void EnsureItemSurfaceBrushCache(bool isDark, Windows.UI.Color accentColor)
    {
        if (_normalItemSurfaceBrush is not null &&
            _itemSurfaceBrushesAreDark == isDark &&
            _itemSurfaceBrushesAccentColor is { } cachedAccentColor &&
            cachedAccentColor.Equals(accentColor))
        {
            return;
        }

        _itemSurfaceBrushesAreDark = isDark;
        _itemSurfaceBrushesAccentColor = accentColor;

        var defaultBackground = ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        var selectedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x31, 0x36, 0x3E)
                    : ColorHelper.FromArgb(0xFF, 0xF1, 0xF6, 0xFC),
                accentMix: isDark ? 0.30 : 0.18,
                overlayMix: isDark ? 0.08 : 0.04),
            isDark ? (byte)0x62 : (byte)0x72);

        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        var pressedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2D, 0x30, 0x37)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xFA),
                accentMix: isDark ? 0.24 : 0.15,
                overlayMix: isDark ? 0.10 : 0.16),
            isDark ? (byte)0x48 : (byte)0x54);

        var selectedHoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            isDark ? (byte)0x78 : (byte)0x88);

        var dropTargetBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x35, 0x3D, 0x48)
                    : ColorHelper.FromArgb(0xFF, 0xE7, 0xF3, 0xFF),
                accentMix: isDark ? 0.42 : 0.30,
                overlayMix: isDark ? 0.06 : 0.04),
            isDark ? (byte)0x92 : (byte)0x9C);

        _normalItemSurfaceBrush = GetOrUpdateSolidColorBrush(_normalItemSurfaceBrush, defaultBackground);
        _selectedItemSurfaceBrush = GetOrUpdateSolidColorBrush(_selectedItemSurfaceBrush, selectedBackground);
        _hoverItemSurfaceBrush = GetOrUpdateSolidColorBrush(_hoverItemSurfaceBrush, hoverBackground);
        _pressedItemSurfaceBrush = GetOrUpdateSolidColorBrush(_pressedItemSurfaceBrush, pressedBackground);
        _selectedHoverItemSurfaceBrush = GetOrUpdateSolidColorBrush(_selectedHoverItemSurfaceBrush, selectedHoverBackground);
        _dropTargetItemSurfaceBrush = GetOrUpdateSolidColorBrush(_dropTargetItemSurfaceBrush, dropTargetBackground);
        _normalItemBorderBrush = GetOrUpdateSolidColorBrush(_normalItemBorderBrush, Colors.Transparent);
        _dropTargetItemBorderBrush = GetOrUpdateSolidColorBrush(
            _dropTargetItemBorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xD8));
    }

    private void WidgetItemSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ResetStackElementVisual(border);
            _interactiveSurfaces.Add(border);
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemTooltip(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
            ResetStackElementVisual(border);
            PrepareStackMemberSurfaceForExpansion(border);
        }
    }

    private void WidgetItemSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ResetStackElementVisual(border);
            _interactiveSurfaces.Remove(border);
        }
    }

    private void WidgetItemSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy || _isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Hover);
        }
    }

    private void WidgetItemSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            return;
        }

        var point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            ClearOtherWidgetSelections();
            if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
            {
                if (!item.IsSelected)
                {
                    listView.SelectedItems.Add(item);
                }
            }
            else if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
            }

            ApplySelectionState(listView);
        }

        RootGrid.Focus(FocusState.Programmatic);
        _surfaceDragCompletionHandled = false;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
    }

    private void WidgetItemSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            bool isInside = e.GetCurrentPoint(border).Position is var point &&
                point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= border.ActualWidth &&
                point.Y <= border.ActualHeight;

            ApplyWidgetItemSurfaceState(border, isInside ? ItemSurfaceState.Hover : ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (_isMigrationBusy)
        {
            args.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            args.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        if (!TryPrepareItemDragPackage(args.Data, GetDragItems(item)))
        {
            args.Cancel = true;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private async void WidgetItemSurface_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_surfaceDragCompletionHandled)
        {
            return;
        }

        _surfaceDragCompletionHandled = true;
        await HandleItemDragCompletedAsync(args.DropResult);
    }
}
