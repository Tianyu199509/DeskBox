using System.Collections.ObjectModel;
using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskBox.Controls.Search;

internal interface ISearchRecommendationOverlayHost
{
    bool IsRecommendationEditMode { get; }
    bool IsDragOutInProgress { get; }
    bool IsPopupVisible { get; }
    bool IsWindowClosed { get; }
    IntPtr OwnerWindowHandle { get; }
    FrameworkElement PopupBoundsRoot { get; }
    FrameworkElement SearchHeaderElement { get; }
    void HidePopup();
    void FocusSearchBox();
    void FocusPopupRoot();
    void CancelRecommendedGridDrag();
    void EndRecommendedGridRubberBand();
    void UpdatePopupDynamicBounds();
    bool TryStartRecommendedExternalDragOut(
        UIElement dragSource,
        PointerRoutedEventArgs e,
        IReadOnlyList<string> candidatePaths,
        string originTag,
        Action suspendInternalDrag);
    void AppendCommonRecommendedAppActions(MenuFlyout flyout, SearchResultItem item);
}

public sealed partial class RecommendationFolderOverlayControl : UserControl
{
    private SearchPopupViewModel _viewModel = null!;
    private LocalizationService _localizationService = null!;
    private ISearchRecommendationOverlayHost? _host;

    private int _overlayAnimationGeneration;
    private bool _isOverlayClosing;
    private Grid? _hoveredOverlayCard;
    private Grid? _overlayPointerCard;
    private SearchResultItem? _overlayPressedItem;
    private Point _overlayPointerStartInHost;
    private bool _overlayIsDragging;
    private bool _overlayPressedInFolder;
    private bool _overlayExternalDragAttempted;
    private string? _openFolderId;
    private bool _isEditingFolderTitle;
    private bool _suppressFolderTitleBlurCommit;
    private readonly System.Collections.ObjectModel.ObservableCollection<SearchResultItem> _folderOverlayItems = [];

    private bool _isRecommendationEditMode => _host?.IsRecommendationEditMode == true;

    public RecommendationFolderOverlayControl()
    {
        InitializeComponent();
        FolderAppsRepeater.ElementPrepared += OnOverlayAppsElementPrepared;
        FolderAppsRepeater.ElementClearing += OnOverlayAppsElementClearing;
        HiddenAppsRepeater.ElementPrepared += OnOverlayAppsElementPrepared;
        HiddenAppsRepeater.ElementClearing += OnOverlayAppsElementClearing;
    }

    internal void Initialize(
        SearchPopupViewModel viewModel,
        LocalizationService localizationService,
        ISearchRecommendationOverlayHost host)
    {
        _viewModel = viewModel;
        _localizationService = localizationService;
        _host = host;
    }

    internal bool IsAnyOverlayOpen => IsAnyRecommendationOverlayOpen();
    internal bool IsHiddenOverlayVisible => HiddenAppsOverlayHost.Visibility == Visibility.Visible;
    internal bool IsEditingFolderTitle => _isEditingFolderTitle;
    internal bool HasActiveInteraction => _overlayIsDragging || _isEditingFolderTitle;

    internal void BindItemsSources()
    {
        FolderAppsRepeater.ItemsSource = _folderOverlayItems;
        HiddenAppsRepeater.ItemsSource = _viewModel.HiddenRecommendations;
    }

    internal void ApplyLocalizedStrings()
    {
        HiddenOverlayTitleText.Text = _localizationService.T("Search.Recommend.HiddenTitle");
        HiddenOverlaySubtitleText.Text = _localizationService.T("Search.Recommend.HiddenHint");
        UnhideAllButtonText.Text = _localizationService.T("Search.Recommend.UnhideAll");
        ToolTipService.SetToolTip(
            FolderOverlayTitleButton,
            _localizationService.T("Search.Recommend.RenameFolder"));
        ToolTipService.SetToolTip(
            CloseHiddenOverlayButton,
            _localizationService.T("Common.Close"));
        UpdateRecommendationOverlayResponsiveBounds();
    }

    internal void ApplyBottomCornerRadius(CornerRadius bottomCornerRadius)
    {
        FolderOverlayScrim.CornerRadius = bottomCornerRadius;
        HiddenAppsOverlayScrim.CornerRadius = bottomCornerRadius;
    }

    internal void OpenFolder(RecommendedGridEntry folderEntry, bool startRename = false) =>
        OpenFolderOverlay(folderEntry, startRename);

    internal void OpenHiddenApps() => OpenHiddenAppsOverlay();

    internal void CloseOverlays(bool immediate = false) => CloseRecommendationOverlays(immediate);

    internal void SyncWithViewModel() => SyncOpenFolderOverlayWithViewModel();

    internal void CancelTitleEdit() => CancelFolderTitleEdit();

    internal void ClearHoveredCard() => ClearHoveredOverlayCard();

    internal void CancelDrag() => CancelOverlayDrag();

    internal void SuspendDragForExternalHandoff() => SuspendOverlayDragForExternalHandoff();

    internal void RefreshIcons()
    {
        RefreshOverlayRepeaterIcons(FolderAppsRepeater);
        RefreshOverlayRepeaterIcons(HiddenAppsRepeater);
    }

