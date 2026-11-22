using System.Collections.Frozen;
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
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Builds a 100% native WinUI 3 visual tree from a <see cref="CustomWidgetManifest"/>
/// using window-scoped Fluent theme resources, and registers lightweight in-place
/// updaters for live <c>$sys</c> metrics so periodic refreshes never disrupt input
/// focus or scroll position.
/// </summary>
public sealed partial class CustomWidgetNativeRenderer
{
    private enum RenderNodeKind : byte
    {
        Stack, ZStack, Wrap, ItemsGrid, Spacer, ModuleStack, Grid, Scroll, SplitView, Card,
        Popover, TeachingTip, Expander, Tabs, SelectorBar, FlipView, List, Table, BarList,
        Text, RichText, CodeBlock, Markdown, Image, MediaPlayer, Input, TextArea, AutoSuggestInput,
        Button, SplitButton, DropDownButton, IconButton, Toggle, CheckBox, Select, Segmented,
        RadioGroup, Rating, ColorPicker, DatePicker, TimePicker, Slider, NumberBox,
        Badge, InfoBadge, InfoBar, ProgressBar, ProgressRing, RingGauge, RadialGauge,
        DonutChart, Chart, Sparkline, HeatmapGrid, Canvas, CalendarGrid, DropZone, RecycleBin, ControlTile,
        StatCard, ProgressRow, KeyValueRow, FileChip, EmptyState, Divider
    }

    private static readonly FrozenDictionary<string, RenderNodeKind> NodeKindsByType =
        new Dictionary<string, RenderNodeKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["stack"] = RenderNodeKind.Stack,
            ["zstack"] = RenderNodeKind.ZStack,
            ["wrap"] = RenderNodeKind.Wrap,
            ["itemsgrid"] = RenderNodeKind.ItemsGrid,
            ["spacer"] = RenderNodeKind.Spacer,
            ["modulestack"] = RenderNodeKind.ModuleStack,
            ["grid"] = RenderNodeKind.Grid,
            ["scroll"] = RenderNodeKind.Scroll,
            ["splitview"] = RenderNodeKind.SplitView,
            ["card"] = RenderNodeKind.Card,
            ["popover"] = RenderNodeKind.Popover,
            ["teachingtip"] = RenderNodeKind.TeachingTip,
            ["expander"] = RenderNodeKind.Expander,
            ["tabs"] = RenderNodeKind.Tabs,
            ["selectorbar"] = RenderNodeKind.SelectorBar,
            ["flipview"] = RenderNodeKind.FlipView,
            ["list"] = RenderNodeKind.List,
            ["table"] = RenderNodeKind.Table,
            ["barlist"] = RenderNodeKind.BarList,
            ["text"] = RenderNodeKind.Text,
            ["richtext"] = RenderNodeKind.RichText,
            ["codeblock"] = RenderNodeKind.CodeBlock,
            ["markdown"] = RenderNodeKind.Markdown,
            ["image"] = RenderNodeKind.Image,
            ["mediaplayer"] = RenderNodeKind.MediaPlayer,
            ["input"] = RenderNodeKind.Input,
            ["textarea"] = RenderNodeKind.TextArea,
            ["autosuggestinput"] = RenderNodeKind.AutoSuggestInput,
            ["button"] = RenderNodeKind.Button,
            ["splitbutton"] = RenderNodeKind.SplitButton,
            ["dropdownbutton"] = RenderNodeKind.DropDownButton,
            ["iconbutton"] = RenderNodeKind.IconButton,
            ["toggle"] = RenderNodeKind.Toggle,
            ["checkbox"] = RenderNodeKind.CheckBox,
            ["select"] = RenderNodeKind.Select,
            ["segmented"] = RenderNodeKind.Segmented,
            ["radiogroup"] = RenderNodeKind.RadioGroup,
            ["rating"] = RenderNodeKind.Rating,
            ["colorpicker"] = RenderNodeKind.ColorPicker,
            ["datepicker"] = RenderNodeKind.DatePicker,
            ["timepicker"] = RenderNodeKind.TimePicker,
            ["slider"] = RenderNodeKind.Slider,
            ["numberbox"] = RenderNodeKind.NumberBox,
            ["badge"] = RenderNodeKind.Badge,
            ["infobadge"] = RenderNodeKind.InfoBadge,
            ["infobar"] = RenderNodeKind.InfoBar,
            ["progressbar"] = RenderNodeKind.ProgressBar,
            ["progressring"] = RenderNodeKind.ProgressRing,
            ["ringgauge"] = RenderNodeKind.RingGauge,
            ["radialgauge"] = RenderNodeKind.RadialGauge,
            ["donutchart"] = RenderNodeKind.DonutChart,
            ["chart"] = RenderNodeKind.Chart,
            ["sparkline"] = RenderNodeKind.Sparkline,
            ["heatmapgrid"] = RenderNodeKind.HeatmapGrid,
            ["canvas"] = RenderNodeKind.Canvas,
            ["calendargrid"] = RenderNodeKind.CalendarGrid,
            ["dropzone"] = RenderNodeKind.DropZone,
            ["recyclebin"] = RenderNodeKind.RecycleBin,
            ["controltile"] = RenderNodeKind.ControlTile,
            ["statcard"] = RenderNodeKind.StatCard,
            ["progressrow"] = RenderNodeKind.ProgressRow,
            ["keyvaluerow"] = RenderNodeKind.KeyValueRow,
            ["filechip"] = RenderNodeKind.FileChip,
            ["emptystate"] = RenderNodeKind.EmptyState,
            ["divider"] = RenderNodeKind.Divider
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly CustomWidgetViewModel _viewModel;
    private readonly DependencyObject? _themeScope;
    private readonly Func<double>? _textSizeProvider;
    private readonly Func<CustomWidgetHostAppearanceProfile>? _appearanceProvider;
    private readonly List<Action<CustomWidgetEvaluationContext>> _liveMetricUpdaters = [];
    private readonly Dictionary<Color, SolidColorBrush> _solidBrushCache = [];
    private readonly Dictionary<Color, LinearGradientBrush> _sparklineBrushCache = [];
    private readonly HashSet<FlyoutBase> _activeOpenFlyouts = [];
    private readonly HashSet<DeskBox.Views.WidgetEditorPopoverWindow> _activeOpenPopovers = [];
    private readonly HashSet<object> _activeInteractions = [];
    private int _parentGridColCount;
    private int _popoverCommandSequence;
    private string? _lastCommandedPopoverId;

    private async Task ExecuteInteractiveClickWithPopoverAsync(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext capturedCtx,
        FrameworkElement anchor)
    {
        int seqBefore = _popoverCommandSequence;
        if (spec.OnClick.Count > 0)
        {
            await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
        }

        if (!string.IsNullOrWhiteSpace(spec.Popover))
        {
            CustomWidgetEvaluationContext freshCtx = _viewModel.CreateEvaluationContext();
            if (capturedCtx.CurrentItem is not null)
            {
                freshCtx = freshCtx with
                {
                    CurrentItem = capturedCtx.CurrentItem,
                    CurrentIndex = capturedCtx.CurrentIndex
                };
            }

            string popId = CustomWidgetExpressionEvaluator.Interpolate(spec.Popover, freshCtx).Trim();
            if (!string.IsNullOrWhiteSpace(popId))
            {
                bool alreadyHandledSamePopover = _popoverCommandSequence != seqBefore &&
                    string.Equals(_lastCommandedPopoverId, popId, StringComparison.OrdinalIgnoreCase);
                if (!alreadyHandledSamePopover)
                {
                    HandlePopoverCommand("toggle", popId, anchor);
                }
            }
        }
    }

    internal bool HasOpenFlyout =>
        _activeOpenFlyouts.Count > 0 ||
        _activeOpenPopovers.Count > 0 ||
        _activeInteractions.Count > 0;

    internal bool InterceptPopoversForStudioPreview { get; set; }

    internal string? ActivePopoverPreviewId { get; private set; }

    internal event Action<string, string>? StudioPopoverPreviewRequested;

