using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskBox.Controls.Search;

internal interface ISearchRecommendationGridHost
{
    bool IsPopupVisible { get; }
    bool IsWindowClosed { get; }
    bool IsDragOutInProgress { get; }
    FrameworkElement PopupBoundsRoot { get; }
    bool IsAnyOverlayOpen { get; }
    void HidePopup();
    void FocusSearchBox();
    void FocusPopupRoot();
    void OpenFolderOverlay(RecommendedGridEntry folderEntry, bool startRename = false);
    void CloseOverlays(bool immediate = false);
    void CancelOverlayDrag();
    void RefreshOverlayIcons();
    void UpdateRecommendationHeaderState();
    void SyncEditModeChrome(bool isEditMode, int multiSelectedCount);
    void TryPreviewSelectedItem();
    bool TryStartRecommendedExternalDragOut(
        UIElement dragSource,
        PointerRoutedEventArgs e,
        IReadOnlyList<string> candidatePaths,
        string originTag,
        Action suspendInternalDrag);
    void QueueRunItemAsAdministrator(SearchResultItem item);
}

public sealed partial class RecommendedAppsGridControl : UserControl
{
    private SearchPopupViewModel _viewModel = null!;
    private SettingsService _settingsService = null!;
    private LocalizationService _localizationService = null!;
    private ISearchRecommendationGridHost? _host;
    private CancellationTokenSource? _skeletonDelayCancellation;
    private int? _pendingEntranceStyle;
    private DispatcherTimer? _entranceGuardTimer;

    // Recommended-apps selection: tracks the currently highlighted app card.
    private int _selectedAppIndex = -1;
    private Grid? _selectedAppCard;
    private Grid? _hoveredAppCard;

    // Recommendation grid drag/merge, batch edit mode, and centered overlay state.
    private Grid? _recPointerCard;
    private RecommendedGridEntry? _recPressedEntry;
    private Point _recPointerStartInPanel;
    private bool _recIsDragging;
    private bool _recWasCtrlPressedOnDown;
    private bool _recAutoEnteredEditModeOnDown;
    private bool _isRecommendationEditMode;
    private readonly HashSet<string> _recMultiSelectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recRubberBandBaseKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rect> _recRubberBandKnownTileRects = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recDraggedAppKeys = [];
    private readonly HashSet<string> _recDraggedEntryKeys = new(StringComparer.OrdinalIgnoreCase);
    // Live reflow preview: translation-free base rects (snapshot per drag so
    // applying translations never corrupts decisions or metrics) and the
    // translations currently applied, so only touched tiles are cleared.
    // Decisions MUST come from these base rects — live rects include the
    // reflow translations, which fed pointer→decision→reflow→pointer
    // oscillation (2026-11-06 evening report: flicker + unreachable folder
    // merge zone).
    private readonly Dictionary<string, Rect> _recTileBaseRects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Numerics.Vector3> _recReflowTranslations = new(StringComparer.OrdinalIgnoreCase);
    // Drop-decision hysteresis: once a decision lands, the pointer owns its
    // anchor region (matched tile / append band, inflated) and the decision
    // is not re-evaluated until the pointer leaves it.
    private RecommendedDropDecision _recLastDropDecision = new(false, null, null, -1, default, default, default);
    private Rect _recDropAnchorRect;
    private bool _recHasDropAnchor;
    private string? _recLastDecisionSummary;
    private bool _recExternalDragAttempted;
    private bool _recHasCachedReflowState;
    private bool _recCachedReflowActive;
    private int _recCachedReflowTargetIndex = -1;
    private int _recCachedReflowRealizedCount = -1;
    private int _recCachedReflowEntriesCount = -1;
    private bool _isRecRubberBanding;
    private Point _recRubberBandStart;
    private Point _recRubberBandCurrent;
    private Point _recRubberBandPointerInViewport;
    private DispatcherTimer? _recRubberBandAutoScrollTimer;
    private FrameworkElement? _recMergeHoveredCard;
    private string? _lastEditFolderClickKey;
    private long _lastEditFolderClickTickMs;
    private bool _lastEditFolderSelectedBeforeClick;
    private const long EditFolderDoubleClickThresholdMs = 450;

    public RecommendedAppsGridControl()
    {
        InitializeComponent();
        RecommendedAppsRepeater.ElementPrepared += OnRecommendedAppsElementPrepared;
        RecommendedAppsRepeater.ElementClearing += OnRecommendedAppsElementClearing;
    }

