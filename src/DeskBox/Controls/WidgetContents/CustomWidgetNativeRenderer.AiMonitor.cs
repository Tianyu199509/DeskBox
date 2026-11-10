using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private static CustomWidgetAiProviderSnapshot? FindActiveProvider(
        CustomWidgetAiMonitorSnapshot snap,
        string id) =>
        snap.ActiveProviders.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private FrameworkElement BuildAiFocusCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isStandaloneHero = false)
    {
        CustomWidgetAiMonitorSnapshot snap = ctx.AiMonitor;
        string cultureName = ctx.CultureName ?? _viewModel.ActiveCultureName;
        CustomWidgetAiProviderSnapshot? focus = CustomWidgetAiMonitorEngine.SelectFocusProvider(snap, ctx.State);
        bool showRemaining = CustomWidgetAiMonitorEngine.IsRemainingMode(ctx.Settings);
        bool hasDragGrip = !isStandaloneHero && CountEnabledAiModules(ctx) > 1;

        var rootStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 1. Top Segmented Provider Switcher + Quiet Subtle Action Buttons
        rootStack.Children.Add(BuildAiTopProviderBar(snap, focus, hasDragGrip, cultureName));

        // 2. Framed Focus Hero Surface Card
        if (focus is null)
        {
            rootStack.Children.Add(WrapInAiFocusSurface(BuildAiOnboardingEmptyCard(isStandaloneHero, cultureName)));
        }
        else if (focus.ErrorMessage is not null)
        {
            rootStack.Children.Add(BuildAiProviderErrorCard(focus, cultureName));
        }
        else if (focus.BillingKind == AiProviderBillingKind.WindowQuota)
        {
            rootStack.Children.Add(WrapInAiFocusSurface(
                BuildAiWindowTwinCards(focus, snap.LocalSummary, showRemaining, cultureName)));
        }
        else
        {
            rootStack.Children.Add(WrapInAiFocusSurface(BuildAiWalletHeroCard(focus)));
        }

        return rootStack;
    }

    private Border WrapInAiFocusSurface(FrameworkElement inner)
    {
        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(11, 9, 11, 9),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = CardStrokeBrush,
            BorderThickness = CardBorderThickness,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = inner
        };
    }

    private FrameworkElement BuildAiTopProviderBar(
        CustomWidgetAiMonitorSnapshot snap,
        CustomWidgetAiProviderSnapshot? focus,
        bool hasDragGrip,
        string cultureName)
    {
        var barGrid = new Grid
        {
            ColumnSpacing = 6,
            Margin = new Thickness(0, 0, 0, 1)
        };
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (snap.ActiveProviders.Count == 0)
        {
            var statusTb = new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Ai.NoProviderConnected", cultureName),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.Medium,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            Grid.SetColumn(statusTb, 0);
            barGrid.Children.Add(statusTb);
        }
        else
        {
            var segRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var pillEntries = new List<(string ProviderId, Color BrandColor, Border DotBorder, Button Button)>(
                snap.ActiveProviders.Count);

            foreach (CustomWidgetAiProviderSnapshot p in snap.ActiveProviders)
            {
                bool isSelected = focus is not null &&
                                  string.Equals(p.Id, focus.Id, StringComparison.OrdinalIgnoreCase);
                Color brandColor = ParseBrandHex(p.BrandHex);
                Color dotColor = p.Health is AiQuotaHealth.Warning or AiQuotaHealth.Critical
                    ? ResolveAiHealthColor(p.Health)
                    : brandColor;

                var itemContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var dotBorder = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = GetOrCreateSolidBrush(dotColor),
                    VerticalAlignment = VerticalAlignment.Center
                };
                itemContent.Children.Add(dotBorder);

                var labelTb = new TextBlock
                {
                    Text = p.ShortName,
                    FontSize = CaptionFontSize,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isSelected
                        ? ResolveForegroundBrush("default")
                        : ResolveForegroundBrush("secondary"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                itemContent.Children.Add(labelTb);

                var segBtn = new Button
                {
                    Height = 25,
                    MinHeight = 25,
                    MinWidth = 0,
                    Padding = new Thickness(8, 0, 9, 0),
                    CornerRadius = CornerSmall,
                    FontSize = CaptionFontSize,
                    Content = itemContent
                };
                ApplySegmentedItemState(segBtn, isSelected);
                string pid = p.Id;
                segBtn.Click += async (_, _) => await _viewModel.SelectAiProviderAsync(pid);
                ToolTipService.SetToolTip(segBtn, $"{p.Name} · {p.RadarValueText}");
                segRow.Children.Add(segBtn);
                pillEntries.Add((pid, brandColor, dotBorder, segBtn));
            }

            _liveMetricUpdaters.Add(liveCtx =>
            {
                foreach ((string pid, Color brandColor, Border dotBorder, Button segBtn) in pillEntries)
                {
                    if (FindActiveProvider(liveCtx.AiMonitor, pid) is { } lp)
                    {
                        Color liveDotColor = lp.Health is AiQuotaHealth.Warning or AiQuotaHealth.Critical
                            ? ResolveAiHealthColor(lp.Health)
                            : brandColor;
                        dotBorder.Background = GetOrCreateSolidBrush(liveDotColor);
                        ToolTipService.SetToolTip(segBtn, $"{lp.Name} · {lp.RadarValueText}");
                    }
                }
            });

            var segTrack = new Border
            {
                CornerRadius = CornerMedium,
                Padding = new Thickness(2),
                Background = ResolveCardBackground("subtle"),
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = segRow
            };

            var pillScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = segTrack
            };
            Grid.SetColumn(pillScroller, 0);
            barGrid.Children.Add(pillScroller);
        }

        // Right Action Buttons: Refresh + Key Vault (Subtle r=6 icon buttons)
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, hasDragGrip ? 22 : 0, 0)
        };

        Button refreshBtn = CreateSubtleIconButton(
            glyph: "\uE72C",
            size: 26,
            iconSize: 12,
            tooltip: Tr("CustomWidget.Renderer.Ai.RefreshNowTooltip", cultureName));
        refreshBtn.Click += async (_, _) => await _viewModel.RefreshAiMonitorNowAsync(showToast: true);
        actionsRow.Children.Add(refreshBtn);

        Button configBtn = CreateSubtleIconButton(
            glyph: "\uE8D7",
            size: 26,
            iconSize: 12,
            tooltip: Tr("CustomWidget.Renderer.Ai.ManageKeysTooltip", cultureName));
        string? defaultTab = focus?.Id;
        configBtn.Click += (_, _) => ShowAiCredentialsFlyout(configBtn, defaultTab);
        actionsRow.Children.Add(configBtn);

        Grid.SetColumn(actionsRow, 1);
        barGrid.Children.Add(actionsRow);

        return barGrid;
    }

    private FrameworkElement BuildAiOnboardingEmptyCard(bool isStandaloneHero, string cultureName)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12, isStandaloneHero ? 20 : 12, 12, isStandaloneHero ? 20 : 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new FontIcon
        {
            Glyph = "\uE950",
            FontSize = 22,
            Foreground = ResolveForegroundBrush("muted"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var titleTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.OnboardingTitle", cultureName),
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(titleTb);

        var subTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.OnboardingSubtitle", cultureName),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(subTb);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnRow.Children.Add(new FontIcon
        {
            Glyph = "\uE8D7",
            FontSize = 12,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var btnTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.ConfigureSourcesButton", cultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        btnRow.Children.Add(btnTb);

        var connectBtn = new Button
        {
            Height = 30,
            MinHeight = 30,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = btnRow
        };
        StyleStandardCommandButton(connectBtn, CornerMedium);
        connectBtn.Click += (_, _) => ShowAiCredentialsFlyout(connectBtn, CustomWidgetAiMonitorEngine.ProviderDeepSeek);
        stack.Children.Add(connectBtn);

        var host = new Grid();
        host.Children.Add(stack);
        return host;
    }

    private FrameworkElement BuildAiProviderErrorCard(CustomWidgetAiProviderSnapshot provider, string cultureName)
    {
        var card = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel { Spacing = 8 };

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftRow.Children.Add(BuildAiBrandMonogramBadge(provider.Monogram, provider.BrandHex, 20));
        var nameTb = new TextBlock
        {
            Text = provider.Name,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        leftRow.Children.Add(nameTb);
        Grid.SetColumn(leftRow, 0);
        topGrid.Children.Add(leftRow);

        FrameworkElement badge = BuildAiHealthPill(
            AiQuotaHealth.Warning,
            Tr("CustomWidget.Renderer.Ai.ConnectionIssue", cultureName));
        Grid.SetColumn(badge, 1);
        topGrid.Children.Add(badge);
        stack.Children.Add(topGrid);

        var errTb = new TextBlock
        {
            Text = provider.ErrorMessage ?? Tr("CustomWidget.Renderer.Ai.RequestFailed", cultureName),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(errTb);

        string pid = provider.Id;
        _liveMetricUpdaters.Add(liveCtx =>
        {
            if (FindActiveProvider(liveCtx.AiMonitor, pid) is { } lp)
            {
                errTb.Text = lp.ErrorMessage ?? Tr("CustomWidget.Renderer.Ai.RequestFailed", liveCtx.CultureName);
            }
        });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var editKeyBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Ai.EditKeyButton", cultureName),
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = CaptionFontSize
        };
        StyleStandardCommandButton(editKeyBtn, CornerSmall);
        editKeyBtn.Click += (_, _) => ShowAiCredentialsFlyout(editKeyBtn, pid);
        btnRow.Children.Add(editKeyBtn);

        var retryBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Ai.RetryNowButton", cultureName),
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = CaptionFontSize
        };
        StyleStandardCommandButton(retryBtn, CornerSmall);
        retryBtn.Click += async (_, _) => await _viewModel.RefreshAiMonitorNowAsync(showToast: true);
        btnRow.Children.Add(retryBtn);
        stack.Children.Add(btnRow);

        var cardHost = new Grid();
        cardHost.Children.Add(stack);
        card.Child = cardHost;
        return card;
    }

    private static Color ResolveWindowBarColor(
        CustomWidgetAiWindowQuota window,
        Color brandColor)
    {
        if (window.Health != AiQuotaHealth.Healthy)
        {
            return ResolveAiHealthColorStatic(window.Health);
        }

        if (window.UsedPercent <= 0.01)
        {
            return Color.FromArgb(116, brandColor.R, brandColor.G, brandColor.B);
        }

        return brandColor;
    }

    private static Color ResolveAiHealthColorStatic(AiQuotaHealth health) => health switch
    {
        AiQuotaHealth.Critical => Color.FromArgb(255, 239, 68, 68),
        AiQuotaHealth.Warning => Color.FromArgb(255, 245, 158, 11),
        _ => Color.FromArgb(255, 16, 185, 129)
    };

    private static string FormatFocusLocalTelemetryText(
        CustomWidgetAiProviderSnapshot provider,
        CustomWidgetAiLocalSummary local,
        string cultureName)
    {
        if (local.TodayTokens > 0)
        {
            string todayWord = LocalizationService.TranslateForCulture(cultureName, "CustomWidget.Renderer.Ai.TodayAxis");
            string tokText = CustomWidgetAiMonitorEngine.FormatCompactTokens(local.TodayTokens);
            string cacheText = LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Renderer.Ai.CacheHitFormat",
                local.CacheHitPercent.ToString("0", CultureInfo.InvariantCulture));
            return $"{todayWord} {tokText} · {cacheText}";
        }

        if (local.Last30DaysTokens > 0)
        {
            string last30Tok = LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Renderer.Ai.Last30DaysTokensFormat",
                CustomWidgetAiMonitorEngine.FormatCompactTokens(local.Last30DaysTokens));
            return $"{last30Tok} · ${local.Last30DaysCostUsd:0.00}";
        }

        return provider.UpdatedAt > DateTimeOffset.MinValue
            ? provider.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : LocalizationService.TranslateForCulture(cultureName, "CustomWidget.AiMonitor.QuotaHealthy");
    }

    private FrameworkElement BuildAiWindowTwinCards(
        CustomWidgetAiProviderSnapshot provider,
        CustomWidgetAiLocalSummary localSummary,
        bool showRemaining,
        string cultureName)
    {
        var outerStack = new StackPanel
        {
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var twinGrid = new Grid();
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        CustomWidgetAiWindowQuota w1 = provider.PrimaryWindow ??
            new CustomWidgetAiWindowQuota(
                Tr("CustomWidget.AiMonitor.Window5hLabel", cultureName),
                0,
                null,
                Tr("CustomWidget.AiMonitor.StandbyStartChat", cultureName),
                AiQuotaHealth.Healthy);
        CustomWidgetAiWindowQuota w2 = provider.SecondaryWindow ??
            new CustomWidgetAiWindowQuota(
                Tr("CustomWidget.AiMonitor.Window7dQuotaLabel", cultureName),
                0,
                null,
                Tr("CustomWidget.AiMonitor.QuotaHealthy", cultureName),
                AiQuotaHealth.Healthy);

        FrameworkElement colLeft = BuildSingleAiWindowTile(
            w1,
            showRemaining,
            provider.Id,
            provider.BrandHex,
            isPrimary: true,
            cultureName: cultureName);
        Grid.SetColumn(colLeft, 0);
        twinGrid.Children.Add(colLeft);

        var divider = new Border
        {
            Width = 1,
            Background = DividerBrush,
            Margin = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(divider, 1);
        twinGrid.Children.Add(divider);

        FrameworkElement colRight = BuildSingleAiWindowTile(
            w2,
            showRemaining,
            provider.Id,
            provider.BrandHex,
            isPrimary: false,
            cultureName: cultureName);
        Grid.SetColumn(colRight, 2);
        twinGrid.Children.Add(colRight);

        outerStack.Children.Add(twinGrid);

        // Embedded compact telemetry footer for local CLI providers (Claude Code / Codex)
        if (provider.IsLocalCli)
        {
            var footerGrid = new Grid
            {
                ColumnSpacing = 8
            };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftBadgeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftBadgeRow.Children.Add(BuildAiBrandMonogramBadge(provider.Monogram, provider.BrandHex, 16));

            var planTb = new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(provider.PlanBadge)
                    ? $"{provider.ShortName} · {provider.PlanBadge}"
                    : provider.Name,
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.Medium,
                Foreground = ResolveForegroundBrush("secondary"),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            leftBadgeRow.Children.Add(planTb);
            Grid.SetColumn(leftBadgeRow, 0);
            footerGrid.Children.Add(leftBadgeRow);

            var telemetryTb = new TextBlock
            {
                Text = FormatFocusLocalTelemetryText(provider, localSummary, cultureName),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetTabularNumerals(telemetryTb);
            Grid.SetColumn(telemetryTb, 1);
            footerGrid.Children.Add(telemetryTb);

            string pid = provider.Id;
            _liveMetricUpdaters.Add(liveCtx =>
            {
                if (FindActiveProvider(liveCtx.AiMonitor, pid) is { } lp)
                {
                    string liveCulture = liveCtx.CultureName ?? _viewModel.ActiveCultureName;
                    planTb.Text = !string.IsNullOrWhiteSpace(lp.PlanBadge)
                        ? $"{lp.ShortName} · {lp.PlanBadge}"
                        : lp.Name;
                    telemetryTb.Text = FormatFocusLocalTelemetryText(lp, liveCtx.AiMonitor.LocalSummary, liveCulture);
                }
            });

            outerStack.Children.Add(new Border
            {
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(2, 6, 2, 0),
                Child = footerGrid
            });
        }

        return outerStack;
    }

    private FrameworkElement BuildSingleAiWindowTile(
        CustomWidgetAiWindowQuota window,
        bool showRemaining,
        string providerId,
        string brandHex,
        bool isPrimary,
        string cultureName)
    {
        var stack = new StackPanel
        {
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Row 0: Window Title + Quiet-When-Healthy Alert Pill
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelTb = new TextBlock
        {
            Text = window.Label,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        Grid.SetColumn(labelTb, 0);
        topRow.Children.Add(labelTb);

        FrameworkElement pill = CreateLiveAiHealthPill(
            window.Health,
            h => h == AiQuotaHealth.Critical
                ? Tr("CustomWidget.Renderer.Ai.HealthCritical")
                : Tr("CustomWidget.Renderer.Ai.HealthTight"),
            out Action<AiQuotaHealth> updatePill);
        Grid.SetColumn(pill, 1);
        topRow.Children.Add(pill);
        stack.Children.Add(topRow);

        // Row 1: Display SemiBold Tabular Percentage + Mode Word
        string pctValue = CustomWidgetAiMonitorEngine.FormatWindowPercentText(
            window,
            showRemaining,
            out string modeWord,
            cultureName);
        string numericOnly = pctValue.TrimEnd('%');

        var numRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 1, 0, 1)
        };

        var bigDigitsTb = new TextBlock
        {
            Text = numericOnly,
            FontFamily = DisplayFontFamily,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = -12,
            Foreground = ResolveForegroundBrush("default"),
            TextLineBounds = TextLineBounds.Tight,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        SetTabularNumerals(bigDigitsTb);
        numRow.Children.Add(bigDigitsTb);

        var pctSymTb = new TextBlock
        {
            Text = "%",
            FontSize = BodyFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("secondary"),
            Margin = new Thickness(0, 0, 4, 1),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        numRow.Children.Add(pctSymTb);

        var modeTb = new TextBlock
        {
            Text = modeWord,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            Margin = new Thickness(0, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        numRow.Children.Add(modeTb);
        stack.Children.Add(numRow);

        // Row 2: Fine 3.5px Proportional Progress Bar (calm opacity when 0% used standby)
        double fillPercent = showRemaining ? window.RemainingPercent : window.UsedPercent;
        Color brandColor = ParseBrandHex(brandHex);
        Color barColor = ResolveWindowBarColor(window, brandColor);
        stack.Children.Add(CreateLiveProportionalBar(
            fillPercent,
            barColor,
            height: 3.5,
            out Action<double, Color> updateBar));

        // Row 3: Reset Countdown Footer (CaptionFontSize >= 11.5px)
        var resetTb = new TextBlock
        {
            Text = window.ResetText,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        SetTabularNumerals(resetTb);
        stack.Children.Add(resetTb);

        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        var tileBorder = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(3, 2, 3, 2),
            Background = transparent,
            Child = stack
        };
        string remWord = Tr("CustomWidget.AiMonitor.WordRemaining", cultureName);
        string usedWord = Tr("CustomWidget.AiMonitor.WordUsed", cultureName);
        ToolTipService.SetToolTip(tileBorder, $"{window.Label} ({remWord} / {usedWord})");

        tileBorder.PointerEntered += (_, _) => tileBorder.Background = SubtleHoverBrush;
        tileBorder.PointerExited += (_, _) => tileBorder.Background = transparent;
        tileBorder.Tapped += async (_, e) =>
        {
            e.Handled = true;
            CustomWidgetEvaluationContext nowCtx = _viewModel.CreateEvaluationContext();
            bool currentlyRemaining = CustomWidgetAiMonitorEngine.IsRemainingMode(nowCtx.Settings);
            await _viewModel.SetSettingValueAsync("quotaDisplayMode", currentlyRemaining ? "used" : "remaining");
        };

        _liveMetricUpdaters.Add(liveCtx =>
        {
            if (FindActiveProvider(liveCtx.AiMonitor, providerId) is not { } lp)
            {
                return;
            }

            CustomWidgetAiWindowQuota liveWindow = isPrimary
                ? (lp.PrimaryWindow ?? new CustomWidgetAiWindowQuota(
                    Tr("CustomWidget.AiMonitor.Window5hLabel", liveCtx.CultureName),
                    0,
                    null,
                    Tr("CustomWidget.AiMonitor.StandbyStartChat", liveCtx.CultureName),
                    AiQuotaHealth.Healthy))
                : (lp.SecondaryWindow ?? new CustomWidgetAiWindowQuota(
                    Tr("CustomWidget.AiMonitor.Window7dQuotaLabel", liveCtx.CultureName),
                    0,
                    null,
                    Tr("CustomWidget.AiMonitor.QuotaHealthy", liveCtx.CultureName),
                    AiQuotaHealth.Healthy));
            bool liveShowRemaining = CustomWidgetAiMonitorEngine.IsRemainingMode(liveCtx.Settings);

            labelTb.Text = liveWindow.Label;
            updatePill(liveWindow.Health);

            string livePctValue = CustomWidgetAiMonitorEngine.FormatWindowPercentText(
                liveWindow,
                liveShowRemaining,
                out string liveModeWord,
                liveCtx.CultureName);
            bigDigitsTb.Text = livePctValue.TrimEnd('%');
            modeTb.Text = liveModeWord;

            double liveFillPercent = liveShowRemaining ? liveWindow.RemainingPercent : liveWindow.UsedPercent;
            Color liveBarColor = ResolveWindowBarColor(liveWindow, brandColor);
            updateBar(liveFillPercent, liveBarColor);
            resetTb.Text = liveWindow.ResetText;
        });

        return tileBorder;
    }

    private FrameworkElement BuildAiWalletHeroCard(CustomWidgetAiProviderSnapshot provider)
    {
        var stack = new StackPanel
        {
            Spacing = 7,
            Padding = new Thickness(2, 4, 2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Top Row: Brand Monogram + Provider Name + Plan Tag + Quiet-When-Healthy Alert Pill
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftInfo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftInfo.Children.Add(BuildAiBrandMonogramBadge(provider.Monogram, provider.BrandHex, 20));

        var nameTb = new TextBlock
        {
            Text = provider.Name,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        leftInfo.Children.Add(nameTb);

        var tagTb = new TextBlock
        {
            Text = provider.PlanBadge ?? string.Empty,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("muted")
        };
        var tagHost = new Grid();
        tagHost.Children.Add(tagTb);

        var tagBorder = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(6, 1, 6, 1),
            Background = ResolveCardBackground("subtle"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrWhiteSpace(provider.PlanBadge)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Child = tagHost
        };
        leftInfo.Children.Add(tagBorder);

        Grid.SetColumn(leftInfo, 0);
        topGrid.Children.Add(leftInfo);

        FrameworkElement statusPill = CreateLiveAiHealthPill(
            provider.Health,
            h => h == AiQuotaHealth.Critical
                ? Tr("CustomWidget.Renderer.Ai.WalletTopUp")
                : Tr("CustomWidget.Renderer.Ai.WalletLow"),
            out Action<AiQuotaHealth> updateStatusPill);
        Grid.SetColumn(statusPill, 1);
        topGrid.Children.Add(statusPill);
        stack.Children.Add(topGrid);

        // Middle Row: Display SemiBold Tabular Currency Balance
        var amountRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 2, 0, 1)
        };
        var symTb = new TextBlock
        {
            Text = provider.CurrencySymbol,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };
        amountRow.Children.Add(symTb);

        var valTb = new TextBlock
        {
            Text = provider.WalletBalance.ToString("0.00", CultureInfo.InvariantCulture),
            FontFamily = DisplayFontFamily,
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = -14,
            Foreground = ResolveForegroundBrush("default"),
            TextLineBounds = TextLineBounds.Tight,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        SetTabularNumerals(valTb);
        amountRow.Children.Add(valTb);
        stack.Children.Add(amountRow);

        Color brandColor = ParseBrandHex(provider.BrandHex);
        Action<double, Color>? updateWalletBar = null;
        if (provider.WalletRemainingPercent is { } remPct)
        {
            Color c = provider.Health == AiQuotaHealth.Healthy
                ? brandColor
                : ResolveAiHealthColor(provider.Health);
            stack.Children.Add(CreateLiveProportionalBar(remPct, c, height: 3.5, out updateWalletBar));
        }

        // Bottom Row: Breakdown + Timestamp
        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var breakdownTb = new TextBlock
        {
            Text = provider.WalletBreakdown,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        SetTabularNumerals(breakdownTb);
        Grid.SetColumn(breakdownTb, 0);
        bottomGrid.Children.Add(breakdownTb);

        var timeTb = new TextBlock
        {
            Text = provider.UpdatedAt > DateTimeOffset.MinValue
                ? provider.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = provider.UpdatedAt > DateTimeOffset.MinValue
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        SetTabularNumerals(timeTb);
        Grid.SetColumn(timeTb, 1);
        bottomGrid.Children.Add(timeTb);

        stack.Children.Add(bottomGrid);

        string pid = provider.Id;
        _liveMetricUpdaters.Add(liveCtx =>
        {
            if (FindActiveProvider(liveCtx.AiMonitor, pid) is not { } lp)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(lp.PlanBadge))
            {
                tagBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                tagTb.Text = lp.PlanBadge;
                tagBorder.Visibility = Visibility.Visible;
            }

            updateStatusPill(lp.Health);
            symTb.Text = lp.CurrencySymbol;
            valTb.Text = lp.WalletBalance.ToString("0.00", CultureInfo.InvariantCulture);

            if (updateWalletBar is not null && lp.WalletRemainingPercent is { } liveRemPct)
            {
                Color liveColor = lp.Health == AiQuotaHealth.Healthy
                    ? brandColor
                    : ResolveAiHealthColor(lp.Health);
                updateWalletBar(liveRemPct, liveColor);
            }

            breakdownTb.Text = lp.WalletBreakdown;
            if (lp.UpdatedAt > DateTimeOffset.MinValue)
            {
                timeTb.Text = lp.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                timeTb.Visibility = Visibility.Visible;
            }
            else
            {
                timeTb.Visibility = Visibility.Collapsed;
            }
        });

        var walletHost = new Grid();
        walletHost.Children.Add(stack);
        return walletHost;
    }

    private FrameworkElement BuildAiRadarList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetAiMonitorSnapshot snap = ctx.AiMonitor;
        bool focusEnabled = IsAiModuleEnabled(ctx, "focus");
        bool hasDragGrip = CountEnabledAiModules(ctx) > 1;
        CustomWidgetAiProviderSnapshot? focus = CustomWidgetAiMonitorEngine.SelectFocusProvider(snap, ctx.State);
        bool showRemaining = CustomWidgetAiMonitorEngine.IsRemainingMode(ctx.Settings);

        IReadOnlyList<CustomWidgetAiProviderSnapshot> rows = focusEnabled && focus is not null
            ? snap.ActiveProviders.Where(p => !string.Equals(p.Id, focus.Id, StringComparison.OrdinalIgnoreCase)).ToList()
            : snap.ActiveProviders;

        var rootStack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var headerGrid = new Grid
        {
            Padding = new Thickness(2, 0, 0, 2)
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTb = new TextBlock
        {
            Text = focusEnabled
                ? Tr("CustomWidget.Renderer.Ai.OtherConnectedProviders", ctx.CultureName)
                : Tr("CustomWidget.Renderer.Ai.MultiProviderOverview", ctx.CultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerTb, 0);
        headerGrid.Children.Add(headerTb);

        if (!focusEnabled)
        {
            Button addBtn = CreateSubtleIconButton(
                glyph: "\uE710",
                size: 26,
                iconSize: 12,
                tooltip: Tr("CustomWidget.Renderer.Ai.AddMoreProvidersTooltip", ctx.CultureName));
            addBtn.Margin = new Thickness(0, 0, hasDragGrip ? 22 : 0, 0);
            addBtn.Click += (_, _) => ShowAiCredentialsFlyout(addBtn, CustomWidgetAiMonitorEngine.ProviderDeepSeek);
            Grid.SetColumn(addBtn, 1);
            headerGrid.Children.Add(addBtn);
        }
        else if (rows.Count > 0)
        {
            var metaTb = new TextBlock
            {
                Text = snap.UpdatedAt > DateTimeOffset.MinValue
                    ? snap.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    : rows.Count.ToString(CultureInfo.InvariantCulture),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, hasDragGrip ? 22 : 2, 0)
            };
            SetTabularNumerals(metaTb);
            _liveMetricUpdaters.Add(liveCtx =>
            {
                if (liveCtx.AiMonitor.UpdatedAt > DateTimeOffset.MinValue)
                {
                    metaTb.Text = liveCtx.AiMonitor.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                }
            });
            Grid.SetColumn(metaTb, 1);
            headerGrid.Children.Add(metaTb);
        }
        rootStack.Children.Add(headerGrid);

        if (rows.Count == 0)
        {
            var hintTb = new TextBlock
            {
                Text = snap.ActiveProviders.Count == 0
                    ? Tr("CustomWidget.Renderer.Ai.RadarEmptyAllHint", ctx.CultureName)
                    : Tr("CustomWidget.Renderer.Ai.RadarEmptyMoreHint", ctx.CultureName),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var hintHost = new Grid();
            hintHost.Children.Add(hintTb);

            var hintBorder = new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(12, 8, 12, 8),
                Background = ResolveCardBackground("subtle"),
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(1),
                Child = hintHost
            };
            rootStack.Children.Add(hintBorder);
        }
        else
        {
            foreach (CustomWidgetAiProviderSnapshot item in rows)
            {
                rootStack.Children.Add(BuildAiRadarRow(item, showRemaining, ctx.CultureName ?? _viewModel.ActiveCultureName));
            }
        }

        return rootStack;
    }

    private FrameworkElement BuildAiRadarRow(
        CustomWidgetAiProviderSnapshot item,
        bool showRemaining,
        string cultureName)
    {
        Brush restBg = ResolveCardBackground("default");
        Brush hoverBg = SubtleHoverBrush;

        var rowBorder = new Border
        {
            MinHeight = 42,
            CornerRadius = CornerSmall,
            Padding = new Thickness(10, 6, 10, 6),
            Background = restBg,
            BorderThickness = CardBorderThickness,
            BorderBrush = CardStrokeBrush
        };

        var grid = new Grid
        {
            ColumnSpacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement badge = BuildAiBrandMonogramBadge(item.Monogram, item.BrandHex, 22);
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var textCol = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleTb = new TextBlock
        {
            Text = item.Name,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("default"),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textCol.Children.Add(titleTb);

        var subTb = new TextBlock
        {
            Text = item.RadarSubtext,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(subTb);
        textCol.Children.Add(subTb);
        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        // Right metric + 6px semantic health dot
        string rightText = item.BillingKind == AiProviderBillingKind.WindowQuota && item.PrimaryWindow is { } pw
            ? CustomWidgetAiMonitorEngine.FormatWindowPercentText(pw, showRemaining, out _, cultureName)
            : item.RadarValueText;

        var rightRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var valTb = new TextBlock
        {
            Text = rightText,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(valTb);
        rightRow.Children.Add(valTb);

        var dotBorder = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = GetOrCreateSolidBrush(ResolveAiHealthColor(item.Health)),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightRow.Children.Add(dotBorder);
        Grid.SetColumn(rightRow, 2);
        grid.Children.Add(rightRow);

        string pid = item.Id;
        _liveMetricUpdaters.Add(liveCtx =>
        {
            if (FindActiveProvider(liveCtx.AiMonitor, pid) is { } lp)
            {
                bool liveShowRemaining = CustomWidgetAiMonitorEngine.IsRemainingMode(liveCtx.Settings);
                subTb.Text = lp.RadarSubtext;
                valTb.Text = lp.BillingKind == AiProviderBillingKind.WindowQuota && lp.PrimaryWindow is { } livePw
                    ? CustomWidgetAiMonitorEngine.FormatWindowPercentText(livePw, liveShowRemaining, out _, liveCtx.CultureName)
                    : lp.RadarValueText;
                dotBorder.Background = GetOrCreateSolidBrush(ResolveAiHealthColor(lp.Health));
            }
        });

        rowBorder.Child = grid;
        rowBorder.PointerEntered += (_, _) => rowBorder.Background = hoverBg;
        rowBorder.PointerExited += (_, _) => rowBorder.Background = restBg;
        rowBorder.Tapped += async (_, e) =>
        {
            e.Handled = true;
            await _viewModel.SelectAiProviderAsync(pid);
        };

        var menu = new MenuFlyout();
        var focusItem = new MenuFlyoutItem
        {
            Text = Tr("CustomWidget.Renderer.Ai.PinToFocusMenu", cultureName),
            Icon = new FontIcon { Glyph = "\uE718" }
        };
        focusItem.Click += async (_, _) => await _viewModel.SelectAiProviderAsync(pid);
        menu.Items.Add(focusItem);

        var cfgItem = new MenuFlyoutItem
        {
            Text = Tr("CustomWidget.Renderer.Ai.ConfigureProviderMenu", cultureName),
            Icon = new FontIcon { Glyph = "\uE8D7" }
        };
        cfgItem.Click += (_, _) =>
        {
            menu.Hide();
            DeskBox.Platform.UiDispatch.PostAfterCallback(() => ShowAiCredentialsFlyout(rowBorder, pid));
        };
        menu.Items.Add(cfgItem);
        TrackInteractiveFlyout(menu);
        rowBorder.ContextFlyout = menu;

        return rowBorder;
    }

    private FrameworkElement BuildAiTokensCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetAiLocalSummary local = ctx.AiMonitor.LocalSummary;
        bool hasPrecedingSection = IsAiModuleEnabled(ctx, "focus") || IsAiModuleEnabled(ctx, "radar");

        var twinGrid = new Grid
        {
            Padding = new Thickness(2, 2, 2, 2)
        };
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        twinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: Cost Usage
        var costStack = new StackPanel { Spacing = 3 };
        var costTitle = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.TodayEquivalentCost", ctx.CultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };
        costStack.Children.Add(costTitle);

        var costVal = new TextBlock
        {
            Text = $"${local.TodayCostUsd:0.00}",
            FontFamily = DisplayFontFamily,
            FontSize = MetricFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextLineBounds = TextLineBounds.Tight,
            Margin = new Thickness(0, 2, 0, 1)
        };
        SetTabularNumerals(costVal);
        costStack.Children.Add(costVal);

        var costSub = new TextBlock
        {
            Text = TrFormat(
                "CustomWidget.Renderer.Ai.Last30DaysCostFormat",
                ctx.CultureName,
                FormattableString.Invariant($"${local.Last30DaysCostUsd:0.00}")),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted")
        };
        SetTabularNumerals(costSub);
        costStack.Children.Add(costSub);
        Grid.SetColumn(costStack, 0);
        twinGrid.Children.Add(costStack);

        var divider = new Border
        {
            Width = 1,
            Background = DividerBrush,
            Margin = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(divider, 1);
        twinGrid.Children.Add(divider);

        // Right: Token Throughput + Prompt Cache Hit %
        var tokStack = new StackPanel { Spacing = 3 };
        var tokTitle = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.TodayTokenThroughput", ctx.CultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };
        tokStack.Children.Add(tokTitle);

        var tokVal = new TextBlock
        {
            Text = CustomWidgetAiMonitorEngine.FormatCompactTokens(local.TodayTokens),
            FontFamily = DisplayFontFamily,
            FontSize = MetricFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextLineBounds = TextLineBounds.Tight,
            Margin = new Thickness(0, 2, 0, 1)
        };
        SetTabularNumerals(tokVal);
        tokStack.Children.Add(tokVal);

        var tokSub = new TextBlock
        {
            Text = local.TodayTokens > 0
                ? TrFormat(
                    "CustomWidget.Renderer.Ai.CacheHitFormat",
                    ctx.CultureName,
                    local.CacheHitPercent.ToString("0", CultureInfo.InvariantCulture))
                : TrFormat(
                    "CustomWidget.Renderer.Ai.Last30DaysTokensFormat",
                    ctx.CultureName,
                    CustomWidgetAiMonitorEngine.FormatCompactTokens(local.Last30DaysTokens)),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted")
        };
        SetTabularNumerals(tokSub);
        tokStack.Children.Add(tokSub);
        Grid.SetColumn(tokStack, 2);
        twinGrid.Children.Add(tokStack);

        _liveMetricUpdaters.Add(liveCtx =>
        {
            CustomWidgetAiLocalSummary liveLocal = liveCtx.AiMonitor.LocalSummary;
            costVal.Text = $"${liveLocal.TodayCostUsd:0.00}";
            costSub.Text = TrFormat(
                "CustomWidget.Renderer.Ai.Last30DaysCostFormat",
                liveCtx.CultureName,
                FormattableString.Invariant($"${liveLocal.Last30DaysCostUsd:0.00}"));
            tokVal.Text = CustomWidgetAiMonitorEngine.FormatCompactTokens(liveLocal.TodayTokens);
            tokSub.Text = liveLocal.TodayTokens > 0
                ? TrFormat(
                    "CustomWidget.Renderer.Ai.CacheHitFormat",
                    liveCtx.CultureName,
                    liveLocal.CacheHitPercent.ToString("0", CultureInfo.InvariantCulture))
                : TrFormat(
                    "CustomWidget.Renderer.Ai.Last30DaysTokensFormat",
                    liveCtx.CultureName,
                    CustomWidgetAiMonitorEngine.FormatCompactTokens(liveLocal.Last30DaysTokens));
        });

        if (!hasPrecedingSection)
        {
            return twinGrid;
        }

        return new Border
        {
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Child = twinGrid
        };
    }

    private FrameworkElement BuildAiTrendCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetAiLocalSummary local = ctx.AiMonitor.LocalSummary;
        bool showCost = ctx.State.TryGetValue(CustomWidgetAiMonitorEngine.KeyTrendMetric, out string? m) &&
                        string.Equals(m, "cost", StringComparison.OrdinalIgnoreCase);
        bool hasPrecedingSection = IsAiModuleEnabled(ctx, "focus") ||
                                   IsAiModuleEnabled(ctx, "radar") ||
                                   IsAiModuleEnabled(ctx, "tokens");
        bool hasDragGrip = CountEnabledAiModules(ctx) > 1;

        var stack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(2, 0, 2, 2)
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Ai.Last14DaysTrend", ctx.CultureName),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 0);
        header.Children.Add(titleTb);

        // Segmented toggle: Tokens | Cost (r=6 track / r=4 items)
        var segRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };

        var tokBtn = new Button
        {
            Content = "Tokens",
            Height = 24,
            MinHeight = 24,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = CornerSmall,
            FontSize = CaptionFontSize
        };
        ApplySegmentedItemState(tokBtn, !showCost);
        tokBtn.Click += async (_, _) =>
        {
            if (showCost)
            {
                await _viewModel.ToggleAiTrendMetricAsync();
            }
        };
        segRow.Children.Add(tokBtn);

        var costBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Ai.CostTab", ctx.CultureName),
            Height = 24,
            MinHeight = 24,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = CornerSmall,
            FontSize = CaptionFontSize
        };
        ApplySegmentedItemState(costBtn, showCost);
        costBtn.Click += async (_, _) =>
        {
            if (!showCost)
            {
                await _viewModel.ToggleAiTrendMetricAsync();
            }
        };
        segRow.Children.Add(costBtn);

        var segTrack = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, hasDragGrip ? 22 : 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = segRow
        };
        Grid.SetColumn(segTrack, 1);
        header.Children.Add(segTrack);
        stack.Children.Add(header);

        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        IReadOnlyList<CustomWidgetAiDailyBucket> buckets = local.Last14Days;
        var barsGrid = new Grid
        {
            Height = 52,
            ColumnSpacing = 4,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        double maxVal = 0;
        foreach (CustomWidgetAiDailyBucket b in buckets)
        {
            double v = showCost ? b.CostUsd : b.TotalTokens;
            if (v > maxVal) maxVal = v;
        }

        Color activeBarColor = IsDark
            ? Color.FromArgb(235, 96, 165, 250)
            : Color.FromArgb(225, 37, 99, 235);
        Color pastBarColor = IsDark
            ? Color.FromArgb(140, 96, 165, 250)
            : Color.FromArgb(135, 37, 99, 235);
        Color emptyBarColor = IsDark
            ? Color.FromArgb(18, 255, 255, 255)
            : Color.FromArgb(14, 0, 0, 0);

        var slotViews = new (Grid Slot, Border Bar)[buckets.Count];

        for (int i = 0; i < buckets.Count; i++)
        {
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            CustomWidgetAiDailyBucket b = buckets[i];
            double rawVal = showCost ? b.CostUsd : b.TotalTokens;
            double ratio = maxVal > 0 ? Math.Clamp(rawVal / maxVal, 0.0, 1.0) : 0.0;
            double barHeight = rawVal <= 0 ? 3 : Math.Max(5, Math.Round(ratio * 50.0));
            bool isToday = i == buckets.Count - 1;

            var slot = new Grid
            {
                Background = transparent,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var bar = new Border
            {
                Height = barHeight,
                CornerRadius = new CornerRadius(2, 2, 1, 1),
                Background = GetOrCreateSolidBrush(rawVal <= 0
                    ? emptyBarColor
                    : isToday ? activeBarColor : pastBarColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            slot.Children.Add(bar);
            ToolTipService.SetToolTip(
                slot,
                $"{b.Date:MM-dd} · {CustomWidgetAiMonitorEngine.FormatCompactTokens(b.TotalTokens)} Tokens · ${b.CostUsd:0.00}");
            Grid.SetColumn(slot, i);
            barsGrid.Children.Add(slot);
            slotViews[i] = (slot, bar);
        }

        stack.Children.Add(barsGrid);

        TextBlock? startTb = null;
        if (buckets.Count >= 2)
        {
            var axisGrid = new Grid();
            axisGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            axisGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            startTb = new TextBlock
            {
                Text = buckets[0].Date.ToString("MM-dd", CultureInfo.InvariantCulture),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted")
            };
            SetTabularNumerals(startTb);
            Grid.SetColumn(startTb, 0);
            axisGrid.Children.Add(startTb);

            var endTb = new TextBlock
            {
                Text = Tr("CustomWidget.Renderer.Ai.TodayAxis", ctx.CultureName),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted")
            };
            Grid.SetColumn(endTb, 1);
            axisGrid.Children.Add(endTb);
            stack.Children.Add(axisGrid);
        }

        _liveMetricUpdaters.Add(liveCtx =>
        {
            CustomWidgetAiLocalSummary liveLocal = liveCtx.AiMonitor.LocalSummary;
            IReadOnlyList<CustomWidgetAiDailyBucket> liveBuckets = liveLocal.Last14Days;
            if (liveBuckets.Count != slotViews.Length)
            {
                return;
            }

            bool liveShowCost = liveCtx.State.TryGetValue(CustomWidgetAiMonitorEngine.KeyTrendMetric, out string? lm) &&
                                string.Equals(lm, "cost", StringComparison.OrdinalIgnoreCase);

            double liveMaxVal = 0;
            foreach (CustomWidgetAiDailyBucket lb in liveBuckets)
            {
                double lv = liveShowCost ? lb.CostUsd : lb.TotalTokens;
                if (lv > liveMaxVal) liveMaxVal = lv;
            }

            Color liveActiveColor = IsDark
                ? Color.FromArgb(235, 96, 165, 250)
                : Color.FromArgb(225, 37, 99, 235);
            Color livePastColor = IsDark
                ? Color.FromArgb(140, 96, 165, 250)
                : Color.FromArgb(135, 37, 99, 235);
            Color liveEmptyColor = IsDark
                ? Color.FromArgb(18, 255, 255, 255)
                : Color.FromArgb(14, 0, 0, 0);

            for (int i = 0; i < slotViews.Length; i++)
            {
                CustomWidgetAiDailyBucket lb = liveBuckets[i];
                double rawVal = liveShowCost ? lb.CostUsd : lb.TotalTokens;
                double ratio = liveMaxVal > 0 ? Math.Clamp(rawVal / liveMaxVal, 0.0, 1.0) : 0.0;
                double barHeight = rawVal <= 0 ? 3 : Math.Max(5, Math.Round(ratio * 50.0));
                bool isToday = i == slotViews.Length - 1;

                (Grid slot, Border bar) = slotViews[i];
                bar.Height = barHeight;
                bar.Background = GetOrCreateSolidBrush(rawVal <= 0
                    ? liveEmptyColor
                    : isToday ? liveActiveColor : livePastColor);
                ToolTipService.SetToolTip(
                    slot,
                    $"{lb.Date:MM-dd} · {CustomWidgetAiMonitorEngine.FormatCompactTokens(lb.TotalTokens)} Tokens · ${lb.CostUsd:0.00}");
            }

            if (startTb is not null && liveBuckets.Count >= 2)
            {
                startTb.Text = liveBuckets[0].Date.ToString("MM-dd", CultureInfo.InvariantCulture);
            }
        });

        if (!hasPrecedingSection)
        {
            return stack;
        }

        return new Border
        {
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Child = stack
        };
    }

    private void ShowAiCredentialsFlyout(FrameworkElement anchor, string? initialProviderId)
    {
        IntPtr ownerHwnd = DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(anchor);
        const string logicalKey = "ai-credentials";
        if (DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(ownerHwnd, logicalKey, anchor))
        {
            return;
        }

        string flyoutCulture = _viewModel.ActiveCultureName;
        var catalog = CustomWidgetAiMonitorEngine.GetCatalog(flyoutCulture);

        string activeTabId = string.IsNullOrWhiteSpace(initialProviderId)
            ? CustomWidgetAiMonitorEngine.ProviderClaude
            : initialProviderId;

        string flyoutTitle = Tr("CustomWidget.Renderer.Ai.FlyoutTitle", flyoutCulture);
        var popover = new DeskBox.Views.WidgetEditorPopoverWindow(
            ownerHwnd,
            flyoutTitle);

        var root = new StackPanel
        {
            Width = 296,
            Spacing = 12
        };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTb = new TextBlock
        {
            Text = flyoutTitle,
            FontSize = SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveForegroundBrush("default")
        };
        headerGrid.Children.Add(headerTb);

        Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
            Tr("Common.Close", flyoutCulture),
            () => popover.ClosePopover(restoreOwnerForeground: true),
            _themeScope);
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        root.Children.Add(headerGrid);

        // 1. 3x2 Segmented Provider Grid (r=6 track / r=4 items, zero cyan pills)
        var tabGrid = new Grid
        {
            RowSpacing = 3,
            ColumnSpacing = 3
        };
        for (int c = 0; c < 3; c++)
        {
            tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var bodyHost = new StackPanel { Spacing = 10 };
        PasswordBox? activePasswordBox = null;

        void RebuildProviderBody()
        {
            activePasswordBox = null;
            foreach ((string id, Button btn) in tabButtons)
            {
                ApplySegmentedItemState(btn, string.Equals(id, activeTabId, StringComparison.OrdinalIgnoreCase));
            }

            bodyHost.Children.Clear();
            var meta = catalog.FirstOrDefault(
                c => string.Equals(c.Id, activeTabId, StringComparison.OrdinalIgnoreCase));
            AiProviderCredentialEntry? existing = CustomWidgetAiCredentialStore.Shared.Get(activeTabId);

            if (meta.IsLocalCli)
            {
                bool isEnabled = existing?.Enabled ?? true;
                string selectedTier = existing?.PlanTier ?? "pro";
                int detectedFiles = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase)
                    ? CustomWidgetAiMonitorSource.Shared.Latest.LocalSummary.DetectedClaudeFiles
                    : CustomWidgetAiMonitorSource.Shared.Latest.LocalSummary.DetectedCodexFiles;

                string folderHint = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase)
                    ? @"~/.claude/projects"
                    : @"~/.codex/sessions";

                var statusBox = new Border
                {
                    CornerRadius = CornerSmall,
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = ResolveCardBackground("subtle"),
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = detectedFiles > 0
                            ? TrFormat("CustomWidget.Renderer.Ai.LocalCliDetectedFormat", flyoutCulture, detectedFiles, folderHint)
                            : TrFormat("CustomWidget.Renderer.Ai.LocalCliMissingFormat", flyoutCulture, folderHint, meta.Name),
                        FontSize = CaptionFontSize,
                        Foreground = ResolveForegroundBrush("secondary"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                bodyHost.Children.Add(statusBox);

                if (activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase))
                {
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = Tr("CustomWidget.Renderer.Ai.PlanTierCalibration", flyoutCulture),
                        FontSize = CaptionFontSize,
                        Foreground = ResolveForegroundBrush("muted")
                    });

                    var tierRow = new Grid { ColumnSpacing = 2 };
                    (string Val, string Label)[] tiers =
                    [
                        ("pro", "Pro"),
                        ("max5x", "Max 5x"),
                        ("max20x", "Max 20x")
                    ];
                    var tierBtns = new List<(string Val, Button Btn)>();
                    for (int i = 0; i < tiers.Length; i++)
                    {
                        tierRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        string tv = tiers[i].Val;
                        var tb = new Button
                        {
                            Content = tiers[i].Label,
                            Height = 28,
                            MinHeight = 28,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            CornerRadius = CornerSmall,
                            FontSize = CaptionFontSize
                        };
                        ApplySegmentedItemState(tb, string.Equals(selectedTier, tv, StringComparison.OrdinalIgnoreCase));
                        tb.Click += async (_, _) =>
                        {
                            selectedTier = tv;
                            foreach (var pair in tierBtns)
                            {
                                ApplySegmentedItemState(pair.Btn, pair.Val == selectedTier);
                            }
                            await _viewModel.SaveAndTestAiCredentialAsync(
                                new AiProviderCredentialEntry(activeTabId, string.Empty, "cn", selectedTier, isEnabled));
                        };
                        tierBtns.Add((tv, tb));
                        Grid.SetColumn(tb, i);
                        tierRow.Children.Add(tb);
                    }

                    bodyHost.Children.Add(new Border
                    {
                        CornerRadius = CornerMedium,
                        Padding = new Thickness(2),
                        Background = ResolveCardBackground("subtle"),
                        BorderBrush = DividerBrush,
                        BorderThickness = new Thickness(1),
                        Child = tierRow
                    });
                }

                var toggleVisBtn = new Button
                {
                    Content = isEnabled
                        ? Tr("CustomWidget.Renderer.Ai.HideProviderButton", flyoutCulture)
                        : Tr("CustomWidget.Renderer.Ai.ShowProviderButton", flyoutCulture),
                    Height = 30,
                    MinHeight = 30,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = CaptionFontSize
                };
                StyleStandardCommandButton(toggleVisBtn, CornerMedium);
                toggleVisBtn.Click += async (_, _) =>
                {
                    isEnabled = !isEnabled;
                    await _viewModel.SaveAndTestAiCredentialAsync(
                        new AiProviderCredentialEntry(activeTabId, string.Empty, "cn", selectedTier, isEnabled));
                    RebuildProviderBody();
                };
                bodyHost.Children.Add(toggleVisBtn);
                popover.RefreshBoundsFromContent();
                return;
            }

            // Cloud API Key Provider Form
            string selectedRegion = existing?.Region ?? "cn";
            bool hasRegionChoice = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGlm, StringComparison.OrdinalIgnoreCase) ||
                                   activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderKimi, StringComparison.OrdinalIgnoreCase);

            if (hasRegionChoice)
            {
                var regGrid = new Grid { ColumnSpacing = 2 };
                regGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                regGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                string cnLabel = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGlm, StringComparison.OrdinalIgnoreCase)
                    ? Tr("CustomWidget.Renderer.Ai.RegionGlmCn", flyoutCulture)
                    : Tr("CustomWidget.Renderer.Ai.RegionKimiCn", flyoutCulture);
                string intlLabel = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGlm, StringComparison.OrdinalIgnoreCase)
                    ? Tr("CustomWidget.Renderer.Ai.RegionGlmIntl", flyoutCulture)
                    : Tr("CustomWidget.Renderer.Ai.RegionKimiIntl", flyoutCulture);

                var cnBtn = new Button
                {
                    Content = cnLabel,
                    Height = 28,
                    MinHeight = 28,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = CornerSmall,
                    FontSize = CaptionFontSize
                };
                var intlBtn = new Button
                {
                    Content = intlLabel,
                    Height = 28,
                    MinHeight = 28,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = CornerSmall,
                    FontSize = CaptionFontSize
                };
                ApplySegmentedItemState(cnBtn, selectedRegion != "intl");
                ApplySegmentedItemState(intlBtn, selectedRegion == "intl");

                cnBtn.Click += (_, _) =>
                {
                    selectedRegion = "cn";
                    ApplySegmentedItemState(cnBtn, true);
                    ApplySegmentedItemState(intlBtn, false);
                };
                intlBtn.Click += (_, _) =>
                {
                    selectedRegion = "intl";
                    ApplySegmentedItemState(cnBtn, false);
                    ApplySegmentedItemState(intlBtn, true);
                };
                Grid.SetColumn(cnBtn, 0);
                Grid.SetColumn(intlBtn, 1);
                regGrid.Children.Add(cnBtn);
                regGrid.Children.Add(intlBtn);

                bodyHost.Children.Add(new Border
                {
                    CornerRadius = CornerMedium,
                    Padding = new Thickness(2),
                    Background = ResolveCardBackground("subtle"),
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(1),
                    Child = regGrid
                });
            }

            string masked = CustomWidgetAiCredentialStore.MaskApiKey(existing?.ApiKey);
            var keyStatusTb = new TextBlock
            {
                Text = string.IsNullOrEmpty(masked)
                    ? TrFormat("CustomWidget.Renderer.Ai.PasteKeyHintFormat", flyoutCulture, meta.Name)
                    : TrFormat("CustomWidget.Renderer.Ai.EncryptedSavedKeyFormat", flyoutCulture, masked),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                TextWrapping = TextWrapping.Wrap
            };
            bodyHost.Children.Add(keyStatusTb);

            var pwdBox = new PasswordBox
            {
                PlaceholderText = string.IsNullOrEmpty(masked)
                    ? "sk-..."
                    : Tr("CustomWidget.Renderer.Ai.OverwriteKeyPlaceholder", flyoutCulture),
                MaxLength = 256,
                CornerRadius = CornerSmall,
                FontSize = BodyFontSize
            };
            activePasswordBox = pwdBox;
            bodyHost.Children.Add(pwdBox);

            var feedbackTb = new TextBlock
            {
                FontSize = CaptionFontSize,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            bodyHost.Children.Add(feedbackTb);

            var actionGrid = new Grid { ColumnSpacing = 8 };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!string.IsNullOrEmpty(masked))
            {
                actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var saveBtn = new Button
            {
                Content = Tr("CustomWidget.Renderer.Ai.VerifyAndSaveButton", flyoutCulture),
                Height = 32,
                MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = CaptionFontSize
            };
            StyleAccentCommitButton(saveBtn);

            async Task CommitApiKeySaveAsync()
            {
                if (!saveBtn.IsEnabled)
                {
                    return;
                }

                string typed = pwdBox.Password.Trim();
                string effectiveKey = !string.IsNullOrEmpty(typed) ? typed : (existing?.ApiKey ?? string.Empty);
                if (string.IsNullOrEmpty(effectiveKey))
                {
                    feedbackTb.Text = Tr("CustomWidget.Renderer.Ai.EmptyKeyError", flyoutCulture);
                    feedbackTb.Foreground = ResolveForegroundBrush("danger");
                    feedbackTb.Visibility = Visibility.Visible;
                    popover.RefreshBoundsFromContent();
                    return;
                }

                saveBtn.IsEnabled = false;
                saveBtn.Content = Tr("CustomWidget.Renderer.Ai.VerifyingButton", flyoutCulture);
                var (ok, msg) = await _viewModel.SaveAndTestAiCredentialAsync(
                    new AiProviderCredentialEntry(activeTabId, effectiveKey, selectedRegion, "pro", Enabled: true));
                saveBtn.IsEnabled = true;
                saveBtn.Content = Tr("CustomWidget.Renderer.Ai.VerifyAndSaveButton", flyoutCulture);

                feedbackTb.Text = msg;
                feedbackTb.Foreground = ResolveForegroundBrush(ok ? "emerald" : "warning");
                feedbackTb.Visibility = Visibility.Visible;
                if (ok)
                {
                    pwdBox.Password = string.Empty;
                    existing = CustomWidgetAiCredentialStore.Shared.Get(activeTabId);
                    keyStatusTb.Text = TrFormat(
                        "CustomWidget.Renderer.Ai.EncryptedSavedKeyFormat",
                        flyoutCulture,
                        CustomWidgetAiCredentialStore.MaskApiKey(effectiveKey));
                }

                popover.RefreshBoundsFromContent();
            }

            saveBtn.Click += async (_, _) => await CommitApiKeySaveAsync();
            pwdBox.KeyDown += async (_, ke) =>
            {
                if (ke.Key == Windows.System.VirtualKey.Enter &&
                    !DeskBox.Views.WidgetEditorPopoverWindow.IsImeCompositionKeyEvent(ke))
                {
                    ke.Handled = true;
                    await CommitApiKeySaveAsync();
                }
            };
            Grid.SetColumn(saveBtn, 0);
            actionGrid.Children.Add(saveBtn);

            if (!string.IsNullOrEmpty(masked))
            {
                var clearBtn = new Button
                {
                    Content = Tr("CustomWidget.Renderer.Ai.ClearButton", flyoutCulture),
                    Height = 32,
                    MinHeight = 32,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = CaptionFontSize
                };
                StyleStandardCommandButton(clearBtn, CornerMedium);
                clearBtn.Click += async (_, _) =>
                {
                    await _viewModel.RemoveAiCredentialAsync(activeTabId);
                    RebuildProviderBody();
                };
                Grid.SetColumn(clearBtn, 1);
                actionGrid.Children.Add(clearBtn);
            }

            bodyHost.Children.Add(actionGrid);
            popover.RefreshBoundsFromContent();
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            var cat = catalog[i];
            var tabBtn = new Button
            {
                Content = cat.ShortName,
                Height = 28,
                MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize
            };
            string cid = cat.Id;
            tabBtn.Click += (_, _) =>
            {
                activeTabId = cid;
                RebuildProviderBody();
                if (activePasswordBox is PasswordBox targetPwdBox)
                {
                    DeskBox.Platform.UiDispatch.PostAfterCallback(() => targetPwdBox.Focus(FocusState.Programmatic));
                }
            };
            tabButtons[cid] = tabBtn;
            Grid.SetRow(tabBtn, i / 3);
            Grid.SetColumn(tabBtn, i % 3);
            tabGrid.Children.Add(tabBtn);
        }

        root.Children.Add(new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(3),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Child = tabGrid
        });
        root.Children.Add(bodyHost);
        RebuildProviderBody();

        ShowTrackedPopoverAt(
            popover,
            anchor,
            root,
            DeskBox.Views.WidgetPopoverPlacement.Bottom,
            minWidthDip: 326,
            maxWidthDip: 348,
            initialFocusTarget: activePasswordBox,
            logScope: "AiCredentialsEditor",
            logicalKey: logicalKey);
    }

    private FrameworkElement BuildAiBrandMonogramBadge(string monogram, string brandHex, double size)
    {
        Color c = ParseBrandHex(brandHex);
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = CornerSmall,
            Background = GetOrCreateSolidBrush(Color.FromArgb((byte)(IsDark ? 36 : 24), c.R, c.G, c.B)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var tb = new TextBlock
        {
            Text = monogram,
            FontSize = Math.Max(11.0, Math.Round(size * 0.48)),
            FontWeight = FontWeights.SemiBold,
            Foreground = GetOrCreateSolidBrush(c),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight
        };
        WidgetTextShadow.SetCast(tb, false);
        border.Child = tb;
        return border;
    }

    private FrameworkElement BuildAiHealthPill(AiQuotaHealth health, string text)
    {
        Color fg = ResolveAiHealthColor(health);
        Color bg = Color.FromArgb((byte)(IsDark ? 34 : 24), fg.R, fg.G, fg.B);

        var pill = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Background = GetOrCreateSolidBrush(bg),
            VerticalAlignment = VerticalAlignment.Center
        };

        var tb = new TextBlock
        {
            Text = text,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetOrCreateSolidBrush(fg),
            VerticalAlignment = VerticalAlignment.Center
        };
        var tbHost = new Grid();
        tbHost.Children.Add(tb);
        pill.Child = tbHost;
        return pill;
    }

    private FrameworkElement CreateLiveAiHealthPill(
        AiQuotaHealth initialHealth,
        Func<AiQuotaHealth, string> labelSelector,
        out Action<AiQuotaHealth> updatePill)
    {
        var tb = new TextBlock
        {
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tbHost = new Grid();
        tbHost.Children.Add(tb);

        var pill = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(6, 1.5, 6, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tbHost
        };

        void Apply(AiQuotaHealth health)
        {
            if (health is AiQuotaHealth.Warning or AiQuotaHealth.Critical)
            {
                Color fg = ResolveAiHealthColor(health);
                Color bg = Color.FromArgb((byte)(IsDark ? 34 : 24), fg.R, fg.G, fg.B);
                pill.Background = GetOrCreateSolidBrush(bg);
                tb.Foreground = GetOrCreateSolidBrush(fg);
                tb.Text = labelSelector(health);
                pill.Visibility = Visibility.Visible;
            }
            else
            {
                pill.Visibility = Visibility.Collapsed;
            }
        }

        Apply(initialHealth);
        updatePill = Apply;
        return pill;
    }

    private FrameworkElement CreateLiveProportionalBar(
        double percent,
        Color fillColor,
        double height,
        out Action<double, Color> updateBar)
    {
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2.0),
            Background = DividerBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var propGrid = new Grid();
        var col0 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var col1 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        propGrid.ColumnDefinitions.Add(col0);
        propGrid.ColumnDefinitions.Add(col1);

        var fill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2.0)
        };
        Grid.SetColumn(fill, 0);
        propGrid.Children.Add(fill);
        track.Child = propGrid;

        void Apply(double pct, Color color)
        {
            double clamped = Math.Clamp(pct, 0.0, 100.0);
            if (clamped <= 0.4)
            {
                propGrid.Visibility = Visibility.Collapsed;
                return;
            }

            col0.Width = new GridLength(clamped, GridUnitType.Star);
            col1.Width = new GridLength(Math.Max(0.01, 100.0 - clamped), GridUnitType.Star);
            fill.Background = GetOrCreateSolidBrush(color);
            propGrid.Visibility = Visibility.Visible;
        }

        Apply(percent, fillColor);
        updateBar = Apply;
        return track;
    }

    private Color ResolveAiHealthColor(AiQuotaHealth health) => health switch
    {
        AiQuotaHealth.Critical => IsDark
            ? Color.FromArgb(255, 248, 113, 113)
            : Color.FromArgb(255, 220, 38, 38),
        AiQuotaHealth.Warning => IsDark
            ? Color.FromArgb(255, 251, 191, 36)
            : Color.FromArgb(255, 217, 119, 6),
        AiQuotaHealth.Healthy => IsDark
            ? Color.FromArgb(255, 52, 211, 153)
            : Color.FromArgb(255, 16, 185, 129),
        _ => IsDark
            ? Color.FromArgb(160, 255, 255, 255)
            : Color.FromArgb(130, 0, 0, 0)
    };

    private static Color ParseBrandHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 6 &&
                byte.TryParse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                byte.TryParse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return Color.FromArgb(255, r, g, b);
            }
        }
        return Color.FromArgb(255, 59, 130, 246);
    }

    private bool IsAiModuleEnabled(CustomWidgetEvaluationContext ctx, string moduleId)
    {
        CustomWidgetSettingDefinition? moduleDef = _viewModel.Manifest.Settings.FirstOrDefault(
            static d => string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));
        if (moduleDef is null)
        {
            return true;
        }

        ctx.Settings.TryGetValue("modules", out string? raw);
        return CustomWidgetModuleListHelper.IsModuleEnabled(raw, moduleId, moduleDef.Options);
    }

    private int CountEnabledAiModules(CustomWidgetEvaluationContext ctx)
    {
        CustomWidgetSettingDefinition? moduleDef = _viewModel.Manifest.Settings.FirstOrDefault(
            static d => string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));
        if (moduleDef is null)
        {
            return 1;
        }

        ctx.Settings.TryGetValue("modules", out string? raw);
        return CustomWidgetModuleListHelper.ParseEntries(raw, moduleDef.Options)
            .Count(static e => e.Enabled);
    }
}