    internal bool UpdateActivePopoverPreviewId(string? popoverId)
    {
        string? normalized = string.IsNullOrWhiteSpace(popoverId) ? null : popoverId.Trim();
        if (string.Equals(ActivePopoverPreviewId, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ActivePopoverPreviewId = normalized;
        return true;
    }

    internal event Action? AllFlyoutsClosed;

    private void NotifyIfAllInteractionsClosed()
    {
        if (_activeOpenFlyouts.Count == 0 &&
            _activeOpenPopovers.Count == 0 &&
            _activeInteractions.Count == 0)
        {
            AllFlyoutsClosed?.Invoke();
        }
    }

    private void BeginInteractiveSession(object token)
    {
        _activeInteractions.Add(token);
    }

    private void EndInteractiveSession(object token)
    {
        if (_activeInteractions.Remove(token))
        {
            NotifyIfAllInteractionsClosed();
        }
    }

    public CustomWidgetNativeRenderer(
        CustomWidgetViewModel viewModel,
        DependencyObject? themeScope = null,
        Func<double>? textSizeProvider = null,
        Func<CustomWidgetHostAppearanceProfile>? appearanceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _themeScope = themeScope;
        _textSizeProvider = textSizeProvider;
        _appearanceProvider = appearanceProvider;
    }

    internal void CloseOpenPopovers()
    {
        if (_activeOpenPopovers.Count == 0)
        {
            return;
        }

        DeskBox.Views.WidgetEditorPopoverWindow[] popovers = [.. _activeOpenPopovers];
        _activeOpenPopovers.Clear();
        foreach (DeskBox.Views.WidgetEditorPopoverWindow popover in popovers)
        {
            popover.ClosePopover(restoreOwnerForeground: false);
        }
    }

    internal void ReleaseResources()
    {
        CloseOpenPopovers();
        ResetExtendedVisualState(releasePopovers: true);
        ResetReconcilerState();
        ResetRichControlsState();
        ResetRecycleBinState();
        ReleaseMediaPlayers();

        _activeOpenFlyouts.Clear();
        _activeInteractions.Clear();
        _liveMetricUpdaters.Clear();
        _solidBrushCache.Clear();
        _sparklineBrushCache.Clear();
        ResetInspectionState();
    }

    private void ApplyActiveFlowDirectionToFlyout(FlyoutBase flyout)
    {
        FlowDirection expectedFlow = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_viewModel.ActiveCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        if (flyout is Flyout regularFlyout)
        {
            if (regularFlyout.Content is FrameworkElement contentFe)
            {
                contentFe.FlowDirection = expectedFlow;
            }
            var presenterStyle = new Style(typeof(FlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, expectedFlow));
            regularFlyout.FlyoutPresenterStyle = presenterStyle;
        }
        else if (flyout is MenuFlyout menuFlyout)
        {
            var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, expectedFlow));
            menuFlyout.MenuFlyoutPresenterStyle = presenterStyle;
        }
    }

