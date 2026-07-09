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
public sealed partial class ContentWidgetWindow : Window, IDesktopWidgetWindow
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

    private readonly WidgetConfig _config;
    private readonly SettingsService _settingsService;
    private readonly WidgetContentDescriptor _descriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private readonly WidgetWindowDiagnostics _diagnostics;
    private readonly WidgetTrayAnimationController _trayAnimation;
    private readonly WidgetShellContentHost _contentHost;
    private readonly ContentWidgetTitleViewModel _titleViewModel;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;

    private DesktopAcrylicController? _acrylicController;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _isResizing;
    private bool _isApplyingBounds;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;
    private bool _isHidePrepared;
    private bool _isHideAnimationRunning;
    private bool _isClosing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private PointInt32 _initialWindowPos;
    private SizeInt32 _initialWindowSize;
    private FrameworkElement? _dragCaptureElement;

    public ContentWidgetWindow(
        WidgetConfig config,
        IWidgetContent content,
        SettingsService settingsService,
        WidgetContentDescriptor descriptor)
    {
        _config = config;
        _settingsService = settingsService;
        _descriptor = descriptor;
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);

        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _diagnostics = new WidgetWindowDiagnostics("Content", _config, () => _hWnd);
        _trayAnimation = new WidgetTrayAnimationController(
            _appWindow,
            RootGrid,
            DispatcherQueue,
            _hWnd,
            () => _diagnostics.AnimationBounds,
            LogTrayWindow);
        _contentHost = new WidgetShellContentHost(ContentWidgetShell);

        _titleViewModel = new ContentWidgetTitleViewModel(_config, _settingsService);
        ContentWidgetShell.DataContext = _titleViewModel;
        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.TitleIconKind = WidgetTitleIconKindNames.FromWidgetKind(_config.WidgetKind);
        ContentWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ContentWidgetShell.IsTitleEditable = true;
        ApplyLocalizedTitleActionTooltips();

        ConfigureWindow();
        ApplyTitleBarLayout();
        SetupEventHandlers();
        _ = LoadContentAsync(content);

        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

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

    public IntPtr WindowHandle => _hWnd;

    public WidgetWindowIdentity Identity => _diagnostics.Identity;

    public Windows.Foundation.Rect AnimationBounds => _diagnostics.AnimationBounds;

    public WidgetConfig Config => _config;

    internal IWidgetContent? CurrentContent => _contentHost.CurrentContent;

    private bool _isVisibleOnDesktop;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private DateTime _lastElevateForInteractionUtc = DateTime.MinValue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostSafetyTimer;
    private WidgetDisplayChangeWatcher? _displayChangeWatcher;

    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        if (_isClosing)
        {
            return;
        }

        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        ApplySurfaceStyle();
        ContentWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ApplyTitleBarLayout();
        _contentHost.ApplyAppearance();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        _trayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

    public void PrepareTrayShowAnimation()
    {
        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        _isHidePrepared = false;
        _isHideAnimationRunning = false;

        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={_trayAnimation.Generation} effect={_settingsService.Settings.WidgetAnimationEffect} " +
            $"speed={_settingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        _trayAnimation.PrepareVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
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
        _trayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={_trayAnimation.Generation}");
        _trayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || _isHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={_isHideAnimationRunning}");
            return false;
        }

        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        _isHideAnimationRunning = true;
        _isHidePrepared = true;
        Visible = false;
        _config.IsVisible = false;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={_trayAnimation.Generation}");
        _trayAnimation.PrepareVisualState(0, 0, WidgetTrayAnimationController.RestingOpacity, WidgetTrayAnimationController.RestingScale);
        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHidePrepared || !_isHideAnimationRunning)
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

        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(_hWnd);
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
        _trayAnimation.Stop();
        _isHideAnimationRunning = false;
        _isHidePrepared = false;
        Visible = false;
        _config.IsVisible = false;
        _settingsService.SaveDebounced();
        WidgetLayerService.ClearTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
    }

    public void CloseWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CloseWindow);
            return;
        }

        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        WidgetLayerService.ReleaseWindow(_hWnd);
        Close();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        _appWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;
        var workArea = DisplayArea.GetFromRect(
            new RectInt32(
                (int)Math.Round(_config.X),
                (int)Math.Round(_config.Y),
                (int)Math.Round(_config.Width),
                (int)Math.Round(_config.Height)),
            DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WidgetPositioningService.ResolveBounds(
            _config,
            workArea,
            WidgetPositioningService.GetAvailableWorkAreas());
        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false);

        ApplyDwmBorderStyle(RootGrid.ActualTheme == ElementTheme.Dark);
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            RootGrid.Focus(FocusState.Programmatic);
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        Activated += ContentWidgetWindow_Activated;
        _appWindow.Changed += AppWindow_Changed;
        _displayChangeWatcher = new WidgetDisplayChangeWatcher(_hWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);
        ContentWidgetShell.RightTapped += ContentWidgetShell_RightTapped;
        ContentWidgetShell.TitleDoubleTapped += ContentWidgetShell_TitleDoubleTapped;

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                child.PointerMoved += ResizeBorder_PointerMoved;
                child.PointerReleased += ResizeBorder_PointerReleased;
                child.PointerEntered += ResizeBorder_PointerEntered;
            }
        }

        Closed += (_, _) =>
        {
            _isClosing = true;
            Visible = false;
            WidgetLayerService.ReleaseWindow(_hWnd);
            _topMostSafetyTimer?.Stop();
            _topMostSafetyTimer = null;
            App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _displayChangeWatcher?.Dispose();
            _displayChangeWatcher = null;
            ContentWidgetShell.RightTapped -= ContentWidgetShell_RightTapped;
            ContentWidgetShell.TitleDoubleTapped -= ContentWidgetShell_TitleDoubleTapped;
            DisposeAcrylicController();
            DisposeMicaController();
            _contentHost.DisposeContent();

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    child.PointerMoved -= ResizeBorder_PointerMoved;
                    child.PointerReleased -= ResizeBorder_PointerReleased;
                    child.PointerEntered -= ResizeBorder_PointerEntered;
                }
            }
        };
    }

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    private bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (_isClosing ||
            _isHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (
            _isDragging ||
            _isResizing ||
            _trayAnimation.IsApplyingBounds)
        {
            return false;
        }

        var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(_config);
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (position.X == bounds.X &&
            position.Y == bounds.Y &&
            size.Width == bounds.Width &&
            size.Height == bounds.Height)
        {
            return true;
        }

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: false);
        return true;
    }

    private bool RestoreBoundsAfterDisplayChange()
    {
        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored && Visible)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _contentHost.OnDeactivated();
            // Always restore desktop layer when deactivated, regardless of _keepRaisedUntilDeactivate
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] Content Deactivated→Restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
            return;
        }

        _contentHost.OnActivated();
    }

    private void ContentWidgetShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDragging || _isResizing || ContentWidgetShell.TitleEditorContent is not null)
            {
                return;
            }

            StartTitleRename();
        });
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingBounds || _trayAnimation.IsApplyingBounds || (!_isDragging && !_isResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            UpdateConfigBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

private void OnSettingsChanged()
{
if (!DispatcherQueue.HasThreadAccess)
{
DispatcherQueue.TryEnqueue(OnSettingsChanged);
return;
}

ContentWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
_titleViewModel.RefreshMetrics();
ApplyAppearancePreview();
}

    private void ApplyTitleBarLayout()
    {
        var chromeMode = _chromeModeResolver.Resolve(_config, _descriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize)
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
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
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

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contentHost.CurrentContent is IWidgetAddActionContent addActionContent)
        {
            await addActionContent.AddFromTitleButtonAsync();
        }
    }

    private void ShowWithoutActivation(bool persistVisibility)
    {
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        _config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        ApplyBackdropPreference();
    }

    private void PushToBottom()
    {
        _isAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(_hWnd);
        App.Log($"[ZOrder] Content PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    private void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(_hWnd);
        App.Log($"[ZOrder] Content ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    private void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            _isAtDesktopLayer = true;
            _keepRaisedUntilDeactivate = false;
            _restoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(_hWnd);
            App.Log($"[ZOrder] Content HoldTemporaryTopMost skipped pinned hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(_hWnd);
        App.Log($"[ZOrder] Content HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    private void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        _lastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            return;
        }

        App.Log($"[ZOrder] Content RestoreDesktopLayer EXECUTING force={force}");
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
    }

    private void StartTopMostSafetyTimer()
    {
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = DispatcherQueue.CreateTimer();
        _topMostSafetyTimer.IsRepeating = false;
        _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
        _topMostSafetyTimer.Tick += (_, _) =>
        {
            _topMostSafetyTimer.Stop();
            _topMostSafetyTimer = null;
            if (!_isAtDesktopLayer && App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] Content safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
        };
        _topMostSafetyTimer.Start();
    }

    private void PlayTrayRaiseAnimation()
    {
        long generation = _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        _isHideAnimationRunning = false;
        _isHidePrepared = false;

        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={generation} durationMs={profile.DurationMs}");
        _trayAnimation.Animate(
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
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                _trayAnimation.RestoreVisualState();
                _trayAnimation.RestoreWindowPosition();
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
        long generation = _trayAnimation.Generation;
        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={generation}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={generation} durationMs={profile.DurationMs}");
        _trayAnimation.Animate(
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
            _settingsService.Settings.WidgetAnimationEasingIntensity,
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

        _isHideAnimationRunning = false;
        _isHidePrepared = false;
        _trayAnimation.Stop();
        WidgetLayerService.ClearTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        LogTrayWindow("CompleteHide");
    }

    private WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return _trayAnimation.CreateProfile(WidgetAnimationSettings.From(_settingsService.Settings));
    }

    private void LogTrayWindow(string message)
    {
        App.LogVerbose(_diagnostics.FormatTrayWindowMessage(message));
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
        width = Math.Max(minSize.Width, width);
        height = Math.Max(minSize.Height, height);
        _isApplyingBounds = true;
        try
        {
            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally
        {
            _isApplyingBounds = false;
        }

        if (persist)
        {
            UpdateConfigBounds(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            UpdateConfigBounds(x, y, width, height, persist: false);
        }
    }

    private SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    private void UpdateConfigBounds(int x, int y, int width, int height, bool persist)
    {
        var bounds = new RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(_config, bounds, workArea);
        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            _settingsService.UpdateWidget(_config, notifySubscribers: false);
            _settingsService.SaveDebounced();
        }
    }

    private void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        _config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(_config, bounds, workArea);
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

    private void ApplyBackdropPreference()
    {
        if (_hWnd == IntPtr.Zero || _isClosing)
        {
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(_settingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);
        string materialType = _settingsService.Settings.WidgetMaterialType;

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            // Apply DWM border color based on border style setting
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (materialType is SettingsService.WidgetMaterialTypeMica)
            {
                controllerApplied = ApplyMicaController(isDark, tintColor, surfaceOpacity);
            }

            if (!controllerApplied && materialType is SettingsService.WidgetMaterialTypeAcrylic)
            {
                controllerApplied = ApplyAcrylicController(isDark, tintColor, surfaceOpacity);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                // Solid: no system backdrop, no accent blur
                DisposeAcrylicController();
                DisposeMicaController();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DisposeAcrylicController();
                DisposeMicaController();
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"ContentWidget ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private double GetCornerRadiusFromPreference()
    {
        return _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    private void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(_hWnd, borderNone);
    }

    private bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (_micaController is null)
        {
            DisposeAcrylicController();
            _micaController = new MicaController { Kind = MicaKind.Base };

            if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeMicaController();
                return false;
            }
        }
        else
        {
            DisposeAcrylicController();
        }

        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _micaController.Kind = MicaKind.Base;
        _micaController.TintColor = tintColor;
        _micaController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _micaController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.5, 0.0, 1.0);
        return true;
    }

    private void DisposeMicaController()
    {
        if (_micaController is null)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
            _micaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _micaController = null;
        }
    }

    private bool ApplyAcrylicController(bool isDark, Windows.UI.Color tintColor, double surfaceOpacity)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _backdropConfiguration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            DisposeMicaController();
            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }
        else
        {
            DisposeMicaController();
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        _acrylicController.TintOpacity = (float)(isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44));
        _acrylicController.LuminosityOpacity = (float)(isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86));
        return true;
    }

    private bool ApplyTransparentAcrylicController(bool isDark)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.TintOpacity = 0.0f;
        _acrylicController.LuminosityOpacity = 0.0f;

        DisposeMicaController();
        return true;
    }

    private void DisposeAcrylicController()
    {
        if (_acrylicController is null)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
        }
        catch
        {
        }

        _acrylicController = null;
    }

    private Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
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

    private void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(_settingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _settingsService.Settings.WidgetMaterialType;
        string borderStyle = _settingsService.Settings.WidgetBorderStyle;

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
        ContentWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
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

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginWindowDrag(e, ContentWidgetShell.TitleBar);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginWindowDrag(e, ContentWidgetShell.DragHandleElement);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        if (_config.IsPositionLocked)
        {
            return;
        }

        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        ApplyWindowBounds(
            _initialWindowPos.X + deltaX,
            _initialWindowPos.Y + deltaY,
            _initialWindowSize.Width,
            _initialWindowSize.Height,
            persist: false);
        e.Handled = true;
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragCaptureElement?.ReleasePointerCapture(e.Pointer);
        _dragCaptureElement = null;
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        UpdateConfigBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        e.Handled = true;
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsSizeLocked || sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeDirection = element.Tag as string ?? string.Empty;
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        int newWidth = _initialWindowSize.Width;
        int newHeight = _initialWindowSize.Height;
        int newX = _initialWindowPos.X;
        int newY = _initialWindowPos.Y;
        var minSize = GetPhysicalMinimumWindowSize(
            _initialWindowPos.X,
            _initialWindowPos.Y,
            _initialWindowSize.Width,
            _initialWindowSize.Height);

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        ApplyWindowBounds(newX, newY, newWidth, newHeight, persist: false);
        e.Handled = true;
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || sender is not FrameworkElement element)
        {
            return;
        }

        _isResizing = false;
        _resizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        UpdateConfigBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        e.Handled = true;
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

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        bool isFeatureWidget = FeatureWidgetSettings.IsFeatureWidget(_config.WidgetKind);

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

        // Add delete widget option for all widget types
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
        _settingsService.UpdateWidget(_config);
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
        _settingsService.UpdateWidget(_config);
        ApplyLockActionIconState();
    }

    private void SetSizeLocked(bool value)
    {
        if (_config.IsSizeLocked == value)
        {
            return;
        }

        _config.IsSizeLocked = value;
        _settingsService.UpdateWidget(_config);
        ApplyLockActionIconState();
    }

    private void StartTitleRename()
    {
        if (_isDragging ||
            _isResizing ||
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