    internal void Initialize(
        SearchPopupViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        ISearchRecommendationGridHost host)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _host = host;
    }

    internal bool IsEditMode => _isRecommendationEditMode;
    internal int SelectedIndex => _selectedAppIndex;
    internal int MultiSelectedCount => _recMultiSelectedKeys.Count;
    internal bool HasActiveInteraction => _recIsDragging || _isRecRubberBanding;
    internal bool IsAppsPanelVisible => RecommendedAppsPanel.Visibility == Visibility.Visible;
    internal bool IsSkeletonVisible => AppsSkeletonPanel.Visibility == Visibility.Visible;
    internal bool IsAllHiddenHintVisible => AllRecommendedHiddenHintPanel.Visibility == Visibility.Visible;

    internal void BindItemsSource()
    {
        RecommendedAppsRepeater.ItemsSource = _viewModel.RecommendedEntries;
    }

    internal void ApplyLocalizedStrings()
    {
        AllRecommendedHiddenHintText.Text = _localizationService.T("Search.Recommend.AllHiddenHint");
    }

    internal void SetAppsPanelVisibility(bool visible)
    {
        RecommendedAppsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetHomeContentState(bool hasEntries, bool hasHidden)
    {
        RecommendedAppsScrollViewer.Visibility = hasEntries
            ? Visibility.Visible
            : Visibility.Collapsed;
        AllRecommendedHiddenHintPanel.Visibility = !hasEntries && hasHidden
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void StartSkeletonDelay()
    {
        CancelSkeletonDelay();
        _skeletonDelayCancellation = new CancellationTokenSource();
        _ = ShowAppsSkeletonAfterDelayAsync(_skeletonDelayCancellation.Token);
    }

    internal void CancelSkeletonDelayAndHide()
    {
        CancelSkeletonDelay();
        HideAppsSkeleton();
    }

    private void CancelSkeletonDelay()
    {
        _skeletonDelayCancellation?.Cancel();
        _skeletonDelayCancellation?.Dispose();
        _skeletonDelayCancellation = null;
    }

    internal void ToggleEditMode() => SetRecommendationEditMode(!_isRecommendationEditMode);

    internal void DisableEditMode() => SetRecommendationEditMode(false);

    internal void ClearEditMultiSelection()
    {
        _recMultiSelectedKeys.Clear();
        RefreshAllRecommendedCardEditVisuals();
        NotifyEditChromeChanged();
    }

    internal void HideSelected() => HideSelectedRecommendations();

    internal void RefreshAfterLayoutUpdate()
    {
        PruneStaleMultiSelectedRecommendationKeys();
        RefreshAllRecommendedCardEditVisuals();
        NotifyEditChromeChanged();
    }

    internal void QueueDeferredEditRefresh()
    {
        RefreshAllRecommendedCardEditVisuals();
        RefreshRecommendedAppIcons();
    }

    internal bool HandleEnterKey(bool controlPressed)
    {
        if (_isRecommendationEditMode)
        {
            if (_selectedAppIndex >= 0 &&
                RecommendedAppsRepeater.ItemsSource is System.Collections.IList editRecEntries &&
                _selectedAppIndex < editRecEntries.Count &&
                editRecEntries[_selectedAppIndex] is RecommendedGridEntry editSelectedEntry)
            {
                if (!_recMultiSelectedKeys.Add(editSelectedEntry.EntryKey))
                {
                    _recMultiSelectedKeys.Remove(editSelectedEntry.EntryKey);
                }

                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            }

            return true;
        }

        if (_selectedAppIndex >= 0 &&
            RecommendedAppsRepeater.ItemsSource is System.Collections.IList recEntries &&
            _selectedAppIndex < recEntries.Count &&
            recEntries[_selectedAppIndex] is RecommendedGridEntry selectedEntry)
        {
            if (selectedEntry.IsFolder)
            {
                _host?.OpenFolderOverlay(selectedEntry);
            }
            else if (selectedEntry.AppItem is { } recApp)
            {
                if (controlPressed && !string.IsNullOrWhiteSpace(recApp.DetailPath))
                {
                    ShellLaunchService.FireAndForgetRevealInExplorer(recApp.DetailPath);
                    _host?.HidePopup();
                }
                else if (_viewModel.ExecuteItem(recApp))
                {
                    _host?.HidePopup();
                }
            }

            return true;
        }

        return false;
    }

    internal bool HandleKey(KeyRoutedEventArgs e) => HandleRecommendedAppsKey(e);

    internal void RefreshIcons() => RefreshRecommendedAppIcons();

    internal void RefreshEditVisuals() => RefreshAllRecommendedCardEditVisuals();

    internal void AnimateEntrance() => AnimateAppIconsEntrance();

    internal void StopEntranceGuard() => StopAndClearEntranceGuardTimer();

    internal void ClearSelection() => ClearRecommendedAppSelection();

    internal void ClearHoveredCard() => ClearHoveredRecommendedCard();

    internal void ResetDoubleClickTracking() => ResetEditFolderDoubleClickTracking();

    internal void SuspendDragForExternalHandoff() => SuspendRecommendedDragForExternalHandoff();

    internal void CancelDrag() => CancelRecommendedDrag();

    internal void EndRubberBand() => EndRecommendationRubberBand();

    internal void ClearRealizedVisuals()
    {
        int appChildCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < appChildCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is FrameworkElement card)
            {
                ClearRecommendedCardVisuals(card);
            }
        }
    }

    internal void Dispose()
    {
        RecommendedAppsRepeater.ElementPrepared -= OnRecommendedAppsElementPrepared;
        RecommendedAppsRepeater.ElementClearing -= OnRecommendedAppsElementClearing;
        CancelSkeletonDelay();
        StopAndClearEntranceGuardTimer();
        if (_recRubberBandAutoScrollTimer is { } recAutoScrollTimer)
        {
            _recRubberBandAutoScrollTimer = null;
            recAutoScrollTimer.Tick -= OnRecommendationRubberBandAutoScrollTick;
            try
            {
                recAutoScrollTimer.Stop();
            }
            catch
            {
            }
        }

        CancelRecommendedDrag();
        EndRecommendationRubberBand();
        _isRecommendationEditMode = false;
        _recMultiSelectedKeys.Clear();
        _recRubberBandBaseKeys.Clear();
        ClearRecommendedAppSelection();
        ClearRealizedVisuals();
        RecommendedAppsRepeater.ItemsSource = null;
        AppsSkeletonRepeater.ItemsSource = null;
        Bindings?.StopTracking();
        _host = null;
        Content = null;
    }

    private FrameworkElement GetPopupBoundsRoot() => _host?.PopupBoundsRoot ?? this;

    private Brush? ResolveLocalThemeBrush(string key) =>
        SearchRecommendationLayoutEngine.ResolveThemeBrush(GetPopupBoundsRoot(), key);

    private void NotifyEditChromeChanged() =>
        _host?.SyncEditModeChrome(_isRecommendationEditMode, _recMultiSelectedKeys.Count);

    private void ArmCardImplicitOffsetAnimation(FrameworkElement card) =>
        SearchRecommendationLayoutEngine.ArmImplicitOffsetAnimationAfterLayout(
            card,
            DispatcherQueue,
            () => _host is { IsWindowClosed: false, IsPopupVisible: true },
            static () => WindowsCompatibilityService.ShouldAnimate);

    private static void ClearRecommendedCardVisuals(FrameworkElement card)
    {
        SearchRecommendationLayoutEngine.DetachImplicitOffsetAnimation(card);
        if (card is Grid grid)
        {
            grid.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        card.Opacity = 1;
        if (SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(card, "CardContentHost") is { } contentHost)
        {
            SearchRecommendationLayoutEngine.ResetCardContentHostVisual(contentHost);
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(card, "CardEditSlotPlate") is { } editSlotPlate)
        {
            editSlotPlate.Visibility = Visibility.Collapsed;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(card, "CardSelectionPlate") is { } selectionPlate)
        {
            selectionPlate.Visibility = Visibility.Collapsed;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(card, "CardSelectionCheckBox") is { } selectionCheckBox)
        {
            selectionCheckBox.Visibility = Visibility.Collapsed;
            if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(selectionCheckBox, "CardSelectionCheckedBox") is { } checkedBox)
            {
                checkedBox.Visibility = Visibility.Collapsed;
            }
            if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(selectionCheckBox, "CardSelectionUncheckedBox") is { } uncheckedBox)
            {
                uncheckedBox.Visibility = Visibility.Visible;
                uncheckedBox.Opacity = 1;
            }
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Button>(card, "CardQuickHideButton") is { } quickHideButton)
        {
            quickHideButton.Visibility = Visibility.Collapsed;
            quickHideButton.Opacity = 1;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Image>(card, "SingleAppIcon") is { } singleImage)
        {
            singleImage.Source = null;
        }

        for (int slot = 0; slot < 4; slot++)
        {
            if (SearchRecommendationLayoutEngine.FindVisualChild<Image>(card, $"FolderMiniIcon{slot}") is { } miniImage)
            {
                miniImage.Source = null;
            }
        }

        card.DataContext = null;
    }

    private void OnRecommendedAppsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement card)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(card, true);
            _recHasCachedReflowState = false;
            SearchRecommendationLayoutEngine.DetachImplicitOffsetAnimation(card);
            RecommendedGridEntry? resolvedEntry =
                RecommendedAppsRepeater.ItemsSource is System.Collections.IList items &&
                args.Index >= 0 &&
                args.Index < items.Count
                    ? items[args.Index] as RecommendedGridEntry
                    : card.DataContext as RecommendedGridEntry;
            SyncRecommendedCardContent(card, resolvedEntry);
            ArmCardImplicitOffsetAnimation(card);
        }

        // If an entrance animation is pending, animate this freshly realized card
        // immediately. This is the reliable trigger because ItemsRepeater realizes
        // elements asynchronously; TryGetElement would still be null at call time.
        if (_pendingEntranceStyle is int style && args.Element is FrameworkElement element)
        {
            AnimateSingleAppCard(element, args.Index, style);
            if (RecommendedAppsRepeater.ItemsSource is System.Collections.ICollection coll &&
                args.Index >= coll.Count - 1)
            {
                _pendingEntranceStyle = null;
                StopAndClearEntranceGuardTimer();
            }
        }
    }

    private void SyncRecommendedCardContent(FrameworkElement card, RecommendedGridEntry? entry)
    {
        card.DataContext = entry;
        card.Opacity = _recIsDragging &&
                       entry is not null &&
                       _recDraggedEntryKeys.Contains(entry.EntryKey)
            ? 0.36
            : 1;

        if (card is Grid grid)
        {
            bool showHoverMask = ReferenceEquals(grid, _hoveredAppCard) &&
                                 !_recIsDragging &&
                                 !_isRecRubberBanding;
            grid.Background = showHoverMask
                ? ResolveLocalThemeBrush("SubtleFillColorSecondaryBrush")
                : SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<TextBlock>(card, "CardLabelTextBlock") is { } labelBlock)
        {
            labelBlock.Text = entry?.Label ?? string.Empty;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(card, "SingleIconHost") is { } singleIconHost)
        {
            singleIconHost.Visibility = entry?.SingleIconVisibility ?? Visibility.Collapsed;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(card, "FolderPreviewPlate") is { } folderPreviewPlate)
        {
            folderPreviewPlate.Visibility = entry?.FolderPreviewVisibility ?? Visibility.Collapsed;
        }

        // Always assign image.Source (even when Icon is null) so recycled
        // containers never flash a previous app's icon while async icons load.
        if (SearchRecommendationLayoutEngine.FindVisualChild<Image>(card, "SingleAppIcon") is { } image)
        {
            if (entry is { IsFolder: false, AppItem: { } item })
            {
                image.Source = item.Icon;
            }
            else
            {
                image.Source = null;
            }
        }

        PopulateFolderPreviewIcons(card, entry);
        ApplyRecommendedCardEditVisuals(card, entry);
    }

    private void OnRecommendedAppsElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement card)
        {
            return;
        }

        if (ReferenceEquals(_selectedAppCard, card))
        {
            _selectedAppCard = null;
            _selectedAppIndex = -1;
        }

        if (ReferenceEquals(_hoveredAppCard, card))
        {
            _hoveredAppCard = null;
        }

        // A recycled card must not carry a live-reflow translation into its
        // next incarnation.
        _recHasCachedReflowState = false;
        if (card.DataContext is RecommendedGridEntry clearingEntry)
        {
            _recTileBaseRects.Remove(clearingEntry.EntryKey);
            _recReflowTranslations.Remove(clearingEntry.EntryKey);
        }

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        if (ElementCompositionPreview.GetElementVisual(card) is { } clearingVisual)
        {
            clearingVisual.StopAnimation("Translation");
            // Translation is animatable but not a settable CLR property in
            // the WinUI 3 projection; it lives in the property store.
            clearingVisual.Properties.InsertVector3(
                "Translation",
                System.Numerics.Vector3.Zero);
        }

        ClearRecommendedCardVisuals(card);
    }

    private static void PopulateFolderPreviewIcons(FrameworkElement card, RecommendedGridEntry? entry)
    {
        for (int slot = 0; slot < 4; slot++)
        {
            if (SearchRecommendationLayoutEngine.FindVisualChild<Image>(card, $"FolderMiniIcon{slot}") is not { } miniImage)
            {
                continue;
            }

            if (entry is { IsFolder: true } && slot < entry.FolderItems.Count)
            {
                ImageSource? memberIcon = entry.FolderItems[slot].Icon;
                miniImage.Source = memberIcon;
                miniImage.Visibility = memberIcon is not null ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                miniImage.Source = null;
                miniImage.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RefreshRecommendedAppIcons()
    {
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is not FrameworkElement card ||
                card.DataContext is not RecommendedGridEntry entry)
            {
                continue;
            }

            if (!entry.IsFolder && entry.AppItem is { } item)
            {
                var image = SearchRecommendationLayoutEngine.FindVisualChild<Image>(card, "SingleAppIcon");
                if (image is not null && !ReferenceEquals(image.Source, item.Icon))
                {
                    image.Source = item.Icon;
                }
            }
            else if (entry.IsFolder)
            {
                PopulateFolderPreviewIcons(card, entry);
            }
        }

        _host?.RefreshOverlayIcons();
    }

    private void SetRecommendationEditMode(bool enabled)
    {
        if (enabled && !_viewModel.HasRecommendedEntries)
        {
            enabled = false;
        }

        if (_isRecommendationEditMode == enabled &&
            (enabled || (_recMultiSelectedKeys.Count == 0 && !_isRecRubberBanding)))
        {
            NotifyEditChromeChanged();
            NotifyEditChromeChanged();
            return;
        }

        _isRecommendationEditMode = enabled;
        ResetEditFolderDoubleClickTracking();
        if (!enabled)
        {
            _recMultiSelectedKeys.Clear();
            _recRubberBandBaseKeys.Clear();
            _recRubberBandKnownTileRects.Clear();
            EndRecommendationRubberBand();
        }

        ClearRecommendedAppSelection();
        NotifyEditChromeChanged();
        RefreshAllRecommendedCardEditVisuals();
        NotifyEditChromeChanged();
    }

    private void HideSelectedRecommendations()
    {
        if (_recMultiSelectedKeys.Count == 0)
        {
            return;
        }

        var selectedItems = new List<SearchResultItem>();
        foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
        {
            if (!_recMultiSelectedKeys.Contains(entry.EntryKey))
            {
                continue;
            }

            if (entry.IsFolder)
            {
                selectedItems.AddRange(entry.FolderItems);
            }
            else if (entry.AppItem is { } appItem)
            {
                selectedItems.Add(appItem);
            }
        }

        _recMultiSelectedKeys.Clear();
        if (selectedItems.Count > 0)
        {
            _viewModel.HideRecommendations(selectedItems);
        }

        RefreshAllRecommendedCardEditVisuals();
        NotifyEditChromeChanged();
    }

    private void PruneStaleMultiSelectedRecommendationKeys()
    {
        if (_recMultiSelectedKeys.Count == 0)
        {
            return;
        }

        var validEntryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
        {
            validEntryKeys.Add(entry.EntryKey);
        }

        _recMultiSelectedKeys.RemoveWhere(key => !validEntryKeys.Contains(key));
    }

    private void ResetEditFolderDoubleClickTracking()
    {
        _lastEditFolderClickKey = null;
        _lastEditFolderClickTickMs = 0;
        _lastEditFolderSelectedBeforeClick = false;
    }

    private void ClearHoveredRecommendedCard()
    {
        if (_hoveredAppCard is not null)
        {
            Grid prev = _hoveredAppCard;
            _hoveredAppCard = null;
            prev.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            if (_isRecommendationEditMode)
            {
                ApplyRecommendedCardEditVisuals(
                    prev,
                    prev.DataContext as RecommendedGridEntry,
                    isPointerOver: false);
            }
        }
    }

    private void RefreshAllRecommendedCardEditVisuals()
    {
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is FrameworkElement card)
            {
                bool isHovered = ReferenceEquals(card, _hoveredAppCard) &&
                                 !_recIsDragging &&
                                 !_isRecRubberBanding;
                if (card is Grid grid)
                {
                    grid.Background = isHovered
                        ? ResolveLocalThemeBrush("SubtleFillColorSecondaryBrush")
                        : SearchRecommendationLayoutEngine.TransparentCardBrush;
                }

                ApplyRecommendedCardEditVisuals(
                    card,
                    card.DataContext as RecommendedGridEntry,
                    isPointerOver: isHovered);
            }
        }
    }

    private void ApplyRecommendedCardEditVisuals(
        FrameworkElement card,
        RecommendedGridEntry? entry,
        bool isPointerOver = false)
    {
        bool isEditActive = _isRecommendationEditMode && entry is not null;
        bool isQuickHideEligible = isEditActive && !entry!.IsFolder;
        bool isSelected = isEditActive && _recMultiSelectedKeys.Contains(entry!.EntryKey);

        // Tile background/plate masks are strictly hover-only (via card.Background on
        // PointerEntered/Exited); neither idle tiles nor selected tiles show a mask.
        if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(card, "CardEditSlotPlate") is { } editSlotPlate)
        {
            editSlotPlate.Visibility = Visibility.Collapsed;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(card, "CardSelectionPlate") is { } selectionPlate)
        {
            selectionPlate.Visibility = Visibility.Collapsed;
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(card, "CardSelectionCheckBox") is { } selectionCheckBox)
        {
            selectionCheckBox.Visibility = isEditActive
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(selectionCheckBox, "CardSelectionCheckedBox") is { } checkedBox)
            {
                checkedBox.Visibility = isSelected
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (SearchRecommendationLayoutEngine.FindVisualChild<Border>(selectionCheckBox, "CardSelectionUncheckedBox") is { } uncheckedBox)
            {
                uncheckedBox.Visibility = isEditActive && !isSelected
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (isEditActive && !isSelected)
                {
                    uncheckedBox.Opacity = isPointerOver ? 1.0 : 0.68;
                    uncheckedBox.BorderBrush = ResolveLocalThemeBrush(
                        isPointerOver
                            ? "ControlStrongStrokeColorDefaultBrush"
                            : "ControlStrongStrokeColorDisabledBrush");
                }
            }
        }

        if (SearchRecommendationLayoutEngine.FindVisualChild<Button>(card, "CardQuickHideButton") is { } quickHideButton)
        {
            quickHideButton.Visibility = isQuickHideEligible
                ? Visibility.Visible
                : Visibility.Collapsed;
            quickHideButton.Opacity = (isPointerOver || isSelected) ? 0.96 : 0.62;
        }
    }


    private void CardQuickHideButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchRecommendationLayoutEngine.FindDataContext<RecommendedGridEntry>(sender as DependencyObject) is
            { IsFolder: false, AppItem: { } appItem } entry)
        {
            _recMultiSelectedKeys.Remove(entry.EntryKey);
            _viewModel.HideRecommendation(appItem);
            NotifyEditChromeChanged();
        }
    }

    private bool HandleRecommendedAppsKey(KeyRoutedEventArgs e)
    {
        var itemsSource = RecommendedAppsRepeater.ItemsSource as System.Collections.IList;
        if (itemsSource is null || itemsSource.Count == 0) return false;

        int count = itemsSource.Count;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
                if (_selectedAppIndex < 0)
                {
                    SelectRecommendedAppByIndex(0);
                }
                else
                {
                    int columns = ComputeRecommendedAppsColumnCount();
                    int newIndex = e.Key switch
                    {
                        Windows.System.VirtualKey.Left => Math.Max(0, _selectedAppIndex - 1),
                        Windows.System.VirtualKey.Right => Math.Min(count - 1, _selectedAppIndex + 1),
                        Windows.System.VirtualKey.Up => Math.Max(0, _selectedAppIndex - columns),
                        Windows.System.VirtualKey.Down => Math.Min(count - 1, _selectedAppIndex + columns),
                        _ => _selectedAppIndex
                    };
                    if (newIndex != _selectedAppIndex && newIndex >= 0 && newIndex < count)
                    {
                        SelectRecommendedAppByIndex(newIndex);
                    }
                }
                return true;

            case Windows.System.VirtualKey.Enter:
                if (_isRecommendationEditMode)
                {
                    if (_selectedAppIndex >= 0 && _selectedAppIndex < count &&
                        itemsSource[_selectedAppIndex] is RecommendedGridEntry editSelected)
                    {
                        if (!_recMultiSelectedKeys.Add(editSelected.EntryKey))
                        {
                            _recMultiSelectedKeys.Remove(editSelected.EntryKey);
                        }

                        RefreshAllRecommendedCardEditVisuals();
                        NotifyEditChromeChanged();
                    }

                    return true;
                }

                if (_selectedAppIndex >= 0 && _selectedAppIndex < count &&
                    itemsSource[_selectedAppIndex] is RecommendedGridEntry selected)
                {
                    if (selected.IsFolder)
                    {
                        _host?.OpenFolderOverlay(selected);
                        return true;
                    }

                    if (selected.AppItem is { } appItem)
                    {
                        _viewModel.ExecuteItem(appItem);
                        return true;
                    }
                }
                return false; // No selection -> let Enter fall through to execute first result.

            case Windows.System.VirtualKey.Space:
                if (_isRecommendationEditMode &&
                    _selectedAppIndex >= 0 &&
                    _selectedAppIndex < count &&
                    itemsSource[_selectedAppIndex] is RecommendedGridEntry editSpaceEntry)
                {
                    if (!_recMultiSelectedKeys.Add(editSpaceEntry.EntryKey))
                    {
                        _recMultiSelectedKeys.Remove(editSpaceEntry.EntryKey);
                    }

                    RefreshAllRecommendedCardEditVisuals();
                    NotifyEditChromeChanged();
                    return true;
                }

                if (_selectedAppIndex >= 0 && _selectedAppIndex < count &&
                    itemsSource[_selectedAppIndex] is RecommendedGridEntry spaceEntry)
                {
                    if (spaceEntry.IsFolder)
                    {
                        _host?.OpenFolderOverlay(spaceEntry);
                    }
                    else
                    {
                        _host?.TryPreviewSelectedItem();
                    }
                }
                return true; // Always consume Space when panel is visible.

            case Windows.System.VirtualKey.Delete when _isRecommendationEditMode && _recMultiSelectedKeys.Count > 0:
                HideSelectedRecommendations();
                return true;

            case Windows.System.VirtualKey.A when _isRecommendationEditMode &&
                                                  Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control):
                _recMultiSelectedKeys.Clear();
                foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
                {
                    _recMultiSelectedKeys.Add(entry.EntryKey);
                }
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
                return true;
        }
        return false;
    }

    private void SelectRecommendedEntry(Grid card, RecommendedGridEntry entry)
    {
        ClearRecommendedAppSelection();
        _selectedAppCard = card;

        // Sync ViewModel selection for preview/action bar when an app tile is active.
        // Card background stays transparent until hovered (hover-only highlight policy).
        _viewModel.SelectedItem = entry.IsFolder
            ? entry.FolderItems.FirstOrDefault()
            : entry.AppItem;

        var itemsSource = RecommendedAppsRepeater.ItemsSource as System.Collections.IList;
        if (itemsSource is not null)
        {
            _selectedAppIndex = itemsSource.IndexOf(entry);
        }
    }

    private void SelectRecommendedAppByIndex(int index)
    {
        var itemsSource = RecommendedAppsRepeater.ItemsSource as System.Collections.IList;
        if (itemsSource is null || index < 0 || index >= itemsSource.Count ||
            itemsSource[index] is not RecommendedGridEntry targetEntry)
        {
            return;
        }

        Grid? matchedCard = null;
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is Grid candidate &&
                candidate.DataContext is RecommendedGridEntry candidateEntry &&
                string.Equals(candidateEntry.EntryKey, targetEntry.EntryKey, StringComparison.OrdinalIgnoreCase))
            {
                matchedCard = candidate;
                break;
            }
        }

        matchedCard ??= RecommendedAppsRepeater.GetOrCreateElement(index) as Grid;
        if (matchedCard is not null)
        {
            SelectRecommendedEntry(matchedCard, targetEntry);
            matchedCard.StartBringIntoView();
        }
    }

    private void ClearRecommendedAppSelection()
    {
        if (_selectedAppCard is not null)
        {
            if (!ReferenceEquals(_selectedAppCard, _hoveredAppCard))
            {
                _selectedAppCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            }

            _selectedAppCard = null;
        }
        _selectedAppIndex = -1;
    }

    // ── Skeleton screen ─────────────────────────────────────────────────────────

    private async Task ShowAppsSkeletonAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested && (_host?.IsPopupVisible == true))
        {
            DispatcherQueue.TryEnqueue(ShowAppsSkeleton);
        }
    }

    private void ShowAppsSkeleton()
    {
        // Populate skeleton items to match the grid layout.
        int count = Math.Max(8, _viewModel.CurrentResults.Count);
        AppsSkeletonRepeater.ItemsSource = Enumerable.Range(0, count).Select(_ => new object()).ToList();
        AppsSkeletonPanel.Visibility = Visibility.Visible;
        RecommendedAppsPanel.Visibility = Visibility.Collapsed;

        var visual = ElementCompositionPreview.GetElementVisual(AppsSkeletonPanel);
        visual.StopAnimation("Opacity");
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            visual.Opacity = 0.62f;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0.42f);
        animation.InsertKeyFrame(1, 0.7f);
        animation.Duration = TimeSpan.FromMilliseconds(900);
        animation.IterationBehavior =
            Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        animation.Direction =
            Microsoft.UI.Composition.AnimationDirection.Alternate;
        visual.StartAnimation("Opacity", animation);
    }

    private void HideAppsSkeleton()
    {
        ElementCompositionPreview
            .GetElementVisual(AppsSkeletonPanel)
            .StopAnimation("Opacity");
        AppsSkeletonRepeater.ItemsSource = null;
        AppsSkeletonPanel.Visibility = Visibility.Collapsed;
        AppsSkeletonPanel.Opacity = 1;
    }

    // ── Icon entrance animations ────────────────────────────────────────────────

    /// <summary>
    /// Plays the configured icon entrance animation on all realized app cards.
    /// Each card's opacity is explicitly reset to 0 first, so re-entering the
    /// home tab replays the animation cleanly instead of no-oping (Composition
    /// animations only fire on the start, not on completion).
    /// </summary>
    private void AnimateAppIconsEntrance()
    {
        int style = _settingsService.Settings.Search.SearchAppIconAnimation;
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            int staticIndex = 0;
            foreach (var _ in RecommendedAppsRepeater.ItemsSource as
                         System.Collections.IEnumerable ?? Array.Empty<object>())
            {
                if (RecommendedAppsRepeater.TryGetElement(staticIndex)
                    is FrameworkElement element)
                {
                    ElementCompositionPreview.GetElementVisual(element).Opacity = 1;
                }
                staticIndex++;
            }
            return;
        }

        // Signal pending entrance so that any cards realized AFTER this point
        // (ItemsRepeater is virtualized; TryGetElement is null until the layout
        // pass realizes them) are animated as soon as ElementPrepared fires.
        _pendingEntranceStyle = style;

        // Safety net: clear the pending flag shortly after the longest possible
        // entrance window so a late/aborted realization can never leave a card
        // stuck at opacity 0. Covers the worst-case stagger + duration.
        StopAndClearEntranceGuardTimer();
        _entranceGuardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _entranceGuardTimer.Tick += OnEntranceGuardTick;
        _entranceGuardTimer.Start();

        // Animate any cards that are already realized right now.
        int index = 0;
        var itemsSource = RecommendedAppsRepeater.ItemsSource as System.Collections.IEnumerable
            ?? Array.Empty<object>();

        foreach (var _ in itemsSource)
        {
            if (RecommendedAppsRepeater.TryGetElement(index)
                is FrameworkElement card)
            {
                AnimateSingleAppCard(card, index, style);
            }
            index++;
        }
    }

    private void StopAndClearEntranceGuardTimer()
    {
        if (_entranceGuardTimer is not { } timer)
        {
            return;
        }

        _entranceGuardTimer = null;
        timer.Tick -= OnEntranceGuardTick;
        try
        {
            timer.Stop();
        }
        catch
        {
        }
    }

    private void OnEntranceGuardTick(object? sender, object e)
    {
        StopAndClearEntranceGuardTimer();
        _pendingEntranceStyle = null;
    }

    private void AnimateSingleAppCard(FrameworkElement card, int index, int style)
    {
        var visual = ElementCompositionPreview.GetElementVisual(card);
        if (visual is null) return;

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var compositor = visual.Compositor;
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            visual.Opacity = 1;
            visual.Scale = System.Numerics.Vector3.One;
            visual.StopAnimation("Translation.Y");
            return;
        }

        // WinUI deceleration curve.
        var decel = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.1f, 1.0f));

        var spring = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.34f, 1.56f),
            new System.Numerics.Vector2(0.64f, 1.0f));

        int columnsForStagger = Math.Max(1, ComputeRecommendedAppsColumnCount());
        int staggerRow = index / columnsForStagger;
        int staggerCol = index % columnsForStagger;
        int delayMs = style == 2
            ? 0
            : Math.Min(160, (staggerRow * 20) + (staggerCol * 12));
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f, decel);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(style switch
        {
            0 => 200,
            1 => 220,
            2 => 260,
            3 => 200,
            _ => 200
        });
        opacityAnim.DelayTime = delay;
        visual.Opacity = 0f;
        visual.StartAnimation("Opacity", opacityAnim);

        // Anchor scale to the card's center so it grows in place. Prefer the
        // visual's realized Size (post-layout); fall back to ActualWidth/Height
        // when the element hasn't been measured yet (e.g. realized during load).
        float cx = visual.Size.X > 0 ? visual.Size.X / 2 : (float)(card.ActualWidth > 0 ? card.ActualWidth : 46) / 2;
        float cy = visual.Size.Y > 0 ? visual.Size.Y / 2 : (float)(card.ActualHeight > 0 ? card.ActualHeight : 92) / 2;
        visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0);

        switch (style)
        {
            case 0:
            {
                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.X",
                    delay,
                    350,
                    decel,
                    0.92f,
                    1.0f);
                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.Y",
                    delay,
                    350,
                    decel,
                    0.92f,
                    1.0f);
                break;
            }
            case 1:
            {
                var translateAnim = compositor.CreateScalarKeyFrameAnimation();
                translateAnim.InsertKeyFrame(0f, 18f);
                translateAnim.InsertKeyFrame(1f, 0f, decel);
                translateAnim.Duration = TimeSpan.FromMilliseconds(400);
                translateAnim.DelayTime = delay;
                visual.StartAnimation("Translation.Y", translateAnim);

                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.X",
                    delay,
                    400,
                    decel,
                    0.95f,
                    1.0f);
                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.Y",
                    delay,
                    400,
                    decel,
                    0.95f,
                    1.0f);
                break;
            }
            case 2:
            {
                int columns = ComputeRecommendedAppsColumnCount();
                int column = columns <= 0 ? 0 : index % columns;
                int row = columns <= 0 ? 0 : index / columns;
                float startY =
                    (column * 10f) + (Math.Min(row, 3) * 3f);

                var translateAnim = compositor.CreateScalarKeyFrameAnimation();
                translateAnim.InsertKeyFrame(0f, startY);
                translateAnim.InsertKeyFrame(1f, 0f, decel);
                translateAnim.Duration = TimeSpan.FromMilliseconds(450);
                translateAnim.DelayTime = delay;
                visual.StartAnimation("Translation.Y", translateAnim);

                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.X",
                    delay,
                    450,
                    decel,
                    0.95f,
                    1.0f);
                SearchRecommendationLayoutEngine.StartScalarAnim(
                    compositor,
                    visual,
                    "Scale.Y",
                    delay,
                    450,
                    decel,
                    0.95f,
                    1.0f);
                break;
            }
            case 3:
            {
                var scaleAnim =
                    compositor.CreateScalarKeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, 0.92f);
                scaleAnim.InsertKeyFrame(0.7f, 1.06f, spring);
                scaleAnim.InsertKeyFrame(1f, 1.0f, decel);
                scaleAnim.Duration = TimeSpan.FromMilliseconds(500);
                scaleAnim.DelayTime = delay;
                visual.StartAnimation("Scale.X", scaleAnim);
                visual.StartAnimation("Scale.Y", scaleAnim);

                var translateAnim =
                    compositor.CreateScalarKeyFrameAnimation();
                translateAnim.InsertKeyFrame(0f, 12f);
                translateAnim.InsertKeyFrame(0.7f, -4f, spring);
                translateAnim.InsertKeyFrame(1f, 0f, decel);
                translateAnim.Duration = TimeSpan.FromMilliseconds(500);
                translateAnim.DelayTime = delay;
                visual.StartAnimation("Translation.Y", translateAnim);
                break;
            }
        }
    }

    private int ComputeRecommendedAppsColumnCount()
    {
        double width = RecommendedAppsScrollViewer.ActualWidth > 0
            ? RecommendedAppsScrollViewer.ActualWidth
                - RecommendedAppsScrollViewer.Padding.Left
                - RecommendedAppsScrollViewer.Padding.Right
            : RecommendedAppsPanel.ActualWidth;
        if (width <= 0)
        {
            return 1;
        }
        return Math.Max(1, (int)Math.Floor((width + 6) / 104));
    }

    private void RecommendedAppsPanel_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CancelRecommendedDrag();
        var source = e.OriginalSource as DependencyObject;
        var entry = SearchRecommendationLayoutEngine.FindDataContext<RecommendedGridEntry>(source);
        bool showBatchMenu =
            _isRecommendationEditMode &&
            _recMultiSelectedKeys.Count > 1 &&
            (entry is null || _recMultiSelectedKeys.Contains(entry.EntryKey));
        if (showBatchMenu)
        {
            UIElement batchAnchor = (UIElement?)SearchRecommendationLayoutEngine.FindVisualAncestor<Grid>(source) ?? RecommendedAppsScrollViewer;
            MenuFlyout batchFlyout = BuildBatchRecommendedAppsContextMenu();
            if (batchFlyout.Items.Count > 0)
            {
                batchFlyout.Closed += (_, _) => ClearHoveredRecommendedCard();
                batchFlyout.ShowAt(batchAnchor, e.GetPosition(batchAnchor));
                e.Handled = true;
            }

            return;
        }

        if (entry is null)
        {
            return;
        }

        UIElement anchor = (UIElement?)SearchRecommendationLayoutEngine.FindVisualAncestor<Grid>(source) ?? RecommendedAppsScrollViewer;
        MenuFlyout flyout = entry.IsFolder
            ? BuildRecommendedFolderContextMenu(entry)
            : BuildRecommendedAppContextMenu(entry);
        if (flyout.Items.Count > 0)
        {
            flyout.Closed += (_, _) => ClearHoveredRecommendedCard();
            flyout.ShowAt(anchor, e.GetPosition(anchor));
            e.Handled = true;
        }
    }

    private MenuFlyout BuildBatchRecommendedAppsContextMenu()
    {
        var flyout = new MenuFlyout();
        var selectedEntries = _viewModel.RecommendedEntries
            .Where(entry => _recMultiSelectedKeys.Contains(entry.EntryKey))
            .ToList();
        if (selectedEntries.Count < 2)
        {
            return flyout;
        }

        bool hasSelectedFolder = selectedEntries.Any(entry => entry.IsFolder);
        var selectedApps = selectedEntries
            .Where(entry => !entry.IsFolder && entry.AppItem is not null)
            .Select(entry => entry.AppItem!)
            .ToList();
        var allHideableItems = new List<SearchResultItem>();
        foreach (RecommendedGridEntry entry in selectedEntries)
        {
            if (entry.IsFolder)
            {
                allHideableItems.AddRange(entry.FolderItems);
            }
            else if (entry.AppItem is { } appItem)
            {
                allHideableItems.Add(appItem);
            }
        }

        if (!hasSelectedFolder && selectedApps.Count >= 2)
        {
            var moveSubItem = new MenuFlyoutSubItem
            {
                Text = _localizationService.T("Search.Recommend.MoveToFolder"),
                Icon = new FontIcon { Glyph = "\uE8F4" }
            };

            var newFolderItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Search.Recommend.NewFolder"),
                Icon = new FontIcon { Glyph = "\uE8F4" }
            };
            newFolderItem.Click += (_, _) =>
            {
                string targetEntryKey = $"app:{Helpers.AppRecommendationIdentity.GetCanonicalKey(selectedApps[0])}";
                var remainingKeys = selectedApps
                    .Skip(1)
                    .Select(Helpers.AppRecommendationIdentity.GetCanonicalKey)
                    .ToList();
                _recMultiSelectedKeys.Clear();
                string? createdFolderId = _viewModel.MergeRecommendationsIntoFolder(remainingKeys, targetEntryKey);
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
                if (_viewModel.FindRecommendedFolder(createdFolderId) is { } createdFolder)
                {
                    _host?.OpenFolderOverlay(createdFolder, startRename: true);
                }
            };
            moveSubItem.Items.Add(newFolderItem);

            IReadOnlyList<RecommendationFolderSummary> existingFolders = _viewModel.GetRecommendationFolders();
            if (existingFolders.Count > 0)
            {
                moveSubItem.Items.Add(new MenuFlyoutSeparator());
                var selectedKeys = selectedApps
                    .Select(Helpers.AppRecommendationIdentity.GetCanonicalKey)
                    .ToList();
                foreach (RecommendationFolderSummary folder in existingFolders)
                {
                    string targetFolderId = folder.FolderId;
                    var targetItem = new MenuFlyoutItem
                    {
                        Text = folder.Name,
                        Icon = new FontIcon { Glyph = "\uE8B7" }
                    };
                    targetItem.Click += (_, _) =>
                    {
                        _recMultiSelectedKeys.Clear();
                        _viewModel.MergeRecommendationsIntoFolder(selectedKeys, $"folder:{targetFolderId}");
                        RefreshAllRecommendedCardEditVisuals();
                        NotifyEditChromeChanged();
                    };
                    moveSubItem.Items.Add(targetItem);
                }
            }

            flyout.Items.Add(moveSubItem);
        }

        if (allHideableItems.Count > 0)
        {
            var hideItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Search.Recommend.Hide"),
                Icon = new FontIcon { Glyph = WindowsCompatibilityService.IsWindows11OrLater ? "\uED1A" : "\uE890" }
            };
            hideItem.Click += (_, _) =>
            {
                _recMultiSelectedKeys.Clear();
                _viewModel.HideRecommendations(allHideableItems);
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            };
            flyout.Items.Add(hideItem);
        }

        return flyout;
    }


    private MenuFlyout BuildRecommendedFolderContextMenu(RecommendedGridEntry folderEntry)
    {
        var flyout = new MenuFlyout();
        if (!folderEntry.IsFolder || string.IsNullOrWhiteSpace(folderEntry.FolderId))
        {
            return flyout;
        }

        string folderId = folderEntry.FolderId;

        var openFolderItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.Open"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        openFolderItem.Click += (_, _) => _host?.OpenFolderOverlay(folderEntry);
        flyout.Items.Add(openFolderItem);

        var renameFolderItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.RenameFolder"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameFolderItem.Click += (_, _) => _host?.OpenFolderOverlay(folderEntry, startRename: true);
        flyout.Items.Add(renameFolderItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var dissolveItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.DissolveFolder"),
            Icon = new FontIcon { Glyph = "\uE8F1" }
        };
        dissolveItem.Click += (_, _) => _viewModel.DissolveRecommendationFolder(folderId);
        flyout.Items.Add(dissolveItem);

        return flyout;
    }

    private MenuFlyout BuildRecommendedAppContextMenu(RecommendedGridEntry appEntry)
    {
        var flyout = new MenuFlyout();
        if (appEntry.AppItem is not { } item)
        {
            return flyout;
        }

        AppendCommonRecommendedAppActions(flyout, item);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var moveSubItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Search.Recommend.MoveToFolder"),
            Icon = new FontIcon { Glyph = "\uE8F4" }
        };

        var newFolderItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.NewFolder"),
            Icon = new FontIcon { Glyph = "\uE8F4" }
        };
        newFolderItem.Click += (_, _) =>
        {
            string? createdFolderId = _viewModel.CreateFolderFromRecommendation(item);
            if (_viewModel.FindRecommendedFolder(createdFolderId) is { } createdFolder)
            {
                _host?.OpenFolderOverlay(createdFolder, startRename: true);
            }
        };
        moveSubItem.Items.Add(newFolderItem);

        IReadOnlyList<RecommendationFolderSummary> existingFolders = _viewModel.GetRecommendationFolders();
        if (existingFolders.Count > 0)
        {
            moveSubItem.Items.Add(new MenuFlyoutSeparator());
            string appKey = Helpers.AppRecommendationIdentity.GetCanonicalKey(item);
            foreach (RecommendationFolderSummary folder in existingFolders)
            {
                string targetFolderId = folder.FolderId;
                var targetItem = new MenuFlyoutItem
                {
                    Text = folder.Name,
                    Icon = new FontIcon { Glyph = "\uE8B7" }
                };
                targetItem.Click += (_, _) =>
                    _viewModel.MergeRecommendationIntoFolder(appKey, $"folder:{targetFolderId}");
                moveSubItem.Items.Add(targetItem);
            }
        }

        flyout.Items.Add(moveSubItem);

        var hideItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.Hide"),
            Icon = new FontIcon
            {
                Glyph = WindowsCompatibilityService.IsWindows11OrLater ? "\uED1A" : "\uE890"
            }
        };
        hideItem.Click += (_, _) => _viewModel.HideRecommendation(item);
        flyout.Items.Add(hideItem);

        return flyout;
    }


    internal void AppendCommonRecommendedAppActions(MenuFlyout flyout, SearchResultItem item)
    {
        var openItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.Open"),
            Icon = new FontIcon { Glyph = "\uE8E5" }
        };
        openItem.Click += (_, _) => _viewModel.ExecuteItem(item);
        flyout.Items.Add(openItem);

        bool hasExistingPath = !string.IsNullOrWhiteSpace(item.DetailPath) &&
                               (File.Exists(item.DetailPath) || Directory.Exists(item.DetailPath));
        if (!hasExistingPath)
        {
            return;
        }

        if (ElevatedFileLauncher.CanRunAsAdministrator(item.DetailPath!))
        {
            var runAsAdminItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.RunAsAdministrator"),
                Icon = new FontIcon { Glyph = "\uE7EF" }
            };
            runAsAdminItem.Click += (_, _) =>
                _host?.QueueRunItemAsAdministrator(item);
            flyout.Items.Add(runAsAdminItem);
        }

        var openLocationItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.OpenLocation"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        openLocationItem.Click += (_, _) =>
        {
            Services.ShellLaunchService.FireAndForgetRevealInExplorer(item.DetailPath!);
            _host?.HidePopup();
        };
        flyout.Items.Add(openLocationItem);
    }
}