    private void TrackInteractiveFlyout(FlyoutBase flyout, bool markOpeningImmediately = false)
    {
        ApplyActiveFlowDirectionToFlyout(flyout);
        if (markOpeningImmediately)
        {
            _activeOpenFlyouts.Add(flyout);
        }

        flyout.Opening += (_, _) =>
        {
            ApplyActiveFlowDirectionToFlyout(flyout);
            _activeOpenFlyouts.Add(flyout);
        };
        flyout.Opened += (_, _) => _activeOpenFlyouts.Add(flyout);
        flyout.Closed += (_, _) =>
        {
            if (_activeOpenFlyouts.Remove(flyout))
            {
                NotifyIfAllInteractionsClosed();
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
            if (_activeOpenFlyouts.Remove(flyout))
            {
                NotifyIfAllInteractionsClosed();
            }
            throw;
        }
    }

    private void ShowTrackedPopoverAt(
        DeskBox.Views.WidgetEditorPopoverWindow popover,
        FrameworkElement anchor,
        FrameworkElement content,
        DeskBox.Views.WidgetPopoverPlacement placement,
        double minWidthDip,
        double maxWidthDip,
        Control? initialFocusTarget = null,
        string logScope = "CustomWidgetPopover",
        string? logicalKey = null)
    {
        _activeOpenPopovers.Add(popover);
        popover.PopoverClosed += (_, _) =>
        {
            if (_activeOpenPopovers.Remove(popover))
            {
                NotifyIfAllInteractionsClosed();
            }
        };

        try
        {
            popover.ShowAnchored(
                anchor,
                content,
                placement,
                minWidthDip: minWidthDip,
                maxWidthDip: maxWidthDip,
                initialFocusTarget: initialFocusTarget,
                triggerElement: anchor,
                logScope: logScope,
                logicalKey: logicalKey);
        }
        catch
        {
            if (_activeOpenPopovers.Remove(popover))
            {
                NotifyIfAllInteractionsClosed();
            }
            try { popover.ClosePopover(restoreOwnerForeground: false); } catch { }
            throw;
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

    internal event Action<string>? SettingsNavigationRequested;

    public UIElement BuildRootElement()
    {
        _activeOpenFlyouts.Clear();
        _activeInteractions.Clear();
        _liveMetricUpdaters.Clear();
        ResetInspectionState();
        ResetExtendedVisualState(releasePopovers: false);
        ResetReconcilerState();
        ResetRichControlsState();
        ResetRecycleBinState();
        ReleaseMediaPlayers();
        IndexManifestPopovers();
        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();

        var rootStack = new Grid
        {
            FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(ctx.CultureName)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight
        };
        _lastRootElementRef = new WeakReference<FrameworkElement>(rootStack);
        UIElement content = TryResolveActivePopoverSpec(out CustomWidgetNodeSpec? activePopoverSpec, out string? activePopoverId)
            ? BuildInStagePopoverCanvas(activePopoverSpec!, activePopoverId!, ctx)
            : BuildNode(_viewModel.Manifest.View, ctx, isRoot: true);

        IReadOnlyList<CustomWidgetSettingDefinition> missingSecrets = _viewModel.GetUnconfiguredSecretSettings();
        bool missingGlobalAi = NeedsGlobalAiSetupBanner(_viewModel);
        if (missingSecrets.Count > 0 || missingGlobalAi)
        {
            var bodyGrid = new Grid { RowSpacing = 2 };
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var bannersStack = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(12, 10, 12, 0)
            };
            if (missingSecrets.Count > 0)
            {
                FrameworkElement secretBanner = BuildSecretSetupBanner(missingSecrets, ctx.CultureName);
                secretBanner.Margin = new Thickness(0);
                bannersStack.Children.Add(secretBanner);
            }
            if (missingGlobalAi)
            {
                FrameworkElement aiBanner = BuildGlobalAiSetupBanner(ctx.CultureName);
                aiBanner.Margin = new Thickness(0);
                bannersStack.Children.Add(aiBanner);
            }

            Grid.SetRow(bannersStack, 0);
            bodyGrid.Children.Add(bannersStack);

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

        AttachRootInspectionHandlers(rootStack);
        _hasRetainedRoot = true;
        return rootStack;
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
        if (_hasRetainedRoot && TryPatchRetainedTree())
        {
            return;
        }

        bool hasPopoverUpdaters = false;
        foreach (OpenPopoverEntry entry in _openPopoversById.Values)
        {
            if (entry.MetricUpdaters.Count > 0)
            {
                hasPopoverUpdaters = true;
                break;
            }
        }

        if (_liveMetricUpdaters.Count == 0 && !hasPopoverUpdaters)
        {
            return;
        }

        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();
        foreach (Action<CustomWidgetEvaluationContext> updater in _liveMetricUpdaters)
        {
            updater(ctx);
        }

        if (hasPopoverUpdaters)
        {
            foreach (OpenPopoverEntry entry in _openPopoversById.Values)
            {
                foreach (Action<CustomWidgetEvaluationContext> updater in entry.MetricUpdaters)
                {
                    updater(ctx);
                }
            }
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

        if (!string.IsNullOrWhiteSpace(spec.When))
        {
            _buildNodeDepth++;
            try
            {
                FrameworkElement slot = (FrameworkElement)BuildConditionalWhenSlot(spec, ctx, isRoot);
                return WrapForNodeInspection(slot, spec, ctx, isRoot);
            }
            finally
            {
                _buildNodeDepth--;
            }
        }

        return BuildNodeCore(spec, ctx, isRoot, ignoreWhen: false);
    }

    private UIElement BuildNodeCore(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot,
        bool ignoreWhen)
    {
        if (_buildNodeDepth > MaxRenderNodeDepth)
        {
            return new Grid { Visibility = Visibility.Collapsed };
        }

        _buildNodeDepth++;
        try
        {
            if (!ignoreWhen &&
                !string.IsNullOrWhiteSpace(spec.When) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.When, ctx))
            {
                return new Grid { Visibility = Visibility.Collapsed };
            }

            RenderNodeKind nodeKind = NodeKindsByType.GetValueOrDefault(spec.Type, RenderNodeKind.Stack);
            FrameworkElement element = nodeKind switch
            {
                RenderNodeKind.Stack => BuildStack(spec, ctx, isRoot),
                RenderNodeKind.ZStack => BuildZStack(spec, ctx, isRoot),
                RenderNodeKind.Wrap => BuildWrap(spec, ctx, isRoot),
                RenderNodeKind.ItemsGrid => BuildItemsGrid(spec, ctx),
                RenderNodeKind.Spacer => BuildSpacer(spec),
                RenderNodeKind.ModuleStack => BuildModuleStack(spec, ctx, isRoot),
                RenderNodeKind.Grid => BuildGrid(spec, ctx, isRoot),
                RenderNodeKind.Scroll => BuildScroll(spec, ctx, isRoot),
                RenderNodeKind.SplitView => BuildSplitView(spec, ctx, isRoot),
                RenderNodeKind.Card => BuildCard(spec, ctx),
                RenderNodeKind.Popover => BuildPopoverDeclarationNode(spec, ctx),
                RenderNodeKind.TeachingTip => BuildTeachingTip(spec, ctx),
                RenderNodeKind.Expander => BuildExpander(spec, ctx),
                RenderNodeKind.Tabs => BuildTabs(spec, ctx, isRoot),
                RenderNodeKind.SelectorBar => BuildSelectorBar(spec, ctx),
                RenderNodeKind.FlipView => BuildFlipView(spec, ctx),
                RenderNodeKind.List => BuildList(spec, ctx),
                RenderNodeKind.Table => BuildTable(spec, ctx),
                RenderNodeKind.BarList => BuildBarList(spec, ctx),
                RenderNodeKind.Text => BuildText(spec, ctx),
                RenderNodeKind.RichText => BuildRichText(spec, ctx),
                RenderNodeKind.CodeBlock => BuildCodeBlock(spec, ctx),
                RenderNodeKind.Markdown => BuildMarkdown(spec, ctx),
                RenderNodeKind.Image => BuildImage(spec, ctx),
                RenderNodeKind.MediaPlayer => BuildMediaPlayer(spec, ctx),
                RenderNodeKind.Input => BuildInput(spec, ctx),
                RenderNodeKind.TextArea => BuildTextArea(spec, ctx),
                RenderNodeKind.AutoSuggestInput => BuildAutoSuggestInput(spec, ctx),
                RenderNodeKind.Button => BuildButton(spec, ctx),
                RenderNodeKind.SplitButton => BuildSplitButton(spec, ctx),
                RenderNodeKind.DropDownButton => BuildDropDownButton(spec, ctx),
                RenderNodeKind.IconButton => BuildIconButton(spec, ctx),
                RenderNodeKind.Toggle => BuildToggle(spec, ctx),
                RenderNodeKind.CheckBox => BuildCheckBox(spec, ctx),
                RenderNodeKind.Select => BuildSelect(spec, ctx),
                RenderNodeKind.Segmented => BuildSegmented(spec, ctx),
                RenderNodeKind.RadioGroup => BuildRadioGroup(spec, ctx),
                RenderNodeKind.Rating => BuildRating(spec, ctx),
                RenderNodeKind.ColorPicker => BuildColorPicker(spec, ctx),
                RenderNodeKind.DatePicker => BuildDatePicker(spec, ctx),
                RenderNodeKind.TimePicker => BuildTimePicker(spec, ctx),
                RenderNodeKind.Slider => BuildSlider(spec, ctx),
                RenderNodeKind.NumberBox => BuildNumberBox(spec, ctx),
                RenderNodeKind.Badge => BuildBadge(spec, ctx),
                RenderNodeKind.InfoBadge => BuildInfoBadge(spec, ctx),
                RenderNodeKind.InfoBar => BuildInfoBar(spec, ctx),
                RenderNodeKind.ProgressBar => BuildProgressBar(spec, ctx),
                RenderNodeKind.ProgressRing => BuildProgressRing(spec, ctx),
                RenderNodeKind.RingGauge => BuildRingGauge(spec, ctx),
                RenderNodeKind.RadialGauge => BuildRadialGauge(spec, ctx),
                RenderNodeKind.DonutChart => BuildDonutChart(spec, ctx),
                RenderNodeKind.Chart => BuildChart(spec, ctx),
                RenderNodeKind.Sparkline => BuildSparkline(spec, ctx),
                RenderNodeKind.HeatmapGrid => BuildHeatmapGrid(spec, ctx),
                RenderNodeKind.Canvas => BuildCanvas(spec, ctx),
                RenderNodeKind.CalendarGrid => BuildCalendarGrid(spec, ctx),
                RenderNodeKind.DropZone => BuildDropZone(spec, ctx),
                RenderNodeKind.RecycleBin => BuildRecycleBin(spec, ctx),
                RenderNodeKind.ControlTile => BuildControlTile(spec, ctx),
                RenderNodeKind.StatCard => BuildStatCard(spec, ctx),
                RenderNodeKind.ProgressRow => BuildProgressRow(spec, ctx),
                RenderNodeKind.KeyValueRow => BuildKeyValueRow(spec, ctx),
                RenderNodeKind.FileChip => BuildFileChip(spec, ctx),
                RenderNodeKind.EmptyState => BuildEmptyState(
                    new CustomWidgetEmptyStateSpec(
                        spec.Icon ?? "\uE70F",
                        spec.Title ?? spec.Text ?? string.Empty,
                        spec.Subtitle ?? string.Empty),
                    ctx),
                RenderNodeKind.Divider => new Border
                {
                    Height = 1,
                    Background = !string.IsNullOrWhiteSpace(spec.Color) && !string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                        ? ResolveAnyColorBrush(spec.Color, ctx, "default")
                        : DividerBrush,
                    Margin = new Thickness(0, 2, 0, 2)
                },
                _ => BuildStack(spec, ctx, isRoot)
            };

            if (nodeKind is not (RenderNodeKind.Popover or RenderNodeKind.TeachingTip))
            {
                ApplyCommonLayoutProperties(element, spec, ctx);
                element = ApplyMaterialAndAspectRatio(element, spec, ctx);
                AttachNodeContextMenu(element, spec, ctx);
            }
            RegisterElementById(spec.Id, element);
            RegisterPopoverTriggerAnchor(spec, element, ctx);

            if (isRoot && nodeKind is not (RenderNodeKind.Stack or RenderNodeKind.ZStack or RenderNodeKind.SplitView or RenderNodeKind.ModuleStack or RenderNodeKind.Grid or RenderNodeKind.Scroll or RenderNodeKind.Tabs or RenderNodeKind.Wrap))
            {
                var safeFrame = new Border
                {
                    Padding = ResolveEffectiveRootSafePadding(spec.Padding),
                    Child = element
                };
                element = nodeKind is RenderNodeKind.List or RenderNodeKind.ItemsGrid
                    ? safeFrame
                    : new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = WrapInShadowHostGrid(safeFrame)
                    };
            }

            return ignoreWhen ? element : WrapForNodeInspection(element, spec, ctx, isRoot);
        }
        finally
        {
            _buildNodeDepth--;
        }
    }

    internal FrameworkElement WrapRootContentInViewportScrollViewer(
        FrameworkElement rootElement,
        CustomWidgetNodeSpec? spec = null)
    {
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = WrapInShadowHostGrid(rootElement)
        };
        sv.SizeChanged += (_, e) =>
        {
            if (double.IsFinite(e.NewSize.Height) && e.NewSize.Height > 0)
            {
                double viewportHeight = sv.ViewportHeight > 0 && double.IsFinite(sv.ViewportHeight)
                    ? sv.ViewportHeight
                    : e.NewSize.Height;
                rootElement.MinHeight = viewportHeight;
            }
        };
        return sv;
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
            ? ResolveEffectiveRootSafePadding(spec.Padding)
            : ParseThickness(spec.Padding);
        double effectiveSpacing = ResolveEffectiveSpacing(spec, isHorizontal);

        static bool IsStretchCollectionSpec(CustomWidgetNodeSpec c) =>
            string.Equals(c.Type, "List", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(c.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) &&
             !c.Height.HasValue &&
             !c.MaxHeight.HasValue &&
             c.MaxLines <= 0 &&
             (!string.IsNullOrWhiteSpace(c.Items) || !string.IsNullOrWhiteSpace(c.Collection)));

        bool IsFlexibleSpacerNode(CustomWidgetNodeSpec c) =>
            string.Equals(c.Type, "Spacer", StringComparison.OrdinalIgnoreCase) &&
            (isHorizontal ? c.Width is null : c.Height is null);

        void AttachStackInteractiveTap(Panel targetPanel)
        {
            if ((spec.OnClick.Count == 0 && string.IsNullOrWhiteSpace(spec.Popover)) || _isNodeSelectionEnabled)
            {
                return;
            }

            targetPanel.Background ??= GetOrCreateSolidBrush(Colors.Transparent);
            MarkInteractiveElement(targetPanel);
            CustomWidgetEvaluationContext capturedCtx = ctx;
            RegisterRetainedPatcher(liveCtx => capturedCtx = liveCtx);
            targetPanel.Tapped += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (_isNodeSelectionEnabled ||
                    e.Handled ||
                    IsInteractiveDescendant(e.OriginalSource as DependencyObject, targetPanel))
                {
                    return;
                }

                e.Handled = true;
                await ExecuteInteractiveClickWithPopoverAsync(spec, capturedCtx, targetPanel);
            });
        }

