using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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
    private bool _isSynchronizingFilter;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;
    private bool _isDisposed;
    private Flyout? _activePeekFlyout;
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

        Loaded += ClipboardWidgetContent_Loaded;
        Unloaded += ClipboardWidgetContent_Unloaded;
    }

    public ClipboardWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse => !_isDisposed && ViewModel.IsInitialized;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public void SyncFromResidentViewModel()
    {
        SynchronizeFilterSelection();
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

        SynchronizeFilterSelection();
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
        Loaded -= ClipboardWidgetContent_Loaded;
        Unloaded -= ClipboardWidgetContent_Unloaded;
        Bindings?.StopTracking();
    }

    public void Dispose()
    {
        DisposeLeafOnly();
    }

    private async void ClipboardWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsInitialized)
        {
            await ViewModel.InitializeAsync();
        }
        else if (ViewModel.IsViewAttached && ViewModel.HasPendingRebuild)
        {
            ViewModel.RebuildVisibleItems();
        }

        SynchronizeFilterSelection();
        UpdateResponsiveLabels(RootGrid.ActualWidth);
        UpdateTimestampTimerState();
    }

    private void ClipboardWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        ClosePeekFlyout();
        _timestampTimer?.Stop();
        ViewModel.SetViewForegroundTracking(this, isActive: false);
    }

    private void UpdateTimestampTimerState()
    {
        bool isActivelyExpanded = !_isDisposed && IsLoaded && _isWindowVisible && !_isCompactCollapsed;
        ViewModel.SetViewForegroundTracking(this, isActivelyExpanded);
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

        ViewModel.SetCompactFilterLabels(width < 276);
    }

    private void SynchronizeFilterSelection()
    {
        _isSynchronizingFilter = true;
        try
        {
            int targetIndex = (int)ViewModel.ActiveFilter;
            if (FilterSegmented.SelectedIndex != targetIndex &&
                targetIndex >= 0 &&
                targetIndex < FilterSegmented.Items.Count)
            {
                FilterSegmented.SelectedIndex = targetIndex;
            }
        }
        finally
        {
            _isSynchronizingFilter = false;
        }
    }

    private void FilterSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingFilter)
        {
            return;
        }

        int idx = FilterSegmented.SelectedIndex;
        if (idx >= 0 && Enum.IsDefined(typeof(ClipboardFilterKind), idx))
        {
            ViewModel.ActiveFilter = (ClipboardFilterKind)idx;
        }
    }

    private void PauseToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePause();
        RaiseFeedback(
            ViewModel.IsPaused
                ? _localizationService.T("Clipboard.Banner.Paused")
                : _localizationService.T("Clipboard.Feedback.Resumed"),
            WidgetFeedbackSeverity.Info,
            "clipboard-pause-state");
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
    }

    private void OpenSearchBox()
    {
        ViewModel.IsSearchOpen = true;
        DispatcherQueue?.TryEnqueue(() =>
        {
            SearchTextBox.Focus(FocusState.Programmatic);
            SearchTextBox.SelectAll();
        });
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.IsSearchOpen = false;
            ItemsList.Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Down && ViewModel.VisibleItems.Count > 0)
        {
            e.Handled = true;
            ItemsList.SelectedIndex = 0;
            ItemsList.Focus(FocusState.Keyboard);
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsKeyDown(VirtualKey.Control);
        if (ctrl && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            OpenSearchBox();
        }
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
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
                break;

            case VirtualKey.Enter:
                e.Handled = true;
                ClosePeekFlyout();
                bool preferPlain = shift || ViewModel.DefaultPastePlainText;
                if (ShouldPasteOnPrimaryKey(ViewModel.ClickBehavior))
                {
                    await PasteWithMicroFeedbackAsync(selected, preferPlain);
                }
                else
                {
                    await CopyWithMicroFeedbackAsync(selected, preferPlain);
                }
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
        if (cardRoot?.Child is not Grid grid)
        {
            return;
        }

        foreach (UIElement child in grid.Children)
        {
            if (child is Border border)
            {
                if (string.Equals(border.Name, "HoverWash", StringComparison.Ordinal))
                {
                    border.Opacity = isHovered ? 1 : 0;
                }
                else if (string.Equals(border.Name, "ActionButtonsOverlay", StringComparison.Ordinal))
                {
                    border.Opacity = isHovered ? 1 : 0;
                    border.IsHitTestVisible = isHovered;
                }
            }
        }
    }

    private async void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
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

    private async void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card ||
            card.DataContext is not ClipboardItemViewModel item ||
            IsDescendantOfActionButton(e.OriginalSource as DependencyObject, card))
        {
            return;
        }

        ItemsList.SelectedItem = item;
        bool forcePlainText = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
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

    private async void CardRoot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card ||
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

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardItemViewModel item })
        {
            bool forcePlainText = IsKeyDown(VirtualKey.Shift) || ViewModel.DefaultPastePlainText;
            await CopyWithMicroFeedbackAsync(item, forcePlainText);
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardItemViewModel item })
        {
            await ViewModel.TogglePinAsync(item);
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
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
        var flyout = new MenuFlyout();

        // 1. Primary Action (Paste or Copy depending on ClickBehavior)
        bool offersDirectPaste =
            string.Equals(ViewModel.ClickBehavior, "CopyOnSinglePasteOnDouble", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ViewModel.ClickBehavior, "PasteOnSingle", StringComparison.OrdinalIgnoreCase);

        if (offersDirectPaste)
        {
            var pasteMenuItem = CreateMenuItem("Clipboard.Action.Paste", "\uE77F");
            pasteMenuItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await PasteWithMicroFeedbackAsync(item, ViewModel.DefaultPastePlainText);
            };
            flyout.Items.Add(pasteMenuItem);
        }
        else
        {
            var copyMenuItem = CreateMenuItem("Clipboard.Action.Copy", "\uE8C8");
            copyMenuItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await CopyWithMicroFeedbackAsync(item, ViewModel.DefaultPastePlainText);
            };
            flyout.Items.Add(copyMenuItem);
        }

        // 2. Dynamic Contextual Action (strictly at most 1 item!)
        MenuFlyoutItem? contextualItem = BuildContextualActionItem(item, flyout);
        if (contextualItem is not null)
        {
            flyout.Items.Add(contextualItem);
        }

        // 3. Pin / Unpin
        var pinMenuItem = CreateMenuItem(
            item.IsPinned ? "Clipboard.Action.Unpin" : "Clipboard.Action.Pin",
            item.IsPinned ? "\uE77A" : "\uE718");
        pinMenuItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ViewModel.TogglePinAsync(item);
        };
        flyout.Items.Add(pinMenuItem);

        // 4. Save to QuickCapture (when QuickCapture feature widget is enabled)
        if (CanSaveToQuickCapture())
        {
            var saveToQuickCaptureItem = CreateMenuItem("Clipboard.Action.SaveToQuickCapture", "\uE70F");
            saveToQuickCaptureItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await SaveItemToQuickCaptureAsync(item);
            };
            flyout.Items.Add(saveToQuickCaptureItem);
        }

        // 5. Separator + Delete
        flyout.Items.Add(new MenuFlyoutSeparator());
        var deleteMenuItem = CreateMenuItem("Clipboard.Action.Delete", "\uE74D");
        deleteMenuItem.Click += async (_, _) =>
        {
            flyout.Hide();
            if (_peekItem?.Id == item.Id)
            {
                ClosePeekFlyout();
            }

            await ViewModel.DeleteItemAsync(item);
        };
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
                copyPlainItem.Click += async (_, _) =>
                {
                    owner.Hide();
                    await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
                };
                return copyPlainItem;
            }

            case ClipboardItemKind.Image:
            {
                var saveImageItem = CreateMenuItem("Clipboard.Action.SaveImageAs", "\uE74E");
                saveImageItem.Click += async (_, _) =>
                {
                    owner.Hide();
                    await SaveImageAsAsync(item);
                };
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
                copyPathsItem.Click += async (_, _) =>
                {
                    owner.Hide();
                    await CopyWithMicroFeedbackAsync(item, forcePlainText: true);
                };
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

        PickFileResult? destination = await picker.PickSaveFileAsync();
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

    // ── Spacebar Peek Preview Flyout ──────────────────────────────────────────

    private void TogglePeekFlyout(ClipboardItemViewModel item)
    {
        if (_activePeekFlyout is not null && _peekItem?.Id == item.Id)
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
            MinWidth = 240,
            MaxWidth = 360
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
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
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
            Grid.SetColumn(revealBtn, 1);
            headerGrid.Children.Add(revealBtn);
        }

        rootStack.Children.Add(headerGrid);

        Image? peekImageElement = null;
        if (item.Kind == ClipboardItemKind.Image)
        {
            string? fullPath = ViewModel.ResolveOriginalImagePath(item.Model);
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
                    BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                    Child = peekImageElement
                };
                rootStack.Children.Add(imageBorder);
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
                MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = filesStack
            });
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
                MaxHeight = 260,
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

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            Content = rootStack
        };
        flyout.Closed += (_, _) =>
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

            if (ReferenceEquals(_activePeekFlyout, flyout))
            {
                _activePeekFlyout = null;
            }

            if (_peekItem is not null)
            {
                _peekItem.SetPeekSelected(false);
                _peekItem = null;
            }
        };

        _activePeekFlyout = flyout;
        flyout.ShowAt(anchor);
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

        if (_activePeekFlyout is { } flyout)
        {
            _activePeekFlyout = null;
            flyout.Hide();
        }

        if (_peekItem is { } item)
        {
            item.SetPeekSelected(false);
            _peekItem = null;
        }
    }

    // ── Drag-Out & Drop-In ────────────────────────────────────────────────────

    private async void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not ClipboardItemViewModel item)
        {
            e.Cancel = true;
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

                e.Data.RequestedOperation = DataPackageOperation.Copy;
                NativeShellFileDragProvider.TryAttach(e.Data, existing);
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

                e.Data.RequestedOperation = DataPackageOperation.Copy;
                try
                {
                    string exportPath = await ViewModel.PrepareDragExportImageAsync(item.Model) ?? imagePath;
                    StorageFile file = await StorageFile.GetFileFromPathAsync(exportPath);
                    e.Data.SetStorageItems(new[] { file });
                    e.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                }
                catch
                {
                    // Keep drag alive if StorageFile broker fails on edge paths
                }
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

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        DataPackageView view = e.DataView;
        if (view.Contains(StandardDataFormats.StorageItems) ||
            view.Contains(StandardDataFormats.Text) ||
            view.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _localizationService.T("Clipboard.Drop.AddToHistory");
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            DataPackageView view = e.DataView;
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
                string[] paths = items
                    .Select(static i => i.Path)
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();
                if (paths.Length > 0)
                {
                    await HandleNativeDroppedFilesAsync(paths);
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
