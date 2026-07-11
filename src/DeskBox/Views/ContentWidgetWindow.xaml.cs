using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Lightweight host window for future non-file widget content.
/// User-facing creation remains gated by WidgetRegistry.
/// </summary>
public sealed partial class ContentWidgetWindow : WidgetWindowBase, IDesktopWidgetWindow
{
    private readonly WidgetConfig _config;
    private readonly WidgetContentDescriptor _descriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private readonly WidgetShellContentHost _contentHost;
    private readonly ContentWidgetTitleViewModel _titleViewModel;

    private bool _isHidePrepared;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;

    private bool _isVisibleOnDesktop;

    public ContentWidgetWindow(
        WidgetConfig config,
        IWidgetContent content,
        SettingsService settingsService,
        WidgetContentDescriptor descriptor)
    {
        _config = config;
        _descriptor = descriptor;
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);

        InitializeComponent();

        SettingsService = settingsService;
        HWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(HWnd);
        AppWindow = AppWindow.GetFromWindowId(windowId);
        Diagnostics = new WidgetWindowDiagnostics("Content", _config, () => HWnd);
        TrayAnimation = new WidgetTrayAnimationController(
            AppWindow,
            RootGrid,
            DispatcherQueue,
            HWnd,
            () => Diagnostics.AnimationBounds,
            LogTrayWindow);
        _contentHost = new WidgetShellContentHost(ContentWidgetShell);

        _titleViewModel = new ContentWidgetTitleViewModel(_config, settingsService);
        ContentWidgetShell.DataContext = _titleViewModel;
        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.TitleIconKind = WidgetTitleIconKindNames.FromWidgetKind(_config.WidgetKind);
        ContentWidgetShell.ShowHoverButtons = settingsService.Settings.ShowHoverButtons;
        ContentWidgetShell.IsTitleEditable = true;
        ApplyLocalizedTitleActionTooltips();

        ConfigureWindowCore();
        ApplyTitleBarLayout();
        SetupEventHandlers();
        _ = LoadContentAsync(content);

        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    // ── Abstract member overrides ──────────────────────────────

    public override WidgetConfig Config => _config;
    protected override double WidgetOpacity => SettingsService.Settings.WidgetOpacity;
    protected override FrameworkElement RootElement => RootGrid;
    protected override string LogPrefix => "Content";
    protected override bool IsSizeLocked => _config.IsSizeLocked;
    protected override bool IsPositionLocked => _config.IsPositionLocked;

    protected override void UpdateConfigBoundsFromPhysical(
        int x, int y, int width, int height, bool persist)
    {
        var bounds = new RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(_config, bounds, workArea);
        if (persist)
        {
            SettingsService.UpdateWidget(_config, notifySubscribers: false);
            SettingsService.SaveDebounced();
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

    // ── Virtual hooks ──────────────────────────────────────────

    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(SettingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = SettingsService.Settings.WidgetMaterialType;
        string borderStyle = SettingsService.Settings.WidgetBorderStyle;

        // Simplified layering: only apply surface color overlay for Solid mode.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            ContentWidgetShell.BackgroundSurface.Background = new SolidColorBrush(surfaceColor);
        }
        else
        {
            ContentWidgetShell.BackgroundSurface.Background = new SolidColorBrush(Colors.Transparent);
        }

        var (borderThickness, borderAlpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            _ => (0.8d, (byte)0x18),
        };

        var borderColor = isDark
            ? ColorHelper.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), borderAlpha);
        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(borderAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(borderAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        ContentWidgetShell.BackgroundSurface.BorderThickness = new Thickness(borderThickness);
        ContentWidgetShell.BackgroundSurface.BorderBrush = new SolidColorBrush(borderColor);
        ContentWidgetShell.BackgroundSurface.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());
        ContentWidgetShell.Divider.Background = new SolidColorBrush(dividerColor);
        ContentWidgetShell.TitleIconAccentColor = iconForeground;
        ContentWidgetShell.TitleIconMode = SettingsService.Settings.WidgetTitleIconMode;
    }

