using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace DeskBox.Helpers;

/// <summary>
/// Shared Fluent 2 emoji picker flyout and inline quick-grid used across Settings,
/// Studio Start composer, and Studio Preview header quick-edit.
/// </summary>
internal static class EmojiPickerFlyoutHelper
{
    internal static readonly string[] FeaturedWidgetEmojis =
    [
        "📊", "🍅", "🤖", "⏳", "📝", "🎯", "📋",
        "💧", "🚀", "💡", "✅", "📅", "📌", "🗂️",
        "💻", "⚡", "🔥", "🔔", "🛠️", "💰", "📈",
        "📎", "🧠", "🏆", "🎨", "🌐", "⏱️", "✨"
    ];

    private static readonly string[] CategoryFluentGlyphs =
    [
        "\uE76E", // Smileys
        "\uE8E1", // People
        "\uE706", // Animals & Nature
        "\uEC32", // Food & Drink
        "\uE709", // Travel & Places
        "\uE770", // Objects & Tech
        "\uE945"  // Symbols
    ];

    internal static Flyout BuildEmojiPickerFlyout(
        Func<ElementTheme> resolveTheme,
        Func<string, string> translate,
        string? currentEmoji,
        bool allowAutoMode,
        Func<string?, Task> onPickedAsync,
        Func<string?>? resolveLiveEmoji = null,
        FlyoutPlacementMode placement = FlyoutPlacementMode.BottomEdgeAlignedLeft)
    {
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue(typeof(FlyoutPresenter), out object? implicitStyleObj) &&
            implicitStyleObj is Style implicitStyle)
        {
            presenterStyle.BasedOn = implicitStyle;
        }
        else if (Application.Current?.Resources is { } defRes &&
                 defRes.TryGetValue("DefaultFlyoutPresenterStyle", out object? defaultStyleObj) &&
                 defaultStyleObj is Style defaultStyle)
        {
            presenterStyle.BasedOn = defaultStyle;
        }
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 316d));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 324d));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));

        var flyout = new Flyout
        {
            Placement = placement,
            FlyoutPresenterStyle = presenterStyle
        };

        var host = new ContentControl
        {
            Width = 292,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        flyout.Opening += async (_, _) =>
        {
            try
            {
                ElementTheme theme = resolveTheme();
                host.RequestedTheme = theme;
                string? active = resolveLiveEmoji is not null ? resolveLiveEmoji() : currentEmoji;
                IReadOnlyList<string> recent = [];
                try
                {
                    recent = await WidgetTitleIconAssetStore.Current.LoadRecentEmojisAsync();
                }
                catch
                {
                }

                host.Content = BuildEmojiPaletteContent(
                    theme,
                    translate,
                    currentEmoji: active,
                    recentEmojis: recent,
                    allowAutoMode: allowAutoMode,
                    onPickedAsync: async picked =>
                    {
                        flyout.Hide();
                        if (!string.IsNullOrWhiteSpace(picked))
                        {
                            _ = WidgetTitleIconAssetStore.Current.AddRecentEmojiAsync(picked.Trim());
                        }
                        await onPickedAsync(picked);
                    });
            }
            catch (Exception ex)
            {
                App.Log($"[EmojiPicker] Opening flyout failed: {ex.Message}");
            }
        };

        flyout.Content = host;
        return flyout;
    }

    internal static FrameworkElement BuildCompactQuickEmojiStrip(
        ElementTheme theme,
        string? currentEmoji,
        Action<string> onPicked,
        int maxCount = 14)
    {
        var wrap = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        const int cols = 7;
        for (int c = 0; c < cols; c++)
        {
            wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        string[] picks = FeaturedWidgetEmojis.Take(Math.Max(cols, maxCount)).ToArray();
        int rows = (picks.Length + cols - 1) / cols;
        for (int r = 0; r < rows; r++)
        {
            wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Brush subtleFill = ResolveBrush(theme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x1C, 0x80, 0x80, 0x80));
        Brush strokeBrush = ResolveBrush(theme, "CardStrokeColorDefaultBrush", Color.FromArgb(0x38, 0x80, 0x80, 0x80));

        for (int i = 0; i < picks.Length; i++)
        {
            string emoji = picks[i];
            bool isCurrent = !string.IsNullOrWhiteSpace(currentEmoji) &&
                             string.Equals(emoji, currentEmoji.Trim(), StringComparison.Ordinal);
            var tb = new TextBlock
            {
                Text = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            WidgetTextShadow.SetCast(tb, false);

            var btn = new Button
            {
                Height = 32,
                MinHeight = 32,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = isCurrent ? subtleFill : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(isCurrent ? 1 : 0),
                BorderBrush = isCurrent ? strokeBrush : null,
                Content = tb
            };
            AutomationProperties.SetName(btn, emoji);
            btn.Click += (_, _) => onPicked(emoji);
            Grid.SetRow(btn, i / cols);
            Grid.SetColumn(btn, i % cols);
            wrap.Children.Add(btn);
        }

        return wrap;
    }

    internal static async Task SyncHostWidgetManifestIconAsync(string widgetId, string updatedManifestJson)
    {
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(updatedManifestJson))
        {
            return;
        }

        CustomWidgetManifest? parsedManifest = CustomWidgetManifestParser.Parse(updatedManifestJson).Manifest;
        if (parsedManifest is null)
        {
            return;
        }

        string? icon = CustomWidgetTemplates.IsEmojiIcon(parsedManifest.Icon)
            ? parsedManifest.Icon.Trim()
            : null;

        if (DeskBoxAgentBridgeServer.TryGetActiveWidgetManager() is { } localManager)
        {
            _ = await localManager.UpdateCustomWidgetIconOnlyAsync(widgetId, icon);
            return;
        }

        _ = await DeskBoxAgentBridgeClient.TrySendAsync(writer =>
        {
            writer.WriteString("op", "sync_icon");
            writer.WriteString("widgetId", widgetId);
            if (icon is not null)
            {
                writer.WriteString("icon", icon);
            }
            else
            {
                writer.WriteNull("icon");
            }
            writer.WriteString("manifestJson", updatedManifestJson);
        });
    }

    private static FrameworkElement BuildEmojiPaletteContent(
        ElementTheme theme,
        Func<string, string> translate,
        string? currentEmoji,
        IReadOnlyList<string> recentEmojis,
        bool allowAutoMode,
        Func<string?, Task> onPickedAsync)
    {
        var stack = new StackPanel { Spacing = 10 };
        string[] featuredMerged = recentEmojis
            .Concat(FeaturedWidgetEmojis)
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var categories = new List<(string Glyph, string Label, string[] Emojis)>(
            1 + WidgetTitleIconEmojiCatalog.Categories.Length)
        {
            ("\uE734", translate("CustomWidget.Native.IconCategoryFeatured"), featuredMerged)
        };

        for (int i = 0; i < WidgetTitleIconEmojiCatalog.Categories.Length; i++)
        {
            WidgetEmojiCategory cat = WidgetTitleIconEmojiCatalog.Categories[i];
            string glyph = i < CategoryFluentGlyphs.Length ? CategoryFluentGlyphs[i] : "\uE734";
            categories.Add((glyph, translate("Widget.CustomIcon.Category." + cat.Id), cat.Emojis));
        }

        var headerGrid = new Grid { ColumnSpacing = 10 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var previewEmojiTb = new TextBlock
        {
            Text = CustomWidgetTemplates.IsEmojiIcon(currentEmoji) ? currentEmoji!.Trim() : "✨",
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetTextShadow.SetCast(previewEmojiTb, false);

        var previewTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = ResolveBrush(theme, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ResolveBrush(theme, "CardStrokeColorDefaultBrush", Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = previewEmojiTb
        };
        headerGrid.Children.Add(previewTile);

        var headerTextStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        headerTextStack.Children.Add(new TextBlock
        {
            Text = translate("Widget.CustomIcon.Title"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        headerTextStack.Children.Add(new TextBlock
        {
            Text = translate("CustomWidget.Native.IconPicker.Subtitle"),
            FontSize = 11,
            Foreground = ResolveBrush(theme, "TextFillColorSecondaryBrush", Color.FromArgb(0xB0, 0x80, 0x80, 0x80)),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(headerTextStack, 1);
        headerGrid.Children.Add(headerTextStack);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (allowAutoMode)
        {
            var autoBtn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                Content = translate("Widget.CustomIcon.Reset")
            };
            ToolTipService.SetToolTip(autoBtn, translate("CustomWidget.Native.IconAutoReset"));
            autoBtn.Click += async (_, _) => await onPickedAsync(null);
            headerActions.Children.Add(autoBtn);
        }

        var randomBtnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        randomBtnPanel.Children.Add(new FontIcon { Glyph = "\uE8B1", FontSize = 11 });
        randomBtnPanel.Children.Add(new TextBlock
        {
            Text = translate("Widget.CustomIcon.Random"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        var randomBtn = new Button
        {
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            Content = randomBtnPanel
        };
        randomBtn.Click += async (_, _) =>
        {
            string[] all = FeaturedWidgetEmojis
                .Concat(WidgetTitleIconEmojiCatalog.GetAllEmojis())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (all.Length > 0)
            {
                await onPickedAsync(all[Random.Shared.Next(all.Length)]);
            }
        };
        headerActions.Children.Add(randomBtn);

        Grid.SetColumn(headerActions, 2);
        headerGrid.Children.Add(headerActions);
        stack.Children.Add(headerGrid);

        var categoryRow = new Grid { ColumnSpacing = 2 };
        for (int c = 0; c < categories.Count; c++)
        {
            categoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        const int GridColumns = 7;
        var emojiMatrix = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int col = 0; col < GridColumns; col++)
        {
            emojiMatrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var emojiScroller = new ScrollViewer
        {
            Height = 196,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = emojiMatrix
        };

        var categorySlots = new List<(Button Btn, FontIcon Icon, Border Pill)>(categories.Count);
        Brush subtleTabFill = ResolveBrush(theme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x1C, 0x80, 0x80, 0x80));
        Brush primaryFg = ResolveBrush(theme, "TextFillColorPrimaryBrush", Colors.White);
        Brush secondaryFg = ResolveBrush(theme, "TextFillColorSecondaryBrush", Color.FromArgb(0xB0, 0x80, 0x80, 0x80));
        Brush accentBrush = ResolveBrush(theme, "AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        Brush cardStroke = ResolveBrush(theme, "CardStrokeColorDefaultBrush", Color.FromArgb(0x38, 0x80, 0x80, 0x80));
        Style? subtleStyle = Application.Current?.Resources is { } appRes &&
                             appRes.TryGetValue("SubtleButtonStyle", out object? sObj) &&
                             sObj is Style st
            ? st
            : null;

        void RenderCategory(int categoryIdx)
        {
            for (int i = 0; i < categorySlots.Count; i++)
            {
                bool active = i == categoryIdx;
                categorySlots[i].Btn.Background = active ? subtleTabFill : new SolidColorBrush(Colors.Transparent);
                categorySlots[i].Icon.Foreground = active ? primaryFg : secondaryFg;
                categorySlots[i].Pill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            emojiScroller.ChangeView(null, 0, null, disableAnimation: true);
            emojiMatrix.Children.Clear();
            emojiMatrix.RowDefinitions.Clear();

            string[] items = categories[categoryIdx].Emojis;
            int rows = (items.Length + GridColumns - 1) / GridColumns;
            for (int r = 0; r < rows; r++)
            {
                emojiMatrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int idx = 0; idx < items.Length; idx++)
            {
                string emoji = items[idx];
                bool isCurrent = !string.IsNullOrWhiteSpace(currentEmoji) &&
                                 string.Equals(emoji, currentEmoji.Trim(), StringComparison.Ordinal);

                var tb = new TextBlock
                {
                    Text = emoji,
                    FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                WidgetTextShadow.SetCast(tb, false);

                var cellBtn = new Button
                {
                    Style = !isCurrent ? subtleStyle : null,
                    Height = 36,
                    MinHeight = 36,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Background = isCurrent ? subtleTabFill : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(isCurrent ? 1 : 0),
                    BorderBrush = isCurrent ? cardStroke : null,
                    Content = tb
                };
                AutomationProperties.SetName(cellBtn, emoji);
                cellBtn.PointerEntered += (_, _) => previewEmojiTb.Text = emoji;
                cellBtn.PointerExited += (_, _) =>
                {
                    previewEmojiTb.Text = CustomWidgetTemplates.IsEmojiIcon(currentEmoji)
                        ? currentEmoji!.Trim()
                        : "✨";
                };
                cellBtn.Click += async (_, _) => await onPickedAsync(emoji);
                Grid.SetRow(cellBtn, idx / GridColumns);
                Grid.SetColumn(cellBtn, idx % GridColumns);
                emojiMatrix.Children.Add(cellBtn);
            }
        }

        for (int i = 0; i < categories.Count; i++)
        {
            int captured = i;
            var icon = new FontIcon
            {
                Glyph = categories[i].Glyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var pill = new Border
            {
                Width = 14,
                Height = 2.5,
                CornerRadius = new CornerRadius(1.25),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                Background = accentBrush,
                Visibility = Visibility.Collapsed
            };
            var tabGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            tabGrid.Children.Add(icon);
            tabGrid.Children.Add(pill);

            var catBtn = new Button
            {
                Style = subtleStyle,
                Height = 32,
                MinHeight = 32,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Content = tabGrid
            };
            ToolTipService.SetToolTip(catBtn, categories[i].Label);
            AutomationProperties.SetName(catBtn, categories[i].Label);
            catBtn.Click += (_, _) => RenderCategory(captured);
            Grid.SetColumn(catBtn, i);
            categorySlots.Add((catBtn, icon, pill));
            categoryRow.Children.Add(catBtn);
        }

        stack.Children.Add(categoryRow);
        stack.Children.Add(emojiScroller);

        string placeholder = translate("CustomWidget.Native.IconPicker.InputPlaceholder");
        var customEmojiBox = new TextBox
        {
            MaxLength = 32,
            PlaceholderText = placeholder,
            FontSize = 12,
            CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(customEmojiBox, placeholder);

        bool committingTyped = false;
        async Task TryCommitTypedEmojiAsync()
        {
            if (committingTyped || ImeCompositionGuard.IsActivelyComposing(customEmojiBox))
            {
                return;
            }

            string candidate = customEmojiBox.Text.Trim();
            if (CustomWidgetTemplates.IsEmojiIcon(candidate))
            {
                committingTyped = true;
                await onPickedAsync(candidate);
            }
        }

        customEmojiBox.TextChanged += async (_, _) => await TryCommitTypedEmojiAsync();
        customEmojiBox.TextCompositionEnded += async (_, _) => await TryCommitTypedEmojiAsync();
        customEmojiBox.KeyDown += async (_, e) =>
        {
            if (ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await TryCommitTypedEmojiAsync();
            }
        };
        stack.Children.Add(customEmojiBox);

        RenderCategory(0);
        return stack;
    }

    private static Brush ResolveBrush(ElementTheme theme, string key, Color fallback) =>
        NeutralInteractionBrush.ResolveThemedResource(key, theme) ?? new SolidColorBrush(fallback);
}
