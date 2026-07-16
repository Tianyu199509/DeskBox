using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void OnSettingsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
        });
    }

    private void ApplyWindowCornerPreference()
    {
        int cornerPreference = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    protected override Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var baseColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BuildAccentSurfaceColor(
            isDark,
            accentColor,
            baseColor,
            accentMix: isDark ? 0.08 : 0.16,
            overlayMix: isDark ? 0.04 : 0.08);
    }

    private static Windows.UI.Color BuildFrostedSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity)
    {
        double materialOpacity = isDark
            ? Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82)
            : Math.Clamp(surfaceOpacity * 0.78, 0.0, 0.78);

        return ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.18,
                overlayMix: isDark ? 0.15 : 0.04),
            materialOpacity);
    }

    private void ApplyWidgetItemTooltip(Border border)
    {
        if (!ViewModel.ShowFileItemPathTooltips || border.DataContext is not WidgetItem item)
        {
            ToolTipService.SetToolTip(border, null);
            return;
        }

        var tooltipText = new TextBlock
        {
            Text = item.FullPath
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipTextStyle", out object? textStyleResource)
            && textStyleResource is Style textStyle)
        {
            tooltipText.Style = textStyle;
        }

        var tooltipContent = new Border
        {
            Child = tooltipText
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipCardStyle", out object? cardStyleResource)
            && cardStyleResource is Style cardStyle)
        {
            tooltipContent.Style = cardStyle;
        }

        var tooltip = new ToolTip
        {
            Content = tooltipContent
        };

        if (Application.Current.Resources.TryGetValue("RoundedToolTipStyle", out object? tooltipStyleResource)
            && tooltipStyleResource is Style tooltipStyle)
        {
            tooltip.Style = tooltipStyle;
        }

        ToolTipService.SetToolTip(border, tooltip);
    }

    private void ApplyTitleBarLayout()
    {
        double listIcon = ViewModel.ListIconSize;
        double labelFont = ViewModel.ListLabelFontSize;
        var chromeMode = _chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? labelFont
            : labelFont * 1.27;

        var metrics = WidgetTitleBarMetricsCalculator.Create(
            listIcon * 0.54,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        FileWidgetShell.ChromeMode = chromeMode;
        FileWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        FileWidgetShell.TitleGlyph = ViewModel.IconGlyph;
        FileWidgetShell.TitleIconKind = ViewModel.TitleIconKind;
        FileWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        FileWidgetShell.TitleIconElement.LabelText = ViewModel.Name;
        FileWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        FileWidgetShell.TitleBarContent = chromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden
            ? null
            : TitleBarGrid;
        ApplyLegacyTitleActionButtonVisibility(chromeMode);
        ApplyLockActionIconState();

        FileTitleIcon.IconSize = metrics.TitleIconSize;
        FileTitleIcon.Glyph = ViewModel.IconGlyph;
        FileTitleIcon.IconKind = ViewModel.TitleIconKind;
        FileTitleIcon.LabelText = ViewModel.Name;
        FileTitleIcon.Mode = _settingsService.Settings.WidgetTitleIconMode;
        TitleText.FontSize = metrics.TitleTextSize;
        TitleEditBox.FontSize = Math.Max(metrics.TitleTextSize - 1, 11);

        ApplyTitleActionButtonConfiguration(chromeMode);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.PositionLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.SizeLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.AddActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CollapseWidgetButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.MoreActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.CloseActionButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            metrics);
        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.AddActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CollapseWidgetButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.MoreActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.CloseActionIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        FileWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
    }

    private void ApplyTitleActionButtonConfiguration(WidgetChromeMode chromeMode)
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
        bool showPositionLock = actions.Contains(SettingsService.WidgetHoverActionLockPosition);
        bool showSizeLock = actions.Contains(SettingsService.WidgetHoverActionLockSize);
        bool showAdd = actions.Contains(SettingsService.WidgetHoverActionAdd) &&
            ViewModel.TopAddButtonVisibility == Visibility.Visible;
        bool showMore = actions.Contains(SettingsService.WidgetHoverActionMore);
        bool showDelete = actions.Contains(SettingsService.WidgetHoverActionDelete);

        PositionLockButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        SizeLockButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = showAdd ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;

        FileWidgetShell.PositionLockActionButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.SizeLockActionButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.ShowAddButton = showAdd;
        FileWidgetShell.MoreActionButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.CloseActionButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            PositionLockButtonIcon,
            PositionLockButtonFilledIcon,
            ViewModel.IsPositionLocked,
            SizeLockButtonIcon,
            SizeLockButtonFilledIcon,
            ViewModel.IsSizeLocked);
        WidgetActionIconHelper.ApplyLockState(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            ViewModel.IsPositionLocked,
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            ViewModel.IsSizeLocked);
    }

    private void ApplyLegacyTitleActionButtonVisibility(WidgetChromeMode chromeMode)
    {
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Stop();

        bool showButtons = _settingsService.Settings.ShowHoverButtons &&
            chromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden);
        RightActionButtons.Opacity = showButtons ? 1 : 0;
        RightActionButtons.IsHitTestVisible = showButtons;
        RightButtonsTransform.X = showButtons ? 0 : 12;
    }

    private IEnumerable<Border> FindInteractiveSurfaceBorders(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "InteractiveSurface")
            {
                yield return border;
            }

            foreach (var nested in FindInteractiveSurfaceBorders(child))
            {
                yield return nested;
            }
        }
    }

    private void ApplyWidgetItemLayout(Border border)
    {
        if (IsWithinItemsListView(border))
        {
            border.Width = double.NaN;
            border.Height = double.NaN;
            border.HorizontalAlignment = HorizontalAlignment.Left;
            border.Margin = ViewModel.ListItemMargin;
            border.Padding = ViewModel.ListItemPadding;
            border.CornerRadius = GetItemSurfaceCornerRadius();

            if (border.Child is Grid listGrid)
            {
                listGrid.ColumnSpacing = 10;
                listGrid.HorizontalAlignment = HorizontalAlignment.Left;

                if (TryGetDescendant<Image>(listGrid, out var icon))
                {
                    icon.Width = ViewModel.ListIconSize;
                    icon.Height = ViewModel.ListIconSize;
                }

                if (TryGetDescendant<FontIcon>(listGrid, out var fallbackIcon))
                {
                    fallbackIcon.Width = ViewModel.ListIconSize;
                    fallbackIcon.Height = ViewModel.ListIconSize;
                    fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.ListIconSize * 0.72), 12, 20);
                }

                double textMaxWidth = GetListItemTextMaxWidth();

                if (TryGetDescendant<StackPanel>(listGrid, out var textHost, "ListItemTextHost"))
                {
                    textHost.MaxWidth = textMaxWidth;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var label))
                {
                    label.FontSize = ViewModel.ListLabelFontSize;
                    label.MaxWidth = textMaxWidth;
                    label.HorizontalAlignment = HorizontalAlignment.Left;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var nameText, "ListItemNameText"))
                {
                    nameText.FontSize = ViewModel.ListLabelFontSize;
                    nameText.MaxWidth = textMaxWidth;
                    nameText.HorizontalAlignment = HorizontalAlignment.Left;
                    nameText.Visibility = Visibility.Visible;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var detailText, "ListItemDetailText"))
                {
                    detailText.FontSize = Math.Max(ViewModel.ListLabelFontSize - 2, 9);
                    detailText.MaxWidth = textMaxWidth;
                    detailText.HorizontalAlignment = HorizontalAlignment.Left;
                    detailText.Visibility = ViewModel.ShowListItemDetails
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            return;
        }

        if (border.Parent is FrameworkElement slot && slot.Tag as string == "ItemSlot")
        {
            slot.Width = ViewModel.IconTileWidth;
            slot.Height = ViewModel.IconTileHeight;
            slot.Margin = ViewModel.IconTileMargin;
        }

        border.Width = double.NaN;
        border.Height = double.NaN;
        border.MaxWidth = Math.Max(ViewModel.IconImageSize + 18, ViewModel.IconLabelMaxWidth + 12);
        border.Margin = new Thickness(0);
        border.Padding = ViewModel.IconTilePadding;
        border.CornerRadius = GetItemSurfaceCornerRadius();

        if (border.Child is StackPanel iconStack)
        {
            iconStack.Spacing = ViewModel.IconContentSpacing;

            if (TryGetDescendant<Image>(iconStack, out var icon))
            {
                icon.Width = ViewModel.IconImageSize;
                icon.Height = ViewModel.IconImageSize;
            }

            if (TryGetDescendant<FontIcon>(iconStack, out var fallbackIcon))
            {
                fallbackIcon.Width = ViewModel.IconImageSize;
                fallbackIcon.Height = ViewModel.IconImageSize;
                fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.IconImageSize * 0.72), 16, 34);
            }

            if (TryGetDescendant<TextBlock>(iconStack, out var label, "IconItemNameText"))
            {
                label.FontSize = ViewModel.IconLabelFontSize;
                label.MaxWidth = ViewModel.IconLabelMaxWidth;
            }
        }
    }

    private double GetListItemTextMaxWidth()
    {
        double availableWidth = ItemsListView.ActualWidth > 0 ? ItemsListView.ActualWidth : ViewModel.Config.Width;
        double horizontalPadding = ViewModel.ListItemPadding.Left + ViewModel.ListItemPadding.Right;
        double reservedWidth = ViewModel.ListIconSize + 10 + horizontalPadding + 24;
        return Math.Max(56, availableWidth - reservedWidth);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result) where T : DependencyObject
    {
        return TryGetDescendant(parent, out result, null);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result, string? name) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild &&
                (string.IsNullOrWhiteSpace(name) ||
                 (typedChild is FrameworkElement frameworkElement &&
                  string.Equals(frameworkElement.Name, name, StringComparison.Ordinal))))
            {
                result = typedChild;
                return true;
            }

            if (TryGetDescendant(child, out result, name))
            {
                return true;
            }
        }

        result = null!;
        return false;
    }

    private CornerRadius GetItemSurfaceCornerRadius()
    {
        double radius = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 6,
            _ => 5
        };

        return new CornerRadius(radius);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private bool IsWithinItemsListView(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ItemsListView))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
