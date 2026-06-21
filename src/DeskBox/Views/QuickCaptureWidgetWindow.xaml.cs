using System.Diagnostics;
using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private const int WidgetShowAnimationMs = 400;
    private const double WidgetSlideOffsetX = 36.0;
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

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
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
    private CompositionScopedBatch? _trayVisualAnimationBatch;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private bool _isHideAnimationRunning;
    private bool _isClearingData;
    private long _trayAnimationGeneration;
    private bool _isTrayWindowRenderingSubscribed;
    private long _trayWindowAnimationStartTicks;
    private long _trayWindowAnimationGeneration;
    private double _trayWindowAnimationFromOffsetX;
    private double _trayWindowAnimationFromOffsetY;
    private double _trayWindowAnimationToOffsetX;
    private double _trayWindowAnimationToOffsetY;
    private int _trayWindowAnimationDurationMs;
    private bool _isTrayWindowAnimationShowing;
    private Windows.Graphics.PointInt32? _lastAppliedTrayWindowPosition;
    private bool _isApplyingTrayAnimationBounds;
    private long _backdropRefreshGeneration;
    private long _statusToastGeneration;

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => _hWnd;

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
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindow();
        SetupEventHandlers();
        ApplyLocalizedText();
        ApplySurfaceStyle();
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
    }

    public void ShowPreparedAtDesktopLayer()
    {
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        _appWindow.Show();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
        PushToBottom();
    }

    public void ShowPreparedRaisedFromTray()
    {
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        HoldTemporaryTopMost();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
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
            return;
        }

        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
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
        PrepareTrayVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimation();
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        _trayAnimationGeneration++;
        StopTrayVisualAnimation();
        RestoreTrayVisualState();
        RestoreTrayWindowPosition();
    }

    public bool PrepareTrayHideAnimation()
    {
        if (!Visible || _isHideAnimationRunning)
        {
            return false;
        }

        _trayAnimationGeneration++;
        StopTrayVisualAnimation();
        _isHideAnimationRunning = true;
        Visible = false;
        ViewModel.Config.IsVisible = false;
        _settingsService.SaveDebounced();
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
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
        PlayTrayRaiseAnimation();

        if (!autoRestore)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1200);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer(force: true);
            }
        };
        timer.Start();
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
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            QueueBackdropRefresh();
            RootGrid.Focus(FocusState.Programmatic);
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += QuickCaptureWidgetWindow_Activated;

        _appWindow.Changed += (_, args) =>
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
        };

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
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            StopTrayVisualAnimation();
            RestoreTrayVisualState();
            RestoreTrayWindowPosition();
            DisposeAcrylicController();
        };
    }

    private void ApplyLocalizedText()
    {
        string addInputText = _localizationService.T("QuickCapture.AddInput");
        string searchText = _localizationService.T("QuickCapture.SearchPlaceholder");
        string closeSearchText = _localizationService.T("QuickCapture.CloseSearch");
        string moreText = _localizationService.T("Widget.Tooltip.More");
        string closeText = _localizationService.T("Widget.Tooltip.DeleteWidget");

        InputTextBox.PlaceholderText = _localizationService.T("QuickCapture.InputPlaceholder");
        SearchTextBox.PlaceholderText = searchText;
        ToolTipService.SetToolTip(AddInputButton, addInputText);
        ToolTipService.SetToolTip(SearchButton, searchText);
        ToolTipService.SetToolTip(CloseSearchButton, closeSearchText);
        ToolTipService.SetToolTip(MoreButton, moreText);
        ToolTipService.SetToolTip(CloseButton, closeText);
        AutomationProperties.SetName(InputTextBox, _localizationService.T("QuickCapture.InputPlaceholder"));
        AutomationProperties.SetName(AddInputButton, addInputText);
        AutomationProperties.SetName(SearchButton, searchText);
        AutomationProperties.SetName(SearchTextBox, searchText);
        AutomationProperties.SetName(CloseSearchButton, closeSearchText);
        AutomationProperties.SetName(MoreButton, moreText);
        AutomationProperties.SetName(CloseButton, closeText);
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.WidgetOpacity))
        {
            ApplyBackdropPreference();
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

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddInputAsync();
        InputTextBox.Focus(FocusState.Programmatic);
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
        ViewModel.SelectedView = QuickCaptureViewMode.Records;
        ApplySurfaceStyle();
    }

    private void PinnedTabButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedView = QuickCaptureViewMode.Pinned;
        ApplySurfaceStyle();
    }

    private void RecentTabButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedView = QuickCaptureViewMode.Recent;
        ApplySurfaceStyle();
    }

    private async void EnableRecentCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (await QuickCaptureClipboardActivationHelper.EnableAsync(RootGrid.XamlRoot, _localizationService))
        {
            ViewModel.SelectedView = QuickCaptureViewMode.Recent;
            ApplySurfaceStyle();
        }
    }

    private async void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QuickCaptureItemViewModel item)
        {
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
        }
    }

    private async void ItemsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QuickCaptureItemViewModel item &&
            !item.IsRecent &&
            item.Type != QuickCaptureItemType.Image)
        {
            await EditItemAsync(item);
        }
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
            await ViewModel.DeleteItemAsync(item);
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
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

    private void QuickCaptureItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        var flyout = CreateItemFlyout(item);
        flyout.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
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
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, false);
    }

    private static void SetItemActionButtonsVisible(DependencyObject? itemRoot, bool isVisible)
    {
        if (itemRoot is null ||
            FindVisualChild<StackPanel>(itemRoot, "ItemActionButtons") is not { } actions)
        {
            return;
        }

        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
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
            await ViewModel.DeleteItemAsync(item);
        }
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

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
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

        flyout.ShowAt(MoreButton);
        await Task.CompletedTask;
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
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
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
            deleteRecentItem.Click += async (_, _) => await ViewModel.DeleteItemAsync(item);
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
        deleteItem.Click += async (_, _) => await ViewModel.DeleteItemAsync(item);
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

    private async Task EditItemAsync(QuickCaptureItemViewModel item)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        double dialogWidth = GetQuickCaptureDialogWidth();
        double contentWidth = Math.Max(120, dialogWidth - 48);
        var textBox = new TextBox
        {
            Text = item.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = contentWidth,
            MinWidth = 0,
            MinHeight = 120
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.Edit"),
            PrimaryButtonText = _localizationService.T("Common.Ok"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = textBox
        };
        ApplyQuickCaptureDialogSizing(dialog, dialogWidth);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.EditItemAsync(item, textBox.Text);
        }
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ = App.Current.WidgetManager?.RemoveWidgetAsync(ViewModel.Config.Id);
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
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
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
        finally
        {
            deferral.Complete();
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
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
        ShowStatusToast(_localizationService.T("QuickCapture.Copied"));
    }

    private void ShowStatusToast(string text)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatusToast(text));
            return;
        }

        long generation = ++_statusToastGeneration;
        StatusToastText.Text = text;
        StatusToast.Opacity = 1;
        _ = HideStatusToastAfterDelayAsync(generation);
    }

    private async Task HideStatusToastAfterDelayAsync(long generation)
    {
        await Task.Delay(1400);
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
        }
    }

    private static async Task<QuickCaptureDropContent> TryReadDroppedContentAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems))
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
                }
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
            if (Visible && !_isAtDesktopLayer)
            {
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }

            return;
        }

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing)
        {
            return;
        }

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
            if (App.Current.IsDeskBoxWindow(foregroundWindow))
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                widgetManager.RestoreRaisedWidgetsToDesktopLayer();
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
            return;
        }

        if (_isDragging || _isResizing)
        {
            if (force || _restoreDesktopLayerWhenIdle)
            {
                _restoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        PushToBottom();
        ApplyBackdropPreference();
    }

    private void ElevateForInteraction()
    {
        HoldTemporaryTopMost();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void HoldTemporaryTopMost()
    {
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        Win32Helper.SetWindowTopMost(_hWnd);
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
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

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
        _acrylicController.TintColor = tintColor;
        _acrylicController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.85, 0.0, 0.85);
        _acrylicController.LuminosityOpacity = (float)Math.Clamp(surfaceOpacity * 0.55, 0.0, 0.55);
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
        ApplyTabButtonStyle(RecordsTabButton, ViewModel.IsRecordsView, isDark, accentColor);
        ApplyTabButtonStyle(PinnedTabButton, ViewModel.IsPinnedView, isDark, accentColor);
        ApplyTabButtonStyle(RecentTabButton, ViewModel.IsRecentView, isDark, accentColor);
    }

    private static void ApplyTabButtonStyle(
        Button button,
        bool isSelected,
        bool isDark,
        Windows.UI.Color accentColor)
    {
        var selectedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            isDark ? (byte)0x78 : (byte)0x88);
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
        var selectedBorder = WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC);
        var normalBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);
        var foreground = isSelected
            ? (isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A))
            : (isDark ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8) : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A));

        button.Background = new SolidColorBrush(isSelected ? selectedBackground : normalBackground);
        button.BorderBrush = new SolidColorBrush(isSelected ? selectedBorder : normalBorder);
        button.Foreground = new SolidColorBrush(foreground);
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
            CompleteTrayShowWithoutAnimation();
            return;
        }

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

    private void PlayTrayHideAnimation(Action completed)
    {
        long generation = _trayAnimationGeneration;
        var profile = GetWidgetAnimationProfile();
        if (!profile.IsEnabled)
        {
            completed();
            return;
        }

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
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = GetTrayVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = opacity;
        visual.Scale = new Vector3(scale, scale, 1.0f);
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
        StopTrayVisualAnimation();
        PrepareTrayVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);

        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        var compositor = visual.Compositor;
        var easing = CreateTrayVisualEasing(compositor, isShowing);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        AnimateTrayWindowOffset(fromOffsetX, fromOffsetY, toOffsetX, toOffsetY, durationMs, isShowing, animationGeneration);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = duration;
        opacityAnimation.InsertKeyFrame(0.0f, fromOpacity);
        opacityAnimation.InsertKeyFrame(1.0f, toOpacity, easing);

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = duration;
        scaleAnimation.InsertKeyFrame(0.0f, new Vector3(fromScale, fromScale, 1.0f));
        scaleAnimation.InsertKeyFrame(1.0f, new Vector3(toScale, toScale, 1.0f), easing);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _trayVisualAnimationBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_trayVisualAnimationBatch, batch) ||
                animationGeneration != _trayAnimationGeneration)
            {
                return;
            }

            _trayVisualAnimationBatch = null;
            RestoreTrayVisualState();
            completed();
        };

        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Scale", scaleAnimation);
        batch.End();
    }

    private void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            return;
        }

        _isHideAnimationRunning = false;
        StopTrayVisualAnimation();
        RestoreTrayVisualState();
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        RestoreTrayWindowPosition();
    }

    private void AnimateTrayWindowOffset(
        double fromX,
        double fromY,
        double toX,
        double toY,
        int durationMs,
        bool isShowing,
        long animationGeneration)
    {
        StopTrayWindowMoveAnimation();
        ApplyTrayWindowOffset(fromX, fromY);

        _trayWindowAnimationFromOffsetX = fromX;
        _trayWindowAnimationFromOffsetY = fromY;
        _trayWindowAnimationToOffsetX = toX;
        _trayWindowAnimationToOffsetY = toY;
        _trayWindowAnimationDurationMs = Math.Max(1, durationMs);
        _isTrayWindowAnimationShowing = isShowing;
        _trayWindowAnimationGeneration = animationGeneration;
        _trayWindowAnimationStartTicks = Stopwatch.GetTimestamp();

        if (_isTrayWindowRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += TrayWindowRendering_Tick;
        _isTrayWindowRenderingSubscribed = true;
    }

    private void TrayWindowRendering_Tick(object? sender, object e)
    {
        if (_trayWindowAnimationGeneration != _trayAnimationGeneration)
        {
            StopTrayWindowMoveAnimation();
            RestoreTrayWindowPosition();
            return;
        }

        double elapsedMs = Stopwatch.GetElapsedTime(_trayWindowAnimationStartTicks).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMs / _trayWindowAnimationDurationMs, 0.0, 1.0);
        double easedProgress = _isTrayWindowAnimationShowing
            ? 1 - Math.Pow(1 - progress, 3)
            : progress * progress * progress;
        ApplyTrayWindowOffset(
            _trayWindowAnimationFromOffsetX + ((_trayWindowAnimationToOffsetX - _trayWindowAnimationFromOffsetX) * easedProgress),
            _trayWindowAnimationFromOffsetY + ((_trayWindowAnimationToOffsetY - _trayWindowAnimationFromOffsetY) * easedProgress));

        if (progress >= 1.0)
        {
            StopTrayWindowMoveAnimation();
            ApplyTrayWindowOffset(_trayWindowAnimationToOffsetX, _trayWindowAnimationToOffsetY);
        }
    }

    private void ApplyTrayWindowOffset(double offsetX, double offsetY)
    {
        var target = _trayAnimationTargetPosition ?? new Windows.Graphics.PointInt32(
            (int)Math.Round(ViewModel.Config.X),
            (int)Math.Round(ViewModel.Config.Y));
        var nextPosition = new Windows.Graphics.PointInt32(
            target.X + (int)Math.Round(offsetX),
            target.Y + (int)Math.Round(offsetY));

        if (_lastAppliedTrayWindowPosition is { } previous &&
            previous.X == nextPosition.X &&
            previous.Y == nextPosition.Y)
        {
            return;
        }

        _isApplyingTrayAnimationBounds = true;
        try
        {
            _appWindow.Move(nextPosition);
            _lastAppliedTrayWindowPosition = nextPosition;
        }
        finally
        {
            _isApplyingTrayAnimationBounds = false;
        }
    }

    private void RestoreTrayWindowPosition()
    {
        StopTrayWindowMoveAnimation();
        if (_trayAnimationTargetPosition is { } target)
        {
            _isApplyingTrayAnimationBounds = true;
            try
            {
                _appWindow.Move(target);
                _lastAppliedTrayWindowPosition = target;
            }
            finally
            {
                _isApplyingTrayAnimationBounds = false;
            }
        }

        _trayAnimationTargetPosition = null;
        _lastAppliedTrayWindowPosition = null;
    }

    private void StopTrayVisualAnimation()
    {
        StopTrayWindowMoveAnimation();
        if (_trayVisualAnimationBatch is null)
        {
            return;
        }

        _trayVisualAnimationBatch = null;
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
    }

    private void StopTrayWindowMoveAnimation()
    {
        if (!_isTrayWindowRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= TrayWindowRendering_Tick;
        _isTrayWindowRenderingSubscribed = false;
    }

    private void RestoreTrayVisualState()
    {
        RootGrid.Opacity = 1;
        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = GetTrayVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = WidgetAnimationRestingOpacity;
        visual.Scale = new Vector3(WidgetAnimationRestingScale, WidgetAnimationRestingScale, 1.0f);
    }

    private Vector3 GetTrayVisualCenterPoint()
    {
        return new Vector3(
            (float)Math.Max(0, RootGrid.ActualWidth / 2),
            (float)Math.Max(0, RootGrid.ActualHeight / 2),
            0);
    }

    private static CompositionEasingFunction CreateTrayVisualEasing(Compositor compositor, bool isShowing)
    {
        return isShowing
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0.0f), new Vector2(0.84f, 0.0f));
    }

    private WidgetAnimationProfile GetWidgetAnimationProfile()
    {
        string effect = _settingsService.Settings.WidgetAnimationEffect;
        int durationMs = GetWidgetAnimationDurationMs(_settingsService.Settings.WidgetAnimationSpeed);

        return effect switch
        {
            SettingsService.WidgetAnimationEffectNone => new WidgetAnimationProfile(0, 0, 0, 0, 1, 1, 1, 1, 1, false),
            SettingsService.WidgetAnimationEffectFade => new WidgetAnimationProfile(0, 0, 0, 0, 0, 0, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeft => new WidgetAnimationProfile(-WidgetSlideOffsetX, 0, -WidgetSlideOffsetX, 0, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUp => new WidgetAnimationProfile(0, -WidgetSlideOffsetX, 0, -WidgetSlideOffsetX, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDown => new WidgetAnimationProfile(0, WidgetSlideOffsetX, 0, WidgetSlideOffsetX, 1, 1, 1, 1, durationMs, true),
            SettingsService.WidgetAnimationEffectScaleFade => new WidgetAnimationProfile(0, 0, 0, 0, WidgetAnimationSoftOpacity, WidgetAnimationSoftOpacity, WidgetAnimationSoftScale, WidgetAnimationSoftScale, durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRight => new WidgetAnimationProfile(WidgetSlideOffsetX, 0, WidgetSlideOffsetX, 0, 1, 1, 1, 1, durationMs, true),
            _ => new WidgetAnimationProfile(WidgetSlideOffsetX, 0, WidgetSlideOffsetX, 0, WidgetAnimationSoftOpacity, WidgetAnimationSoftOpacity, WidgetAnimationSoftScale, WidgetAnimationSoftScale, durationMs, true)
        };
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
}
