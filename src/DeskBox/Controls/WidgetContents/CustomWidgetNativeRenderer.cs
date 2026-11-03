using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Builds a 100% native WinUI 3 visual tree from a <see cref="CustomWidgetManifest"/>
/// using window-scoped Fluent theme resources, and registers lightweight in-place
/// updaters for live <c>$sys</c> metrics so periodic refreshes never disrupt input
/// focus or scroll position.
/// </summary>
public sealed partial class CustomWidgetNativeRenderer
{
    private const double RadiusSmall = 4.0;
    private const double RadiusMedium = 6.0;
    private const double RadiusLarge = 8.0;
    private const double MetricFontSize = 20.0;

    private static readonly FontFamily DisplayFontFamily = new("Segoe UI Variable Display, Segoe UI");

    private static CornerRadius CornerSmall => new(RadiusSmall);
    private static CornerRadius CornerMedium => new(RadiusMedium);
    private static CornerRadius CornerLarge => new(RadiusLarge);

    private readonly CustomWidgetViewModel _viewModel;
    private readonly DependencyObject? _themeScope;
    private readonly Func<double>? _textSizeProvider;
    private readonly List<Action<CustomWidgetEvaluationContext>> _liveMetricUpdaters = [];
    private readonly Dictionary<Color, SolidColorBrush> _solidBrushCache = [];
    private readonly Dictionary<Color, LinearGradientBrush> _sparklineBrushCache = [];
    private readonly HashSet<FlyoutBase> _activeOpenFlyouts = [];

    internal bool HasOpenFlyout => _activeOpenFlyouts.Count > 0;

    internal event Action? AllFlyoutsClosed;

    public CustomWidgetNativeRenderer(
        CustomWidgetViewModel viewModel,
        DependencyObject? themeScope = null,
        Func<double>? textSizeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _themeScope = themeScope;
        _textSizeProvider = textSizeProvider;
    }

    internal void ReleaseResources()
    {
        _activeOpenFlyouts.Clear();
        _liveMetricUpdaters.Clear();
        _solidBrushCache.Clear();
        _sparklineBrushCache.Clear();
    }

    private void TrackInteractiveFlyout(FlyoutBase flyout, bool markOpeningImmediately = false)
    {
        if (markOpeningImmediately)
        {
            _activeOpenFlyouts.Add(flyout);
        }

        flyout.Opening += (_, _) => _activeOpenFlyouts.Add(flyout);
        flyout.Opened += (_, _) => _activeOpenFlyouts.Add(flyout);
        flyout.Closed += (_, _) =>
        {
            if (_activeOpenFlyouts.Remove(flyout) && _activeOpenFlyouts.Count == 0)
            {
                AllFlyoutsClosed?.Invoke();
            }
        };
    }

    private void ShowTrackedFlyoutAt(FlyoutBase flyout, FrameworkElement anchor)
    {
        TrackInteractiveFlyout(flyout, markOpeningImmediately: true);
        try
        {
            flyout.ShowAt(anchor);
        }
        catch
        {
            if (_activeOpenFlyouts.Remove(flyout) && _activeOpenFlyouts.Count == 0)
            {
                AllFlyoutsClosed?.Invoke();
            }
            throw;
        }
    }

    private bool IsDark => NeutralInteractionBrush.IsDarkTheme(_themeScope);

    private double BodyFontSize =>
        Math.Clamp(_textSizeProvider?.Invoke() ?? SettingsService.DefaultTextSize, 12.0, 18.0);

    private double CaptionFontSize =>
        Math.Clamp(BodyFontSize - 2.0, 11.5, 15.0);

    private double SubtitleFontSize => BodyFontSize + 1.0;

