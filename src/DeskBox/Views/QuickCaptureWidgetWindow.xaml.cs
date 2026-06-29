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
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow : Window, IDesktopWidgetWindow
{
    private readonly record struct WidgetAnimationProfile(
        double ShowOffsetX,
        double ShowOffsetY,
        double HideOffsetX,
        double HideOffsetY,
        float ShowStartOpacity,
        float HideEndOpacity,
        float ShowStartScale,
        float HideEndScale,
        int DurationMs,
        bool IsEnabled);

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private const int WidgetShowAnimationMs = 240;
    private const double MinWidgetSlideOffset = 1.0;
    private const double OffscreenSlidePadding = 16.0;
    private const float WidgetAnimationRestingOpacity = 1.0f;
    private const float WidgetAnimationSoftOpacity = 0.0f;
    private const float WidgetAnimationRestingScale = 1.0f;
    private const float WidgetAnimationSoftScale = 0.985f;
    private const long MaxDroppedTextFileBytes = 1024 * 1024;
    private const long MaxDroppedImageFileBytes = QuickCaptureClipboardService.MaxClipboardImageBytes;
    private const double QuickCaptureDialogHorizontalMargin = 24.0;
    private const double QuickCaptureDialogMinWidth = 176.0;
    private const double QuickCaptureDialogMaxWidth = 360.0;
    private const double QuickCaptureDialogMinButtonWidth = 76.0;
    private const double QuickCaptureDialogMaxButtonWidth = 112.0;
    private const int CopyToastMs = 900;
    private const int StatusToastDefaultMs = 1400;
    private const int StatusToastUndoMs = 4200;
    private static readonly string QuickCaptureTextPreviewDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "QuickCapture",
        "Preview");

    private Grid TitleBarGrid => QuickCaptureShell.TitleBar;
    private Border BackgroundPlate => QuickCaptureShell.BackgroundSurface;
    private Border HeaderDivider => QuickCaptureShell.Divider;
    private FontIcon TitleIcon => QuickCaptureShell.TitleIconElement;
    private TextBlock TitleText => QuickCaptureShell.TitleTextElement;
    private StackPanel RightActionButtons => QuickCaptureShell.RightActionButtonHost;
    private Button MoreButton => QuickCaptureShell.MoreActionButton;
    private Button CloseButton => QuickCaptureShell.CloseActionButton;
    private FontIcon MoreButtonIcon => QuickCaptureShell.MoreActionIcon;
    private FontIcon CloseButtonIcon => QuickCaptureShell.CloseActionIcon;

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly ThemeService? _themeService;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _hasMovedTitleBarDrag;
    private bool _isResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private Windows.Graphics.PointInt32 _initialWindowPos;
    private Windows.Graphics.SizeInt32 _initialWindowSize;
    private Windows.Graphics.PointInt32? _trayAnimationTargetPosition;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trayWindowAnimationTimer;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private bool _isHideAnimationRunning;
    private bool _isClearingData;
    private bool _isNativeBackdropSuppressedForTrayReveal;
    private long _trayAnimationGeneration;
    private double? _trayAnimationOffsetOverrideX;
    private double? _trayAnimationOffsetOverrideY;
    private bool _isApplyingTrayAnimationBounds;
    private QuickCaptureItemViewModel? _editingItem;
    private bool _isExpandingInput;
    private Microsoft.UI.Composition.Visual? _cachedRootVisual;
    private long _backdropRefreshGeneration;
    private long _statusToastGeneration;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoRestoreTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostSafetyTimer;
    private DateTime _lastElevateForInteractionUtc = DateTime.MinValue;
    private QuickCaptureDeletedItemSnapshot? _pendingDeletedItemSnapshot;

    private bool _hasTabBrushCache;
    private bool _cachedTabBrushesAreDark;
    private Windows.UI.Color _cachedTabBrushesAccentColor;
    private SolidColorBrush? _tabSelectionBackgroundBrush;
    private SolidColorBrush? _tabSelectionBorderBrush;
    private SolidColorBrush? _tabTransparentBrush;
    private SolidColorBrush? _tabNormalBackgroundBrush;
    private SolidColorBrush? _tabNormalBorderBrush;
    private SolidColorBrush? _tabNormalForegroundBrush;
    private SolidColorBrush? _tabSelectedForegroundBrush;

    private bool _hasItemBrushCache;
    private bool _cachedItemBrushesAreDark;
    private Windows.UI.Color _cachedItemBrushesAccentColor;
    private SolidColorBrush? _itemBackgroundBrush;
    private SolidColorBrush? _itemHoverBackgroundBrush;
    private SolidColorBrush? _itemPressedBackgroundBrush;
    private SolidColorBrush? _itemBorderBrush;
    private SolidColorBrush? _itemHoverBorderBrush;
    private SolidColorBrush? _itemPressedBorderBrush;
    private SolidColorBrush? _itemForegroundBrush;
    private SolidColorBrush? _itemHoverForegroundBrush;
    private SolidColorBrush? _itemDisabledBackgroundBrush;
    private SolidColorBrush? _itemDisabledBorderBrush;
    private SolidColorBrush? _itemDisabledForegroundBrush;

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => _hWnd;

    public Windows.Foundation.Rect AnimationBounds => new(
        ViewModel.Config.X,
        ViewModel.Config.Y,
        Math.Max(MinWidgetSlideOffset, ViewModel.Config.Width),
        Math.Max(MinWidgetSlideOffset, ViewModel.Config.Height));

    private bool _isVisibleOnDesktop;
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public QuickCaptureWidgetWindow(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _themeService = App.Current.ThemeService;
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindow();
        SetupEventHandlers();
        ApplyLocalizedText();
        ApplySurfaceStyle();
        ApplyTitleBarLayout();
    }

    public new void Activate()
    {
        base.Activate();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.SetWindowToBottom(_hWnd);
        App.Log($"[ZOrder] QuickCapture PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    public void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hWnd)
        {
            Win32Helper.BringWindowToFront(foreground);
        }
        App.Log($"[ZOrder] QuickCapture ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedAtDesktopLayer");
        PrepareTrayRevealHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        _appWindow.Show();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();
        PushToBottom();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        _trayAnimationOffsetOverrideX = offsetX;
        _trayAnimationOffsetOverrideY = offsetY;
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedRaisedFromTray");
        PrepareTrayRevealHiddenState();
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        HoldTemporaryTopMost();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);
            if (Visible)
            {
                HoldTemporaryTopMost();
            }
        });
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            LogTrayWindow("EnsureRaisedTopMost skipped reason=not-visible");
            return;
        }

        if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            LogTrayWindow("EnsureRaisedTopMost skipped reason=not-raised");
            return;
        }

        LogTrayWindow("EnsureRaisedTopMost");
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        Win32Helper.BringWindowToFront(_hWnd);
        HoldTemporaryTopMost();
        QueueBackdropRefresh();
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
    }

    public void PrepareTrayShowAnimation()
    {
        _trayAnimationGeneration++;
        StopTrayVisualAnimation();
        _isHideAnimationRunning = false;
        var profile = GetWidgetAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={_trayAnimationGeneration} effect={_settingsService.Settings.WidgetAnimationEffect} " +
            $"speed={_settingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        PrepareTrayVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        _trayAnimationGeneration++;
        LogTrayWindow($"CompleteShowWithoutAnimation gen={_trayAnimationGeneration}");
        StopTrayVisualAnimation();
        SetTrayAnimationOffsetOverride(null, null);
        RestoreNativeBackdropAfterTrayReveal();
        RestoreTrayVisualState();
        RestoreTrayWindowPosition();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || _isHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={_isHideAnimationRunning}");
            return false;
        }

        _trayAnimationGeneration++;
        StopTrayVisualAnimation();
        RestoreNativeBackdropAfterTrayReveal();
        _isHideAnimationRunning = true;
        Visible = false;
        ViewModel.Config.IsVisible = false;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={_trayAnimationGeneration}");
        PrepareTrayVisualState(0, 0, WidgetAnimationRestingOpacity, WidgetAnimationRestingScale);
        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void RevealFromTray(bool autoRestore = true)
    {
        PrepareTrayShowAnimation();
        ElevateForInteraction();
        PrepareTrayRevealHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
        PlayTrayRaiseAnimationAfterFirstFrame();

        if (!autoRestore)
        {
            return;
        }

        _autoRestoreTimer?.Stop();
        _autoRestoreTimer = DispatcherQueue.CreateTimer();
        _autoRestoreTimer.IsRepeating = false;
        _autoRestoreTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _autoRestoreTimer.Tick += (_, _) =>
        {
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer(force: true);
            }
        };
        _autoRestoreTimer.Start();
    }

    public void HideWindow()
    {
        if (!PrepareTrayHideAnimation())
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        ViewModel.ApplyAppearancePreview();
        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        QueueBackdropRefresh();
    }

    public void RestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayer(force: true);
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        _restoreDesktopLayerWhenIdle = true;
        _keepRaisedUntilDeactivate = false;
        RestoreDesktopLayer(force: true);
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
        Win32Helper.AllowShellDragDropMessages(_hWnd);

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
        ApplyWindowBounds(
            (int)ViewModel.Config.X,
            (int)ViewModel.Config.Y,
            (int)ViewModel.Config.Width,
            (int)ViewModel.Config.Height,
            persist: false);

        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            ApplySurfaceStyle();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            QueueBackdropRefresh();
            UpdateTabSelectionIndicator();
            RootGrid.Focus(FocusState.Programmatic);
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTabStyles();
                UpdateTabSelectionIndicator();
            });
        };
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            _hasTabBrushCache = false;
            _hasItemBrushCache = false;
            ApplyBackdropPreference();
        };
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        if (_themeService is not null)
        {
            _themeService.AppearanceChanged += OnThemeAppearanceChanged;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += QuickCaptureWidgetWindow_Activated;

        _appWindow.Changed += AppWindow_Changed;

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
            Visible = false;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            if (_themeService is not null)
            {
                _themeService.AppearanceChanged -= OnThemeAppearanceChanged;
            }

            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;

            StopTrayVisualAnimation();
            RestoreTrayVisualState();
            RestoreTrayWindowPosition();
            DisposeAcrylicController();

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

    private void ApplyLocalizedText()
    {
        string addInputText = _localizationService.T("QuickCapture.AddInput");
        string searchText = _localizationService.T("QuickCapture.SearchPlaceholder");
        string closeSearchText = _localizationService.T("QuickCapture.CloseSearch");
        string moreText = _localizationService.T("Widget.Tooltip.More");
        string closeText = _localizationService.T("Common.Off");

        InputTextBox.PlaceholderText = _localizationService.T("QuickCapture.InputPlaceholder");
        SearchTextBox.PlaceholderText = searchText;
        ToolTipService.SetToolTip(SearchButton, searchText);
        ToolTipService.SetToolTip(CloseSearchButton, closeSearchText);
        ToolTipService.SetToolTip(MoreButton, moreText);
        ToolTipService.SetToolTip(CloseButton, closeText);
        ToolTipService.SetToolTip(EditCloseButton, _localizationService.T("Common.Cancel"));
        AutomationProperties.SetName(InputTextBox, _localizationService.T("QuickCapture.InputPlaceholder"));
        AutomationProperties.SetName(SearchButton, searchText);
        AutomationProperties.SetName(SearchTextBox, searchText);
        AutomationProperties.SetName(CloseSearchButton, closeSearchText);
        AutomationProperties.SetName(MoreButton, moreText);
        AutomationProperties.SetName(CloseButton, closeText);
        AutomationProperties.SetName(EditCloseButton, _localizationService.T("Common.Cancel"));
        EditTitleText.Text = _localizationService.T("QuickCapture.Edit");
        EditCancelButton.Content = _localizationService.T("Common.Cancel");
        EditSaveButton.Content = _localizationService.T("Common.Save");
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingTrayAnimationBounds)
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.WidgetOpacity))
        {
            ApplyBackdropPreference();
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.SelectedView))
        {
            ApplyTabStyles();
            UpdateTabSelectionIndicator();
        }

        if (e.PropertyName is nameof(QuickCaptureWidgetViewModel.IconSize) or
            nameof(QuickCaptureWidgetViewModel.TextSize))
        {
            ApplyTitleBarLayout();
        }
    }

    private void OnSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        QueueBackdropRefresh();
    }

    private void OnThemeAppearanceChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnThemeAppearanceChanged);
            return;
        }

        _hasTabBrushCache = false;
        _hasItemBrushCache = false;
        ApplyBackdropPreference();
        QueueBackdropRefresh();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddInputAsync();
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        _isExpandingInput = true;
        _editingItem = null;
        EditTextBox.Text = InputTextBox.Text;
        EditTitleText.Text = _localizationService.T("QuickCapture.InputPlaceholder");
        EditOverlay.Visibility = Visibility.Visible;
        EditTextBox.Focus(FocusState.Programmatic);
        EditTextBox.Select(EditTextBox.Text.Length, 0);
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter ||
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) &
             Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.AddInputAsync();
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.CollapseSearch();
            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down) ||
            ItemsListView.Items.Count == 0)
        {
            return;
        }

        ItemsListView.SelectedIndex = 0;
        ItemsListView.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollapseSearch();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RecordsTabButton_Click(object sender, RoutedEventArgs e)
    {
        SelectView(QuickCaptureViewMode.Records);
    }

    private void PinnedTabButton_Click(object sender, RoutedEventArgs e)
    {
        SelectView(QuickCaptureViewMode.Pinned);
    }

    private void RecentTabButton_Click(object sender, RoutedEventArgs e)
    {
        SelectView(QuickCaptureViewMode.Recent);
    }

    private void RecordsTabButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SwitchTabOnHover(QuickCaptureViewMode.Records);
    }

    private void PinnedTabButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SwitchTabOnHover(QuickCaptureViewMode.Pinned);
    }

    private void RecentTabButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SwitchTabOnHover(QuickCaptureViewMode.Recent);
    }

    private void TabButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
    }

    private void SelectView(QuickCaptureViewMode view)
    {
        if (ViewModel.SelectedView == view)
        {
            ApplyTabStyles();
            UpdateTabSelectionIndicator();
            return;
        }

        ViewModel.SelectedView = view;
    }

    private void SwitchTabOnHover(QuickCaptureViewMode view)
    {
        if (ViewModel.SelectedView == view ||
            ViewModel.IsSearchExpanded)
        {
            return;
        }

        SelectView(view);
    }

    private async void EnableRecentCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (await QuickCaptureClipboardActivationHelper.EnableAsync(RootGrid.XamlRoot, _localizationService))
        {
            SelectView(QuickCaptureViewMode.Recent);
        }
    }

    private async void ItemsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        await OpenItemInDefaultAppAsync(item);
    }

    private async Task OpenItemInDefaultAppAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            await OpenImageInDefaultViewerAsync(item);
            return;
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }

            return;
        }

        if (item.IsRecent)
        {
            await OpenTextInDefaultEditorAsync(item);
            return;
        }

        await EditItemAsync(item);
    }

    private async Task OpenImageInDefaultViewerAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type != QuickCaptureItemType.Image ||
            string.IsNullOrWhiteSpace(item.ImagePath) ||
            !File.Exists(item.ImagePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open image preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
        }
    }

    private async Task OpenTextInNotepadAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return;
        }

        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{previewPath}\"",
                UseShellExecute = true
            });

            if (process is null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
                return;
            }

            await process.WaitForExitAsync();
            await TryApplyTextPreviewEditAsync(item, previewPath);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text in notepad: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task OpenTextInDefaultEditorAsync(QuickCaptureItemViewModel item)
    {
        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(previewPath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task TryApplyTextPreviewEditAsync(QuickCaptureItemViewModel item, string previewPath)
    {
        if (item.IsRecent)
        {
            return;
        }

        try
        {
            string editedBody = await File.ReadAllTextAsync(previewPath);
            if (string.Equals(editedBody, item.Body, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(editedBody))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
                return;
            }

            await ViewModel.EditItemAsync(item, editedBody);
            ShowStatusToast(_localizationService.T("QuickCapture.Edited"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to apply notepad edit: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task<string?> TryCreateTextPreviewFileAsync(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }

        try
        {
            Directory.CreateDirectory(QuickCaptureTextPreviewDirectory);
            string fileName = BuildQuickCapturePreviewFileName(item);
            string previewPath = Path.Combine(QuickCaptureTextPreviewDirectory, fileName);
            await File.WriteAllTextAsync(previewPath, item.Body);
            return previewPath;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to create text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }
    }

    private static string BuildQuickCapturePreviewFileName(QuickCaptureItemViewModel item)
    {
        string stem = item.Body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Quick Capture";
        stem = SanitizeQuickCapturePreviewFileName(stem);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Quick Capture";
        }

        if (stem.Length > 42)
        {
            stem = stem[..42].Trim();
        }

        return $"{stem}-{item.Id[..Math.Min(8, item.Id.Length)]}.txt";
    }

    private static string SanitizeQuickCapturePreviewFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, ' ');
        }

        return string.Join(" ", fileName.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private async void ItemsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemsListView.SelectedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            await DeleteItemWithUndoAsync(item);
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private void ItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
        {
            SetItemActionButtonsVisibleForItem(item, false);
        }

        foreach (var item in e.AddedItems)
        {
            SetItemActionButtonsVisibleForItem(item, true);
        }
    }

    private void TabHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTabSelectionIndicator();
    }

    private void QuickCaptureItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        var flyout = CreateItemFlyout(item);
        flyout.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    private async void QuickCaptureItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item ||
            e.OriginalSource is not DependencyObject source ||
            IsItemActionSource(source))
        {
            return;
        }

        e.Handled = true;
        await CopyItemWithFeedbackAsync(item);
    }

    private async Task CopyItemWithFeedbackAsync(QuickCaptureItemViewModel item)
    {
        try
        {
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy item {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async void QuickCaptureItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement element || element.DataContext is not QuickCaptureItemViewModel item)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            if (!await TryPrepareQuickCaptureDragPackageAsync(args.Data, item))
            {
                args.Cancel = true;
                return;
            }

            args.AllowedOperations = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to start drag: {ex}");
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void QuickCaptureItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        SetItemActionButtonsVisible(sender, false);
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        if (!_hasItemBrushCache ||
            _cachedItemBrushesAreDark != isDark ||
            _cachedItemBrushesAccentColor != accentColor)
        {
            RecomputeItemBrushCache(isDark, accentColor);
            _hasItemBrushCache = true;
            _cachedItemBrushesAreDark = isDark;
            _cachedItemBrushesAccentColor = accentColor;
        }

        ApplyItemActionButtonStyle(sender, (isDark, accentColor));
        ApplyItemSearchHighlight(sender, item);

        if (FindVisualChild<Button>(sender, "SaveRecentItemButton") is { } saveButton)
        {
            string saveToRecordsText = _localizationService.T("QuickCapture.SaveToRecords");
            saveButton.Visibility = item.IsRecent ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(saveButton, saveToRecordsText);
            AutomationProperties.SetName(saveButton, saveToRecordsText);
        }

        if (FindPinnedButton(sender) is { } pinButton)
        {
            string pinText = item.IsRecent
                ? _localizationService.T("QuickCapture.PinToRecords")
                : item.PinTooltip;
            ToolTipService.SetToolTip(pinButton, pinText);
            AutomationProperties.SetName(pinButton, pinText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemUpButton") is { } moveUpButton)
        {
            string moveUpText = _localizationService.T("QuickCapture.MoveUp");
            ToolTipService.SetToolTip(moveUpButton, moveUpText);
            AutomationProperties.SetName(moveUpButton, moveUpText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemDownButton") is { } moveDownButton)
        {
            string moveDownText = _localizationService.T("QuickCapture.MoveDown");
            ToolTipService.SetToolTip(moveDownButton, moveDownText);
            AutomationProperties.SetName(moveDownButton, moveDownText);
        }

        if (FindVisualChild<Button>(sender, "DeleteItemButton") is { } deleteButton)
        {
            string deleteText = _localizationService.T("Common.Delete");
            ToolTipService.SetToolTip(deleteButton, deleteText);
            AutomationProperties.SetName(deleteButton, deleteText);
        }
    }

    private static void ApplyItemSearchHighlight(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<TextBlock>(itemRoot, "ItemTextBlock") is not { } textBlock)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (item.HighlightStartIndex < 0 || item.HighlightLength <= 0)
        {
            return;
        }

        var highlighter = new TextHighlighter
        {
            Background = GetBrushResourceOrFallback(
                "SystemAccentColorLight2Brush",
                WithAlpha(
                    App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor,
                    0x44)),
            Foreground = GetBrushResourceOrFallback(
                "TextFillColorPrimaryBrush",
                textBlock.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black)
        };
        highlighter.Ranges.Add(new TextRange
        {
            StartIndex = item.HighlightStartIndex,
            Length = item.HighlightLength
        });
        textBlock.TextHighlighters.Add(highlighter);
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, true);
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, false);
        SetItemHoverState(sender as DependencyObject, false);
    }

    private static void SetItemActionButtonsVisible(DependencyObject? itemRoot, bool isVisible)
    {
        if (itemRoot is null ||
            FindVisualChild<Grid>(itemRoot, "ItemActionHost") is not { } actions)
        {
            return;
        }

        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
        ElementCompositionPreview.GetElementVisual(actions).StopAnimation("Offset");
    }

    private static void SetItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "ItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "ImagePreviewBorder") is { } imageBorder)
        {
            imageBorder.BorderBrush = isHovered
                ? new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xE0 : (byte)0xCC))
                : GetBrushResourceOrFallback(
                    "CardStrokeColorDefaultBrush",
                    isDark
                        ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                        : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
    }

    private void SetItemActionButtonsVisibleForItem(object? item, bool isVisible)
    {
        if (item is null)
        {
            return;
        }

        if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
        {
            SetItemActionButtonsVisible(container, isVisible);
            return;
        }

        if (isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ItemsListView.ContainerFromItem(item) is DependencyObject queuedContainer)
                {
                    SetItemActionButtonsVisible(queuedContainer, true);
                }
            });
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            if (item.IsRecent)
            {
                await ViewModel.PinRecentItemAsync(item);
            }
            else
            {
                await ViewModel.TogglePinnedAsync(item);
            }
        }
    }

    private async void SaveRecentItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.SaveRecentItemAsync(item);
        }
    }

    private async void MovePinnedItemUpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, -1);
        }
    }

    private async void MovePinnedItemDownButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, 1);
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await DeleteItemWithUndoAsync(item);
        }
    }

    private async Task DeleteItemWithUndoAsync(QuickCaptureItemViewModel item)
    {
        var snapshot = await ViewModel.DeleteItemAsync(item);
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = snapshot;
        ShowStatusToast(
            _localizationService.T("QuickCapture.Deleted"),
            _localizationService.T("Common.Undo"),
            StatusToastUndoMs);
    }

    private async Task<bool> TryPrepareQuickCaptureDragPackageAsync(DataPackage dataPackage, QuickCaptureItemViewModel item)
    {
        dataPackage.RequestedOperation = DataPackageOperation.Copy;

        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            try
            {
                string? exportPath = await ViewModel.CreateImageExportFileAsync(
                    item,
                    _localizationService.T("QuickCapture.ImageExportFileNamePrefix"));
                if (string.IsNullOrWhiteSpace(exportPath))
                {
                    return false;
                }

                var file = await StorageFile.GetFileFromPathAsync(exportPath);
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                dataPackage.SetStorageItems([file]);
                dataPackage.Properties.Title = Path.GetFileName(exportPath);
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureWidget] Failed to prepare image drag package: {ex}");
                return false;
            }
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            dataPackage.SetText(item.Body);
            dataPackage.SetWebLink(uri);
            dataPackage.SetUri(uri);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            dataPackage.SetText(item.Body);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        return false;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFlyoutWithElevation(CreateMoreFlyout(), MoreButton);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, e.GetPosition(TitleBarGrid));
        e.Handled = true;
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockPosition"),
            "\uE72E",
            ViewModel.Config.IsPositionLocked,
            value => ViewModel.SetPositionLocked(value)));
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockSize"),
            "\uE740",
            ViewModel.Config.IsSizeLocked,
            value => ViewModel.SetSizeLocked(value)));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var clearItem = new MenuFlyoutItem
        {
            Text = ViewModel.IsRecentView
                ? _localizationService.T("QuickCapture.ClearRecent")
                : _localizationService.T("QuickCapture.ClearData"),
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearItem.Click += async (_, _) =>
        {
            if (ViewModel.IsRecentView)
            {
                await ConfirmClearRecentAsync();
            }
            else
            {
                await ConfirmClearDataAsync();
            }
        };
        flyout.Items.Add(clearItem);

        var settingsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("QuickCapture.OpenSettings"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += (_, _) => App.Current.ShowSettings();
        flyout.Items.Add(settingsItem);

        return flyout;
    }

    private MenuFlyout CreateItemFlyout(QuickCaptureItemViewModel item)
    {
        var flyout = new MenuFlyout();

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) =>
        {
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);
        if (CreateSaveToLastFileWidgetItem(item) is { } saveToLastItem)
        {
            flyout.Items.Add(saveToLastItem);
        }

        flyout.Items.Add(CreateSaveToFileWidgetSubItem(item));

        if (item.IsRecent)
        {
            var saveItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.SaveToRecords"),
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveItem.Click += async (_, _) => await ViewModel.SaveRecentItemAsync(item);
            flyout.Items.Add(saveItem);

            var pinRecentItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.PinToRecords"),
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            pinRecentItem.Click += async (_, _) => await ViewModel.PinRecentItemAsync(item);
            flyout.Items.Add(pinRecentItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteRecentItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Common.Delete"),
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteRecentItem.Click += async (_, _) => await DeleteItemWithUndoAsync(item);
            flyout.Items.Add(deleteRecentItem);
            return flyout;
        }

        if (item.Type != QuickCaptureItemType.Image)
        {
            var editItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.Edit"),
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            editItem.Click += async (_, _) => await EditItemAsync(item);
            flyout.Items.Add(editItem);

            var notepadItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.EditInNotepad"),
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            notepadItem.Click += async (_, _) => await OpenTextInNotepadAsync(item);
            flyout.Items.Add(notepadItem);
        }

        var pinItem = new MenuFlyoutItem
        {
            Text = item.IsPinned ? _localizationService.T("QuickCapture.Unpin") : _localizationService.T("QuickCapture.Pin"),
            Icon = new FontIcon { Glyph = item.IsPinned ? "\uE840" : "\uE718" }
        };
        pinItem.Click += async (_, _) => await ViewModel.TogglePinnedAsync(item);
        flyout.Items.Add(pinItem);

        if (ViewModel.IsPinnedView && !ViewModel.HasSearchText)
        {
            var moveUpItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.MoveUp"),
                Icon = new FontIcon { Glyph = "\uE70E" },
                IsEnabled = item.CanMovePinnedUp
            };
            moveUpItem.Click += async (_, _) => await ViewModel.MovePinnedItemAsync(item, -1);
            flyout.Items.Add(moveUpItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.MoveDown"),
                Icon = new FontIcon { Glyph = "\uE70D" },
                IsEnabled = item.CanMovePinnedDown
            };
            moveDownItem.Click += async (_, _) => await ViewModel.MovePinnedItemAsync(item, 1);
            flyout.Items.Add(moveDownItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteItemWithUndoAsync(item);
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyoutSubItem CreateSaveToFileWidgetSubItem(QuickCaptureItemViewModel item)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.SaveToFileWidget"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        var targets = App.Current.WidgetManager?.GetQuickCaptureFileWidgetTargets() ?? [];
        if (targets.Count == 0)
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.NoFileWidgetTargets"),
                IsEnabled = false
            });
            return subItem;
        }

        foreach (var target in targets)
        {
            var targetItem = new MenuFlyoutItem
            {
                Text = target.Name,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            targetItem.Click += async (_, _) =>
            {
                await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
            };
            subItem.Items.Add(targetItem);
        }

        return subItem;
    }

    private MenuFlyoutItem? CreateSaveToLastFileWidgetItem(QuickCaptureItemViewModel item)
    {
        var target = App.Current.WidgetManager?.GetLastQuickCaptureFileWidgetTarget();
        if (target is null)
        {
            return null;
        }

        var menuItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("QuickCapture.SaveToLastFileWidget", target.Name),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        menuItem.Click += async (_, _) => await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
        return menuItem;
    }

    private async Task SaveQuickCaptureItemToFileWidgetAsync(QuickCaptureItemViewModel item, string targetWidgetId)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        string? savedPath = await App.Current.WidgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            item.ToModel(),
            targetWidgetId,
            _localizationService.T("QuickCapture.ImageExportFileNamePrefix"));
        ShowStatusToast(string.IsNullOrWhiteSpace(savedPath)
            ? _localizationService.T("QuickCapture.SaveToFileWidgetFailed")
            : _localizationService.T("QuickCapture.SavedToFileWidget"));
    }

    private Task EditItemAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            return Task.CompletedTask;
        }

        _editingItem = item;
        EditTextBox.Text = item.Body;
        EditOverlay.Visibility = Visibility.Visible;
        EditTextBox.Focus(FocusState.Programmatic);
        EditTextBox.Select(EditTextBox.Text.Length, 0);
        return Task.CompletedTask;
    }

    private async void EditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveInlineEditAsync();
    }

    private void EditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseInlineEdit();
    }

    private async void EditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseInlineEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            await SaveInlineEditAsync();
            e.Handled = true;
        }
    }

    private async Task SaveInlineEditAsync()
    {
        string body = EditTextBox.Text;
        if (string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        if (_isExpandingInput)
        {
            InputTextBox.Text = body;
            await ViewModel.AddInputAsync();
            CloseInlineEdit();
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseInlineEdit();
            return;
        }

        await ViewModel.EditItemAsync(item, body);
        CloseInlineEdit();
    }

    private void CloseInlineEdit()
    {
        _editingItem = null;
        _isExpandingInput = false;
        EditOverlay.Visibility = Visibility.Collapsed;
        EditTextBox.Text = string.Empty;
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async Task ConfirmClearDataAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    ViewModel.RecordCount,
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ConfirmClearRecentAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearRecentAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private double GetQuickCaptureDialogWidth()
    {
        double rootWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = ViewModel.Config.Width;
        }

        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = SettingsService.DefaultWidgetWidth;
        }

        return Math.Clamp(
            rootWidth - QuickCaptureDialogHorizontalMargin,
            QuickCaptureDialogMinWidth,
            QuickCaptureDialogMaxWidth);
    }

    private void ApplyQuickCaptureDialogSizing(ContentDialog dialog, double? dialogWidth = null)
    {
        double compactWidth = dialogWidth ?? GetQuickCaptureDialogWidth();
        double buttonMinWidth = Math.Clamp(
            (compactWidth - 24) / 2,
            QuickCaptureDialogMinButtonWidth,
            QuickCaptureDialogMaxButtonWidth);

        dialog.Resources["ContentDialogMinWidth"] = compactWidth;
        dialog.Resources["ContentDialogMaxWidth"] = compactWidth;
        dialog.Resources["ContentDialogButtonMinWidth"] = buttonMinWidth;
    }

    private MenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
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

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.SetQuickCaptureEnabledAsync(false, reveal: false);
        }
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        ElevateForInteraction();
        flyout.Closed += (_, _) => ReleaseInteractionLayer("quick-flyout-closed");

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private void ReleaseInteractionLayer(string reason)
    {
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsItemActionSource(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button ||
                current is MenuFlyoutItem ||
                (current is FrameworkElement { Name: "ItemActionHost" }))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (ViewModel.IsSearchExpanded)
            {
                ViewModel.CollapseSearch();
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(InputTextBox.Text))
            {
                InputTextBox.Text = string.Empty;
            }

            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                e.AcceptedOperation = await HasSupportedQuickCaptureStorageDropAsync(e.DataView)
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
            }
            finally
            {
                deferral.Complete();
            }

            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else if (HasFallbackFileFormats(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private static bool HasFallbackFileFormats(DataPackageView dataView)
    {
        return dataView.AvailableFormats.Count > 0;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var content = await TryReadDroppedContentAsync(e.DataView);
            if (content.Texts.Count == 0 && content.ImagePaths.Count == 0)
            {
                if (content.SkippedCount > 0)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.DroppedAllSkipped"));
                }

                return;
            }

            foreach (string text in content.Texts)
            {
                await ViewModel.AddTextAsync(text);
            }

            foreach (string imagePath in content.ImagePaths)
            {
                await ViewModel.AddImageFileAsync(imagePath);
            }

            ShowStatusToast(content.SkippedCount > 0
                ? _localizationService.T("QuickCapture.DroppedWithSkipped")
                : _localizationService.T("QuickCapture.Dropped"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_settingsService.Settings.ShowHoverButtons)
        {
            return;
        }

        RightActionButtons.Opacity = 1;
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging || _isResizing)
        {
            return;
        }

        RightActionButtons.Opacity = 0;
    }

    private void ShowCopyToast()
    {
        ShowStatusToast(_localizationService.T("QuickCapture.Copied"), durationMs: CopyToastMs);
    }

    private void ShowStatusToast(string text, string? actionText = null, int durationMs = StatusToastDefaultMs)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatusToast(text, actionText, durationMs));
            return;
        }

        long generation = ++_statusToastGeneration;
        StatusToast.MaxWidth = Math.Max(120, _appWindow.Size.Width - 24);
        StatusToastText.Text = text;
        StatusToastActionButton.Content = actionText;
        StatusToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        StatusToast.Opacity = 1;
        _ = HideStatusToastAfterDelayAsync(generation, durationMs);
    }

    private async Task HideStatusToastAfterDelayAsync(long generation, int durationMs)
    {
        await Task.Delay(durationMs);
        if (generation != _statusToastGeneration)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => HideStatusToastIfCurrent(generation));
            return;
        }

        HideStatusToastIfCurrent(generation);
    }

    private void HideStatusToastIfCurrent(long generation)
    {
        if (generation == _statusToastGeneration)
        {
            StatusToast.Opacity = 0;
            StatusToast.IsHitTestVisible = false;
            StatusToastActionButton.Visibility = Visibility.Collapsed;
            _pendingDeletedItemSnapshot = null;
        }
    }

    private async void StatusToastActionButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _pendingDeletedItemSnapshot;
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = null;
        if (await ViewModel.RestoreDeletedItemAsync(snapshot))
        {
            ShowStatusToast(_localizationService.T("Common.Undone"));
        }
    }

    private static async Task<QuickCaptureDropContent> TryReadDroppedContentAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            HasFallbackFileFormats(dataView))
        {
            return await TryReadDroppedStorageContentAsync(dataView);
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return new QuickCaptureDropContent([link.AbsoluteUri], [], 0);
            }
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length > QuickCaptureClipboardService.MaxClipboardTextCharacters
                    ? new QuickCaptureDropContent([], [], 1)
                    : new QuickCaptureDropContent([text], [], 0);
            }
        }

        return QuickCaptureDropContent.Empty;
    }

    private static async Task<QuickCaptureDropContent> TryReadDroppedStorageContentAsync(DataPackageView dataView)
    {
        var texts = new List<string>();
        var imagePaths = new List<string>();
        int skippedCount = 0;

        try
        {
            var storageItems = await dataView.GetStorageItemsAsync();
            foreach (var storageItem in storageItems)
            {
                if (storageItem is not StorageFile file ||
                    string.IsNullOrWhiteSpace(file.Path))
                {
                    skippedCount++;
                    continue;
                }

                if (IsImageFile(file.Path))
                {
                    if (IsFileWithinSizeLimit(file.Path, MaxDroppedImageFileBytes))
                    {
                        imagePaths.Add(file.Path);
                    }
                    else
                    {
                        skippedCount++;
                    }

                    continue;
                }

                if (IsTextFile(file.Path))
                {
                    if (!IsFileWithinSizeLimit(file.Path, MaxDroppedTextFileBytes))
                    {
                        skippedCount++;
                        continue;
                    }

                    string text = await FileIO.ReadTextAsync(file);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters)
                        {
                            texts.Add(text);
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }

                    continue;
                }

                skippedCount++;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to read dropped storage content: {ex}");
        }

        return new QuickCaptureDropContent(texts, imagePaths, skippedCount);
    }

    private static async Task<bool> HasSupportedQuickCaptureStorageDropAsync(DataPackageView dataView)
    {
        try
        {
            var storageItems = await dataView.GetStorageItemsAsync();
            return storageItems.Any(storageItem =>
                storageItem is StorageFile file &&
                !string.IsNullOrWhiteSpace(file.Path) &&
                ((IsImageFile(file.Path) && IsFileWithinSizeLimit(file.Path, MaxDroppedImageFileBytes)) ||
                 (IsTextFile(file.Path) && IsFileWithinSizeLimit(file.Path, MaxDroppedTextFileBytes))));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to inspect dropped storage content: {ex}");
            return false;
        }
    }

    private static bool IsImageFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileWithinSizeLimit(string path, long maxBytes)
    {
        try
        {
            return new FileInfo(path).Length <= maxBytes;
        }
        catch
        {
            return false;
        }
    }

    private sealed record QuickCaptureDropContent(
        IReadOnlyList<string> Texts,
        IReadOnlyList<string> ImagePaths,
        int SkippedCount)
    {
        public static QuickCaptureDropContent Empty { get; } = new([], [], 0);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.IsPositionLocked)
        {
            return;
        }

        var properties = e.GetCurrentPoint(TitleBarGrid).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        TitleBarGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;

        if (Math.Abs(deltaX) > 2 || Math.Abs(deltaY) > 2)
        {
            _hasMovedTitleBarDrag = true;
        }

        ApplyWindowBounds(
            _initialWindowPos.X + deltaX,
            _initialWindowPos.Y + deltaY,
            _initialWindowSize.Width,
            _initialWindowSize.Height,
            persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        TitleBarGrid.ReleasePointerCapture(e.Pointer);
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: true);
        if (!_hasMovedTitleBarDrag)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }

        e.Handled = true;
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.IsSizeLocked || sender is not FrameworkElement element)
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
        ElevateForInteraction();
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

        if (_resizeDirection.Contains("Right", StringComparison.Ordinal))
        {
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left", StringComparison.Ordinal))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom", StringComparison.Ordinal))
        {
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top", StringComparison.Ordinal))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height - deltaY);
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
        element.ReleasePointerCapture(e.Pointer);
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: true);
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        string tag = element.Tag as string ?? string.Empty;
        var shape = tag switch
        {
            "Left" or "Right" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            "Top" or "Bottom" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            "TopLeft" or "BottomRight" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            "TopRight" or "BottomLeft" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow
        };
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
    }

    private void QuickCaptureWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ApplyBackdropPreference());

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] QuickCapture Deactivated→QueueRestore hwnd=0x{_hWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }

            return;
        }

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing ||
            (App.Current.WidgetManager is { WidgetsRaisedFromTray: true }))
        {
            App.Log($"[ZOrder] QuickCapture PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] QuickCapture PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible || _isAtDesktopLayer)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            bool foregroundIsWidget = App.Current.WidgetManager is { } wm && wm.IsWidgetWindow(foregroundWindow);
            if (foregroundIsWidget)
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("quick-window-deactivated"))
                {
                    RestoreDesktopLayer(force: true);
                }
            }
            else
            {
                RestoreDesktopLayer(force: true);
            }
        });
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer SKIPPED guard1 force={force} idle={_restoreDesktopLayerWhenIdle} keep={_keepRaisedUntilDeactivate}");
            return;
        }

        if (!force && (_isDragging || _isResizing))
        {
            App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer SKIPPED guard2 force={force} drag={_isDragging} resize={_isResizing}");
            if (force || _restoreDesktopLayerWhenIdle)
            {
                _restoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer EXECUTING force={force}");
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
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
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void HoldTemporaryTopMost()
    {
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        Win32Helper.SetWindowTopMost(_hWnd);
        App.Log($"[ZOrder] QuickCapture HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    private void StartTopMostSafetyTimer()
    {
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = DispatcherQueue.CreateTimer();
        _topMostSafetyTimer.IsRepeating = false;
        _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
        _topMostSafetyTimer.Tick += (_, _) =>
        {
            _topMostSafetyTimer?.Stop();
            _topMostSafetyTimer = null;
            if (!_isAtDesktopLayer && !_isDragging && !_isResizing &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] QuickCapture safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
        };
        _topMostSafetyTimer.Start();
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist)
    {
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        ViewModel.UpdateBounds(x, y, width, height, persist: false);
        if (persist)
        {
            ViewModel.UpdateBounds(x, y, width, height, persist: true);
        }
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
        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            ApplySurfaceStyle();
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        Win32Helper.SetWindowTheme(_hWnd, isDark);

        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);

        try
        {
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            RootGrid.Background = new SolidColorBrush(Colors.Transparent);

            int backdropType;
            if (ApplyAcrylicController(isDark, tintColor, surfaceOpacity))
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                SystemBackdrop ??= new DesktopAcrylicBackdrop();
            }

            Win32Helper.DisableAccentPolicy(_hWnd);
        }
        catch (Exception ex)
        {
            App.Log($"QuickCapture ApplyBackdropPreference fallback: {ex}");
            SystemBackdrop = null;
            DisposeAcrylicController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
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
            SystemBackdrop = null;
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
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        double tintOpacity = isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44);
        double luminosityOpacity = isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86);
        _acrylicController.TintOpacity = (float)tintOpacity;
        _acrylicController.LuminosityOpacity = (float)luminosityOpacity;
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
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;

        var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
        byte chromeAlpha = 0x18;
        var borderColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.75), 0, 255), 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), chromeAlpha);
        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);
        var secondaryForeground = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.Background = new SolidColorBrush(surfaceColor);
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        TitleIcon.Foreground = new SolidColorBrush(iconForeground);
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryForeground);
        ApplySearchVisualStyle(isDark, accentColor);
        ApplyEditOverlayStyle(isDark, accentColor);
        ApplyTabStyles(isDark, accentColor);
        ApplyItemActionButtonStyleToVisibleItems(isDark, accentColor);
    }

    private void ApplyTabStyles()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        ApplyTabStyles(isDark, accentColor);
    }

    private void ApplyTabStyles(bool isDark, Windows.UI.Color accentColor)
    {
        if (!_hasTabBrushCache ||
            _cachedTabBrushesAreDark != isDark ||
            _cachedTabBrushesAccentColor != accentColor)
        {
            RecomputeTabBrushCache(isDark, accentColor);
            _hasTabBrushCache = true;
            _cachedTabBrushesAreDark = isDark;
            _cachedTabBrushesAccentColor = accentColor;
        }

        ReapplyCachedTabBrushes();
    }

    private void RecomputeTabBrushCache(bool isDark, Windows.UI.Color accentColor)
    {
        var indicatorBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            isDark ? (byte)0xC8 : (byte)0xD8);
        var indicatorBorder = WithAlpha(accentColor, isDark ? (byte)0xE8 : (byte)0xD8);

        _tabSelectionBackgroundBrush = new SolidColorBrush(indicatorBackground);
        _tabSelectionBorderBrush = new SolidColorBrush(indicatorBorder);

        var normalBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2A, 0x2D, 0x33)
                    : ColorHelper.FromArgb(0xFF, 0xFB, 0xFB, 0xFC),
                accentMix: isDark ? 0.12 : 0.08,
                overlayMix: isDark ? 0.12 : 0.18),
            isDark ? (byte)0x28 : (byte)0x30);
        var normalBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);
        var normalFg = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);
        var selectedFg = isDark
            ? Colors.White
            : BlendColors(ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A), accentColor, 0.18);

        _tabTransparentBrush = new SolidColorBrush(Colors.Transparent);
        _tabNormalBackgroundBrush = new SolidColorBrush(normalBackground);
        _tabNormalBorderBrush = new SolidColorBrush(normalBorder);
        _tabNormalForegroundBrush = new SolidColorBrush(normalFg);
        _tabSelectedForegroundBrush = new SolidColorBrush(selectedFg);
    }

    private void ReapplyCachedTabBrushes()
    {
        TabSelectionIndicator.Background = _tabSelectionBackgroundBrush!;
        TabSelectionIndicator.BorderBrush = _tabSelectionBorderBrush!;

        ApplyCachedTabButtonBrushes(RecordsTabButton, ViewModel.IsRecordsView);
        ApplyCachedTabButtonBrushes(PinnedTabButton, ViewModel.IsPinnedView);
        ApplyCachedTabButtonBrushes(RecentTabButton, ViewModel.IsRecentView);
    }

    private void ApplyCachedTabButtonBrushes(Button button, bool isSelected)
    {
        var bg = isSelected ? _tabTransparentBrush! : _tabNormalBackgroundBrush!;
        var border = isSelected ? _tabTransparentBrush! : _tabNormalBorderBrush!;
        var fg = isSelected ? _tabSelectedForegroundBrush! : _tabNormalForegroundBrush!;

        button.Background = bg;
        button.BorderBrush = border;
        button.Foreground = fg;

        button.Resources["ButtonBackground"] = bg;
        button.Resources["ButtonBackgroundPointerOver"] = bg;
        button.Resources["ButtonBackgroundPressed"] = bg;
        button.Resources["ButtonBackgroundDisabled"] = bg;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonBorderBrushDisabled"] = border;
        button.Resources["ButtonForeground"] = fg;
        button.Resources["ButtonForegroundPointerOver"] = fg;
        button.Resources["ButtonForegroundPressed"] = fg;
        button.Resources["ButtonForegroundDisabled"] = fg;
    }

    private void ApplyTitleBarLayout()
    {
        double titleIconSize = ViewModel.TitleIconSize;
        TitleIcon.FontSize = titleIconSize;

        double titleTextSize = ViewModel.TitleTextSize;
        TitleText.FontSize = titleTextSize;

        double btnSize = Math.Clamp(titleIconSize + 14, 24, 34);
        MoreButton.Width = btnSize;
        MoreButton.Height = btnSize;
        MoreButton.MinWidth = btnSize;
        CloseButton.Width = btnSize;
        CloseButton.Height = btnSize;
        CloseButton.MinWidth = btnSize;

        double btnIconSize = Math.Clamp(titleIconSize - 3, 10, 15);
        MoreButtonIcon.FontSize = btnIconSize;
        CloseButtonIcon.FontSize = btnIconSize;

        double rowHeight = Math.Clamp(titleIconSize + 32, 40, 54);
        var titleRowHeight = new GridLength(rowHeight);
        RootGrid.RowDefinitions[0].Height = titleRowHeight;
        QuickCaptureShell.SetTitleBarRowHeight(titleRowHeight);

        double padH = Math.Clamp(Math.Round(titleIconSize * 0.9), 10, 16);
        double padT = Math.Clamp(Math.Round(titleIconSize * 0.5), 4, 10);
        double padB = Math.Clamp(Math.Round(titleIconSize * 0.35), 3, 8);
        TitleBarGrid.Padding = new Thickness(padH, padT, padH - 2, padB);
    }

    private void ApplySearchVisualStyle(bool isDark, Windows.UI.Color accentColor)
    {
        var background = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark ? ColorHelper.FromArgb(0xFF, 0x24, 0x27, 0x2D) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: ViewModel.IsSearchExpanded ? (isDark ? 0.20 : 0.10) : 0.0,
            overlayMix: isDark ? 0.05 : 0.0);

        SearchTextBox.Background = new SolidColorBrush(WithAlpha(background, isDark ? (byte)0xE8 : (byte)0xF6));
        SearchTextBox.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xCC : (byte)0xAA));
    }

    private void ApplyEditOverlayStyle(bool isDark, Windows.UI.Color accentColor)
    {
        var overlayBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x1F, 0x23, 0x29)
                : ColorHelper.FromArgb(0xFF, 0xFB, 0xFD, 0xFF),
            accentMix: isDark ? 0.20 : 0.10,
            overlayMix: isDark ? 0.06 : 0.02);
        var inputBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x16, 0x19, 0x1E)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.10 : 0.04,
            overlayMix: isDark ? 0.03 : 0.0);

        EditOverlay.Background = new SolidColorBrush(WithAlpha(overlayBackground, isDark ? (byte)0xFA : (byte)0xFE));
        EditOverlay.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xB8 : (byte)0x92));
        EditOverlay.BorderThickness = new Thickness(1.2);
        EditOverlay.Translation = new Vector3(0, 0, 16);

        EditTextBox.Background = new SolidColorBrush(WithAlpha(inputBackground, isDark ? (byte)0xFF : (byte)0xFE));
        EditTextBox.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x7E : (byte)0x66));
        EditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);

        var buttonBackground = new SolidColorBrush(WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x27, 0x2B, 0x32) : ColorHelper.FromArgb(0xFF, 0xF7, 0xFA, 0xFF),
                accentMix: isDark ? 0.24 : 0.16,
                overlayMix: isDark ? 0.04 : 0.02),
            0xFF));
        var buttonBorder = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x92 : (byte)0x76));
        EditCancelButton.Background = buttonBackground;
        EditCancelButton.BorderBrush = buttonBorder;
        EditSaveButton.Background = buttonBackground;
        EditSaveButton.BorderBrush = buttonBorder;
    }

    private void ApplyItemActionButtonStyleToVisibleItems(bool isDark, Windows.UI.Color accentColor)
    {
        if (!_hasItemBrushCache ||
            _cachedItemBrushesAreDark != isDark ||
            _cachedItemBrushesAccentColor != accentColor)
        {
            RecomputeItemBrushCache(isDark, accentColor);
            _hasItemBrushCache = true;
            _cachedItemBrushesAreDark = isDark;
            _cachedItemBrushesAccentColor = accentColor;
        }

        var stamp = (isDark, accentColor);
        foreach (var item in ItemsListView.Items)
        {
            if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
            {
                ApplyItemActionButtonStyle(container, stamp);
            }
        }
    }

    private void ApplyItemActionButtonStyle(DependencyObject itemRoot, (bool isDark, Windows.UI.Color accentColor) stamp)
    {
        if (itemRoot is FrameworkElement fe &&
            fe.Tag is (bool tagDark, Windows.UI.Color tagColor) &&
            tagDark == stamp.isDark && tagColor == stamp.accentColor)
        {
            return;
        }

        if (FindVisualChild<StackPanel>(itemRoot, "ItemActionButtons") is not { } actions)
        {
            return;
        }

        foreach (var button in FindVisualChildren<Button>(actions))
        {
            ApplyButtonStateBrushes(
                button,
                _itemBackgroundBrush!,
                _itemHoverBackgroundBrush!,
                _itemPressedBackgroundBrush!,
                _itemBorderBrush!,
                _itemHoverBorderBrush!,
                _itemPressedBorderBrush!,
                _itemForegroundBrush!,
                _itemHoverForegroundBrush!,
                _itemHoverForegroundBrush!,
                _itemDisabledBackgroundBrush!,
                _itemDisabledBorderBrush!,
                _itemDisabledForegroundBrush!);
        }

        if (itemRoot is FrameworkElement fe2)
        {
            fe2.Tag = stamp;
        }
    }

    private void RecomputeItemBrushCache(bool isDark, Windows.UI.Color accentColor)
    {
        var background = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x24, 0x27, 0x2D)
                : ColorHelper.FromArgb(0xFF, 0xFB, 0xFD, 0xFF),
            accentMix: isDark ? 0.30 : 0.18,
            overlayMix: isDark ? 0.04 : 0.02);
        var hoverBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x2B, 0x2F, 0x36)
                : ColorHelper.FromArgb(0xFF, 0xF8, 0xFB, 0xFF),
            accentMix: isDark ? 0.38 : 0.24,
            overlayMix: isDark ? 0.04 : 0.02);
        var normalForeground = isDark
            ? ColorHelper.FromArgb(0xF0, 0xF4, 0xF8, 0xFF)
            : BlendColors(ColorHelper.FromArgb(0xFF, 0x18, 0x1C, 0x22), accentColor, 0.12);
        var accentForeground = ColorHelper.FromArgb(0xFF, accentColor.R, accentColor.G, accentColor.B);

        _itemBackgroundBrush = new SolidColorBrush(WithAlpha(background, isDark ? (byte)0xF4 : (byte)0xFC));
        _itemHoverBackgroundBrush = new SolidColorBrush(WithAlpha(hoverBackground, 0xFF));
        _itemPressedBackgroundBrush = new SolidColorBrush(WithAlpha(hoverBackground, 0xFF));
        _itemBorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x72 : (byte)0x5C));
        _itemHoverBorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xE4));
        _itemPressedBorderBrush = new SolidColorBrush(WithAlpha(accentColor, 0xFF));
        _itemForegroundBrush = new SolidColorBrush(normalForeground);
        _itemHoverForegroundBrush = new SolidColorBrush(accentForeground);
        _itemDisabledBackgroundBrush = new SolidColorBrush(WithAlpha(background, isDark ? (byte)0xC8 : (byte)0xD6));
        _itemDisabledBorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x34 : (byte)0x28));
        _itemDisabledForegroundBrush = new SolidColorBrush(isDark
            ? ColorHelper.FromArgb(0x76, 0xF4, 0xF8, 0xFF)
            : ColorHelper.FromArgb(0x76, 0x18, 0x1C, 0x22));
    }

    private static void ApplyButtonStateBrushes(
        Button button,
        Brush background,
        Brush pointerBackground,
        Brush pressedBackground,
        Brush border,
        Brush pointerBorder,
        Brush pressedBorder,
        Brush foreground,
        Brush pointerForeground,
        Brush pressedForeground,
        Brush disabledBackground,
        Brush disabledBorder,
        Brush disabledForeground)
    {
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;

        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = pointerBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = disabledBackground;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = pointerBorder;
        button.Resources["ButtonBorderBrushPressed"] = pressedBorder;
        button.Resources["ButtonBorderBrushDisabled"] = disabledBorder;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = pointerForeground;
        button.Resources["ButtonForegroundPressed"] = pressedForeground;
        button.Resources["ButtonForegroundDisabled"] = disabledForeground;
    }

    private void UpdateTabSelectionIndicator()
    {
        Button selectedButton = ViewModel.SelectedView switch
        {
            QuickCaptureViewMode.Pinned => PinnedTabButton,
            QuickCaptureViewMode.Recent => RecentTabButton,
            _ => RecordsTabButton
        };

        if (TabHost.Visibility != Visibility.Visible)
        {
            return;
        }

        if (selectedButton.ActualWidth <= 0 ||
            selectedButton.ActualHeight <= 0 ||
            TabHost.ActualWidth <= 0)
        {
            return;
        }

        var targetPoint = selectedButton
            .TransformToVisual(TabHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetOffset = new Vector3((float)targetPoint.X, (float)targetPoint.Y, 0);
        var visual = ElementCompositionPreview.GetElementVisual(TabSelectionIndicator);
        visual.StopAnimation("Offset");

        TabSelectionIndicator.Width = selectedButton.ActualWidth;
        TabSelectionIndicator.Height = selectedButton.ActualHeight;
        TabSelectionIndicator.Opacity = 1;
        visual.Offset = targetOffset;
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

    private void QueueBackdropRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueBackdropRefresh);
            return;
        }

        long generation = ++_backdropRefreshGeneration;
        _ = RefreshBackdropAfterDelayAsync(generation, 80);
        _ = RefreshBackdropAfterDelayAsync(generation, 320);
        _ = RefreshBackdropAfterDelayAsync(generation, 900);
    }

    private async Task RefreshBackdropAfterDelayAsync(long generation, int delayMs)
    {
        await Task.Delay(delayMs);
        if (generation != _backdropRefreshGeneration)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RefreshBackdropIfCurrent(generation));
            return;
        }

        RefreshBackdropIfCurrent(generation);
    }

    private void RefreshBackdropIfCurrent(long generation)
    {
        if (generation == _backdropRefreshGeneration)
        {
            if (!Visible || _isHideAnimationRunning)
            {
                return;
            }

            ApplyBackdropPreference();
        }
    }

    private void PlayTrayRaiseAnimation()
    {
        long generation = ++_trayAnimationGeneration;
        StopTrayVisualAnimation();
        _isHideAnimationRunning = false;

        var profile = GetWidgetAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={generation} durationMs={profile.DurationMs}");
        AnimateTrayVisual(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            0,
            0,
            profile.ShowStartOpacity,
            WidgetAnimationRestingOpacity,
            profile.ShowStartScale,
            WidgetAnimationRestingScale,
            profile.DurationMs,
            true,
            generation,
            () =>
            {
                RestoreTrayVisualState();
                RestoreTrayWindowPosition();
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
        long generation = _trayAnimationGeneration;
        var profile = GetWidgetAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={generation}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={generation} durationMs={profile.DurationMs}");
        AnimateTrayVisual(
            0,
            0,
            profile.HideOffsetX,
            profile.HideOffsetY,
            WidgetAnimationRestingOpacity,
            profile.HideEndOpacity,
            WidgetAnimationRestingScale,
            profile.HideEndScale,
            profile.DurationMs,
            false,
            generation,
            () =>
            {
                if (!Visible)
                {
                    completed();
                }
            });
    }

    private void PrepareTrayVisualState(double offsetX, double offsetY, float opacity, float scale)
    {
        _trayAnimationTargetPosition = new Windows.Graphics.PointInt32(
            (int)Math.Round(ViewModel.Config.X),
            (int)Math.Round(ViewModel.Config.Y));
        ApplyTrayWindowOffset(offsetX, offsetY);

        RootGrid.Opacity = 1;
        var visual = GetCachedRootVisual();
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = GetTrayVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = WidgetAnimationRestingOpacity;
        visual.Scale = new Vector3(scale, scale, 1.0f);
        ApplyTrayWindowOpacity(opacity);
    }

    private void PrepareTrayRevealHiddenState()
    {
        RootGrid.Opacity = 1;
        var visual = GetCachedRootVisual();
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Offset = Vector3.Zero;
        visual.Opacity = WidgetAnimationRestingOpacity;
        visual.Scale = new Vector3(WidgetAnimationRestingScale, WidgetAnimationRestingScale, 1.0f);
        ApplyTrayWindowOpacity(0);
    }

    private void AnimateTrayVisual(
        double fromOffsetX,
        double fromOffsetY,
        double toOffsetX,
        double toOffsetY,
        float fromOpacity,
        float toOpacity,
        float fromScale,
        float toScale,
        int durationMs,
        bool isShowing,
        long animationGeneration,
        Action completed)
    {
        LogTrayWindow(
            $"AnimateStart mode={(isShowing ? "show" : "hide")} gen={animationGeneration} durationMs={durationMs} " +
            $"windowOffset=({fromOffsetX:F0},{fromOffsetY:F0})->({toOffsetX:F0},{toOffsetY:F0}) " +
            $"windowOpacity={fromOpacity:F2}->{toOpacity:F2}");
        StopTrayVisualAnimation();
        PrepareTrayVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);

        if (durationMs <= 1)
        {
            CompleteTrayWindowAnimation(toOffsetX, toOffsetY, toOpacity, toScale, isShowing, animationGeneration, completed);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        _trayWindowAnimationTimer = timer;

        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_trayWindowAnimationTimer, timer) ||
                animationGeneration != _trayAnimationGeneration)
            {
                timer.Stop();
                return;
            }

            double rawProgress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0.0, 1.0);
            double easedProgress = EaseTrayWindowAnimation(rawProgress, isShowing);
            double currentOffsetX = Lerp(fromOffsetX, toOffsetX, easedProgress);
            double currentOffsetY = Lerp(fromOffsetY, toOffsetY, easedProgress);
            float currentOpacity = (float)Lerp(fromOpacity, toOpacity, easedProgress);
            float currentScale = (float)Lerp(fromScale, toScale, easedProgress);

            ApplyTrayWindowOffset(currentOffsetX, currentOffsetY);
            ApplyTrayWindowOpacity(currentOpacity);
            ApplyTrayVisualScale(currentScale);

            if (rawProgress < 1.0)
            {
                return;
            }

            timer.Stop();
            if (ReferenceEquals(_trayWindowAnimationTimer, timer))
            {
                _trayWindowAnimationTimer = null;
            }

            CompleteTrayWindowAnimation(toOffsetX, toOffsetY, toOpacity, toScale, isShowing, animationGeneration, completed);
        };

        timer.Start();
    }

    private void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            LogTrayWindow("CompleteHide skipped reason=visible-again");
            return;
        }

        _isHideAnimationRunning = false;
        StopTrayVisualAnimation();
        RestoreNativeBackdropAfterTrayReveal();
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        RestoreTrayVisualState();
        RestoreTrayWindowPosition();
        LogTrayWindow("CompleteHide");
    }

    private void ApplyTrayWindowOffset(double offsetX, double offsetY)
    {
        var target = _trayAnimationTargetPosition ?? new Windows.Graphics.PointInt32(
            (int)Math.Round(ViewModel.Config.X),
            (int)Math.Round(ViewModel.Config.Y));
        var nextPosition = new Windows.Graphics.PointInt32(
            target.X + (int)Math.Round(offsetX),
            target.Y + (int)Math.Round(offsetY));

        _isApplyingTrayAnimationBounds = true;
        try
        {
            _appWindow.Move(nextPosition);
        }
        finally
        {
            _isApplyingTrayAnimationBounds = false;
        }
    }

    private void RestoreTrayWindowPosition()
    {
        if (_trayAnimationTargetPosition is { } target)
        {
            _isApplyingTrayAnimationBounds = true;
            try
            {
                _appWindow.Move(target);
            }
            finally
            {
                _isApplyingTrayAnimationBounds = false;
            }
        }

        _trayAnimationTargetPosition = null;
    }

    private void StopTrayVisualAnimation()
    {
        if (_trayWindowAnimationTimer is { } timer)
        {
            timer.Stop();
            _trayWindowAnimationTimer = null;
        }

        if (_cachedRootVisual is { } visual)
        {
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
        }
    }

    private void RestoreTrayVisualState()
    {
        RootGrid.Opacity = 1;
        var visual = GetCachedRootVisual();
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = GetTrayVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = WidgetAnimationRestingOpacity;
        visual.Scale = new Vector3(WidgetAnimationRestingScale, WidgetAnimationRestingScale, 1.0f);
        RestoreTrayWindowOpacity();
    }

    private void CompleteTrayWindowAnimation(
        double finalOffsetX,
        double finalOffsetY,
        float finalOpacity,
        float finalScale,
        bool isShowing,
        long animationGeneration,
        Action completed)
    {
        if (animationGeneration != _trayAnimationGeneration)
        {
            return;
        }

        ApplyTrayWindowOffset(finalOffsetX, finalOffsetY);
        ApplyTrayWindowOpacity(finalOpacity);
        ApplyTrayVisualScale(finalScale);
        SetTrayAnimationOffsetOverride(null, null);
        LogTrayWindow($"AnimateCompleted mode={(isShowing ? "show" : "hide")} gen={animationGeneration}");
        if (isShowing)
        {
            RestoreNativeBackdropAfterTrayReveal();
            RestoreTrayVisualState();
        }

        completed();
    }

    private void ApplyTrayWindowOpacity(float opacity)
    {
        opacity = Math.Clamp(opacity, 0.0f, 1.0f);
        var visual = GetCachedRootVisual();
        visual.Opacity = opacity;
    }

    private void RestoreTrayWindowOpacity()
    {
        var visual = GetCachedRootVisual();
        visual.Opacity = WidgetAnimationRestingOpacity;
    }

    private void ApplyTrayVisualScale(float scale)
    {
        scale = Math.Clamp(scale, 0.0f, 1.0f);
        var visual = GetCachedRootVisual();
        visual.CenterPoint = GetTrayVisualCenterPoint();
        visual.Scale = new Vector3(scale, scale, 1.0f);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private double EaseTrayWindowAnimation(double progress, bool isShowing)
    {
        string intensity = _settingsService.Settings.WidgetAnimationEasingIntensity;
        if (intensity == SettingsService.WidgetAnimationEasingNone)
        {
            return progress;
        }

        if (isShowing)
        {
            return intensity switch
            {
                SettingsService.WidgetAnimationEasingLight => CubicBezierEase(progress, 0.25, 0.9, 0.25, 1.0),
                SettingsService.WidgetAnimationEasingStrong => CubicBezierEase(progress, 0.05, 1.1, 0.15, 1.0),
                _ => CubicBezierEase(progress, 0.16, 1.0, 0.3, 1.0)
            };
        }

        return intensity switch
        {
            SettingsService.WidgetAnimationEasingLight => CubicBezierEase(progress, 0.6, 0.1, 0.9, 0.3),
            SettingsService.WidgetAnimationEasingStrong => CubicBezierEase(progress, 0.7, 0.0, 0.95, -0.1),
            _ => CubicBezierEase(progress, 0.7, 0.0, 0.84, 0.0)
        };
    }

    private static double CubicBezierEase(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;

        double cx = 3.0 * x1;
        double bx = 3.0 * (x2 - x1) - cx;
        double ax = 1.0 - cx - bx;
        double cy = 3.0 * y1;
        double by = 3.0 * (y2 - y1) - cy;
        double ay = 1.0 - cy - by;

        double tGuess = t;
        for (int i = 0; i < 8; i++)
        {
            double x = ((ax * tGuess + bx) * tGuess + cx) * tGuess - t;
            if (Math.Abs(x) < 1e-7) break;
            double dx = (3.0 * ax * tGuess + 2.0 * bx) * tGuess + cx;
            if (Math.Abs(dx) < 1e-10) break;
            tGuess -= x / dx;
        }

        return ((ay * tGuess + by) * tGuess + cy) * tGuess;
    }

    private Microsoft.UI.Composition.Visual GetCachedRootVisual()
    {
        return _cachedRootVisual ??= ElementCompositionPreview.GetElementVisual(RootGrid);
    }

    private void SuppressNativeBackdropForTrayReveal()
    {
        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = true;
        SystemBackdrop = null;
        DisposeAcrylicController();
        Win32Helper.DisableAccentPolicy(_hWnd);
    }

    private void RestoreNativeBackdropAfterTrayReveal()
    {
        if (!_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = false;
        ApplyBackdropPreference();
    }

    private Vector3 GetTrayVisualCenterPoint()
    {
        return new Vector3(
            (float)Math.Max(0, RootGrid.ActualWidth / 2),
            (float)Math.Max(0, RootGrid.ActualHeight / 2),
            0);
    }

    private WidgetAnimationProfile GetWidgetAnimationProfile()
    {
        string effect = _settingsService.Settings.WidgetAnimationEffect;
        string direction = _settingsService.Settings.WidgetAnimationSlideDirection;
        int durationMs = GetWidgetAnimationDurationMs(_settingsService.Settings.WidgetAnimationSpeed);
        var slideOffsets = GetOffscreenSlideOffsets();
        var (dirX, dirY) = GetDirectionalSlideOffset(direction, slideOffsets);

        return effect switch
        {
            SettingsService.WidgetAnimationEffectNone => new WidgetAnimationProfile(0, 0, 0, 0, 1, 1, 1, 1, 1, false),
            SettingsService.WidgetAnimationEffectFade => new WidgetAnimationProfile(0, 0, 0, 0, 0, 0, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeft => new WidgetAnimationProfile(-slideOffsets.Left, 0, -slideOffsets.Left, 0, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUp => new WidgetAnimationProfile(0, -slideOffsets.Up, 0, -slideOffsets.Up, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDown => new WidgetAnimationProfile(0, slideOffsets.Down, 0, slideOffsets.Down, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectScaleFade => new WidgetAnimationProfile(0, 0, 0, 0, WidgetAnimationSoftOpacity, WidgetAnimationSoftOpacity, WidgetAnimationSoftScale, WidgetAnimationSoftScale, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRight => new WidgetAnimationProfile(slideOffsets.Right, 0, slideOffsets.Right, 0, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectZoom => new WidgetAnimationProfile(0, 0, 0, 0, 0, 0, 0.5f, 0.5f, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUpFade => new WidgetAnimationProfile(0, -slideOffsets.Up, 0, -slideOffsets.Up, 0, 0, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDownFade => new WidgetAnimationProfile(0, slideOffsets.Down, 0, slideOffsets.Down, 0, 0, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeftFade => new WidgetAnimationProfile(-slideOffsets.Left, 0, -slideOffsets.Left, 0, 0, 0, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRightFade => new WidgetAnimationProfile(slideOffsets.Right, 0, slideOffsets.Right, 0, 0, 0, 1, 1, durationMs, true),
            _ => new WidgetAnimationProfile(dirX, dirY, dirX, dirY, 1, 1, 1, 1, durationMs, true)
        };
    }

    private (double X, double Y) GetDirectionalSlideOffset(string direction, (double Left, double Right, double Up, double Down) offsets)
    {
        double baseOffset = Math.Max(Math.Max(offsets.Left, offsets.Right), Math.Max(offsets.Up, offsets.Down));
        if (baseOffset < 1) baseOffset = 200;

        return direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => (-baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionRight => (baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionUp => (0, -baseOffset),
            SettingsService.WidgetAnimationSlideDirectionDown => (0, baseOffset),
            _ => (offsets.Right, 0)
        };
    }

    private (double Left, double Right, double Up, double Down) GetOffscreenSlideOffsets()
    {
        if (_trayAnimationOffsetOverrideX.HasValue ||
            _trayAnimationOffsetOverrideY.HasValue)
        {
            double horizontal = Math.Abs(_trayAnimationOffsetOverrideX.GetValueOrDefault());
            double vertical = Math.Abs(_trayAnimationOffsetOverrideY.GetValueOrDefault());
            return (
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset);
        }

        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double x = ViewModel.Config.X;
        double y = ViewModel.Config.Y;
        double width = Math.Max(MinWidgetSlideOffset, ViewModel.Config.Width);
        double height = Math.Max(MinWidgetSlideOffset, ViewModel.Config.Height);

        double left = Math.Max(MinWidgetSlideOffset, (x + width) - workArea.X + OffscreenSlidePadding);
        double right = Math.Max(MinWidgetSlideOffset, (workArea.X + workArea.Width) - x + OffscreenSlidePadding);
        double up = Math.Max(MinWidgetSlideOffset, (y + height) - workArea.Y + OffscreenSlidePadding);
        double down = Math.Max(MinWidgetSlideOffset, (workArea.Y + workArea.Height) - y + OffscreenSlidePadding);
        return (left, right, up, down);
    }

    private static int GetWidgetAnimationDurationMs(string speed)
    {
        return speed switch
        {
            SettingsService.WidgetAnimationSpeedVeryFast => 120,
            SettingsService.WidgetAnimationSpeedFast => 220,
            SettingsService.WidgetAnimationSpeedRelaxed => 520,
            SettingsService.WidgetAnimationSpeedSlow => 680,
            _ => WidgetShowAnimationMs
        };
    }

    private void LogTrayWindow(string message)
    {
        App.LogVerbose($"[TrayWindow] Quick {ViewModel.Config.Name}#{ShortId(ViewModel.Config.Id)} hwnd=0x{_hWnd.ToInt64():X} {message}");
    }

    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "none"
            : id.Length <= 8 ? id : id[..8];
    }
}