        bool hasFlexSpacer = spec.Children.Any(IsFlexibleSpacerNode);
        bool hasExplicitFlexChild = spec.Children.Any(static c => c.Flex is > 0);

        if ((isRoot && !isHorizontal && spec.Children.Any(IsStretchCollectionSpec)) ||
            hasFlexSpacer ||
            hasExplicitFlexChild)
        {
            var flexGrid = new Grid
            {
                Padding = containerPadding
            };
            if (isHorizontal)
            {
                flexGrid.ColumnSpacing = effectiveSpacing;
            }
            else
            {
                flexGrid.RowSpacing = 0;
            }

            List<(RowDefinition RowDef, FrameworkElement Element, Thickness BaseMargin, bool EnforceMinHeight)>? verticalRows =
                isHorizontal ? null : [];
            bool hasConditionalVerticalChildren = false;

            int slotIndex = 0;
            for (int i = 0; i < spec.Children.Count; i++)
            {
                CustomWidgetNodeSpec childSpec = spec.Children[i];
                bool isFlexibleSpacer = IsFlexibleSpacerNode(childSpec);
                double explicitFlexWeight = childSpec.Flex is double fw && fw > 0 ? fw : 0;
                bool absorbsFollowingSpacer = isHorizontal &&
                                              !isFlexibleSpacer &&
                                              explicitFlexWeight <= 0 &&
                                              (string.Equals(childSpec.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(childSpec.Type, "FileChip", StringComparison.OrdinalIgnoreCase)) &&
                                              i + 1 < spec.Children.Count &&
                                              IsFlexibleSpacerNode(spec.Children[i + 1]);

                bool isStretchCollectionRow = !isHorizontal && (isRoot || explicitFlexWeight > 0) && IsStretchCollectionSpec(childSpec);
                bool isFlexible = explicitFlexWeight > 0 ||
                                  isFlexibleSpacer ||
                                  absorbsFollowingSpacer ||
                                  isStretchCollectionRow ||
                                  (!isHorizontal && isRoot &&
                                   string.Equals(childSpec.Type, "Scroll", StringComparison.OrdinalIgnoreCase));
                double starWeight = explicitFlexWeight > 0 ? explicitFlexWeight : 1.0;

                RowDefinition? verticalRowDef = null;
                if (isHorizontal)
                {
                    flexGrid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = isFlexible ? new GridLength(starWeight, GridUnitType.Star) : GridLength.Auto
                    });
                }
                else
                {
                    verticalRowDef = new RowDefinition
                    {
                        Height = isFlexible ? new GridLength(starWeight, GridUnitType.Star) : GridLength.Auto,
                        MinHeight = isStretchCollectionRow ? 120 : 0
                    };
                    flexGrid.RowDefinitions.Add(verticalRowDef);
                    if (!string.IsNullOrWhiteSpace(childSpec.When))
                    {
                        hasConditionalVerticalChildren = true;
                    }
                }

                UIElement childEl = BuildNode(childSpec, ctx);
                if (childEl is FrameworkElement fe)
                {
                    if (isHorizontal)
                    {
                        Grid.SetColumn(fe, slotIndex);
                        if (isFlexible && !isFlexibleSpacer && string.IsNullOrWhiteSpace(childSpec.HorizontalAlignment))
                        {
                            fe.HorizontalAlignment = HorizontalAlignment.Stretch;
                        }
                        if (fe.VerticalAlignment == VerticalAlignment.Stretch && !isFlexibleSpacer)
                        {
                            fe.VerticalAlignment = VerticalAlignment.Center;
                        }
                    }
                    else
                    {
                        Grid.SetRow(fe, slotIndex);
                        if (verticalRowDef is not null && verticalRows is not null)
                        {
                            verticalRows.Add((verticalRowDef, fe, fe.Margin, isStretchCollectionRow));
                        }
                    }
                }
                flexGrid.Children.Add(childEl);
                slotIndex++;

                if (absorbsFollowingSpacer)
                {
                    i++;
                }
            }

            if (verticalRows is { Count: > 0 })
            {
                void UpdateFlexVerticalRows()
                {
                    bool hasVisibleBefore = false;
                    foreach ((RowDefinition rowDef, FrameworkElement feChild, Thickness baseMargin, bool enforceMinHeight) in verticalRows)
                    {
                        bool isVisible = feChild.Visibility == Visibility.Visible;
                        double topGap = (isVisible && hasVisibleBefore) ? effectiveSpacing : 0;
                        Thickness nextMargin = new(
                            baseMargin.Left,
                            baseMargin.Top + topGap,
                            baseMargin.Right,
                            baseMargin.Bottom);
                        if (feChild.Margin != nextMargin)
                        {
                            feChild.Margin = nextMargin;
                        }

                        if (enforceMinHeight)
                        {
                            double targetMin = isVisible ? 120 : 0;
                            if (Math.Abs(rowDef.MinHeight - targetMin) > 0.1)
                            {
                                rowDef.MinHeight = targetMin;
                            }
                            rowDef.Height = isVisible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                        }

                        if (isVisible)
                        {
                            hasVisibleBefore = true;
                        }
                    }
                }

                UpdateFlexVerticalRows();
                if (hasConditionalVerticalChildren)
                {
                    RegisterRetainedPatcher(_ => UpdateFlexVerticalRows());
                }
            }