    private Brush DividerBrush => GetBrush(
        "WidgetLayerStrokeBrush",
        IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));

    private Brush SubtleHoverBrush => GetBrush(
        "SubtleFillColorSecondaryBrush",
        IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));

    private Brush SubtlePressedBrush => GetBrush(
        "SubtleFillColorTertiaryBrush",
        IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));

    private Button CreateSubtleIconButton(
        string glyph,
        double size = 26,
        double iconSize = 12,
        string? tooltip = null)
    {
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            CornerRadius = CornerMedium,
            BorderThickness = new Thickness(0),
            BorderBrush = transparent,
            Background = transparent,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = iconSize,
                Foreground = ResolveForegroundBrush("secondary")
            }
        };
        ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            ToolTipService.SetToolTip(btn, tooltip);
        }

        return btn;
    }

    private Button CreateElevatedTransportButton(UIElement content, double size = 36)
    {
        Brush restBg = GetBrush(
            "ControlFillColorDefaultBrush",
            IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(182, 255, 255, 255));
        Brush hoverBg = GetBrush(
            "ControlFillColorSecondaryBrush",
            IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(215, 255, 255, 255));
        Brush pressedBg = GetBrush(
            "ControlFillColorTertiaryBrush",
            IsDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255));
        Brush restBorder = GetBrush(
            "ControlElevationBorderBrush",
            IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        Brush hoverBorder = GetBrush(
            "ControlStrokeColorSecondaryBrush",
            IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0));

        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = CornerLarge,
            BorderThickness = new Thickness(1),
            BorderBrush = restBorder,
            Background = restBg,
            Foreground = ResolveForegroundBrush("default"),
            UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content
        };
        ApplyButtonStateOverrides(btn, hoverBg, pressedBg, hoverBorder, restBorder);
        return btn;
    }

    private void ApplySegmentedItemState(Button btn, bool active)
    {
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        if (active)
        {
            Brush selectedBg = GetBrush(
                "ControlFillColorDefaultBrush",
                IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(220, 255, 255, 255));
            Brush selectedHover = GetBrush(
                "ControlFillColorSecondaryBrush",
                IsDark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(238, 255, 255, 255));
            Brush selectedPressed = GetBrush(
                "ControlFillColorTertiaryBrush",
                IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(196, 255, 255, 255));
            Brush selectedBorder = GetBrush(
                "ControlElevationBorderBrush",
                IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));

            btn.Background = selectedBg;
            btn.BorderBrush = selectedBorder;
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = ResolveForegroundBrush("default");
            btn.FontWeight = FontWeights.SemiBold;
            ApplyButtonStateOverrides(btn, selectedHover, selectedPressed, selectedBorder, selectedBorder);
        }
        else
        {
            btn.Background = transparent;
            btn.BorderBrush = transparent;
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = ResolveForegroundBrush("secondary");
            btn.FontWeight = FontWeights.Normal;
            ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        }
    }

    private void StyleAccentCommitButton(Button btn)
    {
        btn.CornerRadius = CornerMedium;
        btn.FontWeight = FontWeights.SemiBold;
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("AccentButtonStyle", out object? accentObj) &&
            accentObj is Style accentStyle)
        {
            btn.Style = accentStyle;
            btn.CornerRadius = CornerMedium;
            return;
        }

        Brush accentBg = GetBrush(
            "AccentFillColorDefaultBrush",
            IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Brush accentHover = GetBrush(
            "AccentFillColorSecondaryBrush",
            IsDark ? Color.FromArgb(230, 96, 165, 250) : Color.FromArgb(230, 37, 99, 235));
        Brush accentPressed = GetBrush(
            "AccentFillColorTertiaryBrush",
            IsDark ? Color.FromArgb(204, 96, 165, 250) : Color.FromArgb(204, 37, 99, 235));
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);

        btn.Background = accentBg;
        btn.BorderBrush = transparent;
        btn.BorderThickness = new Thickness(0);
        btn.Foreground = GetBrush(
            "TextOnAccentFillColorPrimaryBrush",
            IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White);
        ApplyButtonStateOverrides(btn, accentHover, accentPressed, transparent, transparent);
    }

    private void StyleStandardCommandButton(Button btn, CornerRadius? radius = null)
    {
        Brush restBg = GetBrush(
            "ControlFillColorDefaultBrush",
            IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(178, 255, 255, 255));
        Brush hoverBg = GetBrush(
            "ControlFillColorSecondaryBrush",
            IsDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(212, 255, 255, 255));
        Brush pressedBg = GetBrush(
            "ControlFillColorTertiaryBrush",
            IsDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(148, 255, 255, 255));
        Brush border = GetBrush(
            "ControlElevationBorderBrush",
            IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));

        btn.CornerRadius = radius ?? CornerMedium;
        btn.Background = restBg;
        btn.BorderBrush = border;
        btn.BorderThickness = new Thickness(1);
        btn.Foreground = ResolveForegroundBrush("default");
        ApplyButtonStateOverrides(btn, hoverBg, pressedBg, border, border);
    }

    private void ApplyCompactInputStyle(TextBox textBox)
    {
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("WidgetCompactInputTextBoxStyle", out object? styleObj) &&
            styleObj is Style inputStyle)
        {
            textBox.Style = inputStyle;
        }
        textBox.CornerRadius = CornerSmall;
        textBox.FontSize = BodyFontSize;
    }

    public UIElement BuildRootElement()
    {
        _activeOpenFlyouts.Clear();
        _liveMetricUpdaters.Clear();
        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();

        var rootStack = new Grid();
        UIElement content = BuildNode(_viewModel.Manifest.View, ctx, isRoot: true);

        IReadOnlyList<CustomWidgetSettingDefinition> missingSecrets = _viewModel.GetUnconfiguredSecretSettings();
        if (missingSecrets.Count > 0)
        {
            var bodyGrid = new Grid { RowSpacing = 2 };
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            FrameworkElement secretBanner = BuildSecretSetupBanner(missingSecrets, ctx.CultureName);
            secretBanner.Margin = new Thickness(12, 10, 12, 0);
            Grid.SetRow(secretBanner, 0);
            bodyGrid.Children.Add(secretBanner);

            if (content is FrameworkElement contentFe)
            {
                Grid.SetRow(contentFe, 1);
            }
            bodyGrid.Children.Add(content);
            rootStack.Children.Add(bodyGrid);
        }
        else
        {
            rootStack.Children.Add(content);
        }

        if (_viewModel.Diagnostics.Any(d => d.IsError))
        {
            InfoBar errorBanner = BuildDiagnosticBanner(_viewModel.Diagnostics);
            errorBanner.VerticalAlignment = VerticalAlignment.Bottom;
            errorBanner.Margin = new Thickness(10);
            rootStack.Children.Add(errorBanner);
        }

        return rootStack;
    }

    private FrameworkElement BuildSecretSetupBanner(
        IReadOnlyList<CustomWidgetSettingDefinition> missingSecrets,
        string? cultureName)
    {
        bool isZh = (cultureName ?? _viewModel.ActiveCultureName)
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string firstLabel = missingSecrets[0].Label;
        string promptText = missingSecrets.Count == 1
            ? (isZh ? $"需配置密钥：{firstLabel}" : $"API secret required: {firstLabel}")
            : (isZh ? $"需配置 {missingSecrets.Count} 项密钥" : $"{missingSecrets.Count} API secrets required");

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyIcon = new FontIcon
        {
            Glyph = "\uE8D7",
            FontSize = 13,
            Foreground = ResolveSemanticBrush("warning"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(keyIcon, 0);
        row.Children.Add(keyIcon);

        var tb = new TextBlock
        {
            Text = promptText,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(tb, 1);
        row.Children.Add(tb);

        var configBtn = new Button
        {
            Content = isZh ? "填写" : "Configure",
            FontSize = CaptionFontSize,
            Padding = new Thickness(10, 3, 10, 3),
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleAccentCommitButton(configBtn);
        configBtn.CornerRadius = CornerSmall;
        configBtn.Click += async (_, _) =>
        {
            XamlRoot? root = configBtn.XamlRoot ?? (_themeScope as FrameworkElement)?.XamlRoot;
            if (root is not null)
            {
                ElementTheme theme = (_themeScope as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                await ShowSecretSettingsDialogAsync(root, theme, _viewModel);
            }
        };
        Grid.SetColumn(configBtn, 2);
        row.Children.Add(configBtn);

        return new Border
        {
            Margin = new Thickness(10, 8, 10, 0),
            Padding = new Thickness(10, 6, 8, 6),
            CornerRadius = CornerMedium,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(36, 245, 158, 11) : Color.FromArgb(26, 245, 158, 11)),
            BorderThickness = new Thickness(1),
            BorderBrush = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(90, 251, 191, 36) : Color.FromArgb(80, 217, 119, 6)),
            Child = row
        };
    }

    public static async Task ShowSecretSettingsDialogAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        CustomWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(viewModel);

        IReadOnlyList<CustomWidgetSettingDefinition> secretDefs = viewModel.Manifest
            .ResolveLocalizedSettings(viewModel.ActiveCultureName)
            .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (secretDefs.Count == 0)
        {
            return;
        }

        bool isZh = viewModel.ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var stack = new StackPanel { Spacing = 12, MinWidth = 340 };
        stack.Children.Add(new TextBlock
        {
            Text = isZh
                ? "密钥使用 Windows DPAPI 本机加密存储，不会写入 manifest.json、store.json 或分享包。"
                : "Secrets are encrypted locally via Windows DPAPI and are never exported in widget packages.",
            FontSize = 12,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap
        });

        var inputs = new List<(string Key, PasswordBox Box, bool HadExisting)>(secretDefs.Count);
        foreach (CustomWidgetSettingDefinition def in secretDefs)
        {
            viewModel.Secrets.TryGetValue(def.Key, out string? existing);
            bool hasExisting = !string.IsNullOrWhiteSpace(existing);

            var fieldPanel = new StackPanel { Spacing = 4 };
            fieldPanel.Children.Add(new TextBlock
            {
                Text = def.Label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });

            var pwdBox = new PasswordBox
            {
                PlaceholderText = hasExisting
                    ? (isZh ? $"已配置 ({CustomWidgetSecretStore.MaskSecret(existing)}) · 留空保持不变" : $"Configured ({CustomWidgetSecretStore.MaskSecret(existing)}) · Leave blank to keep")
                    : (isZh ? "输入 API Token / 密钥…" : "Enter API token / secret…"),
                CornerRadius = new CornerRadius(6)
            };
            fieldPanel.Children.Add(pwdBox);
            stack.Children.Add(fieldPanel);
            inputs.Add((def.Key, pwdBox, hasExisting));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = isZh ? "配置专属 API 密钥" : "Configure Widget API Secrets",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack
            },
            PrimaryButtonText = LocalizationService.TranslateForCulture(viewModel.ActiveCultureName, "Common.Save"),
            SecondaryButtonText = inputs.Any(static x => x.HadExisting)
                ? (isZh ? "清空密钥" : "Clear All")
                : string.Empty,
            CloseButtonText = LocalizationService.TranslateForCulture(viewModel.ActiveCultureName, "Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        try
        {
            ContentDialogResult res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                foreach ((string key, PasswordBox box, bool hadExisting) in inputs)
                {
                    string typed = box.Password.Trim();
                    if (!string.IsNullOrEmpty(typed) || !hadExisting)
                    {
                        await viewModel.SetSecretValueAsync(key, typed);
                    }
                }
            }
            else if (res == ContentDialogResult.Secondary)
            {
                foreach ((string key, _, _) in inputs)
                {
                    await viewModel.SetSecretValueAsync(key, string.Empty);
                }
            }
        }
        catch
        {
        }
    }

    private string Tr(string key, string? cultureName = null) =>
        LocalizationService.TranslateForCulture(
            string.IsNullOrWhiteSpace(cultureName) ? _viewModel.ActiveCultureName : cultureName,
            key);

    private string TrFormat(string key, string? cultureName, params object[] args) =>
        LocalizationService.FormatForCulture(
            string.IsNullOrWhiteSpace(cultureName) ? _viewModel.ActiveCultureName : cultureName,
            key,
            args);

    public void RefreshLiveMetrics()
    {
        if (_liveMetricUpdaters.Count == 0)
        {
            return;
        }

        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();
        foreach (Action<CustomWidgetEvaluationContext> updater in _liveMetricUpdaters)
        {
            updater(ctx);
        }
    }

    private const int MaxRenderNodeDepth = 64;
    private int _buildNodeDepth;

    private UIElement BuildNode(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        if (_buildNodeDepth > MaxRenderNodeDepth)
        {
            return new Grid { Visibility = Visibility.Collapsed };
        }

        _buildNodeDepth++;
        try
        {
            if (!string.IsNullOrWhiteSpace(spec.When) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.When, ctx))
            {
                return new Grid { Visibility = Visibility.Collapsed };
            }

            string normalizedType = spec.Type.ToLowerInvariant();
            FrameworkElement element = normalizedType switch
            {
                "stack" => BuildStack(spec, ctx, isRoot),
                "wrap" => BuildWrap(spec, ctx, isRoot),
                "spacer" => BuildSpacer(spec),
                "modulestack" => BuildModuleStack(spec, ctx, isRoot),
                "grid" => BuildGrid(spec, ctx, isRoot),
                "scroll" => BuildScroll(spec, ctx, isRoot),
                "card" => BuildCard(spec, ctx),
                "expander" => BuildExpander(spec, ctx),
                "tabs" => BuildTabs(spec, ctx, isRoot),
                "list" => BuildList(spec, ctx),
                "barlist" => BuildBarList(spec, ctx),
                "text" => BuildText(spec, ctx),
                "image" => BuildImage(spec, ctx),
                "input" => BuildInput(spec, ctx),
                "textarea" => BuildTextArea(spec, ctx),
                "button" => BuildButton(spec, ctx),
                "iconbutton" => BuildIconButton(spec, ctx),
                "toggle" => BuildToggle(spec, ctx),
                "checkbox" => BuildCheckBox(spec, ctx),
                "select" => BuildSelect(spec, ctx),
                "segmented" => BuildSegmented(spec, ctx),
                "slider" => BuildSlider(spec, ctx),
                "numberbox" => BuildNumberBox(spec, ctx),
                "badge" => BuildBadge(spec, ctx),
                "progressbar" => BuildProgressBar(spec, ctx),
                "progressring" => BuildProgressRing(spec, ctx),
                "ringgauge" => BuildRingGauge(spec, ctx),
                "sparkline" => BuildSparkline(spec, ctx),
                "heatmapgrid" => BuildHeatmapGrid(spec, ctx),
                "calendargrid" => BuildCalendarGrid(spec, ctx),
                "dropzone" => BuildDropZone(spec, ctx),
                "controltile" => BuildControlTile(spec, ctx),
                "statcard" => BuildStatCard(spec, ctx),
                "gpucard" => BuildGpuCard(spec, ctx),
                "processlist" => BuildProcessList(spec, ctx),
                "pomodorocard" => BuildPomodoroCard(spec, ctx),
                "countdownhero" => BuildCountdownHero(spec, ctx),
                "countdownlist" => BuildCountdownList(spec, ctx),
                "timeprogresscard" => BuildTimeProgressCard(spec, ctx),
                "aifocuscard" => BuildAiFocusCard(spec, ctx),
                "airadarlist" => BuildAiRadarList(spec, ctx),
                "aitokenscard" => BuildAiTokensCard(spec, ctx),
                "aitrendcard" => BuildAiTrendCard(spec, ctx),
                "progressrow" => BuildProgressRow(spec, ctx),
                "keyvaluerow" => BuildKeyValueRow(spec, ctx),
                "filechip" => BuildFileChip(spec, ctx),
                "emptystate" => BuildEmptyState(
                    new CustomWidgetEmptyStateSpec(
                        spec.Icon ?? "\uE70F",
                        spec.Title ?? spec.Text ?? string.Empty,
                        spec.Subtitle ?? string.Empty),
                    ctx),
                "divider" => new Border
                {
                    Height = 1,
                    Background = DividerBrush,
                    Margin = new Thickness(0, 2, 0, 2)
                },
                _ => BuildStack(spec, ctx, isRoot)
            };

            ApplyCommonLayoutProperties(element, spec);

            if (isRoot && normalizedType is not ("stack" or "modulestack" or "grid" or "scroll" or "tabs" or "wrap"))
            {
                var safeFrame = new Border
                {
                    Padding = ResolveRootSafePadding(null),
                    Child = element
                };
                element = normalizedType == "list"
                    ? safeFrame
                    : new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = safeFrame
                    };
            }

            return WrapForNodeInspection(element, spec, ctx, isRoot);
        }
        finally
        {
            _buildNodeDepth--;
        }
    }

    private FrameworkElement BuildStack(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot)
    {
        bool isHorizontal = string.Equals(spec.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(spec.Orientation, "row", StringComparison.OrdinalIgnoreCase);

        if (isRoot && !isHorizontal && spec.Children.Count == 1 &&
            string.Equals(spec.Children[0].Type, "ModuleStack", StringComparison.OrdinalIgnoreCase))
        {
            return BuildModuleStack(spec.Children[0], ctx, isRoot: true, outerPaddingFallback: spec.Padding);
        }

        Thickness containerPadding = isRoot
            ? ResolveRootSafePadding(spec.Padding)
            : ParseThickness(spec.Padding);

        bool IsFlexibleSpacerNode(CustomWidgetNodeSpec c) =>
            string.Equals(c.Type, "Spacer", StringComparison.OrdinalIgnoreCase) &&
            (isHorizontal ? c.Width is null : c.Height is null);

        bool hasFlexSpacer = spec.Children.Any(IsFlexibleSpacerNode);

        if ((isRoot && !isHorizontal && spec.Children.Any(c =>
                string.Equals(c.Type, "List", StringComparison.OrdinalIgnoreCase))) ||
            hasFlexSpacer)
        {
            var flexGrid = new Grid
            {
                Padding = containerPadding
            };
            if (isHorizontal)
            {
                flexGrid.ColumnSpacing = spec.Spacing;
            }
            else
            {
                flexGrid.RowSpacing = spec.Spacing;
            }

            int slotIndex = 0;
            for (int i = 0; i < spec.Children.Count; i++)
            {
                CustomWidgetNodeSpec childSpec = spec.Children[i];
                bool isFlexibleSpacer = IsFlexibleSpacerNode(childSpec);
                bool absorbsFollowingSpacer = isHorizontal &&
                                              !isFlexibleSpacer &&
                                              (string.Equals(childSpec.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(childSpec.Type, "FileChip", StringComparison.OrdinalIgnoreCase)) &&
                                              i + 1 < spec.Children.Count &&
                                              IsFlexibleSpacerNode(spec.Children[i + 1]);

                bool isFlexible = isFlexibleSpacer ||
                                  absorbsFollowingSpacer ||
                                  (!isHorizontal && isRoot &&
                                   (string.Equals(childSpec.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(childSpec.Type, "Scroll", StringComparison.OrdinalIgnoreCase)));

                if (isHorizontal)
                {
                    flexGrid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = isFlexible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
                    });
                }
                else
                {
                    flexGrid.RowDefinitions.Add(new RowDefinition
                    {
                        Height = isFlexible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
                    });
                }

                UIElement childEl = BuildNode(childSpec, ctx);
                if (childEl is FrameworkElement fe)
                {
                    if (isHorizontal)
                    {
                        Grid.SetColumn(fe, slotIndex);
                        if (fe.VerticalAlignment == VerticalAlignment.Stretch && !isFlexibleSpacer)
                        {
                            fe.VerticalAlignment = VerticalAlignment.Center;
                        }
                    }
                    else
                    {
                        Grid.SetRow(fe, slotIndex);
                    }
                }
                flexGrid.Children.Add(childEl);
                slotIndex++;

                if (absorbsFollowingSpacer)
                {
                    i++;
                }
            }

            return flexGrid;
        }

        var panel = new StackPanel
        {
            Orientation = isHorizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = spec.Spacing,
            Padding = containerPadding
        };

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            panel.Children.Add(BuildNode(child, ctx));
        }

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
        }

        return panel;
    }

    private FrameworkElement BuildGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var grid = new Grid
        {
            Padding = isRoot ? ResolveRootSafePadding(spec.Padding) : ParseThickness(spec.Padding),
            ColumnSpacing = spec.Spacing,
            RowSpacing = spec.Spacing
        };

        if (!string.IsNullOrWhiteSpace(spec.Columns))
        {
            foreach (string colToken in spec.Columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = ParseGridLength(colToken)
                });
            }
        }

        bool hasStarRow = false;
        if (!string.IsNullOrWhiteSpace(spec.Rows))
        {
            foreach (string rowToken in spec.Rows.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                GridLength rowLen = ParseGridLength(rowToken);
                if (rowLen.IsStar)
                {
                    hasStarRow = true;
                }
                grid.RowDefinitions.Add(new RowDefinition
                {
                    Height = rowLen
                });
            }
        }

        int colCount = grid.ColumnDefinitions.Count;
        bool allImplicitCoords = colCount > 1 &&
                                 spec.Children.Count > 1 &&
                                 spec.Children.All(static c => c.Row == 0 && c.Column == 0);
        bool autoFlowMultiRow = string.IsNullOrWhiteSpace(spec.Rows) &&
                                colCount > 0 &&
                                spec.Children.Count > colCount &&
                                spec.Children.All(static c => c.Row == 0);

        if (autoFlowMultiRow)
        {
            int neededRows = (spec.Children.Count + colCount - 1) / colCount;
            for (int r = 0; r < neededRows; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        for (int idx = 0; idx < spec.Children.Count; idx++)
        {
            CustomWidgetNodeSpec child = spec.Children[idx];
            UIElement childEl = BuildNode(child, ctx);
            if (childEl is FrameworkElement fe)
            {
                int effectiveCol;
                int effectiveRow;
                if (allImplicitCoords)
                {
                    effectiveCol = idx % colCount;
                    effectiveRow = string.IsNullOrWhiteSpace(spec.Rows)
                        ? (idx / colCount)
                        : Math.Min(idx / colCount, Math.Max(0, grid.RowDefinitions.Count - 1));
                }
                else if (autoFlowMultiRow)
                {
                    effectiveCol = Math.Clamp(child.Column, 0, colCount - 1);
                    effectiveRow = idx / colCount;
                }
                else
                {
                    effectiveCol = colCount > 0 ? Math.Clamp(child.Column, 0, colCount - 1) : Math.Max(0, child.Column);
                    effectiveRow = Math.Max(0, child.Row);
                }

                Grid.SetColumn(fe, effectiveCol);
                Grid.SetRow(fe, effectiveRow);
                if (child.ColumnSpan > 1) Grid.SetColumnSpan(fe, child.ColumnSpan);
                if (child.RowSpan > 1) Grid.SetRowSpan(fe, child.RowSpan);
            }
            grid.Children.Add(childEl);
        }

        if (isRoot && !hasStarRow)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid
            };
        }

        return grid;
    }

    private FrameworkElement BuildScroll(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var inner = new StackPanel
        {
            Spacing = spec.Spacing,
            Padding = isRoot ? ResolveRootSafePadding(spec.Padding) : ParseThickness(spec.Padding)
        };
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            inner.Children.Add(BuildNode(child, ctx));
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = inner
        };
    }

    private sealed record DraggableModuleSlot(
        string ModuleId,
        int EnabledIndex,
        Grid HostGrid,
        Border DropIndicator);

    private FrameworkElement BuildModuleStack(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false,
        string? outerPaddingFallback = null)
    {
        string bindRaw = spec.Bind ?? "settings.modules";
        string settingKey = bindRaw.StartsWith("settings.", StringComparison.OrdinalIgnoreCase)
            ? bindRaw["settings.".Length..]
            : bindRaw.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
                ? bindRaw["$settings.".Length..]
                : bindRaw;

        CustomWidgetSettingDefinition? settingDef = _viewModel.Manifest
            .ResolveLocalizedSettings(ctx.CultureName)
            .FirstOrDefault(d => string.Equals(d.Key, settingKey, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions = settingDef?.Options ?? [];

        string? rawModules = ctx.Settings.TryGetValue(settingKey, out string? saved)
            ? saved
            : settingDef?.DefaultValue;

        IReadOnlyList<CustomWidgetModuleEntry> entries =
            CustomWidgetModuleListHelper.ParseEntries(rawModules, definedOptions);

        var childById = new Dictionary<string, CustomWidgetNodeSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            if (!string.IsNullOrWhiteSpace(child.Id))
            {
                childById[child.Id] = child;
            }
        }

        var enabledNodes = new List<(string Id, CustomWidgetNodeSpec Node)>();
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetModuleEntry entry in entries)
        {
            if (!entry.Enabled || !childById.TryGetValue(entry.Id, out CustomWidgetNodeSpec? childSpec))
            {
                continue;
            }

            matchedIds.Add(entry.Id);
            if (!string.IsNullOrWhiteSpace(childSpec.When) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(childSpec.When, ctx))
            {
                continue;
            }

            enabledNodes.Add((entry.Id, childSpec));
        }

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            if (!string.IsNullOrWhiteSpace(child.Id) &&
                (matchedIds.Contains(child.Id) ||
                 definedOptions.Any(o => string.Equals(o.Value, child.Id, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(child.When) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(child.When, ctx))
            {
                continue;
            }

            enabledNodes.Add((child.Id ?? string.Empty, child));
        }

        string? rawModulePadding = !string.IsNullOrWhiteSpace(spec.Padding) ? spec.Padding : outerPaddingFallback;
        Thickness effectivePadding = isRoot
            ? ResolveRootSafePadding(rawModulePadding)
            : ParseThickness(rawModulePadding);

        if (isRoot &&
            enabledNodes.Count == 1 &&
            string.Equals(enabledNodes[0].Node.Type, "PomodoroCard", StringComparison.OrdinalIgnoreCase))
        {
            FrameworkElement standaloneDial = BuildPomodoroCard(enabledNodes[0].Node, ctx, isStandaloneHero: true);
            standaloneDial.HorizontalAlignment = HorizontalAlignment.Center;
            standaloneDial.VerticalAlignment = VerticalAlignment.Center;

            var centeredStage = new Grid
            {
                Padding = effectivePadding,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            centeredStage.Children.Add(standaloneDial);
            return centeredStage;
        }

        if (isRoot &&
            enabledNodes.Count == 1 &&
            string.Equals(enabledNodes[0].Node.Type, "CountdownHero", StringComparison.OrdinalIgnoreCase))
        {
            FrameworkElement standaloneCountdown = BuildCountdownHero(enabledNodes[0].Node, ctx, isStandaloneHero: true);
            standaloneCountdown.HorizontalAlignment = HorizontalAlignment.Stretch;
            standaloneCountdown.VerticalAlignment = VerticalAlignment.Stretch;

            var centeredStage = new Grid
            {
                Padding = effectivePadding,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            centeredStage.Children.Add(standaloneCountdown);
            return centeredStage;
        }

        if (isRoot &&
            enabledNodes.Count == 1 &&
            string.Equals(enabledNodes[0].Node.Type, "AiFocusCard", StringComparison.OrdinalIgnoreCase))
        {
            FrameworkElement standaloneAi = BuildAiFocusCard(enabledNodes[0].Node, ctx, isStandaloneHero: true);
            standaloneAi.HorizontalAlignment = HorizontalAlignment.Stretch;
            standaloneAi.VerticalAlignment = VerticalAlignment.Center;

            var centeredStage = new Grid
            {
                Padding = effectivePadding,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            centeredStage.Children.Add(standaloneAi);
            return centeredStage;
        }

        var container = new StackPanel
        {
            Spacing = spec.Spacing,
            Padding = effectivePadding,
            ChildrenTransitions =
            [
                new RepositionThemeTransition()
            ]
        };

        var slotRecords = new List<DraggableModuleSlot>(enabledNodes.Count);
        bool canDragReorder = enabledNodes.Count > 1 && !string.IsNullOrWhiteSpace(settingKey);

        Grid WrapModuleSlot(string moduleId, int enabledIndex, CustomWidgetNodeSpec nodeSpec)
        {
            UIElement content = BuildNode(nodeSpec, ctx);
            var host = new Grid();
            host.Children.Add(content);

            var dropIndicator = new Border
            {
                Height = 2.5,
                CornerRadius = new CornerRadius(1.25),
                Background = ResolveSemanticBrush("accent"),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, -2, 4, 0),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            host.Children.Add(dropIndicator);

            if (canDragReorder && !string.IsNullOrWhiteSpace(moduleId))
            {
                var gripTb = new TextBlock
                {
                    Text = "⋮⋮",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = ResolveForegroundBrush("muted"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                };
                WidgetTextShadow.SetCast(gripTb, false);

                var handleBorder = new Border
                {
                    Width = 20,
                    Height = 18,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 4, 0),
                    Opacity = 0,
                    Child = gripTb
                };
                ToolTipService.SetToolTip(
                    handleBorder,
                    Tr("CustomWidget.Module.DragHint", ctx.CultureName));

                bool isDragging = false;
                uint activePointerId = 0;
                Point pressPoint = default;
                bool movedEnough = false;
                int targetIndex = enabledIndex;

                host.PointerEntered += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Opacity = 0.72;
                    }
                };
                host.PointerExited += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Opacity = 0;
                    }
                };

                handleBorder.PointerEntered += (_, _) =>
                {
                    handleBorder.Background = GetBrush(
                        "SubtleFillColorSecondaryBrush",
                        IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));
                    handleBorder.Opacity = 1;
                };
                handleBorder.PointerExited += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Background = new SolidColorBrush(Colors.Transparent);
                    }
                };

                handleBorder.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(handleBorder).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    isDragging = true;
                    movedEnough = false;
                    targetIndex = enabledIndex;
                    activePointerId = e.Pointer.PointerId;
                    pressPoint = e.GetCurrentPoint(container).Position;
                    handleBorder.CapturePointer(e.Pointer);
                    host.Opacity = 0.65;
                    e.Handled = true;
                };

                handleBorder.PointerMoved += (_, e) =>
                {
                    if (!isDragging || e.Pointer.PointerId != activePointerId)
                    {
                        return;
                    }

                    Point pt = e.GetCurrentPoint(container).Position;
                    if (!movedEnough)
                    {
                        double dx = pt.X - pressPoint.X;
                        double dy = pt.Y - pressPoint.Y;
                        if ((dx * dx) + (dy * dy) < 25)
                        {
                            return;
                        }
                        movedEnough = true;
                    }

                    int bestSlot = enabledIndex;
                    double bestDistSq = double.MaxValue;
                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        if (slot.HostGrid.ActualWidth <= 0 || slot.HostGrid.ActualHeight <= 0)
                        {
                            continue;
                        }

                        Point origin = slot.HostGrid
                            .TransformToVisual(container)
                            .TransformPoint(new Point(0, 0));
                        double cx = origin.X + (slot.HostGrid.ActualWidth * 0.5);
                        double cy = origin.Y + (slot.HostGrid.ActualHeight * 0.5);
                        double distSq = ((pt.X - cx) * (pt.X - cx)) + ((pt.Y - cy) * (pt.Y - cy));
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestSlot = slot.EnabledIndex;
                        }
                    }

                    targetIndex = bestSlot;
                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        slot.DropIndicator.Visibility =
                            slot.EnabledIndex == targetIndex && targetIndex != enabledIndex
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                    }

                    e.Handled = true;
                };

                async void FinishDrag(PointerRoutedEventArgs e, bool commit)
                {
                    if (!isDragging)
                    {
                        return;
                    }

                    isDragging = false;
                    handleBorder.ReleasePointerCapture(e.Pointer);
                    host.Opacity = 1;
                    handleBorder.Background = new SolidColorBrush(Colors.Transparent);
                    handleBorder.Opacity = 0;

                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        slot.DropIndicator.Visibility = Visibility.Collapsed;
                    }

                    if (commit && movedEnough && targetIndex != enabledIndex)
                    {
                        string? latestRaw = _viewModel.Settings.TryGetValue(settingKey, out string? cur)
                            ? cur
                            : rawModules;
                        string updated = CustomWidgetModuleListHelper.ReorderEnabledModule(
                            latestRaw,
                            definedOptions,
                            moduleId,
                            targetIndex);
                        await _viewModel.SetSettingValueAsync(settingKey, updated);
                    }
                }

                handleBorder.PointerReleased += (_, e) =>
                {
                    e.Handled = true;
                    FinishDrag(e, commit: true);
                };
                handleBorder.PointerCanceled += (_, e) => FinishDrag(e, commit: false);
                handleBorder.PointerCaptureLost += (_, e) => FinishDrag(e, commit: false);

                host.Children.Add(handleBorder);
            }

            slotRecords.Add(new DraggableModuleSlot(moduleId, enabledIndex, host, dropIndicator));
            return host;
        }

        for (int i = 0; i < enabledNodes.Count; i++)
        {
            (string idA, CustomWidgetNodeSpec specA) = enabledNodes[i];
            if (specA.CompactPair &&
                i + 1 < enabledNodes.Count &&
                enabledNodes[i + 1].Node.CompactPair)
            {
                (string idB, CustomWidgetNodeSpec specB) = enabledNodes[i + 1];
                var pairGrid = new Grid
                {
                    ColumnSpacing = spec.Spacing
                };
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Grid leftSlot = WrapModuleSlot(idA, i, specA);
                Grid rightSlot = WrapModuleSlot(idB, i + 1, specB);
                Grid.SetColumn(leftSlot, 0);
                Grid.SetColumn(rightSlot, 1);
                pairGrid.Children.Add(leftSlot);
                pairGrid.Children.Add(rightSlot);
                container.Children.Add(pairGrid);
                i++;
            }
            else
            {
                container.Children.Add(WrapModuleSlot(idA, i, specA));
            }
        }

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = container
            };
        }

        return container;
    }

    private FrameworkElement BuildCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var inner = new StackPanel
        {
            Spacing = spec.Spacing
        };
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            inner.Children.Add(BuildNode(child, ctx));
        }

        double effectiveRadius = Math.Clamp(spec.CornerRadius ?? RadiusMedium, 0.0, RadiusLarge);
        string resolvedTone = CustomWidgetExpressionEvaluator.Interpolate(spec.Tone, ctx);
        Brush restBackground = ResolveCardBackground(resolvedTone);
        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(effectiveRadius),
            Padding = ParseThickness(spec.Padding ?? "12,10,12,10"),
            Background = restBackground,
            BorderThickness = new Thickness(0),
            Child = inner
        };

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(cardBorder, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        if (spec.OnClick.Count > 0)
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            Brush hoverBackground = SubtleHoverBrush;
            cardBorder.PointerEntered += (_, _) => cardBorder.Background = hoverBackground;
            cardBorder.PointerExited += (_, _) => cardBorder.Background = restBackground;
            cardBorder.Tapped += async (_, e) =>
            {
                if (e.Handled || IsInteractiveDescendant(e.OriginalSource as DependencyObject, cardBorder))
                {
                    return;
                }

                e.Handled = true;
                await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
            };
        }

        return cardBorder;
    }

    private static bool IsInteractiveDescendant(DependencyObject? source, DependencyObject stopAt)
    {
        DependencyObject? cur = source;
        while (cur is not null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is ButtonBase or TextBox or ToggleSwitch or Selector)
            {
                return true;
            }

            try
            {
                cur = VisualTreeHelper.GetParent(cur);
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    private FrameworkElement BuildList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string collectionName = CustomWidgetExpressionEvaluator
            .Interpolate(spec.Items ?? "logs", ctx)
            .Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            collectionName = "logs";
        }

        if (collectionName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
        {
            collectionName = collectionName["collections.".Length..];
            int dot = collectionName.IndexOf('.');
            if (dot > 0) collectionName = collectionName[..dot];
        }

        IReadOnlyList<CustomWidgetRecord> allRecords =
            ctx.Collections.TryGetValue(collectionName, out List<CustomWidgetRecord>? list)
                ? list
                : [];

        var filtered = new List<(CustomWidgetRecord Record, int Index)>();
        for (int i = 0; i < allRecords.Count; i++)
        {
            CustomWidgetRecord rec = allRecords[i];
            CustomWidgetEvaluationContext itemCtx = ctx.WithItem(rec, i);
            if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
            {
                continue;
            }
            filtered.Add((rec, i));
        }

        if (filtered.Count == 0)
        {
            return BuildEmptyState(
                spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                    "\uE70F",
                    Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    string.Empty),
                ctx);
        }

        var itemsStack = new StackPanel
        {
            Spacing = 4,
            ChildrenTransitions =
            [
                new EntranceThemeTransition { IsStaggeringEnabled = true },
                new RepositionThemeTransition()
            ]
        };

        int maxVisibleItems = spec.MaxLines > 0 ? spec.MaxLines : int.MaxValue;
        foreach ((CustomWidgetRecord record, int index) in filtered.Take(maxVisibleItems))
        {
            CustomWidgetEvaluationContext itemCtx = ctx.WithItem(record, index);
            itemsStack.Children.Add(BuildListItemCard(spec, itemCtx));
        }

        if (spec.MaxLines > 0)
        {
            return itemsStack;
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = itemsStack
        };
    }

    private FrameworkElement BuildListItemCard(
        CustomWidgetNodeSpec listSpec,
        CustomWidgetEvaluationContext itemCtx)
    {
        var itemGrid = new Grid
        {
            ColumnSpacing = 8
        };
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement body = listSpec.ItemTemplate is not null
            ? BuildNode(listSpec.ItemTemplate, itemCtx)
            : new TextBlock
            {
                Text = itemCtx.CurrentItem?.GetField("text") ?? string.Empty,
                Foreground = ResolveForegroundBrush("default"),
                TextWrapping = TextWrapping.Wrap
            };
        if (body is FrameworkElement bodyFe)
        {
            Grid.SetColumn(bodyFe, 0);
        }
        itemGrid.Children.Add(body);

        StackPanel? hoverPanel = null;
        if (listSpec.HoverActions.Count > 0)
        {
            hoverPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0
            };
            foreach (CustomWidgetNodeSpec actionNode in listSpec.HoverActions)
            {
                hoverPanel.Children.Add(BuildNode(actionNode, itemCtx));
            }
            Grid.SetColumn(hoverPanel, 1);
            itemGrid.Children.Add(hoverPanel);
        }

        DisableTextShadowsInSubtree(itemGrid);
        Brush defaultBg = ResolveCardBackground("default");
        Brush hoverBg = SubtleHoverBrush;

        var cardBorder = new Border
        {
            CornerRadius = CornerSmall,
            MinHeight = 40,
            Padding = new Thickness(10, 7, 10, 7),
            Background = defaultBg,
            BorderThickness = new Thickness(0),
            Child = itemGrid
        };

        cardBorder.PointerEntered += (_, _) =>
        {
            cardBorder.Background = hoverBg;
            if (hoverPanel is not null)
            {
                hoverPanel.Opacity = 1;
            }
        };
        cardBorder.PointerExited += (_, _) =>
        {
            cardBorder.Background = defaultBg;
            if (hoverPanel is not null)
            {
                hoverPanel.Opacity = 0;
            }
        };

        return cardBorder;
    }

    private FrameworkElement BuildText(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTemplate = spec.Text ?? spec.Title ?? spec.Value ?? string.Empty;
        var tb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, ctx),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        if (spec.MaxLines > 0)
        {
            tb.MaxLines = spec.MaxLines;
        }

        ApplyTypography(tb, spec.Style);
        string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);
        tb.Foreground = ResolveForegroundBrush(resolvedColor);

        if (ContainsSystemMetricsToken(rawTemplate) || ContainsSystemMetricsToken(spec.Color))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                tb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, liveCtx);
                if (ContainsSystemMetricsToken(spec.Color))
                {
                    tb.Foreground = ResolveForegroundBrush(CustomWidgetExpressionEvaluator.Interpolate(spec.Color, liveCtx));
                }
            });
        }

        return tb;
    }

    private FrameworkElement BuildInput(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string initialValue = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        var textBox = new TextBox
        {
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            Text = initialValue,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyCompactInputStyle(textBox);

        textBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, textBox.Text);
            }
        };

        if (spec.OnSubmit.Count > 0)
        {
            textBox.KeyDown += async (_, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    if (!string.IsNullOrWhiteSpace(bindKey))
                    {
                        _viewModel.SetStateValueWithoutRebuild(bindKey, textBox.Text);
                    }
                    await _viewModel.ExecuteActionsAsync(spec.OnSubmit);
                }
            };
        }

        return textBox;
    }

    private FrameworkElement BuildButton(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawLabel = spec.Text ?? spec.Title ?? string.Empty;
        string label = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, ctx);
        string? icon = ResolveNormalizedIcon(spec.Icon, ctx);
        bool isAccent = string.Equals(spec.Variant, "accent", StringComparison.OrdinalIgnoreCase);
        Brush? accentIconFg = isAccent
            ? GetBrush("TextOnAccentFillColorPrimaryBrush", IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White)
            : string.Equals(spec.Variant, "danger", StringComparison.OrdinalIgnoreCase)
                ? ResolveForegroundBrush("danger")
                : null;

        FrameworkElement? liveIconEl = null;
        TextBlock? liveLabelTb = null;
        UIElement buttonContent;
        if (!string.IsNullOrWhiteSpace(icon) && !string.IsNullOrWhiteSpace(label))
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            liveIconEl = CreateInlineIconElement(icon, 13, accentIconFg);
            stack.Children.Add(liveIconEl);
            var btnTb = new TextBlock
            {
                Text = label,
                FontSize = CaptionFontSize,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isAccent)
            {
                WidgetTextShadow.SetCast(btnTb, false);
            }
            liveLabelTb = btnTb;
            stack.Children.Add(btnTb);
            buttonContent = stack;
        }
        else if (!string.IsNullOrWhiteSpace(icon))
        {
            liveIconEl = CreateInlineIconElement(icon, 13, accentIconFg);
            buttonContent = liveIconEl;
        }
        else
        {
            var btnTb = new TextBlock
            {
                Text = label,
                FontSize = CaptionFontSize
            };
            if (isAccent)
            {
                WidgetTextShadow.SetCast(btnTb, false);
            }
            liveLabelTb = btnTb;
            buttonContent = btnTb;
        }

        if (ContainsSystemMetricsToken(spec.Icon) || ContainsSystemMetricsToken(rawLabel))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                if (liveLabelTb is not null && ContainsSystemMetricsToken(rawLabel))
                {
                    liveLabelTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                }
                if (liveIconEl is not null && ContainsSystemMetricsToken(spec.Icon))
                {
                    UpdateInlineIconGlyph(liveIconEl, ResolveNormalizedIcon(spec.Icon, liveCtx) ?? string.Empty);
                }
            });
        }

        bool isIconOnly = string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(icon);
        var btn = new Button
        {
            Content = buttonContent,
            CornerRadius = isIconOnly ? CornerMedium : CornerSmall,
            Padding = isIconOnly
                ? new Thickness(0)
                : new Thickness(10, 5, 10, 5),
            MinWidth = isIconOnly ? 26 : 0,
            MinHeight = isIconOnly ? 26 : 28,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (isIconOnly)
        {
            btn.Width = 26;
            btn.Height = 26;
        }

        if (isAccent)
        {
            StyleAccentCommitButton(btn);
            btn.CornerRadius = CornerSmall;
        }
        else if (string.Equals(spec.Variant, "danger", StringComparison.OrdinalIgnoreCase))
        {
            btn.Foreground = ResolveForegroundBrush("danger");
        }
        else if (string.Equals(spec.Variant, "ghost", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(spec.Variant, "subtle", StringComparison.OrdinalIgnoreCase))
        {
            Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
            btn.Background = transparent;
            btn.BorderBrush = transparent;
            btn.BorderThickness = new Thickness(0);
            ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        }
        else
        {
            StyleStandardCommandButton(btn, CornerSmall);
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(btn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        if (spec.OnClick.Count > 0)
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            btn.Click += async (_, _) =>
            {
                await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
            };
        }

        return btn;
    }

    private FrameworkElement BuildSegmented(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string currentVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : string.Empty;

        var hostBorder = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };

        foreach (CustomWidgetOptionSpec opt in spec.Options)
        {
            bool isSelected = string.Equals(opt.Value, currentVal, StringComparison.Ordinal);

            var segmentBtn = new Button
            {
                Content = CustomWidgetExpressionEvaluator.Interpolate(opt.Label, ctx),
                CornerRadius = CornerSmall,
                Height = 26,
                MinHeight = 26,
                MinWidth = 0,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = CaptionFontSize
            };
            ApplySegmentedItemState(segmentBtn, isSelected);

            string targetValue = opt.Value;
            CustomWidgetEvaluationContext capturedSegCtx = ctx;
            segmentBtn.Click += async (_, _) =>
            {
                CustomWidgetEvaluationContext evCtx = capturedSegCtx.WithEventValue(targetValue);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: bindKey, Value: targetValue)
                    ], evCtx);
                }
                if (spec.OnChange.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
                }
            };

            row.Children.Add(segmentBtn);
        }

        hostBorder.Child = row;
        return hostBorder;
    }

    private FrameworkElement BuildBadge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawText = spec.Text ?? spec.Value ?? string.Empty;
        string text = CustomWidgetExpressionEvaluator.Interpolate(rawText, ctx);
        string resolvedTone = CustomWidgetExpressionEvaluator.Interpolate(spec.Tone, ctx);
        (Brush bg, Brush fg) = ResolveBadgeColors(resolvedTone);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badgeBorder = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(6, 2, 6, 2),
            Background = bg,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb
        };

        if (ContainsSystemMetricsToken(rawText) || ContainsSystemMetricsToken(spec.Tone))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                tb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawText, liveCtx);
                if (ContainsSystemMetricsToken(spec.Tone))
                {
                    (Brush liveBg, Brush liveFg) = ResolveBadgeColors(
                        CustomWidgetExpressionEvaluator.Interpolate(spec.Tone, liveCtx));
                    badgeBorder.Background = liveBg;
                    tb.Foreground = liveFg;
                }
            });
        }

        return badgeBorder;
    }

    private FrameworkElement BuildProgressBar(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        double pct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx, Math.Max(warn + 10, 90));

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = Math.Max(1, spec.Max),
            Value = Math.Clamp(pct, 0, Math.Max(1, spec.Max)),
            Foreground = ResolveThresholdBrush(pct, warn, danger, spec.Color),
            MinHeight = 4
        };

        if (ContainsSystemMetricsToken(rawProgress))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                double livePct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
                double liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
                double liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx, Math.Max(liveWarn + 10, 90));
                bar.Value = Math.Clamp(livePct, 0, Math.Max(1, spec.Max));
                bar.Foreground = ResolveThresholdBrush(livePct, liveWarn, liveDanger, spec.Color);
            });
        }

        return bar;
    }

    private FrameworkElement BuildWrap(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var wrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = Math.Max(0, spec.Spacing),
            VerticalSpacing = Math.Max(0, spec.Spacing)
        };

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            wrap.Children.Add(BuildNode(child, ctx));
        }

        Thickness pad = isRoot ? ResolveRootSafePadding(spec.Padding) : ParseThickness(spec.Padding);
        FrameworkElement result = pad.Left > 0 || pad.Top > 0 || pad.Right > 0 || pad.Bottom > 0
            ? new Border
            {
                Padding = pad,
                Child = wrap
            }
            : wrap;

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = result
            };
        }

        return result;
    }

    private static FrameworkElement BuildSpacer(CustomWidgetNodeSpec spec)
    {
        var spacer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (spec.Width is { } w && w > 0)
        {
            spacer.Width = w;
        }
        if (spec.Height is { } h && h > 0)
        {
            spacer.Height = h;
        }
        else if (spec.Size is { } s && s > 0)
        {
            spacer.Height = s;
            spacer.Width = s;
        }

        return spacer;
    }

    private FrameworkElement BuildToggle(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawChecked = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);
        bool isOn = string.Equals(rawChecked, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawChecked, "1", StringComparison.Ordinal);

        string label = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        string accessibleName = !string.IsNullOrWhiteSpace(label)
            ? label
            : (!string.IsNullOrWhiteSpace(bindKey) ? bindKey : "Toggle");

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            MinWidth = 0,
            OnContent = null,
            OffContent = null,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, accessibleName);

        bool suppressEvent = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        toggle.Toggled += async (_, _) =>
        {
            if (suppressEvent)
            {
                return;
            }

            suppressEvent = true;
            try
            {
                string nextVal = toggle.IsOn ? "true" : "false";
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(nextVal);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: bindKey, Value: nextVal)
                    ], evCtx);
                }

                IReadOnlyList<CustomWidgetActionSpec> followUp = spec.OnChange.Count > 0 ? spec.OnChange : spec.OnClick;
                if (followUp.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(followUp, evCtx);
                }
            }
            finally
            {
                suppressEvent = false;
            }
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            return toggle;
        }

        var row = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveForegroundBrush(spec.Color)
        };
        ApplyTypography(labelBlock, string.IsNullOrWhiteSpace(spec.Style) ? "body" : spec.Style);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(toggle, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(toggle);
        return row;
    }

    private FrameworkElement BuildProgressRing(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double size = Math.Clamp(spec.Size ?? spec.Width ?? spec.Height ?? 20, 12, 120);
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        string rawIndeterminate = spec.Indeterminate ?? string.Empty;

        bool isIndeterminate = !string.IsNullOrWhiteSpace(rawIndeterminate)
            ? CustomWidgetExpressionEvaluator.EvaluateCondition(rawIndeterminate, ctx)
            : (string.IsNullOrWhiteSpace(spec.Progress) && string.IsNullOrWhiteSpace(spec.Value));
        double pct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;

        var ring = new ProgressRing
        {
            Width = size,
            Height = size,
            Minimum = 0,
            Maximum = Math.Max(1, spec.Max),
            IsIndeterminate = isIndeterminate,
            IsActive = isIndeterminate || pct > 0,
            Value = Math.Clamp(pct, 0, Math.Max(1, spec.Max)),
            Foreground = ResolveSemanticBrush(
                string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                    ? "accent"
                    : spec.Color),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (ContainsSystemMetricsToken(rawProgress) || ContainsSystemMetricsToken(rawIndeterminate))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                bool liveIndeterminate = !string.IsNullOrWhiteSpace(rawIndeterminate)
                    ? CustomWidgetExpressionEvaluator.EvaluateCondition(rawIndeterminate, liveCtx)
                    : isIndeterminate;
                double livePct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
                ring.IsIndeterminate = liveIndeterminate;
                ring.IsActive = liveIndeterminate || livePct > 0;
                ring.Value = Math.Clamp(livePct, 0, Math.Max(1, spec.Max));
            });
        }

        return ring;
    }

    private FrameworkElement BuildStatCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;
        string rawProgress = spec.Progress ?? rawValue;
        string baseTone = spec.Color;

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };

        var valueTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx),
            FontFamily = DisplayFontFamily,
            FontSize = MetricFontSize,
            FontWeight = FontWeights.SemiBold,
            TextLineBounds = TextLineBounds.Tight,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(valueTb);

        var subtitleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(subtitleTb);

        double initialPct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double initialWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double initialDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx, Math.Max(initialWarn + 10, 92));

        var sparklineHost = new Grid
        {
            Height = 24,
            Margin = new Thickness(0, 4, 0, 2)
        };
        var areaPolygon = new Polygon
        {
            Fill = CreateSparklineAreaBrush(initialPct, initialWarn, initialDanger, baseTone),
            Stretch = Stretch.None
        };
        var linePolyline = new Polyline
        {
            Stroke = ResolveThresholdBrush(initialPct, initialWarn, initialDanger, baseTone),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None
        };
        sparklineHost.Children.Add(areaPolygon);
        sparklineHost.Children.Add(linePolyline);

        void RenderSparklinePoints(IReadOnlyList<double> history, double currentPct, CustomWidgetEvaluationContext evalCtx)
        {
            double width = sparklineHost.ActualWidth > 10 ? sparklineHost.ActualWidth : 132;
            double height = 22;
            double warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, evalCtx);
            double danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, evalCtx, Math.Max(warn + 10, 92));
            linePolyline.Stroke = ResolveThresholdBrush(currentPct, warn, danger, baseTone);
            areaPolygon.Fill = CreateSparklineAreaBrush(currentPct, warn, danger, baseTone);

            PopulateSparklineGeometry(history, currentPct, width, height, linePolyline, areaPolygon);
        }

        IReadOnlyList<double> initialHistory = CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, ctx);
        RenderSparklinePoints(initialHistory, initialPct, ctx);
        sparklineHost.SizeChanged += (_, _) =>
        {
            CustomWidgetEvaluationContext latestCtx = _viewModel.CreateEvaluationContext();
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, latestCtx) ?? 0;
            RenderSparklinePoints(
                CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, latestCtx),
                pctNow,
                latestCtx);
        };

        _liveMetricUpdaters.Add(liveCtx =>
        {
            valueTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx);
            subtitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
            RenderSparklinePoints(
                CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, liveCtx),
                pctNow,
                liveCtx);
        });

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(titleTb);
        stack.Children.Add(valueTb);
        stack.Children.Add(sparklineHost);
        stack.Children.Add(subtitleTb);

        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ResolveCardBackground("default"),
            BorderThickness = new Thickness(0),
            Child = stack
        };
    }

    private FrameworkElement BuildGpuCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string titleLabel = string.IsNullOrWhiteSpace(spec.Title)
            ? "GPU"
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx);

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTitleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftTitleRow.Children.Add(new TextBlock
        {
            Text = titleLabel,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var gpuNameTb = new TextBlock
        {
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 135,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftTitleRow.Children.Add(gpuNameTb);
        Grid.SetColumn(leftTitleRow, 0);
        headerGrid.Children.Add(leftTitleRow);

        var vramSummaryTb = new TextBlock
        {
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        SetTabularNumerals(vramSummaryTb);
        Grid.SetColumn(vramSummaryTb, 1);
        headerGrid.Children.Add(vramSummaryTb);

        var bodyGrid = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.04, GridUnitType.Star) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.96, GridUnitType.Star) });

        // Left: GPU Core % + Area Sparkline
        var coreStack = new StackPanel { Spacing = 3 };
        var gpuPctTb = new TextBlock
        {
            FontFamily = DisplayFontFamily,
            FontSize = MetricFontSize,
            FontWeight = FontWeights.SemiBold,
            TextLineBounds = TextLineBounds.Tight,
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(gpuPctTb);
        coreStack.Children.Add(gpuPctTb);

        var sparklineHost = new Grid
        {
            Height = 22,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var areaPolygon = new Polygon { Stretch = Stretch.None };
        var linePolyline = new Polyline
        {
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None
        };
        sparklineHost.Children.Add(areaPolygon);
        sparklineHost.Children.Add(linePolyline);
        coreStack.Children.Add(sparklineHost);
        Grid.SetColumn(coreStack, 0);
        bodyGrid.Children.Add(coreStack);

        // Right: Dedicated VRAM progress + free GB
        var vramStack = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var vramHeader = new Grid();
        vramHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vramHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var vramLabelTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Gpu.VramUsage", ctx.CultureName),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted")
        };
        var vramPctTb = new TextBlock
        {
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(vramPctTb);
        Grid.SetColumn(vramLabelTb, 0);
        Grid.SetColumn(vramPctTb, 1);
        vramHeader.Children.Add(vramLabelTb);
        vramHeader.Children.Add(vramPctTb);

        var vramBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            MinHeight = 4
        };

        var vramSubTb = new TextBlock
        {
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(vramSubTb);

        vramStack.Children.Add(vramHeader);
        vramStack.Children.Add(vramBar);
        vramStack.Children.Add(vramSubTb);
        Grid.SetColumn(vramStack, 1);
        bodyGrid.Children.Add(vramStack);

        string gpuTone = string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
            ? "emerald"
            : spec.Color;

        void UpdateGpuVisuals(CustomWidgetEvaluationContext evalCtx)
        {
            CustomWidgetSystemMetricsSnapshot m = evalCtx.SystemMetrics;
            double warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, evalCtx);
            double danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, evalCtx, Math.Max(warn + 10, 92));

            gpuNameTb.Text = string.IsNullOrWhiteSpace(m.GpuName) ? string.Empty : m.GpuName;
            gpuPctTb.Text = $"{m.GpuPercent:0}%";

            if (m.GpuVramTotalGb > 0.05)
            {
                vramSummaryTb.Text = $"{m.GpuVramUsedGb:0.0} / {m.GpuVramTotalGb:0.0} GB";
                vramPctTb.Text = $"{m.GpuVramPercent:0}%";
                vramBar.Value = Math.Clamp(m.GpuVramPercent, 0, 100);
                double freeGb = Math.Max(0, m.GpuVramTotalGb - m.GpuVramUsedGb);
                vramSubTb.Text = TrFormat(
                    "CustomWidget.Renderer.Gpu.FreeGbFormat",
                    evalCtx.CultureName,
                    freeGb.ToString("0.1", CultureInfo.InvariantCulture));
            }
            else
            {
                vramSummaryTb.Text = Tr("CustomWidget.Renderer.Gpu.SharedMemory", evalCtx.CultureName);
                vramPctTb.Text = "—";
                vramBar.Value = 0;
                vramSubTb.Text = Tr("CustomWidget.Renderer.Gpu.CoreLoadLive", evalCtx.CultureName);
            }

            vramBar.Foreground = ResolveThresholdBrush(m.GpuVramPercent, warn, danger, gpuTone);
            linePolyline.Stroke = ResolveThresholdBrush(m.GpuPercent, warn, danger, gpuTone);
            areaPolygon.Fill = CreateSparklineAreaBrush(m.GpuPercent, warn, danger, gpuTone);

            double width = sparklineHost.ActualWidth > 10 ? sparklineHost.ActualWidth : 136;
            PopulateSparklineGeometry(m.GpuHistory, m.GpuPercent, width, 20, linePolyline, areaPolygon);
        }

        UpdateGpuVisuals(ctx);
        sparklineHost.SizeChanged += (_, _) => UpdateGpuVisuals(_viewModel.CreateEvaluationContext());
        _liveMetricUpdaters.Add(UpdateGpuVisuals);

        var rootStack = new StackPanel { Spacing = 3 };
        rootStack.Children.Add(headerGrid);
        rootStack.Children.Add(bodyGrid);

        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ResolveCardBackground("default"),
            BorderThickness = new Thickness(0),
            Child = rootStack
        };
    }

    private sealed record ProcessRowView(
        Grid Root,
        TextBlock RankTb,
        TextBlock NameTb,
        Border BarTrack,
        Border BarFill,
        TextBlock ValueTb);

    private FrameworkElement BuildProcessList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        const int MaxSlots = 10;
        const double MiniBarTrackWidth = 42;

        string ResolveActiveSort(CustomWidgetEvaluationContext evalCtx)
        {
            if (evalCtx.State.TryGetValue("processSort", out string? st) &&
                (string.Equals(st, "cpu", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(st, "memory", StringComparison.OrdinalIgnoreCase)))
            {
                return st.ToLowerInvariant();
            }

            if (evalCtx.Settings.TryGetValue("defaultProcessSort", out string? defSort) &&
                string.Equals(defSort, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                return "cpu";
            }

            return "memory";
        }

        string activeSort = ResolveActiveSort(ctx);

        var headerGrid = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 6)
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(spec.Title)
                ? Tr("CustomWidget.Renderer.Proc.RunningApps", ctx.CultureName)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 0);
        headerGrid.Children.Add(titleTb);

        // Compact inline 2-item segmented toggle (Memory | CPU), leaving right margin for hover drag grip
        var toggleHost = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };

        var memBtn = new Button
        {
            Content = Tr("CustomWidget.Renderer.Proc.Memory", ctx.CultureName),
            CornerRadius = CornerSmall,
            Height = 22,
            MinHeight = 22,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = CaptionFontSize
        };
        var cpuBtn = new Button
        {
            Content = "CPU",
            CornerRadius = CornerSmall,
            Height = 22,
            MinHeight = 22,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = CaptionFontSize
        };
        toggleRow.Children.Add(memBtn);
        toggleRow.Children.Add(cpuBtn);
        toggleHost.Child = toggleRow;
        Grid.SetColumn(toggleHost, 1);
        headerGrid.Children.Add(toggleHost);

        var rowsStack = new StackPanel { Spacing = 5 };
        var rowViews = new ProcessRowView[MaxSlots];
        Brush trackBrush = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));

        for (int i = 0; i < MaxSlots; i++)
        {
            var rGrid = new Grid
            {
                ColumnSpacing = 8,
                MinHeight = 21,
                Visibility = Visibility.Collapsed
            };
            rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MiniBarTrackWidth) });
            rGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var rankTb = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetTabularNumerals(rankTb);

            var nameTb = new TextBlock
            {
                FontSize = Math.Max(12.0, BodyFontSize - 0.5),
                Foreground = ResolveForegroundBrush("default"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var barFill = new Border
            {
                Height = 3,
                Width = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ResolveSemanticBrush("accent")
            };
            var barTrack = new Border
            {
                Height = 3,
                Width = MiniBarTrackWidth,
                CornerRadius = new CornerRadius(1.5),
                Background = trackBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Child = barFill
            };

            var valTb = new TextBlock
            {
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetTabularNumerals(valTb);

            Grid.SetColumn(rankTb, 0);
            Grid.SetColumn(nameTb, 1);
            Grid.SetColumn(barTrack, 2);
            Grid.SetColumn(valTb, 3);
            rGrid.Children.Add(rankTb);
            rGrid.Children.Add(nameTb);
            rGrid.Children.Add(barTrack);
            rGrid.Children.Add(valTb);

            WidgetTextShadow.SetCast(rankTb, false);
            WidgetTextShadow.SetCast(nameTb, false);
            WidgetTextShadow.SetCast(valTb, false);
            rowViews[i] = new ProcessRowView(rGrid, rankTb, nameTb, barTrack, barFill, valTb);
            rowsStack.Children.Add(rGrid);
        }

        var emptyHintTb = new TextBlock
        {
            Text = Tr("CustomWidget.Renderer.Proc.Loading", ctx.CultureName),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = Visibility.Collapsed
        };
        WidgetTextShadow.SetCast(emptyHintTb, false);
        rowsStack.Children.Add(emptyHintTb);

        string? appliedSort = null;
        bool? appliedSortDark = null;

        void UpdateSortPillStyles()
        {
            bool byCpu = string.Equals(activeSort, "cpu", StringComparison.OrdinalIgnoreCase);
            bool currentDark = IsDark;
            if (string.Equals(appliedSort, activeSort, StringComparison.Ordinal) &&
                appliedSortDark == currentDark)
            {
                return;
            }

            appliedSort = activeSort;
            appliedSortDark = currentDark;
            ApplySegmentedItemState(memBtn, !byCpu);
            ApplySegmentedItemState(cpuBtn, byCpu);
        }

        void UpdateProcessRows(CustomWidgetEvaluationContext evalCtx)
        {
            activeSort = ResolveActiveSort(evalCtx);
            UpdateSortPillStyles();

            int topCount = 5;
            if (evalCtx.Settings.TryGetValue("topProcessCount", out string? countStr) &&
                int.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount))
            {
                topCount = Math.Clamp(parsedCount, 1, MaxSlots);
            }

            bool byCpu = string.Equals(activeSort, "cpu", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<CustomWidgetProcessEntry> source = byCpu
                ? evalCtx.SystemMetrics.TopProcessesByCpu
                : evalCtx.SystemMetrics.TopProcessesByMemory;

            int visible = Math.Min(topCount, Math.Min(MaxSlots, source.Count));
            emptyHintTb.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;

            double maxRef = 1.0;
            if (visible > 0)
            {
                maxRef = byCpu
                    ? Math.Max(10.0, source[0].CpuPercent)
                    : Math.Max(64.0 * 1024.0 * 1024.0, source[0].MemoryBytes);
            }

            Brush fillBrush = ResolveSemanticBrush("accent");

            for (int i = 0; i < MaxSlots; i++)
            {
                ProcessRowView view = rowViews[i];
                if (i >= visible)
                {
                    view.Root.Visibility = Visibility.Collapsed;
                    continue;
                }

                CustomWidgetProcessEntry entry = source[i];
                view.Root.Visibility = Visibility.Visible;
                view.NameTb.Text = entry.GetLocalizedName(evalCtx.CultureName);
                view.ValueTb.Text = byCpu ? entry.CpuText : entry.MemoryText;
                view.BarFill.Background = fillBrush;

                double metricVal = byCpu ? entry.CpuPercent : entry.MemoryBytes;
                double ratio = Math.Clamp(metricVal / maxRef, 0.06, 1.0);
                view.BarFill.Width = Math.Round(MiniBarTrackWidth * ratio, 1);
            }
        }

        memBtn.Click += (_, _) =>
        {
            _viewModel.SetStateValueWithoutRebuild("processSort", "memory");
            UpdateProcessRows(_viewModel.CreateEvaluationContext());
        };
        cpuBtn.Click += (_, _) =>
        {
            _viewModel.SetStateValueWithoutRebuild("processSort", "cpu");
            UpdateProcessRows(_viewModel.CreateEvaluationContext());
        };

        UpdateProcessRows(ctx);
        _liveMetricUpdaters.Add(UpdateProcessRows);

        var cardStack = new StackPanel { Spacing = 2 };
        cardStack.Children.Add(headerGrid);
        cardStack.Children.Add(rowsStack);

        return new Border
        {
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(2, 8, 2, 2),
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = cardStack
        };
    }

    private static void SetOrAppendPoint(PointCollection points, int index, Point point)
    {
        if (index < points.Count)
        {
            points[index] = point;
        }
        else
        {
            points.Add(point);
        }
    }

    private static void TrimTrailingPoints(PointCollection points, int desiredCount)
    {
        while (points.Count > desiredCount)
        {
            points.RemoveAt(points.Count - 1);
        }
    }

    private static void PopulateSparklineGeometry(
        IReadOnlyList<double> history,
        double currentPct,
        double width,
        double height,
        Polyline linePolyline,
        Polygon areaPolygon)
    {
        PointCollection linePts = linePolyline.Points ??= [];
        PointCollection areaPts = areaPolygon.Points ??= [];
        int lineCount = 0;
        int areaCount = 0;

        if (history.Count == 0)
        {
            double y = height - (Math.Clamp(currentPct, 0, 100) / 100.0 * (height - 4)) - 2;
            SetOrAppendPoint(linePts, lineCount++, new Point(0, y));
            SetOrAppendPoint(linePts, lineCount++, new Point(width, y));
            SetOrAppendPoint(areaPts, areaCount++, new Point(0, height));
            SetOrAppendPoint(areaPts, areaCount++, new Point(0, y));
            SetOrAppendPoint(areaPts, areaCount++, new Point(width, y));
            SetOrAppendPoint(areaPts, areaCount++, new Point(width, height));
        }
        else
        {
            int count = history.Count;
            double stepX = count > 1 ? width / (count - 1) : width;
            SetOrAppendPoint(areaPts, areaCount++, new Point(0, height));
            for (int i = 0; i < count; i++)
            {
                double x = i * stepX;
                double v = Math.Clamp(history[i], 0, 100);
                double y = height - (v / 100.0 * (height - 4)) - 2;
                var pt = new Point(x, y);
                SetOrAppendPoint(linePts, lineCount++, pt);
                SetOrAppendPoint(areaPts, areaCount++, pt);
            }
            if (count == 1)
            {
                double y = linePts[0].Y;
                var pt = new Point(width, y);
                SetOrAppendPoint(linePts, lineCount++, pt);
                SetOrAppendPoint(areaPts, areaCount++, pt);
            }
            SetOrAppendPoint(areaPts, areaCount++, new Point(width, height));
        }

        TrimTrailingPoints(linePts, lineCount);
        TrimTrailingPoints(areaPts, areaCount);
    }

    private FrameworkElement BuildPomodoroCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isStandaloneHero = false)
    {
        CustomWidgetPomodoroSnapshot snap = ctx.GetOrComputePomodoroSnapshot();

        Color phaseColor = ResolvePomodoroPhaseColor(snap.Phase);
        SolidColorBrush phaseBrush = GetOrCreateSolidBrush(phaseColor);
        Brush transparentBrush = GetOrCreateSolidBrush(Colors.Transparent);

        // Open continuous stage on the window backdrop (zero nested gray box)
        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(0),
            Padding = !string.IsNullOrWhiteSpace(spec.Padding)
                ? ParseThickness(spec.Padding)
                : (isStandaloneHero ? new Thickness(4, 2, 4, 4) : new Thickness(4, 4, 4, 6)),
            Background = transparentBrush,
            BorderThickness = new Thickness(0)
        };

        var rootStack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Layer 1: Unified 180x180 Fluent Dial (Phase Switcher + 44px Display Digits + 4 Cycle Dots)
        const double ringSize = 180.0;
        const double cx = ringSize / 2.0;
        const double cy = ringSize / 2.0;
        const double radius = 82.0;
        const double strokeWidth = 4.5;

        var ringHost = new Grid
        {
            Width = ringSize,
            Height = ringSize,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (!string.IsNullOrEmpty(snap.DialSubtext))
        {
            ToolTipService.SetToolTip(ringHost, snap.DialSubtext);
        }

        // Neutral frosted track
        var trackPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = ringSize,
            Height = ringSize,
            StrokeThickness = strokeWidth,
            Stroke = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0)),
            Data = new EllipseGeometry
            {
                Center = new Point(cx, cy),
                RadiusX = radius,
                RadiusY = radius
            }
        };
        ringHost.Children.Add(trackPath);

        var fullRingGeometry = new EllipseGeometry
        {
            Center = new Point(cx, cy),
            RadiusX = radius,
            RadiusY = radius
        };
        var arcSegment = new ArcSegment
        {
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        };
        var arcFigure = new PathFigure
        {
            StartPoint = new Point(cx, cy - radius),
            IsClosed = false,
            IsFilled = false
        };
        arcFigure.Segments.Add(arcSegment);
        var partialArcGeometry = new PathGeometry();
        partialArcGeometry.Figures.Add(arcFigure);

        var arcPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = ringSize,
            Height = ringSize,
            StrokeThickness = strokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = phaseBrush
        };
        double initialRemainingRatio = snap.TotalSeconds > 0
            ? Math.Clamp((double)snap.RemainingSeconds / snap.TotalSeconds, 0.0, 1.0)
            : 1.0;
        UpdatePomodoroRingArc(
            arcPath,
            fullRingGeometry,
            partialArcGeometry,
            arcFigure,
            arcSegment,
            cx,
            cy,
            radius,
            initialRemainingRatio,
            snap.IsRunning,
            snap.IsPaused);
        ringHost.Children.Add(arcPath);

        var dialInnerGrid = new Grid
        {
            Width = 146,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        dialInnerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        dialInnerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        dialInnerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        // Row 0: Quiet phase switcher ("● 专注 ▾") with 6px Fluent hover surface
        var pillIndicatorHost = new Grid
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = phaseBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = snap.IsPaused ? Visibility.Collapsed : Visibility.Visible
        };
        var pillPauseBarLeft = new Border
        {
            Width = 2,
            Height = 7,
            CornerRadius = new CornerRadius(1),
            Background = phaseBrush
        };
        var pillPauseBarRight = new Border
        {
            Width = 2,
            Height = 7,
            CornerRadius = new CornerRadius(1),
            Background = phaseBrush
        };
        var pillPausedBars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = snap.IsPaused ? Visibility.Visible : Visibility.Collapsed
        };
        pillPausedBars.Children.Add(pillPauseBarLeft);
        pillPausedBars.Children.Add(pillPauseBarRight);
        pillIndicatorHost.Children.Add(pillDot);
        pillIndicatorHost.Children.Add(pillPausedBars);

        var headerPillTb = new TextBlock
        {
            Text = snap.HeaderPillText,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(headerPillTb);

        var pillChevron = new FontIcon
        {
            Glyph = "\uE972",
            FontSize = 8.5,
            Foreground = ResolveForegroundBrush("muted"),
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var pillRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillRow.Children.Add(pillIndicatorHost);
        pillRow.Children.Add(headerPillTb);
        pillRow.Children.Add(pillChevron);

        var phaseMenu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false
        };
        TrackInteractiveFlyout(phaseMenu);
        phaseMenu.Opening += (_, _) =>
        {
            phaseMenu.Items.Clear();
            CustomWidgetEvaluationContext menuCtx = _viewModel.CreateEvaluationContext();
            var prefs = CustomWidgetPomodoroEngine.ReadPreferences(menuCtx.Settings);
            CustomWidgetPomodoroSnapshot menuSnap = menuCtx.GetOrComputePomodoroSnapshot();

            void AddPhaseItem(string phaseKey, string title)
            {
                bool isCurrent = string.Equals(menuSnap.Phase, phaseKey, StringComparison.Ordinal);
                var item = new ToggleMenuFlyoutItem
                {
                    Text = title,
                    IsChecked = isCurrent
                };
                item.Click += async (_, _) =>
                {
                    if (!isCurrent)
                    {
                        await _viewModel.ExecuteActionsAsync(
                            [new CustomWidgetActionSpec("pomodoro.setPhase", Value: phaseKey)]);
                    }
                };
                phaseMenu.Items.Add(item);
            }

            AddPhaseItem(
                CustomWidgetPomodoroEngine.PhaseFocus,
                TrFormat("CustomWidget.Renderer.Pomo.FocusMinutesFormat", menuCtx.CultureName, prefs.FocusMin));
            AddPhaseItem(
                CustomWidgetPomodoroEngine.PhaseShortBreak,
                TrFormat("CustomWidget.Renderer.Pomo.ShortBreakMinutesFormat", menuCtx.CultureName, prefs.ShortBreakMin));
            AddPhaseItem(
                CustomWidgetPomodoroEngine.PhaseLongBreak,
                TrFormat("CustomWidget.Renderer.Pomo.LongBreakMinutesFormat", menuCtx.CultureName, prefs.LongBreakMin));
        };

        var headerPillBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 7, 0),
            CornerRadius = CornerMedium,
            BorderThickness = new Thickness(0),
            BorderBrush = transparentBrush,
            Background = transparentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = phaseMenu,
            Content = pillRow
        };
        ApplyButtonStateOverrides(headerPillBtn, SubtleHoverBrush, SubtlePressedBrush, transparentBrush, transparentBrush);
        ToolTipService.SetToolTip(headerPillBtn, Tr("CustomWidget.Renderer.Pomo.SelectPhaseTooltip", ctx.CultureName));
        Grid.SetRow(headerPillBtn, 0);
        dialInnerGrid.Children.Add(headerPillBtn);

        // Row 1: Display 44px SemiBold Tabular Countdown
        var timeTb = new TextBlock
        {
            Text = snap.TimeText,
            FontFamily = DisplayFontFamily,
            FontSize = snap.TimeText.Length > 5 ? 34 : 44,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = -12,
            TextLineBounds = TextLineBounds.Tight,
            Foreground = ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(timeTb);
        Grid.SetRow(timeTb, 1);
        dialInnerGrid.Children.Add(timeTb);

        // Row 2: 4 Refined Cycle Indicators
        var dotsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dotBorders = new Border[4];
        for (int i = 0; i < 4; i++)
        {
            var dot = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2)
            };
            dotBorders[i] = dot;
            dotsRow.Children.Add(dot);
        }
        UpdatePomodoroCycleDots(dotBorders, snap.CycleInSet, snap.TodayCompleted, snap.Phase, phaseColor);
        Grid.SetRow(dotsRow, 2);
        dialInnerGrid.Children.Add(dotsRow);

        ringHost.Children.Add(dialInnerGrid);
        rootStack.Children.Add(ringHost);

        // Layer 2: Transport Bar aligned with MusicWidgetContent (30x30 r=6 | 36x36 r=8 elevated | 30x30 r=6)
        var actionGrid = new Grid
        {
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        Brush sideIconBrush = ResolveForegroundBrush("secondary");

        // Left 30x30 r=6 Subtle Button: "+5m" while running, active reset when paused, dimmed reset when idle
        Button leftBtn;
        if (snap.IsRunning)
        {
            var extendTb = new TextBlock
            {
                Text = "+5m",
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = sideIconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetTabularNumerals(extendTb);

            leftBtn = new Button
            {
                Width = 30,
                Height = 30,
                MinWidth = 30,
                MinHeight = 30,
                Padding = new Thickness(0),
                CornerRadius = CornerMedium,
                BorderThickness = new Thickness(0),
                BorderBrush = transparentBrush,
                Background = transparentBrush,
                UseSystemFocusVisuals = true,
                Content = extendTb
            };
            ApplyButtonStateOverrides(leftBtn, SubtleHoverBrush, SubtlePressedBrush, transparentBrush, transparentBrush);
            ToolTipService.SetToolTip(leftBtn, Tr("CustomWidget.Renderer.Pomo.Extend5MinTooltip", ctx.CultureName));
            leftBtn.Click += async (_, _) =>
            {
                await _viewModel.ExecuteActionsAsync([new CustomWidgetActionSpec("pomodoro.extend", Value: "300")]);
            };
        }
        else
        {
            leftBtn = CreateSubtleIconButton(
                glyph: "\uE72C",
                size: 30,
                iconSize: 13,
                tooltip: snap.IsPaused ? Tr("CustomWidget.Renderer.Pomo.ResetPhaseTooltip", ctx.CultureName) : null);
            leftBtn.Opacity = snap.IsPaused ? 1.0 : 0.34;
            leftBtn.IsHitTestVisible = snap.IsPaused;
            leftBtn.Click += async (_, _) =>
            {
                await _viewModel.ExecuteActionsAsync([new CustomWidgetActionSpec("pomodoro.reset")]);
            };
        }
        Grid.SetColumn(leftBtn, 0);
        actionGrid.Children.Add(leftBtn);

        // Center 36x36 r=8 Elevated WinUI Transport Button (MusicPrimaryTransportButtonStyle parity)
        var primaryIcon = new FontIcon
        {
            Glyph = snap.IsRunning ? "\uE769" : "\uE768",
            FontSize = 14,
            Foreground = ResolveForegroundBrush("default")
        };
        Button primaryBtn = CreateElevatedTransportButton(primaryIcon, size: 36);
        ToolTipService.SetToolTip(
            primaryBtn,
            snap.IsRunning
                ? Tr("CustomWidget.Renderer.Pomo.PauseTooltip", ctx.CultureName)
                : (snap.IsPaused ? Tr("CustomWidget.Renderer.Pomo.ResumeTooltip", ctx.CultureName) : snap.PrimaryActionText));
        primaryBtn.Click += async (_, _) =>
        {
            await _viewModel.ExecuteActionsAsync([new CustomWidgetActionSpec("pomodoro.toggle")]);
        };
        Grid.SetColumn(primaryBtn, 1);
        actionGrid.Children.Add(primaryBtn);

        // Right 30x30 r=6 Subtle Skip Button
        Button skipBtn = CreateSubtleIconButton(
            glyph: "\uE893",
            size: 30,
            iconSize: 13,
            tooltip: Tr("CustomWidget.Renderer.Pomo.SkipPhaseTooltip", ctx.CultureName));
        skipBtn.Click += async (_, _) =>
        {
            await _viewModel.ExecuteActionsAsync([new CustomWidgetActionSpec("pomodoro.skip")]);
        };
        Grid.SetColumn(skipBtn, 2);
        actionGrid.Children.Add(skipBtn);

        rootStack.Children.Add(actionGrid);

        // Register lightweight 1Hz in-place updater so countdown never rebuilds the XAML tree
        _liveMetricUpdaters.Add(liveCtx =>
        {
            CustomWidgetPomodoroSnapshot liveSnap = liveCtx.GetOrComputePomodoroSnapshot();

            Color livePhaseColor = ResolvePomodoroPhaseColor(liveSnap.Phase);
            SolidColorBrush livePhaseBrush = GetOrCreateSolidBrush(livePhaseColor);

            arcPath.Stroke = livePhaseBrush;
            pillDot.Background = livePhaseBrush;
            pillPauseBarLeft.Background = livePhaseBrush;
            pillPauseBarRight.Background = livePhaseBrush;

            headerPillTb.Text = liveSnap.HeaderPillText;
            pillDot.Visibility = liveSnap.IsPaused ? Visibility.Collapsed : Visibility.Visible;
            pillPausedBars.Visibility = liveSnap.IsPaused ? Visibility.Visible : Visibility.Collapsed;

            primaryIcon.Glyph = liveSnap.IsRunning ? "\uE769" : "\uE768";
            ToolTipService.SetToolTip(
                primaryBtn,
                liveSnap.IsRunning
                    ? Tr("CustomWidget.Renderer.Pomo.PauseTooltip", liveCtx.CultureName)
                    : (liveSnap.IsPaused ? Tr("CustomWidget.Renderer.Pomo.ResumeTooltip", liveCtx.CultureName) : liveSnap.PrimaryActionText));

            timeTb.Text = liveSnap.TimeText;
            timeTb.FontSize = liveSnap.TimeText.Length > 5 ? 34 : 44;
            ToolTipService.SetToolTip(
                ringHost,
                string.IsNullOrEmpty(liveSnap.DialSubtext) ? null : liveSnap.DialSubtext);

            double remRatio = liveSnap.TotalSeconds > 0
                ? Math.Clamp((double)liveSnap.RemainingSeconds / liveSnap.TotalSeconds, 0.0, 1.0)
                : 1.0;
            UpdatePomodoroRingArc(
                arcPath,
                fullRingGeometry,
                partialArcGeometry,
                arcFigure,
                arcSegment,
                cx,
                cy,
                radius,
                remRatio,
                liveSnap.IsRunning,
                liveSnap.IsPaused);
            UpdatePomodoroCycleDots(
                dotBorders,
                liveSnap.CycleInSet,
                liveSnap.TodayCompleted,
                liveSnap.Phase,
                livePhaseColor);
        });

        cardBorder.Child = rootStack;
        return cardBorder;
    }

    private static void ApplyButtonStateOverrides(
        Button btn,
        Brush hoverBg,
        Brush pressedBg,
        Brush hoverBorder,
        Brush pressedBorder)
    {
        btn.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        btn.Resources["ButtonBackgroundPressed"] = pressedBg;
        btn.Resources["ButtonBorderBrushPointerOver"] = hoverBorder;
        btn.Resources["ButtonBorderBrushPressed"] = pressedBorder;
    }

    private Color ResolvePomodoroPhaseColor(string phase)
    {
        bool dark = IsDark;
        return CustomWidgetPomodoroEngine.NormalizePhase(phase) switch
        {
            CustomWidgetPomodoroEngine.PhaseShortBreak =>
                dark ? Color.FromArgb(255, 52, 211, 153) : Color.FromArgb(255, 16, 185, 129),
            CustomWidgetPomodoroEngine.PhaseLongBreak =>
                dark ? Color.FromArgb(255, 56, 189, 248) : Color.FromArgb(255, 14, 165, 233),
            _ =>
                dark ? Color.FromArgb(255, 255, 133, 51) : Color.FromArgb(255, 234, 88, 12)
        };
    }

    private void UpdatePomodoroCycleDots(
        Border[] dotBorders,
        int cycleInSet,
        int todayCompleted,
        string phase,
        Color phaseColor)
    {
        bool isFocus = string.Equals(phase, CustomWidgetPomodoroEngine.PhaseFocus, StringComparison.Ordinal);
        Brush inactiveNeutral = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0));

        if (isFocus)
        {
            Brush activePhaseBrush = GetOrCreateSolidBrush(phaseColor);
            for (int i = 0; i < dotBorders.Length; i++)
            {
                int roundIdx = i + 1;
                Border dot = dotBorders[i];
                if (roundIdx == cycleInSet)
                {
                    dot.Width = 12;
                    dot.Background = activePhaseBrush;
                    dot.Opacity = 1.0;
                }
                else if (roundIdx < cycleInSet)
                {
                    dot.Width = 4;
                    dot.Background = activePhaseBrush;
                    dot.Opacity = 0.85;
                }
                else
                {
                    dot.Width = 4;
                    dot.Background = inactiveNeutral;
                    dot.Opacity = 1.0;
                }
            }
        }
        else
        {
            int completedInCurrentSet = string.Equals(phase, CustomWidgetPomodoroEngine.PhaseLongBreak, StringComparison.Ordinal) &&
                                        todayCompleted > 0 &&
                                        todayCompleted % 4 == 0
                ? 4
                : (todayCompleted % 4);
            Brush completedNeutral = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(132, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0));

            for (int i = 0; i < dotBorders.Length; i++)
            {
                Border dot = dotBorders[i];
                dot.Width = 4;
                dot.Background = (i + 1) <= completedInCurrentSet ? completedNeutral : inactiveNeutral;
                dot.Opacity = 1.0;
            }
        }
    }

    private static void UpdatePomodoroRingArc(
        Microsoft.UI.Xaml.Shapes.Path arcPath,
        EllipseGeometry fullRingGeometry,
        PathGeometry partialArcGeometry,
        PathFigure arcFigure,
        ArcSegment arcSegment,
        double cx,
        double cy,
        double radius,
        double remainingRatio,
        bool isRunning,
        bool isPaused)
    {
        bool isActive = isRunning || isPaused;
        if (!isActive && remainingRatio >= 0.9995)
        {
            // Untouched idle: hide colored arc so the neutral frosted glass track stays clean on any wallpaper
            arcPath.Visibility = Visibility.Collapsed;
            return;
        }

        arcPath.Opacity = isPaused ? 0.88 : 1.0;
        if (remainingRatio >= 0.9995)
        {
            // Full duration at start (e.g. 25:00): seamless 360-degree closed ring with zero 12-o'clock gap
            arcPath.Visibility = Visibility.Visible;
            if (!ReferenceEquals(arcPath.Data, fullRingGeometry))
            {
                arcPath.Data = fullRingGeometry;
            }
            return;
        }

        double angleDeg = Math.Clamp(remainingRatio * 360.0, 0.0, 359.85);
        if (angleDeg <= 0.25)
        {
            arcPath.Visibility = Visibility.Collapsed;
            return;
        }

        arcPath.Visibility = Visibility.Visible;
        arcFigure.StartPoint = new Point(cx, cy - radius);
        double endRad = (angleDeg - 90.0) * Math.PI / 180.0;
        arcSegment.Point = new Point(
            cx + radius * Math.Cos(endRad),
            cy + radius * Math.Sin(endRad));
        arcSegment.Size = new Size(radius, radius);
        arcSegment.IsLargeArc = angleDeg > 180.0;

        if (!ReferenceEquals(arcPath.Data, partialArcGeometry))
        {
            arcPath.Data = partialArcGeometry;
        }
    }

    private FrameworkElement BuildProgressRow(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;
        string rawProgress = spec.Progress ?? "0";

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };
        var valueTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(valueTb);
        Grid.SetColumn(titleTb, 0);
        Grid.SetColumn(valueTb, 1);
        headerGrid.Children.Add(titleTb);
        headerGrid.Children.Add(valueTb);

        double initialPct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double initialWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double initialDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx, Math.Max(initialWarn + 10, 92));
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(initialPct, 0, 100),
            Foreground = ResolveThresholdBrush(initialPct, initialWarn, initialDanger, spec.Color),
            MinHeight = 4
        };

        var subtitleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            Visibility = string.IsNullOrWhiteSpace(rawSubtitle) ? Visibility.Collapsed : Visibility.Visible
        };
        SetTabularNumerals(subtitleTb);

        _liveMetricUpdaters.Add(liveCtx =>
        {
            titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            valueTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx);
            subtitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
            double liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
            double liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx, Math.Max(liveWarn + 10, 92));
            progressBar.Value = Math.Clamp(pctNow, 0, 100);
            progressBar.Foreground = ResolveThresholdBrush(pctNow, liveWarn, liveDanger, spec.Color);
        });

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(progressBar);
        stack.Children.Add(subtitleTb);
        return stack;
    }

    private FrameworkElement BuildKeyValueRow(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string? resolvedRowIcon = ResolveNormalizedIcon(spec.Icon, ctx);
        if (!string.IsNullOrWhiteSpace(resolvedRowIcon))
        {
            FrameworkElement icon = CreateInlineIconElement(
                resolvedRowIcon,
                13,
                ResolveForegroundBrush("muted"));
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 1);
        grid.Children.Add(titleTb);

        var valueTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(valueTb);
        Grid.SetColumn(valueTb, 2);
        grid.Children.Add(valueTb);

        if (ContainsSystemMetricsToken(rawValue) || ContainsSystemMetricsToken(rawTitle))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
                valueTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx);
            });
        }

        return grid;
    }

    private FrameworkElement BuildFileChip(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string fileName = CustomWidgetExpressionEvaluator.Interpolate(spec.FileName, ctx);
        string filePath = CustomWidgetExpressionEvaluator.Interpolate(spec.FilePath, ctx);
        string mode = CustomWidgetExpressionEvaluator.Interpolate(spec.StorageMode, ctx);

        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(filePath))
        {
            fileName = System.IO.Path.GetFileName(filePath);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new Grid { Visibility = Visibility.Collapsed };
        }

        bool isCopy = string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new FontIcon
        {
            Glyph = isCopy ? "\uE723" : "\uE71B",
            FontSize = 11.5,
            Foreground = ResolveForegroundBrush("accent")
        });
        var chipTb = new TextBlock
        {
            Text = fileName,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetTextShadow.SetCast(chipTb, false);
        row.Children.Add(chipTb);

        var chipBtn = new Button
        {
            Content = row,
            CornerRadius = CornerSmall,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ResolveCardBackground("subtle"),
            BorderThickness = new Thickness(0)
        };

        string resolvedTooltipPath = _viewModel.Store.ResolveRuntimePath(filePath);
        ToolTipService.SetToolTip(chipBtn, string.IsNullOrWhiteSpace(resolvedTooltipPath) ? filePath : resolvedTooltipPath);

        chipBtn.Click += (_, _) => _viewModel.OpenFileWithDefaultApp(filePath);
        chipBtn.RightTapped += (_, e) =>
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            var openItem = new MenuFlyoutItem
            {
                Text = Tr("Context.Open"),
                Icon = new FontIcon { Glyph = "\uE8E5" }
            };
            openItem.Click += (_, _) => _viewModel.OpenFileWithDefaultApp(filePath);

            var revealItem = new MenuFlyoutItem
            {
                Text = Tr("Context.OpenFileLocation"),
                Icon = new FontIcon { Glyph = "\uEC50" }
            };
            revealItem.Click += (_, _) => _viewModel.RevealFileInExplorer(filePath);

            flyout.Items.Add(openItem);
            flyout.Items.Add(revealItem);
            ShowTrackedFlyoutAt(flyout, chipBtn);
        };

        return chipBtn;
    }

    private FrameworkElement BuildEmptyState(
        CustomWidgetEmptyStateSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var stack = new StackPanel
        {
            MaxWidth = 220,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 24, 16, 24)
        };

        string emptyIcon = ResolveNormalizedIcon(spec.Icon, ctx) ?? "\uE70F";
        FrameworkElement emptyIconElement = CreateInlineIconElement(
            emptyIcon,
            22,
            ResolveForegroundBrush("muted"));
        emptyIconElement.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(emptyIconElement);

        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
                FontSize = BodyFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 220
            });
        }

        return stack;
    }

    private static string? ResolveNormalizedIcon(string? rawIcon, CustomWidgetEvaluationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(rawIcon))
        {
            return null;
        }

        string interpolated = CustomWidgetExpressionEvaluator.Interpolate(rawIcon, ctx).Trim();
        if (string.IsNullOrWhiteSpace(interpolated))
        {
            return null;
        }

        return CustomWidgetManifestParser.NormalizeGlyph(interpolated);
    }

    private static void UpdateInlineIconGlyph(FrameworkElement iconEl, string rawGlyph)
    {
        if (string.IsNullOrWhiteSpace(rawGlyph))
        {
            return;
        }

        string normalized = CustomWidgetManifestParser.NormalizeGlyph(rawGlyph);
        if (iconEl is FontIcon fi && CustomWidgetTemplates.IsSegoeFluentGlyph(normalized))
        {
            fi.Glyph = normalized;
        }
        else if (iconEl is TextBlock tb)
        {
            tb.Text = normalized.Trim();
        }
    }

    private static FrameworkElement CreateInlineIconElement(
        string icon,
        double fontSize,
        Brush? foreground = null)
    {
        string normalized = CustomWidgetManifestParser.NormalizeGlyph(icon);
        if (!CustomWidgetTemplates.IsSegoeFluentGlyph(normalized))
        {
            var symbolTb = new TextBlock
            {
                Text = normalized.Trim(),
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (foreground is not null && !CustomWidgetTemplates.IsEmojiIcon(normalized))
            {
                symbolTb.Foreground = foreground;
            }
            WidgetTextShadow.SetCast(symbolTb, false);
            return symbolTb;
        }

        var fontIcon = new FontIcon
        {
            Glyph = normalized,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (foreground is not null)
        {
            fontIcon.Foreground = foreground;
        }

        return fontIcon;
    }

    private static InfoBar BuildDiagnosticBanner(IReadOnlyList<CustomWidgetValidationDiagnostic> diagnostics)
    {
        CustomWidgetValidationDiagnostic first = diagnostics.First(d => d.IsError);
        return new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = true,
            Title = $"manifest.json ({first.Path})",
            Message = first.Message
        };
    }

    private static void ApplyCommonLayoutProperties(FrameworkElement element, CustomWidgetNodeSpec spec)
    {
        if (spec.Width.HasValue) element.Width = spec.Width.Value;
        if (spec.Height.HasValue) element.Height = spec.Height.Value;
        if (spec.MinHeight.HasValue) element.MinHeight = spec.MinHeight.Value;

        if (!string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
        {
            element.HorizontalAlignment = spec.HorizontalAlignment.ToLowerInvariant() switch
            {
                "left" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch
            };
        }

        if (!string.IsNullOrWhiteSpace(spec.VerticalAlignment))
        {
            element.VerticalAlignment = spec.VerticalAlignment.ToLowerInvariant() switch
            {
                "top" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "bottom" => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch
            };
        }
    }

    private static void SetTabularNumerals(TextBlock tb)
    {
        try
        {
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        }
        catch
        {
            // Headless unit test fallback if XAML typography metadata is unavailable.
        }
    }

    private static double ResolveEffectiveThreshold(
        string? expression,
        double fallback,
        CustomWidgetEvaluationContext ctx,
        double? secondaryFallback = null)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            double? evaluated = CustomWidgetExpressionEvaluator.EvaluateDouble(expression, ctx);
            if (evaluated.HasValue && evaluated.Value > 0)
            {
                return evaluated.Value;
            }
        }

        return secondaryFallback ?? fallback;
    }

    private void ApplyTypography(TextBlock tb, string style)
    {
        switch (style.ToLowerInvariant())
        {
            case "caption":
                tb.FontSize = CaptionFontSize;
                break;
            case "bodystrong":
                tb.FontSize = BodyFontSize;
                tb.FontWeight = FontWeights.SemiBold;
                break;
            case "subtitle":
                tb.FontSize = SubtitleFontSize;
                tb.FontWeight = FontWeights.SemiBold;
                break;
            case "title":
                tb.FontFamily = DisplayFontFamily;
                tb.FontSize = MetricFontSize;
                tb.FontWeight = FontWeights.SemiBold;
                SetTabularNumerals(tb);
                break;
            case "hero":
                tb.FontFamily = DisplayFontFamily;
                tb.FontSize = 28;
                tb.FontWeight = FontWeights.SemiBold;
                tb.TextLineBounds = TextLineBounds.Tight;
                SetTabularNumerals(tb);
                break;
            case "mono":
                tb.FontSize = Math.Max(12.0, BodyFontSize - 0.5);
                tb.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                SetTabularNumerals(tb);
                break;
            default:
                tb.FontSize = BodyFontSize;
                break;
        }
    }

    /// <summary>
    /// Resolves semantic brushes explicitly against the widget's actual theme polarity so
    /// Dark-mode text/strokes always maintain WCAG relative luminance &gt; 0.30 (preventing
    /// white LightHalo text shadows on dark glass), and Light-mode inks stay crisp and dark.
    /// </summary>
    private SolidColorBrush ResolveSemanticBrush(string semanticToken)
    {
        bool dark = IsDark;
        Color color = semanticToken.ToLowerInvariant() switch
        {
            "coral" or "orange" => dark
                ? Color.FromArgb(255, 251, 146, 60)  // #FB923C (L = 0.42)
                : Color.FromArgb(255, 234, 88, 12),   // #EA580C (L = 0.17)
            "violet" or "purple" => dark
                ? Color.FromArgb(255, 167, 139, 250) // #A78BFA (L = 0.34)
                : Color.FromArgb(255, 109, 40, 217),  // #6D28D9 (L = 0.09)
            "emerald" or "success" => dark
                ? Color.FromArgb(255, 52, 211, 153)  // #34D399 (L = 0.55)
                : Color.FromArgb(255, 4, 120, 87),    // #047857 (L = 0.14)
            "cyan" => dark
                ? Color.FromArgb(255, 56, 189, 248)  // #38BDF8 (L = 0.44)
                : Color.FromArgb(255, 3, 105, 161),   // #0369A1 (L = 0.13)
            "warning" => dark
                ? Color.FromArgb(255, 251, 191, 36)  // #FBBF24 (L = 0.57)
                : Color.FromArgb(255, 180, 83, 9),    // #B45309 (L = 0.12)
            "danger" => dark
                ? Color.FromArgb(255, 248, 113, 113) // #F87171 (L = 0.32)
                : Color.FromArgb(255, 220, 38, 38),   // #DC2626 (L = 0.14)
            _ => dark
                ? Color.FromArgb(255, 96, 165, 250)  // #60A5FA (L = 0.35)
                : Color.FromArgb(255, 37, 99, 235)    // #2563EB (L = 0.12)
        };
        return GetOrCreateSolidBrush(color);
    }

    private SolidColorBrush GetOrCreateSolidBrush(Color color)
    {
        if (!_solidBrushCache.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            _solidBrushCache[color] = brush;
        }

        return brush;
    }

    private Brush ResolveForegroundBrush(string colorToken)
    {
        return colorToken.ToLowerInvariant() switch
        {
            "secondary" => GetBrush(
                "TextFillColorSecondaryBrush",
                IsDark ? Color.FromArgb(196, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0)),
            "muted" => GetBrush(
                "TextFillColorTertiaryBrush",
                IsDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(116, 0, 0, 0)),
            "accent" or "coral" or "orange" or "violet" or "purple" or "emerald" or "cyan" or "success" or "warning" or "danger" =>
                ResolveSemanticBrush(colorToken),
            _ => GetBrush(
                "TextFillColorPrimaryBrush",
                IsDark ? Colors.White : Color.FromArgb(228, 0, 0, 0))
        };
    }

    private Brush ResolveCardBackground(string tone)
    {
        return tone.ToLowerInvariant() switch
        {
            "subtle" => GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(11, 255, 255, 255) : Color.FromArgb(88, 255, 255, 255)),
            "accent" => GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(22, 96, 165, 250) : Color.FromArgb(22, 37, 99, 235)),
            "success" => GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(24, 52, 211, 153) : Color.FromArgb(28, 4, 120, 87)),
            "warning" => GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(26, 251, 191, 36) : Color.FromArgb(32, 180, 83, 9)),
            "danger" => GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(26, 248, 113, 113) : Color.FromArgb(28, 220, 38, 38)),
            _ => GetBrush(
                "WidgetLayerFillSecondaryBrush",
                IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(112, 255, 255, 255))
        };
    }

    private (Brush Bg, Brush Fg) ResolveBadgeColors(string tone)
    {
        return tone.Trim().ToLowerInvariant() switch
        {
            "subtle" or "muted" or "secondary" or "default" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0)),
                ResolveForegroundBrush("secondary")),
            "success" or "emerald" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(36, 52, 211, 153) : Color.FromArgb(28, 4, 120, 87)),
                ResolveSemanticBrush("success")),
            "warning" or "coral" or "orange" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(38, 251, 191, 36) : Color.FromArgb(34, 180, 83, 9)),
                ResolveSemanticBrush("warning")),
            "danger" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(38, 248, 113, 113) : Color.FromArgb(30, 220, 38, 38)),
                ResolveSemanticBrush("danger")),
            "violet" or "purple" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(36, 167, 139, 250) : Color.FromArgb(26, 109, 40, 217)),
                ResolveSemanticBrush("violet")),
            "cyan" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(34, 56, 189, 248) : Color.FromArgb(26, 3, 105, 161)),
                ResolveSemanticBrush("cyan")),
            _ => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(28, 96, 165, 250) : Color.FromArgb(24, 37, 99, 235)),
                ResolveSemanticBrush("accent"))
        };
    }

    private Brush ResolveThresholdBrush(
        double value,
        double warning,
        double danger,
        string baseTone = "accent")
    {
        if (value >= danger)
        {
            return ResolveSemanticBrush("danger");
        }
        if (value >= warning)
        {
            return ResolveSemanticBrush("warning");
        }
        return ResolveSemanticBrush(
            string.IsNullOrWhiteSpace(baseTone) || string.Equals(baseTone, "default", StringComparison.OrdinalIgnoreCase)
                ? "accent"
                : baseTone);
    }

    private Brush CreateSparklineAreaBrush(
        double value,
        double warning,
        double danger,
        string baseTone = "accent")
    {
        Brush strokeBrush = ResolveThresholdBrush(value, warning, danger, baseTone);
        Color strokeColor = strokeBrush is SolidColorBrush scb
            ? scb.Color
            : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Color topColor = Color.FromArgb((byte)(IsDark ? 38 : 24), strokeColor.R, strokeColor.G, strokeColor.B);

        if (!_sparklineBrushCache.TryGetValue(topColor, out LinearGradientBrush? gradient))
        {
            gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            gradient.GradientStops.Add(new GradientStop { Color = topColor, Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, topColor.R, topColor.G, topColor.B), Offset = 1 });
            _sparklineBrushCache[topColor] = gradient;
        }

        return gradient;
    }

    private Brush GetBrush(string key, Color fallback)
    {
        try
        {
            if (NeutralInteractionBrush.ResolveThemedResource(key, _themeScope) is { } brush)
            {
                return brush;
            }
        }
        catch
        {
            // Headless unit test hosts may not initialize Application.Current.Resources.
        }
        return GetOrCreateSolidBrush(fallback);
    }

    internal static Thickness ResolveRootSafePadding(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Thickness(12, 10, 12, 12);
        }

        Thickness parsed = ParseThickness(raw);
        if (parsed.Left <= 0 && parsed.Top <= 0 && parsed.Right <= 0 && parsed.Bottom <= 0)
        {
            return new Thickness(12, 10, 12, 12);
        }

        return new Thickness(
            Math.Max(10, parsed.Left),
            Math.Max(6, parsed.Top),
            Math.Max(10, parsed.Right),
            Math.Max(10, parsed.Bottom));
    }

    private static Thickness ParseThickness(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Thickness(0);
        }

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double u))
        {
            return new Thickness(u);
        }
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return new Thickness(h, v, h, v);
        }
        if (parts.Length == 4 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            return new Thickness(l, t, r, b);
        }
        return new Thickness(0);
    }

    private static GridLength ParseGridLength(string token)
    {
        if (string.Equals(token, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }
        if (token.EndsWith('*'))
        {
            string numPart = token[..^1];
            double weight = string.IsNullOrWhiteSpace(numPart)
                ? 1
                : double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                    ? w
                    : 1;
            return new GridLength(Math.Max(0.1, weight), GridUnitType.Star);
        }
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
        {
            return new GridLength(Math.Max(0, px), GridUnitType.Pixel);
        }
        return new GridLength(1, GridUnitType.Star);
    }

    private static bool ContainsSystemMetricsToken(string? template) =>
        !string.IsNullOrEmpty(template) &&
        (template.Contains("$sys.", StringComparison.Ordinal) ||
         template.Contains("$device.", StringComparison.Ordinal) ||
         template.Contains("$pomodoro.", StringComparison.Ordinal) ||
         template.Contains("$pomo.", StringComparison.Ordinal) ||
         template.Contains("$countdown.", StringComparison.Ordinal) ||
         template.Contains("$time.", StringComparison.Ordinal) ||
         template.Contains("$clock.", StringComparison.Ordinal) ||
         template.Contains("$clipboard.", StringComparison.Ordinal) ||
         template.Contains("$drives.", StringComparison.Ordinal) ||
         template.Contains("tz(", StringComparison.OrdinalIgnoreCase) ||
         template.Contains("$ai.", StringComparison.Ordinal));

    private static void DisableTextShadowsInSubtree(UIElement element)
    {
        switch (element)
        {
            case TextBlock tb:
                WidgetTextShadow.SetCast(tb, false);
                break;
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    DisableTextShadowsInSubtree(child);
                }
                break;
            case Border { Child: { } borderChild }:
                DisableTextShadowsInSubtree(borderChild);
                break;
            case ContentControl { Content: UIElement contentEl }:
                DisableTextShadowsInSubtree(contentEl);
                break;
        }
    }
}

public sealed partial class CustomWidgetWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        bool firstInLine = true;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            if (desired.Width <= 0 && desired.Height <= 0)
            {
                continue;
            }

            double neededWidth = firstInLine ? desired.Width : lineWidth + HorizontalSpacing + desired.Width;
            if (!firstInLine && neededWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = neededWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
                firstInLine = false;
            }
        }

        if (!firstInLine)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = Math.Max(0, finalSize.Width);
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        bool firstInLine = true;

        foreach (UIElement child in Children)
        {
            Size desired = child.DesiredSize;
            if (desired.Width <= 0 && desired.Height <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (!firstInLine && x + HorizontalSpacing + desired.Width > maxWidth)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                firstInLine = true;
            }

            if (!firstInLine)
            {
                x += HorizontalSpacing;
            }

            double childWidth = Math.Min(desired.Width, Math.Max(0, maxWidth - x));
            child.Arrange(new Rect(x, y, childWidth, desired.Height));
            x += childWidth;
            lineHeight = Math.Max(lineHeight, desired.Height);
            firstInLine = false;
        }

        return finalSize;
    }
}
