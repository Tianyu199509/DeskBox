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

internal sealed partial class StudioStageViewportController : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioStageViewportController(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }

    private CustomWidgetViewModel? _previewVm;
    private CustomWidgetContent? _previewContent;
    private ColumnDefinition? _stageColumn;
    private bool _stageWidthManuallyAdjusted;
    private double _lastPreviewWidth = CustomWidgetStudioWindow.DesiredWidth;
    private double _currentPreviewWidgetWidth = 320;
    private Border? _stageBorder;
    private Border? _widgetFrame;
    private TextBlock? _headerEmojiText;
    private TextBlock? _nameText;
    private DeskBox.Controls.WidgetTitleIcon? _frameTitleIcon;
    private TextBlock? _frameTitleText;
    private Button? _sizeQuickButton;
    private Border? _stageWallpaperLayer;
    private Border? _stageWallpaperScrim;
    private ProgressBar? _stageGeneratingProgressBar;
    private ProgressRing? _stageGeneratingProgressRing;
    private Border? _stageGeneratingStatusPill;
    private TextBlock? _stageGeneratingStatusText;
    private Border? _frameDivider;
    private FontIcon? _frameEditGlyph;
    private Button? _pickElementButton;
    private FontIcon? _pickElementIcon;
    private TextBlock? _pickElementLabel;
    private ToolkitControls.Segmented? _stageViewportModeSegmented;
    private ToolkitControls.Segmented? _stageBackdropSegmented;
    private ToolkitControls.Segmented? _stageWidgetThemeSegmented;
    private ToolkitControls.Segmented? _stageChromeModeSegmented;
    private Button? _stageAppearanceButton;
    private Button? _resetPreviewStateButton;
    private int? _currentStageZoomPercent;
    private Action? _refreshStageZoomBadgeAction;
    private Border? _stageToastBorder;
    private TextBlock? _stageToastTitleText;
    private TextBlock? _stageToastBodyText;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _stageToastTimer;
    private Border? _scriptErrorBannerBorder;
    private TextBlock? _scriptErrorBannerText;
    private Button? _scriptErrorFixButton;
    private Border? _runtimeAnomalyBannerBorder;
    private TextBlock? _runtimeAnomalyBannerText;
    private Button? _runtimeAnomalyDiagnoseButton;
    private CustomWidgetRuntimeTraceEntry? _latestUnacknowledgedRuntimeAnomaly;
    private Button? _sampleDataToggleButton;
    private Button? _settingsPreviewButton;
    private string? _activeStudioPopoverPreviewId;
    private DropDownButton? _popoverPreviewButton;
    private Button? _fileDropTestButton;
    private Viewbox? _stageViewbox;
    private Border? _stageShadowReceiver;
    private Button? _titleEditButton;
    private Grid? _resizeHandlesOverlay;
    private Border? _resizeDimensionPill;
    private TextBlock? _resizeDimensionText;
    private Action<AgentStageSummary, int>? _showIncomingStageBannerAction;
    private Action? _hideIncomingStageBannerAction;
    private string? _dismissedIncomingStageId;

    internal void ShowIncomingAgentStageBanner(AgentStageSummary stage, int pendingCount)
    {
        if (string.Equals(stage.StageId, _dismissedIncomingStageId, StringComparison.Ordinal))
        {
            return;
        }
        _showIncomingStageBannerAction?.Invoke(stage, pendingCount);
    }

    internal void HideIncomingAgentStageBanner() => _hideIncomingStageBannerAction?.Invoke();

    internal bool CanMountOrUpdatePreviewInPlace => _stageViewbox is not null;

    internal CustomWidgetViewModel? PreviewVm { get => _previewVm; set => _previewVm = value; }
    internal CustomWidgetContent? PreviewContent { get => _previewContent; set => _previewContent = value; }
    internal ColumnDefinition? StageColumn { get => _stageColumn; set => _stageColumn = value; }
    internal bool StageWidthManuallyAdjusted { get => _stageWidthManuallyAdjusted; set => _stageWidthManuallyAdjusted = value; }
    internal double LastPreviewWidth { get => _lastPreviewWidth; set => _lastPreviewWidth = value; }
    internal double CurrentPreviewWidgetWidth { get => _currentPreviewWidgetWidth; set => _currentPreviewWidgetWidth = value; }
    internal Border? StageBorder { get => _stageBorder; set => _stageBorder = value; }
    internal Border? WidgetFrame { get => _widgetFrame; set => _widgetFrame = value; }
    internal TextBlock? HeaderEmojiText { get => _headerEmojiText; set => _headerEmojiText = value; }
    internal TextBlock? NameText { get => _nameText; set => _nameText = value; }
    internal TextBlock? FrameTitleText { get => _frameTitleText; set => _frameTitleText = value; }
    internal Button? SizeQuickButton { get => _sizeQuickButton; set => _sizeQuickButton = value; }
    internal Border? StageWallpaperLayer { get => _stageWallpaperLayer; set => _stageWallpaperLayer = value; }
    internal Border? StageWallpaperScrim { get => _stageWallpaperScrim; set => _stageWallpaperScrim = value; }
    internal ProgressBar? StageGeneratingProgressBar { get => _stageGeneratingProgressBar; set => _stageGeneratingProgressBar = value; }
    internal ProgressRing? StageGeneratingProgressRing { get => _stageGeneratingProgressRing; set => _stageGeneratingProgressRing = value; }
    internal Border? StageGeneratingStatusPill { get => _stageGeneratingStatusPill; set => _stageGeneratingStatusPill = value; }
    internal TextBlock? StageGeneratingStatusText { get => _stageGeneratingStatusText; set => _stageGeneratingStatusText = value; }
    internal Border? FrameDivider { get => _frameDivider; set => _frameDivider = value; }
    internal FontIcon? FrameEditGlyph { get => _frameEditGlyph; set => _frameEditGlyph = value; }
    internal ToolkitControls.Segmented? StageViewportModeSegmented { get => _stageViewportModeSegmented; set => _stageViewportModeSegmented = value; }
    internal Border? ScriptErrorBannerBorder { get => _scriptErrorBannerBorder; set => _scriptErrorBannerBorder = value; }
    internal TextBlock? ScriptErrorBannerText { get => _scriptErrorBannerText; set => _scriptErrorBannerText = value; }
    internal Button? ScriptErrorFixButton { get => _scriptErrorFixButton; set => _scriptErrorFixButton = value; }

    internal void RefreshStageToolbarChrome()
    {
        ElementTheme stageTheme = _stageBorder?.RequestedTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;
        bool isLightStage = stageTheme == ElementTheme.Light;

        Brush pillBg = new SolidColorBrush(
            isLightStage
                ? Windows.UI.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Brush pillBorder = new SolidColorBrush(
            isLightStage
                ? Windows.UI.Color.FromArgb(0x1C, 0x00, 0x00, 0x00)
                : Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Brush textPrimary = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorPrimaryBrush", stageTheme);
        Brush textSecondary = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorSecondaryBrush", stageTheme);

        if (_pickElementButton is not null)
        {
            _pickElementButton.IsEnabled = !_session.IsCapsulePreviewMode && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
            _pickElementButton.RequestedTheme = stageTheme;
            if (_session.IsInspectorModeEnabled && _pickElementButton.IsEnabled)
            {
                Brush accentStroke = StudioUiPrimitives.StageThemeBrush(_shell, "AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));
                _pickElementButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 30 : 44), accentColor.R, accentColor.G, accentColor.B));
                _pickElementButton.BorderBrush = accentStroke;
                _pickElementButton.BorderThickness = new Thickness(1);
                if (_pickElementIcon is not null)
                {
                    _pickElementIcon.Foreground = accentText;
                }
                if (_pickElementLabel is not null)
                {
                    _pickElementLabel.Foreground = accentText;
                    _pickElementLabel.FontWeight = FontWeights.SemiBold;
                }
            }
            else
            {
                _pickElementButton.Background = pillBg;
                _pickElementButton.BorderBrush = pillBorder;
                _pickElementButton.BorderThickness = new Thickness(1);
                if (_pickElementIcon is not null)
                {
                    _pickElementIcon.Foreground = textSecondary;
                }
                if (_pickElementLabel is not null)
                {
                    _pickElementLabel.Foreground = textPrimary;
                    _pickElementLabel.FontWeight = FontWeights.Normal;
                }
            }
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.RequestedTheme = stageTheme;
            _sizeQuickButton.Background = pillBg;
            _sizeQuickButton.BorderBrush = pillBorder;
            _sizeQuickButton.BorderThickness = new Thickness(1);
            _sizeQuickButton.Foreground = textPrimary;
        }

        if (_sampleDataToggleButton is not null)
        {
            _sampleDataToggleButton.RequestedTheme = stageTheme;
            _sampleDataToggleButton.Background = pillBg;
            _sampleDataToggleButton.BorderBrush = pillBorder;
            _sampleDataToggleButton.BorderThickness = new Thickness(1);
            _sampleDataToggleButton.Foreground = textPrimary;
        }

        if (_settingsPreviewButton is not null)
        {
            _settingsPreviewButton.IsEnabled = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson) && !_session.IsInPlaceGenerating;
            _settingsPreviewButton.RequestedTheme = stageTheme;
            _settingsPreviewButton.Background = pillBg;
            _settingsPreviewButton.BorderBrush = pillBorder;
            _settingsPreviewButton.BorderThickness = new Thickness(1);
            _settingsPreviewButton.Foreground = textPrimary;
        }

        if (_popoverPreviewButton is not null)
        {
            _popoverPreviewButton.RequestedTheme = stageTheme;
            bool isPopoverActive = !string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId);
            if (isPopoverActive)
            {
                Brush accentStroke = StudioUiPrimitives.StageThemeBrush(_shell, "AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));
                _popoverPreviewButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 26 : 40), accentColor.R, accentColor.G, accentColor.B));
                _popoverPreviewButton.BorderBrush = accentStroke;
                _popoverPreviewButton.Foreground = accentText;
            }
            else
            {
                _popoverPreviewButton.Background = pillBg;
                _popoverPreviewButton.BorderBrush = pillBorder;
                _popoverPreviewButton.Foreground = textPrimary;
            }
            _popoverPreviewButton.BorderThickness = new Thickness(1);
        }

        if (_fileDropTestButton is not null)
        {
            _fileDropTestButton.RequestedTheme = stageTheme;
            _fileDropTestButton.Background = pillBg;
            _fileDropTestButton.BorderBrush = pillBorder;
            _fileDropTestButton.BorderThickness = new Thickness(1);
            _fileDropTestButton.Foreground = textPrimary;
        }

        if (_shell.LocaleCenter.PreviewCultureOverridePillButton is not null)
        {
            bool showOverride = !string.IsNullOrWhiteSpace(_session.ActivePreviewCultureOverride) &&
                                !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
            if (!showOverride)
            {
                _shell.LocaleCenter.PreviewCultureOverridePillButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                CustomWidgetSupportedLocaleInfo info =
                    CustomWidgetLocaleCatalog.GetLocaleInfo(_session.ActivePreviewCultureOverride!);
                string labelText = $"{info.NativeName} ({info.Code})  ✕";
                string resetTip = _loc.Format("CustomWidget.Workshop.I18n.ResetPreviewCultureTip", info.NativeName);
                Brush accentStroke = StudioUiPrimitives.StageThemeBrush(_shell, "AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));

                _shell.LocaleCenter.PreviewCultureOverridePillButton.RequestedTheme = stageTheme;
                _shell.LocaleCenter.PreviewCultureOverridePillButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 28 : 42), accentColor.R, accentColor.G, accentColor.B));
                _shell.LocaleCenter.PreviewCultureOverridePillButton.BorderBrush = accentStroke;
                _shell.LocaleCenter.PreviewCultureOverridePillButton.BorderThickness = new Thickness(1);
                _shell.LocaleCenter.PreviewCultureOverridePillButton.Foreground = accentText;
                _shell.LocaleCenter.PreviewCultureOverridePillButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uF2B7", labelText);
                ToolTipService.SetToolTip(_shell.LocaleCenter.PreviewCultureOverridePillButton, resetTip);
                AutomationProperties.SetName(_shell.LocaleCenter.PreviewCultureOverridePillButton, resetTip);
                _shell.LocaleCenter.PreviewCultureOverridePillButton.Visibility = Visibility.Visible;
            }
        }

        if (_stageViewportModeSegmented is not null)
        {
            _stageViewportModeSegmented.RequestedTheme = stageTheme;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_stageViewportModeSegmented);
        }

        if (_stageAppearanceButton is not null)
        {
            _stageAppearanceButton.RequestedTheme = stageTheme;
            bool isExplicitTheme = !string.Equals(
                _session.StageWidgetTheme,
                CustomWidgetStudioPreferencesStore.StageThemeAuto,
                StringComparison.OrdinalIgnoreCase);
            (string themeGlyph, string themeLabelKey) = _session.StageWidgetTheme switch
            {
                CustomWidgetStudioPreferencesStore.StageThemeLight => ("\uE706", "CustomWidget.Workshop.Stage.ThemeLight"),
                CustomWidgetStudioPreferencesStore.StageThemeDark => ("\uE708", "CustomWidget.Workshop.Stage.ThemeDark"),
                _ => ("\uE790", "CustomWidget.Workshop.Stage.ThemeAuto")
            };
            string themeLabel = _loc.T(themeLabelKey);
            StageBackdropMode effectiveBackdropMode = _session.StageBackdropMode == StageBackdropMode.Wallpaper && !_session.HasSystemWallpaperAvailable
                ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
                : _session.StageBackdropMode;
            string backdropLabel = _loc.T(effectiveBackdropMode switch
            {
                StageBackdropMode.Wallpaper => "CustomWidget.Workshop.Stage.BackdropWallpaper",
                StageBackdropMode.Light => "CustomWidget.Workshop.Stage.BackdropLight",
                _ => "CustomWidget.Workshop.Stage.BackdropDark"
            });
            ElementTheme effectiveAutoTheme = effectiveBackdropMode switch
            {
                StageBackdropMode.Wallpaper => _shell.PreviewTheme.CachedWallpaperTheme ?? ElementTheme.Dark,
                StageBackdropMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Light
            };
            string effectiveThemeLabel = _loc.T(effectiveAutoTheme == ElementTheme.Light
                ? "CustomWidget.Workshop.Stage.ThemeLight"
                : "CustomWidget.Workshop.Stage.ThemeDark");
            bool hasCustomChrome = _session.StageChromeMode != WidgetChromeMode.System;
            string chromeLabel = _loc.T(_session.StageChromeMode switch
            {
                WidgetChromeMode.Standard => "Widget.ChromeMode.Standard",
                WidgetChromeMode.Compact => "Widget.ChromeMode.Compact",
                WidgetChromeMode.Overlay => "Widget.ChromeMode.Overlay",
                _ => "CustomWidget.Workshop.Stage.ThemeAuto"
            });
            string themeSummary = isExplicitTheme
                ? $"{backdropLabel} · {themeLabel} · {chromeLabel}"
                : $"{backdropLabel} · {_loc.Format("CustomWidget.Workshop.Stage.ThemeToggleTooltipResolvedFormat", themeLabel, effectiveThemeLabel)} · {chromeLabel}";
            string themeTip = $"{_loc.T("CustomWidget.Workshop.Stage.AppearanceButtonTip")}\n{themeSummary}";

            _stageAppearanceButton.Content = new FontIcon { Glyph = themeGlyph, FontSize = 12 };
            ToolTipService.SetToolTip(_stageAppearanceButton, themeTip);
            AutomationProperties.SetName(_stageAppearanceButton, _loc.T("CustomWidget.Workshop.Stage.AppearanceButtonTip"));
            if (isExplicitTheme || hasCustomChrome)
            {
                Brush accentStroke = StudioUiPrimitives.StageThemeBrush(_shell, "AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));
                _stageAppearanceButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 26 : 40), accentColor.R, accentColor.G, accentColor.B));
                _stageAppearanceButton.BorderBrush = accentStroke;
                _stageAppearanceButton.Foreground = accentText;
            }
            else
            {
                _stageAppearanceButton.Background = pillBg;
                _stageAppearanceButton.BorderBrush = pillBorder;
                _stageAppearanceButton.Foreground = textPrimary;
            }
        }

        if (_resetPreviewStateButton is not null)
        {
            _resetPreviewStateButton.IsEnabled = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson) && !_session.IsInPlaceGenerating;
            _resetPreviewStateButton.RequestedTheme = stageTheme;
            _resetPreviewStateButton.Background = pillBg;
            _resetPreviewStateButton.BorderBrush = pillBorder;
            _resetPreviewStateButton.BorderThickness = new Thickness(1);
            _resetPreviewStateButton.Foreground = textPrimary;
        }

        if (_runtimeAnomalyBannerBorder is not null)
        {
            _runtimeAnomalyBannerBorder.RequestedTheme = stageTheme;
        }

        if (_stageGeneratingStatusPill is not null && _stageGeneratingStatusText is not null)
        {
            _stageGeneratingStatusPill.RequestedTheme = stageTheme;
            _stageGeneratingStatusPill.Background = new SolidColorBrush(
                isLightStage
                    ? Windows.UI.Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0xE8, 0x1E, 0x22, 0x2B));
            _stageGeneratingStatusPill.BorderBrush = pillBorder;
            _stageGeneratingStatusText.Foreground = textSecondary;
        }
    }

    internal void RefreshSampleDataBadgeChrome()
    {
        if (_sampleDataToggleButton is null)
        {
            return;
        }

        bool hasSample = !_session.IsEditMode &&
                         !string.IsNullOrWhiteSpace(_session.CurrentManifestJson) &&
                         (!string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson) || (_previewVm?.HasPreviewSampleData ?? false));
        if (!hasSample)
        {
            _sampleDataToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        string shortLabel = _session.IsSampleDataMutedInPreview
            ? _loc.T("CustomWidget.Workshop.CoCreate.EmptyStateShort")
            : _loc.T("CustomWidget.Workshop.CoCreate.SampleDataShort");
        string fullStatus = _session.IsSampleDataMutedInPreview
            ? _loc.T("CustomWidget.Workshop.CoCreate.EmptyStateBadge")
            : _loc.T("CustomWidget.Workshop.CoCreate.SampleDataBadge");
        string glyph = _session.IsSampleDataMutedInPreview ? "\uE7B8" : "\uE9D9";

        _sampleDataToggleButton.Content = StudioUiPrimitives.CreateSmallIconLabel(glyph, shortLabel);
        ToolTipService.SetToolTip(
            _sampleDataToggleButton,
            $"{fullStatus}\n{_loc.T("CustomWidget.Workshop.CoCreate.SampleDataToggleTip")}");
        AutomationProperties.SetName(_sampleDataToggleButton, fullStatus);
        _sampleDataToggleButton.Visibility = Visibility.Visible;
    }

    internal void RefreshSettingsPreviewBadgeChrome()
    {
        RefreshPopoverAndDropToolbarChrome();

        if (_settingsPreviewButton is null)
        {
            return;
        }

        int settingsCount = _previewVm?.Manifest.Settings.Count ?? 0;
        if (settingsCount <= 0 || string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            _settingsPreviewButton.Visibility = Visibility.Collapsed;
            return;
        }

        string fullBadgeLabel = _loc.Format("CustomWidget.Workshop.Stage.SettingsBadgeFormat", settingsCount);
        string compactCount = settingsCount.ToString(CultureInfo.InvariantCulture);
        string tip = $"{fullBadgeLabel}\n{_loc.T("CustomWidget.Workshop.Stage.SettingsTooltip")}";
        _settingsPreviewButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE713", compactCount);
        ToolTipService.SetToolTip(_settingsPreviewButton, tip);
        AutomationProperties.SetName(_settingsPreviewButton, fullBadgeLabel);
        _settingsPreviewButton.IsEnabled = !_session.IsInPlaceGenerating;
        _settingsPreviewButton.Visibility = Visibility.Visible;
    }

    private string ResolvePopoverDisplayTitle(CustomWidgetNodeSpec? pop, string fallbackId)
    {
        if (pop is null || string.IsNullOrWhiteSpace(pop.Title))
        {
            return fallbackId;
        }

        string raw = pop.Title.Trim();
        if (_previewVm is not null)
        {
            try
            {
                string evaluated = CustomWidgetExpressionEvaluator.Interpolate(raw, _previewVm.CreateEvaluationContext()).Trim();
                if (!string.IsNullOrWhiteSpace(evaluated))
                {
                    return evaluated;
                }
            }
            catch
            {
            }
        }

        return raw;
    }

    private void RefreshPopoverAndDropToolbarChrome()
    {
        CustomWidgetManifest? manifest = _previewVm?.Manifest;
        bool hasManifest = manifest is not null && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);

        if (_popoverPreviewButton is not null)
        {
            IReadOnlyList<CustomWidgetNodeSpec> popovers = hasManifest ? manifest!.Popovers : [];
            if (!hasManifest || popovers.Count == 0)
            {
                if (_activeStudioPopoverPreviewId is not null)
                {
                    _activeStudioPopoverPreviewId = null;
                    if (_previewContent is not null)
                    {
                        _previewContent.ActivePopoverPreviewId = null;
                    }
                }
                _popoverPreviewButton.Visibility = Visibility.Collapsed;
            }
            else if (_session.IsCapsulePreviewMode)
            {
                _popoverPreviewButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                CustomWidgetNodeSpec? activePop = !string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId)
                    ? popovers.FirstOrDefault(p => string.Equals(p.PopoverId ?? p.Id, _activeStudioPopoverPreviewId, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (activePop is null && _activeStudioPopoverPreviewId is not null)
                {
                    _activeStudioPopoverPreviewId = null;
                    if (_previewContent is not null)
                    {
                        _previewContent.ActivePopoverPreviewId = null;
                    }
                }

                string label;
                if (string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId))
                {
                    label = _loc.Format("CustomWidget.Workshop.Stage.PopoverBadgeFormat", popovers.Count);
                }
                else
                {
                    string resolvedTitle = ResolvePopoverDisplayTitle(activePop, _activeStudioPopoverPreviewId!);
                    string shortTitle = resolvedTitle.Length > 10 ? $"{resolvedTitle[..10]}…" : resolvedTitle;
                    label = _loc.Format("CustomWidget.Workshop.Stage.PopoverActiveFormat", shortTitle);
                }
                string tip = _loc.T("CustomWidget.Workshop.Stage.PopoverTip");
                _popoverPreviewButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8A7", label);
                ToolTipService.SetToolTip(_popoverPreviewButton, tip);
                AutomationProperties.SetName(_popoverPreviewButton, label);

                var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
                var mainItem = new ToggleMenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Stage.PopoverMainView"),
                    IsChecked = string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId)
                };
                mainItem.Click += (_, _) => SetActiveStudioPopoverPreview(null);
                menu.Items.Add(mainItem);
                menu.Items.Add(new MenuFlyoutSeparator());

                foreach (CustomWidgetNodeSpec pop in popovers)
                {
                    string popId = !string.IsNullOrWhiteSpace(pop.PopoverId) ? pop.PopoverId!.Trim() : (pop.Id ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(popId))
                    {
                        continue;
                    }
                    string resolvedTitle = ResolvePopoverDisplayTitle(pop, popId);
                    string popTitle = !string.Equals(resolvedTitle, popId, StringComparison.OrdinalIgnoreCase)
                        ? $"{resolvedTitle} ({popId})"
                        : popId;
                    string capturedId = popId;
                    var popItem = new ToggleMenuFlyoutItem
                    {
                        Text = popTitle,
                        IsChecked = string.Equals(_activeStudioPopoverPreviewId, capturedId, StringComparison.OrdinalIgnoreCase)
                    };
                    popItem.Click += (_, _) => SetActiveStudioPopoverPreview(capturedId);
                    menu.Items.Add(popItem);
                }

                _popoverPreviewButton.Flyout = menu;
                _popoverPreviewButton.IsEnabled = true;
                _popoverPreviewButton.Visibility = Visibility.Visible;
            }
        }

        if (_fileDropTestButton is not null)
        {
            bool acceptsFiles = hasManifest && CustomWidgetViewModel.DoesManifestAcceptFileDrops(manifest);
            _fileDropTestButton.IsEnabled = acceptsFiles && !_session.IsInPlaceGenerating;
            _fileDropTestButton.Visibility = acceptsFiles ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetActiveStudioPopoverPreview(string? popoverId)
    {
        string? normalized = string.IsNullOrWhiteSpace(popoverId) ? null : popoverId.Trim();
        if (string.Equals(_activeStudioPopoverPreviewId, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(normalized) && _session.IsCapsulePreviewMode)
        {
            _session.IsCapsulePreviewMode = false;
            RefreshStageViewportModeVisuals();
        }

        _activeStudioPopoverPreviewId = normalized;
        _session.SelectedTargetNode = null;
        if (_previewContent is not null)
        {
            _previewContent.SelectedNodePath = null;
            _previewContent.ActivePopoverPreviewId = normalized;
        }

        _shell.ChatPanel.RefreshTargetNodePillChrome();
        RefreshPopoverAndDropToolbarChrome();
        RefreshStageToolbarChrome();

        if (_widgetFrame is not null)
        {
            CustomWidgetManifest? effectiveManifest = ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
            CustomWidgetSizeSpec effectiveFrameSize = ResolveEffectiveStageFrameSize(effectiveManifest);
            _widgetFrame.Width = Math.Clamp(effectiveFrameSize.Width, 160, 640);
            _widgetFrame.Height = Math.Clamp(effectiveFrameSize.Height, 96, 900);
        }

        if (_resizeHandlesOverlay is not null)
        {
            _resizeHandlesOverlay.Visibility = !_session.IsCapsulePreviewMode && string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnStudioPopoverPreviewRequested(string mode, string popoverId)
    {
        if (_shell.DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() =>
            {
                if (!_shell.IsClosed)
                {
                    OnStudioPopoverPreviewRequested(mode, popoverId);
                }
            });
            return;
        }

        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();
        if (string.Equals(normalizedMode, "close", StringComparison.Ordinal) ||
            string.Equals(popoverId, "*", StringComparison.Ordinal))
        {
            SetActiveStudioPopoverPreview(null);
            return;
        }

        if (string.Equals(normalizedMode, "toggle", StringComparison.Ordinal) &&
            string.Equals(_activeStudioPopoverPreviewId, popoverId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            SetActiveStudioPopoverPreview(null);
            return;
        }

        SetActiveStudioPopoverPreview(popoverId);
    }

    private async Task TestStageFileDropFromPickerAsync()
    {
        if (_previewVm is null || _shell.IsClosed)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> picked = await FileOpenPickerService.PickFilesAsync(_shell.WindowHandle, null);
            if (picked.Count > 0 && !_shell.IsClosed && _previewVm is not null)
            {
                await _previewVm.ImportDroppedFilesAsync(picked);
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] TestStageFileDropFromPickerAsync failed: {ex.Message}");
        }
    }

    internal void SetStudioWindowActive(bool active)
    {
        _previewContent?.SetWindowVisibility(active);
        _previewVm?.OnWindowVisibilityChanged(active);
    }

    private bool _isReapplyingSessionOverrides;

    private void OnPreviewVmSettingChanged(string key, string value)
    {
        if (_isReapplyingSessionOverrides || string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        _session.SessionSettingOverrides[key] = value ?? string.Empty;
    }

    private void OnPreviewVmSecretChanged(string key, string value)
    {
        if (_isReapplyingSessionOverrides || string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (string.IsNullOrEmpty(value))
        {
            _session.SessionSecretOverrides.Remove(key);
        }
        else
        {
            _session.SessionSecretOverrides[key] = value;
        }
    }

    internal void ReapplySessionSettingOverridesToPreviewVm()
    {
        if (_previewVm is null || (_session.SessionSettingOverrides.Count == 0 && _session.SessionSecretOverrides.Count == 0))
        {
            return;
        }

        var declaredSettings = _previewVm.Manifest.Settings;
        if (declaredSettings.Count == 0)
        {
            return;
        }

        _isReapplyingSessionOverrides = true;
        try
        {
            foreach (CustomWidgetSettingDefinition def in declaredSettings)
            {
                if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    if (_session.SessionSecretOverrides.TryGetValue(def.Key, out string? secretVal))
                    {
                        _ = _previewVm.SetSecretValueAsync(def.Key, secretVal);
                    }
                }
                else if (_session.SessionSettingOverrides.TryGetValue(def.Key, out string? settingVal))
                {
                    _ = _previewVm.SetSettingValueAsync(def.Key, settingVal);
                }
            }
        }
        finally
        {
            _isReapplyingSessionOverrides = false;
        }
    }

    private void ToggleSampleDataPreviewMode()
    {
        string? curJson = _session.CurrentManifestJson;
        if (_previewVm is null || string.IsNullOrWhiteSpace(curJson))
        {
            return;
        }

        _session.IsSampleDataMutedInPreview = !_session.IsSampleDataMutedInPreview;
        _previewVm.AllowPreviewHostStandIns = !_session.IsSampleDataMutedInPreview;
        if (_session.IsSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
            _previewVm.ApplyPreviewBundle(curJson, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: false);
            if (_session.IsEditMode && _session.EditBaselineStorePayload is not null)
            {
                _previewVm.ApplyPreviewSeedPayload(_session.EditBaselineStorePayload);
            }
            _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        }
        else
        {
            _previewVm.ApplyPreviewBundle(curJson, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: false);
            if (_session.IsEditMode && _session.EditBaselineStorePayload is not null)
            {
                _previewVm.ApplyPreviewSeedPayload(_session.EditBaselineStorePayload);
            }
            _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
            if (!string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson))
            {
                _previewVm.ApplyPreviewSampleDataJson(_session.PreviewSampleDataJson);
            }
        }

        ReapplySessionSettingOverridesToPreviewVm();
        SyncRuntimeScriptErrorFromPreviewVm();
        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        RefreshSampleDataBadgeChrome();
    }

    private void ResetPreviewRuntimeState()
    {
        string? curJson = _session.CurrentManifestJson;
        if (_previewVm is null || string.IsNullOrWhiteSpace(curJson) || _session.IsInPlaceGenerating)
        {
            return;
        }

        _latestUnacknowledgedRuntimeAnomaly = null;
        _previewVm.AllowPreviewHostStandIns = !_session.IsSampleDataMutedInPreview;
        _previewVm.ApplyPreviewBundle(curJson, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: false);
        if (_session.IsEditMode && _session.EditBaselineStorePayload is not null)
        {
            _previewVm.ApplyPreviewSeedPayload(_session.EditBaselineStorePayload);
        }
        else if (!_session.IsEditMode &&
                 !string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson) &&
                 !_session.IsSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_session.PreviewSampleDataJson);
        }
        else if (_session.IsSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        ReapplySessionSettingOverridesToPreviewVm();
        SyncRuntimeScriptErrorFromPreviewVm();
        RefreshScriptErrorBannerChrome();
        RefreshRuntimeAnomalyBannerChrome();
        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        RefreshSampleDataBadgeChrome();
        ShowStageToastBanner(
            _loc.T("CustomWidget.Workshop.Stage.ResetPreviewStateTip"),
            _loc.T("CustomWidget.Workshop.Stage.ResetPreviewStateDone"));
    }

    internal CustomWidgetManifest? ResolvePreviewManifestWithSidecar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(json!).Manifest;
        CustomWidgetLocaleCatalog? locales = _session.ActiveI18nWorkingCatalog ?? _session.CurrentLocales;
        if (manifest is not null && locales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(locales);
        }

        return manifest;
    }

    private CustomWidgetSizeSpec ResolveEffectiveStageFrameSize(CustomWidgetManifest? manifest)
    {
        CustomWidgetSizeSpec baseSize = manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        if (string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId) || manifest?.View is null)
        {
            return baseSize;
        }

        CustomWidgetNodeSpec? popSpec = FindPopoverSpecById(manifest, _activeStudioPopoverPreviewId!);
        if (popSpec is null)
        {
            return baseSize;
        }

        int popW = popSpec.Width is > 0 ? (int)Math.Round(popSpec.Width.Value) : Math.Max(baseSize.Width, 320);
        int popH = popSpec.Height is > 0 ? (int)Math.Round(popSpec.Height.Value) + 64 : Math.Max(baseSize.Height, 320);
        return new CustomWidgetSizeSpec
        {
            Width = Math.Clamp(popW, 240, 640),
            Height = Math.Clamp(popH, 180, 900)
        };
    }

    private static CustomWidgetNodeSpec? FindPopoverSpecById(CustomWidgetManifest manifest, string popoverId)
    {
        foreach (CustomWidgetNodeSpec p in manifest.Popovers)
        {
            string id = !string.IsNullOrWhiteSpace(p.PopoverId) ? p.PopoverId! : (p.Id ?? string.Empty);
            if (string.Equals(id.Trim(), popoverId, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        return null;
    }

    private void EnsureLivePreviewMountedInStage(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales)
    {
        if (_stageViewbox is null)
        {
            return;
        }

        CustomWidgetManifest? effectiveManifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
        if (effectiveManifest is null)
        {
            return;
        }

        if (locales is { HasAnyLocale: true })
        {
            effectiveManifest = effectiveManifest.WithLocales(locales);
        }

        CustomWidgetSizeSpec size = effectiveManifest.DefaultSize;
        _currentPreviewWidgetWidth = size.Width;

        _shell.PreviewTheme.StopBlueprintSkeletonPulses();

        if (_previewVm is null)
        {
            _previewVm = new CustomWidgetViewModel(
                new WidgetConfig
                {
                    Id = _session.IsEditMode ? _session.TargetWidgetId! : "__studio_preview__",
                    Name = _session.TargetWidgetName ?? _loc.T("CustomWidget.Title"),
                    WidgetKind = WidgetKind.Custom,
                    Width = size.Width,
                    Height = size.Height
                },
                _loc,
                store: null,
                dispatcherQueue: _shell.DispatcherQueue,
                notificationServiceProvider: null,
                isPreviewOnly: true);
            _previewVm.FeedbackRequested += OnPreviewVmFeedbackRequested;
            _previewVm.PreviewNotificationDispatched += OnPreviewVmNotificationDispatched;
            _previewVm.ScriptErrorChanged += OnPreviewVmScriptErrorChanged;
            _previewVm.RuntimeTraceErrorRecorded += OnPreviewVmRuntimeTraceErrorRecorded;
            _previewVm.MetricsRefreshed += OnPreviewVmMetricsRefreshed;
            _previewVm.PropertyChanged += OnPreviewVmPropertyChanged;
            _previewVm.SettingChanged += OnPreviewVmSettingChanged;
            _previewVm.SecretChanged += OnPreviewVmSecretChanged;
        }

        _previewVm.AllowPreviewHostStandIns = !_session.IsSampleDataMutedInPreview;
        _previewVm.IsUntrustedStagePreview = _session.IsExternalImportSession || !string.IsNullOrWhiteSpace(_session.PendingAgentStageId);
        _previewVm.ApplyPreviewBundle(manifestJson, logicJs, locales, preserveRuntimeData: true);
        if (_session.IsEditMode && _session.EditBaselineStorePayload is not null && !_previewVm.HasUserRuntimeMutations)
        {
            _previewVm.ApplyPreviewSeedPayload(_session.EditBaselineStorePayload);
        }
        _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        if (!_session.IsInPlaceGenerating)
        {
            SyncRuntimeScriptErrorFromPreviewVm();
        }
        ReapplySessionSettingOverridesToPreviewVm();

        if (_previewContent is null)
        {
            _previewContent = new CustomWidgetContent(_previewVm)
            {
                IsNodeSelectionEnabled = !_session.IsCapsulePreviewMode && _session.IsInspectorModeEnabled,
                SelectedNodePath = _session.IsCapsulePreviewMode ? null : _session.SelectedTargetNode?.NodePath,
                InterceptPopoversForStudioPreview = true,
                ActivePopoverPreviewId = _activeStudioPopoverPreviewId
            };
            _previewContent.NodePicked += OnPreviewNodePicked;
            _previewContent.StudioPopoverPreviewRequested += OnStudioPopoverPreviewRequested;
            _previewContent.SettingsNavigationRequested += OnPreviewSettingsNavigationRequested;
            _previewContent.Loaded += OnPreviewContentLoadedForHealth;
        }

        if (_widgetFrame is null)
        {
            string resolvedTitle = effectiveManifest.ResolveLocalizedName(_session.EffectivePreviewCultureName);
            _widgetFrame = _shell.PreviewTheme.CreateWidgetFrame(
                _shell.PreviewTheme.ResolveDisplayEmoji(effectiveManifest),
                resolvedTitle,
                _previewContent,
                ResolveEffectiveStageFrameSize(effectiveManifest),
                clipViewport: false,
                out _frameTitleIcon,
                out _frameTitleText,
                existingWidgetId: _session.TargetWidgetId);
            FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(_widgetFrame);
            Border capsulePreviewFrame = _shell.PreviewTheme.BuildInteractiveCapsulePreviewCard();

            if (_stageShadowReceiver is not null)
            {
                var widgetElevationShadow = new ThemeShadow();
                widgetElevationShadow.Receivers.Add(_stageShadowReceiver);
                _widgetFrame.Shadow = widgetElevationShadow;
                _widgetFrame.Translation = new System.Numerics.Vector3(0, 0, 24);
                capsulePreviewFrame.Shadow = widgetElevationShadow;
                capsulePreviewFrame.Translation = new System.Numerics.Vector3(0, 0, 24);
            }

            if (_refreshStageZoomBadgeAction is { } zoomUpdater)
            {
                _widgetFrame.SizeChanged += (_, _) => zoomUpdater();
            }

            Grid viewportHost = BuildStageViewportHostWithResizeHandles(_widgetFrame, capsulePreviewFrame);
            _stageViewbox.Child = viewportHost;
            _shell.PreviewTheme.ApplyStageBackdropVisuals();
            FluentMotionHelper.PlayViewportMorphIn(_widgetFrame, durationMs: 185);
        }

        if (_titleEditButton is not null)
        {
            _titleEditButton.IsEnabled = true;
            _titleEditButton.Flyout ??= _shell.StageDialogs.BuildHeaderQuickEditFlyout();
        }
        if (_pickElementButton is not null)
        {
            _pickElementButton.IsEnabled = !_session.IsCapsulePreviewMode;
        }
        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.IsEnabled = true;
        }
        if (_settingsPreviewButton is not null)
        {
            _settingsPreviewButton.IsEnabled = !_session.IsInPlaceGenerating;
        }
        if (_resetPreviewStateButton is not null)
        {
            _resetPreviewStateButton.IsEnabled = !_session.IsInPlaceGenerating;
        }
        if (_popoverPreviewButton is not null)
        {
            _popoverPreviewButton.IsEnabled = !_session.IsCapsulePreviewMode;
        }
    }

    internal void TryApplyProgressiveManifestPreview(string progressiveManifestJson)
    {
        if (!_session.IsInPlaceGenerating || string.IsNullOrWhiteSpace(progressiveManifestJson) || _stageViewbox is null)
        {
            return;
        }

        CustomWidgetManifest? progressiveManifest = CustomWidgetManifestParser.Parse(progressiveManifestJson).Manifest;
        if (progressiveManifest is null)
        {
            return;
        }

        EnsureLivePreviewMountedInStage(progressiveManifestJson, _session.CurrentLogicJs, _session.CurrentLocales);
        if (_previewVm is null || _widgetFrame is null)
        {
            return;
        }

        _previewVm.ApplyPreviewBundle(progressiveManifestJson, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: true);
        CustomWidgetSizeSpec frameSize = ResolveEffectiveStageFrameSize(progressiveManifest);
        _widgetFrame.Width = Math.Clamp(frameSize.Width, 160, 640);
        _widgetFrame.Height = Math.Clamp(frameSize.Height, 96, 900);
        string title = progressiveManifest.ResolveLocalizedName(_session.EffectivePreviewCultureName);
        if (_frameTitleText is not null)
        {
            _frameTitleText.Text = title;
        }
        if (_nameText is not null)
        {
            _nameText.Text = title;
        }
    }

    internal bool IsBlockingPageDiagnosticOpen =>
        _shell.PageInfoBar is { IsOpen: true, Severity: InfoBarSeverity.Error or InfoBarSeverity.Warning };

    internal void RevertUncommittedProgressivePreview()
    {
        if (!string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            if (_previewVm is not null && _session.Page == StudioPage.Preview)
            {
                ApplyLivePreviewUpdateInPlace();
            }
            return;
        }

        _session.LiveRuntimeScriptError = null;
        _session.LiveRuntimeScriptErrorLine = null;
        if (_previewVm is not null && _session.Page == StudioPage.Preview)
        {
            _session.RefineDraftIdea = _shell.ChatPanel.RefineTextBox?.Text ?? _session.RefineDraftIdea;
            ShowPreviewPage(justLoaded: false);
        }
        else
        {
            RefreshScriptErrorBannerChrome();
        }
    }

    internal void ApplyLivePreviewUpdateInPlace(IReadOnlyList<string>? highlightDiffPills = null)
    {
        string? json = _session.CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            ShowPreviewPage(justLoaded: true);
            return;
        }

        if (_previewVm is null || _previewContent is null || _widgetFrame is null)
        {
            if (_stageViewbox is null)
            {
                ShowPreviewPage(justLoaded: true);
                return;
            }

            EnsureLivePreviewMountedInStage(json, _session.CurrentLogicJs, _session.CurrentLocales);
            if (_previewVm is null || _previewContent is null || _widgetFrame is null)
            {
                ShowPreviewPage(justLoaded: true);
                return;
            }
        }

        _latestUnacknowledgedRuntimeAnomaly = null;
        _previewVm.AllowPreviewHostStandIns = !_session.IsSampleDataMutedInPreview;
        _previewVm.ApplyPreviewBundle(json, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: true);
        _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
        if (!_session.IsInPlaceGenerating)
        {
            SyncRuntimeScriptErrorFromPreviewVm();
        }
        ReapplySessionSettingOverridesToPreviewVm();
        if (!_session.IsEditMode && !string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson) && !_session.IsSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_session.PreviewSampleDataJson, seedOnlyIfEmptyOrSample: true);
        }
        else if (_session.IsSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        CustomWidgetManifest? effectiveManifest = ResolvePreviewManifestWithSidecar(json);
        CustomWidgetSizeSpec nextSize = effectiveManifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        CustomWidgetSizeSpec frameSize = ResolveEffectiveStageFrameSize(effectiveManifest);
        _currentPreviewWidgetWidth = nextSize.Width;

        string nextTitle = effectiveManifest?.ResolveLocalizedName(_session.EffectivePreviewCultureName) ?? _loc.T("CustomWidget.Title");
        string nextEmoji = _shell.PreviewTheme.ResolveDisplayEmoji(effectiveManifest);
        if (_headerEmojiText is not null)
        {
            _headerEmojiText.Text = nextEmoji;
        }

        if (_nameText is not null)
        {
            _nameText.Text = nextTitle;
        }

        if (_frameTitleText is not null)
        {
            _frameTitleText.Text = nextTitle;
        }

        if (_widgetFrame is not null)
        {
            _widgetFrame.Width = Math.Clamp(frameSize.Width, 160, 640);
            _widgetFrame.Height = Math.Clamp(frameSize.Height, 96, 900);
            if (_widgetFrame.Tag is StudioWidgetFrameVisualState frameState)
            {
                frameState.ExistingWidgetId = _session.TargetWidgetId;
                frameState.RawEmojiOrGlyph = nextEmoji;
            }

            _shell.PreviewTheme.ApplyWidgetFrameTheme(
                _widgetFrame,
                _previewContent,
                _frameTitleText,
                _frameEditGlyph,
                _frameDivider,
                _widgetFrame.RequestedTheme,
                effectiveManifest);
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.IsEnabled = true;
            RefreshSizeQuickButtonChrome(nextSize);
        }

        if (_stageColumn is not null && !_stageWidthManuallyAdjusted)
        {
            _stageColumn.Width = new GridLength(
                CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(_lastPreviewWidth, _currentPreviewWidgetWidth));
        }

        _session.LatestHealthReport = _shell.BuildCurrentHealthReport(json);
        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        RefreshStageViewportModeVisuals();
        RefreshSampleDataBadgeChrome();
        RefreshSettingsPreviewBadgeChrome();
        _shell.LocaleCenter.RefreshLocaleCenterChrome();
        _shell.StageInspector.InspectorActiveNodePath = null;
        _shell.ChatPanel.RefreshRevisionChrome();
        _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
        _shell.ChatPanel.RefreshTargetNodePillChrome();
        RefreshScriptErrorBannerChrome();
        _shell.ChatPanel.RefreshSuggestionChips();
        SyncHeaderCommitActionsForCurrentMode();
        _ = _shell.PreviewTheme.PulseStageAsync();

        if (highlightDiffPills is { Count: > 0 } &&
            _session.SelectedTargetNode is null &&
            !_session.IsCapsulePreviewMode &&
            _previewContent is not null &&
            effectiveManifest is not null)
        {
            string? flashNodePath = TryFindFirstChangedNodePath(effectiveManifest, highlightDiffPills);
            if (!string.IsNullOrWhiteSpace(flashNodePath))
            {
                _ = FlashChangedNodeBrieflyAsync(flashNodePath!);
            }
        }
    }

    private static string? TryFindFirstChangedNodePath(CustomWidgetManifest manifest, IReadOnlyList<string> pills)
    {
        foreach (string pill in pills)
        {
            int hashIdx = pill.IndexOf('#');
            if (hashIdx >= 0 && hashIdx + 1 < pill.Length)
            {
                int end = hashIdx + 1;
                while (end < pill.Length && (char.IsLetterOrDigit(pill[end]) || pill[end] is '_' or '-'))
                {
                    end++;
                }
                if (end > hashIdx + 1)
                {
                    string id = pill[(hashIdx + 1)..end];
                    if (StudioManifestInspector.FindMatchingNodePath(
                            manifest,
                            node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) is { } matched)
                    {
                        return matched;
                    }
                }
            }
        }

        return null;
    }

    private async Task FlashChangedNodeBrieflyAsync(string nodePath)
    {
        if (_previewContent is null || _session.SelectedTargetNode is not null)
        {
            return;
        }

        _previewContent.SelectedNodePath = nodePath;
        await Task.Delay(900);
        if (!_shell.IsClosed && _previewContent is not null && _session.SelectedTargetNode is null &&
            string.Equals(_previewContent.SelectedNodePath, nodePath, StringComparison.Ordinal))
        {
            _previewContent.SelectedNodePath = null;
        }
    }

    internal void ShowPreviewPage(bool justLoaded)
    {
        _shell.ComposerBuilder.ClearPageLifecycleCallbacks();
        _shell.ChannelOnboarding.ResetUiDelegates();
        _shell.StartGallery.RefreshAgentStageBanner = null;
        _shell.StartGallery.RefreshTemplateGalleryFromRemote = null;
        string? json = _session.CurrentManifestJson;
        if (json is null && !_session.IsInPlaceGenerating && _session.ChatMessages.Count == 0)
        {
            _shell.StartGallery.ShowStartPage(_session.StartDraftIdea);
            return;
        }

        _shell.StartGallery.DisposeTemplatePreviews();
        DisposePreviewContentOnly();
        _session.Page = StudioPage.Preview;

        bool hasLiveManifest = !string.IsNullOrWhiteSpace(json);
        CustomWidgetManifest? effectiveManifest = hasLiveManifest ? ResolvePreviewManifestWithSidecar(json) : null;
        CustomWidgetSizeSpec size = effectiveManifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        _currentPreviewWidgetWidth = size.Width;

        if (hasLiveManifest)
        {
            if (_previewVm is null)
            {
                _previewVm = new CustomWidgetViewModel(
                    new WidgetConfig
                    {
                        Id = _session.IsEditMode ? _session.TargetWidgetId! : "__studio_preview__",
                        Name = _session.TargetWidgetName ?? _loc.T("CustomWidget.Title"),
                        WidgetKind = WidgetKind.Custom,
                        Width = size.Width,
                        Height = size.Height
                    },
                    _loc,
                    store: null,
                    dispatcherQueue: _shell.DispatcherQueue,
                    notificationServiceProvider: null,
                    isPreviewOnly: true);
                _previewVm.FeedbackRequested += OnPreviewVmFeedbackRequested;
                _previewVm.PreviewNotificationDispatched += OnPreviewVmNotificationDispatched;
                _previewVm.ScriptErrorChanged += OnPreviewVmScriptErrorChanged;
                _previewVm.RuntimeTraceErrorRecorded += OnPreviewVmRuntimeTraceErrorRecorded;
                _previewVm.MetricsRefreshed += OnPreviewVmMetricsRefreshed;
                _previewVm.PropertyChanged += OnPreviewVmPropertyChanged;
                _previewVm.SettingChanged += OnPreviewVmSettingChanged;
                _previewVm.SecretChanged += OnPreviewVmSecretChanged;
            }

            _previewVm.AllowPreviewHostStandIns = !_session.IsSampleDataMutedInPreview;
            _previewVm.IsUntrustedStagePreview = _session.IsExternalImportSession || !string.IsNullOrWhiteSpace(_session.PendingAgentStageId);
            _previewVm.ApplyPreviewBundle(json!, _session.CurrentLogicJs, _session.CurrentLocales, preserveRuntimeData: true);
            if (_session.IsEditMode && _session.EditBaselineStorePayload is not null && !_previewVm.HasUserRuntimeMutations)
            {
                _previewVm.ApplyPreviewSeedPayload(_session.EditBaselineStorePayload);
            }
            _previewVm.SetPreviewCultureOverride(_session.ActivePreviewCultureOverride);
            SyncRuntimeScriptErrorFromPreviewVm();
            ReapplySessionSettingOverridesToPreviewVm();
            if (!_session.IsEditMode && !string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson) && !_session.IsSampleDataMutedInPreview)
            {
                _previewVm.ApplyPreviewSampleDataJson(_session.PreviewSampleDataJson, seedOnlyIfEmptyOrSample: true);
            }
            else if (_session.IsSampleDataMutedInPreview)
            {
                _previewVm.ClearPreviewSampleData();
            }

            _previewContent = new CustomWidgetContent(_previewVm)
            {
                IsNodeSelectionEnabled = !_session.IsCapsulePreviewMode && _session.IsInspectorModeEnabled,
                SelectedNodePath = _session.IsCapsulePreviewMode ? null : _session.SelectedTargetNode?.NodePath,
                InterceptPopoversForStudioPreview = true,
                ActivePopoverPreviewId = _activeStudioPopoverPreviewId
            };
            _previewContent.NodePicked += OnPreviewNodePicked;
            _previewContent.StudioPopoverPreviewRequested += OnStudioPopoverPreviewRequested;
            _previewContent.SettingsNavigationRequested += OnPreviewSettingsNavigationRequested;
            _previewContent.Loaded += OnPreviewContentLoadedForHealth;
        }

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(24, 12, 24, 18),
            RowSpacing = 12
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _frameTitleIcon = null;
        _frameTitleText = null;
        _frameDivider = null;
        _frameEditGlyph = null;
        _shell.PreviewTheme.ResetPreviewFrameElements();
        _pickElementButton = null;
        _pickElementIcon = null;
        _pickElementLabel = null;
        _settingsPreviewButton = null;
        _shell.LocaleCenter.PreviewCultureOverridePillButton = null;
        _shell.LocaleCenter.I18nHeaderButton = null;
        _shell.LocaleCenter.I18nHeaderIcon = null;
        _shell.LocaleCenter.I18nHeaderLabel = null;
        _shell.LocaleCenter.LocaleCenterHostBorder = null;
        _shell.LocaleCenter.LocaleCenterSummaryText = null;
        _shell.LocaleCenter.LocaleCenterHintText = null;
        _shell.LocaleCenter.LocaleCenterOverallProgressBar = null;
        _shell.LocaleCenter.LocaleCenterSyncAllButton = null;
        _shell.LocaleCenter.LocaleCenterSync2Button = null;
        _shell.LocaleCenter.LocaleCenterRetryFailedButton = null;
        _shell.LocaleCenter.LocaleCenterStopButton = null;
        _shell.LocaleCenter.LocaleCenterCloseButton = null;
        _shell.LocaleCenter.LocaleCenterMatrixScroll = null;
        _shell.LocaleCenter.LocaleCenterCardRefs.Clear();
        _stageViewportModeSegmented = null;
        _stageBackdropSegmented = null;
        _stageWidgetThemeSegmented = null;
        _stageChromeModeSegmented = null;
        _stageAppearanceButton = null;
        _resetPreviewStateButton = null;
        _currentStageZoomPercent = null;
        _refreshStageZoomBadgeAction = null;
        _stageToastTimer?.Stop();
        _stageToastBorder = null;
        _stageToastTitleText = null;
        _stageToastBodyText = null;
        _runtimeAnomalyBannerBorder = null;
        _runtimeAnomalyBannerText = null;
        _runtimeAnomalyDiagnoseButton = null;

        root.Children.Add(BuildWorkspaceHeaderBar(hasLiveManifest, effectiveManifest));

        // Row 1 — Optional Draft Recovery + Incoming External Agent Stage Banners
        var previewTopBanners = new StackPanel { Spacing = 8 };
        if (_shell.ComposerBuilder.CreateDraftRecoveryBannerIfNeeded() is { } previewRecoveryBanner)
        {
            previewTopBanners.Children.Add(previewRecoveryBanner);
        }
        previewTopBanners.Children.Add(_shell.ComposerBuilder.CreatePreviewIncomingAgentStageBanner(
            stageId => _dismissedIncomingStageId = stageId,
            out _showIncomingStageBannerAction,
            out _hideIncomingStageBannerAction));
        Grid.SetRow(previewTopBanners, 1);
        root.Children.Add(previewTopBanners);

        // Row 2 — Resizable Split Workspace: Live Preview Stage (Left) | GridSplitter | Co-Creation Workspace (Right)
        var body = new Grid();
        double initialStageWidth = CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(_lastPreviewWidth, _currentPreviewWidgetWidth);
        _stageColumn = new ColumnDefinition
        {
            MinWidth = 300,
            MaxWidth = 840,
            Width = new GridLength(initialStageWidth)
        };
        body.ColumnDefinitions.Add(_stageColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        Grid.SetRow(body, 2);

        root.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            _lastPreviewWidth = w;
            root.Padding = w < 880
                ? new Thickness(16, 10, 16, 14)
                : new Thickness(24, 12, 24, 18);
            if (_stageColumn is not null)
            {
                double maxStage = Math.Clamp(Math.Min(w - 400, w * 0.48), 300, 840);
                _stageColumn.MaxWidth = maxStage;
                if (!_stageWidthManuallyAdjusted)
                {
                    _stageColumn.Width = new GridLength(
                        Math.Min(maxStage, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(w, _currentPreviewWidgetWidth)));
                }
                else if (_stageColumn.Width.Value > maxStage)
                {
                    _stageColumn.Width = new GridLength(maxStage);
                }
            }
        };

        _stageBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerOnMicaBaseAltFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 6, 0),
            AllowDrop = true
        };
        _stageBorder.DragOver += (_, e) =>
        {
            if (e.Handled || _previewVm?.AcceptsFileDrops != true)
            {
                return;
            }

            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
            }
        };
        _stageBorder.Drop += (_, e) =>
        {
            if (e.Handled || _previewVm?.AcceptsFileDrops != true)
            {
                return;
            }

            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                ShowStageToastBanner(
                    _loc.T("CustomWidget.Workshop.Stage.TestFileDrop"),
                    _loc.T("CustomWidget.Workshop.Stage.DropShieldMissHint"));
            }
        };

        var stageGrid = new Grid();
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stageWallpaperLayer = new Border
        {
            CornerRadius = new CornerRadius(7),
            Opacity = 0.96,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperLayer, 4);
        stageGrid.Children.Add(_stageWallpaperLayer);

        _stageWallpaperScrim = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x0D, 0x10, 0x18)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperScrim, 4);
        stageGrid.Children.Add(_stageWallpaperScrim);

        var stageShadowReceiver = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsHitTestVisible = false
        };
        _stageShadowReceiver = stageShadowReceiver;
        Grid.SetRowSpan(stageShadowReceiver, 4);
        stageGrid.Children.Add(stageShadowReceiver);

        _stageGeneratingProgressBar = new ProgressBar
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 10, 0),
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed
        };
        stageGrid.Children.Add(_stageGeneratingProgressBar);

        Grid stageTopBar = BuildStageTopToolbar(size, hasLiveManifest);
        stageGrid.Children.Add(stageTopBar);

        _shell.StageInspector.StageInspectorBarBorder = new Border
        {
            Margin = new Thickness(10, 2, 10, 4),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerOnMicaBaseAltFillColorDefaultBrush"),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_shell.StageInspector.StageInspectorBarBorder, 1);
        stageGrid.Children.Add(_shell.StageInspector.StageInspectorBarBorder);

        string resolvedManifestTitle = hasLiveManifest
            ? (effectiveManifest?.ResolveLocalizedName(_session.EffectivePreviewCultureName) ?? _loc.T("CustomWidget.Title"))
            : (_session.IsInPlaceGenerating
                ? _loc.T("CustomWidget.Workshop.Stage.BlueprintPreparing")
                : _loc.T("CustomWidget.Title"));

        UIElement stageCenterChild;
        if (hasLiveManifest && _previewContent is not null)
        {
            _widgetFrame = _shell.PreviewTheme.CreateWidgetFrame(
                _shell.PreviewTheme.ResolveDisplayEmoji(effectiveManifest),
                resolvedManifestTitle,
                _previewContent,
                ResolveEffectiveStageFrameSize(effectiveManifest),
                clipViewport: false,
                out _frameTitleIcon,
                out _frameTitleText,
                existingWidgetId: _session.TargetWidgetId);
            FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(_widgetFrame);
            Border capsulePreviewFrame = _shell.PreviewTheme.BuildInteractiveCapsulePreviewCard();

            var widgetElevationShadow = new ThemeShadow();
            widgetElevationShadow.Receivers.Add(stageShadowReceiver);
            _widgetFrame.Shadow = widgetElevationShadow;
            _widgetFrame.Translation = new System.Numerics.Vector3(0, 0, 24);
            capsulePreviewFrame.Shadow = widgetElevationShadow;
            capsulePreviewFrame.Translation = new System.Numerics.Vector3(0, 0, 24);

            stageCenterChild = BuildStageViewportHostWithResizeHandles(_widgetFrame, capsulePreviewFrame);
        }
        else
        {
            _widgetFrame = null;
            _resizeHandlesOverlay = null;
            _resizeDimensionPill = null;
            _resizeDimensionText = null;
            stageCenterChild = _shell.PreviewTheme.BuildBlueprintStagePlaceholder();
        }

        var stageViewbox = new Viewbox
        {
            Margin = new Thickness(18, 6, 18, 8),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stageCenterChild
        };
        _stageViewbox = stageViewbox;
        Grid.SetRow(stageViewbox, 2);
        stageGrid.Children.Add(stageViewbox);

        _stageGeneratingStatusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        _stageGeneratingProgressRing = new ProgressRing
        {
            IsActive = _session.IsInPlaceGenerating,
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillStack.Children.Add(_stageGeneratingProgressRing);
        pillStack.Children.Add(_stageGeneratingStatusText);
        _stageGeneratingStatusPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SolidBackgroundFillColorBaseBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = pillStack
        };
        Grid.SetRow(_stageGeneratingStatusPill, 2);
        stageGrid.Children.Add(_stageGeneratingStatusPill);

        _stageToastTitleText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        _stageToastBodyText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3
        };
        var toastTextStack = new StackPanel { Spacing = 2 };
        toastTextStack.Children.Add(_stageToastTitleText);
        toastTextStack.Children.Add(_stageToastBodyText);
        var toastRow = new Grid { ColumnSpacing = 8 };
        toastRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toastRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var toastIcon = new FontIcon
        {
            Glyph = "\uEA8F",
            FontSize = 14,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
        toastRow.Children.Add(toastIcon);
        Grid.SetColumn(toastTextStack, 1);
        toastRow.Children.Add(toastTextStack);
        _stageToastBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(14),
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = toastRow
        };
        Canvas.SetZIndex(_stageToastBorder, 24);
        Grid.SetRow(_stageToastBorder, 2);
        stageGrid.Children.Add(_stageToastBorder);

        _runtimeAnomalyBannerText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _runtimeAnomalyDiagnoseButton = new Button
        {
            Height = 26,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.Stage.RuntimeAnomalyDiagnose"))
        };
        _runtimeAnomalyDiagnoseButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "RuntimeAnomalyDiagnose",
            TriggerRuntimeAnomalyDiagnoseAsync);
        var dismissAnomalyBtn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }
        };
        ToolTipService.SetToolTip(dismissAnomalyBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        AutomationProperties.SetName(dismissAnomalyBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        dismissAnomalyBtn.Click += (_, _) =>
        {
            _latestUnacknowledgedRuntimeAnomaly = null;
            RefreshRuntimeAnomalyBannerChrome();
        };
        var anomalyGrid = new Grid { ColumnSpacing = 8 };
        anomalyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        anomalyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        anomalyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        anomalyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        anomalyGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 13,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCautionBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_runtimeAnomalyBannerText, 1);
        anomalyGrid.Children.Add(_runtimeAnomalyBannerText);
        Grid.SetColumn(_runtimeAnomalyDiagnoseButton, 2);
        anomalyGrid.Children.Add(_runtimeAnomalyDiagnoseButton);
        Grid.SetColumn(dismissAnomalyBtn, 3);
        anomalyGrid.Children.Add(dismissAnomalyBtn);
        _runtimeAnomalyBannerBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            Margin = new Thickness(14, 0, 14, 10),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCautionBackgroundBrush"),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Child = anomalyGrid
        };
        Canvas.SetZIndex(_runtimeAnomalyBannerBorder, 22);
        Grid.SetRow(_runtimeAnomalyBannerBorder, 2);
        stageGrid.Children.Add(_runtimeAnomalyBannerBorder);

        ToolkitControls.Segmented viewportModeSegmented = BuildStageViewportModeSegmented(hasLiveManifest);
        _popoverPreviewButton = new DropDownButton
        {
            Height = 28,
            Padding = new Thickness(9, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            IsEnabled = hasLiveManifest && !_session.IsCapsulePreviewMode,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };

        void UpdateStageZoomBadge()
        {
            FrameworkElement? activeViewportEl = _session.IsCapsulePreviewMode
                ? _shell.PreviewTheme.CapsulePreviewFrame
                : _widgetFrame;
            int? nextPct = null;
            if (activeViewportEl is not null &&
                activeViewportEl.ActualWidth > 0 &&
                activeViewportEl.ActualHeight > 0 &&
                stageViewbox.ActualWidth > 0 &&
                stageViewbox.ActualHeight > 0)
            {
                double scaleX = stageViewbox.ActualWidth / activeViewportEl.ActualWidth;
                double scaleY = stageViewbox.ActualHeight / activeViewportEl.ActualHeight;
                double scale = Math.Min(1.0, Math.Min(scaleX, scaleY));
                if (scale < 0.985)
                {
                    nextPct = Math.Clamp((int)Math.Round(scale * 100), 10, 99);
                }
            }

            if (nextPct != _currentStageZoomPercent)
            {
                _currentStageZoomPercent = nextPct;
                RefreshSizeQuickButtonChrome();
            }
        }

        _refreshStageZoomBadgeAction = UpdateStageZoomBadge;
        stageViewbox.SizeChanged += (_, _) => UpdateStageZoomBadge();
        if (_widgetFrame is not null)
        {
            _widgetFrame.SizeChanged += (_, _) => UpdateStageZoomBadge();
        }

        var stageBottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 4, 10, 10)
        };
        stageBottomBar.Children.Add(viewportModeSegmented);
        stageBottomBar.Children.Add(_popoverPreviewButton);
        Grid.SetRow(stageBottomBar, 3);
        stageGrid.Children.Add(stageBottomBar);

        _stageBorder.Child = stageGrid;
        body.Children.Add(_stageBorder);

        var stageSplitter = new ToolkitControls.GridSplitter
        {
            ResizeBehavior = ToolkitControls.GridSplitter.GridResizeBehavior.BasedOnAlignment,
            ResizeDirection = ToolkitControls.GridSplitter.GridResizeDirection.Columns
        };
        if (Application.Current.Resources.TryGetValue("WidgetMasterDetailSplitterStyle", out object? splitterRes) &&
            splitterRes is Style splitterStyle)
        {
            stageSplitter.Style = splitterStyle;
        }
        stageSplitter.PointerPressed += (_, _) => _stageWidthManuallyAdjusted = true;
        Grid.SetColumn(stageSplitter, 1);
        body.Children.Add(stageSplitter);

        FrameworkElement refinePanel = _shell.ChatPanel.BuildRefinePanel();
        Grid.SetColumn(refinePanel, 2);
        body.Children.Add(refinePanel);
        root.Children.Add(body);

        _shell.SetPage(new Grid { Children = { root } });
        if (_nameText is not null)
        {
            _nameText.Text = resolvedManifestTitle;
        }
        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        RefreshStageViewportModeVisuals();
        _shell.PreviewTheme.ApplyStageBackdropVisuals();
        RefreshSampleDataBadgeChrome();
        RefreshSettingsPreviewBadgeChrome();
        _shell.ChatPanel.RefreshRevisionChrome();
        _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
        _shell.ChatPanel.RefreshTargetNodePillChrome();
        RefreshScriptErrorBannerChrome();
        _shell.ChatPanel.RefreshSuggestionChips();

        if (_session.HasUnsavedWork)
        {
            IReadOnlyList<AgentStageSummary> pendingStages = _hostBridge.GetPendingAgentStages();
            if (pendingStages.Count > 0 &&
                !string.Equals(pendingStages[0].StageId, _session.PendingAgentStageId, StringComparison.Ordinal))
            {
                ShowIncomingAgentStageBanner(pendingStages[0], pendingStages.Count);
            }
        }

        if (_session.PendingRecoveryDraft is null && _session.WasReconciledExternalOnOpen)
        {
            _session.WasReconciledExternalOnOpen = false;
            _shell.ShowPageMessage(
                InfoBarSeverity.Informational,
                string.Empty,
                _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncNotice"),
                null);
        }

        if (justLoaded && hasLiveManifest)
        {
            _ = _shell.PreviewTheme.PulseStageAsync();
        }
    }

    internal void RefreshStageViewportModeVisuals(bool animateTransition = false)
    {
        Border? capsulePreviewFrame = _shell.PreviewTheme.CapsulePreviewFrame;
        bool hasLiveManifest = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
        bool showCapsule = hasLiveManifest && _session.IsCapsulePreviewMode && capsulePreviewFrame is not null;

        if (_widgetFrame is not null)
        {
            _widgetFrame.Visibility = showCapsule ? Visibility.Collapsed : Visibility.Visible;
        }

        if (capsulePreviewFrame is not null)
        {
            capsulePreviewFrame.Visibility = showCapsule ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.Visibility = showCapsule ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_resizeHandlesOverlay is not null)
        {
            _resizeHandlesOverlay.Visibility = !showCapsule && hasLiveManifest && string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (showCapsule)
        {
            _session.IsInspectorModeEnabled = false;
            if (_previewContent is not null)
            {
                _previewContent.IsNodeSelectionEnabled = false;
                _previewContent.OnCompactStateChanged(true);
            }
        }
        else if (_previewContent is not null)
        {
            _previewContent.OnCompactStateChanged(false);
            _previewContent.IsNodeSelectionEnabled = _session.IsInspectorModeEnabled;
            _previewContent.SelectedNodePath = _session.SelectedTargetNode?.NodePath;
        }

        if (animateTransition)
        {
            UIElement? morphTarget = showCapsule ? capsulePreviewFrame : _widgetFrame;
            FluentMotionHelper.PlayViewportMorphIn(morphTarget, durationMs: 160);
        }

        if (_shell.ChatPanel.RefineTextBox is not null)
        {
            _shell.ChatPanel.RefineTextBox.PlaceholderText = _loc.T(showCapsule
                ? "CustomWidget.Workshop.Refine.CapsulePlaceholder"
                : "CustomWidget.Workshop.Refine.Placeholder");
        }

        _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
        RefreshPopoverAndDropToolbarChrome();
        RefreshStageToolbarChrome();
        _shell.ChatPanel.RefreshTargetNodePillChrome();
        _shell.ChatPanel.RefreshSuggestionChips();
        _refreshStageZoomBadgeAction?.Invoke();
    }

    internal void SyncRuntimeScriptErrorFromPreviewVm()
    {
        CustomWidgetScriptRuntimeError? err = _previewVm?.LastScriptError;
        _session.LiveRuntimeScriptError = err is null
            ? null
            : (string.IsNullOrWhiteSpace(err.FunctionName)
                ? err.ErrorMessage
                : $"[{err.FunctionName}] {err.ErrorMessage}");
        _session.LiveRuntimeScriptErrorLine = err is { ErrorLine: > 0 } ? err.ErrorLine : null;
    }

    private void OnPreviewVmScriptErrorChanged(CustomWidgetScriptRuntimeError? error)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed || _session.IsInPlaceGenerating)
            {
                return;
            }

            _session.LiveRuntimeScriptError = error is null
                ? null
                : (string.IsNullOrWhiteSpace(error.FunctionName)
                    ? error.ErrorMessage
                    : $"[{error.FunctionName}] {error.ErrorMessage}");
            _session.LiveRuntimeScriptErrorLine = error is { ErrorLine: > 0 } ? error.ErrorLine : null;
            RefreshScriptErrorBannerChrome();
            _shell.ChatPanel.RefreshSuggestionChips();
        });
    }

    private void OnPreviewVmMetricsRefreshed(object? sender, EventArgs e)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_shell.IsClosed && _session.Page == StudioPage.Preview)
            {
                _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
            }
        });
    }

    private void OnPreviewVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_shell.IsClosed && _session.Page == StudioPage.Preview)
            {
                _shell.PreviewTheme.RefreshCapsulePreviewCardContent();
            }
        });
    }

    internal void RefreshScriptErrorBannerChrome()
    {
        bool canInteractWithLivePreview =
            !string.IsNullOrWhiteSpace(_session.CurrentManifestJson) && !_session.IsInPlaceGenerating;
        if (_resetPreviewStateButton is not null)
        {
            _resetPreviewStateButton.IsEnabled = canInteractWithLivePreview;
        }
        if (_settingsPreviewButton is not null)
        {
            _settingsPreviewButton.IsEnabled = canInteractWithLivePreview;
        }

        if (_scriptErrorBannerBorder is null || _scriptErrorBannerText is null)
        {
            return;
        }

        if (_session.IsInPlaceGenerating ||
            IsBlockingPageDiagnosticOpen ||
            string.IsNullOrWhiteSpace(_session.LiveRuntimeScriptError))
        {
            _scriptErrorBannerBorder.Visibility = Visibility.Collapsed;
            RefreshRuntimeAnomalyBannerChrome();
            return;
        }

        string linePrefix = _session.LiveRuntimeScriptErrorLine is { } ln and > 0
            ? $"logic.js:{ln}: "
            : "logic.js: ";
        string fullErr = linePrefix + _session.LiveRuntimeScriptError;
        _scriptErrorBannerText.Text = fullErr;
        ToolTipService.SetToolTip(_scriptErrorBannerText, fullErr);
        if (_scriptErrorFixButton is not null)
        {
            _scriptErrorFixButton.IsEnabled = !_session.IsInPlaceGenerating;
        }

        _scriptErrorBannerBorder.Visibility = Visibility.Visible;
        RefreshRuntimeAnomalyBannerChrome();
    }

    private void OnPreviewVmRuntimeTraceErrorRecorded(CustomWidgetRuntimeTraceEntry entry)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed || _session.IsInPlaceGenerating || _session.Page != StudioPage.Preview)
            {
                return;
            }

            _latestUnacknowledgedRuntimeAnomaly = entry;
            RefreshRuntimeAnomalyBannerChrome();
        });
    }

    internal void RefreshRuntimeAnomalyBannerChrome()
    {
        if (_runtimeAnomalyBannerBorder is null || _runtimeAnomalyBannerText is null)
        {
            return;
        }

        CustomWidgetRuntimeTraceEntry? entry = _latestUnacknowledgedRuntimeAnomaly;
        bool scriptBannerCoversSameError =
            _scriptErrorBannerBorder is { Visibility: Visibility.Visible } &&
            entry is not null &&
            string.Equals(entry.ActionType, "script", StringComparison.OrdinalIgnoreCase);

        if (_session.IsInPlaceGenerating ||
            IsBlockingPageDiagnosticOpen ||
            entry is null ||
            scriptBannerCoversSameError)
        {
            _runtimeAnomalyBannerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        string actionLabel = !string.IsNullOrWhiteSpace(entry.ActionType)
            ? entry.ActionType
            : (!string.IsNullOrWhiteSpace(entry.Trigger) ? entry.Trigger : "runtime");
        string detailSummary = !string.IsNullOrWhiteSpace(entry.Detail)
            ? entry.Detail
            : (!string.IsNullOrWhiteSpace(entry.TargetSummary) ? entry.TargetSummary : entry.Outcome);
        string compactLine = _loc.Format("CustomWidget.Workshop.Stage.RuntimeAnomalyFormat", actionLabel, detailSummary);
        _runtimeAnomalyBannerText.Text = compactLine;
        string fullTooltip = string.IsNullOrWhiteSpace(entry.TargetSummary)
            ? compactLine
            : $"{compactLine}\n{entry.TargetSummary}";
        ToolTipService.SetToolTip(_runtimeAnomalyBannerText, fullTooltip);
        if (_runtimeAnomalyDiagnoseButton is not null)
        {
            _runtimeAnomalyDiagnoseButton.IsEnabled = !_session.IsInPlaceGenerating;
        }

        _runtimeAnomalyBannerBorder.Visibility = Visibility.Visible;
    }

    private async Task TriggerRuntimeAnomalyDiagnoseAsync()
    {
        CustomWidgetRuntimeTraceEntry? entry = _latestUnacknowledgedRuntimeAnomaly;
        if (_session.IsInPlaceGenerating || entry is null)
        {
            return;
        }

        StudioAiChannelKind channel = _session.ResolveEffectiveStudioChannel(out _);
        if (channel != StudioAiChannelKind.AgentMcp && !DeskBoxAiService.Shared.HasAnyConfiguredProvider())
        {
            await _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, _shell.ChannelOnboarding.NotifyChannelSelectorsRefresh);
            if (!DeskBoxAiService.Shared.HasAnyConfiguredProvider())
            {
                return;
            }
        }

        _latestUnacknowledgedRuntimeAnomaly = null;
        RefreshRuntimeAnomalyBannerChrome();

        string actionLabel = !string.IsNullOrWhiteSpace(entry.ActionType) ? entry.ActionType : "runtime";
        string targetPart = string.IsNullOrWhiteSpace(entry.TargetSummary) ? string.Empty : $" ({entry.TargetSummary})";
        string detailPart = string.IsNullOrWhiteSpace(entry.Detail) ? entry.Outcome : entry.Detail;
        string prompt =
            $"Live preview hit a runtime {entry.Outcome} during `{actionLabel}`{targetPart}: {detailPart}. " +
            "Inspect the attached `[Recent Runtime Diagnostics]` trace and current workspace, identify the root cause, and fix the widget or script so this action succeeds cleanly (or explain clearly if an external endpoint/permission requires user setup).";
        string displayPrompt = _loc.Format("CustomWidget.Workshop.Stage.RuntimeAnomalyDiagnoseDisplayFormat", actionLabel);
        await _shell.Generation.BeginGenerationAsync(prompt, displayPrompt: displayPrompt);
    }

    internal async Task TriggerRuntimeScriptAutoFixAsync()
    {
        if (_session.IsInPlaceGenerating || string.IsNullOrWhiteSpace(_session.LiveRuntimeScriptError))
        {
            return;
        }

        string locRef = _session.LiveRuntimeScriptErrorLine is { } ln and > 0
            ? $"logic.js:{ln}"
            : "logic.js";
        string prompt = _shell.Generation.BuildCombinedHealthAndDiagnosticsRepairPrompt(primaryHealthIssue: null);
        string displayPrompt = _loc.Format("CustomWidget.Workshop.ScriptError.AutoFixDisplayFormat", locRef);
        await _shell.Generation.BeginGenerationAsync(prompt, displayPrompt: displayPrompt);
    }

    internal void OnPreviewNodePicked(CustomWidgetPickedNode picked)
    {
        _session.SelectedTargetNode = picked;
        _session.IsInspectorModeEnabled = true;
        if (_previewContent is not null)
        {
            _previewContent.SelectedNodePath = picked.NodePath;
            _previewContent.IsNodeSelectionEnabled = true;
        }

        RefreshStageToolbarChrome();
        _shell.ChatPanel.RefreshTargetNodePillChrome();
        _shell.ChatPanel.RefineTextBox?.Focus(FocusState.Programmatic);
    }

    private void OnPreviewContentLoadedForHealth(object sender, RoutedEventArgs e)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed || _session.Page != StudioPage.Preview)
            {
                return;
            }

            _session.LatestHealthReport = _shell.BuildCurrentHealthReport(_session.CurrentManifestJson);
            _shell.ChatPanel.RefreshSuggestionChips();
        });
    }

    private void DisposePreviewContentOnly()
    {
        if (_previewContent is null)
        {
            return;
        }

        try
        {
            _previewContent.NodePicked -= OnPreviewNodePicked;
            _previewContent.StudioPopoverPreviewRequested -= OnStudioPopoverPreviewRequested;
            _previewContent.SettingsNavigationRequested -= OnPreviewSettingsNavigationRequested;
            _previewContent.Loaded -= OnPreviewContentLoadedForHealth;
            if (_previewContent.Parent is Panel parent)
            {
                parent.Children.Remove(_previewContent);
            }
            else if (_previewContent.Parent is ContentControl cc)
            {
                cc.Content = null;
            }

            _previewContent.Dispose();
        }
        catch
        {
        }

        _previewContent = null;
    }

    private void OnPreviewSettingsNavigationRequested(string sectionTag)
    {
        if (string.Equals(sectionTag, "AiSettings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionTag, "ai", StringComparison.OrdinalIgnoreCase))
        {
            _ = _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(
                null,
                () => _previewContent?.RequestCoalescedRebuild());
        }
    }

    private void OnPreviewVmNotificationDispatched(object? sender, (string Title, string Message) notification)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed || _session.Page != StudioPage.Preview)
            {
                return;
            }

            ShowStageToastBanner(notification.Title, notification.Message);
        });
    }

    private void ShowStageToastBanner(string title, string message)
    {
        if (_stageToastBorder is null || _stageToastTitleText is null || _stageToastBodyText is null)
        {
            return;
        }

        _stageToastTitleText.Text = string.IsNullOrWhiteSpace(title)
            ? (_nameText?.Text ?? _loc.T("CustomWidget.Title"))
            : title;
        _stageToastBodyText.Text = message ?? string.Empty;
        _stageToastBodyText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _stageToastBorder.Visibility = Visibility.Visible;

        if (_stageToastTimer is null)
        {
            _stageToastTimer = _shell.DispatcherQueue.CreateTimer();
            _stageToastTimer.IsRepeating = false;
            _stageToastTimer.Interval = TimeSpan.FromMilliseconds(3200);
            _stageToastTimer.Tick += (_, _) =>
            {
                if (_stageToastBorder is not null)
                {
                    _stageToastBorder.Visibility = Visibility.Collapsed;
                }
            };
        }

        _stageToastTimer.Stop();
        _stageToastTimer.Start();
    }

    private void OnPreviewVmFeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed || _session.Page != StudioPage.Preview)
            {
                return;
            }

            InfoBarSeverity severity = e.Request.Severity switch
            {
                WidgetFeedbackSeverity.Error => InfoBarSeverity.Error,
                WidgetFeedbackSeverity.Warning => InfoBarSeverity.Warning,
                WidgetFeedbackSeverity.Success => InfoBarSeverity.Success,
                _ => InfoBarSeverity.Informational
            };

            ButtonBase? actionBtn = null;
            if (severity is InfoBarSeverity.Warning or InfoBarSeverity.Error)
            {
                StudioAiChannelKind channel = _session.ResolveEffectiveStudioChannel(out _);
                if (channel == StudioAiChannelKind.AgentMcp || DeskBoxAiService.Shared.HasAnyConfiguredProvider())
                {
                    string capturedErrorMsg = e.Request.Message;
                    var fixBtn = new Button
                    {
                        Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.ScriptError.FixWithAi"))
                    };
                    fixBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                        _shell,
                        "ScriptToastFixWithAi",
                        () =>
                        {
                            string prompt = _loc.Format("CustomWidget.Workshop.ScriptError.AutoFixPromptFormat", capturedErrorMsg);
                            return _shell.Generation.BeginGenerationAsync(prompt, displayPrompt: _loc.T("CustomWidget.Workshop.ScriptError.FixWithAi"));
                        });
                    actionBtn = fixBtn;
                }
                else
                {
                    var setupBtn = new Button { Content = _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi") };
                    setupBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                        _shell,
                        "ScriptToastQuickSetupApi",
                        () => _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, null));
                    actionBtn = setupBtn;
                }
            }

            _shell.ShowPageMessage(severity, string.Empty, e.Request.Message, actionBtn);
        });
    }

    internal void DisposePreview()
    {
        DisposePreviewContentOnly();
        try
        {
            if (_previewVm is not null)
            {
                _previewVm.FeedbackRequested -= OnPreviewVmFeedbackRequested;
                _previewVm.PreviewNotificationDispatched -= OnPreviewVmNotificationDispatched;
                _previewVm.ScriptErrorChanged -= OnPreviewVmScriptErrorChanged;
                _previewVm.RuntimeTraceErrorRecorded -= OnPreviewVmRuntimeTraceErrorRecorded;
                _previewVm.MetricsRefreshed -= OnPreviewVmMetricsRefreshed;
                _previewVm.PropertyChanged -= OnPreviewVmPropertyChanged;
                _previewVm.SettingChanged -= OnPreviewVmSettingChanged;
                _previewVm.SecretChanged -= OnPreviewVmSecretChanged;
                _previewVm.Dispose();
            }
        }
        catch
        {
        }

        _previewVm = null;
        _shell.StartGallery.RefreshTemplateGalleryFromRemote = null;
        _stageColumn = null;
        _stageWidthManuallyAdjusted = false;
        _stageBorder = null;
        _widgetFrame = null;
        _shell.PreviewTheme.ResetPreviewFrameElements();
        if (_stageToastTimer is not null)
        {
            _stageToastTimer.Stop();
            _stageToastTimer = null;
        }
        _stageToastBorder = null;
        _stageToastTitleText = null;
        _stageToastBodyText = null;
        _scriptErrorBannerBorder = null;
        _scriptErrorBannerText = null;
        _scriptErrorFixButton = null;
        _runtimeAnomalyBannerBorder = null;
        _runtimeAnomalyBannerText = null;
        _runtimeAnomalyDiagnoseButton = null;
        _latestUnacknowledgedRuntimeAnomaly = null;
        _resetPreviewStateButton = null;
        _sampleDataToggleButton = null;
        _settingsPreviewButton = null;
        _popoverPreviewButton = null;
        _fileDropTestButton = null;
        _stageViewbox = null;
        _stageShadowReceiver = null;
        _titleEditButton = null;
        _resizeHandlesOverlay = null;
        _resizeDimensionPill = null;
        _resizeDimensionText = null;
        _showIncomingStageBannerAction = null;
        _hideIncomingStageBannerAction = null;
        _shell.StageInspector.StageInspectorBarBorder = null;
        _shell.StageInspector.InspectorActiveNodePath = null;
        _shell.StageInspector.InspectorActivePropKey = null;
        _shell.StageInspector.InspectorTextBox = null;
        _shell.StageInspector.InspectorStyleCombo = null;
        _shell.StageInspector.InspectorToneCombo = null;
        _shell.StageInspector.InspectorSpacingBox = null;
        _headerEmojiText = null;
        _nameText = null;
        _frameTitleIcon = null;
        _frameTitleText = null;
        _shell.ChatPanel.UndoButton = null;
        _shell.ChatPanel.RedoButton = null;
        _sizeQuickButton = null;
        _shell.ChatPanel.RevisionsMenuButton = null;
        _shell.ChatPanel.BriefHeaderButton = null;
        _shell.LocaleCenter.I18nHeaderButton = null;
        _shell.LocaleCenter.I18nHeaderIcon = null;
        _shell.LocaleCenter.I18nHeaderLabel = null;
        _shell.LocaleCenter.PreviewCultureOverridePillButton = null;
        _shell.LocaleCenter.LocaleCenterHostBorder = null;
        _shell.LocaleCenter.LocaleCenterSummaryText = null;
        _shell.LocaleCenter.LocaleCenterHintText = null;
        _shell.LocaleCenter.LocaleCenterOverallProgressBar = null;
        _shell.LocaleCenter.LocaleCenterSyncAllButton = null;
        _shell.LocaleCenter.LocaleCenterSync2Button = null;
        _shell.LocaleCenter.LocaleCenterRetryFailedButton = null;
        _shell.LocaleCenter.LocaleCenterStopButton = null;
        _shell.LocaleCenter.LocaleCenterCloseButton = null;
        _shell.LocaleCenter.LocaleCenterMatrixScroll = null;
        _shell.LocaleCenter.LocaleCenterCardRefs.Clear();
        _shell.ChatPanel.ChatStreamScroller = null;
        _shell.ChatPanel.ChatStreamPanel = null;
        _shell.ChatPanel.StageHealthDiagnosticBar = null;
        _shell.ChatPanel.SuggestionChipsPanel = null;
        _shell.ChatPanel.TargetNodePillBorder = null;
        _shell.ChatPanel.TargetNodePillText = null;
        _shell.ChatPanel.RefineTextBox = null;
        _shell.ChatPanel.RefineSubmitButton = null;
        _shell.ChatPanel.ApplyInPlaceButton = null;
        _shell.ChatPanel.CommitButton = null;
        _stageWallpaperLayer = null;
        _stageWallpaperScrim = null;
        _stageGeneratingProgressBar = null;
        _stageGeneratingProgressRing = null;
        _stageGeneratingStatusPill = null;
        _stageGeneratingStatusText = null;
        _frameDivider = null;
        _frameEditGlyph = null;
        _pickElementButton = null;
        _pickElementIcon = null;
        _pickElementLabel = null;
        _settingsPreviewButton = null;
        _stageViewportModeSegmented = null;
        _stageBackdropSegmented = null;
        _stageWidgetThemeSegmented = null;
        _stageChromeModeSegmented = null;
        _stageAppearanceButton = null;
        _currentStageZoomPercent = null;
        _refreshStageZoomBadgeAction = null;
        _sampleDataToggleButton = null;
        _shell.ChatPanel.DirectStageTitleText = null;
        _shell.ChatPanel.DirectStatsBadgeText = null;
        _shell.ChatPanel.DirectThinkingContainer = null;
        _shell.ChatPanel.DirectThinkingHeaderText = null;
        _shell.ChatPanel.DirectThinkingCollapsedPreviewText = null;
        _shell.ChatPanel.DirectThinkingScroller = null;
        _shell.ChatPanel.DirectThinkingText = null;
        _shell.ChatPanel.PendingChatStickyScrollQueued = false;
        _shell.ChatPanel.PendingThinkingStickyScrollQueued = false;
        _shell.ChatPanel.DirectActionTracePanel = null;
        _shell.ChatPanel.DirectStreamHintText = null;
        _shell.ChatPanel.DirectStep1Pill = null;
        _shell.ChatPanel.DirectStep2Pill = null;
        _shell.ChatPanel.DirectStep3Pill = null;
    }

    internal void ClearWallpaperLayerBackground()
    {
        if (_stageWallpaperLayer is not null)
        {
            _stageWallpaperLayer.Background = null;
        }
    }

    public void Dispose()
    {
        DisposePreview();
    }
}