    internal void ClearRealizedVisuals()
    {
        ClearOverlayRepeaterVisuals(FolderAppsRepeater);
        ClearOverlayRepeaterVisuals(HiddenAppsRepeater);
    }

    internal void Dispose()
    {
        FolderAppsRepeater.ElementPrepared -= OnOverlayAppsElementPrepared;
        FolderAppsRepeater.ElementClearing -= OnOverlayAppsElementClearing;
        HiddenAppsRepeater.ElementPrepared -= OnOverlayAppsElementPrepared;
        HiddenAppsRepeater.ElementClearing -= OnOverlayAppsElementClearing;
        CloseRecommendationOverlays(immediate: true);
        ClearRealizedVisuals();
        FolderAppsRepeater.ItemsSource = null;
        HiddenAppsRepeater.ItemsSource = null;
        _folderOverlayItems.Clear();
        Bindings?.StopTracking();
        _host = null;
        Content = null;
    }

    private FrameworkElement GetPopupBoundsRoot() => _host?.PopupBoundsRoot ?? this;

    private double GetSearchHeaderHeight() =>
        _host?.SearchHeaderElement.ActualHeight > 0 ? _host.SearchHeaderElement.ActualHeight : 56;

    private Brush? ResolveLocalThemeBrush(string key) =>
        SearchRecommendationLayoutEngine.ResolveThemeBrush(GetPopupBoundsRoot(), key);

    private void ArmCardImplicitOffsetAnimation(FrameworkElement card) =>
        SearchRecommendationLayoutEngine.ArmImplicitOffsetAnimationAfterLayout(
            card,
            DispatcherQueue,
            () => _host is { IsWindowClosed: false, IsPopupVisible: true },
            static () => WindowsCompatibilityService.ShouldAnimate);

