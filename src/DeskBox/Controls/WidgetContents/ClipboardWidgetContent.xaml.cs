using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace DeskBox.Controls.WidgetContents;

public sealed record ClipboardTransientState(
    ClipboardFilterKind Filter,
    bool IsSearchOpen,
    string SearchText,
    string? SelectedItemId);

public sealed partial class ClipboardWidgetContent : UserControl, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _timestampTimer;
    private static readonly SolidColorBrush s_transparentBrush = new(Colors.Transparent);
    private bool _isFilterPointerDown;
    private bool _isFilterDragPanning;
    private bool _suppressNextFilterClick;
    private double _filterDragStartX;
    private double _filterDragStartOffset;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;
    private bool _isDisposed;
    private bool _isModalDialogOpen;
    private DeskBox.Views.WidgetEditorPopoverWindow? _activePeekFlyout;
    private ClipboardItemViewModel? _peekItem;
    private CancellationTokenSource? _peekLoadCts;
    private CancellationTokenSource? _copiedFeedbackCts;

    public ClipboardWidgetContent(
        ClipboardWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);

        ViewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;
        DataContext = viewModel;

        InitializeComponent();
        FilterScrollViewer.AddHandler(PointerPressedEvent, new PointerEventHandler(FilterScrollViewer_PointerPressed), true);
        if (DragDropPermissionService.IsCurrentProcessElevatedCached())
        {
            ItemsList.CanDragItems = false;
        }

        if (DispatcherQueue is { } dq)
        {
            _timestampTimer = dq.CreateTimer();
            _timestampTimer.Interval = TimeSpan.FromSeconds(60);
            _timestampTimer.IsRepeating = true;
            _timestampTimer.Tick += (_, _) =>
            {
                if (_isWindowVisible && !_isCompactCollapsed && !_isDisposed)
                {
                    ViewModel.RefreshRelativeTimestamps();
                }
            };
        }

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _settingsService.SettingsChanged += SettingsService_SettingsChanged;
        ActualThemeChanged += ClipboardWidgetContent_ActualThemeChanged;
        Loaded += ClipboardWidgetContent_Loaded;
        Unloaded += ClipboardWidgetContent_Unloaded;
    }

    public ClipboardWidgetViewModel ViewModel { get; }

    public bool IsQuickPastePopup { get; set; }

    public bool IsModalDialogOpen => _isModalDialogOpen;

    public bool IsPeekPopoverOpen => _activePeekFlyout?.IsShown == true;

    public Func<ClipboardItemViewModel, bool, Task>? QuickPasteExecutor { get; set; }

    public Func<bool, Task>? QuickPasteQueueNextExecutor { get; set; }

    public bool IsReadyForReuse => !_isDisposed && ViewModel.IsInitialized;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler? DismissRequested;

    public void PrepareForQuickPastePopup()
    {
        if (_isDisposed)
        {
            return;
        }

        ClosePeekFlyout();
        ViewModel.ExitMultiSelectMode();
        ViewModel.ActiveFilter = ClipboardFilterKind.All;
        ViewModel.SearchText = string.Empty;
        ViewModel.IsSearchOpen = true;
        SynchronizeFilterSelection(bringIntoView: false);
        SynchronizeViewModeButtonVisual();
        SynchronizePasteQueueButtonVisual();
        SynchronizeQuickCaptureButtonVisibility();

        if (ViewModel.VisibleItems.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
            ItemsList.ScrollIntoView(ViewModel.VisibleItems[0]);
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                SearchTextBox.Focus(FocusState.Programmatic);
                SearchTextBox.SelectAll();
            }
        });
    }

    public void SyncFromResidentViewModel()
    {
        SynchronizeFilterSelection(bringIntoView: false);
        SynchronizeViewModeButtonVisual();
        SynchronizePasteQueueButtonVisual();
        SynchronizeQuickCaptureButtonVisibility();
        UpdateResponsiveLabels(RootGrid.ActualWidth);
        UpdateTimestampTimerState();
    }

    public void OnWindowVisibilityChanged(bool isVisible)
    {
        _isWindowVisible = isVisible;
        if (!isVisible)
        {
            ClosePeekFlyout();
        }

        UpdateTimestampTimerState();
    }

    public void OnCompactStateChanged(bool isCollapsed)
    {
        _isCompactCollapsed = isCollapsed;
        if (isCollapsed)
        {
            ClosePeekFlyout();
        }

        UpdateTimestampTimerState();
    }

    public ClipboardTransientState CaptureTransientState()
    {
        return new ClipboardTransientState(
            ViewModel.ActiveFilter,
            ViewModel.IsSearchOpen,
            ViewModel.SearchText,
            (ItemsList.SelectedItem as ClipboardItemViewModel)?.Id);
    }

    public void RestoreTransientState(ClipboardTransientState state)
    {
        ViewModel.ActiveFilter = state.Filter;
        ViewModel.IsSearchOpen = state.IsSearchOpen;
        if (state.IsSearchOpen)
        {
            ViewModel.SearchText = state.SearchText;
        }

        SynchronizeFilterSelection(bringIntoView: true);
        if (!string.IsNullOrWhiteSpace(state.SelectedItemId))
        {
            ClipboardItemViewModel? match = ViewModel.VisibleItems.FirstOrDefault(
                candidate => string.Equals(candidate.Id, state.SelectedItemId, StringComparison.Ordinal));
            if (match is not null)
            {
                ItemsList.SelectedItem = match;
            }
        }
    }

    internal async Task<int> HandleNativeDroppedFilesAsync(IReadOnlyList<DroppedFilePath> files)
    {
        int imported = await ViewModel.ImportDroppedFilesAsync(files);
        if (imported > 0)
        {
            RaiseFeedback(
                _localizationService.Format("Clipboard.Feedback.ImportedCount", imported),
                WidgetFeedbackSeverity.Info,
                "clipboard-imported");
        }

        return imported;
    }

    public async Task<int> HandleNativeDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        int imported = await ViewModel.ImportDroppedPathsAsync(paths);
        if (imported > 0)
        {
            RaiseFeedback(
                _localizationService.Format("Clipboard.Feedback.ImportedCount", imported),
                WidgetFeedbackSeverity.Info,
                "clipboard-imported");
        }

        return imported;
    }

    public void DisposeLeafOnly()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ClosePeekFlyout();
        ViewModel.SetViewForegroundTracking(this, isActive: false);
        _copiedFeedbackCts?.Cancel();
        _copiedFeedbackCts?.Dispose();
        _copiedFeedbackCts = null;
        _timestampTimer?.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        ActualThemeChanged -= ClipboardWidgetContent_ActualThemeChanged;
        Loaded -= ClipboardWidgetContent_Loaded;
        Unloaded -= ClipboardWidgetContent_Unloaded;
        Bindings?.StopTracking();
    }

    public void Dispose()
    {
        DisposeLeafOnly();
    }

    private void SettingsService_SettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } dq)
        {
            dq.TryEnqueue(() =>
            {
                if (!_isDisposed)
                {
                    SynchronizeQuickCaptureButtonVisibility();
                    SynchronizeViewModeButtonVisual();
                }
            });
            return;
        }

        SynchronizeQuickCaptureButtonVisibility();
        SynchronizeViewModeButtonVisual();
    }

    private void ClipboardWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_isDisposed)
        {
            SynchronizeFilterSelection(bringIntoView: false);
            SynchronizeViewModeButtonVisual();
            SynchronizePasteQueueButtonVisual();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.ActiveFilter), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.TabDisplayMode), StringComparison.Ordinal))
        {
            SynchronizeFilterSelection(bringIntoView: true);
        }
        else if (string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.IsCompactViewMode), StringComparison.Ordinal))
        {
            SynchronizeViewModeButtonVisual();
            RehydrateRealizedItemVisualAssets();
        }
        else if (string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.IsPasteQueueActive), StringComparison.Ordinal))
        {
            SynchronizePasteQueueButtonVisual();
        }
        else if (string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.IsMultiSelectMode), StringComparison.Ordinal))
        {
            SynchronizeQuickCaptureButtonVisibility();
        }
        else if (IsQuickPastePopup &&
                 (string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.SearchText), StringComparison.Ordinal) ||
                  string.Equals(e.PropertyName, nameof(ClipboardWidgetViewModel.ListVisibility), StringComparison.Ordinal)))
        {
            if (ViewModel.VisibleItems.Count > 0 && ItemsList.SelectedIndex < 0)
            {
                ItemsList.SelectedIndex = 0;
            }
        }
    }

    private async void ClipboardWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.IsInitialized)
            {
                await ViewModel.InitializeAsync();
            }
            else if (ViewModel.IsViewAttached && ViewModel.HasPendingRebuild)
            {
                ViewModel.RebuildVisibleItems();
            }

            SynchronizeFilterSelection(bringIntoView: false);
            SynchronizeViewModeButtonVisual();
            SynchronizePasteQueueButtonVisual();
            SynchronizeQuickCaptureButtonVisibility();
            UpdateResponsiveLabels(RootGrid.ActualWidth);
            UpdateTimestampTimerState();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] Loaded failed: {ex.Message}");
        }
    }

    private void ClipboardWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        ClosePeekFlyout();
        _timestampTimer?.Stop();
        ViewModel.SetViewForegroundTracking(this, isActive: false);
    }

    private void UpdateTimestampTimerState()
    {
        bool canTrackForeground = !_isDisposed && IsLoaded;
        bool isActivelyExpanded = canTrackForeground && _isWindowVisible && !_isCompactCollapsed;

        // Keep external foreground tracking active even while hidden in QuickReveal (F7)
        // or collapsed as a capsule so paste-back always knows the last external window.
        ViewModel.SetViewForegroundTracking(this, canTrackForeground);

        if (isActivelyExpanded)
        {
            RehydrateRealizedItemVisualAssets();
        }

        if (_timestampTimer is null)
        {
            return;
        }

        if (isActivelyExpanded)
        {
            if (!_timestampTimer.IsRunning)
            {
                _timestampTimer.Start();
            }
        }
        else if (_timestampTimer.IsRunning)
        {
            _timestampTimer.Stop();
        }
    }

    private void RehydrateRealizedItemVisualAssets()
    {
        if (_isDisposed || !IsLoaded || !_isWindowVisible || _isCompactCollapsed)
        {
            return;
        }

        if (ItemsList.ItemsPanelRoot is not Panel panel)
        {
            return;
        }

        foreach (UIElement element in panel.Children)
        {
            if (element is ListViewItem container &&
                ItemsList.ItemFromContainer(container) is ClipboardItemViewModel vm)
            {
                _ = vm.EnsureVisualAssetsLoadedAsync();
            }
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDisposed && !_isCompactCollapsed)
        {
            ViewModel.CaptureExternalForegroundWindow();
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLabels(e.NewSize.Width);
    }

    private void UpdateResponsiveLabels(double width)
    {
        if (width <= 0)
        {
            return;
        }

        ViewModel.UpdateResponsiveFilterChrome(width);
    }

    private void SynchronizeFilterSelection(bool bringIntoView)
    {
        if (FilterTabPanel is null)
        {
            return;
        }

        string activeTag = ViewModel.ActiveFilter.ToString();
        Brush selectedBg = ResolveThemeBrush("ControlFillColorSecondaryBrush") ?? s_transparentBrush;
        Brush selectedBorder = ResolveThemeBrush("CardStrokeColorDefaultBrush") ?? s_transparentBrush;
        Brush primaryFg = ResolveThemeBrush("TextFillColorPrimaryBrush") ?? s_transparentBrush;
        Brush secondaryFg = ResolveThemeBrush("TextFillColorSecondaryBrush") ?? primaryFg;
        Brush accentFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush") ?? primaryFg;

        Button? activeButton = null;
        foreach (UIElement child in FilterTabPanel.Children)
        {
            if (child is not Button tabButton)
            {
                continue;
            }

            bool isSelected = string.Equals(tabButton.Tag as string, activeTag, StringComparison.OrdinalIgnoreCase);
            tabButton.Background = isSelected ? selectedBg : s_transparentBrush;
            tabButton.BorderBrush = isSelected ? selectedBorder : s_transparentBrush;

            if (tabButton.Content is Grid tabGrid)
            {
                foreach (UIElement tabChild in tabGrid.Children)
                {
                    if (tabChild is Border indicator &&
                        string.Equals(indicator.Tag as string, "ActiveIndicator", StringComparison.Ordinal))
                    {
                        indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else if (tabChild is StackPanel contentStack)
                    {
                        foreach (UIElement item in contentStack.Children)
                        {
                            if (item is FontIcon icon)
                            {
                                icon.Foreground = isSelected ? accentFg : secondaryFg;
                            }
                            else if (item is TextBlock label)
                            {
                                label.Foreground = isSelected ? primaryFg : secondaryFg;
                                label.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                            }
                        }
                    }
                }
            }

            if (isSelected)
            {
                activeButton = tabButton;
            }
        }

        if (bringIntoView && activeButton is not null)
        {
            activeButton.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true
            });
        }
    }

    private void SynchronizeViewModeButtonVisual()
    {
        if (ViewModeToggleButton is null)
        {
            return;
        }

        bool isCompact = ViewModel.IsCompactViewMode;
        Brush activeBg = ResolveThemeBrush("SubtleFillColorSecondaryBrush") ?? s_transparentBrush;
        Brush accentFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush")
            ?? ResolveThemeBrush("TextFillColorPrimaryBrush")
            ?? s_transparentBrush;
        Brush secondaryFg = ResolveThemeBrush("TextFillColorSecondaryBrush") ?? accentFg;

        ViewModeToggleButton.Background = isCompact ? activeBg : s_transparentBrush;
        if (ViewModeToggleButton.Content is FontIcon icon)
        {
            icon.Foreground = isCompact ? accentFg : secondaryFg;
        }
    }

    private void SynchronizePasteQueueButtonVisual()
    {
        if (PasteQueueToggleButton is null)
        {
            return;
        }

        bool isActive = ViewModel.IsPasteQueueActive;
        Brush activeBg = ResolveThemeBrush("SubtleFillColorSecondaryBrush") ?? s_transparentBrush;
        Brush accentFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush")
            ?? ResolveThemeBrush("TextFillColorPrimaryBrush")
            ?? s_transparentBrush;
        Brush secondaryFg = ResolveThemeBrush("TextFillColorSecondaryBrush") ?? accentFg;

        PasteQueueToggleButton.Background = isActive ? activeBg : s_transparentBrush;
        if (PasteQueueToggleButton.Content is FontIcon icon)
        {
            icon.Foreground = isActive ? accentFg : secondaryFg;
        }
    }

    private void SynchronizeQuickCaptureButtonVisibility()
    {
        if (MergeSaveQuickCaptureButton is not null)
        {
            MergeSaveQuickCaptureButton.Visibility = CanSaveToQuickCapture()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private Brush? ResolveThemeBrush(string key)
    {
        ElementTheme effective = ActualTheme == ElementTheme.Default
            ? (Application.Current.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
            : ActualTheme;

        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(
                effective == ElementTheme.Light ? "Light" : "Dark",
                out object? dictObj) &&
            dictObj is ResourceDictionary themeDict &&
            themeDict.TryGetValue(key, out object? themedBrush) &&
            themedBrush is Brush tb)
        {
            return tb;
        }

        return Application.Current.Resources.TryGetValue(key, out object? fallback) && fallback is Brush fb
            ? fb
            : null;
    }

    private void FilterTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressNextFilterClick)
        {
            _suppressNextFilterClick = false;
            return;
        }

        if (sender is Button { Tag: string tag } &&
            Enum.TryParse<ClipboardFilterKind>(tag, ignoreCase: true, out ClipboardFilterKind filter))
        {
            ViewModel.ActiveFilter = filter;
        }
    }

    private void FilterScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (FilterScrollViewer is null || FilterScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        PointerPointProperties props = e.GetCurrentPoint(FilterScrollViewer).Properties;
        double delta = props.IsHorizontalMouseWheel ? props.MouseWheelDelta : -props.MouseWheelDelta;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        double targetOffset = Math.Clamp(
            FilterScrollViewer.HorizontalOffset + delta * 0.55,
            0,
            FilterScrollViewer.ScrollableWidth);
        FilterScrollViewer.ChangeView(targetOffset, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private void FilterScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FilterScrollViewer is null || FilterScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(FilterScrollViewer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isFilterPointerDown = true;
        _isFilterDragPanning = false;
        _suppressNextFilterClick = false;
        _filterDragStartX = point.Position.X;
        _filterDragStartOffset = FilterScrollViewer.HorizontalOffset;
    }

    private void FilterScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFilterPointerDown || FilterScrollViewer is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(FilterScrollViewer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _isFilterPointerDown = false;
            _isFilterDragPanning = false;
            return;
        }

        double dx = point.Position.X - _filterDragStartX;
        if (!_isFilterDragPanning && Math.Abs(dx) >= 6)
        {
            _isFilterDragPanning = true;
            _suppressNextFilterClick = true;
            FilterScrollViewer.CapturePointer(e.Pointer);
        }

        if (_isFilterDragPanning)
        {
            double nextOffset = Math.Clamp(
                _filterDragStartOffset - dx,
                0,
                FilterScrollViewer.ScrollableWidth);
            FilterScrollViewer.ChangeView(nextOffset, null, null, disableAnimation: true);
            e.Handled = true;
        }
    }

    private void FilterScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFilterPointerDown && !_isFilterDragPanning)
        {
            return;
        }

        bool wasPanning = _isFilterDragPanning;
        _isFilterPointerDown = false;
        _isFilterDragPanning = false;
        FilterScrollViewer?.ReleasePointerCaptures();
        if (wasPanning)
        {
            e.Handled = true;
            DispatcherQueue?.TryEnqueue(() => _suppressNextFilterClick = false);
        }
    }

    private void ViewModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleViewMode();
    }

    private void PasteQueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleLivePasteQueue();
        RaiseFeedback(
            _localizationService.T(
                ViewModel.IsPasteQueueActive
                    ? "Clipboard.Feedback.PasteQueueStarted"
                    : "Clipboard.Feedback.PasteQueueStopped"),
            WidgetFeedbackSeverity.Info,
            "clipboard-paste-queue");
    }

    private async void PasteQueueNextButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool preferPlain = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
            if (IsQuickPastePopup)
            {
                if (QuickPasteQueueNextExecutor is { } queueExecutor)
                {
                    await queueExecutor(preferPlain);
                }
                else
                {
                    DismissRequested?.Invoke(this, EventArgs.Empty);
                    await ViewModel.PasteNextQueueItemAsync(preferPlain);
                }

                return;
            }

            (bool copied, bool pasted) = await ViewModel.PasteNextQueueItemAsync(preferPlain);
            if (copied)
            {
                RaiseFeedback(
                    pasted
                        ? _localizationService.T("Clipboard.Feedback.Pasted")
                        : _localizationService.T("Clipboard.Feedback.CopiedFallback"),
                    WidgetFeedbackSeverity.Info,
                    "clipboard-paste");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] PasteQueueNext failed: {ex.Message}");
        }
    }

    private async void PasteQueueSkipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SkipQueueHeadAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] PasteQueueSkip failed: {ex.Message}");
        }
    }

    private void PasteQueueStopButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopPasteQueue();
        RaiseFeedback(
            _localizationService.T("Clipboard.Feedback.PasteQueueStopped"),
            WidgetFeedbackSeverity.Info,
            "clipboard-paste-queue");
    }

    private async void MergeCopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ExecuteMergeCopyAsync("\n");
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] MergeCopy failed: {ex.Message}");
        }
    }

    private void MergeCopyOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        void AddOption(string key, string separator)
        {
            var item = new MenuFlyoutItem { Text = _localizationService.T(key) };
            item.Click += async (_, _) =>
            {
                try
                {
                    flyout.Hide();
                    await ExecuteMergeCopyAsync(separator);
                }
                catch (Exception ex)
                {
                    App.Log($"[ClipboardWidgetContent] MergeCopy option failed: {ex.Message}");
                }
            };
            flyout.Items.Add(item);
        }

        AddOption("Clipboard.MultiSelect.JoinNewline", "\n");
        AddOption("Clipboard.MultiSelect.JoinBlankLine", "\n\n");
        AddOption("Clipboard.MultiSelect.JoinSpace", " ");
        AddOption("Clipboard.MultiSelect.JoinComma", ", ");
        flyout.ShowAt(anchor);
    }

    private async Task ExecuteMergeCopyAsync(string separator)
    {
        int count = ViewModel.MultiSelectedCount;
        if (await ViewModel.MergeCopySelectedAsync(separator))
        {
            RaiseFeedback(
                _localizationService.Format("Clipboard.Feedback.MergedCopied", count),
                WidgetFeedbackSeverity.Info,
                "clipboard-merge-copy");
        }
    }

    private async void MergeSaveQuickCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await MergeSaveSelectedToQuickCaptureAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] MergeSaveQuickCapture failed: {ex.Message}");
        }
    }

    private async void EnqueueSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = await ViewModel.EnqueueSelectedToPasteQueueAsync();
            if (count > 0)
            {
                RaiseFeedback(
                    _localizationService.Format("Clipboard.Feedback.PasteQueueEnqueued", count),
                    WidgetFeedbackSeverity.Info,
                    "clipboard-paste-queue");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] EnqueueSelected failed: {ex.Message}");
        }
    }

    private async void BatchPinButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.BatchPinSelectedAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] BatchPin failed: {ex.Message}");
        }
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClosePeekFlyout();
            await ViewModel.BatchDeleteSelectedAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] BatchDelete failed: {ex.Message}");
        }
    }

    private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExitMultiSelectMode();
    }

    private void ResumeBannerButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResumeMonitoring();
        RaiseFeedback(
            _localizationService.T("Clipboard.Feedback.Resumed"),
            WidgetFeedbackSeverity.Info,
            "clipboard-pause-state");
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSearchBox();
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSearchOpen = false;
        if (IsQuickPastePopup)
        {
            ItemsList.Focus(FocusState.Programmatic);
        }
    }

    private void OpenSearchBox(string? seedText = null)
    {
        ViewModel.IsSearchOpen = true;
        if (!string.IsNullOrEmpty(seedText))
        {
            ViewModel.SearchText = seedText;
        }

        if (!IsQuickPastePopup)
        {
            DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this);
        }

        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(SearchTextBox);
        SearchTextBox.UpdateLayout();
        if (SearchTextBox.Focus(FocusState.Programmatic))
        {
            if (!string.IsNullOrEmpty(seedText))
            {
                SearchTextBox.SelectionStart = SearchTextBox.Text.Length;
                SearchTextBox.SelectionLength = 0;
            }
            else
            {
                SearchTextBox.SelectAll();
            }

            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isDisposed || !ViewModel.IsSearchOpen)
            {
                return;
            }

            SearchTextBox.Focus(FocusState.Programmatic);
            if (!string.IsNullOrEmpty(seedText))
            {
                SearchTextBox.SelectionStart = SearchTextBox.Text.Length;
                SearchTextBox.SelectionLength = 0;
            }
            else
            {
                SearchTextBox.SelectAll();
            }
        });
    }

    private async void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

            if (e.Key == VirtualKey.Escape &&
                !DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                if (_activePeekFlyout is not null)
                {
                    e.Handled = true;
                    ClosePeekFlyout();
                    return;
                }

                e.Handled = true;
                if (IsQuickPastePopup)
                {
                    if (!string.IsNullOrEmpty(ViewModel.SearchText))
                    {
                        ViewModel.SearchText = string.Empty;
                    }
                    else
                    {
                        DismissRequested?.Invoke(this, EventArgs.Empty);
                    }

                    return;
                }

                ViewModel.IsSearchOpen = false;
                ItemsList.Focus(FocusState.Programmatic);
            }
            else if (IsQuickPastePopup && e.Key == VirtualKey.Down && ViewModel.VisibleItems.Count > 0)
            {
                e.Handled = true;
                int nextIndex = Math.Clamp(ItemsList.SelectedIndex + 1, 0, ViewModel.VisibleItems.Count - 1);
                ItemsList.SelectedIndex = nextIndex;
                ItemsList.ScrollIntoView(ViewModel.VisibleItems[nextIndex]);
            }
            else if (IsQuickPastePopup && e.Key == VirtualKey.Up && ViewModel.VisibleItems.Count > 0)
            {
                e.Handled = true;
                int prevIndex = Math.Clamp(ItemsList.SelectedIndex - 1, 0, ViewModel.VisibleItems.Count - 1);
                ItemsList.SelectedIndex = prevIndex;
                ItemsList.ScrollIntoView(ViewModel.VisibleItems[prevIndex]);
            }
            else if (e.Key == VirtualKey.Enter && ViewModel.VisibleItems.Count > 0)
            {
                e.Handled = true;
                ClipboardItemViewModel target = (ItemsList.SelectedItem as ClipboardItemViewModel) ?? ViewModel.VisibleItems[0];
                bool preferPlain = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
                await ExecuteQuickPasteOrPrimaryAsync(target, preferPlain);
            }
            else if (e.Key == VirtualKey.Down && ViewModel.VisibleItems.Count > 0)
            {
                e.Handled = true;
                ItemsList.SelectedIndex = 0;
                ItemsList.Focus(FocusState.Keyboard);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] SearchTextBox_KeyDown failed: {ex.Message}");
        }
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            bool ctrl = IsKeyDown(VirtualKey.Control);
            bool alt = IsKeyDown(VirtualKey.Menu);
            bool shift = IsKeyDown(VirtualKey.Shift);
            bool win = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);

            if (e.Key == VirtualKey.Escape &&
                !DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                if (_activePeekFlyout is not null)
                {
                    e.Handled = true;
                    ClosePeekFlyout();
                    return;
                }

                if (ViewModel.IsMultiSelectMode)
                {
                    e.Handled = true;
                    ViewModel.ExitMultiSelectMode();
                    return;
                }

                if (IsQuickPastePopup)
                {
                    e.Handled = true;
                    DismissRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            if (!IsQuickPastePopup && ctrl && !alt && !win && e.Key == VirtualKey.A && e.OriginalSource is not TextBox)
            {
                e.Handled = true;
                ViewModel.SelectAllVisible();
                return;
            }

            if (ctrl && !alt && e.Key == VirtualKey.F)
            {
                e.Handled = true;
                OpenSearchBox();
                return;
            }

            // #1..#9 Quick-Slot Paste/Copy (Alt+1..9 or Ctrl+1..9)
            if ((alt || ctrl) && !win && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
            {
                int slotIndex = (int)e.Key - (int)VirtualKey.Number1;
                if (slotIndex >= 0 && slotIndex < ViewModel.VisibleItems.Count)
                {
                    e.Handled = true;
                    ClosePeekFlyout();
                    ClipboardItemViewModel target = ViewModel.VisibleItems[slotIndex];
                    ItemsList.SelectedItem = target;
                    bool preferPlain = shift || ViewModel.DefaultPastePlainText;
                    await ExecuteQuickPasteOrPrimaryAsync(target, preferPlain);
                }

                return;
            }

            // CJK IME composition start (VK_PROCESSKEY = 229): open & focus SearchTextBox synchronously
            // without swallowing the key event so TSF routes the composition into the TextBox.
            if (!ViewModel.IsSearchOpen &&
                _activePeekFlyout is null &&
                !ctrl &&
                !alt &&
                !win &&
                e.Key == (VirtualKey)229 &&
                e.OriginalSource is not TextBox)
            {
                OpenSearchBox();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] Root_KeyDown failed: {ex.Message}");
        }
    }

    private void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (ViewModel.IsSearchOpen ||
            _activePeekFlyout is not null ||
            IsKeyDown(VirtualKey.Control) ||
            IsKeyDown(VirtualKey.Menu) ||
            IsKeyDown(VirtualKey.LeftWindows) ||
            IsKeyDown(VirtualKey.RightWindows))
        {
            return;
        }

        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        char ch = args.Character;
        if (char.IsControl(ch) || ch <= ' ')
        {
            return;
        }

        args.Handled = true;
        if (ch == '/')
        {
            OpenSearchBox();
            return;
        }

        OpenSearchBox(ch.ToString());
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (ItemsList.SelectedItem is not ClipboardItemViewModel selected)
            {
                return;
            }

            bool ctrl = IsKeyDown(VirtualKey.Control);
            bool shift = IsKeyDown(VirtualKey.Shift);

            switch (e.Key)
            {
                case VirtualKey.Space:
                    e.Handled = true;
                    TogglePeekFlyout(selected);
                    break;

                case VirtualKey.Escape:
                    if (_activePeekFlyout is not null)
                    {
                        e.Handled = true;
                        ClosePeekFlyout();
                    }
                    else if (ViewModel.IsMultiSelectMode)
                    {
                        e.Handled = true;
                        ViewModel.ExitMultiSelectMode();
                    }
                    else if (IsQuickPastePopup)
                    {
                        e.Handled = true;
                        DismissRequested?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case VirtualKey.Enter:
                    e.Handled = true;
                    ClosePeekFlyout();
                    bool preferPlain = shift || ViewModel.DefaultPastePlainText;
                    await ExecuteQuickPasteOrPrimaryAsync(selected, preferPlain);
                    break;

                case VirtualKey.C when ctrl:
                    e.Handled = true;
                    await CopyWithMicroFeedbackAsync(selected, shift || ViewModel.DefaultPastePlainText);
                    break;

                case VirtualKey.Delete:
                    e.Handled = true;
                    ClosePeekFlyout();
                    await ViewModel.DeleteItemAsync(selected);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] ItemsList_KeyDown failed: {ex.Message}");
        }
    }

    private void ItemsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.Item is ClipboardItemViewModel recycledVm)
            {
                recycledVm.ReleaseVisualAssets();
            }
            else if ((args.ItemContainer?.ContentTemplateRoot as FrameworkElement)?.DataContext is ClipboardItemViewModel fallbackVm)
            {
                fallbackVm.ReleaseVisualAssets();
            }

            return;
        }

        if (args.Item is ClipboardItemViewModel vm)
        {
            _ = vm.EnsureVisualAssetsLoadedAsync();
        }
    }

    private void CardRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardItemViewModel vm })
        {
            _ = vm.EnsureVisualAssetsLoadedAsync();
        }
    }

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetCardHoverVisual(sender as Border, isHovered: true);
    }

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetCardHoverVisual(sender as Border, isHovered: false);
    }

    private static void SetCardHoverVisual(Border? cardRoot, bool isHovered)
    {
        if (cardRoot?.Child is not Grid outerGrid)
        {
            return;
        }

        foreach (UIElement child in outerGrid.Children)
        {
            if (child is Border hoverBorder &&
                string.Equals(hoverBorder.Name, "HoverWash", StringComparison.Ordinal))
            {
                hoverBorder.Opacity = isHovered ? 1 : 0;
            }
            else if (child is Border actionsBorder &&
                     string.Equals(actionsBorder.Name, "ActionButtonsOverlay", StringComparison.Ordinal))
            {
                actionsBorder.Opacity = isHovered ? 1 : 0;
                actionsBorder.IsHitTestVisible = isHovered;
            }
            else if (child is Grid contentGrid &&
                     string.Equals(contentGrid.Name, "CardContentGrid", StringComparison.Ordinal))
            {
                foreach (UIElement contentChild in contentGrid.Children)
                {
                    if (contentChild is Grid headerGrid &&
                        string.Equals(headerGrid.Name, "HeaderRowGrid", StringComparison.Ordinal))
                    {
                        foreach (UIElement headerChild in headerGrid.Children)
                        {
                            if (headerChild is FrameworkElement rightCluster &&
                                string.Equals(rightCluster.Name, "HeaderMetaRightCluster", StringComparison.Ordinal))
                            {
                                rightCluster.Opacity = isHovered ? 0 : 1;
                            }
                        }
                    }
                    else if (contentChild is Grid bodyGrid &&
                             string.Equals(bodyGrid.Name, "BodyRowGrid", StringComparison.Ordinal))
                    {
                        foreach (UIElement bodyChild in bodyGrid.Children)
                        {
                            if (bodyChild is FrameworkElement compactCluster &&
                                string.Equals(compactCluster.Name, "CompactRightCluster", StringComparison.Ordinal))
                            {
                                compactCluster.Opacity = isHovered ? 0 : 1;
                            }
                        }
                    }
                }
            }
        }
    }

    private async void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement card ||
                card.DataContext is not ClipboardItemViewModel item ||
                IsDescendantOfActionButton(e.OriginalSource as DependencyObject, card))
            {
                return;
            }

            PointerPoint point = e.GetCurrentPoint(card);
            if (point.Properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                ItemsList.SelectedItem = item;
                if (string.Equals(ViewModel.ClickBehavior, "PasteOnSingle", StringComparison.OrdinalIgnoreCase))
                {
                    await PasteWithMicroFeedbackAsync(item, forcePlainText: true);
                }
                else
                {
                    await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] CardRoot_PointerPressed failed: {ex.Message}");
        }
    }

    private async void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement card ||
                card.DataContext is not ClipboardItemViewModel item ||
                IsDescendantOfActionButton(e.OriginalSource as DependencyObject, card))
            {
                return;
            }

            bool ctrl = IsKeyDown(VirtualKey.Control);
            bool shift = IsKeyDown(VirtualKey.Shift);

            if (ctrl && !IsQuickPastePopup)
            {
                e.Handled = true;
                ItemsList.SelectedItem = item;
                if (shift && ViewModel.IsMultiSelectMode)
                {
                    ViewModel.SelectRangeToItem(item);
                }
                else
                {
                    ViewModel.ToggleMultiSelectItem(item);
                }

                return;
            }

            if (ViewModel.IsMultiSelectMode)
            {
                e.Handled = true;
                ItemsList.SelectedItem = item;
                if (shift)
                {
                    ViewModel.SelectRangeToItem(item);
                }
                else
                {
                    ViewModel.ToggleMultiSelectItem(item);
                }

                return;
            }

            ItemsList.SelectedItem = item;
            bool forcePlainText = shift || ViewModel.DefaultPastePlainText;

            if (IsQuickPastePopup)
            {
                e.Handled = true;
                await ExecuteQuickPasteOrPrimaryAsync(item, forcePlainText);
                return;
            }

            string mode = ViewModel.ClickBehavior;

            if (string.Equals(mode, "PasteOnSingle", StringComparison.OrdinalIgnoreCase))
            {
                await PasteWithMicroFeedbackAsync(item, forcePlainText);
            }
            else if (string.Equals(mode, "CopyOnSingle", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "CopyOnSinglePasteOnDouble", StringComparison.OrdinalIgnoreCase))
            {
                await CopyWithMicroFeedbackAsync(item, forcePlainText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] CardRoot_Tapped failed: {ex.Message}");
        }
    }

    private async void CardRoot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsMultiSelectMode ||
                IsQuickPastePopup ||
                sender is not FrameworkElement card ||
                card.DataContext is not ClipboardItemViewModel item ||
                IsDescendantOfActionButton(e.OriginalSource as DependencyObject, card))
            {
                return;
            }

            e.Handled = true;
            ItemsList.SelectedItem = item;
            bool forcePlainText = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
            string mode = ViewModel.ClickBehavior;

            if (string.Equals(mode, "CopyOnSinglePasteOnDouble", StringComparison.OrdinalIgnoreCase))
            {
                await PasteWithMicroFeedbackAsync(item, forcePlainText);
            }
            else if (string.Equals(mode, "CopyOnDouble", StringComparison.OrdinalIgnoreCase))
            {
                await CopyWithMicroFeedbackAsync(item, forcePlainText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] CardRoot_DoubleTapped failed: {ex.Message}");
        }
    }

    private async void QuickActionItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button { Tag: ClipboardItemViewModel item })
            {
                return;
            }

            if (item.Kind == ClipboardItemKind.Text &&
                item.TextSubKind == ClipboardTextSubKind.Url &&
                Uri.TryCreate(item.Model.PlainText?.Trim(), UriKind.Absolute, out Uri? uri))
            {
                Win32Helper.OpenFile(uri.AbsoluteUri);
                return;
            }

            if (item.Kind == ClipboardItemKind.Image)
            {
                await SaveImageAsAsync(item);
                return;
            }

            if (item.Kind == ClipboardItemKind.Files)
            {
                string? existingPath = item.Model.FilePaths.FirstOrDefault(
                    static p => File.Exists(p) || Directory.Exists(p));
                if (!string.IsNullOrWhiteSpace(existingPath))
                {
                    Win32Helper.ShowInExplorer(existingPath);
                }

                return;
            }

            if (item.Kind == ClipboardItemKind.Text &&
                item.TextSubKind is ClipboardTextSubKind.Html or ClipboardTextSubKind.Rtf)
            {
                await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] QuickActionItemButton_Click failed: {ex.Message}");
        }
    }

    private void PeekEditItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardItemViewModel item } btn)
        {
            ItemsList.SelectedItem = item;
            TogglePeekFlyout(item, triggerElement: btn);
        }
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: ClipboardItemViewModel item })
            {
                bool forcePlainText = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
                await CopyWithMicroFeedbackAsync(item, forcePlainText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] CopyItemButton_Click failed: {ex.Message}");
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: ClipboardItemViewModel item })
            {
                await ViewModel.TogglePinAsync(item);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] PinItemButton_Click failed: {ex.Message}");
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: ClipboardItemViewModel item })
            {
                if (_peekItem?.Id == item.Id)
                {
                    ClosePeekFlyout();
                }

                await ViewModel.DeleteItemAsync(item);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] DeleteItemButton_Click failed: {ex.Message}");
        }
    }

    private void CardRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card ||
            card.DataContext is not ClipboardItemViewModel item)
        {
            return;
        }

        e.Handled = true;
        ItemsList.SelectedItem = item;
        MenuFlyout flyout = BuildRestrainedContextMenu(item);
        flyout.ShowAt(card, e.GetPosition(card));
    }

    private MenuFlyout BuildRestrainedContextMenu(ClipboardItemViewModel item)
    {
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        // 1. Primary Action (Paste or Copy depending on ClickBehavior / QuickPaste popup)
        bool offersDirectPaste =
            IsQuickPastePopup ||
            string.Equals(ViewModel.ClickBehavior, "CopyOnSinglePasteOnDouble", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ViewModel.ClickBehavior, "PasteOnSingle", StringComparison.OrdinalIgnoreCase);

        if (offersDirectPaste)
        {
            var pasteMenuItem = CreateMenuItem("Clipboard.Action.Paste", "\uE77F");
            pasteMenuItem.Click += async (_, _) => await RunSafeAsync(async () =>
            {
                flyout.Hide();
                await ExecuteQuickPasteOrPrimaryAsync(item, ViewModel.DefaultPastePlainText);
            }, "Menu.Paste");
            flyout.Items.Add(pasteMenuItem);
        }
        else
        {
            var copyMenuItem = CreateMenuItem("Clipboard.Action.Copy", "\uE8C8");
            copyMenuItem.Click += async (_, _) => await RunSafeAsync(async () =>
            {
                flyout.Hide();
                await CopyWithMicroFeedbackAsync(item, ViewModel.DefaultPastePlainText);
            }, "Menu.Copy");
            flyout.Items.Add(copyMenuItem);
        }

        // 2. Dynamic Contextual Action
        MenuFlyoutItem? contextualItem = BuildContextualActionItem(item, flyout);
        if (contextualItem is not null)
        {
            flyout.Items.Add(contextualItem);
        }

        // 3. Offline Image OCR or Smart Text Clean Transforms
        if (item.CanExtractOcr)
        {
            var ocrItem = CreateMenuItem("Clipboard.Action.ExtractOcr", "\uE8FE");
            ocrItem.Click += (_, _) =>
            {
                flyout.Hide();
                DispatcherQueue?.TryEnqueue(async () =>
                {
                    await Task.Yield();
                    if (!_isDisposed)
                    {
                        TogglePeekFlyout(item, focusNote: false, autoRunOcr: true);
                    }
                });
            };
            flyout.Items.Add(ocrItem);
        }
        else if (item.Kind == ClipboardItemKind.Text && !item.IsMaskedSecret)
        {
            string sampleText = item.Model.PlainText ?? item.Model.SummaryText ?? string.Empty;
            if (ClipboardTextTransformService.TryCleanTrackingUrl(sampleText, out _))
            {
                var cleanUrlItem = CreateMenuItem("Clipboard.Action.CopyCleanUrl", "\uE71B");
                cleanUrlItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    flyout.Hide();
                    if (await ViewModel.CopyCleanTrackingUrlAsync(item))
                    {
                        ShowInlineCheckmark(item);
                        RaiseFeedback(
                            _localizationService.T("Clipboard.Feedback.CleanUrlCopied"),
                            WidgetFeedbackSeverity.Info,
                            "clipboard-clean-url");
                    }
                }, "Menu.CopyCleanUrl");
                flyout.Items.Add(cleanUrlItem);
            }

            if (CanOfferJsonTransform(item, sampleText))
            {
                var formatJsonItem = CreateMenuItem("Clipboard.Action.FormatJsonCopy", "\uE943");
                formatJsonItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    flyout.Hide();
                    if (await ViewModel.CopyFormattedJsonAsync(item, indented: true))
                    {
                        ShowInlineCheckmark(item);
                        RaiseFeedback(
                            _localizationService.T("Clipboard.Feedback.JsonFormattedCopied"),
                            WidgetFeedbackSeverity.Info,
                            "clipboard-json");
                    }
                }, "Menu.FormatJson");
                flyout.Items.Add(formatJsonItem);

                var minifyJsonItem = CreateMenuItem("Clipboard.Action.MinifyJsonCopy", "\uE943");
                minifyJsonItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    flyout.Hide();
                    if (await ViewModel.CopyFormattedJsonAsync(item, indented: false))
                    {
                        ShowInlineCheckmark(item);
                        RaiseFeedback(
                            _localizationService.T("Clipboard.Feedback.JsonMinifiedCopied"),
                            WidgetFeedbackSeverity.Info,
                            "clipboard-json");
                    }
                }, "Menu.MinifyJson");
                flyout.Items.Add(minifyJsonItem);
            }
            else if (CanOfferPdfLineMerge(item, sampleText))
            {
                var mergeLinesItem = CreateMenuItem("Clipboard.Action.MergeLineBreaksCopy", "\uE8E4");
                mergeLinesItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    flyout.Hide();
                    if (await ViewModel.CopyMergedPdfLinesAsync(item))
                    {
                        ShowInlineCheckmark(item);
                        RaiseFeedback(
                            _localizationService.T("Clipboard.Feedback.MergedLinesCopied"),
                            WidgetFeedbackSeverity.Info,
                            "clipboard-merge-lines");
                    }
                }, "Menu.MergeLineBreaks");
                flyout.Items.Add(mergeLinesItem);
            }
        }

        // 4. Preview, Inline Edit & Note
        var editNoteMenuItem = CreateMenuItem("Clipboard.Action.EditAndNote", "\uE70F");
        editNoteMenuItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue?.TryEnqueue(async () =>
            {
                await Task.Yield();
                if (!_isDisposed)
                {
                    TogglePeekFlyout(item, focusNote: true);
                }
            });
        };
        flyout.Items.Add(editNoteMenuItem);

        // 5. Pin / Unpin
        var pinMenuItem = CreateMenuItem(
            item.IsPinned ? "Clipboard.Action.Unpin" : "Clipboard.Action.Pin",
            item.IsPinned ? "\uE77A" : "\uE718");
        pinMenuItem.Click += async (_, _) => await RunSafeAsync(async () =>
        {
            flyout.Hide();
            await ViewModel.TogglePinAsync(item);
        }, "Menu.TogglePin");
        flyout.Items.Add(pinMenuItem);

        // 6. Workflow Handoffs (Save to QuickCapture / Create Todo / Paste Queue / Multi-Select)
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (CanSaveToQuickCapture())
        {
            var saveToQuickCaptureItem = CreateMenuItem("Clipboard.Action.SaveToQuickCapture", "\uE77B");
            saveToQuickCaptureItem.Click += async (_, _) => await RunSafeAsync(async () =>
            {
                flyout.Hide();
                await SaveItemToQuickCaptureAsync(item);
            }, "Menu.SaveToQuickCapture");
            flyout.Items.Add(saveToQuickCaptureItem);
        }

        if (CanCreateTodo() && item.Kind == ClipboardItemKind.Text && !item.IsMaskedSecret)
        {
            var createTodoItem = CreateMenuItem("Clipboard.Action.CreateTodo", "\uE73A");
            createTodoItem.Click += async (_, _) => await RunSafeAsync(async () =>
            {
                flyout.Hide();
                await CreateTodoFromItemAsync(item);
            }, "Menu.CreateTodo");
            flyout.Items.Add(createTodoItem);
        }

        var enqueueQueueItem = CreateMenuItem("Clipboard.Action.EnqueuePasteQueue", "\uE8B5");
        enqueueQueueItem.Click += async (_, _) => await RunSafeAsync(async () =>
        {
            flyout.Hide();
            int count = await ViewModel.EnqueueSingleItemToPasteQueueAsync(item);
            if (count > 0)
            {
                RaiseFeedback(
                    _localizationService.Format("Clipboard.Feedback.PasteQueueEnqueued", count),
                    WidgetFeedbackSeverity.Info,
                    "clipboard-paste-queue");
            }
        }, "Menu.EnqueuePasteQueue");
        flyout.Items.Add(enqueueQueueItem);

        if (!IsQuickPastePopup && !ViewModel.IsMultiSelectMode)
        {
            var multiSelectItem = CreateMenuItem("Clipboard.Action.EnterMultiSelect", "\uE762");
            multiSelectItem.Click += (_, _) =>
            {
                flyout.Hide();
                ViewModel.EnterMultiSelectMode(item);
            };
            flyout.Items.Add(multiSelectItem);
        }

        // 7. Separator + Delete
        flyout.Items.Add(new MenuFlyoutSeparator());
        var deleteMenuItem = CreateMenuItem("Clipboard.Action.Delete", "\uE74D");
        deleteMenuItem.Click += async (_, _) => await RunSafeAsync(async () =>
        {
            flyout.Hide();
            if (_peekItem?.Id == item.Id)
            {
                ClosePeekFlyout();
            }

            await ViewModel.DeleteItemAsync(item);
        }, "Menu.Delete");
        flyout.Items.Add(deleteMenuItem);

        return flyout;
    }

    private MenuFlyoutItem? BuildContextualActionItem(ClipboardItemViewModel item, MenuFlyout owner)
    {
        switch (item.Kind)
        {
            case ClipboardItemKind.Text when item.TextSubKind == ClipboardTextSubKind.Url &&
                                            Uri.TryCreate(item.Model.PlainText?.Trim(), UriKind.Absolute, out Uri? uri):
            {
                var openUrlItem = CreateMenuItem("Clipboard.Action.OpenLink", "\uE71B");
                openUrlItem.Click += (_, _) =>
                {
                    owner.Hide();
                    Win32Helper.OpenFile(uri.AbsoluteUri);
                };
                return openUrlItem;
            }

            case ClipboardItemKind.Text:
            {
                var copyPlainItem = CreateMenuItem("Clipboard.Action.CopyPlainText", "\uE8E4");
                copyPlainItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    owner.Hide();
                    await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
                }, "Menu.CopyPlainText");
                return copyPlainItem;
            }

            case ClipboardItemKind.Image:
            {
                var saveImageItem = CreateMenuItem("Clipboard.Action.SaveImageAs", "\uE74E");
                saveImageItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    owner.Hide();
                    await SaveImageAsAsync(item);
                }, "Menu.SaveImageAs");
                return saveImageItem;
            }

            case ClipboardItemKind.Files:
            {
                string? existingPath = item.Model.FilePaths.FirstOrDefault(
                    static p => File.Exists(p) || Directory.Exists(p));
                if (!string.IsNullOrWhiteSpace(existingPath))
                {
                    var revealItem = CreateMenuItem("Clipboard.Action.ShowInExplorer", "\uEC50");
                    revealItem.Click += (_, _) =>
                    {
                        owner.Hide();
                        Win32Helper.ShowInExplorer(existingPath);
                    };
                    return revealItem;
                }

                var copyPathsItem = CreateMenuItem("Clipboard.Action.CopyFilePaths", "\uE8C8");
                copyPathsItem.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    owner.Hide();
                    await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
                }, "Menu.CopyFilePaths");
                return copyPathsItem;
            }

            default:
                return null;
        }
    }

    private MenuFlyoutItem CreateMenuItem(string key, string glyph) =>
        new()
        {
            Text = _localizationService.T(key),
            Icon = new FontIcon { Glyph = glyph }
        };

    private bool CanSaveToQuickCapture()
    {
        return App.Current?.QuickCaptureService is not null &&
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture);
    }

    private bool CanCreateTodo()
    {
        return App.Current is not null &&
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo);
    }

    private static bool CanOfferJsonTransform(ClipboardItemViewModel item, string sampleText)
    {
        if (!item.Model.HasExternalTextPayload)
        {
            return ClipboardTextTransformService.CanTransformJson(sampleText);
        }

        if (string.Equals(item.Model.CodeLanguage, "JSON", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ReadOnlySpan<char> trimmed = sampleText.AsSpan().TrimStart();
        return trimmed.Length > 1 && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    private static bool CanOfferPdfLineMerge(ClipboardItemViewModel item, string sampleText)
    {
        if (item.Model.HasExternalTextPayload && item.TextSubKind == ClipboardTextSubKind.Code)
        {
            return false;
        }

        return ClipboardTextTransformService.CanMergePdfLineBreaks(sampleText);
    }

    private async Task SaveItemToQuickCaptureAsync(ClipboardItemViewModel item)
    {
        QuickCaptureService? quickCapture = App.Current?.QuickCaptureService;
        if (quickCapture is null)
        {
            return;
        }

        bool saved = false;
        switch (item.Kind)
        {
            case ClipboardItemKind.Image:
            {
                string? imagePath = ViewModel.ResolveOriginalImagePath(item.Model);
                if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                {
                    saved = await quickCapture.AddImageFileItemAsync(imagePath) is not null;
                }
                break;
            }

            case ClipboardItemKind.Files:
            {
                string[] existingFiles = item.Model.FilePaths.Where(File.Exists).ToArray();
                if (existingFiles.Length > 0)
                {
                    saved = await quickCapture.AddItemWithAttachmentsAsync(existingFiles, copyToManagedStorage: false) is not null;
                }
                else if (item.Model.FilePaths.Count > 0)
                {
                    await quickCapture.AddItemAsync(string.Join(Environment.NewLine, item.Model.FilePaths));
                    saved = true;
                }
                break;
            }

            default:
            {
                string text = await ViewModel.GetFullTextAsync(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await quickCapture.AddItemAsync(text);
                    saved = true;
                }
                break;
            }
        }

        if (saved)
        {
            RaiseFeedback(
                _localizationService.T("Clipboard.Feedback.SavedToQuickCapture"),
                WidgetFeedbackSeverity.Info,
                "clipboard-saved-quickcapture");
        }
    }

    private async Task MergeSaveSelectedToQuickCaptureAsync()
    {
        QuickCaptureService? quickCapture = App.Current?.QuickCaptureService;
        if (quickCapture is null || !CanSaveToQuickCapture())
        {
            return;
        }

        IReadOnlyList<ClipboardItemViewModel> selected = ViewModel.GetSelectedItemsInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        var textParts = new List<string>();
        var attachmentPaths = new List<string>();

        foreach (ClipboardItemViewModel item in selected)
        {
            switch (item.Kind)
            {
                case ClipboardItemKind.Image:
                {
                    string? imagePath = ViewModel.ResolveOriginalImagePath(item.Model);
                    if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                    {
                        attachmentPaths.Add(imagePath);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.Model.OcrText))
                    {
                        textParts.Add(item.Model.OcrText.Trim());
                    }
                    break;
                }

                case ClipboardItemKind.Files:
                {
                    bool anyExistingFile = false;
                    foreach (string path in item.Model.FilePaths)
                    {
                        if (File.Exists(path))
                        {
                            attachmentPaths.Add(path);
                            anyExistingFile = true;
                        }
                    }

                    if (!anyExistingFile && item.Model.FilePaths.Count > 0)
                    {
                        textParts.Add(string.Join(Environment.NewLine, item.Model.FilePaths));
                    }
                    break;
                }

                default:
                {
                    string fullText = (await ViewModel.GetFullTextAsync(item)).TrimEnd('\r', '\n');
                    if (!string.IsNullOrWhiteSpace(fullText))
                    {
                        textParts.Add(fullText);
                    }
                    break;
                }
            }
        }

        string mergedText = string.Join("\n\n", textParts);
        bool saved = false;

        if (!string.IsNullOrWhiteSpace(mergedText))
        {
            QuickCaptureItem created = await quickCapture.AddItemAsync(mergedText);
            if (attachmentPaths.Count > 0)
            {
                await quickCapture.AddAttachmentsAsync(created.Id, attachmentPaths, copyToManagedStorage: true);
            }

            saved = true;
        }
        else if (attachmentPaths.Count == 1 && selected.Count == 1 && selected[0].ShowImageCardPreview)
        {
            saved = await quickCapture.AddImageFileItemAsync(attachmentPaths[0]) is not null;
        }
        else if (attachmentPaths.Count > 0)
        {
            saved = await quickCapture.AddItemWithAttachmentsAsync(attachmentPaths, copyToManagedStorage: true) is not null;
        }

        if (saved)
        {
            ViewModel.ExitMultiSelectMode();
            RaiseFeedback(
                _localizationService.T("Clipboard.Feedback.SavedToQuickCapture"),
                WidgetFeedbackSeverity.Info,
                "clipboard-saved-quickcapture");
        }
    }

    private async Task CreateTodoFromItemAsync(ClipboardItemViewModel item)
    {
        if (App.Current is null)
        {
            return;
        }

        string fullText = await ViewModel.GetFullTextAsync(item);
        if (await App.Current.CreateTodoItemFromTextAsync(fullText))
        {
            RaiseFeedback(
                _localizationService.T("Clipboard.Feedback.TodoCreated"),
                WidgetFeedbackSeverity.Info,
                "clipboard-todo-created");
        }
    }

    private async Task ExecuteQuickPasteOrPrimaryAsync(ClipboardItemViewModel item, bool preferPlainText)
    {
        if (IsQuickPastePopup)
        {
            if (QuickPasteExecutor is { } executor)
            {
                await executor(item, preferPlainText);
            }
            else
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
                await PasteWithMicroFeedbackAsync(item, preferPlainText);
            }

            return;
        }

        if (ShouldPasteOnPrimaryKey(ViewModel.ClickBehavior))
        {
            await PasteWithMicroFeedbackAsync(item, preferPlainText);
        }
        else
        {
            await CopyWithMicroFeedbackAsync(item, preferPlainText);
        }
    }

    private async Task SaveImageAsAsync(ClipboardItemViewModel item)
    {
        string? sourcePath = ViewModel.ResolveOriginalImagePath(item.Model);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        IntPtr hwnd = ResolveHostWindowHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var picker = new FileSavePicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd))
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"Clipboard_{item.Model.CreatedAtUtc.ToLocalTime():yyyyMMdd_HHmmss}"
        };
        picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });

        PickFileResult? destination;
        _isModalDialogOpen = true;
        try
        {
            destination = await picker.PickSaveFileAsync();
        }
        finally
        {
            _isModalDialogOpen = false;
            if (IsQuickPastePopup && _isWindowVisible && !_isDisposed)
            {
                _ = Win32Helper.SetForegroundWindow(hwnd);
                ItemsList.Focus(FocusState.Programmatic);
            }
        }

        if (!string.IsNullOrWhiteSpace(destination?.Path))
        {
            ClipboardStorageFileOps.CopyFileOverwrite(sourcePath, destination.Path);
            RaiseFeedback(
                _localizationService.T("Clipboard.Feedback.ImageSaved"),
                WidgetFeedbackSeverity.Info,
                "clipboard-image-saved");
        }
    }

    private IntPtr ResolveHostWindowHandle()
    {
        if (XamlRoot?.ContentIslandEnvironment is { } env && env.AppWindowId.Value != 0)
        {
            IntPtr xamlHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
            if (xamlHwnd != IntPtr.Zero)
            {
                return xamlHwnd;
            }
        }

        if (App.Current?.WidgetManager?.GetFeatureWidget(WidgetKind.Clipboard) is { } host)
        {
            return host.WindowHandle;
        }

        return IntPtr.Zero;
    }

    private async Task CopyWithMicroFeedbackAsync(ClipboardItemViewModel item, bool forcePlainText)
    {
        bool copied = await ViewModel.CopyItemAsync(item, forcePlainText);
        if (!copied)
        {
            return;
        }

        ShowInlineCheckmark(item);
        RaiseFeedback(
            forcePlainText
                ? _localizationService.T("Clipboard.Feedback.CopiedPlainText")
                : _localizationService.T("Clipboard.Feedback.Copied"),
            WidgetFeedbackSeverity.Info,
            "clipboard-copy");
    }

    private async Task PasteWithMicroFeedbackAsync(ClipboardItemViewModel item, bool forcePlainText)
    {
        (bool copied, bool pasted) = await ViewModel.PasteItemAsync(item, forcePlainText);
        if (!copied)
        {
            return;
        }

        ShowInlineCheckmark(item);
        RaiseFeedback(
            pasted
                ? _localizationService.T("Clipboard.Feedback.Pasted")
                : _localizationService.T("Clipboard.Feedback.CopiedFallback"),
            WidgetFeedbackSeverity.Info,
            "clipboard-paste");
    }

    private void ShowInlineCheckmark(ClipboardItemViewModel item)
    {
        _copiedFeedbackCts?.Cancel();
        _copiedFeedbackCts?.Dispose();
        var cts = new CancellationTokenSource();
        _copiedFeedbackCts = cts;

        foreach (ClipboardItemViewModel candidate in ViewModel.VisibleItems)
        {
            if (!ReferenceEquals(candidate, item))
            {
                candidate.SetJustCopied(false);
            }
        }

        item.SetJustCopied(true);
        _ = ResetInlineCheckmarkAfterDelayAsync(item, cts.Token);
    }

    private async Task ResetInlineCheckmarkAfterDelayAsync(ClipboardItemViewModel item, CancellationToken token)
    {
        try
        {
            await Task.Delay(900, token);
            if (!_isDisposed)
            {
                item.SetJustCopied(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Spacebar / Pencil Peek, Note & Quick-Edit Flyout ──────────────────────

    private void TogglePeekFlyout(
        ClipboardItemViewModel item,
        bool focusNote = false,
        bool autoRunOcr = false,
        FrameworkElement? triggerElement = null)
    {
        IntPtr ownerHwnd = ResolveHostWindowHandle();
        string logicalKey = $"clipboard-peek:{item.Id}";
        if (!focusNote &&
            !autoRunOcr &&
            DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(
                ownerHwnd,
                logicalKey,
                triggerElement))
        {
            ClosePeekFlyout();
            return;
        }

        if (_activePeekFlyout is not null && _peekItem?.Id == item.Id && !focusNote && !autoRunOcr)
        {
            ClosePeekFlyout();
            return;
        }

        ClosePeekFlyout();

        FrameworkElement? anchor = (ItemsList.ContainerFromItem(item) as SelectorItem)?.ContentTemplateRoot as FrameworkElement
            ?? ItemsList as FrameworkElement;
        if (anchor is null)
        {
            return;
        }

        _peekItem = item;
        item.SetPeekSelected(true);

        var peekLoadCts = new CancellationTokenSource();
        _peekLoadCts = peekLoadCts;
        CancellationToken peekToken = peekLoadCts.Token;

        var rootStack = new StackPanel
        {
            Spacing = 8,
            MinWidth = 264,
            MaxWidth = 376
        };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string headerMeta = string.IsNullOrWhiteSpace(item.MetaText)
            ? item.RelativeTimeText
            : $"{item.MetaText} · {item.RelativeTimeText}";
        headerGrid.Children.Add(new TextBlock
        {
            Text = headerMeta,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });

        TextBlock? secretBodyBlock = null;
        string currentPeekRawText = item.Model.PlainText ?? item.Model.SummaryText ?? string.Empty;
        bool currentPeekTruncated = currentPeekRawText.Length > ClipboardWidgetViewModel.MaxPeekTextChars ||
                                    item.Model.CharacterCount > currentPeekRawText.Length;
        if (currentPeekRawText.Length > ClipboardWidgetViewModel.MaxPeekTextChars)
        {
            currentPeekRawText = ClipboardStore.TruncateSurrogateSafe(
                currentPeekRawText,
                ClipboardWidgetViewModel.MaxPeekTextChars);
        }

        var headerActionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerActionsPanel, 1);

        bool revealed = false;
        if (item.IsMaskedSecret)
        {
            var revealBtn = new Button
            {
                Content = _localizationService.T("Clipboard.Peek.ShowPlaintext"),
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 24,
                FontSize = 11.5,
                CornerRadius = new CornerRadius(4)
            };
            revealBtn.Click += (_, _) =>
            {
                revealed = !revealed;
                revealBtn.Content = _localizationService.T(
                    revealed ? "Clipboard.Peek.HidePlaintext" : "Clipboard.Peek.ShowPlaintext");
                if (secretBodyBlock is not null)
                {
                    secretBodyBlock.Text = FormatPeekBodyDisplayText(
                        currentPeekRawText,
                        currentPeekTruncated,
                        item.Model.CharacterCount,
                        maskSecret: !revealed);
                }
            };
            headerActionsPanel.Children.Add(revealBtn);
        }

        headerActionsPanel.Children.Add(
            DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                _localizationService.T("Common.Close"),
                ClosePeekFlyout,
                this));
        headerGrid.Children.Add(headerActionsPanel);

        rootStack.Children.Add(headerGrid);

        // User Note / Search Alias Box (auto-saves on Enter or flyout close)
        string initialNote = item.Model.Note ?? string.Empty;
        bool noteCommitted = false;
        var noteTextBox = new TextBox
        {
            Text = initialNote,
            PlaceholderText = _localizationService.T("Clipboard.Note.Placeholder"),
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(8, 4, 8, 4),
            MaxLength = 120
        };

        async Task CommitNoteIfChangedAsync()
        {
            if (noteCommitted || _isDisposed)
            {
                return;
            }

            string trimmedNote = noteTextBox.Text.Trim();
            if (string.Equals(trimmedNote, initialNote.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            noteCommitted = true;
            initialNote = trimmedNote;
            await ViewModel.UpdateItemNoteAsync(item, trimmedNote);
        }

        rootStack.Children.Add(noteTextBox);

        Image? peekImageElement = null;
        TextBox? editableBodyBox = null;
        if (item.ShowImageCardPreview)
        {
            string? fullPath = item.Kind == ClipboardItemKind.Image
                ? ViewModel.ResolveOriginalImagePath(item.Model)
                : item.Model.FilePaths.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 640
                };
                bitmap.UriSource = new Uri(fullPath);

                peekImageElement = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 256
                };

                var imageBorder = new Border
                {
                    MaxHeight = 260,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this),
                    Child = peekImageElement
                };
                rootStack.Children.Add(imageBorder);

                string cachedOcr = item.Model.OcrText?.Trim() ?? string.Empty;
                var ocrActionsRow = new Grid { ColumnSpacing = 6 };
                ocrActionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ocrActionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var ocrExtractBtn = new Button
                {
                    Padding = new Thickness(8, 3, 8, 3),
                    MinHeight = 26,
                    FontSize = 11.5,
                    CornerRadius = new CornerRadius(4),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        Children =
                        {
                            new FontIcon { Glyph = "\uE8FE", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock
                            {
                                Text = _localizationService.T("Clipboard.Action.ExtractOcr"),
                                FontSize = 11.5,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                };

                var ocrCopyBtn = new Button
                {
                    Content = _localizationService.T("Clipboard.Action.Copy"),
                    Padding = new Thickness(8, 3, 8, 3),
                    MinHeight = 26,
                    FontSize = 11.5,
                    CornerRadius = new CornerRadius(4),
                    Visibility = string.IsNullOrEmpty(cachedOcr) ? Visibility.Collapsed : Visibility.Visible
                };
                Grid.SetColumn(ocrCopyBtn, 1);

                var ocrResultBox = new TextBox
                {
                    AcceptsReturn = true,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    MinHeight = 64,
                    MaxHeight = 160,
                    Visibility = string.IsNullOrEmpty(cachedOcr) ? Visibility.Collapsed : Visibility.Visible
                };
                ScrollViewer.SetVerticalScrollBarVisibility(ocrResultBox, ScrollBarVisibility.Auto);
                ocrResultBox.Text = cachedOcr;

                bool ocrInFlight = false;
                async Task RunOcrExtractionAsync()
                {
                    if (ocrInFlight || _isDisposed || peekToken.IsCancellationRequested)
                    {
                        return;
                    }

                    ocrInFlight = true;
                    ocrExtractBtn.IsEnabled = false;
                    try
                    {
                        string? extracted = await ViewModel.ExtractOcrTextAsync(
                            item,
                            copyToClipboard: true,
                            cancellationToken: peekToken);
                        if (_isDisposed || peekToken.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            ocrResultBox.Text = extracted;
                            ocrResultBox.Visibility = Visibility.Visible;
                            ocrCopyBtn.Visibility = Visibility.Visible;
                            _activePeekFlyout?.RefreshBoundsFromContent();
                            ShowInlineCheckmark(item);
                            RaiseFeedback(
                                _localizationService.T("Clipboard.Feedback.OcrCopied"),
                                WidgetFeedbackSeverity.Info,
                                "clipboard-ocr");
                        }
                        else
                        {
                            RaiseFeedback(
                                _localizationService.T("Clipboard.Feedback.OcrEmpty"),
                                WidgetFeedbackSeverity.Info,
                                "clipboard-ocr");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[ClipboardWidgetContent] OCR extraction failed: {ex.Message}");
                    }
                    finally
                    {
                        ocrInFlight = false;
                        if (!_isDisposed)
                        {
                            ocrExtractBtn.IsEnabled = true;
                        }
                    }
                }

                ocrExtractBtn.Click += async (_, _) => await RunOcrExtractionAsync();
                ocrCopyBtn.Click += async (_, _) => await RunSafeAsync(async () =>
                {
                    string normalizedOcr = (ocrResultBox.Text ?? string.Empty)
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
                    if (!string.IsNullOrWhiteSpace(normalizedOcr) &&
                        await ViewModel.CopyGeneratedTextAsync(normalizedOcr))
                    {
                        ShowInlineCheckmark(item);
                        RaiseFeedback(
                            _localizationService.T("Clipboard.Feedback.OcrCopied"),
                            WidgetFeedbackSeverity.Info,
                            "clipboard-ocr");
                    }
                }, "Peek.CopyOcr");

                ocrActionsRow.Children.Add(ocrExtractBtn);
                ocrActionsRow.Children.Add(ocrCopyBtn);
                rootStack.Children.Add(ocrActionsRow);
                rootStack.Children.Add(ocrResultBox);

                if (autoRunOcr)
                {
                    _ = RunOcrExtractionAsync();
                }
            }
        }
        else if (item.Kind == ClipboardItemKind.Files)
        {
            var filesStack = new StackPanel { Spacing = 4 };
            foreach (string path in item.Model.FilePaths)
            {
                bool exists = File.Exists(path) || Directory.Exists(path);
                filesStack.Children.Add(new TextBlock
                {
                    Text = path,
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = exists ? 1.0 : 0.55,
                    TextDecorations = exists ? Windows.UI.Text.TextDecorations.None : Windows.UI.Text.TextDecorations.Strikethrough
                });
            }

            rootStack.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = filesStack
            });
        }
        else if (!item.IsMaskedSecret && (!item.Model.HasExternalTextPayload || item.Model.CharacterCount <= ClipboardWidgetViewModel.MaxPeekTextChars))
        {
            string editableBaseline = currentPeekRawText;
            bool isLoadingExternal = item.Model.HasExternalTextPayload;

            var editTextBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = item.TextSubKind == ClipboardTextSubKind.Code ? TextWrapping.NoWrap : TextWrapping.Wrap,
                FontSize = item.TextSubKind == ClipboardTextSubKind.Code ? 12 : 12.5,
                MinHeight = 84,
                MaxHeight = 220,
                IsReadOnly = isLoadingExternal
            };
            editableBodyBox = editTextBox;
            ScrollViewer.SetVerticalScrollBarVisibility(editTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(
                editTextBox,
                item.TextSubKind == ClipboardTextSubKind.Code ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            editTextBox.Text = editableBaseline;

            if (item.TextSubKind == ClipboardTextSubKind.Code || item.TextSubKind == ClipboardTextSubKind.Color)
            {
                editTextBox.FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI");
            }

            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6,
                Visibility = Visibility.Collapsed
            };

            var saveBtn = new Button
            {
                Content = _localizationService.T("Common.Save"),
                Padding = new Thickness(10, 3, 10, 4),
                MinHeight = 26,
                FontSize = 12
            };
            var saveAndCopyBtn = new Button
            {
                Content = _localizationService.T("Clipboard.Action.SaveAndCopy"),
                Padding = new Thickness(10, 3, 10, 4),
                MinHeight = 26,
                FontSize = 12
            };

            editTextBox.TextChanged += (_, _) =>
            {
                if (editTextBox.IsReadOnly)
                {
                    return;
                }

                bool hasEdits = !string.Equals(editTextBox.Text, editableBaseline, StringComparison.Ordinal) &&
                                !string.IsNullOrEmpty(editTextBox.Text);
                Visibility nextVisibility = hasEdits ? Visibility.Visible : Visibility.Collapsed;
                if (actionsPanel.Visibility != nextVisibility)
                {
                    actionsPanel.Visibility = nextVisibility;
                    _activePeekFlyout?.RefreshBoundsFromContent();
                }
            };

            async Task SaveEditedBodyAsync(bool copyAfterSave)
            {
                string edited = editTextBox.Text;
                if (string.IsNullOrEmpty(edited))
                {
                    return;
                }

                await CommitNoteIfChangedAsync();
                bool ok = await ViewModel.UpdateItemTextAsync(item, edited, copyAfterSave);
                ClosePeekFlyout();
                if (ok && copyAfterSave)
                {
                    RaiseFeedback(
                        _localizationService.T("Clipboard.Feedback.Copied"),
                        WidgetFeedbackSeverity.Info,
                        "clipboard-copy");
                }
            }

            saveBtn.Click += async (_, _) => await RunSafeAsync(() => SaveEditedBodyAsync(copyAfterSave: false), "Peek.SaveBody");
            saveAndCopyBtn.Click += async (_, _) => await RunSafeAsync(() => SaveEditedBodyAsync(copyAfterSave: true), "Peek.SaveAndCopyBody");

            actionsPanel.Children.Add(saveBtn);
            actionsPanel.Children.Add(saveAndCopyBtn);
            rootStack.Children.Add(editTextBox);
            rootStack.Children.Add(actionsPanel);

            if (isLoadingExternal)
            {
                _ = LoadExternalEditablePeekTextAsync(
                    item,
                    editTextBox,
                    loaded => editableBaseline = loaded,
                    peekToken);
            }
        }
        else
        {
            var textBlock = new TextBlock
            {
                Text = FormatPeekBodyDisplayText(
                    currentPeekRawText,
                    currentPeekTruncated,
                    item.Model.CharacterCount,
                    maskSecret: item.IsMaskedSecret),
                FontSize = 12.5,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };
            if (item.IsMaskedSecret)
            {
                textBlock.FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI");
            }

            secretBodyBlock = textBlock;
            rootStack.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = textBlock
            });

            if (item.Model.HasExternalTextPayload)
            {
                _ = LoadExternalPeekTextAsync(
                    item,
                    textBlock,
                    (loadedText, wasTruncated) =>
                    {
                        currentPeekRawText = loadedText;
                        currentPeekTruncated = wasTruncated;
                        return !item.IsMaskedSecret || revealed;
                    },
                    peekToken);
            }
        }

        var popover = new DeskBox.Views.WidgetEditorPopoverWindow(
            ownerHwnd,
            _localizationService.T("Widget.Clipboard.DefaultName"));

        noteTextBox.KeyDown += async (_, keyArgs) => await RunSafeAsync(async () =>
        {
            if (keyArgs.Key == VirtualKey.Enter &&
                !DeskBox.Views.WidgetEditorPopoverWindow.IsImeCompositionKeyEvent(keyArgs))
            {
                keyArgs.Handled = true;
                await CommitNoteIfChangedAsync();
                popover.ClosePopover(restoreOwnerForeground: true);
            }
        }, "Peek.NoteKeyDown");

        popover.PopoverClosed += async (_, _) => await RunSafeAsync(async () =>
        {
            if (peekImageElement is not null)
            {
                peekImageElement.Source = null;
            }

            if (ReferenceEquals(_peekLoadCts, peekLoadCts))
            {
                _peekLoadCts = null;
            }

            peekLoadCts.Cancel();
            peekLoadCts.Dispose();

            if (ReferenceEquals(_activePeekFlyout, popover))
            {
                _activePeekFlyout = null;
            }

            if (_peekItem is not null)
            {
                _peekItem.SetPeekSelected(false);
                _peekItem = null;
            }

            await CommitNoteIfChangedAsync();

            if (IsQuickPastePopup && _isWindowVisible && !_isDisposed && !_isModalDialogOpen)
            {
                UiDispatch.PostAfterCallback(() =>
                {
                    if (_isDisposed || !_isWindowVisible || _isModalDialogOpen || _activePeekFlyout is not null)
                    {
                        return;
                    }

                    IntPtr host = ResolveHostWindowHandle();
                    if (ClipboardPlatformInterop.ShouldKeepPopupOpenAfterFlyoutClose(host))
                    {
                        if (host != IntPtr.Zero)
                        {
                            _ = Win32Helper.SetForegroundWindow(host);
                        }

                        object? focused = XamlRoot is { } root
                            ? FocusManager.GetFocusedElement(root)
                            : null;
                        if (focused is not (TextBox or RichEditBox or PasswordBox or AutoSuggestBox))
                        {
                            ItemsList.Focus(FocusState.Programmatic);
                        }
                    }
                    else
                    {
                        DismissRequested?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
        }, "Peek.Closed");

        Control? initialFocusTarget = focusNote
            ? noteTextBox
            : (!autoRunOcr && editableBodyBox is { IsReadOnly: false }
                ? editableBodyBox
                : (!autoRunOcr ? noteTextBox : null));

        _activePeekFlyout = popover;
        popover.ShowAnchored(
            anchor,
            rootStack,
            DeskBox.Views.WidgetPopoverPlacement.Side,
            minWidthDip: 292,
            maxWidthDip: 416,
            initialFocusTarget: initialFocusTarget,
            triggerElement: triggerElement,
            logScope: "ClipboardPeek",
            logicalKey: logicalKey);
    }

    private async Task LoadExternalEditablePeekTextAsync(
        ClipboardItemViewModel item,
        TextBox targetBox,
        Action<string> updateBaseline,
        CancellationToken cancellationToken)
    {
        try
        {
            (string preview, bool wasTruncated) = await ViewModel.GetPeekTextAsync(
                item,
                ClipboardWidgetViewModel.MaxPeekTextChars,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || _isDisposed || !ReferenceEquals(_peekItem, item))
            {
                return;
            }

            updateBaseline(preview);
            targetBox.Text = preview;
            targetBox.IsReadOnly = wasTruncated;
            _activePeekFlyout?.RefreshBoundsFromContent();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadExternalPeekTextAsync(
        ClipboardItemViewModel item,
        TextBlock targetBlock,
        Func<string, bool, bool> updateStateAndShouldShowPlaintext,
        CancellationToken cancellationToken)
    {
        try
        {
            (string preview, bool wasTruncated) = await ViewModel.GetPeekTextAsync(
                item,
                ClipboardWidgetViewModel.MaxPeekTextChars,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || _isDisposed || !ReferenceEquals(_peekItem, item))
            {
                return;
            }

            bool showPlaintext = updateStateAndShouldShowPlaintext(preview, wasTruncated);
            targetBlock.Text = FormatPeekBodyDisplayText(
                preview,
                wasTruncated,
                item.Model.CharacterCount,
                maskSecret: !showPlaintext);
            _activePeekFlyout?.RefreshBoundsFromContent();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatPeekBodyDisplayText(
        string body,
        bool wasTruncated,
        int totalCharCount,
        bool maskSecret)
    {
        string rendered = maskSecret
            ? ClipboardClassifier.MaskSecretForDisplay(body)
            : body;
        if (!wasTruncated)
        {
            return rendered;
        }

        int shown = Math.Min(body.Length, ClipboardWidgetViewModel.MaxPeekTextChars);
        int total = Math.Max(totalCharCount, shown);
        return $"{rendered}\n\n… ({shown:N0} / {total:N0})";
    }

    private void ClosePeekFlyout()
    {
        _peekLoadCts?.Cancel();
        _peekLoadCts?.Dispose();
        _peekLoadCts = null;

        if (_activePeekFlyout is { } popover)
        {
            _activePeekFlyout = null;
            popover.ClosePopover(restoreOwnerForeground: true);
        }

        if (_peekItem is { } item)
        {
            item.SetPeekSelected(false);
            _peekItem = null;
        }
    }

    // ── Drag-Out & Drop-In ────────────────────────────────────────────────────

    internal static IReadOnlyList<ClipboardItemViewModel> ResolveMultiSelectDragBatch(
        ClipboardItemViewModel draggedItem,
        bool isMultiSelectMode,
        IReadOnlyList<ClipboardItemViewModel> selectedInOrder)
    {
        if (!isMultiSelectMode || !draggedItem.IsMultiSelected || selectedInOrder.Count <= 1)
        {
            return [draggedItem];
        }

        return selectedInOrder.Any(candidate => string.Equals(candidate.Id, draggedItem.Id, StringComparison.Ordinal))
            ? selectedInOrder
            : [draggedItem];
    }

    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        try
        {
            if (e.Items.FirstOrDefault() is not ClipboardItemViewModel item)
            {
                e.Cancel = true;
                return;
            }

            IReadOnlyList<ClipboardItemViewModel> dragBatch = ResolveMultiSelectDragBatch(
                item,
                ViewModel.IsMultiSelectMode,
                ViewModel.GetSelectedItemsInOrder());
            if (dragBatch.Count > 1)
            {
                var batchPaths = new List<string>();
                foreach (ClipboardItemViewModel entry in dragBatch)
                {
                    if (entry.Kind == ClipboardItemKind.Files)
                    {
                        batchPaths.AddRange(entry.Model.FilePaths.Where(static p => File.Exists(p) || Directory.Exists(p)));
                    }
                    else if (entry.Kind == ClipboardItemKind.Image)
                    {
                        string? imgPath = ViewModel.ResolveOriginalImagePath(entry.Model);
                        if (!string.IsNullOrWhiteSpace(imgPath) && File.Exists(imgPath))
                        {
                            batchPaths.Add(imgPath);
                        }
                    }
                }

                string[] distinctBatchPaths = batchPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (distinctBatchPaths.Length > 0)
                {
                    FileService fileService = App.Current?.FileService ?? new FileService();
                    if (!DeskBox.Controls.FileItemDragPackage.TryPreparePaths(
                            e.Data,
                            distinctBatchPaths,
                            fileService.GetStorageItems,
                            paths => paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count.ToString(),
                            "clipboard-multiselect",
                            out _))
                    {
                        e.Cancel = true;
                        return;
                    }

                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                    return;
                }

                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.Data.SetDataProvider(StandardDataFormats.Text, async request =>
                {
                    DataProviderDeferral deferral = request.GetDeferral();
                    try
                    {
                        string merged = await ViewModel.BuildMergedTextForSelectedAsync();
                        if (!string.IsNullOrEmpty(merged))
                        {
                            request.SetData(merged);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[ClipboardWidgetContent] Multi-select text drag failed: {ex.Message}");
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                });
                return;
            }

            switch (item.Kind)
            {
                case ClipboardItemKind.Files:
                {
                    string[] existing = item.Model.FilePaths
                        .Where(static p => File.Exists(p) || Directory.Exists(p))
                        .ToArray();
                    if (existing.Length == 0)
                    {
                        e.Cancel = true;
                        return;
                    }

                    FileService fileService = App.Current?.FileService ?? new FileService();
                    if (!FileItemDragPackage.TryPreparePaths(
                            e.Data,
                            existing,
                            fileService.GetStorageItems,
                            paths => paths.Count == 1
                                ? Path.GetFileName(paths[0])
                                : paths.Count.ToString(),
                            "clipboard",
                            out _))
                    {
                        e.Cancel = true;
                        return;
                    }

                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                    break;
                }

                case ClipboardItemKind.Image:
                {
                    string? imagePath = ViewModel.ResolveOriginalImagePath(item.Model);
                    if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    {
                        e.Cancel = true;
                        return;
                    }

                    FileService fileService = App.Current?.FileService ?? new FileService();
                    _ = FileItemDragPackage.TryPreparePaths(
                        e.Data,
                        [imagePath],
                        fileService.GetStorageItems,
                        static paths => Path.GetFileName(paths[0]),
                        "clipboard-image",
                        out _);
                    e.Data.RequestedOperation = DataPackageOperation.Copy;

                    var capturedModel = item.Model;
                    string fallbackImagePath = imagePath;
                    e.Data.SetDataProvider(StandardDataFormats.Bitmap, async request =>
                    {
                        DataProviderDeferral deferral = request.GetDeferral();
                        try
                        {
                            string exportPath = await ViewModel.PrepareDragExportImageAsync(capturedModel) ?? fallbackImagePath;
                            StorageFile file = await StorageFile.GetFileFromPathAsync(exportPath);
                            request.SetData(RandomAccessStreamReference.CreateFromFile(file));
                        }
                        catch (Exception ex)
                        {
                            App.Log($"[ClipboardWidgetContent] Drag image data provider failed: {ex.Message}");
                        }
                        finally
                        {
                            deferral.Complete();
                        }
                    });
                    break;
                }

                default:
                {
                    string inlineSnippet = item.Model.PlainText ?? item.Model.SummaryText;
                    if (string.IsNullOrEmpty(inlineSnippet) && !item.Model.HasExternalTextPayload)
                    {
                        e.Cancel = true;
                        return;
                    }

                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                    if (item.Model.HasExternalTextPayload)
                    {
                        ClipboardItemViewModel capturedItem = item;
                        e.Data.SetDataProvider(StandardDataFormats.Text, async request =>
                        {
                            DataProviderDeferral deferral = request.GetDeferral();
                            try
                            {
                                string full = await ViewModel.GetFullTextAsync(capturedItem);
                                request.SetData(full);
                            }
                            catch (Exception ex)
                            {
                                App.Log($"[ClipboardWidgetContent] Drag text data provider failed: {ex.Message}");
                            }
                            finally
                            {
                                deferral.Complete();
                            }
                        });
                    }
                    else
                    {
                        e.Data.SetText(inlineSnippet);
                    }

                    if (item.TextSubKind == ClipboardTextSubKind.Url &&
                        Uri.TryCreate(inlineSnippet.Trim(), UriKind.Absolute, out Uri? uri))
                    {
                        e.Data.SetWebLink(uri);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] ItemsList_DragItemsStarting failed: {ex.Message}");
            e.Cancel = true;
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        DataPackageView view = e.DataView;
        if (DeskBoxDragData.HasDroppedFiles(view))
        {
            DataPackageOperation negotiated = DeskBoxDragData.GetFileAssociationOperation(
                view,
                e.AllowedOperations);
            if (negotiated != DataPackageOperation.None)
            {
                e.AcceptedOperation = negotiated;
                e.DragUIOverride.Caption = _localizationService.T("Clipboard.Drop.AddToHistory");
            }

            return;
        }

        if (view.Contains(StandardDataFormats.Text) ||
            view.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _localizationService.T("Clipboard.Drop.AddToHistory");
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DataPackageOperation allowedOperations = e.AllowedOperations;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            DataPackageView view = e.DataView;
            if (DeskBoxDragData.HasDroppedFiles(view))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(view);
                if (batch.Files.Count > 0)
                {
                    int imported = await HandleNativeDroppedFilesAsync(batch.Files);
                    e.AcceptedOperation = imported > 0
                        ? DeskBoxDragData.GetFileAssociationOperation(view, allowedOperations)
                        : DataPackageOperation.None;
                    return;
                }
            }

            if (view.Contains(StandardDataFormats.WebLink))
            {
                Uri? uri = await view.GetWebLinkAsync();
                if (uri is not null && await ViewModel.ImportDroppedTextAsync(uri.AbsoluteUri))
                {
                    RaiseFeedback(_localizationService.T("Clipboard.Feedback.ImportedText"), WidgetFeedbackSeverity.Info, "clipboard-imported");
                    return;
                }
            }

            if (view.Contains(StandardDataFormats.Text))
            {
                string text = await view.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && await ViewModel.ImportDroppedTextAsync(text))
                {
                    RaiseFeedback(_localizationService.T("Clipboard.Feedback.ImportedText"), WidgetFeedbackSeverity.Info, "clipboard-imported");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] Drop import failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task RunSafeAsync(Func<Task> action, string context)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardWidgetContent] {context} failed: {ex.Message}");
        }
    }

    private void RaiseFeedback(string message, WidgetFeedbackSeverity severity, string key)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(new WidgetFeedbackRequest(message, severity, key)));
    }

    private static bool ShouldPasteOnPrimaryKey(string clickBehavior)
    {
        return string.Equals(clickBehavior, "CopyOnSinglePasteOnDouble", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(clickBehavior, "PasteOnSingle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static bool IsDescendantOfActionButton(DependencyObject? source, DependencyObject stopAt)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
