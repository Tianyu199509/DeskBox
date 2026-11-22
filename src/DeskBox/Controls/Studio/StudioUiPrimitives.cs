using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal static class StudioUiPrimitives
{
    internal static async Task SafeInvokeAsync(CustomWidgetStudioWindow shell, string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            shell.HostBridge.Log($"[CustomWidgetStudio] {label} failed: {ex}");
        }
    }

    internal static void ApplyBorderlessTextBoxResources(TextBox box)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
    }

    internal static UIElement CreateStepRow(CustomWidgetStudioWindow shell, string badge, string text)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush(shell, "AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = badge,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush(shell, "TextOnAccentFillColorPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var label = new TextBlock
        {
            Text = text,
            Style = TextStyle("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    internal static Border CreateSolidStopSquareIcon(CustomWidgetStudioWindow shell, double size = 10.5) =>
        new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(2),
            Background = ThemeBrush(shell, "SystemFillColorCriticalBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    internal static Windows.UI.Color ResolveAccentColor(CustomWidgetStudioWindow shell)
    {
        if (ThemeBrush(shell, "AccentFillColorDefaultBrush") is SolidColorBrush scb && scb.Color.A > 0)
        {
            return scb.Color;
        }

        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? cObj) &&
            cObj is Windows.UI.Color sysColor)
        {
            return sysColor;
        }

        return Windows.UI.Color.FromArgb(255, 56, 139, 253);
    }

    internal static Border CreateUserMessageBubbleChrome(
        CustomWidgetStudioWindow shell,
        UIElement child,
        double opacity = 1.0)
    {
        Windows.UI.Color accent = ResolveAccentColor(shell);
        bool isDark = GetStudioExplicitTheme(shell) == ElementTheme.Dark;
        byte fillAlpha = (byte)(isDark ? 46 : 28);
        byte strokeAlpha = (byte)(isDark ? 120 : 96);

        return new Border
        {
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(10, 10, 2, 10),
            Padding = new Thickness(12, 8, 12, 9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(strokeAlpha, accent.R, accent.G, accent.B)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(fillAlpha, accent.R, accent.G, accent.B)),
            Opacity = opacity,
            Child = child
        };
    }

    internal static Button CreateHighlightedClarifyChipButton(
        CustomWidgetStudioWindow shell,
        string text,
        string? glyph = "\uE945")
    {
        Windows.UI.Color accent = ResolveAccentColor(shell);
        bool isDark = GetStudioExplicitTheme(shell) == ElementTheme.Dark;
        Brush fg = ThemeBrush(shell, "AccentTextFillColorPrimaryBrush");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            row.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 11.5,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var chip = new Button
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 148 : 126), accent.R, accent.G, accent.B)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 38 : 24), accent.R, accent.G, accent.B)),
            Foreground = fg,
            Content = row
        };
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    internal static PathIcon CreateCapsuleViewportIcon()
    {
        static PathFigure CreateStadiumFigure(double leftCenterX, double rightCenterX, double centerY, double radius)
        {
            var fig = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(leftCenterX, centerY - radius),
                IsClosed = true,
                IsFilled = true
            };
            fig.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(rightCenterX, centerY - radius)
            });
            fig.Segments.Add(new ArcSegment
            {
                Point = new Windows.Foundation.Point(rightCenterX, centerY + radius),
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            fig.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(leftCenterX, centerY + radius)
            });
            fig.Segments.Add(new ArcSegment
            {
                Point = new Windows.Foundation.Point(leftCenterX, centerY - radius),
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            return fig;
        }

        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        // 13.6 x 7.6 horizontal capsule stadium anchored at (0, 0) so PathIcon centers cleanly
        geometry.Figures.Add(CreateStadiumFigure(3.8, 9.8, 3.8, 3.8));
        geometry.Figures.Add(CreateStadiumFigure(3.8, 9.8, 3.8, 2.55));
        // Left status indicator dot + short summary bar mirroring DeskBox's desktop capsule
        geometry.Figures.Add(CreateStadiumFigure(3.9, 3.9, 3.8, 1.05));
        geometry.Figures.Add(CreateStadiumFigure(6.55, 10.15, 3.8, 0.6));

        return new PathIcon
        {
            Data = geometry,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// 技术详情折叠块。异常原文只放这里，不再占据提示正文——
    /// 之前 6 处直接把 ex.Message 当正文，用户看到的是
    /// "HttpRequestException: 由于目标计算机积极拒绝" 这一类原文，既看不懂也不知道下一步做什么。
    /// </summary>
    internal static UIElement BuildFailureTechDetails(CustomWidgetStudioWindow shell, string detail)
    {
        return new Expander
        {
            Header = new TextBlock
            {
                Text = shell.Localization.T("CustomWidget.Workshop.Diag.TechDetailsHeader"),
                FontSize = 12
            },
            Content = new TextBlock
            {
                Text = detail,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5
            }
        };
    }

    internal static Button CreateIconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 14 }
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    internal static Button CreateChipButton(string text, string? glyph = null, IconElement? iconElement = null)
    {
        var chip = new Button
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,


            Content = iconElement is not null
                ? CreateSmallIconElementLabel(iconElement, text)
                : (string.IsNullOrWhiteSpace(glyph)
                    ? text
                    : CreateSmallIconLabel(glyph!, text))
        };
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    internal static Grid CreateSmallIconElementLabel(IconElement icon, string text)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    internal static Grid CreateSmallIconLabel(string glyph, string text)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    internal static Style? TextStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Style : null;

    internal static Style? AccentButtonStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? value) ? value as Style : null;

    internal static Brush StageThemeBrush(CustomWidgetStudioWindow shell, string key, ElementTheme stageTheme)
    {
        ElementTheme resolved = stageTheme == ElementTheme.Default ? GetStudioExplicitTheme(shell) : stageTheme;
        if (NeutralInteractionBrush.ResolveThemedResource(key, resolved) is Brush themed)
        {
            return themed;
        }

        return ThemeBrush(shell, key);
    }

    internal static ElementTheme GetStudioExplicitTheme(CustomWidgetStudioWindow shell) =>
        NeutralInteractionBrush.IsDarkTheme(shell.ShellRootGrid) ? ElementTheme.Dark : ElementTheme.Light;

    internal static Style BuildStudioThemedFlyoutPresenterStyle(CustomWidgetStudioWindow shell, ElementTheme explicitTheme)
    {
        var style = new Style(typeof(FlyoutPresenter));
        if (Application.Current.Resources.TryGetValue(typeof(FlyoutPresenter), out object? implicitObj) &&
            implicitObj is Style implicitStyle)
        {
            style.BasedOn = implicitStyle;
        }
        else if (Application.Current.Resources.TryGetValue("DefaultFlyoutPresenterStyle", out object? defaultObj) &&
                 defaultObj is Style defaultStyle)
        {
            style.BasedOn = defaultStyle;
        }

        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, explicitTheme));
        return style;
    }

    internal static void BindFlyoutToStudioTheme(CustomWidgetStudioWindow shell, Flyout flyout, FrameworkElement? contentRoot = null)
    {
        void SyncTheme()
        {
            ElementTheme explicitTheme = GetStudioExplicitTheme(shell);
            flyout.FlyoutPresenterStyle = BuildStudioThemedFlyoutPresenterStyle(shell, explicitTheme);
            if (contentRoot is not null)
            {
                contentRoot.RequestedTheme = explicitTheme;
            }
            else if (flyout.Content is FrameworkElement fe)
            {
                fe.RequestedTheme = explicitTheme;
            }
        }

        SyncTheme();
        flyout.Opening += (_, _) => SyncTheme();
    }

    internal static Brush ThemeBrush(CustomWidgetStudioWindow shell, string key)
    {
        if (NeutralInteractionBrush.ResolveThemedResource(key, shell.ShellRootGrid) is Brush themed)
        {
            return themed;
        }

        return Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush fallback
            ? fallback
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