    private static void ClearOverlayRepeaterVisuals(ItemsRepeater repeater)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(repeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(repeater, i) is FrameworkElement card)
            {
                SearchRecommendationLayoutEngine.DetachImplicitOffsetAnimation(card);
                if (card is Grid grid)
                {
                    grid.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
                }

                card.Opacity = 1;
                if (SearchRecommendationLayoutEngine.FindDescendant<Image>(card) is { } iconImage)
                {
                    iconImage.Source = null;
                }

                card.DataContext = null;
            }
        }
    }

    private void OnOverlayAppsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement card)
        {
            return;
        }

        SearchRecommendationLayoutEngine.DetachImplicitOffsetAnimation(card);
        SearchResultItem? resolvedItem =
            sender.ItemsSource is System.Collections.IList items &&
            args.Index >= 0 &&
            args.Index < items.Count
                ? items[args.Index] as SearchResultItem
                : card.DataContext as SearchResultItem;
        card.DataContext = resolvedItem;
        card.Opacity = 1;

        if (card is Grid grid)
        {
            grid.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        if (SearchRecommendationLayoutEngine.FindDescendant<Image>(card) is { } overlayImage)
        {
            ImageSource? resolvedIcon = resolvedItem?.Icon;
            overlayImage.Source = resolvedIcon;
        }

        ArmCardImplicitOffsetAnimation(card);
    }

    private void OnOverlayAppsElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement card)
        {
            return;
        }

        if (ReferenceEquals(_hoveredOverlayCard, card))
        {
            _hoveredOverlayCard = null;
        }

        SearchRecommendationLayoutEngine.DetachImplicitOffsetAnimation(card);
        if (card is Grid grid)
        {
            grid.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        card.Opacity = 1;
        if (SearchRecommendationLayoutEngine.FindDescendant<Image>(card) is { } overlayImage)
        {
            overlayImage.Source = null;
        }

        card.DataContext = null;
    }

    private static void RefreshOverlayRepeaterIcons(ItemsRepeater repeater)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(repeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(repeater, i) is FrameworkElement card &&
                card.DataContext is SearchResultItem entryItem &&
                SearchRecommendationLayoutEngine.FindDescendant<Image>(card) is { } overlayImage)
            {
                ImageSource? resolvedIcon = entryItem.Icon;
                if (!ReferenceEquals(overlayImage.Source, resolvedIcon))
                {
                    overlayImage.Source = resolvedIcon;
                }
            }
        }
    }

    private void RecommendationOverlaysLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRecommendationOverlayResponsiveBounds();
    }

    private void UpdateRecommendationOverlayResponsiveBounds()
    {
        double hostWidth = GetPopupBoundsRoot().ActualWidth > 0
            ? GetPopupBoundsRoot().ActualWidth
            : (RecommendationOverlaysLayer.ActualWidth > 0
                ? RecommendationOverlaysLayer.ActualWidth
                : SearchPopupSurfacePolicy.DefaultPopupWidthLogical);
        double searchRowHeight = GetSearchHeaderHeight() > 0 ? GetSearchHeaderHeight() : 60;
        double hostHeight = RecommendationOverlaysLayer.ActualHeight > 0
            ? RecommendationOverlaysLayer.ActualHeight
            : Math.Max(
                220,
                (GetPopupBoundsRoot().ActualHeight > 0 ? GetPopupBoundsRoot().ActualHeight : 500) - searchRowHeight);

        double cardWidth = Math.Min(448, Math.Max(200, hostWidth - 28));
        double cardMaxHeight = Math.Clamp(hostHeight - 20, 180, 368);
        double cardMinHeight = Math.Min(196, cardMaxHeight);
        double maxTitleWidth = Math.Max(120, cardWidth - 68);

        FolderOverlayCard.Width = cardWidth;
        FolderOverlayCard.MinHeight = cardMinHeight;
        FolderOverlayCard.MaxHeight = cardMaxHeight;
        FolderOverlayTitleText.MaxWidth = maxTitleWidth;
        FolderOverlayTitleEditor.MaxWidth = maxTitleWidth;
        FolderOverlayTitleEditor.MinWidth = Math.Min(160, maxTitleWidth);

        HiddenAppsOverlayCard.Width = cardWidth;
        HiddenAppsOverlayCard.MinHeight = cardMinHeight;
        HiddenAppsOverlayCard.MaxHeight = cardMaxHeight;
    }

    private bool IsAnyRecommendationOverlayOpen() =>
        !_isOverlayClosing &&
        RecommendationOverlaysLayer.Visibility == Visibility.Visible &&
        (FolderOverlayHost.Visibility == Visibility.Visible ||
         HiddenAppsOverlayHost.Visibility == Visibility.Visible);

    private void OpenFolderOverlay(RecommendedGridEntry folderEntry, bool startRename = false)
    {
        if (!folderEntry.IsFolder || string.IsNullOrWhiteSpace(folderEntry.FolderId))
        {
            return;
        }

        _host?.CancelRecommendedGridDrag();
        CancelFolderTitleEdit();
        _host?.EndRecommendedGridRubberBand();
        UpdateRecommendationOverlayResponsiveBounds();
        _openFolderId = folderEntry.FolderId;
        FolderOverlayTitleText.Text = folderEntry.Label;

        _folderOverlayItems.Clear();
        foreach (SearchResultItem member in folderEntry.FolderItems)
        {
            _folderOverlayItems.Add(member);
        }

        HiddenAppsOverlayHost.Visibility = Visibility.Collapsed;
        FolderOverlayHost.Visibility = Visibility.Visible;
        RecommendationOverlaysLayer.Visibility = Visibility.Visible;
        _host?.UpdatePopupDynamicBounds();
        FolderOverlayScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        RefreshOverlayRepeaterIcons(FolderAppsRepeater);
        AnimateRecommendationOverlayOpen(FolderOverlayScrim, FolderOverlayCard);

        if (startRename)
        {
            DispatcherQueue.TryEnqueue(BeginFolderTitleEdit);
        }
    }

    private void OpenHiddenAppsOverlay()
    {
        if (!_viewModel.HasHiddenRecommendations)
        {
            return;
        }

        _host?.CancelRecommendedGridDrag();
        CancelFolderTitleEdit();
        _host?.EndRecommendedGridRubberBand();
        UpdateRecommendationOverlayResponsiveBounds();
        _openFolderId = null;

        FolderOverlayHost.Visibility = Visibility.Collapsed;
        HiddenAppsOverlayHost.Visibility = Visibility.Visible;
        RecommendationOverlaysLayer.Visibility = Visibility.Visible;
        _host?.UpdatePopupDynamicBounds();
        HiddenOverlayScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        RefreshOverlayRepeaterIcons(HiddenAppsRepeater);
        AnimateRecommendationOverlayOpen(HiddenAppsOverlayScrim, HiddenAppsOverlayCard);
    }

    private void CloseRecommendationOverlays(bool immediate = false)
    {
        ClearHoveredOverlayCard();
        CancelOverlayDrag();
        CancelFolderTitleEdit();
        _openFolderId = null;

        bool anyVisible = RecommendationOverlaysLayer.Visibility == Visibility.Visible &&
                          (FolderOverlayHost.Visibility == Visibility.Visible ||
                           HiddenAppsOverlayHost.Visibility == Visibility.Visible);
        if (!anyVisible)
        {
            _isOverlayClosing = false;
            FolderOverlayHost.Visibility = Visibility.Collapsed;
            HiddenAppsOverlayHost.Visibility = Visibility.Collapsed;
            RecommendationOverlaysLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (immediate || !WindowsCompatibilityService.ShouldAnimate)
        {
            ++_overlayAnimationGeneration;
            _isOverlayClosing = false;
            ResetRecommendationOverlayVisuals();
            FolderOverlayHost.Visibility = Visibility.Collapsed;
            HiddenAppsOverlayHost.Visibility = Visibility.Collapsed;
            RecommendationOverlaysLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (_isOverlayClosing)
        {
            return;
        }

        _isOverlayClosing = true;
        int generation = ++_overlayAnimationGeneration;
        bool closingFolder = FolderOverlayHost.Visibility == Visibility.Visible;
        Border scrim = closingFolder ? FolderOverlayScrim : HiddenAppsOverlayScrim;
        Border card = closingFolder ? FolderOverlayCard : HiddenAppsOverlayCard;

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = cardVisual.Compositor;

        float cx = (float)(card.ActualWidth > 0
            ? card.ActualWidth
            : (!double.IsNaN(card.Width) && card.Width > 0 ? card.Width : 380)) / 2f;
        float cy = (float)(card.ActualHeight > 0 ? card.ActualHeight : 250) / 2f;
        cardVisual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.4f, 0.0f),
            new System.Numerics.Vector2(1.0f, 1.0f));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, scrimVisual, "Opacity", TimeSpan.Zero, 125, easeIn, scrimVisual.Opacity, 0f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Opacity", TimeSpan.Zero, 125, easeIn, cardVisual.Opacity, 0f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Scale.X", TimeSpan.Zero, 135, easeIn, 1.0f, 0.94f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Scale.Y", TimeSpan.Zero, 135, easeIn, 1.0f, 0.94f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Translation.Y", TimeSpan.Zero, 135, easeIn, 0f, 6f);

        batch.Completed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _overlayAnimationGeneration)
                {
                    return;
                }

                _isOverlayClosing = false;
                FolderOverlayHost.Visibility = Visibility.Collapsed;
                HiddenAppsOverlayHost.Visibility = Visibility.Collapsed;
                RecommendationOverlaysLayer.Visibility = Visibility.Collapsed;
                ResetRecommendationOverlayVisuals();
                _host?.UpdatePopupDynamicBounds();
            });
        };
        batch.End();
    }

    private void AnimateRecommendationOverlayOpen(Border scrim, Border card)
    {
        ++_overlayAnimationGeneration;
        _isOverlayClosing = false;
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);

        scrimVisual.StopAnimation("Opacity");
        cardVisual.StopAnimation("Opacity");
        cardVisual.StopAnimation("Scale.X");
        cardVisual.StopAnimation("Scale.Y");
        cardVisual.StopAnimation("Translation.Y");

        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            scrimVisual.Opacity = 1f;
            cardVisual.Opacity = 1f;
            cardVisual.Scale = System.Numerics.Vector3.One;
            return;
        }

        float cx = (float)(!double.IsNaN(card.Width) && card.Width > 0
            ? card.Width
            : (card.ActualWidth > 0 ? card.ActualWidth : 380)) / 2f;
        float cy = (float)(card.ActualHeight > 0 ? card.ActualHeight : 250) / 2f;
        cardVisual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

        var compositor = cardVisual.Compositor;
        var decel = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.1f, 1.0f));

        scrimVisual.Opacity = 0f;
        cardVisual.Opacity = 0f;
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, scrimVisual, "Opacity", TimeSpan.Zero, 150, decel, 0f, 1f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Opacity", TimeSpan.Zero, 155, decel, 0f, 1f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Scale.X", TimeSpan.Zero, 200, decel, 0.93f, 1.0f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Scale.Y", TimeSpan.Zero, 200, decel, 0.93f, 1.0f);
        SearchRecommendationLayoutEngine.StartScalarAnim(compositor, cardVisual, "Translation.Y", TimeSpan.Zero, 200, decel, 10f, 0f);
    }

    private void ResetRecommendationOverlayVisuals()
    {
        ResetSingleOverlayVisualPair(FolderOverlayScrim, FolderOverlayCard);
        ResetSingleOverlayVisualPair(HiddenAppsOverlayScrim, HiddenAppsOverlayCard);
    }

    private static void ResetSingleOverlayVisualPair(Border scrim, Border card)
    {
        var scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
        if (scrimVisual is not null)
        {
            scrimVisual.StopAnimation("Opacity");
            scrimVisual.Opacity = 1f;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var cardVisual = ElementCompositionPreview.GetElementVisual(card);
        if (cardVisual is not null)
        {
            cardVisual.StopAnimation("Opacity");
            cardVisual.StopAnimation("Scale.X");
            cardVisual.StopAnimation("Scale.Y");
            cardVisual.StopAnimation("Translation.Y");
            cardVisual.Opacity = 1f;
            cardVisual.Scale = System.Numerics.Vector3.One;
        }
    }

    private void SyncOpenFolderOverlayWithViewModel()
    {
        if (FolderOverlayHost.Visibility != Visibility.Visible ||
            string.IsNullOrWhiteSpace(_openFolderId))
        {
            return;
        }

        RecommendedGridEntry? refreshedFolder = _viewModel.FindRecommendedFolder(_openFolderId);
        if (refreshedFolder is null || refreshedFolder.FolderItems.Count == 0)
        {
            CloseRecommendationOverlays();
            return;
        }

        if (!_isEditingFolderTitle)
        {
            FolderOverlayTitleText.Text = refreshedFolder.Label;
        }

        _folderOverlayItems.Clear();
        foreach (SearchResultItem member in refreshedFolder.FolderItems)
        {
            _folderOverlayItems.Add(member);
        }

        RefreshOverlayRepeaterIcons(FolderAppsRepeater);
    }

    private void BeginFolderTitleEdit()
    {
        if (FolderOverlayHost.Visibility != Visibility.Visible ||
            string.IsNullOrWhiteSpace(_openFolderId))
        {
            return;
        }

        _isEditingFolderTitle = true;
        _suppressFolderTitleBlurCommit = false;
        _folderTitleEditOpenedAtTick = Environment.TickCount64;
        FolderOverlayTitleEditor.Text = FolderOverlayTitleText.Text;
        FolderOverlayTitleButton.Visibility = Visibility.Collapsed;
        FolderOverlayTitleEditor.Visibility = Visibility.Visible;
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(FolderOverlayTitleEditor);
        FolderOverlayTitleEditor.Focus(FocusState.Programmatic);
        FolderOverlayTitleEditor.SelectAll();
    }

    private void CommitFolderTitleEdit()
    {
        if (!_isEditingFolderTitle)
        {
            return;
        }

        string? folderId = _openFolderId;
        string updatedTitle = FolderOverlayTitleEditor.Text;
        _isEditingFolderTitle = false;
        _suppressFolderTitleBlurCommit = true;
        FolderOverlayTitleEditor.Visibility = Visibility.Collapsed;
        FolderOverlayTitleButton.Visibility = Visibility.Visible;
        _suppressFolderTitleBlurCommit = false;

        if (!string.IsNullOrWhiteSpace(folderId))
        {
            _viewModel.RenameRecommendationFolder(folderId, updatedTitle);
            if (_viewModel.FindRecommendedFolder(folderId) is { } folder)
            {
                FolderOverlayTitleText.Text = folder.Label;
            }
        }
    }

    private void CancelFolderTitleEdit()
    {
        if (!_isEditingFolderTitle)
        {
            return;
        }

        _isEditingFolderTitle = false;
        _suppressFolderTitleBlurCommit = true;
        FolderOverlayTitleEditor.Visibility = Visibility.Collapsed;
        FolderOverlayTitleButton.Visibility = Visibility.Visible;
        _suppressFolderTitleBlurCommit = false;
    }


    private void FolderOverlayScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditingFolderTitle)
        {
            CommitFolderTitleEdit();
        }

        CloseRecommendationOverlays();
        e.Handled = true;
    }

    private void FolderOverlayTitleButton_Click(object sender, RoutedEventArgs e)
    {
        BeginFolderTitleEdit();
    }

    private long _folderTitleEditOpenedAtTick;

    private void FolderOverlayTitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitFolderTitleEdit();
            _host?.FocusSearchBox();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelFolderTitleEdit();
            _host?.FocusSearchBox();
            e.Handled = true;
        }
    }

    private void FolderOverlayTitleEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressFolderTitleBlurCommit || !_isEditingFolderTitle)
        {
            return;
        }

        if (DeskBox.Helpers.TransientInputFocusGuard.ShouldDeferInlineEditorBlurCommit(
                FolderOverlayTitleEditor,
                _folderTitleEditOpenedAtTick,
                (_host?.OwnerWindowHandle ?? IntPtr.Zero),
                () => _isEditingFolderTitle,
                () =>
                {
                    CommitFolderTitleEdit();
                    return Task.CompletedTask;
                }))
        {
            return;
        }

        CommitFolderTitleEdit();
    }

    private void HiddenAppsOverlayScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CloseRecommendationOverlays();
        e.Handled = true;
    }

    private void UnhideAllButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.UnhideAllRecommendations();
        CloseRecommendationOverlays();
    }

    private void CloseHiddenOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRecommendationOverlays();
    }

    // ── Overlay tile interactions (folder app launch/drag & hidden app unhide) ──

    private void OverlayAppCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid card || card.DataContext is not SearchResultItem item)
        {
            return;
        }

        bool inFolderOverlay = FolderOverlayHost.Visibility == Visibility.Visible &&
                               SearchRecommendationLayoutEngine.IsVisualDescendantOf(card, FolderOverlayHost);
        UIElement hostElement = inFolderOverlay ? FolderOverlayHost : HiddenAppsOverlayHost;
        var pointInHost = e.GetCurrentPoint(hostElement);
        if (!pointInHost.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_isEditingFolderTitle)
        {
            CommitFolderTitleEdit();
        }

        _overlayPointerCard = card;
        _overlayPressedItem = item;
        _overlayPointerStartInHost = pointInHost.Position;
        _overlayIsDragging = false;
        _overlayPressedInFolder = inFolderOverlay;
        _overlayExternalDragAttempted = false;
        card.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OverlayAppCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayPressedItem is null ||
            _overlayPointerCard is null ||
            !ReferenceEquals(sender, _overlayPointerCard))
        {
            return;
        }

        UIElement hostElement = _overlayPressedInFolder ? FolderOverlayHost : HiddenAppsOverlayHost;
        var pointInHost = e.GetCurrentPoint(hostElement);
        if (!pointInHost.Properties.IsLeftButtonPressed)
        {
            CancelOverlayDrag();
            return;
        }

        double dx = pointInHost.Position.X - _overlayPointerStartInHost.X;
        double dy = pointInHost.Position.Y - _overlayPointerStartInHost.Y;
        if (!_overlayIsDragging)
        {
            if ((dx * dx) + (dy * dy) < SearchRecommendationLayoutEngine.RecommendationDragThreshold * SearchRecommendationLayoutEngine.RecommendationDragThreshold)
            {
                return;
            }

            ClearHoveredOverlayCard();
            _overlayIsDragging = true;
            _overlayPointerCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            _overlayPointerCard.Opacity = 0.36;
            if (_overlayPressedInFolder)
            {
                FolderDragGhostTitle.Text = _overlayPressedItem.AppDisplayName;
                FolderDragGhostImage.Source = _overlayPressedItem.Icon;
                FolderDragGhostBorder.Visibility = Visibility.Visible;
                SearchRecommendationLayoutEngine.AnimateDragGhostPickup(
                    FolderDragGhostBorder,
                    WindowsCompatibilityService.ShouldAnimate);
            }
        }

        Point pointInRoot = e.GetCurrentPoint(GetPopupBoundsRoot()).Position;
        double rootWidth = GetPopupBoundsRoot().ActualWidth;
        double rootHeight = GetPopupBoundsRoot().ActualHeight;
        if (SearchRecommendationLayoutEngine.IsPointLeavingPopupDragBounds(pointInRoot, rootWidth, rootHeight))
        {
            if (!_overlayExternalDragAttempted)
            {
                _overlayExternalDragAttempted = true;
                Grid dragCard = _overlayPointerCard;
                string? detailPath = _overlayPressedItem.DetailPath;
                string originTag = _overlayPressedInFolder ? "folder-overlay" : "hidden-overlay";
                if (!string.IsNullOrWhiteSpace(detailPath) &&
                    _host?.TryStartRecommendedExternalDragOut(
                        dragCard,
                        e,
                        [detailPath],
                        originTag,
                        SuspendOverlayDragForExternalHandoff) == true)
                {
                    e.Handled = true;
                    return;
                }
            }

            if (pointInRoot.X < 0 ||
                pointInRoot.Y < 0 ||
                pointInRoot.X > rootWidth ||
                pointInRoot.Y > rootHeight)
            {
                if (_overlayPressedInFolder)
                {
                    FolderDragGhostTransform.X = pointInHost.Position.X - (FolderDragGhostBorder.Width / 2);
                    FolderDragGhostTransform.Y = pointInHost.Position.Y - (FolderDragGhostBorder.Height / 2);
                    FolderDropInsertIndicator.Visibility = Visibility.Collapsed;
                }

                e.Handled = true;
                return;
            }
        }
        else
        {
            _overlayExternalDragAttempted = false;
        }

        if (!_overlayPressedInFolder)
        {
            e.Handled = true;
            return;
        }

        FolderDragGhostTransform.X = pointInHost.Position.X - (FolderDragGhostBorder.Width / 2);
        FolderDragGhostTransform.Y = pointInHost.Position.Y - (FolderDragGhostBorder.Height / 2);

        Point pointInCard = e.GetCurrentPoint(FolderOverlayCard).Position;
        var folderCardBounds = new Rect(0, 0, FolderOverlayCard.ActualWidth, FolderOverlayCard.ActualHeight);
        if (!folderCardBounds.Contains(pointInCard))
        {
            // Dragging outside the folder card (within the popup) extracts the app back to the main grid on release.
            FolderDropInsertIndicator.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        SearchRecommendationLayoutEngine.AutoScrollScrollViewerOnDrag(
            FolderOverlayScrollViewer,
            e.GetCurrentPoint(FolderOverlayScrollViewer).Position);

        (int reorderIndex, Point barPos) = EvaluateOverlayFolderReorderIndex(
            e.GetCurrentPoint(FolderOverlaySurface).Position,
            _overlayPressedItem);
        if (reorderIndex >= 0)
        {
            FolderDropInsertTransform.X = barPos.X;
            FolderDropInsertTransform.Y = barPos.Y;
            FolderDropInsertIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            FolderDropInsertIndicator.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private void OverlayAppCard_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayPressedItem is not { } pressedItem ||
            _overlayPointerCard is not { } pressedCard ||
            !ReferenceEquals(sender, pressedCard))
        {
            return;
        }

        // Snapshot gesture state before ReleasePointerCapture synchronously
        // fires OverlayAppCard_PointerCaptureLost -> CancelOverlayDrag().
        bool wasDragging = _overlayIsDragging;
        bool inFolder = _overlayPressedInFolder;
        string? openFolderId = _openFolderId;
        Point pointInRoot = e.GetCurrentPoint(GetPopupBoundsRoot()).Position;
        var popupBounds = new Rect(0, 0, GetPopupBoundsRoot().ActualWidth, GetPopupBoundsRoot().ActualHeight);
        Point pointInTile = e.GetCurrentPoint(pressedCard).Position;
        var tileBounds = new Rect(0, 0, pressedCard.ActualWidth, pressedCard.ActualHeight);
        Point pointInFolderCard = inFolder
            ? e.GetCurrentPoint(FolderOverlayCard).Position
            : default;
        Point pointInFolderSurface = inFolder
            ? e.GetCurrentPoint(FolderOverlaySurface).Position
            : default;
        int reorderIndex = wasDragging && inFolder && !string.IsNullOrWhiteSpace(openFolderId)
            ? EvaluateOverlayFolderReorderIndex(pointInFolderSurface, pressedItem).TargetIndex
            : -1;
        pressedCard.ReleasePointerCapture(e.Pointer);

        if (wasDragging && inFolder && !string.IsNullOrWhiteSpace(openFolderId))
        {
            var folderBounds = new Rect(0, 0, FolderOverlayCard.ActualWidth, FolderOverlayCard.ActualHeight);
            CancelOverlayDrag();

            if (!folderBounds.Contains(pointInFolderCard) && popupBounds.Contains(pointInRoot))
            {
                bool removed = _viewModel.RemoveRecommendationFromFolder(
                    openFolderId,
                    pressedItem,
                    out bool folderStillExists);
                App.Log(
                    $"[RecDrag] drag-out folder={openFolderId} removed={removed} " +
                    $"folderStillExists={folderStillExists}");
                // Any successful extraction closes the overlay: the folder
                // card blocks the main grid, hiding where the app landed.
                // (Previously it closed only when the folder dissolved.)
                if (removed || !folderStillExists)
                {
                    CloseRecommendationOverlays();
                }
            }
            else if (folderBounds.Contains(pointInFolderCard) && reorderIndex >= 0)
            {
                string draggedKey = Helpers.AppRecommendationIdentity.GetCanonicalKey(pressedItem);
                _viewModel.ReorderFolderRecommendation(openFolderId, draggedKey, reorderIndex);
            }

            e.Handled = true;
            return;
        }

        CancelOverlayDrag();
        if (!wasDragging && tileBounds.Contains(pointInTile))
        {
            if (inFolder)
            {
                if (!_isRecommendationEditMode)
                {
                    _viewModel.ExecuteItem(pressedItem);
                }
            }
            else
            {
                _viewModel.UnhideRecommendation(pressedItem);
                if (!_viewModel.HasHiddenRecommendations)
                {
                    CloseRecommendationOverlays();
                }
            }
        }
        else if (!wasDragging)
        {
            ClearHoveredOverlayCard();
        }

        e.Handled = true;
    }

    private void OverlayAppCard_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayPointerCard is not null && ReferenceEquals(sender, _overlayPointerCard))
        {
            CancelOverlayDrag();
        }
    }

    private void ClearHoveredOverlayCard()
    {
        if (_hoveredOverlayCard is not null)
        {
            _hoveredOverlayCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            _hoveredOverlayCard = null;
        }
    }

    private void OverlayAppCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayIsDragging || (_host?.IsDragOutInProgress == true) || sender is not Grid card)
        {
            return;
        }

        if (_hoveredOverlayCard is not null && !ReferenceEquals(_hoveredOverlayCard, card))
        {
            _hoveredOverlayCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        _hoveredOverlayCard = card;
        card.Background = ResolveLocalThemeBrush("SubtleFillColorSecondaryBrush");
    }

    private void OverlayAppCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid card)
        {
            return;
        }

        if (ReferenceEquals(_hoveredOverlayCard, card))
        {
            _hoveredOverlayCard = null;
        }

        card.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
    }

    private void SuspendOverlayDragForExternalHandoff()
    {
        if (_overlayPointerCard is not null)
        {
            _overlayPointerCard.Opacity = 1;
            _overlayPointerCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            _overlayPointerCard = null;
        }

        _overlayPressedItem = null;
        _overlayIsDragging = false;
        _overlayPressedInFolder = false;
        _overlayExternalDragAttempted = false;
        FolderDragGhostBorder.Visibility = Visibility.Collapsed;
        FolderDragGhostImage.Source = null;
        FolderDropInsertIndicator.Visibility = Visibility.Collapsed;
    }

    private void CancelOverlayDrag()
    {
        if (_overlayPointerCard is not null)
        {
            _overlayPointerCard.Opacity = 1;
            _overlayPointerCard.ReleasePointerCaptures();
            _overlayPointerCard = null;
        }

        _overlayPressedItem = null;
        _overlayIsDragging = false;
        _overlayPressedInFolder = false;
        _overlayExternalDragAttempted = false;
        FolderDragGhostBorder.Visibility = Visibility.Collapsed;
        FolderDragGhostImage.Source = null;
        FolderDropInsertIndicator.Visibility = Visibility.Collapsed;
    }

    private (int TargetIndex, Point BarPosition) EvaluateOverlayFolderReorderIndex(
        Point pointInSurface,
        SearchResultItem draggedItem)
    {
        int count = _folderOverlayItems.Count;
        if (count <= 1)
        {
            return (-1, default);
        }

        string draggedKey = Helpers.AppRecommendationIdentity.GetCanonicalKey(draggedItem);
        int sourceIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(
                    Helpers.AppRecommendationIdentity.GetCanonicalKey(_folderOverlayItems[i]),
                    draggedKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                sourceIndex = i;
                break;
            }
        }

        if (sourceIndex < 0)
        {
            return (-1, default);
        }

        FrameworkElement? lastTile = null;
        Rect lastRect = default;
        int matchedIndex = -1;
        Rect matchedRect = default;
        double bestCenterDistanceSq = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (FolderAppsRepeater.TryGetElement(i) is not FrameworkElement tile ||
                tile.ActualWidth <= 0 ||
                tile.ActualHeight <= 0)
            {
                continue;
            }

            var rect = new Rect(
                tile.TransformToVisual(FolderOverlaySurface).TransformPoint(new Point(0, 0)),
                new Size(tile.ActualWidth, tile.ActualHeight));
            lastTile = tile;
            lastRect = rect;

            // Expand by half the 6px inter-tile spacing (+2px overlap) so gaps never blink off.
            var hitRect = new Rect(rect.X - 5, rect.Y - 5, rect.Width + 10, rect.Height + 10);
            if (!hitRect.Contains(pointInSurface))
            {
                continue;
            }

            double cx = rect.X + (rect.Width * 0.5);
            double cy = rect.Y + (rect.Height * 0.5);
            double distSq = ((pointInSurface.X - cx) * (pointInSurface.X - cx)) +
                            ((pointInSurface.Y - cy) * (pointInSurface.Y - cy));
            if (distSq < bestCenterDistanceSq)
            {
                bestCenterDistanceSq = distSq;
                matchedIndex = i;
                matchedRect = rect;
            }
        }

        if (matchedIndex >= 0)
        {
            if (matchedIndex == sourceIndex)
            {
                return (-1, default);
            }

            bool beforeTile = (pointInSurface.X - matchedRect.X) < (matchedRect.Width * 0.5);
            int slotIndex = beforeTile ? matchedIndex : matchedIndex + 1;
            if (slotIndex == sourceIndex || slotIndex == sourceIndex + 1)
            {
                return (-1, default);
            }

            int targetIndex = slotIndex > sourceIndex ? slotIndex - 1 : slotIndex;
            double barX = beforeTile ? Math.Max(0, matchedRect.X - 4.5) : matchedRect.Right + 1.5;
            double barY = matchedRect.Y + ((matchedRect.Height - FolderDropInsertIndicator.Height) / 2);
            return (targetIndex, new Point(barX, barY));
        }

        if (lastTile is not null &&
            sourceIndex < count - 1 &&
            (pointInSurface.Y > lastRect.Bottom - 8 ||
             (pointInSurface.Y >= lastRect.Y - 4 && pointInSurface.X >= lastRect.Right)))
        {
            double barX = lastRect.Right + 2;
            double barY = lastRect.Y + ((lastRect.Height - FolderDropInsertIndicator.Height) / 2);
            return (count - 1, new Point(barX, barY));
        }

        return (-1, default);
    }

    private void FolderOverlayScrollViewer_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CancelOverlayDrag();
        var source = e.OriginalSource as DependencyObject;
        var item = SearchRecommendationLayoutEngine.FindDataContext<SearchResultItem>(source);
        if (item is null || string.IsNullOrWhiteSpace(_openFolderId))
        {
            return;
        }

        string folderId = _openFolderId;
        UIElement anchor = (UIElement?)SearchRecommendationLayoutEngine.FindVisualAncestor<Grid>(source) ?? FolderOverlayScrollViewer;
        MenuFlyout flyout = BuildFolderOverlayItemContextMenu(folderId, item);
        if (flyout.Items.Count > 0)
        {
            flyout.Closed += (_, _) => ClearHoveredOverlayCard();
            flyout.ShowAt(anchor, e.GetPosition(anchor));
            e.Handled = true;
        }
    }

    private void HiddenOverlayScrollViewer_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = SearchRecommendationLayoutEngine.FindDataContext<SearchResultItem>(source);
        if (item is null)
        {
            return;
        }

        UIElement anchor = (UIElement?)SearchRecommendationLayoutEngine.FindVisualAncestor<Grid>(source) ?? HiddenOverlayScrollViewer;
        MenuFlyout flyout = BuildHiddenOverlayItemContextMenu(item);
        if (flyout.Items.Count > 0)
        {
            flyout.Closed += (_, _) => ClearHoveredOverlayCard();
            flyout.ShowAt(anchor, e.GetPosition(anchor));
            e.Handled = true;
        }
    }


    private MenuFlyout BuildFolderOverlayItemContextMenu(string folderId, SearchResultItem item)
    {
        var flyout = new MenuFlyout();
        _host?.AppendCommonRecommendedAppActions(flyout, item);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var removeFromFolderItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.RemoveFromFolder"),
            Icon = new FontIcon { Glyph = "\uE8F1" }
        };
        removeFromFolderItem.Click += (_, _) =>
        {
            _viewModel.RemoveRecommendationFromFolder(folderId, item, out bool stillExists);
            if (!stillExists)
            {
                CloseRecommendationOverlays();
            }
        };
        flyout.Items.Add(removeFromFolderItem);

        var hideItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.Hide"),
            Icon = new FontIcon { Glyph = WindowsCompatibilityService.IsWindows11OrLater ? "\uED1A" : "\uE890" }
        };
        hideItem.Click += (_, _) => _viewModel.HideRecommendation(item);
        flyout.Items.Add(hideItem);

        return flyout;
    }

    private MenuFlyout BuildHiddenOverlayItemContextMenu(SearchResultItem item)
    {
        var flyout = new MenuFlyout();

        var unhideItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Recommend.Unhide"),
            Icon = new FontIcon { Glyph = "\uE7B3" }
        };
        unhideItem.Click += (_, _) =>
        {
            _viewModel.UnhideRecommendation(item);
            if (!_viewModel.HasHiddenRecommendations)
            {
                CloseRecommendationOverlays();
            }
        };
        flyout.Items.Add(unhideItem);

        flyout.Items.Add(new MenuFlyoutSeparator());
        _host?.AppendCommonRecommendedAppActions(flyout, item);
        return flyout;
    }
}