    protected override void OnRootElementLoaded()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    // ── IDesktopWidgetWindow implementation ────────────────────

    public IntPtr WindowHandle => HWnd;
    public WidgetWindowIdentity Identity => Diagnostics.Identity;
    public Windows.Foundation.Rect AnimationBounds => Diagnostics.AnimationBounds;

    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    internal IWidgetContent? CurrentContent => _contentHost.CurrentContent;

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        if (IsClosing)
        {
            return;
        }

        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        ApplySurfaceStyle();
        ContentWidgetShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;
        ApplyTitleBarLayout();
        _contentHost.ApplyAppearance();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        TrayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

    public void PrepareTrayShowAnimation()
    {
        TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        _isHidePrepared = false;
        IsHideAnimationRunning = false;

        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={TrayAnimation.Generation} effect={SettingsService.Settings.WidgetAnimationEffect} " +
            $"speed={SettingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        TrayAnimation.PrepareVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        PushToBottom();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        HoldTemporaryTopMost();
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        TrayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={TrayAnimation.Generation}");
        TrayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || IsHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={IsHideAnimationRunning}");
            return false;
        }

        TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        IsHideAnimationRunning = true;
        _isHidePrepared = true;
        Visible = false;
        _config.IsVisible = false;
        if (persistVisibility)
        {
            SettingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={TrayAnimation.Generation}");
        TrayAnimation.PrepareVisualState(0, 0, WidgetTrayAnimationController.RestingOpacity, WidgetTrayAnimationController.RestingScale);
        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHidePrepared || !IsHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void ActivateRaisedFromTrayBatch()
    {
        if (!Visible)
        {
            return;
        }

        HoldTemporaryTopMost();
        base.Activate();
        RootGrid.Focus(FocusState.Programmatic);
        _contentHost.OnActivated();
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            return;
        }

        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(HWnd);
        HoldTemporaryTopMost();
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayerFromManager();
    }

    public void RestoreDesktopLayerFromManager()
    {
        if (!Visible)
        {
            return;
        }

        RestoreDesktopLayer(force: true);
        _contentHost.OnDeactivated();
    }

