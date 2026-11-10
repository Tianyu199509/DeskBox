using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private FrameworkElement BuildCountdownHero(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isStandaloneHero = false)
    {
        CustomWidgetCountdownSnapshot snap = ctx.GetOrComputeCountdownSnapshot();
        string cultureName = ctx.CultureName ?? _viewModel.ActiveCultureName;

        CustomWidgetCountdownEventView? hero = snap.HeroEvent;
        if (hero is null)
        {
            return BuildCountdownEmptyHeroCard(isStandaloneHero, cultureName);
        }

        if (isStandaloneHero)
        {
            return BuildCountdownStandaloneMonumentHero(snap, hero, cultureName);
        }

        bool listModuleEnabled = IsCountdownListModuleEnabled(ctx);
        return BuildCountdownEditorialHero(hero, showQuickAddButton: !listModuleEnabled, cultureName);
    }

    private FrameworkElement BuildCountdownEmptyHeroCard(bool isStandaloneHero, string cultureName)
    {
        var stack = new StackPanel
        {
            MaxWidth = 220,
            Spacing = 6,
            Margin = isStandaloneHero ? new Thickness(12, 16, 12, 16) : new Thickness(8, 12, 8, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new FontIcon
        {
            Glyph = "\uE787",
            FontSize = 22,
            Foreground = ResolveForegroundBrush("muted"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.EmptyHeroTitle", cultureName),
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.EmptyHeroSubtitle", cultureName),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var addBtnContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        addBtnContent.Children.Add(new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        addBtnContent.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.AddEventButton", cultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var addBtn = new Button
        {
            Height = 30,
            MinHeight = 30,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = addBtnContent
        };
        StyleStandardCommandButton(addBtn, CornerMedium);
        addBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(addBtn, existingEvent: null);
        stack.Children.Add(addBtn);

        return stack;
    }

    private FrameworkElement BuildCountdownStandaloneMonumentHero(
        CustomWidgetCountdownSnapshot snap,
        CustomWidgetCountdownEventView hero,
        string cultureName)
    {
        Color accentColor = ResolveCountdownSemanticColor(hero);
        Brush accentBrush = GetOrCreateSolidBrush(accentColor);
        Brush transparentBrush = GetOrCreateSolidBrush(Colors.Transparent);

        var stageGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(4, 2, 4, 4)
        };

        Button quickAddBtn = CreateSubtleIconButton(
            glyph: "\uE710",
            size: 26,
            iconSize: 12,
            tooltip: Tr("CustomWidget.Renderer.Countdown.NewEventTooltip", cultureName));
        quickAddBtn.HorizontalAlignment = HorizontalAlignment.Right;
        quickAddBtn.VerticalAlignment = VerticalAlignment.Top;
        quickAddBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(quickAddBtn, existingEvent: null);

        var centerStack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Row 0: Quiet Fluent switcher ("[📅 春节假期 · 还有 ▾]")
        var pillRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillRow.Children.Add(CreateCountdownInlineIcon(hero.Emoji, 13, ResolveForegroundBrush("secondary")));

        var pillTitleTb = new TextBlock
        {
            Text = hero.Title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 156,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        pillRow.Children.Add(pillTitleTb);

        bool highlightStatus = hero.Direction == CountdownDirection.Today || hero.IsUrgent || hero.IsApproaching;
        var pillStatusTb = new TextBlock
        {
            Text = hero.StatusWord,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = highlightStatus ? accentBrush : ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        pillRow.Children.Add(pillStatusTb);

        if (snap.AllEvents.Count > 1)
        {
            pillRow.Children.Add(new FontIcon
            {
                Glyph = "\uE972",
                FontSize = 8.5,
                Foreground = ResolveForegroundBrush("muted"),
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        var headerPillBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = CornerMedium,
            BorderThickness = new Thickness(0),
            BorderBrush = transparentBrush,
            Background = transparentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = pillRow
        };
        ApplyButtonStateOverrides(headerPillBtn, SubtleHoverBrush, SubtlePressedBrush, transparentBrush, transparentBrush);

        if (snap.AllEvents.Count > 1)
        {
            var switchMenu = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShouldConstrainToRootBounds = false
            };
            switchMenu.Opening += (_, _) =>
            {
                switchMenu.Items.Clear();
                string menuCulture = _viewModel.ActiveCultureName;
                foreach (CustomWidgetCountdownEventView item in snap.AllEvents)
                {
                    bool isCurrent = string.Equals(item.Id, hero.Id, StringComparison.Ordinal);
                    string prefix = CustomWidgetTemplates.IsEmojiIcon(item.Emoji) ? $"{item.Emoji}  " : string.Empty;
                    var menuItem = new ToggleMenuFlyoutItem
                    {
                        Text = TrFormat(
                            "CustomWidget.Renderer.Countdown.SwitchMenuItemFormat",
                            menuCulture,
                            $"{prefix}{item.Title}",
                            item.BadgeText),
                        IsChecked = isCurrent
                    };
                    string targetId = item.Id;
                    menuItem.Click += async (_, _) =>
                    {
                        if (!isCurrent)
                        {
                            await _viewModel.PinCountdownEventAsync(targetId);
                        }
                    };
                    switchMenu.Items.Add(menuItem);
                }

                switchMenu.Items.Add(new MenuFlyoutSeparator());
                var editCur = new MenuFlyoutItem
                {
                    Text = Tr("CustomWidget.Renderer.Countdown.EditCurrentEventMenu", menuCulture),
                    Icon = new FontIcon { Glyph = "\uE70F" }
                };
                editCur.Click += (_, _) =>
                {
                    switchMenu.Hide();
                    UiDispatch.PostAfterCallback(() => ShowCountdownEventEditorFlyout(headerPillBtn, hero));
                };
                switchMenu.Items.Add(editCur);
            };
            TrackInteractiveFlyout(switchMenu);
            headerPillBtn.Flyout = switchMenu;
            ToolTipService.SetToolTip(headerPillBtn, Tr("CustomWidget.Renderer.Countdown.SwitchPinnedTooltip", cultureName));
        }
        else
        {
            ToolTipService.SetToolTip(headerPillBtn, Tr("CustomWidget.Renderer.Countdown.ClickToEditTooltip", cultureName));
            headerPillBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(headerPillBtn, hero);
        }
        centerStack.Children.Add(headerPillBtn);

        // Row 1: Display Numeral + Click-to-Edit Surface
        var bodyBtn = new Button
        {
            Padding = new Thickness(12, 6, 12, 8),
            CornerRadius = CornerMedium,
            BorderThickness = new Thickness(0),
            BorderBrush = transparentBrush,
            Background = transparentBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ApplyButtonStateOverrides(bodyBtn, SubtleHoverBrush, SubtlePressedBrush, transparentBrush, transparentBrush);
        ToolTipService.SetToolTip(bodyBtn, Tr("CustomWidget.Renderer.Countdown.ClickToEditThisTooltip", cultureName));
        bodyBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(bodyBtn, hero);

        var bodyStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (hero.Direction == CountdownDirection.Today)
        {
            bodyStack.Children.Add(new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Countdown.ItsToday", cultureName),
                FontFamily = DisplayFontFamily,
                FontSize = 32,
                FontWeight = FontWeights.SemiBold,
                Foreground = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            string digits = hero.DisplayDays.ToString(CultureInfo.InvariantCulture);
            double digitSize = digits.Length >= 4 ? 38 : 48;

            var numRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var numTb = new TextBlock
            {
                Text = digits,
                FontFamily = DisplayFontFamily,
                FontSize = digitSize,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = -16,
                TextLineBounds = TextLineBounds.Tight,
                Foreground = ResolveForegroundBrush("default"),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            SetTabularNumerals(numTb);
            numRow.Children.Add(numTb);

            numRow.Children.Add(new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Countdown.DaysUnit", cultureName),
                FontSize = BodyFontSize,
                FontWeight = FontWeights.Medium,
                Foreground = ResolveForegroundBrush("secondary"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(5, 0, 0, 2)
            });
            bodyStack.Children.Add(numRow);
        }

        TextBlock subTb = CreateCountdownHeroSubtitle(hero, centerAlign: true);
        bodyStack.Children.Add(subTb);

        bodyStack.Children.Add(BuildCountdownLinearBar(hero.ProgressPercent, accentBrush, fixedWidth: 164, height: 3.0));
        bodyBtn.Content = bodyStack;
        centerStack.Children.Add(bodyBtn);

        stageGrid.Children.Add(centerStack);
        stageGrid.Children.Add(quickAddBtn);
        AttachCountdownItemContextMenu(stageGrid, hero, includePin: false, cultureName);
        return stageGrid;
    }

    /// <summary>
    /// Hero subtitle with the v3.0 milestone hint as a separate run: while inside the
    /// urgentDays window the hint warms up to the shared amber "approaching" semantic
    /// (same palette as ResolveCountdownSemanticColor's tier, no new colors).
    /// </summary>
    private TextBlock CreateCountdownHeroSubtitle(
        CustomWidgetCountdownEventView hero,
        bool centerAlign)
    {
        var tb = new TextBlock
        {
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (centerAlign)
        {
            tb.HorizontalAlignment = HorizontalAlignment.Center;
        }

        if (string.IsNullOrEmpty(hero.MilestoneText))
        {
            tb.Text = hero.HeroSubtitle;
            SetTabularNumerals(tb);
            return tb;
        }

        tb.Inlines.Add(new Run { Text = hero.HeroBaseSubtitle });
        tb.Inlines.Add(new Run
        {
            Text = $" · {hero.MilestoneText}",
            FontWeight = hero.IsMilestoneImminent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = hero.IsMilestoneImminent
                ? ResolveSemanticBrush("warning")
                : ResolveForegroundBrush("muted")
        });
        SetTabularNumerals(tb);
        return tb;
    }

    private FrameworkElement BuildCountdownEditorialHero(
        CustomWidgetCountdownEventView hero,
        bool showQuickAddButton,
        string cultureName)
    {
        Color accentColor = ResolveCountdownSemanticColor(hero);
        Brush accentBrush = GetOrCreateSolidBrush(accentColor);
        Brush transparentBrush = GetOrCreateSolidBrush(Colors.Transparent);

        // Open continuous editorial hero directly on the window backdrop (zero nested card box)
        var heroSurface = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(4, 2, 4, 6),
            Background = transparentBrush,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var outerStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var topGrid = new Grid
        {
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left Column: Category Glyph Badge + Event Title + Status Word + Date/Lunar Subtitle
        var leftStack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLine.Children.Add(CreateCountdownIconBadge(hero.Emoji, badgeSize: 22, glyphSize: 12));

        var titleTb = new TextBlock
        {
            Text = hero.Title,
            FontSize = SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 144,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLine.Children.Add(titleTb);

        bool highlightHeroStatus = hero.Direction == CountdownDirection.Today || hero.IsUrgent || hero.IsApproaching;
        var statusDotTb = new TextBlock
        {
            Text = $"· {hero.StatusWord}",
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = highlightHeroStatus ? accentBrush : ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLine.Children.Add(statusDotTb);
        leftStack.Children.Add(titleLine);

        TextBlock subtitleTb = CreateCountdownHeroSubtitle(hero, centerAlign: false);
        leftStack.Children.Add(subtitleTb);

        Grid.SetColumn(leftStack, 0);
        topGrid.Children.Add(leftStack);

        // Right Column: Co-axial Display Countdown Number + optional '+' when list is off
        var rightHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (hero.Direction == CountdownDirection.Today)
        {
            (Brush badgeBg, Brush badgeFg) = ResolveBadgeColors("success");
            rightHost.Children.Add(new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(8, 3, 8, 3),
                Background = badgeBg,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Tr("CustomWidget.Countdown.BadgeToday", cultureName),
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = badgeFg,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        else
        {
            string digits = hero.DisplayDays.ToString(CultureInfo.InvariantCulture);
            double fontSize = digits.Length >= 4 ? 28 : 36;

            var numPair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var numTb = new TextBlock
            {
                Text = digits,
                FontFamily = DisplayFontFamily,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = -14,
                TextLineBounds = TextLineBounds.Tight,
                Foreground = ResolveForegroundBrush("default"),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            SetTabularNumerals(numTb);
            numPair.Children.Add(numTb);

            numPair.Children.Add(new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Countdown.DaysUnit", cultureName),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.Medium,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 0, 2)
            });
            rightHost.Children.Add(numPair);
        }

        if (showQuickAddButton)
        {
            Button addIconBtn = CreateSubtleIconButton(
                glyph: "\uE710",
                size: 26,
                iconSize: 12,
                tooltip: Tr("CustomWidget.Renderer.Countdown.NewEventTooltip", cultureName));
            addIconBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(addIconBtn, existingEvent: null);
            rightHost.Children.Add(addIconBtn);
        }

        Grid.SetColumn(rightHost, 1);
        topGrid.Children.Add(rightHost);
        outerStack.Children.Add(topGrid);

        // Sleek 3px progress bar anchored under the editorial hero
        outerStack.Children.Add(BuildCountdownLinearBar(hero.ProgressPercent, accentBrush, fixedWidth: null, height: 3.0));

        heroSurface.Child = outerStack;
        heroSurface.PointerEntered += (_, _) => heroSurface.Background = SubtleHoverBrush;
        heroSurface.PointerExited += (_, _) => heroSurface.Background = transparentBrush;
        heroSurface.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject dep && IsInsideChildButton(dep, heroSurface))
            {
                return;
            }
            e.Handled = true;
            ShowCountdownEventEditorFlyout(heroSurface, hero);
        };
        ToolTipService.SetToolTip(heroSurface, Tr("CustomWidget.Renderer.Countdown.HeroSurfaceTooltip", cultureName));
        AttachCountdownItemContextMenu(heroSurface, hero, includePin: false, cultureName);

        return heroSurface;
    }

    private FrameworkElement BuildCountdownList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetCountdownSnapshot snap = ctx.GetOrComputeCountdownSnapshot();

        bool heroModuleEnabled = IsCountdownHeroModuleEnabled(ctx);
        IReadOnlyList<CustomWidgetCountdownEventView> items = heroModuleEnabled
            ? snap.SecondaryEvents
            : snap.AllEvents;

        var rootStack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var headerGrid = new Grid
        {
            Padding = new Thickness(2, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sectionTitleTb = new TextBlock
        {
            Text = heroModuleEnabled
                ? Tr("CustomWidget.Renderer.Countdown.UpcomingEvents", ctx.CultureName)
                : Tr("CustomWidget.Renderer.Countdown.AllEvents", ctx.CultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(sectionTitleTb, 0);
        headerGrid.Children.Add(sectionTitleTb);

        Button addBtn = CreateSubtleIconButton(
            glyph: "\uE710",
            size: 26,
            iconSize: 12,
            tooltip: Tr("CustomWidget.Renderer.Countdown.AddCountdownOrAnniversaryTooltip", ctx.CultureName));
        addBtn.Margin = new Thickness(0, 0, 14, 0);
        addBtn.Click += (_, _) => ShowCountdownEventEditorFlyout(addBtn, existingEvent: null);
        Grid.SetColumn(addBtn, 1);
        headerGrid.Children.Add(addBtn);

        rootStack.Children.Add(headerGrid);

        // v3.0: built-in "next statutory holiday" row (shared static schedule, zero network).
        if (snap.NextHoliday is { } nextHoliday)
        {
            rootStack.Children.Add(BuildCountdownHolidayRow(nextHoliday, ctx.CultureName ?? _viewModel.ActiveCultureName));
        }

        if (items.Count == 0)
        {
            rootStack.Children.Add(new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(12, 10, 12, 10),
                Background = ResolveCardBackground("subtle"),
                Child = new TextBlock
                {
                    Text = snap.AllEvents.Count > 0
                        ? Tr("CustomWidget.Renderer.Countdown.ListEmptyMoreHint", ctx.CultureName)
                        : Tr("CustomWidget.Renderer.Countdown.ListEmptyFirstHint", ctx.CultureName),
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted"),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
        }
        else
        {
            foreach (CustomWidgetCountdownEventView item in items)
            {
                rootStack.Children.Add(BuildCountdownListRow(item, allowPinAction: heroModuleEnabled, ctx.CultureName ?? _viewModel.ActiveCultureName));
            }
        }

        if (!heroModuleEnabled)
        {
            return rootStack;
        }

        return new Border
        {
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Child = rootStack
        };
    }

    private FrameworkElement BuildCountdownListRow(
        CustomWidgetCountdownEventView item,
        bool allowPinAction,
        string cultureName)
    {
        Brush rowRestBg = ResolveCardBackground("default");
        Brush rowHoverBg = SubtleHoverBrush;

        var rowBorder = new Border
        {
            CornerRadius = CornerSmall,
            MinHeight = 42,
            Padding = new Thickness(10, 6, 10, 6),
            Background = rowRestBg,
            BorderThickness = new Thickness(0),
            Opacity = item.IsArchivedExpired ? 0.58 : 1.0
        };

        var grid = new Grid
        {
            ColumnSpacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement iconBadge = CreateCountdownIconBadge(item.Emoji, badgeSize: 26, glyphSize: 13);
        Grid.SetColumn(iconBadge, 0);
        grid.Children.Add(iconBadge);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var subTb = new TextBlock
        {
            Text = item.ListSubtitle,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(subTb);
        textStack.Children.Add(subTb);

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        Button? pinBtn = null;
        if (allowPinAction)
        {
            pinBtn = CreateSubtleIconButton(
                glyph: "\uE718",
                size: 26,
                iconSize: 11.5,
                tooltip: Tr("CustomWidget.Renderer.Countdown.PinToHeroTooltip", cultureName));
            pinBtn.Opacity = 0;
            string pinId = item.Id;
            pinBtn.Click += async (_, _) => await _viewModel.PinCountdownEventAsync(pinId);
            Grid.SetColumn(pinBtn, 2);
            grid.Children.Add(pinBtn);
        }

        // Right Column: Co-axial Crisp Tabular Day Count (quiet neutral unless urgent/today)
        FrameworkElement rightBadge;
        if (item.Direction == CountdownDirection.Today)
        {
            (Brush badgeBg, Brush badgeFg) = ResolveBadgeColors("success");
            rightBadge = new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(7, 2, 7, 2),
                Background = badgeBg,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Tr("CustomWidget.Countdown.BadgeToday", cultureName),
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = badgeFg,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
        else
        {
            var countRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (item.Direction == CountdownDirection.CountUp && !item.IsArchivedExpired)
            {
                string prefixWord = Tr("CustomWidget.Renderer.Countdown.RowElapsedPrefix", cultureName);
                if (!string.IsNullOrEmpty(prefixWord))
                {
                    countRow.Children.Add(new TextBlock
                    {
                        Text = prefixWord,
                        FontSize = CaptionFontSize,
                        Foreground = ResolveForegroundBrush("muted"),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 3, 1)
                    });
                }
            }

            Brush dayFg = item.IsUrgent && !item.IsArchivedExpired
                ? ResolveSemanticBrush("coral")
                : item.IsApproaching && !item.IsArchivedExpired
                    ? ResolveSemanticBrush("warning")
                    : ResolveForegroundBrush("default");

            var numTb = new TextBlock
            {
                Text = item.DisplayDays.ToString(CultureInfo.InvariantCulture),
                FontFamily = DisplayFontFamily,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = -8,
                TextLineBounds = TextLineBounds.Tight,
                Foreground = dayFg,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            SetTabularNumerals(numTb);
            countRow.Children.Add(numTb);

            string suffixWord = item.Direction == CountdownDirection.CountUp && item.IsArchivedExpired
                ? Tr("CustomWidget.Renderer.Countdown.RowPastSuffix", cultureName)
                : Tr("CustomWidget.Renderer.Countdown.DaysUnitShort", cultureName);
            countRow.Children.Add(new TextBlock
            {
                Text = suffixWord,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2.5, 0, 0, 1)
            });

            rightBadge = countRow;
        }

        Grid.SetColumn(rightBadge, 3);
        grid.Children.Add(rightBadge);

        rowBorder.Child = grid;
        rowBorder.PointerEntered += (_, _) =>
        {
            rowBorder.Background = rowHoverBg;
            if (pinBtn is not null)
            {
                pinBtn.Opacity = 0.9;
            }
        };
        rowBorder.PointerExited += (_, _) =>
        {
            rowBorder.Background = rowRestBg;
            if (pinBtn is not null)
            {
                pinBtn.Opacity = 0;
            }
        };
        rowBorder.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject dep && IsInsideChildButton(dep, rowBorder))
            {
                return;
            }
            e.Handled = true;
            ShowCountdownEventEditorFlyout(rowBorder, item);
        };
        AttachCountdownItemContextMenu(rowBorder, item, includePin: allowPinAction, cultureName);

        return rowBorder;
    }

    /// <summary>Built-in next statutory holiday countdown row (v3.0). Read-only: the data lives in the shared static schedule.</summary>
    private FrameworkElement BuildCountdownHolidayRow(
        CustomWidgetCountdownHolidayView holiday,
        string cultureName)
    {
        var rowBorder = new Border
        {
            CornerRadius = CornerSmall,
            MinHeight = 42,
            Padding = new Thickness(10, 6, 10, 6),
            Background = ResolveCardBackground("default"),
            BorderThickness = new Thickness(0)
        };

        var grid = new Grid
        {
            ColumnSpacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement iconBadge = CreateCountdownIconBadge("\uE734", badgeSize: 26, glyphSize: 13);
        Grid.SetColumn(iconBadge, 0);
        grid.Children.Add(iconBadge);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(new TextBlock
        {
            Text = holiday.Title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var subTb = new TextBlock
        {
            Text = holiday.Subtitle,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(subTb);
        textStack.Children.Add(subTb);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        FrameworkElement rightBadge;
        if (holiday.IsInProgress)
        {
            (Brush badgeBg, Brush badgeFg) = ResolveBadgeColors("success");
            rightBadge = new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(7, 2, 7, 2),
                Background = badgeBg,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Tr("CustomWidget.Countdown.HolidayInProgressBadge", cultureName),
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = badgeFg,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
        else
        {
            var countRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            string prefixWord = Tr("CustomWidget.Countdown.HolidayDaysLeftPrefix", cultureName);
            if (!string.IsNullOrEmpty(prefixWord))
            {
                countRow.Children.Add(new TextBlock
                {
                    Text = prefixWord,
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted"),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 3, 1)
                });
            }

            Brush dayFg = holiday.DaysRemaining <= 3
                ? ResolveSemanticBrush("warning")
                : ResolveForegroundBrush("default");

            var numTb = new TextBlock
            {
                Text = holiday.DaysRemaining.ToString(CultureInfo.InvariantCulture),
                FontFamily = DisplayFontFamily,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = -8,
                TextLineBounds = TextLineBounds.Tight,
                Foreground = dayFg,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            SetTabularNumerals(numTb);
            countRow.Children.Add(numTb);

            countRow.Children.Add(new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Countdown.DaysUnitShort", cultureName),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2.5, 0, 0, 1)
            });

            rightBadge = countRow;
        }

        Grid.SetColumn(rightBadge, 2);
        grid.Children.Add(rightBadge);

        rowBorder.Child = grid;
        rowBorder.PointerEntered += (_, _) => rowBorder.Background = SubtleHoverBrush;
        rowBorder.PointerExited += (_, _) => rowBorder.Background = ResolveCardBackground("default");
        ToolTipService.SetToolTip(
            rowBorder,
            Tr("CustomWidget.Renderer.Countdown.HolidayRowTooltip", cultureName));

        return rowBorder;
    }

    private FrameworkElement BuildTimeProgressCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        DateTimeOffset nowLocal = (ctx.ClockOverride ?? DateTimeOffset.Now).ToLocalTime();
        DateOnly today = DateOnly.FromDateTime(nowLocal.DateTime);
        CustomWidgetTimeProgressSnapshot tp = CustomWidgetCountdownEngine.ComputeTimeProgress(today, ctx.CultureName);

        string scope = ctx.Settings.TryGetValue("progressScope", out string? ps) && !string.IsNullOrWhiteSpace(ps)
            ? ps.Trim().ToLowerInvariant()
            : "workandyear";

        bool hasAboveModules = IsCountdownHeroModuleEnabled(ctx) || IsCountdownListModuleEnabled(ctx);

        var stack = new StackPanel
        {
            Spacing = 12
        };

        Color yearColor = IsDark
            ? Color.FromArgb(255, 96, 165, 250)
            : Color.FromArgb(255, 37, 99, 235);
        Color monthColor = IsDark
            ? Color.FromArgb(255, 52, 211, 153)
            : Color.FromArgb(255, 16, 185, 129);
        Color weekColor = IsDark
            ? Color.FromArgb(255, 34, 211, 238)
            : Color.FromArgb(255, 6, 182, 212);

        // v3.0 "今日工时" live bar (workAndYear / allFour scopes).
        if (scope is "workandyear" or "allfour")
        {
            string workHoursSpec = ctx.Settings.TryGetValue("workHours", out string? wh) && !string.IsNullOrWhiteSpace(wh)
                ? wh.Trim()
                : CustomWidgetCountdownEngine.DefaultWorkHours;
            CustomWidgetWorkdayProgressSnapshot work =
                CustomWidgetCountdownEngine.ComputeWorkdayProgress(nowLocal.DateTime, workHoursSpec, ctx.CultureName);
            stack.Children.Add(BuildWorkdayProgressSection(work));
        }

        if (scope is "year" or "both" or "workandyear" or "allfour")
        {
            int totalDots = 36;
            int activeDotIndex = Math.Clamp((int)Math.Ceiling(tp.YearPercent / 100.0 * totalDots) - 1, 0, totalDots - 1);
            stack.Children.Add(BuildDotMatrixSection(
                title: tp.YearTitle,
                summary: tp.YearSummary,
                totalDots: totalDots,
                columns: 18,
                activeIndex: activeDotIndex,
                accentColor: yearColor));
        }

        if (scope is "month" or "both")
        {
            stack.Children.Add(BuildDotMatrixSection(
                title: tp.MonthTitle,
                summary: tp.MonthSummary,
                totalDots: tp.DaysInMonth,
                columns: 16,
                activeIndex: tp.DayOfMonth - 1,
                accentColor: monthColor));
        }

        if (scope is "allfour")
        {
            stack.Children.Add(BuildDotMatrixSection(
                title: tp.WeekTitle,
                summary: tp.WeekSummary,
                totalDots: 7,
                columns: 7,
                activeIndex: Math.Clamp(tp.WeekDayIndex - 1, 0, 6),
                accentColor: weekColor));

            stack.Children.Add(BuildCompactLinearProgressSection(
                title: tp.MonthTitle,
                summary: tp.MonthSummary,
                percent: tp.MonthPercent,
                accentColor: monthColor));
        }

        if (hasAboveModules)
        {
            return new Border
            {
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(2, 8, 2, 2),
                Child = stack
            };
        }

        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ResolveCardBackground("default"),
            Child = stack
        };
    }

    /// <summary>Compact work-hours section: title + range header, 3px live bar, gentle phase summary.</summary>
    private FrameworkElement BuildWorkdayProgressSection(CustomWidgetWorkdayProgressSnapshot work)
    {
        var section = new StackPanel { Spacing = 7 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = work.Title,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 0);
        header.Children.Add(titleTb);

        var rangeTb = new TextBlock
        {
            Text = work.RangeText,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        SetTabularNumerals(rangeTb);
        Grid.SetColumn(rangeTb, 1);
        header.Children.Add(rangeTb);
        section.Children.Add(header);

        // After work the bar pins to a calm emerald 100% — never an overflowing加班 bar.
        Color workColor = work.Phase switch
        {
            CustomWidgetWorkdayPhase.AfterWork => IsDark
                ? Color.FromArgb(255, 52, 211, 153)
                : Color.FromArgb(255, 16, 185, 129),
            CustomWidgetWorkdayPhase.Working => IsDark
                ? Color.FromArgb(255, 167, 139, 250)
                : Color.FromArgb(255, 124, 58, 237),
            _ => IsDark
                ? Color.FromArgb(255, 161, 161, 170)
                : Color.FromArgb(255, 113, 113, 122)
        };

        section.Children.Add(BuildCountdownLinearBar(
            work.Percent,
            GetOrCreateSolidBrush(workColor),
            fixedWidth: null,
            height: 3.0));

        var summaryTb = new TextBlock
        {
            Text = work.Summary,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(summaryTb);
        section.Children.Add(summaryTb);

        return section;
    }

    /// <summary>Header + stretch linear bar (compact month row inside the four-dimension scope).</summary>
    private FrameworkElement BuildCompactLinearProgressSection(
        string title,
        string summary,
        double percent,
        Color accentColor)
    {
        var section = new StackPanel { Spacing = 7 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(titleTb);
        Grid.SetColumn(titleTb, 0);
        header.Children.Add(titleTb);

        var sumTb = new TextBlock
        {
            Text = summary,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        SetTabularNumerals(sumTb);
        Grid.SetColumn(sumTb, 1);
        header.Children.Add(sumTb);
        section.Children.Add(header);

        section.Children.Add(BuildCountdownLinearBar(
            percent,
            GetOrCreateSolidBrush(accentColor),
            fixedWidth: null,
            height: 3.0));

        return section;
    }

    private FrameworkElement BuildDotMatrixSection(
        string title,
        string summary,
        int totalDots,
        int columns,
        int activeIndex,
        Color accentColor)
    {
        var section = new StackPanel
        {
            Spacing = 8
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(titleTb);
        Grid.SetColumn(titleTb, 0);
        header.Children.Add(titleTb);

        var sumTb = new TextBlock
        {
            Text = summary,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        SetTabularNumerals(sumTb);
        Grid.SetColumn(sumTb, 1);
        header.Children.Add(sumTb);

        section.Children.Add(header);

        int rows = (totalDots + columns - 1) / columns;
        var matrix = new Grid
        {
            RowSpacing = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        for (int c = 0; c < columns; c++)
        {
            matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int r = 0; r < rows; r++)
        {
            matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Brush passedBrush = GetOrCreateSolidBrush(accentColor);
        Brush todayBrush = GetOrCreateSolidBrush(IsDark ? Colors.White : Color.FromArgb(255, 24, 24, 27));
        Brush futureBrush = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));

        for (int i = 0; i < totalDots; i++)
        {
            Brush dotBrush = i < activeIndex
                ? passedBrush
                : (i == activeIndex ? todayBrush : futureBrush);

            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(2),
                Background = dotBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(dot, i / columns);
            Grid.SetColumn(dot, i % columns);
            matrix.Children.Add(dot);
        }

        section.Children.Add(matrix);
        return section;
    }

    private FrameworkElement BuildCountdownLinearBar(
        double progressPercent,
        Brush fillBrush,
        double? fixedWidth,
        double height)
    {
        double clampedRatio = Math.Clamp(progressPercent / 100.0, 0.0, 1.0);
        double radius = height * 0.5;

        var trackGrid = new Grid
        {
            Height = height,
            HorizontalAlignment = fixedWidth.HasValue ? HorizontalAlignment.Center : HorizontalAlignment.Stretch
        };
        if (fixedWidth.HasValue)
        {
            trackGrid.Width = fixedWidth.Value;
        }

        var trackBg = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        trackGrid.Children.Add(trackBg);

        var fillBorder = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = fillBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = fixedWidth.HasValue ? Math.Max(height, fixedWidth.Value * clampedRatio) : 0
        };
        trackGrid.Children.Add(fillBorder);

        if (!fixedWidth.HasValue)
        {
            trackGrid.SizeChanged += (_, args) =>
            {
                double w = args.NewSize.Width;
                fillBorder.Width = clampedRatio <= 0.001
                    ? 0
                    : Math.Clamp(w * clampedRatio, height, w);
            };
        }

        return trackGrid;
    }

    private void AttachCountdownItemContextMenu(
        FrameworkElement element,
        CustomWidgetCountdownEventView item,
        bool includePin,
        string cultureName)
    {
        var menu = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        if (includePin)
        {
            var pinItem = new MenuFlyoutItem
            {
                Text = Tr("CustomWidget.Renderer.Countdown.PinToHeroTooltip", cultureName),
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            string pinId = item.Id;
            pinItem.Click += async (_, _) => await _viewModel.PinCountdownEventAsync(pinId);
            menu.Items.Add(pinItem);
        }

        var editItem = new MenuFlyoutItem
        {
            Text = Tr("CustomWidget.Renderer.Countdown.EditEventMenu", cultureName),
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        editItem.Click += (_, _) =>
        {
            menu.Hide();
            UiDispatch.PostAfterCallback(() => ShowCountdownEventEditorFlyout(element, item));
        };
        menu.Items.Add(editItem);

        var addItem = new MenuFlyoutItem
        {
            Text = Tr("CustomWidget.Renderer.Countdown.NewEventMenu", cultureName),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        addItem.Click += (_, _) =>
        {
            menu.Hide();
            UiDispatch.PostAfterCallback(() => ShowCountdownEventEditorFlyout(element, existingEvent: null));
        };
        menu.Items.Add(addItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = Tr("CustomWidget.Renderer.Countdown.DeleteEventMenu", cultureName),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        string delId = item.Id;
        deleteItem.Click += async (_, _) => await _viewModel.DeleteCountdownEventAsync(delId);
        menu.Items.Add(deleteItem);

        TrackInteractiveFlyout(menu);
        element.ContextFlyout = menu;
    }

    private void ShowCountdownEventEditorFlyout(
        FrameworkElement anchor,
        CustomWidgetCountdownEventView? existingEvent)
    {
        IntPtr ownerHwnd = DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(anchor);
        string logicalKey = $"countdown-editor:{existingEvent?.Id ?? "new"}";
        if (DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(ownerHwnd, logicalKey, anchor))
        {
            return;
        }

        string flyoutCulture = _viewModel.ActiveCultureName;
        IReadOnlyList<(string Glyph, string Label)> presetFluentIcons =
            CustomWidgetCountdownEngine.GetPresetFluentIcons(flyoutCulture);
        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime);

        string selectedIcon = existingEvent?.Emoji ?? CustomWidgetCountdownEngine.DefaultEventIconGlyph;
        string selectedCalendar = existingEvent?.CalendarType ?? CustomWidgetCountdownEngine.CalendarSolar;
        string selectedRepeat = existingEvent?.RepeatMode ?? CustomWidgetCountdownEngine.RepeatNone;
        string selectedWeekendRule = CustomWidgetCountdownEngine.NormalizeWeekendRule(existingEvent?.WeekendRule);
        bool isPinned = existingEvent?.IsPinned ?? false;
        bool selIsLunarLeap = existingEvent?.IsLunarLeap ?? false;

        int selYear = today.Year;
        int selMonth = today.Month;
        int selDay = today.Day;

        if (existingEvent is not null &&
            CustomWidgetCountdownEngine.TryParseYmd(existingEvent.RawDate, out int ey, out int em, out int ed))
        {
            selYear = ey;
            selMonth = em;
            selDay = ed;
        }
        else
        {
            DateOnly defaultTarget = today.AddDays(7);
            selYear = defaultTarget.Year;
            selMonth = defaultTarget.Month;
            selDay = defaultTarget.Day;
        }

        var root = new StackPanel
        {
            Width = 286,
            Spacing = 12
        };

        // 1. Header
        string editorTitle = existingEvent is null
            ? Tr("CustomWidget.Renderer.Countdown.NewEventTitle", flyoutCulture)
            : Tr("CustomWidget.Renderer.Countdown.EditEventTitle", flyoutCulture);
        var popover = new DeskBox.Views.WidgetEditorPopoverWindow(
            ownerHwnd,
            editorTitle);

        root.Children.Add(new TextBlock
        {
            Text = existingEvent is null
                ? Tr("CustomWidget.Renderer.Countdown.NewEventTitle", flyoutCulture)
                : Tr("CustomWidget.Renderer.Countdown.EditEventTitle", flyoutCulture),
            FontSize = SubtitleFontSize,
            FontWeight = FontWeights.SemiBold
        });

        // 2. Icon Button + Title TextBox
        var nameGrid = new Grid
        {
            ColumnSpacing = 8
        };
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconPreviewHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        void RefreshIconPreview()
        {
            iconPreviewHost.Children.Clear();
            iconPreviewHost.Children.Add(CreateCountdownInlineIcon(selectedIcon, 15, ResolveForegroundBrush("default")));
        }
        RefreshIconPreview();

        var iconBtn = new Button
        {
            Width = 36,
            Height = 32,
            MinWidth = 36,
            MinHeight = 32,
            Padding = new Thickness(0),
            Content = iconPreviewHost
        };
        StyleStandardCommandButton(iconBtn, CornerSmall);
        ToolTipService.SetToolTip(iconBtn, Tr("CustomWidget.Renderer.Countdown.PickIconTooltip", flyoutCulture));
        Grid.SetColumn(iconBtn, 0);
        nameGrid.Children.Add(iconBtn);

        var titleBox = new TextBox
        {
            PlaceholderText = Tr("CustomWidget.Renderer.Countdown.EventNamePlaceholder", flyoutCulture),
            Text = existingEvent?.Title ?? string.Empty,
            MaxLength = 40,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyCompactInputStyle(titleBox);
        Grid.SetColumn(titleBox, 1);
        nameGrid.Children.Add(titleBox);
        root.Children.Add(nameGrid);

        // Collapsible Inline Icon Picker: Fluent Monochrome Category Icons (primary) + Emoji (secondary)
        var iconDrawer = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(8),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        var iconDrawerStack = new StackPanel { Spacing = 8 };

        var fluentGrid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (int c = 0; c < 4; c++)
        {
            fluentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        fluentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fluentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pickButtons = new List<(string IconValue, Button Btn)>();

        void RefreshIconSelectionStates()
        {
            foreach ((string val, Button btn) in pickButtons)
            {
                ApplySegmentedItemState(btn, string.Equals(val, selectedIcon, StringComparison.Ordinal));
            }
        }

        for (int i = 0; i < presetFluentIcons.Count; i++)
        {
            (string glyph, string label) = presetFluentIcons[i];
            var pickBtn = new Button
            {
                Height = 30,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 14
                }
            };
            ToolTipService.SetToolTip(pickBtn, label);
            pickBtn.Click += (_, _) =>
            {
                selectedIcon = glyph;
                RefreshIconPreview();
                RefreshIconSelectionStates();
                iconDrawer.Visibility = Visibility.Collapsed;
            };
            pickButtons.Add((glyph, pickBtn));
            Grid.SetRow(pickBtn, i / 4);
            Grid.SetColumn(pickBtn, i % 4);
            fluentGrid.Children.Add(pickBtn);
        }
        iconDrawerStack.Children.Add(fluentGrid);

        iconDrawerStack.Children.Add(new Border
        {
            Height = 1,
            Background = DividerBrush
        });

        var emojiGrid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (int c = 0; c < 5; c++)
        {
            emojiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        emojiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        emojiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < CustomWidgetCountdownEngine.PresetEmojis.Count; i++)
        {
            string presetEmoji = CustomWidgetCountdownEngine.PresetEmojis[i];
            var pickBtn = new Button
            {
                Height = 30,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                Content = CreateUnclippedEmojiElement(presetEmoji, 14.5)
            };
            pickBtn.Click += (_, _) =>
            {
                selectedIcon = presetEmoji;
                RefreshIconPreview();
                RefreshIconSelectionStates();
                iconDrawer.Visibility = Visibility.Collapsed;
            };
            pickButtons.Add((presetEmoji, pickBtn));
            Grid.SetRow(pickBtn, i / 5);
            Grid.SetColumn(pickBtn, i % 5);
            emojiGrid.Children.Add(pickBtn);
        }
        iconDrawerStack.Children.Add(emojiGrid);
        iconDrawer.Child = iconDrawerStack;
        RefreshIconSelectionStates();

        iconBtn.Click += (_, _) =>
        {
            iconDrawer.Visibility = iconDrawer.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (iconDrawer.Visibility == Visibility.Visible)
            {
                RefreshIconSelectionStates();
            }
        };
        root.Children.Add(iconDrawer);

        // 3. Calendar System (公历 / 农历) Segmented + Date Trigger Button
        var dateBarGrid = new Grid
        {
            ColumnSpacing = 8
        };
        dateBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var calSegBorder = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1)
        };
        var calSegHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        var solarTabBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Countdown.SolarTab", flyoutCulture),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = CornerSmall,
            FontSize = CaptionFontSize
        };
        var lunarTabBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Countdown.LunarTab", flyoutCulture),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = CornerSmall,
            FontSize = CaptionFontSize
        };
        calSegHost.Children.Add(solarTabBtn);
        calSegHost.Children.Add(lunarTabBtn);
        calSegBorder.Child = calSegHost;
        Grid.SetColumn(calSegBorder, 0);
        dateBarGrid.Children.Add(calSegBorder);

        var dateTriggerTb = new TextBlock
        {
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(dateTriggerTb);
        var dateTriggerContent = new Grid();
        dateTriggerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateTriggerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dateTriggerTb, 0);
        dateTriggerContent.Children.Add(dateTriggerTb);
        var dateChevron = new FontIcon
        {
            Glyph = "\uE972",
            FontSize = 8.5,
            Opacity = 0.72,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dateChevron, 1);
        dateTriggerContent.Children.Add(dateChevron);

        var dateTriggerBtn = new Button
        {
            Height = 32,
            MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 0, 10, 0),
            Content = dateTriggerContent
        };
        StyleStandardCommandButton(dateTriggerBtn, CornerSmall);
        Grid.SetColumn(dateTriggerBtn, 1);
        dateBarGrid.Children.Add(dateTriggerBtn);
        root.Children.Add(dateBarGrid);

        // Inline Date Drawer (renders Solar Month Grid OR Lunar Year/Month/Day Picker)
        var inlineDateDrawer = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(10, 8, 10, 10),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(inlineDateDrawer);

        void RefreshDateTriggerAndDrawer()
        {
            bool isLunar = selectedCalendar == CustomWidgetCountdownEngine.CalendarLunar;
            ApplySegmentedItemState(solarTabBtn, !isLunar);
            ApplySegmentedItemState(lunarTabBtn, isLunar);

            if (isLunar)
            {
                selMonth = Math.Clamp(selMonth, 1, 12);
                selDay = Math.Clamp(selDay, 1, 30);
                if (CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(selYear) != selMonth)
                {
                    selIsLunarLeap = false;
                }
                dateTriggerTb.Text = TrFormat(
                    "CustomWidget.Renderer.Countdown.LunarDateTriggerFormat",
                    flyoutCulture,
                    selYear,
                    CustomWidgetCountdownEngine.FormatLunarMonthDay(selMonth, selDay, selIsLunarLeap, flyoutCulture));
            }
            else
            {
                selMonth = Math.Clamp(selMonth, 1, 12);
                selDay = Math.Clamp(selDay, 1, DateTime.DaysInMonth(selYear, selMonth));
                dateTriggerTb.Text = $"{selYear:D4}-{selMonth:D2}-{selDay:D2}";
            }

            if (inlineDateDrawer.Visibility == Visibility.Visible)
            {
                inlineDateDrawer.Child = isLunar
                    ? BuildInlineLunarPicker(
                        selYear,
                        selMonth,
                        selDay,
                        selIsLunarLeap,
                        flyoutCulture,
                        (ny, nm, nd, nLeap) =>
                        {
                            selYear = ny;
                            selMonth = nm;
                            selDay = nd;
                            selIsLunarLeap = nLeap;
                            RefreshDateTriggerAndDrawer();
                        })
                    : BuildInlineSolarCalendar(
                        selYear,
                        selMonth,
                        selDay,
                        today,
                        flyoutCulture,
                        (ny, nm, nd, closeDrawer) =>
                        {
                            selYear = ny;
                            selMonth = nm;
                            selDay = nd;
                            if (closeDrawer)
                            {
                                inlineDateDrawer.Visibility = Visibility.Collapsed;
                            }
                            RefreshDateTriggerAndDrawer();
                        });
            }
        }

        StackPanel? weekendRuleSection = null;
        (string Key, string Label)[] weekendRuleOptions = [];
        Button[] weekendRuleButtons = [];

        solarTabBtn.Click += (_, _) =>
        {
            if (selectedCalendar != CustomWidgetCountdownEngine.CalendarSolar)
            {
                if (CustomWidgetCountdownEngine.TryLunarToSolar(selYear, selMonth, selDay, selIsLunarLeap, out DateOnly conv))
                {
                    selYear = conv.Year;
                    selMonth = conv.Month;
                    selDay = conv.Day;
                }
                selIsLunarLeap = false;
                selectedCalendar = CustomWidgetCountdownEngine.CalendarSolar;
                RefreshDateTriggerAndDrawer();
                RefreshWeekendRuleSection();
            }
        };

        lunarTabBtn.Click += (_, _) =>
        {
            if (selectedCalendar != CustomWidgetCountdownEngine.CalendarLunar)
            {
                int clampedD = Math.Clamp(selDay, 1, DateTime.DaysInMonth(selYear, selMonth));
                DateOnly curSolar = new(selYear, selMonth, clampedD);
                if (CustomWidgetCountdownEngine.TrySolarToLunar(curSolar, out int ly, out int lm, out int ld, out bool isLeap))
                {
                    selYear = ly;
                    selMonth = lm;
                    selDay = ld;
                    selIsLunarLeap = isLeap;
                }
                else
                {
                    selIsLunarLeap = false;
                }
                selectedCalendar = CustomWidgetCountdownEngine.CalendarLunar;
                RefreshDateTriggerAndDrawer();
                RefreshWeekendRuleSection();
            }
        };

        dateTriggerBtn.Click += (_, _) =>
        {
            inlineDateDrawer.Visibility = inlineDateDrawer.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshDateTriggerAndDrawer();
        };

        RefreshDateTriggerAndDrawer();

        // 4. Repeat Mode Segmented Strip
        var repeatSection = new StackPanel { Spacing = 6 };
        repeatSection.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.RepeatLabel", flyoutCulture),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted")
        });

        var repeatTrack = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1)
        };
        var repeatGrid = new Grid { ColumnSpacing = 2 };
        (string Key, string Label)[] repeatOptions =
        [
            (CustomWidgetCountdownEngine.RepeatNone, Tr("CustomWidget.Renderer.Countdown.RepeatNone", flyoutCulture)),
            (CustomWidgetCountdownEngine.RepeatMonthly, Tr("CustomWidget.Renderer.Countdown.RepeatMonthly", flyoutCulture)),
            (CustomWidgetCountdownEngine.RepeatYearly, Tr("CustomWidget.Renderer.Countdown.RepeatYearly", flyoutCulture)),
            (CustomWidgetCountdownEngine.RepeatWeekly, Tr("CustomWidget.Renderer.Countdown.RepeatWeekly", flyoutCulture))
        ];
        var repeatButtons = new Button[repeatOptions.Length];
        void RefreshRepeatButtons()
        {
            for (int i = 0; i < repeatOptions.Length; i++)
            {
                ApplySegmentedItemState(repeatButtons[i], selectedRepeat == repeatOptions[i].Key);
            }
        }
        for (int i = 0; i < repeatOptions.Length; i++)
        {
            repeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string key = repeatOptions[i].Key;
            var btn = new Button
            {
                Content = repeatOptions[i].Label,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            btn.Click += (_, _) =>
            {
                selectedRepeat = key;
                RefreshRepeatButtons();
                RefreshWeekendRuleSection();
            };
            repeatButtons[i] = btn;
            Grid.SetColumn(btn, i);
            repeatGrid.Children.Add(btn);
        }
        RefreshRepeatButtons();
        repeatTrack.Child = repeatGrid;
        repeatSection.Children.Add(repeatTrack);
        root.Children.Add(repeatSection);

        // v3.0: payday weekend rule (solar monthly only) — 不调整 / 提前至周五 / 顺延至周一.
        weekendRuleSection = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        weekendRuleSection.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.WeekendRuleLabel", flyoutCulture),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted")
        });

        var weekendRuleTrack = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1)
        };
        var weekendRuleGrid = new Grid { ColumnSpacing = 2 };
        weekendRuleOptions =
        [
            (CustomWidgetCountdownEngine.WeekendRuleNone, Tr("CustomWidget.Renderer.Countdown.WeekendRuleNone", flyoutCulture)),
            (CustomWidgetCountdownEngine.WeekendRuleFriday, Tr("CustomWidget.Renderer.Countdown.WeekendRuleFriday", flyoutCulture)),
            (CustomWidgetCountdownEngine.WeekendRuleMonday, Tr("CustomWidget.Renderer.Countdown.WeekendRuleMonday", flyoutCulture))
        ];
        weekendRuleButtons = new Button[weekendRuleOptions.Length];
        for (int i = 0; i < weekendRuleOptions.Length; i++)
        {
            weekendRuleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string key = weekendRuleOptions[i].Key;
            var btn = new Button
            {
                Content = weekendRuleOptions[i].Label,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            btn.Click += (_, _) =>
            {
                selectedWeekendRule = key;
                RefreshWeekendRuleSection();
            };
            weekendRuleButtons[i] = btn;
            Grid.SetColumn(btn, i);
            weekendRuleGrid.Children.Add(btn);
        }

        void RefreshWeekendRuleSection()
        {
            if (weekendRuleSection is null)
            {
                return;
            }

            bool applicable = selectedRepeat == CustomWidgetCountdownEngine.RepeatMonthly &&
                              selectedCalendar == CustomWidgetCountdownEngine.CalendarSolar;
            weekendRuleSection.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < weekendRuleOptions.Length; i++)
            {
                ApplySegmentedItemState(weekendRuleButtons[i], applicable && selectedWeekendRule == weekendRuleOptions[i].Key);
            }
        }

        weekendRuleTrack.Child = weekendRuleGrid;
        weekendRuleSection.Children.Add(weekendRuleTrack);
        root.Children.Add(weekendRuleSection);
        RefreshWeekendRuleSection();

        // 5. Footer: Delete (if edit) + Pin Toggle + Cancel + Save
        var footerGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        if (existingEvent is not null)
        {
            Brush trans = GetOrCreateSolidBrush(Colors.Transparent);
            Brush delHover = GetOrCreateSolidBrush(Color.FromArgb(26, 239, 68, 68));
            var delBtn = new Button
            {
                Width = 30,
                Height = 30,
                MinWidth = 30,
                MinHeight = 30,
                Padding = new Thickness(0),
                CornerRadius = CornerMedium,
                BorderThickness = new Thickness(0),
                BorderBrush = trans,
                Background = trans,
                Content = new FontIcon
                {
                    Glyph = "\uE74D",
                    FontSize = 12.5,
                    Foreground = ResolveForegroundBrush("danger")
                }
            };
            ApplyButtonStateOverrides(delBtn, delHover, delHover, trans, trans);
            ToolTipService.SetToolTip(delBtn, Tr("CustomWidget.Renderer.Countdown.DeleteEventMenu", flyoutCulture));
            string delId = existingEvent.Id;
            delBtn.Click += async (_, _) =>
            {
                popover.ClosePopover(restoreOwnerForeground: true);
                await _viewModel.DeleteCountdownEventAsync(delId);
            };
            leftActions.Children.Add(delBtn);
        }

        var pinContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        pinContent.Children.Add(new FontIcon
        {
            Glyph = "\uE718",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        pinContent.Children.Add(new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Countdown.PinButton", flyoutCulture),
            FontSize = CaptionFontSize,
            VerticalAlignment = VerticalAlignment.Center
        });

        var pinToggleBtn = new Button
        {
            Content = pinContent,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = CornerSmall
        };
        ApplySegmentedItemState(pinToggleBtn, isPinned);
        pinToggleBtn.Click += (_, _) =>
        {
            isPinned = !isPinned;
            ApplySegmentedItemState(pinToggleBtn, isPinned);
        };
        leftActions.Children.Add(pinToggleBtn);
        Grid.SetColumn(leftActions, 0);
        footerGrid.Children.Add(leftActions);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var cancelBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Countdown.CancelButton", flyoutCulture),
            Height = 30,
            MinHeight = 30,
            MinWidth = 58,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = CaptionFontSize
        };
        StyleStandardCommandButton(cancelBtn, CornerMedium);
        cancelBtn.Click += (_, _) => popover.ClosePopover(restoreOwnerForeground: true);
        rightActions.Children.Add(cancelBtn);

        var saveBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Countdown.SaveButton", flyoutCulture),
            Height = 30,
            MinHeight = 30,
            MinWidth = 62,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = CaptionFontSize
        };
        StyleAccentCommitButton(saveBtn);

        async Task CommitSaveAsync()
        {
            string finalTitle = string.IsNullOrWhiteSpace(titleBox.Text)
                ? Tr("CustomWidget.Renderer.Countdown.DefaultEventName", flyoutCulture)
                : titleBox.Text.Trim();
            string dateIso = $"{selYear:D4}-{selMonth:D2}-{selDay:D2}";
            bool finalIsLunarLeap = selectedCalendar == CustomWidgetCountdownEngine.CalendarLunar &&
                                    selIsLunarLeap &&
                                    CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(selYear) == selMonth;
            popover.ClosePopover(restoreOwnerForeground: true);
            string? finalWeekendRule = selectedRepeat == CustomWidgetCountdownEngine.RepeatMonthly &&
                                       selectedCalendar == CustomWidgetCountdownEngine.CalendarSolar
                ? selectedWeekendRule
                : null;
            await _viewModel.UpsertCountdownEventAsync(
                eventId: existingEvent?.Id,
                title: finalTitle,
                emoji: selectedIcon,
                dateIso: dateIso,
                calendar: selectedCalendar,
                repeat: selectedRepeat,
                pinned: isPinned,
                isLunarLeap: finalIsLunarLeap,
                weekendRule: finalWeekendRule);
        }

        saveBtn.Click += async (_, _) => await CommitSaveAsync();
        titleBox.KeyDown += async (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter &&
                !DeskBox.Views.WidgetEditorPopoverWindow.IsImeCompositionKeyEvent(ke))
            {
                ke.Handled = true;
                await CommitSaveAsync();
            }
        };

        rightActions.Children.Add(saveBtn);
        Grid.SetColumn(rightActions, 2);
        footerGrid.Children.Add(rightActions);
        root.Children.Add(footerGrid);

        if (root.Children.Count > 0 && root.Children[0] is TextBlock headerTitleText)
        {
            root.Children.RemoveAt(0);
            headerTitleText.VerticalAlignment = VerticalAlignment.Center;
            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(headerTitleText);
            Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                Tr("Common.Close", flyoutCulture),
                () => popover.ClosePopover(restoreOwnerForeground: true),
                _themeScope);
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);
            root.Children.Insert(0, headerGrid);
        }

        // Hosted in WidgetEditorPopoverWindow with its own XamlRoot and ContentIsland
        // so CJK composition and Windows IME candidate window coordinates stay exact.
        ShowTrackedPopoverAt(
            popover,
            anchor,
            root,
            DeskBox.Views.WidgetPopoverPlacement.Bottom,
            minWidthDip: 316,
            maxWidthDip: 338,
            initialFocusTarget: titleBox,
            logScope: "CountdownEventEditor",
            logicalKey: logicalKey);

    }

    private UIElement BuildInlineSolarCalendar(
        int year,
        int month,
        int selectedDay,
        DateOnly today,
        string cultureName,
        Action<int, int, int, bool> onChanged)
    {
        var wrap = new StackPanel { Spacing = 6 };
        CultureInfo calCulture;
        try
        {
            calCulture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            calCulture = CultureInfo.InvariantCulture;
        }

        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button MakeNavBtn(string label, Action click)
        {
            Brush trans = GetOrCreateSolidBrush(Colors.Transparent);
            var b = new Button
            {
                Content = label,
                Width = 26,
                Height = 24,
                MinWidth = 26,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                BorderThickness = new Thickness(0),
                BorderBrush = trans,
                Background = trans,
                FontSize = CaptionFontSize
            };
            ApplyButtonStateOverrides(b, SubtleHoverBrush, SubtlePressedBrush, trans, trans);
            b.Click += (_, _) => click();
            return b;
        }

        Button prevYearBtn = MakeNavBtn("«", () =>
        {
            int ny = Math.Clamp(year - 1, 1902, 2099);
            onChanged(ny, month, Math.Min(selectedDay, DateTime.DaysInMonth(ny, month)), false);
        });
        Grid.SetColumn(prevYearBtn, 0);
        navGrid.Children.Add(prevYearBtn);

        Button prevMonthBtn = MakeNavBtn("‹", () =>
        {
            int nm = month == 1 ? 12 : month - 1;
            int ny = month == 1 ? Math.Max(1902, year - 1) : year;
            onChanged(ny, nm, Math.Min(selectedDay, DateTime.DaysInMonth(ny, nm)), false);
        });
        prevMonthBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(prevMonthBtn, 1);
        navGrid.Children.Add(prevMonthBtn);

        bool isCjkYearMonth = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                              cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
                              cultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        string monthDisplay = isCjkYearMonth
            ? month.ToString("D2", CultureInfo.InvariantCulture)
            : new DateTime(year, month, 1).ToString("MMM", calCulture);
        var ymTb = new TextBlock
        {
            Text = TrFormat(
                "CustomWidget.Renderer.Countdown.SolarYearMonthFormat",
                cultureName,
                year,
                monthDisplay),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(ymTb);
        Grid.SetColumn(ymTb, 2);
        navGrid.Children.Add(ymTb);

        Button nextMonthBtn = MakeNavBtn("›", () =>
        {
            int nm = month == 12 ? 1 : month + 1;
            int ny = month == 12 ? Math.Min(2099, year + 1) : year;
            onChanged(ny, nm, Math.Min(selectedDay, DateTime.DaysInMonth(ny, nm)), false);
        });
        nextMonthBtn.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(nextMonthBtn, 3);
        navGrid.Children.Add(nextMonthBtn);

        Button nextYearBtn = MakeNavBtn("»", () =>
        {
            int ny = Math.Clamp(year + 1, 1902, 2099);
            onChanged(ny, month, Math.Min(selectedDay, DateTime.DaysInMonth(ny, month)), false);
        });
        Grid.SetColumn(nextYearBtn, 4);
        navGrid.Children.Add(nextYearBtn);

        wrap.Children.Add(navGrid);

        var calGrid = new Grid { RowSpacing = 2, ColumnSpacing = 2 };
        string[] dows = BuildMondayFirstWeekdayLabels(cultureName, calCulture);
        for (int c = 0; c < 7; c++)
        {
            calGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int r = 0; r < 7; r++)
        {
            calGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int c = 0; c < 7; c++)
        {
            var dowTb = new TextBlock
            {
                Text = dows[c],
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(dowTb, 0);
            Grid.SetColumn(dowTb, c);
            calGrid.Children.Add(dowTb);
        }

        DateOnly firstOfMonth = new(year, month, 1);
        int startOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        int daysInMonth = DateTime.DaysInMonth(year, month);

        for (int d = 1; d <= daysInMonth; d++)
        {
            int cell = startOffset + d - 1;
            int row = 1 + (cell / 7);
            int col = cell % 7;
            bool isSelected = d == selectedDay;
            bool isToday = year == today.Year && month == today.Month && d == today.Day;

            int capturedDay = d;
            var dayBtn = new Button
            {
                Content = d.ToString(CultureInfo.InvariantCulture),
                Height = 26,
                MinHeight = 26,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplySegmentedItemState(dayBtn, isSelected);
            if (isToday && !isSelected)
            {
                dayBtn.BorderBrush = ResolveSemanticBrush("accent");
                dayBtn.FontWeight = FontWeights.SemiBold;
            }

            dayBtn.Click += (_, _) => onChanged(year, month, capturedDay, true);
            Grid.SetRow(dayBtn, row);
            Grid.SetColumn(dayBtn, col);
            calGrid.Children.Add(dayBtn);
        }

        wrap.Children.Add(calGrid);
        return wrap;
    }

    private static string[] BuildMondayFirstWeekdayLabels(string cultureName, CultureInfo calCulture)
    {
        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return ["一", "二", "三", "四", "五", "六", "日"];
        }

        if (cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return ["月", "火", "水", "木", "金", "土", "日"];
        }

        if (cultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return ["월", "화", "수", "목", "금", "토", "일"];
        }

        string[] shortest = calCulture.DateTimeFormat.ShortestDayNames;
        if (shortest.Length == 7)
        {
            return [shortest[1], shortest[2], shortest[3], shortest[4], shortest[5], shortest[6], shortest[0]];
        }

        return ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    }

    private UIElement BuildInlineLunarPicker(
        int lunarYear,
        int lunarMonth,
        int lunarDay,
        bool isLeapMonth,
        string cultureName,
        Action<int, int, int, bool> onChanged)
    {
        int leapLogicalMonth = CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(lunarYear);
        bool effectiveLeap = isLeapMonth && leapLogicalMonth == lunarMonth;

        var wrap = new StackPanel { Spacing = 8 };

        var yearNav = new Grid();
        yearNav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        yearNav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        yearNav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush trans = GetOrCreateSolidBrush(Colors.Transparent);
        var prevY = new Button
        {
            Content = "‹",
            Width = 28,
            Height = 24,
            MinWidth = 28,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = CornerSmall,
            BorderThickness = new Thickness(0),
            BorderBrush = trans,
            Background = trans
        };
        ApplyButtonStateOverrides(prevY, SubtleHoverBrush, SubtlePressedBrush, trans, trans);
        prevY.Click += (_, _) =>
        {
            int ny = Math.Clamp(lunarYear - 1, 1902, 2099);
            onChanged(
                ny,
                lunarMonth,
                lunarDay,
                effectiveLeap && CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(ny) == lunarMonth);
        };
        Grid.SetColumn(prevY, 0);
        yearNav.Children.Add(prevY);

        string solarHint = CustomWidgetCountdownEngine.TryLunarToSolar(lunarYear, lunarMonth, lunarDay, effectiveLeap, out DateOnly mappedSolar)
            ? TrFormat(
                "CustomWidget.Renderer.Countdown.MappedSolarHintFormat",
                cultureName,
                mappedSolar.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : string.Empty;
        var yLabel = new TextBlock
        {
            Text = TrFormat("CustomWidget.Renderer.Countdown.LunarYearHeaderFormat", cultureName, lunarYear, solarHint).Trim(),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(yLabel);
        Grid.SetColumn(yLabel, 1);
        yearNav.Children.Add(yLabel);

        var nextY = new Button
        {
            Content = "›",
            Width = 28,
            Height = 24,
            MinWidth = 28,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = CornerSmall,
            BorderThickness = new Thickness(0),
            BorderBrush = trans,
            Background = trans
        };
        ApplyButtonStateOverrides(nextY, SubtleHoverBrush, SubtlePressedBrush, trans, trans);
        nextY.Click += (_, _) =>
        {
            int ny = Math.Clamp(lunarYear + 1, 1902, 2099);
            onChanged(
                ny,
                lunarMonth,
                lunarDay,
                effectiveLeap && CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(ny) == lunarMonth);
        };
        Grid.SetColumn(nextY, 2);
        yearNav.Children.Add(nextY);
        wrap.Children.Add(yearNav);

        // 6x2 Lunar Months Grid
        var monthGrid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (int c = 0; c < 6; c++) monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 2; r++) monthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int m = 1; m <= 12; m++)
        {
            int capturedM = m;
            bool sel = m == lunarMonth;
            var mb = new Button
            {
                Content = CustomWidgetCountdownEngine.GetLunarMonthName(m, cultureName),
                Height = 25,
                MinHeight = 25,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplySegmentedItemState(mb, sel);
            mb.Click += (_, _) => onChanged(lunarYear, capturedM, lunarDay, capturedM == lunarMonth && effectiveLeap);
            Grid.SetRow(mb, (m - 1) / 6);
            Grid.SetColumn(mb, (m - 1) % 6);
            monthGrid.Children.Add(mb);
        }
        wrap.Children.Add(monthGrid);

        if (leapLogicalMonth == lunarMonth)
        {
            string monthName = CustomWidgetCountdownEngine.GetLunarMonthName(lunarMonth, cultureName);
            var leapRow = new Grid { ColumnSpacing = 3 };
            leapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var regularBtn = new Button
            {
                Content = monthName,
                Height = 24,
                MinHeight = 24,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplySegmentedItemState(regularBtn, !effectiveLeap);
            regularBtn.Click += (_, _) =>
            {
                if (effectiveLeap)
                {
                    onChanged(lunarYear, lunarMonth, lunarDay, false);
                }
            };
            Grid.SetColumn(regularBtn, 0);
            leapRow.Children.Add(regularBtn);

            var leapBtn = new Button
            {
                Content = TrFormat("CustomWidget.Renderer.Countdown.LunarLeapMonthFormat", cultureName, monthName),
                Height = 24,
                MinHeight = 24,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplySegmentedItemState(leapBtn, effectiveLeap);
            leapBtn.Click += (_, _) =>
            {
                if (!effectiveLeap)
                {
                    onChanged(lunarYear, lunarMonth, lunarDay, true);
                }
            };
            Grid.SetColumn(leapBtn, 1);
            leapRow.Children.Add(leapBtn);

            wrap.Children.Add(leapRow);
        }

        // 6x5 Lunar Days Grid
        var dayGrid = new Grid { RowSpacing = 2, ColumnSpacing = 2 };
        for (int c = 0; c < 6; c++) dayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 5; r++) dayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int d = 1; d <= 30; d++)
        {
            int capturedD = d;
            bool sel = d == lunarDay;
            var db = new Button
            {
                Content = CustomWidgetCountdownEngine.GetLunarDayName(d, cultureName),
                Height = 25,
                MinHeight = 25,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplySegmentedItemState(db, sel);
            db.Click += (_, _) => onChanged(lunarYear, lunarMonth, capturedD, effectiveLeap);
            Grid.SetRow(db, (d - 1) / 6);
            Grid.SetColumn(db, (d - 1) % 6);
            dayGrid.Children.Add(db);
        }
        wrap.Children.Add(dayGrid);

        return wrap;
    }

    private FrameworkElement CreateCountdownIconBadge(string icon, double badgeSize, double glyphSize)
    {
        var box = new Border
        {
            Width = badgeSize,
            Height = badgeSize,
            CornerRadius = CornerSmall,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Center
        };

        UIElement inner = CustomWidgetTemplates.IsEmojiIcon(icon)
            ? CreateUnclippedEmojiElement(icon, glyphSize + 0.5)
            : new FontIcon
            {
                Glyph = string.IsNullOrWhiteSpace(icon) ? CustomWidgetCountdownEngine.DefaultEventIconGlyph : icon,
                FontSize = glyphSize,
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

        box.Child = inner;
        return box;
    }

    private static FrameworkElement CreateCountdownInlineIcon(string icon, double fontSize, Brush foreground)
    {
        if (CustomWidgetTemplates.IsEmojiIcon(icon))
        {
            return CreateUnclippedEmojiElement(icon, fontSize);
        }

        return new FontIcon
        {
            Glyph = string.IsNullOrWhiteSpace(icon) ? CustomWidgetCountdownEngine.DefaultEventIconGlyph : icon,
            FontSize = fontSize,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static FrameworkElement CreateUnclippedEmojiElement(string emoji, double fontSize)
    {
        double boxWidth = Math.Ceiling(fontSize * 1.35);
        double boxHeight = Math.Ceiling(fontSize * 1.55);

        var host = new Grid
        {
            Width = boxWidth,
            Height = boxHeight,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tb = new TextBlock
        {
            Text = emoji,
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = fontSize,
            TextLineBounds = TextLineBounds.Full,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };
        WidgetTextShadow.SetCast(tb, false);
        host.Children.Add(tb);
        return host;
    }

    private Color ResolveCountdownSemanticColor(CustomWidgetCountdownEventView item)
    {
        bool dark = IsDark;
        if (item.IsArchivedExpired)
        {
            return dark
                ? Color.FromArgb(148, 255, 255, 255)
                : Color.FromArgb(132, 0, 0, 0);
        }

        if (item.Direction == CountdownDirection.Today)
        {
            return dark
                ? Color.FromArgb(255, 52, 211, 153)
                : Color.FromArgb(255, 16, 185, 129);
        }

        if (item.Direction == CountdownDirection.CountUp)
        {
            return dark
                ? Color.FromArgb(255, 251, 191, 36)
                : Color.FromArgb(255, 180, 83, 9);
        }

        if (item.IsUrgent)
        {
            return dark
                ? Color.FromArgb(255, 251, 146, 60)
                : Color.FromArgb(255, 234, 88, 12);
        }

        // v3.0 amber "approaching" tier (urgentDays window, beyond the legacy 3-day coral).
        if (item.IsApproaching)
        {
            return dark
                ? Color.FromArgb(255, 251, 191, 36)
                : Color.FromArgb(255, 180, 83, 9);
        }

        return dark
            ? Color.FromArgb(255, 96, 165, 250)
            : Color.FromArgb(255, 37, 99, 235);
    }

    private bool IsCountdownHeroModuleEnabled(CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetSettingDefinition? moduleDef = _viewModel.Manifest.Settings.FirstOrDefault(
            static d => string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));
        if (moduleDef is null)
        {
            return true;
        }

        ctx.Settings.TryGetValue("modules", out string? raw);
        return CustomWidgetModuleListHelper.IsModuleEnabled(raw, "hero", moduleDef.Options);
    }

    private bool IsCountdownListModuleEnabled(CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetSettingDefinition? moduleDef = _viewModel.Manifest.Settings.FirstOrDefault(
            static d => string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));
        if (moduleDef is null)
        {
            return false;
        }

        ctx.Settings.TryGetValue("modules", out string? raw);
        return CustomWidgetModuleListHelper.IsModuleEnabled(raw, "list", moduleDef.Options);
    }

    private static bool IsInsideChildButton(DependencyObject source, DependencyObject stopAt)
    {
        DependencyObject? cur = source;
        while (cur is not null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is Button)
            {
                return true;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
