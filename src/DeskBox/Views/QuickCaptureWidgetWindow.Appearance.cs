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
    protected override void UpdateConfigBoundsFromPhysical(int x, int y, int width, int height, bool persist)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
        if (persist)
        {
            _settingsService.UpdateWidget(ViewModel.Config, notifySubscribers: false);
        }
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

    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _settingsService.Settings.WidgetMaterialType;

        // Simplified layering: only apply surface color overlay for Solid mode.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, surfaceColor);
        }
        else
        {
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, Colors.Transparent);
        }

        var (borderThickness, borderColor, dividerColor) = GetWidgetBorderVisuals(isDark, accentColor);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);
        var secondaryForeground = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.BorderThickness = new Thickness(borderThickness);
        BackgroundPlate.BorderBrush = GetOrUpdateSolidColorBrush(BackgroundPlate.BorderBrush, borderColor);
        BackgroundPlate.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());
        HeaderDivider.Background = GetOrUpdateSolidColorBrush(HeaderDivider.Background, dividerColor);
        QuickCaptureShell.TitleIconAccentColor = iconForeground;
        QuickCaptureShell.TitleIconKind = WidgetTitleIconKindNames.QuickCapture;
        QuickCaptureShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        EmptyStateIcon.Foreground = GetOrUpdateSolidColorBrush(EmptyStateIcon.Foreground, secondaryForeground);
        SelectionRectangle.Background = GetOrUpdateSolidColorBrush(
            SelectionRectangle.Background,
            WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = GetOrUpdateSolidColorBrush(
            SelectionRectangle.BorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        ApplySearchVisualStyle(isDark, accentColor);
        ApplyEditOverlayStyle(isDark, accentColor);
        RefreshSelectedViewSegment();
        RefreshItemMaterialSurfaces();
    }

    private void ApplyTabStyles()
    {
        RefreshSelectedViewSegment();
    }

    private void RefreshSelectedViewSegment()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        int selectedIndex = GetViewSegmentIndex(ViewModel.SelectedView);
        if (QuickCaptureViewSegmented.SelectedIndex != selectedIndex)
        {
            QuickCaptureViewSegmented.SelectedIndex = selectedIndex;
        }
    }

    private QuickCaptureViewMode GetSelectedSegmentView()
    {
        return QuickCaptureViewSegmented?.SelectedIndex switch
        {
            1 => QuickCaptureViewMode.Pinned,
            2 => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };
    }

    private static int GetViewSegmentIndex(QuickCaptureViewMode view)
    {
        return view switch
        {
            QuickCaptureViewMode.Pinned => 1,
            QuickCaptureViewMode.Recent => 2,
            _ => 0
        };
    }

    private void ApplyTitleBarLayout()
    {
        var chromeMode = _chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? ViewModel.TextSize
            : ViewModel.TitleTextSize;
        var metrics = WidgetTitleBarMetricsCalculator.Create(
            ViewModel.TitleIconSize,
            titleTextSize,
            includeInnerPadding: true,
            chromeMode);

        QuickCaptureShell.ChromeMode = chromeMode;
        QuickCaptureShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        TitleIcon.IconSize = metrics.TitleIconSize;
        TitleText.FontSize = metrics.TitleTextSize;
        ApplyTitleActionButtonConfiguration();
        ApplyLockActionIconState();

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetActionIconHelper.ApplyPairSize(PositionLockButtonIcon, PositionLockButtonFilledIcon, metrics);
        WidgetActionIconHelper.ApplyPairSize(SizeLockButtonIcon, SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        QuickCaptureShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
    }

    private void ApplyTitleActionButtonConfiguration()
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
        PositionLockButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockPosition)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SizeLockButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        QuickCaptureShell.ShowAddButton = actions.Contains(SettingsService.WidgetHoverActionAdd);
        MoreButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionMore)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloseButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionDelete)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            PositionLockButtonIcon,
            PositionLockButtonFilledIcon,
            ViewModel.Config.IsPositionLocked,
            SizeLockButtonIcon,
            SizeLockButtonFilledIcon,
            ViewModel.Config.IsSizeLocked);
    }

    private void ApplySearchVisualStyle(bool isDark, Windows.UI.Color accentColor)
    {
        var background = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark ? ColorHelper.FromArgb(0xFF, 0x24, 0x27, 0x2D) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: ViewModel.IsSearchExpanded ? (isDark ? 0.20 : 0.10) : 0.0,
            overlayMix: isDark ? 0.05 : 0.0);

        SearchTextBox.Background = GetOrUpdateSolidColorBrush(
            SearchTextBox.Background,
            WithAlpha(background, isDark ? (byte)0xE8 : (byte)0xF6));
        SearchTextBox.BorderBrush = GetOrUpdateSolidColorBrush(
            SearchTextBox.BorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xCC : (byte)0xAA));
    }

    private void ApplyEditOverlayStyle(bool isDark, Windows.UI.Color accentColor)
    {
        QuickCaptureInlineEditor.OverlaySurface.Background = GetOrUpdateSolidColorBrush(
            QuickCaptureInlineEditor.OverlaySurface.Background,
            GetNeutralOverlaySurfaceColor(isDark));
        QuickCaptureInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        QuickCaptureInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        QuickCaptureInlineEditor.Translation = new Vector3(0, 0, 16);

        EditTextBox.Background = GetOrUpdateSolidColorBrush(
            EditTextBox.Background,
            GetNeutralInputSurfaceColor(isDark));
        EditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        EditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);
    }

    private static Windows.UI.Color GetNeutralOverlaySurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x2A, 0x30, 0x38)
            : ColorHelper.FromArgb(0xFF, 0xFB, 0xFC, 0xFD);
    }

    private static Windows.UI.Color GetNeutralInputSurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x22, 0x28, 0x30)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private static Brush GetNeutralOverlayBorderBrush(bool isDark)
    {
        return GetBrushResourceOrFallback(
            "CardStrokeColorDefaultBrush",
            isDark ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    private void PlayItemsViewTransition()
    {
        if (!RootGrid.IsLoaded)
        {
            ResetItemsViewTransitionState();
            return;
        }

        // Skip the fade animation on the very first data load.
        // The Composition Visual may not be fully ready, and if the
        // animation fails the opacity stays at 0, making items invisible.
        if (!_hasPlayedInitialItemsTransition)
        {
            _hasPlayedInitialItemsTransition = true;
            ResetItemsViewTransitionState();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ItemsListView.UpdateLayout();
            EmptyStateHost.UpdateLayout();

            if (ItemsListView.Visibility == Visibility.Visible)
            {
                StartSubtleOffsetAnimation(ItemsListView, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(ItemsListView, 0, 1, ItemsViewTransitionMs);
                ScheduleTransitionSafetyFallback(ItemsListView);
            }

            if (EmptyStateHost.Visibility == Visibility.Visible)
            {
                StartSubtleOffsetAnimation(EmptyStateHost, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(EmptyStateHost, 0, 1, ItemsViewTransitionMs);
                ScheduleTransitionSafetyFallback(EmptyStateHost);
            }
        });
    }

    private void ScheduleTransitionSafetyFallback(UIElement element)
    {
        // Safety fallback: if the Composition animation fails to start or
        // complete (e.g., the Visual isn't fully composed yet), ensure the
        // element is still visible after the expected animation duration.
        DispatcherQueue.TryEnqueue(() =>
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ItemsViewTransitionMs + 80);
            timer.IsRepeating = false;
            timer.Tick += OnTransitionSafetyFallbackTick;

            void OnTransitionSafetyFallbackTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
            {
                sender.Stop();
                sender.Tick -= OnTransitionSafetyFallbackTick;
                if (element.Visibility == Visibility.Visible)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    if (visual.Opacity < 0.99f)
                    {
                        visual.StopAnimation("Opacity");
                        visual.Opacity = 1f;
                    }
                    element.Opacity = 1;
                }
            }
            timer.Start();
        });
    }

    private void ResetItemsViewTransitionState()
    {
        ResetTransitionState(ItemsListView);
        ResetTransitionState(EmptyStateHost);
    }

    private static void ResetTransitionState(UIElement element)
    {
        element.Opacity = 1;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
    }

    private static void StartOpacityAnimation(UIElement element, double from, double to, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.InsertKeyFrame(0.0f, (float)from);
        animation.InsertKeyFrame(1.0f, (float)to, easing);
        visual.Opacity = (float)to;
        visual.StartAnimation("Opacity", animation);
    }

    private static void StartSubtleOffsetAnimation(
        UIElement element,
        double fromX,
        double toX,
        double fromY,
        double toY,
        int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Offset");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(durationMs);
        offsetAnimation.InsertKeyFrame(0.0f, new Vector3((float)fromX, (float)fromY, 0));
        offsetAnimation.InsertKeyFrame(1.0f, new Vector3((float)toX, (float)toY, 0), easing);
        visual.Offset = new Vector3((float)toX, (float)toY, 0);
        visual.StartAnimation("Offset", offsetAnimation);
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

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild &&
                (string.IsNullOrEmpty(name) || string.Equals(typedChild.Name, name, StringComparison.Ordinal)))
            {
                return typedChild;
            }

            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static Button? FindPinnedButton(DependencyObject parent)
    {
        return FindVisualChild<Button>(parent, "PinItemButton");
    }
}