    public void HideWindow()
    {
        TrayAnimation.Stop();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        Visible = false;
        _config.IsVisible = false;
        SettingsService.SaveDebounced();
        WidgetLayerService.ClearTopMost(HWnd);
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_HIDE);
        AppWindow.Hide();
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        _contentHost.OnWindowVisibilityChanged(false);
    }

    public void CloseWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CloseWindow);
            return;
        }

        if (IsClosing)
        {
            return;
        }

        IsClosing = true;
        WidgetLayerService.ReleaseWindow(HWnd);
        Close();
    }

    // ── Event setup ────────────────────────────────────────────

    private void OnLanguageChanged()
    {
        _titleViewModel.RefreshDisplayName();
        ApplyLocalizedTitleActionTooltips();
    }

    private async Task LoadContentAsync(IWidgetContent content)
    {
        await _contentHost.SetContentAsync(content);
        ApplyTitleActionButtonConfiguration();
    }

    private void ApplyLocalizedTitleActionTooltips()
    {
        var localization = App.Current.LocalizationService;
        ToolTipService.SetToolTip(ContentWidgetShell.PositionLockActionButton, localization.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(ContentWidgetShell.SizeLockActionButton, localization.T("Widget.LockSize"));
        ToolTipService.SetToolTip(ContentWidgetShell.AddActionButton, localization.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(ContentWidgetShell.MoreActionButton, localization.T("Widget.Tooltip.More"));
        ToolTipService.SetToolTip(ContentWidgetShell.CloseActionButton, localization.T("Widget.Tooltip.DeleteWidget"));
    }

    private void SetupEventHandlers()
    {
        SettingsService.SettingsChanged += OnSettingsChanged;
        Activated += ContentWidgetWindow_Activated;
        AppWindow.Changed += OnAppWindowChanged;
        DisplayChangeWatcher = new WidgetDisplayChangeWatcher(HWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);
        ContentWidgetShell.RightTapped += ContentWidgetShell_RightTapped;
        ContentWidgetShell.TitleDoubleTapped += ContentWidgetShell_TitleDoubleTapped;

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                child.PointerPressed += ResizeBorder_PointerPressed;
                child.PointerMoved += ResizeBorder_PointerMoved;
                child.PointerReleased += ResizeBorder_PointerReleased;
                child.PointerEntered += ResizeBorder_PointerEntered;
            }
        }

        Closed += (_, _) =>
        {
            IsClosing = true;
            Visible = false;
            App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
            SettingsService.SettingsChanged -= OnSettingsChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            ContentWidgetShell.RightTapped -= ContentWidgetShell_RightTapped;
            ContentWidgetShell.TitleDoubleTapped -= ContentWidgetShell_TitleDoubleTapped;
            CleanupBase();
            _contentHost.DisposeContent();

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    child.PointerPressed -= ResizeBorder_PointerPressed;
                    child.PointerMoved -= ResizeBorder_PointerMoved;
                    child.PointerReleased -= ResizeBorder_PointerReleased;
                    child.PointerEntered -= ResizeBorder_PointerEntered;
                }
            }
        };
    }

    private void OnSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ContentWidgetShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;
        _titleViewModel.RefreshMetrics();
        ApplyAppearancePreview();
    }

    // ── Drag handlers (delegate to base) ───────────────────────

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsPositionLocked) return;
        var properties = e.GetCurrentPoint(ContentWidgetShell.TitleBar).Properties;
        if (!properties.IsLeftButtonPressed) return;
        BeginWindowDragCore(e, ContentWidgetShell.TitleBar);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsPositionLocked) return;
        var properties = e.GetCurrentPoint(ContentWidgetShell.DragHandleElement).Properties;
        if (!properties.IsLeftButtonPressed) return;
        BeginWindowDragCore(e, ContentWidgetShell.DragHandleElement);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

    protected override void OnDragEnd(bool hasMoved)
    {
        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    // ── Resize handlers (delegate to base) ─────────────────────

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerPressedCore(sender, e);
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerMovedCore(sender, e);
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerReleasedCore(sender, e);
    }

    protected override void OnResizeEnd()
    {
        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = _config.IsSizeLocked
            ? InputSystemCursorShape.Arrow
            : element is FrameworkElement frameworkElement
                ? frameworkElement.Tag switch
                {
                    "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
                    "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
                    "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
                    "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                }
                : InputSystemCursorShape.Arrow;

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    // ── Activation ─────────────────────────────────────────────

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _contentHost.OnDeactivated();
            if (Visible && !IsAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] Content Deactivated→Restore hwnd=0x{HWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
            return;
        }

        _contentHost.OnActivated();
    }

    // ── Tray animation ─────────────────────────────────────────

    private void ShowWithoutActivation(bool persistVisibility)
    {
        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        _config.IsVisible = true;
        if (persistVisibility)
        {
            SettingsService.SaveDebounced();
        }

        ApplyBackdropPreference();
        _contentHost.OnWindowVisibilityChanged(true);
    }

    private void PushToBottom()
    {
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd);
        App.LogVerbose($"[ZOrder] Content PushToBottom hwnd=0x{HWnd.ToInt64():X}");
    }

    private void PlayTrayRaiseAnimation()
    {
        long generation = TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;

        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={generation} durationMs={profile.DurationMs}");
        TrayAnimation.Animate(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            0,
            0,
            profile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            profile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            profile.DurationMs,
            true,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                TrayAnimation.RestoreVisualState();
                TrayAnimation.RestoreWindowPosition();
            });
    }

    private void PlayTrayRaiseAnimationAfterFirstFrame()
    {
        if (Visible)
        {
            PlayTrayRaiseAnimation();
        }
    }

    private void PlayTrayHideAnimation(Action completed)
    {
        long generation = TrayAnimation.Generation;
        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={generation}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={generation} durationMs={profile.DurationMs}");
        TrayAnimation.Animate(
            0,
            0,
            profile.HideOffsetX,
            profile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            profile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            profile.HideEndScale,
            profile.DurationMs,
            false,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                if (!Visible)
                {
                    completed();
                }
            });
    }

    private void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            LogTrayWindow("CompleteHide skipped reason=visible-again");
            return;
        }

        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        TrayAnimation.Stop();
        WidgetLayerService.ClearTopMost(HWnd);
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_HIDE);
        AppWindow.Hide();
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        _contentHost.OnWindowVisibilityChanged(false);
        LogTrayWindow("CompleteHide");
    }

    // ── Title bar & layout ─────────────────────────────────────

    private void ApplyTitleBarLayout()
    {
        var chromeMode = _chromeModeResolver.Resolve(_config, _descriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? SettingsService.NormalizeTextSize(SettingsService.Settings.TextSize)
            : _titleViewModel.TitleTextSize;
        var metrics = WidgetTitleBarMetricsCalculator.Create(
            _titleViewModel.TitleIconSize,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        ContentWidgetShell.ChromeMode = chromeMode;
        ContentWidgetShell.TitleIconElement.IconSize = metrics.TitleIconSize;
        ContentWidgetShell.TitleTextElement.FontSize = metrics.TitleTextSize;
        ApplyTitleActionButtonConfiguration();
        ApplyLockActionIconState();

        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.PositionLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.SizeLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.AddActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.MoreActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.CloseActionButton, metrics);

        WidgetActionIconHelper.ApplyPairSize(
            ContentWidgetShell.PositionLockActionIcon,
            ContentWidgetShell.PositionLockFilledActionIcon,
            metrics);
        WidgetActionIconHelper.ApplyPairSize(
            ContentWidgetShell.SizeLockActionIcon,
            ContentWidgetShell.SizeLockFilledActionIcon,
            metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.AddActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.MoreActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.CloseActionIcon, metrics);

        ContentWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        ContentWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
    }

    private void ApplyTitleActionButtonConfiguration()
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(SettingsService.Settings.WidgetHoverButtonActions);
        bool contentCanAdd = _contentHost.CurrentContent is IWidgetAddActionContent;
        ContentWidgetShell.PositionLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockPosition)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.SizeLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.ShowAddButton = contentCanAdd &&
            actions.Contains(SettingsService.WidgetHoverActionAdd);
        ContentWidgetShell.MoreActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionMore)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.CloseActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionDelete)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            ContentWidgetShell.PositionLockActionIcon,
            ContentWidgetShell.PositionLockFilledActionIcon,
            _config.IsPositionLocked,
            ContentWidgetShell.SizeLockActionIcon,
            ContentWidgetShell.SizeLockFilledActionIcon,
            _config.IsSizeLocked);
    }

    // ── Button click handlers ──────────────────────────────────

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contentHost.CurrentContent is IWidgetAddActionContent addActionContent)
        {
            await addActionContent.AddFromTitleButtonAsync();
        }
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (FeatureWidgetSettings.IsFeatureWidget(_config.WidgetKind) &&
            App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.SetFeatureWidgetEnabledAsync(_config.WidgetKind, enabled: false, reveal: false);
            return;
        }

        HideWindow();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? ContentWidgetShell.MoreActionButton;
        ShowFlyoutWithInteraction(CreateMoreFlyout(), target);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!_config.IsPositionLocked);
        ApplyLockActionIconState();
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!_config.IsSizeLocked);
        ApplyLockActionIconState();
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell.TitleBar, e.GetPosition(ContentWidgetShell.TitleBar));
        e.Handled = true;
    }

    private void ContentWidgetShell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ContentWidgetShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden))
        {
            return;
        }

        ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell, e.GetPosition(ContentWidgetShell));
        e.Handled = true;
    }

    private void ContentWidgetShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsDragging || IsResizing || ContentWidgetShell.TitleEditorContent is not null)
            {
                return;
            }

            StartTitleRename();
        });
    }

    // ── Flyout ─────────────────────────────────────────────────

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(CreateToggleMenuItem(
            App.Current.LocalizationService.T("Widget.LockPosition"),
            "\uE72E",
            _config.IsPositionLocked,
            SetPositionLocked));
        flyout.Items.Add(CreateToggleMenuItem(
            App.Current.LocalizationService.T("Widget.LockSize"),
            "\uE9CE",
            _config.IsSizeLocked,
            SetSizeLocked));
        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            _config,
            _descriptor,
            App.Current.LocalizationService,
            SetChromeModeOverride));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += (_, _) => DispatcherQueue.TryEnqueue(StartTitleRename);
        flyout.Items.Add(rename);

        if (_descriptor.HasSettingsPage && !string.IsNullOrWhiteSpace(_descriptor.SettingsSectionTag))
        {
            var settingsItem = new MenuFlyoutItem
            {
                Text = App.Current.LocalizationService.T(GetSettingsMenuTextKey(_config.WidgetKind)),
                Icon = new FontIcon { Glyph = "\uE713" }
            };
            settingsItem.Click += (_, _) => App.Current.ShowSettings(_descriptor.SettingsSectionTag);
            flyout.Items.Add(settingsItem);
        }

        if (_config.WidgetKind == WidgetKind.Todo)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var clearAllItem = new MenuFlyoutItem
            {
                Text = App.Current.LocalizationService.T("Todo.ClearAll"),
                Icon = new FontIcon
                {
                    Glyph = "\uE894",
                    Foreground = new SolidColorBrush(Colors.Red)
                }
            };
            clearAllItem.Click += (_, _) => ShowTodoClearAllConfirmation();
            flyout.Items.Add(clearAllItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var deleteWidget = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Widget.Tooltip.DeleteWidget"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += async (_, _) =>
        {
            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.RemoveWidgetAsync(_config.Id);
            }
        };
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private void ShowTodoClearAllConfirmation()
    {
        if (_contentHost.CurrentContent?.View is TodoWidgetContent todoContent)
        {
            todoContent.ShowClearAllConfirmation(ContentWidgetShell.MoreActionButton);
        }
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(_config, mode);
        SettingsService.UpdateWidget(_config);
        ApplyAppearancePreview();
    }

    private static string GetSettingsMenuTextKey(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.Todo => "Todo.OpenSettings",
            WidgetKind.Music => "Music.OpenSettings",
            _ => "Common.Configure"
        };
    }

    private static ToggleMenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsChecked = isChecked
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }

    private void SetPositionLocked(bool value)
    {
        if (_config.IsPositionLocked == value)
        {
            return;
        }

        _config.IsPositionLocked = value;
        SettingsService.UpdateWidget(_config);
        ApplyLockActionIconState();
    }

    private void SetSizeLocked(bool value)
    {
        if (_config.IsSizeLocked == value)
        {
            return;
        }

        _config.IsSizeLocked = value;
        SettingsService.UpdateWidget(_config);
        ApplyLockActionIconState();
    }

    // ── Title rename ───────────────────────────────────────────

    private void StartTitleRename()
    {
        if (IsDragging ||
            IsResizing ||
            ContentWidgetShell.TitleEditorContent is not null)
        {
            return;
        }

        _isCancellingTitleRename = false;
        App.Current.WidgetManager?.BeginWidgetInteraction("content-title-rename-opened");
        var editor = CreateTitleRenameEditor();
        ContentWidgetShell.TitleEditorContent = editor;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private TextBox CreateTitleRenameEditor()
    {
        var localization = App.Current.LocalizationService;
        double titleWidth = ContentWidgetShell.TitleTextElement.ActualWidth > 0
            ? ContentWidgetShell.TitleTextElement.ActualWidth + 36
            : (_titleViewModel.DisplayName.Length * 9.5) + 36;

        var editor = new TextBox
        {
            Text = _titleViewModel.DisplayName,
            PlaceholderText = localization.T("Widget.TitlePlaceholder"),
            Width = Math.Clamp(titleWidth, 120, 220),
            MaxWidth = 220,
            FontSize = Math.Max(ContentWidgetShell.TitleTextElement.FontSize - 1, 11),
            Style = GetTextBoxStyleResource("WidgetTitleRenameTextBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        editor.KeyDown += TitleRenameEditor_KeyDown;
        editor.LostFocus += TitleRenameEditor_LostFocus;
        return editor;
    }

    private static Style? GetTextBoxStyleResource(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Style style
            ? style
            : null;
    }

    private async void TitleRenameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitTitleRenameAsync();
    }

    private async void TitleRenameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitTitleRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelTitleRename();
            e.Handled = true;
        }
    }

    private async Task CommitTitleRenameAsync()
    {
        if (_isCommittingTitleRename ||
            ContentWidgetShell.TitleEditorContent is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await App.Current.WidgetManager!.RenameWidgetAsync(_config.Id, newName);
                _titleViewModel.RefreshDisplayName();
            }

            CompleteTitleRename("content-title-rename-committed");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(App.Current.LocalizationService.T("Widget.RenameFailed"), ex.Message);
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelTitleRename()
    {
        _isCancellingTitleRename = true;
        CompleteTitleRename("content-title-rename-canceled");
    }

    private void CompleteTitleRename(string reason)
    {
        if (ContentWidgetShell.TitleEditorContent is TextBox editor)
        {
            editor.KeyDown -= TitleRenameEditor_KeyDown;
            editor.LostFocus -= TitleRenameEditor_LostFocus;
        }

        ContentWidgetShell.TitleEditorContent = null;
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayerFromManager();
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var localization = App.Current.LocalizationService;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxWidth = 320
            },
            CloseButtonText = localization.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void ShowFlyoutWithInteraction(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction("content-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            App.Current.WidgetManager?.EndWidgetInteraction("content-flyout-closed");
            if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer("content-flyout-closed") == true)
            {
                return;
            }

            RestoreDesktopLayerFromManager();
        };

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    // ── Color helpers ──────────────────────────────────────────

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

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var mixed = BlendColors(baseColor, accentColor, accentMix);
        var overlay = isDark
            ? ColorHelper.FromArgb(0xFF, 0x2B, 0x2F, 0x36)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(mixed, overlay, overlayMix);
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        return Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    // ── Nested: title view model ───────────────────────────────

    private sealed class ContentWidgetTitleViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;

        public ContentWidgetTitleViewModel(WidgetConfig config, SettingsService settingsService)
        {
            Config = config;
            _settingsService = settingsService;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public WidgetConfig Config { get; }

        public string DisplayName
        {
            get
            {
                if (Config.IsDefaultTitle)
                {
                    var localization = App.Current.LocalizationService;
                    var key = Config.WidgetKind switch
                    {
                        WidgetKind.Todo => "Todo.Title",
                        WidgetKind.Weather => "Weather.Title",
                        WidgetKind.Tags => "Tags.Title",
                        WidgetKind.Music => "Music.Title",
                        WidgetKind.SystemMonitor => "SystemMonitor.Title",
                        _ => ""
                    };
                    if (!string.IsNullOrEmpty(key))
                    {
                        var localized = localization.T(key);
                        if (!string.IsNullOrEmpty(localized))
                            return localized;
                    }
                }

                return string.IsNullOrWhiteSpace(Config.Name)
                    ? Config.WidgetKind.ToString()
                    : Config.Name;
            }
        }

        public double TitleIconSize
        {
            get
            {
                double iconSize = SettingsService.NormalizeIconSize(_settingsService.Settings.IconSize);
                return Math.Clamp(Math.Round(iconSize * 0.72 * 0.56 * 0.54), 11, 18);
            }
        }

        public double TitleTextSize
        {
            get
            {
                double textSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
                return Math.Min(SettingsService.MaxTextSize + 2, textSize + 3);
            }
        }

        public void RefreshDisplayName()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }

        public void RefreshMetrics()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TitleIconSize)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TitleTextSize)));
        }
    }
}