            AttachStackInteractiveTap(flexGrid);
            FrameworkElement styledFlex = ApplyContainerVisualOverrides(flexGrid, spec, ctx);
            return isRoot ? WrapRootContentInViewportScrollViewer(styledFlex, spec) : styledFlex;
        }

        var panel = new StackPanel
        {
            Orientation = isHorizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = effectiveSpacing,
            Padding = containerPadding
        };

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            panel.Children.Add(BuildNode(child, ctx));
        }

        AttachStackInteractiveTap(panel);

        FrameworkElement styledPanel = ApplyContainerVisualOverrides(panel, spec, ctx);
        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(styledPanel)
            };
        }

        return styledPanel;
    }



    private FrameworkElement BuildCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        Thickness defaultCardPadding = _parentGridColCount >= 4
            ? ScaleDefaultThickness(6, 4, 6, 4, EffectiveAppearance.DensityMultiplier)
            : ScaleDefaultThickness(12, 10, 12, 10, EffectiveAppearance.DensityMultiplier);
        var inner = new StackPanel
        {
            Spacing = ResolveEffectiveSpacing(spec, isHorizontal: false),
            VerticalAlignment = (spec.MinHeight.HasValue || spec.Height.HasValue) && string.IsNullOrWhiteSpace(spec.VerticalAlignment)
                ? VerticalAlignment.Center
                : VerticalAlignment.Stretch
        };
        int savedParentGridColCount = _parentGridColCount;
        _parentGridColCount = 0;
        try
        {
            foreach (CustomWidgetNodeSpec child in spec.Children)
            {
                inner.Children.Add(BuildNode(child, ctx));
            }
        }
        finally
        {
            _parentGridColCount = savedParentGridColCount;
        }

        string resolvedTone = CustomWidgetExpressionEvaluator.Interpolate(spec.Tone, ctx);
        Brush defaultBackground = ResolveCardBackground(resolvedTone);
        var cardBorder = new Border
        {
            CornerRadius = CornerMedium,
            Padding = !string.IsNullOrWhiteSpace(spec.Padding) ? ParseThickness(spec.Padding) : defaultCardPadding,
            Background = defaultBackground,
            BorderThickness = CardBorderThickness,
            BorderBrush = CardStrokeBrush
        };
        ApplyContainerVisualOverrides(cardBorder, spec, ctx);
        Brush restBackground = cardBorder.Background ?? defaultBackground;
        bool useShadowHostGrid = IsSeeThroughShadowBrush(restBackground) &&
            !ContainsDynamicToken(spec.Tone);
        cardBorder.Child = useShadowHostGrid
            ? WrapInShadowHostGrid(inner)
            : inner;

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(cardBorder, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        bool isHovered = false;
        if ((spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover)) && !_isNodeSelectionEnabled)
        {
            MarkInteractiveElement(cardBorder);
            AttachInteractiveSurfaceFeedback(cardBorder);
            CustomWidgetEvaluationContext capturedCtx = ctx;
            RegisterRetainedPatcher(liveCtx => capturedCtx = liveCtx);
            Brush hoverBackground = SubtleHoverBrush;
            cardBorder.PointerEntered += (_, _) =>
            {
                isHovered = true;
                cardBorder.Background = hoverBackground;
            };
            cardBorder.PointerExited += (_, _) =>
            {
                isHovered = false;
                cardBorder.Background = restBackground;
            };
            cardBorder.Tapped += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (_isNodeSelectionEnabled ||
                    e.Handled ||
                    IsInteractiveDescendant(e.OriginalSource as DependencyObject, cardBorder))
                {
                    return;
                }

                e.Handled = true;
                await ExecuteInteractiveClickWithPopoverAsync(spec, capturedCtx, cardBorder);
            });
        }

        if (ContainsDynamicToken(spec.Tone) || ContainsDynamicToken(spec.Background) || ContainsDynamicToken(spec.Tooltip))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (ContainsDynamicToken(spec.Tone) || ContainsDynamicToken(spec.Background))
                {
                    Brush nextBg = TryResolveExplicitBackgroundBrush(spec, liveCtx, out Brush? explicitBg) && explicitBg is not null
                        ? explicitBg
                        : ResolveCardBackground(CustomWidgetExpressionEvaluator.Interpolate(spec.Tone, liveCtx));
                    restBackground = nextBg;
                    if (!isHovered)
                    {
                        cardBorder.Background = nextBg;
                    }
                }
                if (!string.IsNullOrWhiteSpace(spec.Tooltip))
                {
                    ToolTipService.SetToolTip(cardBorder, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, liveCtx));
                }
            });
        }

        if (string.Equals(spec.Id, "workspace-card", StringComparison.OrdinalIgnoreCase))
        {
            string prevErrorSeq = ctx.State.GetValueOrDefault("errorSeq", string.Empty);
            RegisterRetainedPatcher(liveCtx =>
            {
                string curErrorSeq = liveCtx.State.GetValueOrDefault("errorSeq", string.Empty);
                if (!string.Equals(prevErrorSeq, curErrorSeq, StringComparison.Ordinal))
                {
                    prevErrorSeq = curErrorSeq;
                    if (!string.IsNullOrEmpty(curErrorSeq) && !string.Equals(curErrorSeq, "0", StringComparison.Ordinal))
                    {
                        PlayHorizontalErrorNudge(cardBorder);
                    }
                }
            });
        }

        return cardBorder;
    }

    private static bool IsInteractiveDescendant(DependencyObject? source, DependencyObject stopAt)
    {
        DependencyObject? cur = source;
        while (cur is not null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is ButtonBase or TextBox or ToggleSwitch or Selector
                    or CalendarDatePicker or TimePicker or AutoSuggestBox
                    or RadioButtons or RatingControl or NumberBox or Slider
                    or SplitButton or DropDownButton or SelectorBar or SelectorBarItem or MediaPlayerElement)
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



    private static FlowDirection? ResolveExplicitFlowDirection(
        CustomWidgetNodeSpec spec,
        bool treatMonoAsLtr = false)
    {
        if (string.Equals(spec.Dir, "ltr", StringComparison.OrdinalIgnoreCase))
        {
            return FlowDirection.LeftToRight;
        }
        if (string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase))
        {
            return FlowDirection.RightToLeft;
        }
        if (treatMonoAsLtr &&
            (string.Equals(spec.Style, "mono", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(spec.FontFamily, "mono", StringComparison.OrdinalIgnoreCase)))
        {
            return FlowDirection.LeftToRight;
        }
        return null;
    }

    private bool ClearTextInputFocus(Control inputControl)
    {
        EndInteractiveSession(inputControl);
        DependencyObject? current = VisualTreeHelper.GetParent(inputControl);
        while (current is not null)
        {
            if (current is Control focusable &&
                focusable.IsTabStop &&
                focusable.IsEnabled &&
                focusable.Visibility == Visibility.Visible &&
                focusable is not (TextBox or PasswordBox or RichEditBox or NumberBox or AutoSuggestBox))
            {
                if (focusable.Focus(FocusState.Programmatic))
                {
                    return true;
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private FrameworkElement BuildText(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTemplate = spec.Text ?? spec.Title ?? spec.Value ?? string.Empty;
        var tb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, ctx),
            TextWrapping = spec.WrapText ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (ResolveExplicitFlowDirection(spec) is { } explicitTextDir)
        {
            tb.FlowDirection = explicitTextDir;
        }

        if (spec.MaxLines > 0)
        {
            tb.MaxLines = spec.MaxLines;
        }

        ApplyTypography(tb, spec.Style);
        string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);
        tb.Foreground = ResolveForegroundBrush(resolvedColor);
        ApplyRawTextTypographyOverrides(tb, spec, ctx);

        bool animateDiscreteHero = (string.Equals(spec.Style, "hero", StringComparison.OrdinalIgnoreCase) ||
                                    spec.AnimateChange) &&
                                   !ContainsSystemMetricsToken(rawTemplate);
        if (ContainsDynamicToken(rawTemplate) ||
            ContainsDynamicToken(spec.Color) ||
            ContainsDynamicToken(spec.TextColor))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                string nextText = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, liveCtx);
                if (!string.Equals(tb.Text, nextText, StringComparison.Ordinal))
                {
                    tb.Text = nextText;
                    if (animateDiscreteHero)
                    {
                        TryAnimateTextChange(tb);
                    }
                }
                if (ContainsDynamicToken(spec.TextColor) || ContainsDynamicToken(spec.Color))
                {
                    if (TryResolveExplicitTextBrush(spec, liveCtx, out Brush? explicitLiveFg) && explicitLiveFg is not null)
                    {
                        tb.Foreground = explicitLiveFg;
                    }
                    else
                    {
                        tb.Foreground = ResolveForegroundBrush(CustomWidgetExpressionEvaluator.Interpolate(spec.Color, liveCtx));
                    }
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
        if (ResolveExplicitFlowDirection(spec, treatMonoAsLtr: true) is { } explicitInputDir)
        {
            textBox.FlowDirection = explicitInputDir;
        }
        ApplyCompactInputStyle(textBox);
        MarkInteractiveElement(textBox);

        textBox.GotFocus += (_, _) => BeginInteractiveSession(textBox);
        textBox.LostFocus += (_, _) => EndInteractiveSession(textBox);
        textBox.Unloaded += (_, _) => EndInteractiveSession(textBox);

        bool suppressTextSync = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        IReadOnlyList<CustomWidgetActionSpec> liveOnChangeActions = spec.OnChange.Count > 0
            ? FilterNonRedundantInputOnChangeActions(spec.OnChange, bindKey)
            : [];
        string lastCommittedText = textBox.Text ?? string.Empty;

        void CommitInputValue(string currentText)
        {
            if (string.Equals(lastCommittedText, currentText, StringComparison.Ordinal))
            {
                return;
            }

            lastCommittedText = currentText;
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, currentText, notifyLiveBindings: true);
            }
            if (liveOnChangeActions.Count > 0)
            {
                _ = _viewModel.ExecuteActionsAsync(liveOnChangeActions, capturedCtx.WithEventValue(currentText));
            }
        }

        textBox.TextChanged += (_, _) =>
        {
            if (suppressTextSync)
            {
                return;
            }
            if (textBox.FocusState != FocusState.Unfocused)
            {
                BeginInteractiveSession(textBox);
            }
            string currentText = textBox.Text ?? string.Empty;
            if (DeskBox.Helpers.ImeCompositionGuard.IsActivelyComposing(textBox))
            {
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    _viewModel.SetStateValueWithoutRebuild(bindKey, currentText, notifyLiveBindings: false);
                }
                return;
            }

            CommitInputValue(currentText);
        };
        textBox.TextCompositionEnded += (_, _) =>
        {
            if (suppressTextSync)
            {
                return;
            }
            CommitInputValue(textBox.Text ?? string.Empty);
        };

        textBox.KeyDown += (_, e) =>
        {
            if (!DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e) &&
                e.Key == VirtualKey.Escape &&
                ClearTextInputFocus(textBox))
            {
                e.Handled = true;
            }
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (ContainsDynamicToken(spec.Placeholder))
            {
                textBox.PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, liveCtx);
            }
            if (_activeInteractions.Contains(textBox) ||
                DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace())
            {
                return;
            }
            string nextVal = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            if (!string.Equals(textBox.Text, nextVal, StringComparison.Ordinal))
            {
                suppressTextSync = true;
                try
                {
                    textBox.Text = nextVal;
                    lastCommittedText = nextVal;
                }
                finally
                {
                    suppressTextSync = false;
                }
            }
        });

        if (spec.OnSubmit.Count > 0)
        {
            textBox.KeyDown += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
                {
                    return;
                }

                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    string submittedText = textBox.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(bindKey))
                    {
                        _viewModel.SetStateValueWithoutRebuild(bindKey, submittedText);
                    }
                    EndInteractiveSession(textBox);
                    await _viewModel.ExecuteActionsAsync(spec.OnSubmit, capturedCtx.WithEventValue(submittedText));
                }
            });
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
        string initialVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, ctx).Trim();
        bool isAccent = string.Equals(initialVariant, "accent", StringComparison.OrdinalIgnoreCase);
        bool isGhostOrSubtle = string.Equals(initialVariant, "ghost", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(initialVariant, "subtle", StringComparison.OrdinalIgnoreCase);

        Brush ResolveVariantForeground(string variant, CustomWidgetEvaluationContext evalCtx)
        {
            if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                return GetBrush("TextOnAccentFillColorPrimaryBrush", IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White);
            }
            if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveForegroundBrush("danger");
            }
            if (string.Equals(variant, "ghost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(variant, "subtle", StringComparison.OrdinalIgnoreCase))
            {
                string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx).Trim();
                return ResolveForegroundBrush(
                    string.IsNullOrWhiteSpace(resolvedColor) || string.Equals(resolvedColor, "default", StringComparison.OrdinalIgnoreCase)
                        ? "secondary"
                        : resolvedColor);
            }

            return ResolveForegroundBrush("default");
        }

        Brush? accentIconFg = (isAccent || isGhostOrSubtle || string.Equals(initialVariant, "danger", StringComparison.OrdinalIgnoreCase))
            ? ResolveVariantForeground(initialVariant, ctx)
            : null;

        bool hasDynamicIcon = ContainsDynamicToken(spec.Icon);
        bool hasDynamicLabel = ContainsDynamicToken(rawLabel);
        bool mayHaveIcon = !string.IsNullOrWhiteSpace(icon) || hasDynamicIcon;
        bool mayHaveLabel = !string.IsNullOrWhiteSpace(label) || hasDynamicLabel;
        HorizontalAlignment contentHorizAlign = !string.IsNullOrWhiteSpace(spec.TextAlignment)
            ? ParseHorizontalAlignment(spec.TextAlignment, HorizontalAlignment.Center)
            : HorizontalAlignment.Center;

        FrameworkElement? liveIconEl = null;
        TextBlock? liveLabelTb = null;
        Grid? liveContentGrid = null;
        UIElement buttonContent;
        if (mayHaveIcon && mayHaveLabel)
        {
            var contentGrid = new Grid
            {
                ColumnSpacing = (!string.IsNullOrWhiteSpace(icon) && !string.IsNullOrWhiteSpace(label)) ? 6 : 0,
                HorizontalAlignment = contentHorizAlign,
                VerticalAlignment = VerticalAlignment.Center
            };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            liveIconEl = CreateInlineIconElement(string.IsNullOrWhiteSpace(icon) ? "\uE710" : icon, 13, accentIconFg);
            liveIconEl.Visibility = string.IsNullOrWhiteSpace(icon) ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(liveIconEl, 0);
            contentGrid.Children.Add(liveIconEl);

            var btnTb = new TextBlock
            {
                Text = label,
                FontSize = CaptionFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Visibility = string.IsNullOrWhiteSpace(label) ? Visibility.Collapsed : Visibility.Visible
            };
            if (isAccent)
            {
                WidgetTextShadow.SetCast(btnTb, false);
            }
            if (ContainsSystemMetricsToken(rawLabel))
            {
                SetTabularNumerals(btnTb);
            }
            liveLabelTb = btnTb;
            Grid.SetColumn(btnTb, 1);
            contentGrid.Children.Add(btnTb);
            liveContentGrid = contentGrid;
            buttonContent = contentGrid;
        }
        else if (mayHaveIcon)
        {
            liveIconEl = CreateInlineIconElement(string.IsNullOrWhiteSpace(icon) ? "\uE710" : icon, 13, accentIconFg);
            liveIconEl.Visibility = string.IsNullOrWhiteSpace(icon) ? Visibility.Collapsed : Visibility.Visible;
            buttonContent = liveIconEl;
        }
        else
        {
            var btnTb = new TextBlock
            {
                Text = label,
                FontSize = CaptionFontSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            if (isAccent)
            {
                WidgetTextShadow.SetCast(btnTb, false);
            }
            if (ContainsSystemMetricsToken(rawLabel))
            {
                SetTabularNumerals(btnTb);
            }
            liveLabelTb = btnTb;
            buttonContent = btnTb;
        }

        string lastResolvedIcon = icon ?? string.Empty;
        if (hasDynamicIcon || hasDynamicLabel)
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (liveLabelTb is not null && hasDynamicLabel)
                {
                    string nextLabel = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                    liveLabelTb.Text = nextLabel;
                    if (liveContentGrid is not null)
                    {
                        liveLabelTb.Visibility = string.IsNullOrWhiteSpace(nextLabel) ? Visibility.Collapsed : Visibility.Visible;
                    }
                }
                if (liveIconEl is not null && hasDynamicIcon)
                {
                    string nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx) ?? string.Empty;
                    if (!string.Equals(nextIcon, lastResolvedIcon, StringComparison.Ordinal))
                    {
                        lastResolvedIcon = nextIcon;
                        UpdateInlineIconGlyph(liveIconEl, nextIcon);
                        FluentMotionHelper.PlayGlyphMorphTransition(liveIconEl);
                    }
                    liveIconEl.Visibility = string.IsNullOrWhiteSpace(nextIcon) ? Visibility.Collapsed : Visibility.Visible;
                }
                if (liveContentGrid is not null && liveIconEl is not null && liveLabelTb is not null)
                {
                    liveContentGrid.ColumnSpacing =
                        liveIconEl.Visibility == Visibility.Visible && liveLabelTb.Visibility == Visibility.Visible ? 6 : 0;
                }
            });
        }

        bool isIconOnly = !mayHaveLabel && mayHaveIcon;
        bool stretchesHorizontally = string.Equals(spec.HorizontalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        bool stretchesVertically = string.Equals(spec.VerticalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        var btn = new Button
        {
            Content = buttonContent,
            HorizontalContentAlignment = contentHorizAlign,
            CornerRadius = isIconOnly ? CornerMedium : CornerSmall,
            Padding = isIconOnly
                ? new Thickness(0)
                : (!string.IsNullOrWhiteSpace(spec.Padding) ? ParseThickness(spec.Padding) : new Thickness(10, 5, 10, 5)),
            MinWidth = isIconOnly ? 26 : 0,
            MinHeight = isIconOnly ? 26 : 28,
            VerticalAlignment = VerticalAlignment.Center
        };
        MarkInteractiveElement(btn);
        if (isIconOnly)
        {
            if (!stretchesHorizontally && !spec.Width.HasValue)
            {
                btn.Width = 26;
            }
            if (!stretchesVertically && !spec.Height.HasValue)
            {
                btn.Height = 26;
            }
        }

        CornerRadius baseRadius = isIconOnly ? CornerMedium : CornerSmall;
        void ApplyVariantVisuals(string resolvedVariant, CustomWidgetEvaluationContext evalCtx)
        {
            bool nextAccent = string.Equals(resolvedVariant, "accent", StringComparison.OrdinalIgnoreCase);
            bool nextDanger = string.Equals(resolvedVariant, "danger", StringComparison.OrdinalIgnoreCase);
            bool nextGhostOrSubtle = string.Equals(resolvedVariant, "ghost", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(resolvedVariant, "subtle", StringComparison.OrdinalIgnoreCase);

            if (!nextAccent)
            {
                btn.ClearValue(FrameworkElement.StyleProperty);
                btn.FontWeight = FontWeights.Normal;
            }

            if (nextAccent)
            {
                StyleAccentCommitButton(btn);
                btn.CornerRadius = CornerSmall;
            }
            else if (nextDanger)
            {
                StyleStandardCommandButton(btn, baseRadius);
                btn.Foreground = ResolveForegroundBrush("danger");
            }
            else if (nextGhostOrSubtle)
            {
                Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
                btn.CornerRadius = baseRadius;
                btn.Background = transparent;
                btn.BorderBrush = transparent;
                btn.BorderThickness = new Thickness(0);
                btn.Foreground = ResolveVariantForeground(resolvedVariant, evalCtx);
                ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
            }
            else
            {
                StyleStandardCommandButton(btn, CornerSmall);
            }

            Brush contentFg = ResolveVariantForeground(resolvedVariant, evalCtx);
            if (liveLabelTb is not null)
            {
                liveLabelTb.Foreground = nextAccent
                    ? contentFg
                    : (nextDanger || nextGhostOrSubtle ? contentFg : ResolveForegroundBrush("default"));
                WidgetTextShadow.SetCast(liveLabelTb, !nextAccent);
            }
            if (liveIconEl is FontIcon liveFi)
            {
                liveFi.Foreground = (nextAccent || nextDanger || nextGhostOrSubtle)
                    ? contentFg
                    : ResolveForegroundBrush("default");
            }
            else if (liveIconEl is TextBlock liveIconTb && !CustomWidgetTemplates.IsEmojiIcon(liveIconTb.Text))
            {
                liveIconTb.Foreground = (nextAccent || nextDanger || nextGhostOrSubtle)
                    ? contentFg
                    : ResolveForegroundBrush("default");
            }

            ApplyRawButtonVisualOverrides(btn, liveLabelTb, liveIconEl, spec, evalCtx);
        }

        ApplyVariantVisuals(initialVariant, ctx);

        if (ContainsDynamicToken(spec.Variant) ||
            ContainsDynamicToken(spec.Color) ||
            ContainsDynamicToken(spec.TextColor))
        {
            string lastVariant = initialVariant;
            string lastColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx).Trim();
            string lastTextColor = CustomWidgetExpressionEvaluator.Interpolate(spec.TextColor, ctx).Trim();
            RegisterRetainedPatcher(liveCtx =>
            {
                string nextVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, liveCtx).Trim();
                string nextColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, liveCtx).Trim();
                string nextTextColor = CustomWidgetExpressionEvaluator.Interpolate(spec.TextColor, liveCtx).Trim();
                if (!string.Equals(nextVariant, lastVariant, StringComparison.Ordinal) ||
                    !string.Equals(nextColor, lastColor, StringComparison.Ordinal) ||
                    !string.Equals(nextTextColor, lastTextColor, StringComparison.Ordinal))
                {
                    lastVariant = nextVariant;
                    lastColor = nextColor;
                    lastTextColor = nextTextColor;
                    ApplyVariantVisuals(nextVariant, liveCtx);
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(btn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
            if (ContainsDynamicToken(spec.Tooltip))
            {
                RegisterRetainedPatcher(liveCtx =>
                    ToolTipService.SetToolTip(btn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, liveCtx)));
            }
        }

        bool isCopyNode = IsCopyActionNode(spec);
        bool isBtnCopy = string.Equals(spec.Id, "btn-copy", StringComparison.OrdinalIgnoreCase);
        bool hasDirectClipboardCopy = spec.OnClick.Any(static a =>
            string.Equals(a.Type, "clipboard.copy", StringComparison.OrdinalIgnoreCase));
        if (spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover) || isCopyNode)
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            Action? triggerCopyMorph = isCopyNode
                ? AttachCopyMorphFeedback(
                    btn,
                    liveIconEl,
                    liveLabelTb,
                    () => capturedCtx.I18n.GetValueOrDefault("copiedBtn")
                          ?? Tr("CustomWidget.Toast.Copied", capturedCtx.CultureName),
                    () => CustomWidgetExpressionEvaluator.Interpolate(rawLabel, capturedCtx),
                    () => ResolveNormalizedIcon(spec.Icon, capturedCtx) ?? icon ?? "\uE8C8",
                    accentIconFg)
                : null;
            string prevCopySeq = isBtnCopy
                ? ctx.State.GetValueOrDefault("copySeq", string.Empty)
                : string.Empty;

            RegisterRetainedPatcher(liveCtx =>
            {
                capturedCtx = liveCtx;
                if (isBtnCopy && triggerCopyMorph is not null)
                {
                    string curCopySeq = liveCtx.State.GetValueOrDefault("copySeq", string.Empty);
                    if (!string.Equals(prevCopySeq, curCopySeq, StringComparison.Ordinal))
                    {
                        prevCopySeq = curCopySeq;
                        if (!string.IsNullOrEmpty(curCopySeq) && !string.Equals(curCopySeq, "0", StringComparison.Ordinal))
                        {
                            triggerCopyMorph();
                        }
                    }
                }
            });

            if (spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover))
            {
                btn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    FluentMotionHelper.PlayGlyphMicroMotion(liveIconEl, lastResolvedIcon);
                    await ExecuteInteractiveClickWithPopoverAsync(spec, capturedCtx, btn);
                    if (hasDirectClipboardCopy)
                    {
                        triggerCopyMorph?.Invoke();
                    }
                });
            }
        }

        return btn;
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

    internal static string NormalizeFluentGlyphForOs(string? normalizedGlyph, bool isWindows11OrLater) =>
        CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs(normalizedGlyph, isWindows11OrLater);

    private static void UpdateInlineIconGlyph(FrameworkElement iconEl, string rawGlyph)
    {
        if (string.IsNullOrWhiteSpace(rawGlyph))
        {
            if (iconEl is FontIcon emptyFi)
            {
                emptyFi.Glyph = string.Empty;
            }
            else if (iconEl is TextBlock emptyTb)
            {
                emptyTb.Text = string.Empty;
            }
            return;
        }

        string normalized = CustomWidgetManifestParser.NormalizeGlyph(rawGlyph);
        if (iconEl is FontIcon fi && CustomWidgetTemplates.IsSegoeFluentGlyph(normalized))
        {
            fi.Glyph = NormalizeFluentGlyphForOs(normalized, WindowsCompatibilityService.IsWindows11OrLater);
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
            Glyph = NormalizeFluentGlyphForOs(normalized, WindowsCompatibilityService.IsWindows11OrLater),
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

    private void ApplyCommonLayoutProperties(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (spec.Width.HasValue) element.Width = spec.Width.Value;
        if (spec.Height.HasValue) element.Height = spec.Height.Value;
        if (spec.MinWidth.HasValue) element.MinWidth = spec.MinWidth.Value;
        if (spec.MaxWidth.HasValue) element.MaxWidth = spec.MaxWidth.Value;
        if (spec.MinHeight.HasValue) element.MinHeight = spec.MinHeight.Value;
        if (spec.MaxHeight.HasValue) element.MaxHeight = spec.MaxHeight.Value;
        if (!string.IsNullOrWhiteSpace(spec.Margin)) element.Margin = ParseThickness(spec.Margin);
        if (spec.Opacity.HasValue)
        {
            element.Opacity = Math.Clamp(spec.Opacity.Value, 0.0, 1.0);
        }
        else if (!string.IsNullOrWhiteSpace(spec.OpacityExpression))
        {
            static double ResolveDynamicOpacity(string expr, CustomWidgetEvaluationContext evalCtx, double fallback)
            {
                if (CustomWidgetExpressionEvaluator.EvaluateDouble(expr, evalCtx) is double num && double.IsFinite(num))
                {
                    return Math.Clamp(num, 0.0, 1.0);
                }

                string interpolated = CustomWidgetExpressionEvaluator.Interpolate(expr, evalCtx).Trim();
                if (double.TryParse(interpolated, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) &&
                    double.IsFinite(parsed))
                {
                    return Math.Clamp(parsed, 0.0, 1.0);
                }

                return fallback;
            }

            element.Opacity = ResolveDynamicOpacity(spec.OpacityExpression, ctx, 1.0);
            if (ContainsDynamicToken(spec.OpacityExpression))
            {
                string rawOpacityExpr = spec.OpacityExpression;
                RegisterRetainedPatcher(liveCtx =>
                {
                    element.Opacity = ResolveDynamicOpacity(rawOpacityExpr, liveCtx, element.Opacity);
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
        {
            element.HorizontalAlignment = ParseHorizontalAlignment(spec.HorizontalAlignment);
        }

        if (!string.IsNullOrWhiteSpace(spec.VerticalAlignment))
        {
            element.VerticalAlignment = ParseVerticalAlignment(spec.VerticalAlignment);
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

    private static double? ResolveEffectiveThreshold(
        string? expression,
        double? fallback,
        CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            double? evaluated = CustomWidgetExpressionEvaluator.EvaluateDouble(expression, ctx);
            if (evaluated.HasValue && evaluated.Value > 0)
            {
                return evaluated.Value;
            }
        }

        return fallback;
    }

    internal static UIElement WrapInShadowHostGrid(UIElement content)
    {
        if (content is Grid)
        {
            return content;
        }

        if (content is FrameworkElement { Parent: { } priorParent })
        {
            App.Log($"[CustomWidgetRenderer] Detaching unexpected prior parent {priorParent.GetType().Name} before WrapInShadowHostGrid.");
            switch (priorParent)
            {
                case Border parentBorder when ReferenceEquals(parentBorder.Child, content):
                    parentBorder.Child = null;
                    break;
                case ContentPresenter parentPresenter when ReferenceEquals(parentPresenter.Content, content):
                    parentPresenter.Content = null;
                    break;
                case ContentControl parentControl when ReferenceEquals(parentControl.Content, content):
                    parentControl.Content = null;
                    break;
                case Panel parentPanel:
                    parentPanel.Children.Remove(content);
                    break;
            }
        }

        var host = new Grid();
        host.Children.Add(content);
        return host;
    }

    internal static bool ContainsDynamicToken(string? template) =>
        !string.IsNullOrEmpty(template) && template.Contains('$', StringComparison.Ordinal);

    internal static bool ContainsSystemMetricsToken(string? template) =>
        !string.IsNullOrEmpty(template) &&
        (template.Contains("$sys.", StringComparison.Ordinal) ||
         template.Contains("$device.", StringComparison.Ordinal) ||
         template.Contains("$music.", StringComparison.Ordinal) ||
         template.Contains("$todo.", StringComparison.Ordinal) ||
         template.Contains("$pomodoro.", StringComparison.Ordinal) ||
         template.Contains("$pomo.", StringComparison.Ordinal) ||
         template.Contains("$countdown.", StringComparison.Ordinal) ||
         template.Contains("$time.", StringComparison.Ordinal) ||
         template.Contains("$clock.", StringComparison.Ordinal) ||
         template.Contains("$clipboard.", StringComparison.Ordinal) ||
         template.Contains("$drives.", StringComparison.Ordinal) ||
         template.Contains("$computed.", StringComparison.Ordinal) ||
         template.Contains("computed.", StringComparison.Ordinal) ||
         template.Contains("tz(", StringComparison.OrdinalIgnoreCase) ||
         template.Contains("tzDiffLabel(", StringComparison.OrdinalIgnoreCase) ||
         template.Contains("tzWorkState(", StringComparison.OrdinalIgnoreCase) ||
         template.Contains("tzCity(", StringComparison.OrdinalIgnoreCase) ||
         template.Contains("$ai.", StringComparison.Ordinal));

    private static IReadOnlyList<CustomWidgetActionSpec> FilterNonRedundantInputOnChangeActions(
        IReadOnlyList<CustomWidgetActionSpec> actions,
        string? bindKey)
    {
        if (actions.Count == 0 || string.IsNullOrWhiteSpace(bindKey))
        {
            return actions;
        }

        string cleanBind = NormalizeBoundStateLeafKey(bindKey);
        if (string.IsNullOrEmpty(cleanBind))
        {
            return actions;
        }

        List<CustomWidgetActionSpec>? filtered = null;
        for (int i = 0; i < actions.Count; i++)
        {
            CustomWidgetActionSpec act = actions[i];
            bool isSelfStateSet =
                string.Equals(act.Type, "state.set", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeBoundStateLeafKey(act.Key), cleanBind, StringComparison.Ordinal) &&
                (act.Then is null || act.Then.Count == 0) &&
                act.Value is not null &&
                (string.Equals(act.Value.Trim(), "$value", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(act.Value.Trim(), "${$value}", StringComparison.OrdinalIgnoreCase));
            if (isSelfStateSet)
            {
                if (filtered is null)
                {
                    filtered = new List<CustomWidgetActionSpec>(actions.Count - 1);
                    for (int j = 0; j < i; j++)
                    {
                        filtered.Add(actions[j]);
                    }
                }
                continue;
            }

            filtered?.Add(act);
        }

        return filtered ?? actions;
    }

    private static string NormalizeBoundStateLeafKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return string.Empty;
        }

        string k = rawKey.Trim();
        if (k.StartsWith("${", StringComparison.Ordinal) && k.EndsWith('}'))
        {
            k = k[2..^1].Trim();
        }
        if (k.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            k = k[6..].Trim();
        }
        return k;
    }
}
