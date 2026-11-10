using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{

    internal static readonly TimeSpan StackPopoverIdleReleaseDelay =
        TimeSpan.FromSeconds(45);
    internal const int StackSpringLoadDelayMilliseconds = 500;
    internal const int StackPopoverLightDismissMaxDeferralMilliseconds = 450;
    private const int StackSpringLoadedAutoCloseGraceMilliseconds = 270;
    private static readonly UIntPtr StackPopoverDropSubclassId = new(0xDDB3);
    private static WeakReference<FileSurfaceContent>? s_leasedStackPopoverOwner;

    internal static FileSurfaceContent? TryGetActiveStackPopoverOwner(IntPtr rootWindowHandle)
    {
        if (rootWindowHandle == IntPtr.Zero ||
            s_leasedStackPopoverOwner?.TryGetTarget(out FileSurfaceContent? owner) != true ||
            owner is null ||
            owner._isDisposed ||
            !owner._stackPopoverPopupOpen ||
            owner._stackPopoverHostWindow is not { WindowHandle: var hwnd } ||
            hwnd != rootWindowHandle)
        {
            return null;
        }

        return owner;
    }

    private StackPopoverHostWindow? _stackPopoverHostWindow;
    private NativeDropHostBridge? _stackPopoverNativeDropBridge;
    private bool _stackPopoverNativeDragOver;
    private long _stackPopoverInboundEpoch;
    private long _lastStackPopoverXamlClaimedEpoch = -1;
    private string? _lastStackPopoverXamlClaimedSessionId;
    private long _lastStackPopoverNativeClaimedGestureId;
    private long _lastStackPopoverNativeCompletedTick64;
    private string? _lastStackPopoverNativeFirstPath;
    private DispatcherQueueTimer? _deferredStackPopoverDismissTimer;
    private long _deferredStackPopoverDismissStartedTick;
    private Win32Helper.POINT? _deferredStackPopoverDismissOriginCursor;
    private bool _deferredStackPopoverDismissPointerMoved;
    private bool _bypassStackPopoverLightDismissDeferral;
    private DispatcherQueueTimer? _stackSpringLoadTimer;
    private string? _pendingSpringLoadStackKey;
    private bool _stackPopoverRevealWithoutActivation;
    private bool _stackPopoverOpenedBySpringLoad;
    private DispatcherQueueTimer? _springLoadedPopoverWatchTimer;
    private DispatcherQueueTimer? _stackPopoverOutboundPassthroughTimer;
    private bool _stackPopoverDragForeignWindowActivated;
    private long _springLoadedPopoverOutsideSinceTick;
    private DispatcherQueueTimer? _stackPopoverIdleReleaseTimer;
    private Windows.Graphics.RectInt32 _stackPopoverScreenBounds =
        new(0, 0, 0, 0);
    private ListViewBase? _stackPopoverItemsView;
    // Keep one source instance for the lifetime of the cached popup. Replacing
    // ItemsSource on every open makes WinUI rebuild its view/recycle pool and
    // leaves native template allocations behind after repeated light dismisses.
    private readonly ObservableCollection<WidgetItem> _stackPopoverItems = [];
    private Button? _stackPopoverCloseButton;
    private Border? _stackPopoverSurface;
    private Grid? _stackPopoverTitleHost;
    private TextBlock? _stackPopoverTitleText;
    private TextBox? _stackPopoverTitleEditor;
    private TextBlock? _stackPopoverEmptyText;
    private Canvas? _stackPopoverReorderOverlay;
    private Border? _stackPopoverReorderIndicator;
    private Canvas? _stackPopoverSelectionOverlay;
    private Border? _stackPopoverSelectionRectangle;
    private Grid? _stackPopoverSelectionHost;
    private StackPopoverLayout? _stackPopoverLayout;
    private int _stackPopoverReorderInsertionIndex = -1;
    private WidgetItem[] _stackPopoverMembers = [];
    private string? _stackPopoverKey;
    // The stack whose members _stackPopoverItems currently holds. Unlike
    // _stackPopoverKey it survives popover close, so reopening a DIFFERENT
    // stack can detect the switch and reset the realized containers.
    private string? _stackPopoverItemsStackKey;
    private bool _stackPopoverPopupOpen;
    private bool _stackPopoverPopupClosing;
    private bool _stackPopoverIsListMode;
    private bool _stackPopoverContextMenuOpen;
    private bool _stackPopoverSystemContextMenuOpen;
    private Services.FlyoutBridgeBandPromoter? _popoverFlyoutBandPromoter;
    private bool _stackPopoverDragActive;
    private bool _stackPopoverCleanupPending;
    private bool _stackPopoverCleanupRestoreOwnerForeground = true;
    private string? _lightDismissedByOwnTileClickKey;
    private long _lightDismissedByOwnTileClickTick;
    private long _stackPopoverShowGeneration;
    private string? _pendingStackPopoverKey;
    private EventHandler<object>? _stackPopoverRevealRenderingHandler;
    private DispatcherQueueTimer? _stackPopoverRevealFallbackTimer;
    private int _stackPopoverRevealFrameCount;
    private long _stackPopoverOpenedAtTick;
    private KeyEventHandler? _stackPopoverPreviewKeyHandler;
    private PointerEventHandler? _stackPopoverExpansionWheelHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerPressedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerMovedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerReleasedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerCaptureLostHandler;
    private PointerEventHandler? _stackPopoverSurfacePointerPressedHandler;
    private bool _stackPopoverTitleEditing;
    private bool _stackPopoverTitleCommitInProgress;
    private bool _stackPopoverTitleCancelling;
    private string? _stackPopoverTitleOriginalName;
    private long _stackPopoverTitleOpenedAtTick;
    private bool _stackPopoverLayoutRefreshQueued;
    private int _stackPopoverIconContainerStyleSignature;

    private bool IsStackPopoverInteractionActive =>
        _stackPopoverItemsView is not null &&
        (_stackPopoverPopupOpen || _stackPopoverContextMenuOpen);

    internal bool IsStackPopoverBlockingSurfaceOpen =>
        _stackPopoverPopupOpen ||
        _stackPopoverPopupClosing ||
        _stackPopoverContextMenuOpen ||
        _stackPopoverDragActive ||
        _stackPopoverTitleEditing ||
        IsStackPopoverItemRenameEditing;

    internal IntPtr ActiveStackPopoverWindowHandle =>
        (_stackPopoverPopupOpen ||
            _stackPopoverPopupClosing ||
            _pendingStackPopoverKey is not null) &&
        _stackPopoverHostWindow is { } host
            ? host.WindowHandle
            : IntPtr.Zero;

    internal void CloseOpenStackPopover()
    {
        if (_stackPopoverPopupOpen ||
            _stackPopoverPopupClosing ||
            _pendingStackPopoverKey is not null)
        {
            CloseStackPopover(restoreOwnerForeground: false);
        }
    }

    internal static bool IsActiveStackPopoverWindow(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero &&
        s_leasedStackPopoverOwner is { } lease &&
        lease.TryGetTarget(out FileSurfaceContent? owner) &&
        owner is { _isDisposed: false, _stackPopoverPopupOpen: true, _stackPopoverHostWindow: { } host } &&
        host.WindowHandle == windowHandle;

    internal static void DismissActiveStackPopoverFromDesktopPinnedClick(
        IntPtr clickedWidgetHandle,
        object? originalSource)
    {
        if (s_leasedStackPopoverOwner is not { } lease ||
            !lease.TryGetTarget(out FileSurfaceContent? owner) ||
            owner is not { _isDisposed: false, _stackPopoverPopupOpen: true })
        {
            return;
        }

        if (clickedWidgetHandle == owner._hostWindowHandle &&
            FindItemFromSource(originalSource) is WidgetStackItem)
        {
            return;
        }

        UiDispatch.PostAfterCallback(() =>
        {
            if (!owner._isDisposed && owner._stackPopoverPopupOpen)
            {
                owner.StackPopoverHost_DeactivatedByOutsideClick();
            }
        });
    }

    private void InitializeStackPopoverLifecycle()
    {
        ViewModel.PropertyChanged += ViewModel_StackPopoverPropertyChanged;
    }

    private void DisposeStackPopoverLifecycle()
    {
        ViewModel.PropertyChanged -= ViewModel_StackPopoverPropertyChanged;
        DisposeStackSpringLoadAndDismissTimers();
        CloseStackPopover(releaseImmediately: true);
    }

    private void ViewModel_StackPopoverPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetViewModel.FileStackOpenMode))
        {
            UpdateStackFolderPreviewModes();
            if (!ViewModel.UsesStackPopover)
            {
                CloseStackPopover(releaseImmediately: true);
                return;
            }
        }

        if (IsStackPopoverLayoutProperty(e.PropertyName))
        {
            // Layout settings update the stack item metrics later in the same
            // dispatcher turn. Reapply after that update so the local visual
            // values continue to follow the configured icon size.
            QueueStackPopoverLayoutRefresh();
        }

        if (e.PropertyName is nameof(WidgetViewModel.IsIconMode) or
            nameof(WidgetViewModel.IsListMode))
        {
            CloseStackPopover(releaseImmediately: true);
        }
    }

    private static bool IsStackPopoverLayoutProperty(string? propertyName) =>
        propertyName is nameof(WidgetViewModel.IconTileWidth) or
            nameof(WidgetViewModel.IconTileHeight) or
            nameof(WidgetViewModel.IconTileMargin) or
            nameof(WidgetViewModel.IconTilePadding) or
            nameof(WidgetViewModel.IconContentSpacing) or
            nameof(WidgetViewModel.IconImageSize) or
            nameof(WidgetViewModel.IconLabelMaxWidth) or
            nameof(WidgetViewModel.IconLabelFontSize) or
            nameof(WidgetViewModel.IconLabelMaxLines) or
            nameof(WidgetViewModel.IconLabelVisibility) or
            nameof(WidgetViewModel.ListItemMargin) or
            nameof(WidgetViewModel.ListItemPadding) or
            nameof(WidgetViewModel.ListIconSize) or
            nameof(WidgetViewModel.ListLabelFontSize) or
            nameof(WidgetViewModel.EffectiveIconSize);

    private void QueueStackPopoverLayoutRefresh()
    {
        if (_stackPopoverLayoutRefreshQueued)
        {
            return;
        }

        _stackPopoverLayoutRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _stackPopoverLayoutRefreshQueued = false;
            if (_isDisposed)
            {
                return;
            }

            UpdateStackFolderPreviewModes();
            if (_stackPopoverKey is not { } stackKey ||
                ViewModel.FindStackByKey(stackKey) is not { } stack)
            {
                return;
            }

            ApplyStackPopoverLayout(stack);
            _stackPopoverSurface?.UpdateLayout();
        }))
        {
            _stackPopoverLayoutRefreshQueued = false;
        }
    }

    internal static readonly TimeSpan StackPopoverRevealFallbackDelay =
        TimeSpan.FromMilliseconds(48);

    private void UpdateStackFolderPreviewModes()
    {
        foreach (Border surface in EnumerateRealizedStackSurfaces())
        {
            ApplyStackFolderPreviewMode(surface);
        }
    }

    private void ApplyStackFolderPreviewMode(Border surface)
    {
        if (surface.DataContext is not WidgetStackItem stack ||
            FindDescendantByTag(
                surface,
                "StackPreviewHost") is not Grid previewHost ||
            FindDescendantByTag(
                surface,
                "StackPopoverFolderBackdrop") is not Border backdrop ||
            FindDescendantByTag(surface, "StackPreviewOne") is not Grid one ||
            FindDescendantByTag(surface, "StackPreviewTwo") is not Grid two ||
            FindDescendantByTag(surface, "StackPreviewThree") is not Grid three ||
            FindDescendantByTag(surface, "StackPreviewFour") is not Grid four ||
            FindDescendantByTag(
                surface,
                "StackPreviewCountBadge") is not Border countBadge)
        {
            return;
        }

        TextBlock? countText = FindDescendantByTag(
            surface,
            "StackPreviewCountText") as TextBlock;
        bool isListMode = ViewModel.IsListMode;
        if (!ViewModel.UsesStackPopover)
        {
            RestoreInlineStackPreview(
                previewHost,
                backdrop,
                one,
                two,
                three,
                four,
                countBadge,
                countText,
                isListMode,
                previewSize: isListMode
                    ? stack.ListIconSize
                    : stack.PreviewSize);
            return;
        }

        double previewSize = isListMode
            ? stack.ListIconSize
            : stack.PreviewSize;
        double previewItemSize = isListMode
            ? stack.ListIconSize
            : stack.PreviewItemSize;
        StackFolderPreviewMetrics metrics =
            StackFolderPreviewMetricsCalculator.Calculate(
                previewSize,
                previewItemSize,
                isListMode,
                WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                    _settingsService.Settings.WidgetCornerPreference));

        previewHost.Width = metrics.HostSize;
        previewHost.Height = metrics.HostSize;
        backdrop.Visibility = Visibility.Visible;
        backdrop.Margin = new Thickness(metrics.BackdropMargin);
        backdrop.CornerRadius = new CornerRadius(metrics.CornerRadius);
        countBadge.Visibility = Visibility.Collapsed;
        four.Visibility = stack.FourthPreviewVisibility;
        ApplyFolderMiniature(
            one,
            metrics.MiniatureScale,
            -metrics.MiniatureOffset,
            -metrics.MiniatureOffset);
        ApplyFolderMiniature(
            two,
            metrics.MiniatureScale,
            metrics.MiniatureOffset,
            -metrics.MiniatureOffset);
        ApplyFolderMiniature(
            three,
            metrics.MiniatureScale,
            -metrics.MiniatureOffset,
            metrics.MiniatureOffset);
        ApplyFolderMiniature(
            four,
            metrics.MiniatureScale,
            metrics.MiniatureOffset,
            metrics.MiniatureOffset);

        countBadge.MinWidth = metrics.BadgeSize;
        countBadge.Height = metrics.BadgeSize;
        countBadge.Padding = new Thickness(
            metrics.BadgeSize >= 14 ? 2 : 1,
            0,
            metrics.BadgeSize >= 14 ? 2 : 1,
            0);
        countBadge.Margin = new Thickness(
            0,
            0,
            metrics.InnerPadding,
            metrics.InnerPadding);
        countBadge.CornerRadius = new CornerRadius(metrics.BadgeSize / 2);
        if (countText is not null)
        {
            countText.FontSize = metrics.BadgeFontSize;
        }
    }

    private static void ApplyFolderMiniature(
        Grid preview,
        double scale,
        double translateX,
        double translateY)
    {
        preview.Margin = new Thickness(0);
        preview.Opacity = 1;
        preview.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        CompositeTransform transform =
            preview.RenderTransform as CompositeTransform ?? new CompositeTransform();
        // The XAML fan layout leaves rotations on the second and third
        // previews; folder mode renders a straight 2x2 grid instead.
        transform.Rotation = 0;
        transform.SkewX = 0;
        transform.SkewY = 0;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        transform.TranslateX = translateX;
        transform.TranslateY = translateY;
        if (!ReferenceEquals(preview.RenderTransform, transform))
        {
            preview.RenderTransform = transform;
        }
    }

    private static void RestoreInlineStackPreview(
        Grid previewHost,
        Border backdrop,
        Grid one,
        Grid two,
        Grid three,
        Grid four,
        Border countBadge,
        TextBlock? countText,
        bool isListMode,
        double previewSize)
    {
        previewHost.Width = previewSize;
        previewHost.Height = previewSize;
        backdrop.Visibility = Visibility.Collapsed;
        backdrop.Margin = new Thickness(isListMode ? 0.5 : 1);
        four.Visibility = Visibility.Collapsed;
        four.Margin = new Thickness(0);
        four.Opacity = 0;
        four.RenderTransform = null;
        countBadge.Visibility = Visibility.Visible;
        if (isListMode)
        {
            three.Margin = new Thickness(0);
            three.Opacity = 0.55;
            three.RenderTransform = null;
            two.Margin = new Thickness(4, 2, 0, 0);
            two.Opacity = 0.76;
            two.RenderTransform = null;
            one.Margin = new Thickness(0, 0, 4, 3);
            one.Opacity = 1;
            one.RenderTransform = null;
            countBadge.MinWidth = 14;
            countBadge.Height = 14;
            countBadge.Padding = new Thickness(2, 0, 2, 0);
            countBadge.Margin = new Thickness(0);
            countBadge.CornerRadius = new CornerRadius(7);
            if (countText is not null)
            {
                countText.FontSize = 8;
            }
            return;
        }

        SetInlineIconPreview(
            three,
            opacity: 0.72,
            rotation: -7,
            scale: 0.70,
            translateX: -5,
            translateY: 3);
        SetInlineIconPreview(
            two,
            opacity: 0.88,
            rotation: 6,
            scale: 0.80,
            translateX: 5,
            translateY: 2);
        SetInlineIconPreview(
            one,
            opacity: 1,
            rotation: 0,
            scale: 0.90,
            translateX: 0,
            translateY: -2);
        countBadge.MinWidth = 16;
        countBadge.Height = 16;
        countBadge.Padding = new Thickness(3, 0, 3, 0);
        countBadge.Margin = new Thickness(0, 0, 1, 1);
        countBadge.CornerRadius = new CornerRadius(8);
        if (countText is not null)
        {
            countText.FontSize = 9;
        }
    }

    private static void SetInlineIconPreview(
        Grid preview,
        double opacity,
        double rotation,
        double scale,
        double translateX,
        double translateY)
    {
        preview.Margin = new Thickness(0);
        preview.Opacity = opacity;
        preview.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.82);
        CompositeTransform transform =
            preview.RenderTransform as CompositeTransform ?? new CompositeTransform();
        transform.Rotation = rotation;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        transform.TranslateX = translateX;
        transform.TranslateY = translateY;
        if (!ReferenceEquals(preview.RenderTransform, transform))
        {
            preview.RenderTransform = transform;
        }
    }

    private void ToggleStackPopover(WidgetStackItem stack)
    {
        string stackKey = stack.StackKey;
        if (_stackPopoverPopupOpen || _stackPopoverPopupClosing)
        {
            bool sameStack = string.Equals(
                _stackPopoverKey,
                stackKey,
                StringComparison.Ordinal);
            if (sameStack &&
                _stackPopoverPopupOpen &&
                !_stackPopoverPopupClosing &&
                _settingsService.Settings.FileWidget.DoubleClickToOpen)
            {
                long sinceOpened = Environment.TickCount64 - _stackPopoverOpenedAtTick;
                if (sinceOpened >= 0 &&
                    sinceOpened < Win32Helper.GetDoubleClickTime())
                {
                    return;
                }
            }

            CloseStackPopover();
            if (sameStack)
            {
                return;
            }

            QueueStackPopoverShow(stackKey);
            return;
        }

        if (string.Equals(
                _pendingStackPopoverKey,
                stackKey,
                StringComparison.Ordinal))
        {
            return;
        }

        string? dismissedOwnTileKey = _lightDismissedByOwnTileClickKey;
        long dismissedOwnTileTick = _lightDismissedByOwnTileClickTick;
        _lightDismissedByOwnTileClickKey = null;
        if (string.Equals(dismissedOwnTileKey, stackKey, StringComparison.Ordinal) &&
            Environment.TickCount64 - dismissedOwnTileTick <= 600)
        {
            return;
        }

        // A normally dismissed popover keeps its control tree as a bounded
        // per-surface cache. Reopening it only rebinds the current members.
        QueueStackPopoverShow(stackKey);
    }

    private FrameworkElement? ResolveStackPopoverAnchorHint(string stackKey)
    {
        if (_lastStackInputAnchor is null ||
            !_lastStackInputAnchor.TryGetTarget(out FrameworkElement? candidate) ||
            candidate is null ||
            candidate.XamlRoot is null ||
            candidate.DataContext is not WidgetStackItem candidateStack ||
            !string.Equals(candidateStack.StackKey, stackKey, StringComparison.Ordinal))
        {
            return null;
        }

        return candidate;
    }

    private void QueueStackPopoverShow(string stackKey)
    {
        CancelStackPopoverReveal();
        long generation = ++_stackPopoverShowGeneration;
        _pendingStackPopoverKey = stackKey;
        App.LogVerbose(
            $"[FileStack] Popover show queued widget={WidgetId} " +
            $"stack={stackKey} generation={generation}");
        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _stackPopoverShowGeneration ||
                !string.Equals(
                    _pendingStackPopoverKey,
                    stackKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (_isDisposed ||
                _stackPopoverPopupOpen ||
                _stackPopoverPopupClosing)
            {
                // Keep the request until Popup.Closed has finished releasing
                // the previous presentation. This matters when light dismiss
                // completes asynchronously.
                return;
            }

            if (ViewModel.UsesStackPopover &&
                ViewModel.FindStackByKey(stackKey) is { } current)
            {
                ShowStackPopoverCore(current, generation);
            }
            else
            {
                ClearPendingStackPopoverShow(generation, stackKey);
            }
        });
        if (!queued && generation == _stackPopoverShowGeneration)
        {
            _pendingStackPopoverKey = null;
        }
    }

    private void ShowStackPopover(WidgetStackItem stack)
    {
        CancelStackPopoverReveal();
        long generation = ++_stackPopoverShowGeneration;
        _pendingStackPopoverKey = stack.StackKey;
        ShowStackPopoverCore(stack, generation);
    }

    private void ShowStackPopoverCore(
        WidgetStackItem stack,
        long generation)
    {
        if (generation != _stackPopoverShowGeneration ||
            !string.Equals(
                _pendingStackPopoverKey,
                stack.StackKey,
                StringComparison.Ordinal))
        {
            return;
        }

        if (_isDisposed ||
            !ViewModel.UsesStackPopover ||
            _stackPopoverPopupOpen ||
            _stackPopoverPopupClosing ||
            XamlRoot is null)
        {
            ClearPendingStackPopoverShow(generation, stack.StackKey);
            return;
        }

        WidgetStackItem? currentStack =
            ViewModel.FindStackByKey(stack.StackKey);
        if (currentStack is null || currentStack.Members.Count == 0)
        {
            ClearPendingStackPopoverShow(generation, stack.StackKey);
            return;
        }

        FrameworkElement? anchor = FindStackSurface(currentStack.StackKey) ??
            ResolveStackPopoverAnchorHint(currentStack.StackKey);
        if (anchor is null)
        {
            App.LogVerbose(
                $"[FileStack] Popover anchor unresolved; falling back to cursor/host placement " +
                $"widget={WidgetId} stack={currentStack.StackKey}");
        }

        try
        {
            CancelStackPopoverIdleReleaseTimer();
            ClaimSingletonStackPopoverLease();

            StackPopoverLayout layout = CalculateStackPopoverLayout(
                currentStack.Members.Count);

            // ListView and GridView have different native templates. Recreate
            // the hosted tree only when the host view mode changes; ordinary
            // open/close cycles reuse the same host window, backdrop, realized
            // containers included — no island churn, no per-cycle allocation.
            if (_stackPopoverHostWindow is { } cachedHost &&
                _stackPopoverIsListMode != ViewModel.IsListMode)
            {
                bool preserveNoActivate = _stackPopoverRevealWithoutActivation;
                bool preserveSpringLoad = _stackPopoverOpenedBySpringLoad;
                ReleaseStackPopover();
                // Release clears ordinary pending state. This open request is
                // still current and is about to build the correctly typed tree.
                _pendingStackPopoverKey = currentStack.StackKey;
                _stackPopoverRevealWithoutActivation = preserveNoActivate;
                _stackPopoverOpenedBySpringLoad = preserveSpringLoad;
            }

            StackPopoverHostWindow host;
            if (_stackPopoverHostWindow is null)
            {
                ListViewBase itemsView = CreateStackPopoverItemsView(layout);
                Border surface = CreateStackPopoverSurface(
                    currentStack,
                    itemsView,
                    layout);
                host = new StackPopoverHostWindow(_hostWindowHandle);
                host.SetContent(surface);
                host.DeactivatedByOutsideClick +=
                    StackPopoverHost_DeactivatedByOutsideClick;
                host.EscapeRequested += StackPopoverHost_EscapeRequested;
                host.UndoRequested += StackPopoverHost_UndoRequested;
                _stackPopoverHostWindow = host;
                _stackPopoverItemsView = itemsView;
                _stackPopoverSurface = surface;
                _stackPopoverIsListMode = ViewModel.IsListMode;
            }
            else
            {
                host = _stackPopoverHostWindow;
            }

            EnsureStackPopoverNativeDropBridge(host);

            if (_stackPopoverItemsView is null || _stackPopoverSurface is null)
            {
                ReleaseStackPopover();
                return;
            }

            _stackPopoverKey = currentStack.StackKey;
            _stackPopoverMembers = currentStack.Members.ToArray();
            ViewModel.SetOpenPopoverStackKey(currentStack.StackKey);
            // The popover is the only surface that displays every folded
            // member at once; acquiring their icons here keeps the
            // release/acquire pairing intact when a release ran while the
            // stack was collapsed (group switch, long-hidden maintenance).
            ViewModel.HealReleasedItemIconsIfIdle();
            _stackPopoverLayout = layout;
            _stackPopoverPopupClosing = false;
            _stackPopoverCleanupPending = false;
            _stackPopoverItemsView.SelectedItems.Clear();
            // While the popover owns interaction, a lingering main-surface
            // selection must not keep an expanded label under the popover.
            CollapseMainSurfaceNameExpansion();
            bool switchingStacks =
                !string.IsNullOrEmpty(_stackPopoverItemsStackKey) &&
                !string.Equals(
                    _stackPopoverItemsStackKey,
                    currentStack.StackKey,
                    StringComparison.Ordinal);
            // Switching to a different stack replaces the inner XAML view inside
            // the persistent host window so no recycled ItemsWrapGrid containers
            // from the previous stack survive, while keeping the host HWND/island.
            if (switchingStacks)
            {
                RebuildStackPopoverContentForHost(
                    host,
                    currentStack,
                    layout);
            }

            if (_stackPopoverItemsView is null || _stackPopoverSurface is null)
            {
                ReleaseStackPopover();
                return;
            }

            ReconcileStackPopoverItems(_stackPopoverMembers);
            _stackPopoverItemsStackKey = currentStack.StackKey;
            _stackPopoverEmptyText?.Visibility =
                _stackPopoverMembers.Length == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            _stackPopoverSurface.DataContext = currentStack;
            AutomationProperties.SetName(
                _stackPopoverSurface,
                currentStack.Name);
            if (!_stackPopoverTitleEditing &&
                _stackPopoverTitleText is { } title)
            {
                title.Text = currentStack.Name;
                AutomationProperties.SetName(title, currentStack.Name);
            }
            ApplyStackPopoverLayout(currentStack);
            UpdateStackPopoverAppearance();
            // Rebind and re-measure the reused tree while the host window is
            // still parked, so opening a different stack never flashes the
            // previous stack's tiles before the refresh lands.
            _stackPopoverSurface.UpdateLayout();
            ConfigureStackPopoverItemsPanel(
                _stackPopoverItemsView,
                layout);
            LogStackPopoverRevealReadiness(currentStack);

            ShowStackPopoverHost(
                host,
                anchor,
                layout,
                generation,
                currentStack.StackKey,
                switchingStacks);
        }
        catch (Exception ex)
        {
            // Everything from tree construction to the native window show runs
            // on the UI thread; an unexpected failure must degrade to "the
            // popover does not open" instead of taking down the whole app.
            App.Log(
                $"[FileStack] Popover open failed widget={WidgetId} " +
                $"stack={currentStack.StackKey}: {ex}");
            ReleaseStackPopover();
        }
    }

    private void ShowStackPopoverHost(
        StackPopoverHostWindow host,
        FrameworkElement? anchor,
        StackPopoverLayout layout,
        long generation,
        string stackKey,
        bool waitForContentCommit)
    {
        double hostScale = Math.Max(
            0.5,
            Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                XamlRoot));
        int left;
        int top;
        double targetScale = hostScale;
        if (anchor is not null &&
            _hostWindowHandle != IntPtr.Zero &&
            Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT hostBounds))
        {
            StackPopoverPosition position = ResolveStackPopoverPosition(
                anchor,
                layout.Width,
                layout.Height);
            left = hostBounds.Left + (int)Math.Round(position.Left * hostScale);
            top = hostBounds.Top + (int)Math.Round(position.Top * hostScale);
            int probeX = left + (int)Math.Round((layout.Width * hostScale) / 2);
            int probeY = top + (int)Math.Round((layout.Height * hostScale) / 2);
            targetScale = Math.Max(0.5, Win32Helper.GetDpiScaleForPoint(probeX, probeY));
        }
        else if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            targetScale = Math.Max(0.5, Win32Helper.GetDpiScaleForPoint(cursor.X, cursor.Y));
            int cursorWidth = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                layout.Width,
                targetScale);
            int cursorHeight = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                layout.Height,
                targetScale);
            left = cursor.X - (cursorWidth / 2);
            top = cursor.Y - (cursorHeight / 2);
            if (Win32Helper.TryGetMonitorWorkArea(
                    cursor.X,
                    cursor.Y,
                    out _,
                    out Win32Helper.RECT workArea))
            {
                left = Math.Clamp(
                    left,
                    workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - cursorWidth));
                top = Math.Clamp(
                    top,
                    workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - cursorHeight));
            }
        }
        else if (_hostWindowHandle != IntPtr.Zero &&
                 Win32Helper.GetWindowRect(
                     _hostWindowHandle,
                     out Win32Helper.RECT fallbackHostBounds))
        {
            int hostCenterX = fallbackHostBounds.Left +
                (fallbackHostBounds.Right - fallbackHostBounds.Left) / 2;
            int hostCenterY = fallbackHostBounds.Top +
                (fallbackHostBounds.Bottom - fallbackHostBounds.Top) / 2;
            targetScale = Math.Max(0.5, Win32Helper.GetDpiScaleForPoint(hostCenterX, hostCenterY));
            int fallbackWidth = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                layout.Width,
                targetScale);
            int fallbackHeight = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                layout.Height,
                targetScale);
            left = hostCenterX - (fallbackWidth / 2);
            top = hostCenterY - (fallbackHeight / 2);
        }
        else
        {
            left = 0;
            top = 0;
        }

        int width = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
            layout.Width,
            targetScale);
        int height = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
            layout.Height,
            targetScale);

        _stackPopoverScreenBounds = new Windows.Graphics.RectInt32(
            left,
            top,
            width,
            height);
        host.PrepareForShow(_stackPopoverScreenBounds);
        // Geometry diagnostics for the stack-popover clipping investigation:
        // one line with every computed input and one deferred line with what
        // the framework actually realized, so a single log from an affected
        // machine pinpoints the failing link (calculator, DPI conversion,
        // window bounds, or panel wrap).
        App.Log(
            $"[FileStack] Popover geometry widget={WidgetId} " +
            $"mode={(ViewModel.IsListMode ? "list" : "icons")} " +
            $"count={_stackPopoverMembers.Length} " +
            $"tile={ViewModel.IconTileWidth:0.#}x{ViewModel.IconTileHeight:0.#} " +
            $"grid={layout.Columns}x{layout.VisibleRows} " +
            $"cell={layout.CellWidth:0.#}x{layout.CellHeight:0.#} " +
            $"items={layout.ItemsWidth:0.#}x{layout.ItemsHeight:0.#} " +
            $"win={layout.Width:0.#}x{layout.Height:0.#} " +
            $"scale={targetScale:0.###} phys={width}x{height} at={left},{top}");
        if (waitForContentCommit)
        {
            QueueStackPopoverRevealAfterContentCommit(
                host,
                generation,
                stackKey);
            return;
        }

        CompleteStackPopoverReveal(host, generation, stackKey);
    }

    private void QueueStackPopoverRevealAfterContentCommit(
        StackPopoverHostWindow host,
        long generation,
        string stackKey)
    {
        CancelStackPopoverReveal();
        _stackPopoverRevealFrameCount = 0;
        _stackPopoverRevealRenderingHandler = (_, _) =>
        {
            if (!CanCompleteStackPopoverReveal(host, generation, stackKey))
            {
                CancelStackPopoverReveal();
                return;
            }

            // Frame one commits the rebound XAML surface while the HWND stays
            // off-screen. Revealing on frame two guarantees that DWM no longer
            // has to reuse the previously opened stack's presented texture.
            if (++_stackPopoverRevealFrameCount < 2)
            {
                // Nudge the cloaked XAML tree so the render loop schedules
                // frame two on the very next VSync instead of going idle when
                // no uncloaked element is animating.
                _stackPopoverSurface?.InvalidateArrange();
                return;
            }

            App.LogVerbose(
                $"[FileStack] Popover content committed widget={WidgetId} " +
                $"stack={stackKey} frames={_stackPopoverRevealFrameCount}");
            CompleteStackPopoverReveal(host, generation, stackKey);
        };
        CompositionTarget.Rendering += _stackPopoverRevealRenderingHandler;
        ArmStackPopoverRevealFallbackTimer(host, generation, stackKey);
    }

    private bool CanCompleteStackPopoverReveal(
        StackPopoverHostWindow host,
        long generation,
        string stackKey) =>
        !_isDisposed &&
        generation == _stackPopoverShowGeneration &&
        ReferenceEquals(_stackPopoverHostWindow, host) &&
        string.Equals(_stackPopoverKey, stackKey, StringComparison.Ordinal) &&
        string.Equals(
            _pendingStackPopoverKey,
            stackKey,
            StringComparison.Ordinal);

    private void CompleteStackPopoverReveal(
        StackPopoverHostWindow host,
        long generation,
        string stackKey)
    {
        if (!CanCompleteStackPopoverReveal(host, generation, stackKey))
        {
            CancelStackPopoverReveal();
            return;
        }

        CancelStackPopoverReveal();
        _pendingStackPopoverKey = null;
        // Raise the open flag BEFORE RevealPrepared: Activate() inside it fires
        // the owner window's Deactivated synchronously, and the selection-clear
        // guard must already see the popover as open.
        _stackPopoverPopupOpen = true;
        _stackPopoverOpenedAtTick = Environment.TickCount64;
        bool revealWithoutActivation = _stackPopoverRevealWithoutActivation;
        _stackPopoverRevealWithoutActivation = false;
        try
        {
            if (revealWithoutActivation)
            {
                host.RevealPreparedWithoutActivation(_stackPopoverScreenBounds);
            }
            else
            {
                host.RevealPrepared(_stackPopoverScreenBounds);
            }
            EnsureStackPopoverNativeDropBridge(host);
            _ = host.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                LogStackPopoverRealizedGeometry);
            if (!revealWithoutActivation)
            {
                App.Current?.WidgetManager?.ReassertRaisedWidgetGroupAfterDeskBoxActivation(
                    _hostWindowHandle,
                    "stack-popover-opened");
            }
            App.LogVerbose(
                $"[FileStack] Popover opened widget={WidgetId} " +
                $"stack={stackKey} noActivate={revealWithoutActivation}");
            if (!revealWithoutActivation)
            {
                _stackPopoverItemsView?.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileStack] Popover reveal failed widget={WidgetId} " +
                $"stack={stackKey}: {ex}");
            ReleaseStackPopover();
        }
    }

    private void CancelStackPopoverReveal()
    {
        _stackPopoverRevealFallbackTimer?.Stop();
        if (_stackPopoverRevealRenderingHandler is not null)
        {
            CompositionTarget.Rendering -= _stackPopoverRevealRenderingHandler;
            _stackPopoverRevealRenderingHandler = null;
        }

        _stackPopoverRevealFrameCount = 0;
    }

    private void ClearPendingStackPopoverShow(
        long generation,
        string stackKey)
    {
        if (generation == _stackPopoverShowGeneration &&
            string.Equals(
                _pendingStackPopoverKey,
                stackKey,
                StringComparison.Ordinal))
        {
            _pendingStackPopoverKey = null;
        }
    }

    /// <summary>
    /// Diagnostic for the switch-stack first-frame investigation: records what
    /// the realized panel actually shows right before the popover is revealed.
    /// If the first container's data context still names the previous stack,
    /// the item rebind is not completing before the reveal.
    /// </summary>
    private void LogStackPopoverRevealReadiness(WidgetStackItem stack)
    {
        try
        {
            string expectedFirst = _stackPopoverMembers.Length > 0
                ? _stackPopoverMembers[0].Name
                : "<empty>";
            string realizedFirst = "<none>";
            if (_stackPopoverItemsView?.ItemsPanelRoot is { } panel &&
                panel.Children.Count > 0)
            {
                DependencyObject firstChild = panel.Children[0];
                object? realizedItem =
                    (firstChild as FrameworkElement)?.DataContext ??
                    (firstChild as ContentControl)?.Content ??
                    (_stackPopoverItemsView.ContainerFromIndex(0) as ContentControl)
                        ?.Content;
                realizedFirst = (realizedItem as WidgetItem)?.Name
                    ?? realizedItem?.GetType().Name
                    ?? "<null>";
            }

            App.Log(
                $"[FileStack] Reveal readiness widget={WidgetId} " +
                $"stack={stack.Name} expectedFirst='{expectedFirst}' " +
                $"realizedFirst='{realizedFirst}' " +
                $"sourceCount={_stackPopoverItems.Count} " +
                $"panelChildren={_stackPopoverItemsView?.ItemsPanelRoot?.Children.Count ?? -1}");
        }
        catch (Exception ex)
        {
            App.Log($"[FileStack] Reveal readiness probe failed: {ex.Message}");
        }
    }

    private void LogStackPopoverRealizedGeometry()
    {
        if (_isDisposed ||
            !_stackPopoverPopupOpen ||
            _stackPopoverHostWindow is not { } host ||
            _stackPopoverItemsView is not { } view)
        {
            return;
        }

        double popoverScale = view.XamlRoot?.RasterizationScale ?? 0;
        string windowRect = Win32Helper.GetWindowRect(
            host.WindowHandle,
            out Win32Helper.RECT rect)
            ? $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top} at {rect.Left},{rect.Top}"
            : "unavailable";
        string panelInfo = view.ItemsPanelRoot is { } panel
            ? $"{panel.GetType().Name} {panel.ActualWidth:0.#}x{panel.ActualHeight:0.#}"
            : "null";
        string visibleRange = view.ItemsPanelRoot is Microsoft.UI.Xaml.Controls.ItemsWrapGrid wrap
            ? $" visible={wrap.FirstVisibleIndex}..{wrap.LastVisibleIndex}" +
                $" cell={wrap.ItemWidth:0.##}x{wrap.ItemHeight:0.##}" +
                $" max={wrap.MaximumRowsOrColumns}"
            : string.Empty;

        App.Log(
            $"[FileStack] Popover realized widget={WidgetId} " +
            $"view={view.ActualWidth:0.#}x{view.ActualHeight:0.#} " +
            $"items={view.Items.Count} panel={panelInfo}{visibleRange} " +
            $"popoverScale={popoverScale:0.###} window={windowRect}");
    }

    private void StackPopoverHost_DeactivatedByOutsideClick()
    {
        if (_isDisposed ||
            !_stackPopoverPopupOpen ||
            _stackPopoverContextMenuOpen ||
            _stackPopoverDragActive)
        {
            return;
        }

        string? closingStackKey = _stackPopoverKey;
        Win32Helper.POINT? deactivationCursor =
            _stackPopoverHostWindow?.LastDeactivationCursorPoint;
        bool clickedOwnAnchorTile = closingStackKey is not null &&
            IsScreenPointInsideStackSurface(closingStackKey, deactivationCursor);
        if (!_bypassStackPopoverLightDismissDeferral &&
            !clickedOwnAnchorTile &&
            ShouldDeferStackPopoverLightDismiss(
                mouseButtonDown: IsAnyMouseButtonPressed(),
                dragActive: IsAnyPopoverTargetDragInFlight(),
                nativeDragOver: _stackPopoverNativeDragOver,
                elapsedSinceDeactivateMs: 0))
        {
            ArmDeferredStackPopoverDismissTimer(deactivationCursor);
            return;
        }

        if (clickedOwnAnchorTile)
        {
            _lightDismissedByOwnTileClickKey = closingStackKey;
            _lightDismissedByOwnTileClickTick = Environment.TickCount64;
        }
        else
        {
            _lightDismissedByOwnTileClickKey = null;
        }

        if (IsStackPopoverItemRenameEditing)
        {
            // Keep the same commit-on-lost-focus behavior as the normal file
            // surface. CloseStackPopover defers the hide until the async file
            // rename has finished, so an activation change cannot tear down
            // the editor while the filesystem operation is still in flight.
            CloseStackPopover(restoreOwnerForeground: false);
            return;
        }

        CommitStackPopoverTitleRename();
        HideStackPopoverForReuse(restoreOwnerForeground: false);
        App.Current?.WidgetManager?.NotifyTransientPopupDismissedFromWidget(_hostWindowHandle);
    }

    private bool IsScreenPointInsideStackSurface(
        string stackKey,
        Win32Helper.POINT? screenPoint)
    {
        if (screenPoint is not { } pt ||
            _hostWindowHandle == IntPtr.Zero ||
            FindStackSurface(stackKey) is not
            {
                XamlRoot: not null,
                ActualWidth: > 0,
                ActualHeight: > 0
            } anchor ||
            !Win32Helper.GetWindowRect(_hostWindowHandle, out Win32Helper.RECT hostRect))
        {
            return false;
        }

        IntPtr pointWindow = Win32Helper.WindowFromPoint(pt);
        if (pointWindow == IntPtr.Zero ||
            (pointWindow != _hostWindowHandle &&
             Win32Helper.GetAncestor(pointWindow, Win32Helper.GA_ROOT) != _hostWindowHandle))
        {
            return false;
        }

        double scale = Math.Max(
            0.5,
            Win32Helper.GetDpiScaleForWindow(_hostWindowHandle, XamlRoot));
        Windows.Foundation.Point topLeftDip =
            anchor.TransformToVisual(null).TransformPoint(default);
        double left = hostRect.Left + (topLeftDip.X * scale);
        double top = hostRect.Top + (topLeftDip.Y * scale);
        double right = left + (anchor.ActualWidth * scale);
        double bottom = top + (anchor.ActualHeight * scale);
        return pt.X >= left && pt.X <= right && pt.Y >= top && pt.Y <= bottom;
    }

    private async void StackPopoverHost_UndoRequested()
    {
        if (_isDisposed || !_stackPopoverPopupOpen)
        {
            return;
        }

        await ExecuteUndoLatestMoveAsync();
    }

    private void StackPopoverHost_EscapeRequested()
    {
        if (_isDisposed || !_stackPopoverPopupOpen)
        {
            return;
        }

        if (_isBoxSelecting && _stackPopoverItemsView is { } marqueeView)
        {
            CancelBoxSelection(marqueeView);
            return;
        }

        if (_stackPopoverTitleEditing)
        {
            CancelStackPopoverTitleRename();
            return;
        }

        if (IsStackPopoverItemRenameEditing)
        {
            CancelStackPopoverItemRename();
            return;
        }

        CloseStackPopover();
    }

    private StackPopoverLayout CalculateStackPopoverLayout(int itemCount)
    {
        (double workAreaWidth, double workAreaHeight) =
            ResolveStackPopoverWorkArea();
        double listRowHeight =
            ViewModel.ListIconSize +
            ViewModel.ListItemPadding.Top +
            ViewModel.ListItemPadding.Bottom + 6;
        return StackPopoverLayoutCalculator.Calculate(
            ViewModel.IsListMode,
            itemCount,
            Math.Max(ActualWidth, Config.Width),
            workAreaWidth,
            workAreaHeight,
            ViewModel.IconTileWidth,
            ViewModel.IsListMode
                ? listRowHeight
                : ViewModel.IconTileHeight,
            SettingsService.NormalizeFileStackPopoverLayout(
                _settingsService.Settings.FileStackPopoverLayout));
    }

    private ListViewBase CreateStackPopoverItemsView(
        StackPopoverLayout layout)
    {
        ListViewBase view;
        if (ViewModel.IsListMode)
        {
            view = new ListView
            {
                ItemContainerStyle =
                    Resources["SurfaceListViewItemStyle"] as Style,
                ItemTemplate =
                    Resources["StackPopoverFileListTemplate"] as DataTemplate
            };
        }
        else
        {
            view = new GridView
            {
                ItemTemplate =
                    Resources["StackPopoverFileIconTemplate"] as DataTemplate
            };
        }

        view.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        // Keep the native panel virtualized and avoid the default item-container
        // transition objects. The popup already has a bounded viewport; animating
        // every recycled child adds compositor work and retains transition state.
        // The ItemsPanel assignment is conditional: under Native AOT the template
        // resources cross back into managed code as the base FrameworkTemplate
        // type, and assigning the resulting null would wipe the control's
        // default panel and silently fall back to a vertical StackPanel. Leaving
        // the property untouched keeps GridView's horizontally wrapping default.
        object? panelTemplate = ViewModel.IsListMode
            ? Resources["StackPopoverListItemsPanelTemplate"]
            : Resources["StackPopoverIconItemsPanelTemplate"];
        if (panelTemplate is ItemsPanelTemplate concretePanel)
        {
            view.ItemsPanel = concretePanel;
        }
        view.ItemContainerTransitions = null;
        view.ItemsSource = _stackPopoverItems;
        view.Width = layout.ItemsWidth;
        view.MaxHeight = layout.ItemsHeight;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Stretch;
        view.IsItemClickEnabled = true;
        view.CanReorderItems = false;
        view.AllowDrop = true;
        view.IsMultiSelectCheckBoxEnabled = false;
        view.SelectionMode = ListViewSelectionMode.Extended;
        view.Loaded += StackPopoverItemsView_Loaded;
        ScrollViewer.SetVerticalScrollBarVisibility(
            view,
            layout.HasVerticalOverflow
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            view,
            ScrollBarVisibility.Disabled);
        UpdateStackPopoverIconItemContainerStyle(
            view,
            layout,
            force: true);

        view.ItemClick += Items_ItemClick;
        view.ContainerContentChanging += ItemsView_ContainerContentChanging;
        view.DragOver += StackPopoverItems_DragOver;
        view.DragLeave += StackPopoverItems_DragLeave;
        view.Drop += StackPopoverItems_Drop;
        view.DoubleTapped += Items_DoubleTapped;
        view.KeyDown += Root_KeyDown;
        view.RightTapped += Items_RightTapped;
        view.SelectionChanged += Items_SelectionChanged;
        view.CharacterReceived += Root_CharacterReceived;
        _stackPopoverExpansionWheelHandler =
            new PointerEventHandler(StackPopoverItems_PointerWheelDuringExpansion);
        view.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _stackPopoverExpansionWheelHandler,
            handledEventsToo: true);
        _stackPopoverPreviewKeyHandler =
            new KeyEventHandler(ItemsView_PreviewKeyDown);
        view.AddHandler(
            UIElement.PreviewKeyDownEvent,
            _stackPopoverPreviewKeyHandler,
            handledEventsToo: true);
        RegisterScrollBarActivityTracking(view);
        return view;
    }

    private void StackPopoverItems_PointerWheelDuringExpansion(
        object sender, PointerRoutedEventArgs e)
    {
        // The popover's expanded-label overlay is positioned against the
        // popover viewport; wheel scrolling invalidates it, so collapse
        // instead of chasing the item.
        HideNameExpansion(isPopoverView: true);
    }

    private void StackPopoverItemsView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is ListViewBase view &&
            _stackPopoverLayout is { } layout)
        {
            ConfigureStackPopoverItemsPanel(view, layout);
        }
    }

    private static void ConfigureStackPopoverItemsPanel(
        ListViewBase itemsView,
        StackPopoverLayout layout)
    {
        if (itemsView.ItemsPanelRoot is not ItemsWrapGrid wrap)
        {
            return;
        }

        // WinUI snaps item slots to whole physical pixels. With fractional DPI
        // scales (1.25/1.5) the per-slot rounding grows with the column count
        // and can push the last column out of the viewport, turning a
        // requested 3-column grid into 2+2+1 on 2K displays. Snapping the
        // slots down to physical pixels keeps every configured column inside
        // the viewport at any scale.
        double scale = itemsView.XamlRoot?.RasterizationScale ?? 1;

        wrap.Orientation = Orientation.Horizontal;
        wrap.ItemWidth = StackPopoverPixelCalculator.ToContainedLogicalSize(
            layout.CellWidth,
            scale);
        wrap.ItemHeight = StackPopoverPixelCalculator.ToContainedLogicalSize(
            layout.CellHeight,
            scale);
        wrap.MaximumRowsOrColumns = Math.Max(1, layout.Columns);
    }

    private Style CreateStackPopoverIconItemContainerStyle(
        StackPopoverLayout layout)
    {
        var containerStyle = new Style(typeof(GridViewItem))
        {
            BasedOn = Resources["SurfaceGridViewItemStyle"] as Style
        };
        double horizontalMargin = Math.Max(
            0,
            (layout.CellWidth - ViewModel.IconTileWidth) / 2);
        double verticalMargin = Math.Max(
            0,
            (layout.CellHeight - ViewModel.IconTileHeight) / 2);
        containerStyle.Setters.Add(new Setter(
            FrameworkElement.WidthProperty,
            ViewModel.IconTileWidth));
        containerStyle.Setters.Add(new Setter(
            FrameworkElement.MinHeightProperty,
            ViewModel.IconTileHeight));
        containerStyle.Setters.Add(new Setter(
            FrameworkElement.MarginProperty,
            new Thickness(
                horizontalMargin,
                verticalMargin,
                horizontalMargin,
                verticalMargin)));
        return containerStyle;
    }

    private void UpdateStackPopoverIconItemContainerStyle(
        ListViewBase itemsView,
        StackPopoverLayout layout,
        bool force = false)
    {
        if (itemsView is not GridView)
        {
            return;
        }

        int signature = HashCode.Combine(
            layout.CellWidth,
            layout.CellHeight,
            ViewModel.IconTileWidth,
            ViewModel.IconTileHeight);
        if (!force && signature == _stackPopoverIconContainerStyleSignature)
        {
            return;
        }

        itemsView.ItemContainerStyle =
            CreateStackPopoverIconItemContainerStyle(layout);
        _stackPopoverIconContainerStyleSignature = signature;
    }

    private Border CreateStackPopoverSurface(
        WidgetStackItem stack,
        ListViewBase itemsView,
        StackPopoverLayout layout)
    {
        var content = new Grid();
        ApplyStackPopoverForegroundResources(content);
        content.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        content.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        var titleHost = new Grid
        {
            Height = StackPopoverLayoutCalculator.TitleHeight,
            Margin = new Thickness(
                0,
                0,
                0,
                StackPopoverLayoutCalculator.TitleBottomSpacing),
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        double titleMaximumWidth = Math.Max(
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            layout.Width -
                (StackPopoverLayoutCalculator.SurfacePadding * 2) -
                StackPopoverLayoutCalculator.TitleTrailingButtonWidth);
        var title = new TextBlock
        {
            Text = stack.Name,
            MinWidth = Math.Min(
                StackPopoverLayoutCalculator.TitleMinimumWidth,
                titleMaximumWidth),
            MaxWidth = titleMaximumWidth,
            Height = StackPopoverLayoutCalculator.TitleHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        titleHost.DoubleTapped += StackPopoverTitle_DoubleTapped;
        AutomationProperties.SetName(title, stack.Name);
        _stackPopoverTitleHost = titleHost;
        _stackPopoverTitleText = title;
        var titleEditor = new TextBox
        {
            MaxWidth = titleMaximumWidth,
            Height = StackPopoverLayoutCalculator.TitleEditorHeight,
            MaxLength = 128,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Style = Application.Current.Resources.TryGetValue(
                "WidgetInlineRenameTextBoxStyle",
                out object? inlineRenameStyleValue)
                ? inlineRenameStyleValue as Style
                : null
        };
        if (ResolveBrush("TextFillColorPrimaryBrush") is { } titleEditorForeground)
        {
            titleEditor.Foreground = titleEditorForeground;
        }
        titleEditor.Loaded += StackPopoverTitleEditor_Loaded;
        titleEditor.KeyDown += StackPopoverTitleEditor_KeyDown;
        titleEditor.LostFocus += StackPopoverTitleEditor_LostFocus;
        AutomationProperties.SetName(titleEditor, T("Common.Rename"));
        _stackPopoverTitleEditor = titleEditor;
        var closeButton = new Button
        {
            Style = Application.Current.Resources.TryGetValue(
                "WidgetInlineEditorCloseButtonStyle",
                out object? closeStyleValue)
                ? closeStyleValue as Style
                : null,
            Content = new FontIcon
            {
                Glyph = "",
                FontSize = 10
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeButton.Click += StackPopoverCloseButton_Click;
        AutomationProperties.SetName(
            closeButton,
            T("Widget.Stack.Popover.Close"));
        titleHost.Children.Add(title);
        titleHost.Children.Add(titleEditor);
        titleHost.Children.Add(closeButton);
        _stackPopoverCloseButton = closeButton;
        content.Children.Add(titleHost);

        var itemsHost = new Grid
        {
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        _stackPopoverSelectionPointerPressedHandler =
            StackPopoverSelectionHost_PointerPressed;
        _stackPopoverSelectionPointerMovedHandler =
            StackPopoverSelectionHost_PointerMoved;
        _stackPopoverSelectionPointerReleasedHandler =
            StackPopoverSelectionHost_PointerReleased;
        _stackPopoverSelectionPointerCaptureLostHandler =
            StackPopoverSelectionHost_PointerCaptureLost;
        itemsHost.AddHandler(
            UIElement.PointerPressedEvent,
            _stackPopoverSelectionPointerPressedHandler,
            handledEventsToo: true);
        itemsHost.AddHandler(
            UIElement.PointerMovedEvent,
            _stackPopoverSelectionPointerMovedHandler,
            handledEventsToo: true);
        itemsHost.AddHandler(
            UIElement.PointerReleasedEvent,
            _stackPopoverSelectionPointerReleasedHandler,
            handledEventsToo: true);
        itemsHost.AddHandler(
            UIElement.PointerCaptureLostEvent,
            _stackPopoverSelectionPointerCaptureLostHandler,
            handledEventsToo: true);
        _stackPopoverSelectionHost = itemsHost;
        itemsHost.Children.Add(itemsView);
        var selectionOverlay = new Canvas
        {
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(selectionOverlay, 20);
        var selectionRectangle = new Border
        {
            Width = 0,
            Height = 0,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        ApplySelectionRectangleAppearance(selectionRectangle);
        selectionOverlay.Children.Add(selectionRectangle);
        itemsHost.Children.Add(selectionOverlay);
        _stackPopoverSelectionOverlay = selectionOverlay;
        _stackPopoverSelectionRectangle = selectionRectangle;
        CreateStackPopoverItemRenameEditor(itemsHost);
        var reorderOverlay = new Canvas
        {
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(reorderOverlay, 25);
        var reorderIndicator = new Border
        {
            // Starts uncolored: UpdateStackPopoverAppearance repaints it through
            // the neutral palette once the tree is attached, so the indicator
            // never shows a color resolved from a different scope.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        reorderOverlay.Children.Add(reorderIndicator);
        itemsHost.Children.Add(reorderOverlay);
        _stackPopoverReorderOverlay = reorderOverlay;
        _stackPopoverReorderIndicator = reorderIndicator;
        var emptyText = new TextBlock
        {
            Text = T("Widget.Stack.Popover.Empty"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            Visibility = Visibility.Collapsed
        };
        _stackPopoverEmptyText = emptyText;
        itemsHost.Children.Add(emptyText);
        Grid.SetRow(itemsHost, 1);
        content.Children.Add(itemsHost);

        _stackPopoverSurfacePointerPressedHandler =
            StackPopoverSurface_PointerPressed;
        content.AddHandler(
            UIElement.PointerPressedEvent,
            _stackPopoverSurfacePointerPressedHandler,
            handledEventsToo: true);

        double cornerRadius = ResolveStackPopoverCornerRadius();
        content.CornerRadius = new CornerRadius(
            Math.Max(0, cornerRadius - StackPopoverLayoutCalculator.SurfacePadding));
        WidgetBorderVisuals borderVisuals =
            ResolveStackPopoverBorderVisuals();
        var surface = new Border
        {
            Width = layout.Width,
            Height = layout.Height,
            Padding = new Thickness(
                StackPopoverLayoutCalculator.SurfacePadding),
            CornerRadius = new CornerRadius(cornerRadius),
            Background = CreateStackPopoverSurfaceBrush(),
            BorderBrush = new SolidColorBrush(borderVisuals.BorderColor),
            BorderThickness = new Thickness(borderVisuals.Thickness),
            AllowDrop = true,
            DataContext = stack,
            Child = content
        };
        surface.DragOver += StackSurface_DragOver;
        surface.DragLeave += StackSurface_DragLeave;
        surface.Drop += StackSurface_Drop;
        AutomationProperties.SetName(surface, stack.Name);
        return surface;
    }

    private void StackPopoverTitle_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (_stackPopoverTitleEditing ||
            IsVisualDescendantOf(
                e.OriginalSource as DependencyObject,
                _stackPopoverCloseButton))
        {
            return;
        }

        if (_stackPopoverTitleText is { } title)
        {
            BeginStackPopoverTitleRename(title);
        }
        e.Handled = true;
    }

    private void StackPopoverCloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-stack-popover-close-button");
        CloseStackPopover();
    }

    private void ActivateStackPopoverInput(UIElement? focusTarget = null)
    {
        if (_stackPopoverHostWindow is not { } host)
        {
            return;
        }

        host.ActivateInput(focusTarget);
    }

    private void BeginStackPopoverTitleRename(TextBlock title)
    {
        if (_stackPopoverTitleEditing ||
            !ReferenceEquals(_stackPopoverTitleText, title) ||
            _stackPopoverTitleEditor is not { } editor ||
            _stackPopoverKey is not { } stackKey ||
            ViewModel.FindStackByKey(stackKey) is not { } stack)
        {
            return;
        }

        if (IsStackPopoverItemRenameEditing)
        {
            CancelStackPopoverItemRename(finishPendingClose: false);
        }

        const double editorHeight =
            StackPopoverLayoutCalculator.TitleEditorHeight;
        double maximumWidth = Math.Max(
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            (_stackPopoverSurface?.ActualWidth ?? 0) -
                (StackPopoverLayoutCalculator.SurfacePadding * 2) -
                StackPopoverLayoutCalculator.TitleTrailingButtonWidth);
        double editorWidth = Math.Clamp(
            Math.Max(title.ActualWidth, title.DesiredSize.Width) + 12,
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            maximumWidth);

        editor.MaxWidth = maximumWidth;
        editor.Width = editorWidth;
        editor.Height = editorHeight;
        editor.Text = stack.Name;
        if (ResolveBrush("TextFillColorPrimaryBrush") is { } foreground)
        {
            editor.Foreground = foreground;
        }

        _stackPopoverTitleEditing = true;
        _stackPopoverTitleCancelling = false;
        _stackPopoverTitleOriginalName = stack.Name;
        _stackPopoverTitleOpenedAtTick = Environment.TickCount64;
        title.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.IsHitTestVisible = true;
        HideStackPopoverTitleDeleteButton(editor);
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-stack-popover-title-rename-opened");
        ActivateStackPopoverInput();
        InlineEditorFocus.FocusWhenLoaded(
            editor,
            static editor => editor.SelectAll(),
            DispatcherQueue,
            "StackPopoverTitleRename");
    }

    private void StackPopoverTitleEditor_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitStackPopoverTitleRename();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelStackPopoverTitleRename();
        }
    }

    private void StackPopoverTitleEditor_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is TextBox editor)
        {
            HideStackPopoverTitleDeleteButton(editor);
        }
    }

    private static void HideStackPopoverTitleDeleteButton(
        DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button { Name: "DeleteButton" } deleteButton)
            {
                deleteButton.Width = 0;
                deleteButton.MinWidth = 0;
                deleteButton.MaxWidth = 0;
                deleteButton.Padding = new Thickness(0);
                deleteButton.Margin = new Thickness(0);
                deleteButton.Opacity = 0;
                deleteButton.IsHitTestVisible = false;
                return;
            }

            HideStackPopoverTitleDeleteButton(child);
        }
    }

    private static bool IsVisualDescendantOf(
        DependencyObject? source,
        DependencyObject? ancestor)
    {
        if (source is null || ancestor is null)
        {
            return false;
        }

        for (DependencyObject? current = source;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void StackPopoverTitleEditor_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_stackPopoverTitleCancelling)
        {
            _stackPopoverTitleCancelling = false;
            return;
        }

        if (DeskBox.Helpers.TransientInputFocusGuard.ShouldDeferInlineEditorBlurCommit(
                sender as Control,
                _stackPopoverTitleOpenedAtTick,
                _stackPopoverHostWindow?.WindowHandle ?? default,
                () => _stackPopoverTitleEditing,
                () =>
                {
                    CommitStackPopoverTitleRename();
                    return Task.CompletedTask;
                }))
        {
            return;
        }

        CommitStackPopoverTitleRename();
    }

    private void StackPopoverSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_stackPopoverTitleEditing &&
            !IsVisualDescendantOf(
                e.OriginalSource as DependencyObject,
                _stackPopoverTitleEditor))
        {
            CommitStackPopoverTitleRename();
        }
    }

    private void CommitStackPopoverTitleRename()
    {
        if (!_stackPopoverTitleEditing ||
            _stackPopoverTitleCommitInProgress ||
            _stackPopoverTitleEditor is not { } editor ||
            _stackPopoverTitleText is not { } title)
        {
            return;
        }

        string newName = editor.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) ||
            _stackPopoverKey is not { } stackKey ||
            ViewModel.FindStackByKey(stackKey) is not { } stack)
        {
            CancelStackPopoverTitleRename();
            return;
        }

        _stackPopoverTitleCommitInProgress = true;
        try
        {
            if (!string.Equals(stack.Name, newName, StringComparison.Ordinal))
            {
                ViewModel.SetStackNameOverride(stackKey, newName);
            }

            title.Text = newName;
            AutomationProperties.SetName(title, newName);
            if (_stackPopoverSurface is { } surface)
            {
                AutomationProperties.SetName(surface, newName);
            }
            CompleteStackPopoverTitleRename();
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (_stackPopoverHostWindow is not null)
                    {
                        ReconcileStackPopover();
                    }
                });
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Stack popover title rename failed " +
                $"id={WidgetId} key={stackKey}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "stack-popover-title-rename-error"));
            CompleteStackPopoverTitleRename();
        }
        finally
        {
            _stackPopoverTitleCommitInProgress = false;
        }
    }

    private void CancelStackPopoverTitleRename()
    {
        if (!_stackPopoverTitleEditing)
        {
            return;
        }

        _stackPopoverTitleCancelling = true;
        if (_stackPopoverTitleText is { } title &&
            _stackPopoverTitleOriginalName is { } originalName)
        {
            title.Text = originalName;
            AutomationProperties.SetName(title, originalName);
        }
        CompleteStackPopoverTitleRename();
    }

    private void CompleteStackPopoverTitleRename()
    {
        if (!_stackPopoverTitleEditing)
        {
            return;
        }

        _stackPopoverTitleEditing = false;
        _stackPopoverTitleOriginalName = null;
        if (_stackPopoverTitleEditor is { } editor)
        {
            editor.Visibility = Visibility.Collapsed;
            editor.IsHitTestVisible = false;
            editor.Text = string.Empty;
        }
        if (_stackPopoverTitleText is { } title)
        {
            title.Visibility = Visibility.Visible;
        }
        App.Current?.WidgetManager?.EndWidgetInteraction(
            "surface-stack-popover-title-rename-closed");
        if (_stackPopoverPopupOpen && !_stackPopoverCleanupPending)
        {
            _stackPopoverItemsView?.Focus(FocusState.Programmatic);
        }
        TryFinishStackPopoverCleanup();
    }

    private void ApplyStackPopoverForegroundResources(FrameworkElement scope)
    {
        Brush? primary = ResolveBrush("TextFillColorPrimaryBrush");
        Brush? secondary = ResolveBrush("TextFillColorSecondaryBrush");
        Brush? tertiary = ResolveBrush("TextFillColorTertiaryBrush");
        Brush? disabled = ResolveBrush("TextFillColorDisabledBrush");
        Brush? divider = ResolveBrush("DividerStrokeColorDefaultBrush");

        AddBrush("TextFillColorPrimaryBrush", primary);
        AddBrush("TextFillColorSecondaryBrush", secondary);
        AddBrush("TextFillColorTertiaryBrush", tertiary);
        AddBrush("TextFillColorDisabledBrush", disabled);
        AddBrush("ControlStrongFillColorDefaultBrush", primary);
        AddBrush("ControlStrongFillColorDisabledBrush", disabled);
        AddBrush("ControlStrongStrokeColorDefaultBrush", secondary);
        AddBrush("ControlStrongStrokeColorDisabledBrush", disabled);
        AddBrush("ButtonForeground", primary);
        AddBrush("ButtonForegroundPointerOver", primary);
        AddBrush("ButtonForegroundPressed", secondary);
        AddBrush("ButtonForegroundDisabled", disabled);
        AddBrush("SubtleButtonForeground", primary);
        AddBrush("SubtleButtonForegroundPointerOver", primary);
        AddBrush("SubtleButtonForegroundPressed", secondary);
        AddBrush("SubtleButtonForegroundDisabled", disabled);
        AddBrush("DividerStrokeColorDefaultBrush", divider);
        AddBrush("TextControlForeground", primary);
        AddBrush("TextControlForegroundPointerOver", primary);
        AddBrush("TextControlForegroundFocused", primary);
        AddBrush("TextControlForegroundDisabled", disabled);
        AddBrush("TextControlPlaceholderForeground", secondary);
        AddBrush("TextControlPlaceholderForegroundPointerOver", secondary);
        AddBrush("TextControlPlaceholderForegroundFocused", secondary);
        AddBrush("TextControlPlaceholderForegroundDisabled", disabled);
        AddBrush("GridViewItemForeground", primary);
        AddBrush("GridViewItemForegroundPointerOver", secondary);
        AddBrush("GridViewItemForegroundSelected", primary);
        AddBrush("ListViewItemForeground", primary);
        AddBrush("ListViewItemForegroundPointerOver", primary);
        AddBrush("ListViewItemForegroundPressed", primary);
        AddBrush("ListViewItemForegroundSelected", primary);
        AddBrush("ListViewItemForegroundSelectedPointerOver", primary);
        AddBrush("ListViewItemForegroundSelectedPressed", primary);

        void AddBrush(string key, Brush? brush)
        {
            if (brush is not null)
            {
                scope.Resources[key] = brush;
            }
        }
    }

    private WidgetMaterialBackdropAppearance
        ResolveStackPopoverMaterialAppearance()
    {
        bool isDark = IsStackPopoverDarkTheme();
        Windows.UI.Color accentColor =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        double surfaceOpacity =
            double.IsFinite(_settingsService.Settings.WidgetOpacity)
                ? Math.Clamp(
                    _settingsService.Settings.WidgetOpacity,
                    SettingsService.MinWidgetOpacity,
                    SettingsService.MaxWidgetOpacity)
                : SettingsService.DefaultWidgetOpacity;
        double materialIntensity =
            double.IsFinite(_settingsService.Settings.WidgetMaterialIntensity)
                ? Math.Clamp(
                    _settingsService.Settings.WidgetMaterialIntensity,
                    SettingsService.MinWidgetMaterialIntensity,
                    SettingsService.MaxWidgetMaterialIntensity)
                : SettingsService.DefaultWidgetMaterialIntensity;
        string materialType =
            WindowsCompatibilityService.ResolveWidgetMaterialType(
                _settingsService.Settings.WidgetMaterialType);
        return new WidgetMaterialBackdropAppearance(
            materialType,
            isDark,
            accentColor,
            surfaceOpacity,
            materialIntensity);
    }

    private SolidColorBrush CreateStackPopoverSurfaceBrush() =>
        CreateStackPopoverSurfaceBrush(
            ResolveStackPopoverMaterialAppearance());

    private static SolidColorBrush CreateStackPopoverSurfaceBrush(
        WidgetMaterialBackdropAppearance appearance)
    {
        bool materialSupported = WidgetMaterialSystemBackdrop.IsSupported(
            appearance.MaterialType);
        Windows.UI.Color surfaceColor;
        if (materialSupported)
        {
            surfaceColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }
        else
        {
            surfaceColor =
                WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                    appearance.IsDark,
                    appearance.AccentColor,
                    appearance.SurfaceOpacity);
            if (surfaceColor.A == 0)
            {
                surfaceColor = Windows.UI.Color.FromArgb(
                    1,
                    surfaceColor.R,
                    surfaceColor.G,
                    surfaceColor.B);
            }
        }

        return SharedBrushCache.GetOrCreate(surfaceColor);
    }

    private double ResolveStackPopoverCornerRadius() =>
        WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                _settingsService.Settings.WidgetCornerPreference));

    private WidgetBorderVisuals ResolveStackPopoverBorderVisuals()
    {
        bool isDark = IsStackPopoverDarkTheme();
        Windows.UI.Color accentColor =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        WidgetBorderVisuals visuals = WidgetBorderVisualCalculator.Resolve(
            _settingsService.Settings.WidgetBorderStyle,
            _settingsService.Settings.WidgetBorderColorMode,
            isDark,
            accentColor);
        if (visuals.Thickness <= 0 || visuals.BorderColor.A == 0)
        {
            byte channel = isDark ? (byte)0xFF : (byte)0x00;
            byte alpha = isDark ? (byte)0x22 : (byte)0x16;
            return new WidgetBorderVisuals(
                1.0,
                Windows.UI.Color.FromArgb(alpha, channel, channel, channel),
                visuals.DividerColor);
        }

        return visuals;
    }

    private bool IsStackPopoverDarkTheme() =>
        NeutralInteractionBrush.IsDarkTheme(this);

    private int _stackPopoverAppearanceSignature;

    private void UpdateStackPopoverAppearance()
    {
        if (_stackPopoverSurface is null)
        {
            return;
        }

        bool followMaterial = UsesStackPopoverMaterialStyle();
        // Per-open refresh rewrites the content resource dictionary, which
        // invalidates list-item templates on the persistent tree. Skip it
        // entirely while nothing in the visual signature changed.
        var appearanceSignature = new HashCode();
        appearanceSignature.Add(IsStackPopoverDarkTheme());
        appearanceSignature.Add(followMaterial);
        appearanceSignature.Add(_settingsService.Settings.WidgetMaterialType);
        appearanceSignature.Add(
            App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor);
        appearanceSignature.Add(_settingsService.Settings.WidgetOpacity);
        appearanceSignature.Add(
            _settingsService.Settings.WidgetMaterialIntensity);
        appearanceSignature.Add(ResolveStackPopoverCornerRadius());
        appearanceSignature.Add(_settingsService.Settings.WidgetBorderStyle);
        appearanceSignature.Add(
            _settingsService.Settings.WidgetBorderColorMode);
        int signature = appearanceSignature.ToHashCode();
        if (signature == _stackPopoverAppearanceSignature &&
            _stackPopoverHostWindow is not null)
        {
            return;
        }

        _stackPopoverAppearanceSignature = signature;
        WidgetMaterialBackdropAppearance materialAppearance =
            ResolveStackPopoverMaterialAppearance();
        ElementTheme requestedTheme = materialAppearance.IsDark
            ? ElementTheme.Dark
            : ElementTheme.Light;
        double cornerRadius = ResolveStackPopoverCornerRadius();
        WidgetBorderVisuals borderVisuals =
            ResolveStackPopoverBorderVisuals();
        Brush background = followMaterial
            ? CreateStackPopoverSurfaceBrush(materialAppearance)
            : CreateStackPopoverNeutralBrush(materialAppearance.IsDark);
        _stackPopoverSurface.RequestedTheme = requestedTheme;
        _stackPopoverSurface.Background = background;
        _stackPopoverSurface.CornerRadius = new CornerRadius(cornerRadius);
        if (_stackPopoverSurface.Child is Grid contentGrid)
        {
            contentGrid.CornerRadius = new CornerRadius(
                Math.Max(0, cornerRadius - StackPopoverLayoutCalculator.SurfacePadding));
        }
        _stackPopoverSurface.BorderBrush =
            SharedBrushCache.GetOrCreate(borderVisuals.BorderColor);
        _stackPopoverSurface.BorderThickness =
            new Thickness(borderVisuals.Thickness);

        Brush? primary = ResolveBrush("TextFillColorPrimaryBrush");
        Brush? secondary = ResolveBrush("TextFillColorSecondaryBrush");
        if (_stackPopoverSurface.Child is FrameworkElement content)
        {
            content.RequestedTheme = requestedTheme;
            ApplyStackPopoverForegroundResources(content);
        }
        if (_stackPopoverTitleText is not null)
        {
            _stackPopoverTitleText.Foreground = primary;
        }
        if (_stackPopoverTitleEditor is not null)
        {
            _stackPopoverTitleEditor.Foreground = primary;
        }
        if (_stackPopoverItemRenameEditor is not null)
        {
            _stackPopoverItemRenameEditor.Foreground = primary;
        }
        if (_stackPopoverEmptyText is not null)
        {
            _stackPopoverEmptyText.Foreground = secondary;
        }
        if (_stackPopoverReorderIndicator is not null)
        {
            _stackPopoverReorderIndicator.Background = SharedBrushCache.GetOrCreate(
                NeutralInteractionBrush.Line(_stackPopoverReorderIndicator));
        }

        _stackPopoverHostWindow?.UpdateAppearance(materialAppearance, followMaterial);
    }

    private bool UsesStackPopoverMaterialStyle() =>
        SettingsService.NormalizeFileStackPopoverStyle(
            _settingsService.Settings.FileStackPopoverStyle) ==
        SettingsService.FileStackPopoverStyleFollowMaterial;

    private static SolidColorBrush CreateStackPopoverNeutralBrush(
        bool isDark) =>
        // Semi-transparent solid over the plain acrylic backdrop: reads as a
        // calm neutral surface while keeping the window on the fast DWM
        // composition path (fully opaque content on a backdrop-less window
        // forces the layered-window path — the jank and flash source).
        SharedBrushCache.GetOrCreate(isDark
            ? Windows.UI.Color.FromArgb(0xD8, 0x2B, 0x2B, 0x31)
            : Windows.UI.Color.FromArgb(0xE0, 0xF2, 0xF2, 0xF5));

    private void UpdateStackPopoverScrollPolicy(int visibleItemCount)
    {
        if (_stackPopoverItemsView is not { } view ||
            _stackPopoverLayout is not { } layout)
        {
            return;
        }

        int visibleCapacity = ViewModel.IsListMode
            ? layout.VisibleRows
            : layout.Columns * layout.VisibleRows;
        ScrollViewer.SetVerticalScrollBarVisibility(
            view,
            visibleItemCount > visibleCapacity
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled);
    }

    private void CloseStackPopover(
        bool releaseImmediately = false,
        bool restoreOwnerForeground = true)
    {
        _stackPopoverShowGeneration++;
        CancelStackPopoverReveal();
        _pendingStackPopoverKey = null;
        if (_stackPopoverHostWindow is null)
        {
            return;
        }

        CommitStackPopoverTitleRename();
        if (releaseImmediately)
        {
            ReleaseStackPopover();
            return;
        }

        if (IsStackPopoverItemRenameEditing)
        {
            _stackPopoverCleanupPending = true;
            _stackPopoverCleanupRestoreOwnerForeground = restoreOwnerForeground;
            _ = CommitStackPopoverItemRenameAsync();
            return;
        }

        if (_stackPopoverContextMenuOpen ||
            _stackPopoverDragActive ||
            _stackPopoverTitleEditing)
        {
            _stackPopoverCleanupPending = true;
            _stackPopoverCleanupRestoreOwnerForeground = restoreOwnerForeground;
            return;
        }

        if (restoreOwnerForeground)
        {
            HideStackPopoverForReuse();
        }
        else
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }
    }

    /// <summary>
    /// Hides the persistent host window and resets transient interaction
    /// state. The realized tree stays alive inside the hidden window, so
    /// reopening performs no container or island work at all.
    /// </summary>
    private void HideStackPopoverForReuse() =>
        HideStackPopoverForReuse(restoreOwnerForeground: true);

    private void HideStackPopoverForReuse(bool restoreOwnerForeground)
    {
        if (_stackPopoverHostWindow is not { } host)
        {
            return;
        }

        CommitStackPopoverTitleRename();
        if (IsStackPopoverItemRenameEditing)
        {
            _stackPopoverCleanupPending = true;
            _stackPopoverCleanupRestoreOwnerForeground = restoreOwnerForeground;
            _ = CommitStackPopoverItemRenameAsync();
            return;
        }

        try
        {
            host.HidePopover(restoreOwnerForeground);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileStack] Popover hide failed widget={WidgetId}: {ex}");
        }

        if (_stackPopoverItemsView is { } view)
        {
            view.SelectedItems.Clear();
        }

        // The popover no longer owns interaction, so a lingering main-surface
        // sole selection may expand its label again; refresh from the now
        // active main view.
        UpdateItemSurfaceVisuals();

        if (_stackPopoverSurface is { } surface)
        {
            surface.DataContext = null;
        }

        CancelDeferredStackPopoverDismissTimer();
        StopInteractiveAutoScroll(InteractiveAutoScrollMode.StackPopoverDrag);
        StopSpringLoadedPopoverWatchTimer();
        StopStackPopoverOutboundDragPassthroughWatch();
        _stackPopoverOpenedBySpringLoad = false;
        _stackPopoverRevealWithoutActivation = false;
        _stackPopoverNativeDragOver = false;
        ResetBoxSelectionState();
        HideStackPopoverReorderIndicator();
        _stackPopoverMembers = [];
        _stackPopoverKey = null;
        ViewModel.SetOpenPopoverStackKey(null);
        _stackPopoverLayout = null;
        _stackPopoverScreenBounds = new Windows.Graphics.RectInt32(0, 0, 0, 0);
        ArmStackPopoverIdleReleaseTimer();
        _stackPopoverPopupOpen = false;
        _stackPopoverPopupClosing = false;
        _stackPopoverCleanupPending = false;
        _stackPopoverCleanupRestoreOwnerForeground = true;
        _stackPopoverContextMenuOpen = false;
        _stackPopoverSystemContextMenuOpen = false;
        _stackPopoverDragActive = false;
        UpdateSelectionCommandBar();
    }

    private void ReconcileStackPopoverItems(
        IReadOnlyList<WidgetItem> target)
    {
        // Keep the source object stable and only notify the selector when the
        // member identity/order actually changed. In particular, reopening the
        // same large stack emits no collection notification at all.
        for (int index = 0; index < target.Count; index++)
        {
            WidgetItem desired = target[index];
            if (index < _stackPopoverItems.Count &&
                IsSameStackPopoverItem(_stackPopoverItems[index], desired))
            {
                if (!ReferenceEquals(_stackPopoverItems[index], desired))
                {
                    _stackPopoverItems[index] = desired;
                }

                continue;
            }

            int existingIndex = FindStackPopoverItemIndex(
                desired,
                index + 1);
            if (existingIndex >= 0)
            {
                _stackPopoverItems.Move(existingIndex, index);
            }
            else if (index < _stackPopoverItems.Count)
            {
                _stackPopoverItems[index] = desired;
            }
            else
            {
                _stackPopoverItems.Add(desired);
            }
        }

        while (_stackPopoverItems.Count > target.Count)
        {
            _stackPopoverItems.RemoveAt(_stackPopoverItems.Count - 1);
        }
    }

    private int FindStackPopoverItemIndex(
        WidgetItem desired,
        int startIndex)
    {
        for (int index = startIndex; index < _stackPopoverItems.Count; index++)
        {
            if (IsSameStackPopoverItem(_stackPopoverItems[index], desired))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSameStackPopoverItem(
        WidgetItem left,
        WidgetItem right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.Path) &&
            string.Equals(
                left.Path,
                right.Path,
                StringComparison.OrdinalIgnoreCase);
    }

    private void DetachStackPopoverItemSurfaces(ListViewBase view)
    {
        DetachStackPopoverItemSurfaces((DependencyObject)view);
        foreach (object item in view.Items)
        {
            if (view.ContainerFromItem(item) is DependencyObject container)
            {
                DetachStackPopoverItemSurfaces(container);
            }
        }
    }

    private void DetachStackPopoverItemSurfaces(DependencyObject root)
    {
        if (root is SelectorItem selectorItem)
        {
            selectorItem.DragStarting -= Items_DragStarting;
            selectorItem.DropCompleted -= Items_DropCompleted;
        }

        if (root is FileItemSurface surface)
        {
            surface.VisualStateChanged -= ItemSurface_VisualStateChanged;
            surface.DataContextChanged -= ItemSurface_DataContextChanged;
            surface.LayoutContext = null;
            surface.PrepareForContainerDetach();
            _itemSurfaces.Remove(surface.InteractiveBorder);
            if (ReferenceEquals(surface.InteractiveBorder, _folderDropTarget))
            {
                _folderDropTarget = null;
                _folderDropVisualActive = false;
            }
            if (ReferenceEquals(surface.InteractiveBorder, _launchDropTarget))
            {
                _launchDropTarget = null;
            }
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DetachStackPopoverItemSurfaces(
                VisualTreeHelper.GetChild(root, index));
        }
    }

    private void ClaimSingletonStackPopoverLease()
    {
        if (s_leasedStackPopoverOwner?.TryGetTarget(out FileSurfaceContent? previous) == true &&
            !ReferenceEquals(previous, this))
        {
            previous.EvictParkedStackPopoverFromSingletonLease();
        }

        s_leasedStackPopoverOwner = new WeakReference<FileSurfaceContent>(this);
    }

    private void EvictParkedStackPopoverFromSingletonLease()
    {
        if (_isDisposed ||
            _stackPopoverHostWindow is null ||
            IsStackPopoverBlockingSurfaceOpen)
        {
            return;
        }

        ReleaseStackPopover();
    }

    private void ArmStackPopoverIdleReleaseTimer()
    {
        if (_isDisposed || _stackPopoverHostWindow is null)
        {
            return;
        }

        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        if (_stackPopoverIdleReleaseTimer is null)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = StackPopoverIdleReleaseDelay;
            timer.Tick += StackPopoverIdleReleaseTimer_Tick;
            _stackPopoverIdleReleaseTimer = timer;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _stackPopoverIdleReleaseTimer.Stop();
            _stackPopoverIdleReleaseTimer.Interval = StackPopoverIdleReleaseDelay;
        }

        _stackPopoverIdleReleaseTimer.Start();
    }

    private void CancelStackPopoverIdleReleaseTimer()
    {
        _stackPopoverIdleReleaseTimer?.Stop();
    }

    private void DisposeStackPopoverIdleReleaseTimer()
    {
        if (_stackPopoverIdleReleaseTimer is not { } timer)
        {
            return;
        }

        _stackPopoverIdleReleaseTimer = null;
        timer.Stop();
        timer.Tick -= StackPopoverIdleReleaseTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void StackPopoverIdleReleaseTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_isDisposed ||
            _stackPopoverHostWindow is null ||
            IsStackPopoverBlockingSurfaceOpen)
        {
            return;
        }

        App.LogVerbose(
            $"[FileStack] Releasing idle parked popover widget={WidgetId}");
        ReleaseStackPopover();
    }

    private void ArmStackPopoverRevealFallbackTimer(
        StackPopoverHostWindow host,
        long generation,
        string stackKey)
    {
        if (_isDisposed || DispatcherQueue is not { } queue)
        {
            return;
        }

        if (_stackPopoverRevealFallbackTimer is null)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = StackPopoverRevealFallbackDelay;
            timer.Tick += StackPopoverRevealFallbackTimer_Tick;
            _stackPopoverRevealFallbackTimer = timer;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _stackPopoverRevealFallbackTimer.Stop();
            _stackPopoverRevealFallbackTimer.Interval = StackPopoverRevealFallbackDelay;
        }

        _stackPopoverRevealFallbackTimer.Start();
    }

    private void DisposeStackPopoverRevealFallbackTimer()
    {
        if (_stackPopoverRevealFallbackTimer is not { } timer)
        {
            return;
        }

        _stackPopoverRevealFallbackTimer = null;
        timer.Stop();
        timer.Tick -= StackPopoverRevealFallbackTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void StackPopoverRevealFallbackTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_stackPopoverHostWindow is not { } host ||
            _pendingStackPopoverKey is not { } stackKey)
        {
            CancelStackPopoverReveal();
            return;
        }

        long generation = _stackPopoverShowGeneration;
        if (!CanCompleteStackPopoverReveal(host, generation, stackKey))
        {
            CancelStackPopoverReveal();
            return;
        }

        App.LogVerbose(
            $"[FileStack] Popover content commit fallback fired widget={WidgetId} " +
            $"stack={stackKey} frames={_stackPopoverRevealFrameCount}");
        CompleteStackPopoverReveal(host, generation, stackKey);
    }

    private void RebuildStackPopoverContentForHost(
        StackPopoverHostWindow host,
        WidgetStackItem currentStack,
        StackPopoverLayout layout)
    {
        DetachStackPopoverContentTree();
        _stackPopoverItems.Clear();
        ListViewBase itemsView = CreateStackPopoverItemsView(layout);
        Border surface = CreateStackPopoverSurface(
            currentStack,
            itemsView,
            layout);
        host.SetContent(surface);
        _stackPopoverItemsView = itemsView;
        _stackPopoverSurface = surface;
        _stackPopoverIsListMode = ViewModel.IsListMode;
        EnsureStackPopoverNativeDropBridge(host);
    }

    private void DetachStackPopoverContentTree()
    {
        ReleaseStackPopoverItemRenameEditor();
        if (_stackPopoverTitleHost is not null)
        {
            _stackPopoverTitleHost.DoubleTapped -=
                StackPopoverTitle_DoubleTapped;
        }
        if (_stackPopoverTitleEditor is not null)
        {
            _stackPopoverTitleEditor.Loaded -=
                StackPopoverTitleEditor_Loaded;
            _stackPopoverTitleEditor.KeyDown -=
                StackPopoverTitleEditor_KeyDown;
            _stackPopoverTitleEditor.LostFocus -=
                StackPopoverTitleEditor_LostFocus;
        }
        if (_stackPopoverSurface is { } surface)
        {
            surface.DragOver -= StackSurface_DragOver;
            surface.DragLeave -= StackSurface_DragLeave;
            surface.Drop -= StackSurface_Drop;
            surface.DataContext = null;
        }

        if (_stackPopoverItemsView is { } view)
        {
            DetachStackPopoverItemSurfaces(view);
            view.Loaded -= StackPopoverItemsView_Loaded;
            view.ItemClick -= Items_ItemClick;
            view.ContainerContentChanging -= ItemsView_ContainerContentChanging;
            view.DragStarting -= Items_DragStarting;
            view.DropCompleted -= Items_DropCompleted;
            view.DragOver -= StackPopoverItems_DragOver;
            view.DragLeave -= StackPopoverItems_DragLeave;
            view.Drop -= StackPopoverItems_Drop;
            view.DoubleTapped -= Items_DoubleTapped;
            view.KeyDown -= Root_KeyDown;
            view.RightTapped -= Items_RightTapped;
            view.SelectionChanged -= Items_SelectionChanged;
            view.CharacterReceived -= Root_CharacterReceived;
            if (_stackPopoverExpansionWheelHandler is not null)
            {
                view.RemoveHandler(
                    UIElement.PointerWheelChangedEvent,
                    _stackPopoverExpansionWheelHandler);
            }
            if (_stackPopoverPreviewKeyHandler is not null)
            {
                view.RemoveHandler(
                    UIElement.PreviewKeyDownEvent,
                    _stackPopoverPreviewKeyHandler);
            }
            UnregisterScrollBarActivityTracking(view);
            view.ItemsSource = null;
        }

        if (_stackPopoverSelectionHost is { } selectionHost)
        {
            if (_stackPopoverSelectionPointerPressedHandler is not null)
            {
                selectionHost.RemoveHandler(
                    UIElement.PointerPressedEvent,
                    _stackPopoverSelectionPointerPressedHandler);
            }
            if (_stackPopoverSelectionPointerMovedHandler is not null)
            {
                selectionHost.RemoveHandler(
                    UIElement.PointerMovedEvent,
                    _stackPopoverSelectionPointerMovedHandler);
            }
            if (_stackPopoverSelectionPointerReleasedHandler is not null)
            {
                selectionHost.RemoveHandler(
                    UIElement.PointerReleasedEvent,
                    _stackPopoverSelectionPointerReleasedHandler);
            }
            if (_stackPopoverSelectionPointerCaptureLostHandler is not null)
            {
                selectionHost.RemoveHandler(
                    UIElement.PointerCaptureLostEvent,
                    _stackPopoverSelectionPointerCaptureLostHandler);
            }
        }

        if (_stackPopoverSurface?.Child is Grid content &&
            _stackPopoverSurfacePointerPressedHandler is not null)
        {
            content.RemoveHandler(
                UIElement.PointerPressedEvent,
                _stackPopoverSurfacePointerPressedHandler);
        }

        if (_stackPopoverCloseButton is not null)
        {
            _stackPopoverCloseButton.Click -= StackPopoverCloseButton_Click;
        }

        ResetBoxSelectionState();
        _stackPopoverAppearanceSignature = 0;
        _stackPopoverItemsView = null;
        _stackPopoverSurface = null;
        _stackPopoverTitleHost = null;
        _stackPopoverTitleText = null;
        _stackPopoverCloseButton = null;
        _stackPopoverTitleEditor = null;
        _stackPopoverItemRenameEditor = null;
        _stackPopoverEmptyText = null;
        _stackPopoverReorderOverlay = null;
        _stackPopoverReorderIndicator = null;
        _stackPopoverSelectionOverlay = null;
        _stackPopoverSelectionRectangle = null;
        ReleaseStackPopoverNameExpansionOverlay();
        _stackPopoverSelectionHost = null;
        _stackPopoverReorderInsertionIndex = -1;
        _stackPopoverPreviewKeyHandler = null;
        _stackPopoverExpansionWheelHandler = null;
        _stackPopoverSelectionPointerPressedHandler = null;
        _stackPopoverSelectionPointerMovedHandler = null;
        _stackPopoverSelectionPointerReleasedHandler = null;
        _stackPopoverSelectionPointerCaptureLostHandler = null;
        _stackPopoverSurfacePointerPressedHandler = null;
        _stackPopoverTitleEditing = false;
        _stackPopoverTitleCommitInProgress = false;
        _stackPopoverTitleCancelling = false;
        _stackPopoverTitleOriginalName = null;
        _stackPopoverIconContainerStyleSignature = 0;
    }

    private void ReleaseStackPopover()
    {
        // A popover member drag can outlive the popover: once the events are
        // detached below, DropCompleted can never arrive and the drag-session
        // fields would stay armed until the next gesture. Complete the
        // session first so the in-flight drop's observation, suppression and
        // reorder cleanup still run.
        DisposeStackPopoverIdleReleaseTimer();
        DisposeStackPopoverRevealFallbackTimer();
        DisposeStackSpringLoadAndDismissTimers();
        _stackPopoverNativeDropBridge = null;
        _stackPopoverNativeDragOver = false;
        _stackPopoverOpenedBySpringLoad = false;
        _stackPopoverRevealWithoutActivation = false;
        if (s_leasedStackPopoverOwner?.TryGetTarget(out FileSurfaceContent? owner) == true &&
            ReferenceEquals(owner, this))
        {
            s_leasedStackPopoverOwner = null;
        }

        if (_sourceDragSession.ReleaseRecoveryPending || _stackPopoverDragActive)
        {
            CompleteDragItemsSession(
                DataPackageOperation.None,
                _stackPopoverDragActive,
                eventItems: []);
        }

        CancelStackPopoverReveal();
        DetachStackPopoverContentTree();

        // The collection is intentionally retained during ordinary light
        // dismisses, but a lifecycle release must drop every member reference
        // before the cached native tree is detached.
        _stackPopoverItems.Clear();
        _stackPopoverItemsStackKey = null;

        if (_stackPopoverHostWindow is { } releasingHost)
        {
            releasingHost.DeactivatedByOutsideClick -=
                StackPopoverHost_DeactivatedByOutsideClick;
            releasingHost.EscapeRequested -=
                StackPopoverHost_EscapeRequested;
            releasingHost.UndoRequested -=
                StackPopoverHost_UndoRequested;
            releasingHost.Destroy();
        }
        _stackPopoverHostWindow = null;
        _stackPopoverLayout = null;
        _stackPopoverMembers = [];
        _stackPopoverKey = null;
        ViewModel.SetOpenPopoverStackKey(null);
        _stackPopoverPopupOpen = false;
        _stackPopoverPopupClosing = false;
        _stackPopoverContextMenuOpen = false;
        _stackPopoverSystemContextMenuOpen = false;
        _stackPopoverDragActive = false;
        _stackPopoverCleanupPending = false;
        _stackPopoverCleanupRestoreOwnerForeground = true;
        _lightDismissedByOwnTileClickKey = null;
        _pendingStackPopoverKey = null;
        _stackPopoverLayoutRefreshQueued = false;
        UpdateSelectionCommandBar();
        UpdateItemSurfaceVisuals();
    }

    private void StackPopoverSelectionHost_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is UIElement pointerSurface &&
            _stackPopoverItemsView is { } listView)
        {
            HandleItemsPointerPressed(listView, pointerSurface, e);
        }
    }

    private void StackPopoverSelectionHost_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is UIElement pointerSurface &&
            _stackPopoverItemsView is { } listView)
        {
            HandleItemsPointerMoved(listView, pointerSurface, e);
        }
    }

    private void StackPopoverSelectionHost_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is UIElement pointerSurface &&
            _stackPopoverItemsView is { } listView)
        {
            HandleItemsPointerReleased(listView, pointerSurface, e);
        }
    }

    private void StackPopoverSelectionHost_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        HandleElevatedPointerCaptureLost(e.Pointer.PointerId);
        if (_stackPopoverItemsView is { } listView)
        {
            HandleItemsPointerCaptureLost(listView);
        }
    }

    private void CompleteStackPopoverContextMenu()
    {
        _stackPopoverContextMenuOpen = false;
        if (_stackPopoverCleanupPending &&
            !_stackPopoverDragActive &&
            !_stackPopoverTitleEditing &&
            !IsStackPopoverItemRenameEditing &&
            _stackPopoverHostWindow is not null)
        {
            bool restoreOwnerForeground = _stackPopoverCleanupRestoreOwnerForeground;
            HideStackPopoverForReuse(restoreOwnerForeground);
            if (!restoreOwnerForeground)
            {
                App.Current?.WidgetManager?.NotifyTransientPopupDismissedFromWidget(_hostWindowHandle);
            }
        }

        if (_stackPopoverPopupOpen)
        {
            QueueStackPopoverReconciliation();
        }

        SchedulePopoverDismissIfDeactivated();
    }

    /// <summary>
    /// The popover's light dismiss rides a fresh Activated→Deactivated
    /// transition, and the menu-round flag deliberately blocks that
    /// transition while a context menu is open. An outside click that
    /// dismisses the menu fires Deactivated BEFORE the flyout's Closed
    /// resets the flag, so the one transition the dismiss needed is
    /// consumed and no new one follows — the popover is already
    /// deactivated. Re-evaluate once activation settles after every menu
    /// round: when the host no longer owns the foreground, run the same
    /// dismiss path the missed transition would have.
    /// </summary>
    private void SchedulePopoverDismissIfDeactivated()
    {
        if (_stackPopoverHostWindow is not { } host)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_stackPopoverPopupOpen &&
                !_stackPopoverContextMenuOpen &&
                !_stackPopoverSystemContextMenuOpen &&
                !_stackPopoverDragActive &&
                Win32Helper.GetForegroundWindow() != host.WindowHandle)
            {
                StackPopoverHost_DeactivatedByOutsideClick();
            }
        });
    }

    private void CompleteStackPopoverDrag()
    {
        CompleteStackPopoverDragCore();
        NotifyOutboundDragCompletedIfIdle();
    }

    private void StartStackPopoverOutboundDragPassthroughWatch()
    {
        _stackPopoverDragForeignWindowActivated = false;
        if (_stackPopoverOutboundPassthroughTimer is null)
        {
            _stackPopoverOutboundPassthroughTimer = DispatcherQueue.CreateTimer();
            _stackPopoverOutboundPassthroughTimer.Interval = TimeSpan.FromMilliseconds(45);
            _stackPopoverOutboundPassthroughTimer.IsRepeating = true;
            _stackPopoverOutboundPassthroughTimer.Tick += StackPopoverOutboundPassthroughTimer_Tick;
        }

        if (!_stackPopoverOutboundPassthroughTimer.IsRunning)
        {
            _stackPopoverOutboundPassthroughTimer.Start();
        }
    }

    private void StopStackPopoverOutboundDragPassthroughWatch()
    {
        _stackPopoverOutboundPassthroughTimer?.Stop();
        _stackPopoverDragForeignWindowActivated = false;
        _stackPopoverHostWindow?.SetExternalDragPassthrough(false);
    }

    private void StackPopoverOutboundPassthroughTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed ||
            !_stackPopoverDragActive ||
            !_stackPopoverPopupOpen ||
            _stackPopoverHostWindow is not { IsVisible: true } host ||
            host.WindowHandle == IntPtr.Zero)
        {
            StopStackPopoverOutboundDragPassthroughWatch();
            return;
        }

        IntPtr fg = Win32Helper.GetForegroundWindow();
        if (fg != IntPtr.Zero &&
            fg != host.WindowHandle &&
            fg != _hostWindowHandle &&
            !TransientInputFocusGuard.IsCurrentProcessPopupBridge(fg) &&
            !Win32Helper.IsWindowInCurrentProcess(fg))
        {
            _stackPopoverDragForeignWindowActivated = true;
        }

        bool insidePopover = IsCursorInsideOpenStackPopoverBounds(marginPixels: 0);
        bool passthrough = !insidePopover || _stackPopoverDragForeignWindowActivated;
        host.SetExternalDragPassthrough(passthrough);
    }

    private void CompleteStackPopoverDragCore()
    {
        StopStackPopoverOutboundDragPassthroughWatch();
        HideStackPopoverReorderIndicator();
        _stackPopoverDragActive = false;
        if (_stackPopoverCleanupPending &&
            !_stackPopoverContextMenuOpen &&
            !_stackPopoverTitleEditing &&
            !IsStackPopoverItemRenameEditing &&
            _stackPopoverHostWindow is not null)
        {
            bool restoreOwnerForeground = _stackPopoverCleanupRestoreOwnerForeground;
            HideStackPopoverForReuse(restoreOwnerForeground);
            if (!restoreOwnerForeground)
            {
                App.Current?.WidgetManager?.NotifyTransientPopupDismissedFromWidget(_hostWindowHandle);
            }

            return;
        }

        if (_stackPopoverPopupOpen)
        {
            QueueStackPopoverReconciliation();
        }

        SchedulePopoverDismissIfDeactivated();
    }


    private bool TryCompleteReleasedStackPopoverReorder(
        IReadOnlyCollection<string> sourcePaths,
        bool handledAsStackMembership)
    {
        ListViewBase? view = _stackPopoverItemsView;
        StackPopoverHostWindow? host = _stackPopoverHostWindow;
        bool pointerInsideItems =
            view is not null &&
            host is { IsVisible: true } &&
            Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) &&
            TryGetScreenPointInElement(
                view,
                host.WindowHandle,
                cursor.X,
                cursor.Y,
                out _);
        if (!ShouldCommitReleasedStackPopoverReorder(
                _stackPopoverDragActive,
                _stackPopoverReorderInsertionIndex,
                pointerInsideItems,
                sourcePaths.Count,
                handledAsStackMembership) ||
            view is null ||
            _stackPopoverKey is not { Length: > 0 } stackKey ||
            !TryGetStackPopoverDragItems(
                stackKey,
                sourcePaths,
                out WidgetStackItem sourceStack,
                out WidgetItem[] items))
        {
            return false;
        }

        int insertionIndex = ResolveStackPopoverMemberInsertionIndex(
            view,
            _stackPopoverReorderInsertionIndex);
        bool reordered = ReorderStackPopoverMembers(
            sourceStack,
            items,
            insertionIndex);
        HideStackPopoverReorderIndicator();
        if (!reordered)
        {
            QueueStackPopoverReconciliation();
        }

        App.Log(
            $"[FileStack] Recovered internal reorder after pointer release " +
            $"widget={WidgetId} reordered={reordered}");
        return true;
    }

    internal static bool ShouldCommitReleasedStackPopoverReorder(
        bool dragActive,
        int insertionIndex,
        bool pointerInsideItems,
        int sourcePathCount,
        bool handledAsStackMembership) =>
        dragActive &&
        insertionIndex >= 0 &&
        pointerInsideItems &&
        sourcePathCount > 0 &&
        !handledAsStackMembership;

    private bool IsItemInStackPopover(WidgetItem item) =>
        IsStackPopoverInteractionActive &&
        _stackPopoverItemsView?.Items
            .OfType<WidgetItem>()
            .Any(candidate => ReferenceEquals(candidate, item)) == true;

    private void StackPopoverItems_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (sender is not ListViewBase view)
        {
            return;
        }

        BeginStackPopoverDragAutoScroll();
        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!TryGetCurrentStackPopoverDrag(
                payload,
                out _,
                out _))
        {
            if (payload.HasSurfacePathData &&
                !payload.IsInternalReorder &&
                !payload.IsStackPopoverMemberDrag)
            {
                e.Handled = true;
                if (!payload.IsDeskBoxFileDrag)
                {
                    SuppressExternalDragOperationBadge(e);
                }

                FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                    payload.DataView,
                    e.AllowedOperations,
                    destinationFolderPath: ViewModel.CurrentFolderPath);
                e.AcceptedOperation = DeskBoxDragData.ResolveFileDragFeedbackOperation(
                    e.DataView,
                    ToDataPackageOperation(resolvedIntent),
                    e.AllowedOperations);
                string popoverTargetName = _stackPopoverKey is { } key &&
                    ViewModel.FindStackByKey(key) is { } targetStack
                        ? targetStack.Name
                        : string.Empty;
                string popoverCaption = resolvedIntent == FileDropIntent.Shortcut
                    ? FormatDropCaption(resolvedIntent, popoverTargetName)
                    : _localizationService.Format(
                        "Widget.Stack.DragCaption.Add",
                        popoverTargetName);
                if (payload.IsDeskBoxFileDrag)
                {
                    e.DragUIOverride.IsGlyphVisible =
                        e.AcceptedOperation !=
                        Windows.ApplicationModel.DataTransfer
                            .DataPackageOperation.None;
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.Caption = popoverCaption;
                }
                else if (!HasActivePopoverShellDragWindow())
                {
                    ApplyExternalDragOperationVisuals(
                        e,
                        e.AcceptedOperation,
                        popoverCaption);
                }

                Windows.Foundation.Point externalPosition =
                    e.GetPosition(view);
                UpdateStackPopoverExternalDropPreview(
                    payload,
                    view,
                    externalPosition);
                return;
            }

            if (payload.HasSurfacePathData &&
                payload.IsInternalReorder &&
                !payload.IsStackPopoverMemberDrag)
            {
                UpdateStackPopoverExternalDropPreview(
                    payload,
                    view,
                    e.GetPosition(view));
                return;
            }

            HideStackPopoverReorderIndicator();
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = ResolveInternalArrangementFeedbackOperation(
            e.AllowedOperations,
            e.DataView.RequestedOperation);
        TraceInternalDragDecision("stack-reorder", payload, e);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = T("Widget.DragCaption.Reorder");
        Windows.Foundation.Point position = e.GetPosition(view);
        _stackPopoverReorderInsertionIndex =
            ReorderDropIndexCalculator.Compute(
                view,
                position,
                _stackPopoverReorderInsertionIndex);
        UpdateStackPopoverReorderIndicator(
            view,
            position,
            _stackPopoverReorderInsertionIndex);
    }

    private void UpdateStackPopoverExternalDropPreview(
        DragPayloadSnapshot payload,
        ListViewBase view,
        Windows.Foundation.Point position)
    {
        if (payload.Paths.Length > 0)
        {
            _externalDropPathHints = payload.Paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (_isDisposed ||
            _isImportBusy ||
            !payload.HasSurfacePathData ||
            view.Items.Count == 0 ||
            !ReorderDropIndexCalculator.IsPointerOverRealizedItem(
                view,
                position))
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        _stackPopoverReorderInsertionIndex =
            ReorderDropIndexCalculator.Compute(
                view,
                position,
                _stackPopoverReorderInsertionIndex);
        // The trailing area is intentionally an append/automatic destination,
        // not an explicit reorder position. Keep the existing stack drop
        // behavior there without showing a misleading line.
        if (_stackPopoverReorderInsertionIndex < 0 ||
            _stackPopoverReorderInsertionIndex >= view.Items.Count)
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        UpdateStackPopoverReorderIndicator(
            view,
            position,
            _stackPopoverReorderInsertionIndex);
    }

    private void StackPopoverItems_DragLeave(
        object sender,
        DragEventArgs e)
    {
        if (sender is not ListViewBase view)
        {
            return;
        }

        try
        {
            Windows.Foundation.Point point = e.GetPosition(view);
            if (point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= view.ActualWidth &&
                point.Y <= view.ActualHeight)
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // A closing popup no longer has a stable transform. Hiding the
            // marker is the only state transition required here.
        }

        HideStackPopoverReorderIndicator();
    }

    private void StackPopoverItems_Drop(
        object sender,
        DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        if (sender is not ListViewBase view)
        {
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        TraceTargetDropEntered("stack-popover", payload, e);
        if (!TryGetCurrentStackPopoverDrag(
                payload,
                out WidgetStackItem sourceStack,
                out WidgetItem[] items))
        {
            return;
        }

        e.Handled = true;
        _activeDragHandledAsStackMembership = true;
        int insertionIndex = ResolveStackPopoverMemberInsertionIndex(
            view,
            e.GetPosition(view));
        bool reordered = ReorderStackPopoverMembers(
            sourceStack,
            items,
            insertionIndex);
        e.AcceptedOperation = ResolveInternalArrangementCompletionOperation(
            e.AllowedOperations,
            e.DataView.RequestedOperation);
        HideStackPopoverReorderIndicator();
        PersistSurfaceReorder();
        ResetExternalDropPreview();
        ResetDragPayloadCache();
        if (!reordered)
        {
            QueueStackPopoverReconciliation();
        }
    }

    private bool TryGetCurrentStackPopoverDrag(
        DragPayloadSnapshot payload,
        out WidgetStackItem sourceStack,
        out WidgetItem[] items)
    {
        if (!TryGetStackPopoverDragItems(
                payload,
                out sourceStack,
                out items) ||
            !string.Equals(
                sourceStack.StackKey,
                _stackPopoverKey,
                StringComparison.Ordinal))
        {
            sourceStack = null!;
            items = [];
            return false;
        }

        return true;
    }

    private void UpdateStackPopoverKeyAfterReorder(
        IReadOnlyList<WidgetItem> reorderedItems)
    {
        UpdateStackPopoverKeyFromMemberPaths(
            reorderedItems.Select(item => item.Path));
    }

    private bool ReorderStackPopoverMembers(
        WidgetStackItem sourceStack,
        IReadOnlyList<WidgetItem> items,
        int insertionIndex)
    {
        bool reordered = false;
        string resolvedStackKey = sourceStack.StackKey;
        ApplyStackProjectionChange(() =>
            reordered = ViewModel.MoveStackMembersForReorder(
                sourceStack.StackKey,
                items,
                insertionIndex,
                out resolvedStackKey));
        if (reordered)
        {
            _stackPopoverKey = resolvedStackKey;
            UpdateStackPopoverKeyAfterReorder(items);
            ScheduleStackPopoverReorderSelectionRestore(items);
        }

        return reordered;
    }

    // The projection-change wrapper around a popover reorder clears the
    // popover selection, including a deferred reset that runs after
    // container recycling. The restore therefore hops twice through the
    // low-priority queue: the first hop queues behind that reset's
    // enqueue, and the second runs after the reset itself, so the same
    // members stay selected for a follow-up drag.
    private void ScheduleStackPopoverReorderSelectionRestore(
        IReadOnlyList<WidgetItem> items)
    {
        string[] paths = items
            .Select(item => Path.GetFullPath(item.Path))
            .ToArray();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (_isDisposed ||
                        _stackPopoverItemsView is not { } view ||
                        !IsStackPopoverInteractionActive)
                    {
                        return;
                    }

                    HashSet<string> pathSet = paths
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    List<WidgetItem> restored = view.Items
                        .OfType<WidgetItem>()
                        .Where(item => item is not WidgetStackItem &&
                            pathSet.Contains(Path.GetFullPath(item.Path)))
                        .ToList();
                    if (restored.Count == 0)
                    {
                        return;
                    }

                    view.SelectedItems.Clear();
                    foreach (WidgetItem item in restored)
                    {
                        view.SelectedItems.Add(item);
                    }

                    UpdateSelectionCommandBar();
                }));
    }

    private int ResolveStackPopoverMemberInsertionIndex(
        ListViewBase view,
        Windows.Foundation.Point position)
    {
        int visibleInsertionIndex = ReorderDropIndexCalculator.Compute(
            view,
            position,
            _stackPopoverReorderInsertionIndex);
        return ResolveStackPopoverMemberInsertionIndex(
            view,
            visibleInsertionIndex);
    }

    private int ResolveStackPopoverMemberInsertionIndex(
        ListViewBase view,
        int visibleInsertionIndex)
    {
        WidgetItem[] visibleItems = view.Items
            .OfType<WidgetItem>()
            .ToArray();
        if (visibleItems.Length == 0)
        {
            return 0;
        }

        WidgetItem reference = visibleInsertionIndex < visibleItems.Length
            ? visibleItems[visibleInsertionIndex]
            : visibleItems[^1];
        int fullIndex = Array.FindIndex(
            _stackPopoverMembers,
            candidate => ReferenceEquals(candidate, reference));
        if (fullIndex < 0)
        {
            fullIndex = Array.FindIndex(
                _stackPopoverMembers,
                candidate => string.Equals(
                    candidate.Path,
                    reference.Path,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (fullIndex < 0)
        {
            return Math.Clamp(
                visibleInsertionIndex,
                0,
                _stackPopoverMembers.Length);
        }

        return visibleInsertionIndex < visibleItems.Length
            ? fullIndex
            : fullIndex + 1;
    }

    private void UpdateStackPopoverReorderIndicator(
        ListViewBase view,
        Windows.Foundation.Point position,
        int insertionIndex)
    {
        if (_stackPopoverReorderOverlay is not { } overlay ||
            _stackPopoverReorderIndicator is not { } indicator ||
            !ReorderDropIndexCalculator.TryGetInsertionIndicatorPlacement(
                view,
                overlay,
                insertionIndex,
                position,
                out ReorderInsertionIndicatorPlacement placement))
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        const double LineThickness = 2;
        indicator.Width = placement.IsVertical
            ? LineThickness
            : placement.Bounds.Width;
        indicator.Height = placement.IsVertical
            ? placement.Bounds.Height
            : LineThickness;
        Canvas.SetLeft(
            indicator,
            placement.IsVertical
                ? placement.Bounds.X +
                    ((placement.Bounds.Width - LineThickness) / 2)
                : placement.Bounds.X);
        Canvas.SetTop(
            indicator,
            placement.IsVertical
                ? placement.Bounds.Y
                : placement.Bounds.Y +
                    ((placement.Bounds.Height - LineThickness) / 2));
        indicator.Visibility = Visibility.Visible;
    }

    private void HideStackPopoverReorderIndicator()
    {
        _stackPopoverReorderInsertionIndex = -1;
        if (_stackPopoverReorderIndicator is { } indicator)
        {
            indicator.Visibility = Visibility.Collapsed;
            indicator.Width = 0;
            indicator.Height = 0;
        }
    }

    internal void NotifyItemsMovedOutFromExternalWidget()
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateEmptyState();
        if (_stackPopoverHostWindow is not null)
        {
            QueueStackPopoverReconciliation();
        }
    }

    private void QueueStackPopoverReconciliation(
        string? expectedStackKey = null,
        IReadOnlyList<string>? memberAnchorPaths = null)
    {
        if (_stackPopoverHostWindow is not { } expectedPopup ||
            expectedStackKey is not null &&
            !string.Equals(
                _stackPopoverKey,
                expectedStackKey,
                StringComparison.Ordinal))
        {
            return;
        }

        string[] anchors = memberAnchorPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_stackPopoverHostWindow, expectedPopup))
            {
                return;
            }

            if (anchors.Length > 0)
            {
                UpdateStackPopoverKeyFromMemberPaths(anchors);
            }
            ReconcileStackPopover();

            // The view-model projection is synchronous, but the ItemsControl
            // can realize the replacement stack container one layout pass
            // later. Reconcile once more so centering follows the new anchor.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (ReferenceEquals(
                            _stackPopoverHostWindow,
                            expectedPopup))
                    {
                        ReconcileStackPopover();
                    }
                });
        });
    }

    private void UpdateStackPopoverKeyFromMemberPaths(
        IEnumerable<string> memberPaths)
    {
        HashSet<string> anchors = memberPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (anchors.Count == 0)
        {
            return;
        }

        WidgetStackItem? currentStack = ViewModel.VisibleItems
            .OfType<WidgetStackItem>()
            .FirstOrDefault(stack => stack.Members.Any(member =>
                anchors.Contains(member.Path)));
        if (currentStack is not null)
        {
            _stackPopoverKey = currentStack.StackKey;
        }
    }

    private void ReconcileStackPopover()
    {
        if (_stackPopoverHostWindow is null)
        {
            return;
        }

        if (_stackPopoverKey is not { } stackKey)
        {
            if (!string.IsNullOrEmpty(_stackPopoverItemsStackKey) &&
                ViewModel.FindStackByKey(_stackPopoverItemsStackKey) is null &&
                _stackPopoverItems.Count > 0)
            {
                // A parked stack was dissolved while the popover was closed.
                // Drop stale member references immediately while retaining
                // _stackPopoverItemsStackKey so the next open treats the host
                // as switchingStacks and rebuilds its inner view cleanly.
                _stackPopoverItems.Clear();
            }

            return;
        }

        WidgetStackItem? stack = ViewModel.FindStackByKey(stackKey);
        if (!ViewModel.UsesStackPopover ||
            stack is null ||
            stack.Members.Count == 0)
        {
            CloseStackPopover(
                releaseImmediately: !_stackPopoverDragActive &&
                    !_stackPopoverContextMenuOpen);
            return;
        }

        _stackPopoverMembers = stack.Members.ToArray();
        if (_stackPopoverSurface is not null)
        {
            _stackPopoverSurface.DataContext = stack;
            AutomationProperties.SetName(
                _stackPopoverSurface,
                stack.Name);
        }
        if (!_stackPopoverTitleEditing &&
            _stackPopoverTitleText is { } title)
        {
            title.Text = stack.Name;
            AutomationProperties.SetName(title, stack.Name);
        }
        if (_stackPopoverItemsView is { } itemsView)
        {
            ReconcileStackPopoverItems(_stackPopoverMembers);
            UpdateStackPopoverScrollPolicy(_stackPopoverMembers.Length);
        }
        ApplyStackPopoverLayout(stack);
        UpdateStackFolderPreviewModes();
    }

    private void ApplyStackPopoverLayout(WidgetStackItem stack)
    {
        if (_stackPopoverHostWindow is null ||
            _stackPopoverItemsView is not { } itemsView ||
            _stackPopoverSurface is not { } surface)
        {
            return;
        }

        StackPopoverLayout layout = CalculateStackPopoverLayout(
            stack.Members.Count);
        _stackPopoverLayout = layout;
        UpdateStackPopoverIconItemContainerStyle(itemsView, layout);
        ConfigureStackPopoverItemsPanel(itemsView, layout);
        itemsView.Width = layout.ItemsWidth;
        itemsView.MaxHeight = layout.ItemsHeight;
        surface.Width = layout.Width;
        surface.Height = layout.Height;
        double titleMaxWidth = Math.Max(
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            layout.Width -
                (StackPopoverLayoutCalculator.SurfacePadding * 2) -
                StackPopoverLayoutCalculator.TitleTrailingButtonWidth);
        if (_stackPopoverTitleText is { } title)
        {
            title.MinWidth = Math.Min(
                StackPopoverLayoutCalculator.TitleMinimumWidth,
                titleMaxWidth);
            title.MaxWidth = titleMaxWidth;
        }
        if (_stackPopoverTitleEditor is { } titleEditor)
        {
            titleEditor.MaxWidth = titleMaxWidth;
            if (_stackPopoverTitleEditing)
            {
                titleEditor.Width = Math.Clamp(
                    titleEditor.Width,
                    StackPopoverLayoutCalculator.TitleMinimumWidth,
                    titleMaxWidth);
            }
        }
        UpdateStackPopoverScrollPolicy(itemsView.Items.Count);

        if (FindStackSurface(stack.StackKey) is not { } anchor)
        {
            return;
        }

        StackPopoverPosition position = ResolveStackPopoverPosition(
            anchor,
            layout.Width,
            layout.Height);
        double scale = Math.Max(
            0.5,
            Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                XamlRoot));
        if (_hostWindowHandle != IntPtr.Zero &&
            Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT hostBounds) &&
            _stackPopoverHostWindow.IsVisible)
        {
            int left = hostBounds.Left + (int)Math.Round(position.Left * scale);
            int top = hostBounds.Top + (int)Math.Round(position.Top * scale);
            int probeX = left + (int)Math.Round((layout.Width * scale) / 2);
            int probeY = top + (int)Math.Round((layout.Height * scale) / 2);
            double targetScale = Math.Max(0.5, Win32Helper.GetDpiScaleForPoint(probeX, probeY));
            var bounds = new Windows.Graphics.RectInt32(
                left,
                top,
                StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                    layout.Width,
                    targetScale),
                StackPopoverPixelCalculator.ToCoveringPhysicalPixels(
                    layout.Height,
                    targetScale));
            _stackPopoverScreenBounds = bounds;
            _stackPopoverHostWindow.UpdateBounds(bounds);
        }
    }

    private (double Width, double Height) ResolveStackPopoverWorkArea()
    {
        double fallbackWidth = Math.Max(640, XamlRoot?.Size.Width ?? 0);
        double fallbackHeight = Math.Max(480, XamlRoot?.Size.Height ?? 0);
        if (_hostWindowHandle == IntPtr.Zero ||
            !Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT windowRect) ||
            !Win32Helper.TryGetMonitorWorkArea(
                windowRect.Left + (windowRect.Right - windowRect.Left) / 2,
                windowRect.Top + (windowRect.Bottom - windowRect.Top) / 2,
                out _,
                out Win32Helper.RECT workArea))
        {
            return (fallbackWidth, fallbackHeight);
        }

        double scale = Math.Max(
            0.5,
            Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                XamlRoot));
        return (
            Math.Max(180, (workArea.Right - workArea.Left) / scale),
            Math.Max(160, (workArea.Bottom - workArea.Top) / scale));
    }

    private StackPopoverPosition ResolveStackPopoverPosition(
        FrameworkElement anchor,
        double popoverWidth,
        double popoverHeight)
    {
        try
        {
            FrameworkElement iconAnchor =
                FindDescendantByTag(anchor, "StackPreviewHost") ?? anchor;
            UIElement coordinateRoot = XamlRoot?.Content ?? Root;
            Windows.Foundation.Point center = iconAnchor
                .TransformToVisual(coordinateRoot)
                .TransformPoint(new Windows.Foundation.Point(
                    iconAnchor.ActualWidth / 2,
                    iconAnchor.ActualHeight / 2));

            double workAreaLeft = 0;
            double workAreaTop = 0;
            double workAreaWidth = Math.Max(1, XamlRoot?.Size.Width ?? ActualWidth);
            double workAreaHeight = Math.Max(1, XamlRoot?.Size.Height ?? ActualHeight);
            if (_hostWindowHandle != IntPtr.Zero &&
                Win32Helper.GetWindowRect(
                    _hostWindowHandle,
                    out Win32Helper.RECT windowRect) &&
                Win32Helper.TryGetMonitorWorkArea(
                    windowRect.Left + (windowRect.Right - windowRect.Left) / 2,
                    windowRect.Top + (windowRect.Bottom - windowRect.Top) / 2,
                    out _,
                    out Win32Helper.RECT workArea))
            {
                double scale = Math.Max(
                    0.5,
                    Win32Helper.GetDpiScaleForWindow(
                        _hostWindowHandle,
                        XamlRoot));
                workAreaLeft = (workArea.Left - windowRect.Left) / scale;
                workAreaTop = (workArea.Top - windowRect.Top) / scale;
                workAreaWidth = (workArea.Right - workArea.Left) / scale;
                workAreaHeight = (workArea.Bottom - workArea.Top) / scale;
            }

            return StackPopoverPositionCalculator.Calculate(
                center.X,
                center.Y,
                popoverWidth,
                popoverHeight,
                workAreaLeft,
                workAreaTop,
                workAreaWidth,
                workAreaHeight);
        }
        catch (InvalidOperationException)
        {
            double width = Math.Max(1, XamlRoot?.Size.Width ?? ActualWidth);
            double height = Math.Max(1, XamlRoot?.Size.Height ?? ActualHeight);
            double left = Math.Max(
                0,
                (width - popoverWidth) / 2);
            double top = Math.Max(
                0,
                (height - popoverHeight) / 2);
            return new StackPopoverPosition(left, top, true, true);
        }
    }

    internal static bool ShouldDeferStackPopoverLightDismiss(
        bool mouseButtonDown,
        bool dragActive,
        bool nativeDragOver,
        long elapsedSinceDeactivateMs)
    {
        if (dragActive || nativeDragOver)
        {
            return true;
        }

        return mouseButtonDown &&
            elapsedSinceDeactivateMs >= 0 &&
            elapsedSinceDeactivateMs < StackPopoverLightDismissMaxDeferralMilliseconds;
    }

    internal static bool ShouldArmStackSpringLoad(
        bool usesStackPopover,
        bool isStackExpanded,
        bool isDraggingStackHeader,
        string? hoveredStackKey,
        string? openPopoverStackKey,
        string? sourceStackKey)
    {
        if (!usesStackPopover ||
            isStackExpanded ||
            isDraggingStackHeader ||
            string.IsNullOrWhiteSpace(hoveredStackKey))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(openPopoverStackKey) &&
            string.Equals(hoveredStackKey, openPopoverStackKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceStackKey) &&
            string.Equals(hoveredStackKey, sourceStackKey, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsAnyMouseButtonPressed() =>
        Win32Helper.IsKeyPressed(Windows.System.VirtualKey.LeftButton) ||
        Win32Helper.IsKeyPressed(Windows.System.VirtualKey.RightButton);

    private bool IsAnyPopoverTargetDragInFlight() =>
        ActiveDeskBoxDragRegistry.HasActiveDrag ||
        _elevatedDragSession is not null ||
        _stackPopoverDragActive ||
        _externalDropPathHints.Length > 0;

    private bool HasActivePopoverShellDragWindow() =>
        _stackPopoverNativeDropBridge?.HasActiveShellDragWindow == true;

    private bool IsCursorInsideOpenStackPopoverBounds(int marginPixels = 0)
    {
        if (!_stackPopoverPopupOpen ||
            _stackPopoverHostWindow is not { IsVisible: true } host ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT pt) ||
            !Win32Helper.GetWindowRect(host.WindowHandle, out Win32Helper.RECT rect))
        {
            return false;
        }

        return pt.X >= rect.Left - marginPixels &&
               pt.X <= rect.Right + marginPixels &&
               pt.Y >= rect.Top - marginPixels &&
               pt.Y <= rect.Bottom + marginPixels;
    }

    private void ArmDeferredStackPopoverDismissTimer(Win32Helper.POINT? originCursor)
    {
        _deferredStackPopoverDismissStartedTick = Environment.TickCount64;
        _deferredStackPopoverDismissOriginCursor = originCursor;
        _deferredStackPopoverDismissPointerMoved = false;
        if (_deferredStackPopoverDismissTimer is null)
        {
            _deferredStackPopoverDismissTimer = DispatcherQueue.CreateTimer();
            _deferredStackPopoverDismissTimer.Interval = TimeSpan.FromMilliseconds(35);
            _deferredStackPopoverDismissTimer.IsRepeating = true;
            _deferredStackPopoverDismissTimer.Tick += DeferredStackPopoverDismissTimer_Tick;
        }

        if (!_deferredStackPopoverDismissTimer.IsRunning)
        {
            _deferredStackPopoverDismissTimer.Start();
        }
    }

    private void CancelDeferredStackPopoverDismissTimer()
    {
        _deferredStackPopoverDismissTimer?.Stop();
        _deferredStackPopoverDismissOriginCursor = null;
        _deferredStackPopoverDismissPointerMoved = false;
    }

    private void DeferredStackPopoverDismissTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed || !_stackPopoverPopupOpen || _stackPopoverPopupClosing)
        {
            CancelDeferredStackPopoverDismissTimer();
            return;
        }

        if (_stackPopoverHostWindow is { } host &&
            Win32Helper.GetForegroundWindow() == host.WindowHandle)
        {
            CancelDeferredStackPopoverDismissTimer();
            return;
        }

        bool mouseDown = IsAnyMouseButtonPressed();
        if (mouseDown &&
            !_deferredStackPopoverDismissPointerMoved &&
            _deferredStackPopoverDismissOriginCursor is { } origin &&
            Win32Helper.GetCursorPos(out Win32Helper.POINT current))
        {
            int dx = Math.Abs(current.X - origin.X);
            int dy = Math.Abs(current.Y - origin.Y);
            if (dx >= 6 || dy >= 6)
            {
                _deferredStackPopoverDismissPointerMoved = true;
            }
        }

        long elapsed = Environment.TickCount64 - _deferredStackPopoverDismissStartedTick;
        bool externalDragMoving = mouseDown &&
            _deferredStackPopoverDismissPointerMoved &&
            elapsed < 15_000;
        bool dragActive = IsAnyPopoverTargetDragInFlight() || externalDragMoving;

        if (ShouldDeferStackPopoverLightDismiss(
                mouseDown,
                dragActive,
                _stackPopoverNativeDragOver,
                elapsed))
        {
            return;
        }

        CancelDeferredStackPopoverDismissTimer();
        if (IsCursorInsideOpenStackPopoverBounds(marginPixels: 4))
        {
            return;
        }

        _bypassStackPopoverLightDismissDeferral = true;
        try
        {
            StackPopoverHost_DeactivatedByOutsideClick();
        }
        finally
        {
            _bypassStackPopoverLightDismissDeferral = false;
        }
    }

    private void ObserveStackTileSpringLoad(
        WidgetStackItem? stack,
        bool isStackHeader,
        string? sourceStackKey)
    {
        if (_isDisposed ||
            _isImportBusy ||
            !ShouldArmStackSpringLoad(
                ViewModel.UsesStackPopover,
                stack?.IsExpanded ?? false,
                isStackHeader,
                stack?.StackKey,
                _stackPopoverPopupOpen ? _stackPopoverKey : null,
                sourceStackKey))
        {
            CancelStackSpringLoadTimer();
            return;
        }

        string targetKey = stack!.StackKey;
        if (string.Equals(_pendingSpringLoadStackKey, targetKey, StringComparison.Ordinal) &&
            _stackSpringLoadTimer is { IsRunning: true })
        {
            return;
        }

        _pendingSpringLoadStackKey = targetKey;
        if (_stackSpringLoadTimer is null)
        {
            _stackSpringLoadTimer = DispatcherQueue.CreateTimer();
            _stackSpringLoadTimer.Interval = TimeSpan.FromMilliseconds(StackSpringLoadDelayMilliseconds);
            _stackSpringLoadTimer.IsRepeating = false;
            _stackSpringLoadTimer.Tick += StackSpringLoadTimer_Tick;
        }

        _stackSpringLoadTimer.Stop();
        _stackSpringLoadTimer.Start();
    }

    private void CancelStackSpringLoadTimer()
    {
        _pendingSpringLoadStackKey = null;
        _stackSpringLoadTimer?.Stop();
    }

    private void StackSpringLoadTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        string? targetKey = _pendingSpringLoadStackKey;
        _pendingSpringLoadStackKey = null;
        sender.Stop();

        if (_isDisposed ||
            _isImportBusy ||
            string.IsNullOrWhiteSpace(targetKey) ||
            !ViewModel.UsesStackPopover ||
            !IsAnyMouseButtonPressed() ||
            ViewModel.FindStackByKey(targetKey) is not { Members.Count: > 0 } currentStack)
        {
            return;
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt) &&
            !IsScreenPointInsideStackSurface(targetKey, pt) &&
            (_stackMemberDropTarget?.DataContext is not WidgetStackItem memberStack ||
             !string.Equals(memberStack.StackKey, targetKey, StringComparison.Ordinal)))
        {
            return;
        }

        if (_stackPopoverPopupOpen &&
            !string.Equals(_stackPopoverKey, targetKey, StringComparison.Ordinal))
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }

        if (_stackPopoverPopupOpen)
        {
            return;
        }

        _stackPopoverRevealWithoutActivation = true;
        _stackPopoverOpenedBySpringLoad = true;
        ShowStackPopover(currentStack);
        StartSpringLoadedPopoverWatchTimer();
    }

    private void StartSpringLoadedPopoverWatchTimer()
    {
        _springLoadedPopoverOutsideSinceTick = 0;
        if (_springLoadedPopoverWatchTimer is null)
        {
            _springLoadedPopoverWatchTimer = DispatcherQueue.CreateTimer();
            _springLoadedPopoverWatchTimer.Interval = TimeSpan.FromMilliseconds(90);
            _springLoadedPopoverWatchTimer.IsRepeating = true;
            _springLoadedPopoverWatchTimer.Tick += SpringLoadedPopoverWatchTimer_Tick;
        }

        if (!_springLoadedPopoverWatchTimer.IsRunning)
        {
            _springLoadedPopoverWatchTimer.Start();
        }
    }

    private void StopSpringLoadedPopoverWatchTimer()
    {
        _springLoadedPopoverWatchTimer?.Stop();
        _springLoadedPopoverOutsideSinceTick = 0;
    }

    private void SpringLoadedPopoverWatchTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed || !_stackPopoverOpenedBySpringLoad)
        {
            StopSpringLoadedPopoverWatchTimer();
            return;
        }

        // While switchingStacks is waiting 2 render frames to reveal, keep the
        // watch timer armed until CompleteStackPopoverReveal sets PopupOpen.
        if (!_stackPopoverPopupOpen)
        {
            if (string.IsNullOrEmpty(_pendingStackPopoverKey))
            {
                StopSpringLoadedPopoverWatchTimer();
            }

            return;
        }

        if (_stackPopoverKey is not { } openKey)
        {
            StopSpringLoadedPopoverWatchTimer();
            return;
        }

        Win32Helper.POINT? cursor = Win32Helper.GetCursorPos(out Win32Helper.POINT pt)
            ? pt
            : null;
        bool insidePopover = IsCursorInsideOpenStackPopoverBounds(marginPixels: 28) ||
            _stackPopoverNativeDragOver;
        bool overAnchor = IsScreenPointInsideStackSurface(openKey, cursor);
        bool mouseDown = IsAnyMouseButtonPressed();

        if (insidePopover || overAnchor)
        {
            _springLoadedPopoverOutsideSinceTick = 0;
            if (!mouseDown && !_stackPopoverNativeDragOver && !_stackPopoverContextMenuOpen)
            {
                // Spring-loaded popovers open via SW_SHOWNOACTIVATE; if the drag
                // ended or cancelled via Esc without dropping, close the transient
                // TOPMOST host so it cannot remain stranded without light-dismiss.
                HideStackPopoverForReuse(restoreOwnerForeground: false);
            }

            return;
        }

        if (!mouseDown)
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
            return;
        }

        long now = Environment.TickCount64;
        if (_springLoadedPopoverOutsideSinceTick == 0)
        {
            _springLoadedPopoverOutsideSinceTick = now;
            return;
        }

        if (now - _springLoadedPopoverOutsideSinceTick >= StackSpringLoadedAutoCloseGraceMilliseconds)
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }
    }

    private void DisposeStackSpringLoadAndDismissTimers()
    {
        if (_deferredStackPopoverDismissTimer is not null)
        {
            _deferredStackPopoverDismissTimer.Stop();
            _deferredStackPopoverDismissTimer.Tick -= DeferredStackPopoverDismissTimer_Tick;
            _deferredStackPopoverDismissTimer = null;
        }

        if (_stackSpringLoadTimer is not null)
        {
            _stackSpringLoadTimer.Stop();
            _stackSpringLoadTimer.Tick -= StackSpringLoadTimer_Tick;
            _stackSpringLoadTimer = null;
        }

        if (_springLoadedPopoverWatchTimer is not null)
        {
            _springLoadedPopoverWatchTimer.Stop();
            _springLoadedPopoverWatchTimer.Tick -= SpringLoadedPopoverWatchTimer_Tick;
            _springLoadedPopoverWatchTimer = null;
        }

        if (_stackPopoverOutboundPassthroughTimer is not null)
        {
            _stackPopoverOutboundPassthroughTimer.Stop();
            _stackPopoverOutboundPassthroughTimer.Tick -= StackPopoverOutboundPassthroughTimer_Tick;
            _stackPopoverOutboundPassthroughTimer = null;
        }

        _pendingSpringLoadStackKey = null;
        _deferredStackPopoverDismissOriginCursor = null;
        _deferredStackPopoverDismissPointerMoved = false;
        _springLoadedPopoverOutsideSinceTick = 0;
    }

    internal void MarkStackPopoverXamlDropClaimed(DataPackageView? dataView)
    {
        _lastStackPopoverXamlClaimedEpoch = _stackPopoverInboundEpoch;
        if (dataView is not null &&
            dataView.Properties.TryGetValue(
                DeskBoxDragData.DragSessionIdProperty,
                out object? rawSessionId) &&
            rawSessionId is string sessionId &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            _lastStackPopoverXamlClaimedSessionId = sessionId;
        }

        if (_stackPopoverNativeDropBridge is { MaxActiveGestureId: > 0 } bridge &&
            bridge.MaxActiveGestureId > _lastStackPopoverNativeClaimedGestureId)
        {
            _lastStackPopoverNativeClaimedGestureId = bridge.MaxActiveGestureId;
        }
    }

    private void EnsureStackPopoverNativeDropBridge(StackPopoverHostWindow host)
    {
        if (_isDisposed || host.WindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_stackPopoverNativeDropBridge is null ||
            _stackPopoverNativeDropBridge.HostWindowHandle != host.WindowHandle)
        {
            _stackPopoverNativeDropBridge?.Dispose();
            _stackPopoverNativeDropBridge = new NativeDropHostBridge(
                host.WindowHandle,
                host.DispatcherQueue,
                StackPopoverDropSubclassId,
                CreateStackPopoverNativeDropTarget,
                DetachStackPopoverNativeDropTarget,
                ShouldSkipStackPopoverLegacyDropFiles,
                HandleStackPopoverLegacyDropFiles);
            host.AttachNativeDropBridge(_stackPopoverNativeDropBridge);
        }

        _stackPopoverNativeDropBridge.EnsureRegistered();
    }

    private NativeDropTarget CreateStackPopoverNativeDropTarget(IntPtr targetWindow)
    {
        var target = new NativeDropTarget(
            targetWindow,
            ShouldDefaultStackPopoverNativeDropToMove,
            CreateStackPopoverNativeDropDescription,
            ShouldUseStackPopoverNativeDropVisual,
            ShouldFollowWindowsStackPopoverNativeDrop)
        {
            SameVolumeProbe = IsStackPopoverNativeDropOnSameVolume,
            HasMappedFolderProbe = HasStackPopoverMappedFolder,
            UndisplayablePathProbe = FileService.IsFilteredFromWidgetDisplay,
            ImportBusyProbe = () => _isImportBusy
        };
        target.DragEnterEvent += StackPopoverNativeDrop_DragEnter;
        target.DragOverEvent += StackPopoverNativeDrop_DragOver;
        target.DragLeaveEvent += StackPopoverNativeDrop_DragLeave;
        target.UndisplayableDropBlocked += StackPopoverNativeDrop_UndisplayableBlocked;
        target.ZeroResultDropBlocked += StackPopoverNativeDrop_ZeroResultBlocked;
        target.ImportBusyDropBlocked += StackPopoverNativeDrop_ImportBusyBlocked;
        target.DropIntentEvent += StackPopoverNativeDrop_DropIntent;
        return target;
    }

    private void DetachStackPopoverNativeDropTarget(NativeDropTarget target)
    {
        target.DragEnterEvent -= StackPopoverNativeDrop_DragEnter;
        target.DragOverEvent -= StackPopoverNativeDrop_DragOver;
        target.DragLeaveEvent -= StackPopoverNativeDrop_DragLeave;
        target.UndisplayableDropBlocked -= StackPopoverNativeDrop_UndisplayableBlocked;
        target.ZeroResultDropBlocked -= StackPopoverNativeDrop_ZeroResultBlocked;
        target.ImportBusyDropBlocked -= StackPopoverNativeDrop_ImportBusyBlocked;
        target.DropIntentEvent -= StackPopoverNativeDrop_DropIntent;
    }

    private bool ShouldUseStackPopoverNativeDropVisual() =>
        !SuppressesNativeShellDragVisual && !_stackPopoverDragActive;

    private bool ShouldDefaultStackPopoverNativeDropToMove() =>
        string.Equals(
            _settingsService.Settings.FileWidget.ManagedDropAction,
            SettingsService.ManagedDropActionMove,
            StringComparison.Ordinal);

    private bool ShouldFollowWindowsStackPopoverNativeDrop() =>
        string.Equals(
            _settingsService.Settings.FileWidget.ManagedDropAction,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);

    private bool HasStackPopoverMappedFolder()
    {
        string prospective = TransferIntentEngine.ResolveProspectiveManagedDestination(
            ViewModel.CurrentFolderPath,
            ViewModel.MappedFolderPath,
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        return !string.IsNullOrWhiteSpace(prospective);
    }

    private bool IsStackPopoverNativeDropOnSameVolume(IReadOnlyList<string> paths)
    {
        string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
            ViewModel.CurrentFolderPath,
            ViewModel.MappedFolderPath,
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return true;
        }

        return PathOriginClassifier.AreAllOnSameVolume(
            paths,
            destination,
            TimeSpan.FromMilliseconds(80));
    }

    private NativeDropDescriptionText? CreateStackPopoverNativeDropDescription(uint effect)
    {
        if (effect == NativeDropEffectPolicy.None)
        {
            return null;
        }

        string stackName = _stackPopoverKey is { } key &&
            ViewModel.FindStackByKey(key) is { } stack
                ? stack.Name
                : (string.IsNullOrWhiteSpace(ViewModel.Name) ? "DeskBox" : ViewModel.Name);

        if (effect == NativeDropEffectPolicy.Link)
        {
            string linkMessage = ContentWidgetWindow.ToShellDropDescriptionMessage(
                _localizationService.T("Widget.CreateShortcut"));
            return linkMessage.Contains("%1", StringComparison.Ordinal)
                ? new NativeDropDescriptionText(linkMessage, stackName)
                : new NativeDropDescriptionText("%1", linkMessage);
        }

        string importMessage = ContentWidgetWindow.ToShellDropDescriptionMessage(
            _localizationService.T("Widget.Stack.DragCaption.Import"));
        return importMessage.Contains("%1", StringComparison.Ordinal)
            ? new NativeDropDescriptionText(importMessage, stackName)
            : null;
    }

    private void StackPopoverNativeDrop_UndisplayableBlocked(int count)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                NotifyNativeDropBlockedUndisplayable(count);
            }
        });
    }

    private void StackPopoverNativeDrop_ZeroResultBlocked()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                NotifyNativeDropZeroResult();
            }
        });
    }

    private void StackPopoverNativeDrop_ImportBusyBlocked()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                NotifyNativeDropImportBusy();
            }
        });
    }

    private void StackPopoverNativeDrop_DragEnter(int screenX, int screenY, bool hasFileData)
    {
        System.Threading.Interlocked.Increment(ref _stackPopoverInboundEpoch);
        _stackPopoverNativeDragOver = hasFileData;
        ObserveStackPopoverNativePointer(screenX, screenY, hasFileData);
    }

    private void StackPopoverNativeDrop_DragOver(int screenX, int screenY)
    {
        bool hasFileData = _stackPopoverNativeDropBridge?.Targets.Any(static t => t.HasFileData) == true;
        _stackPopoverNativeDragOver = hasFileData;
        ObserveStackPopoverNativePointer(screenX, screenY, hasFileData);
    }

    private void StackPopoverNativeDrop_DragLeave()
    {
        _stackPopoverNativeDragOver = false;
        System.Threading.Interlocked.Increment(ref _stackPopoverInboundEpoch);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            StopInteractiveAutoScroll(InteractiveAutoScrollMode.StackPopoverDrag);
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }
        });
    }

    private void ObserveStackPopoverNativePointer(int screenX, int screenY, bool hasFileData)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || !_stackPopoverPopupOpen)
            {
                return;
            }

            if (!hasFileData || _isImportBusy || _stackPopoverDragActive)
            {
                HideStackPopoverReorderIndicator();
                if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
                {
                    ClearStackMemberDropTarget();
                }

                return;
            }

            BeginStackPopoverDragAutoScroll();
            if (TryResolveStackPopoverChildIconDropTarget(
                    screenX,
                    screenY,
                    out Border? childBorder,
                    out WidgetItem? childTarget) &&
                childBorder is not null &&
                childTarget is not null)
            {
                HideStackPopoverReorderIndicator();
                if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
                {
                    ClearStackMemberDropTarget();
                }

                if (ItemDropBehaviorPolicy.Resolve(childTarget) == ItemDropBehavior.Launch)
                {
                    SetLaunchDropTarget(childBorder);
                }
                else
                {
                    SetFolderDropTarget(childBorder);
                }

                return;
            }

            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            if (_stackPopoverSurface is { } surface)
            {
                SetStackMemberDropTarget(surface);
            }

            UpdateStackPopoverIndicatorFromScreenPoint(screenX, screenY);
        });
    }

    internal bool TryResolveStackPopoverChildIconDropTarget(
        int? screenX,
        int? screenY,
        out Border? targetBorder,
        out WidgetItem? childTarget)
    {
        targetBorder = null;
        childTarget = null;
        if (screenX is not { } sx ||
            screenY is not { } sy ||
            _stackPopoverHostWindow is not { IsVisible: true, WindowHandle: not 0 } host ||
            _stackPopoverItemsView is not { Items.Count: > 0, XamlRoot.Content: FrameworkElement rootContent } view ||
            !TryGetScreenPointInElement(
                rootContent,
                host.WindowHandle,
                sx,
                sy,
                out Windows.Foundation.Point rootPoint))
        {
            return false;
        }

        foreach (DependencyObject element in VisualTreeHelper.FindElementsInHostCoordinates(rootPoint, view))
        {
            if (FileItemSurface.FindOwner(element) is not { } itemSurface ||
                itemSurface.DataContext is not WidgetItem item ||
                item is WidgetStackItem)
            {
                continue;
            }

            ItemDropBehavior behavior = ItemDropBehaviorPolicy.Resolve(item);
            if (behavior is not (ItemDropBehavior.FolderImport or
                ItemDropBehavior.RecycleBinDelete or
                ItemDropBehavior.Launch))
            {
                return false;
            }

            if (itemSurface.IconHitTestElement is not { } iconHost ||
                !TryGetScreenPointInElement(
                    iconHost,
                    host.WindowHandle,
                    sx,
                    sy,
                    out Windows.Foundation.Point localPoint) ||
                !ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
                    0,
                    0,
                    iconHost.ActualWidth,
                    iconHost.ActualHeight,
                    LaunchIconSlackPixels,
                    localPoint.X,
                    localPoint.Y))
            {
                return false;
            }

            targetBorder = itemSurface.InteractiveBorder;
            childTarget = item;
            return true;
        }

        return false;
    }

    private void UpdateStackPopoverIndicatorFromScreenPoint(int screenX, int screenY)
    {
        if (_stackPopoverHostWindow is not { IsVisible: true } host ||
            _stackPopoverItemsView is not { Items.Count: > 0 } view ||
            !TryGetScreenPointInElement(
                view,
                host.WindowHandle,
                screenX,
                screenY,
                out Windows.Foundation.Point pointInView) ||
            !ReorderDropIndexCalculator.IsPointerOverRealizedItem(view, pointInView))
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        _stackPopoverReorderInsertionIndex = ReorderDropIndexCalculator.Compute(
            view,
            pointInView,
            _stackPopoverReorderInsertionIndex);
        if (_stackPopoverReorderInsertionIndex < 0 ||
            _stackPopoverReorderInsertionIndex >= view.Items.Count)
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        UpdateStackPopoverReorderIndicator(
            view,
            pointInView,
            _stackPopoverReorderInsertionIndex);
    }

    private int? TryResolveStackPopoverNativeInsertionIndex(int? screenX, int? screenY)
    {
        if (screenX is not { } sx ||
            screenY is not { } sy ||
            _stackPopoverHostWindow is not { } host ||
            _stackPopoverItemsView is not { Items.Count: > 0 } view ||
            !TryGetScreenPointInElement(
                view,
                host.WindowHandle,
                sx,
                sy,
                out Windows.Foundation.Point pointInView) ||
            !ReorderDropIndexCalculator.IsPointerOverRealizedItem(view, pointInView))
        {
            return null;
        }

        int visibleIndex = ReorderDropIndexCalculator.Compute(
            view,
            pointInView,
            _stackPopoverReorderInsertionIndex);
        if (visibleIndex < 0 || visibleIndex >= view.Items.Count)
        {
            return null;
        }

        return ResolveStackPopoverMemberInsertionIndex(view, visibleIndex);
    }

    private bool IsStackPopoverGestureAlreadyClaimed(
        IReadOnlyList<string> paths,
        long gestureId,
        long epochAtDrop)
    {
        if (gestureId > 0 && gestureId <= _lastStackPopoverNativeClaimedGestureId)
        {
            return true;
        }

        if (epochAtDrop >= 0 && _lastStackPopoverXamlClaimedEpoch == epochAtDrop)
        {
            return true;
        }

        if (ActiveDeskBoxDragRegistry.TryMatch(
                paths,
                out string matchedSessionId,
                out _,
                out _) &&
            !string.IsNullOrWhiteSpace(matchedSessionId) &&
            string.Equals(
                matchedSessionId,
                _lastStackPopoverXamlClaimedSessionId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private void StackPopoverNativeDrop_DropIntent(NativeDropIntentEventArgs args)
    {
        _stackPopoverNativeDragOver = false;
        long capturedEpoch = _stackPopoverInboundEpoch;
        System.Threading.Interlocked.Increment(ref _stackPopoverInboundEpoch);

        string[] ownedPaths = args.Paths.ToArray();
        if (ownedPaths.Length == 0 ||
            IsStackPopoverGestureAlreadyClaimed(ownedPaths, args.GestureId, capturedEpoch))
        {
            if (args.ContainsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(ownedPaths));
            }

            return;
        }

        if (args.GestureId > _lastStackPopoverNativeClaimedGestureId)
        {
            _lastStackPopoverNativeClaimedGestureId = args.GestureId;
        }

        _lastStackPopoverNativeCompletedTick64 = Environment.TickCount64;
        _lastStackPopoverNativeFirstPath = ownedPaths[0];

        uint allowedEffects = args.AllowedEffects;
        bool isDeskBoxOwnedSource = ActiveDeskBoxDragRegistry.TryMatch(
            ownedPaths,
            out _,
            out _,
            out _);
        SourceOriginSnapshot sourceOrigin = PathOriginClassifier.ClassifySources(
            ownedPaths,
            TimeSpan.FromMilliseconds(80));
        bool allItemsTemporary = args.ItemOrigins is { Count: > 0 } origins
            ? origins.All(static o => o != DroppedItemOriginKind.RealFileSystemPath)
            : args.ContainsTemporaryFiles;
        bool sourceCanCopy = isDeskBoxOwnedSource ||
            (allowedEffects & NativeDropEffectPolicy.Copy) != 0;
        bool sourceCanMove = !allItemsTemporary &&
            !sourceOrigin.IsMoveProtectedSystemOrSharedSource &&
            (isDeskBoxOwnedSource ||
             (allowedEffects & NativeDropEffectPolicy.Move) != 0);
        if (args.RightButtonDrag)
        {
            if (!DispatcherQueue.TryEnqueue(() => _ = HandleStackPopoverRightButtonDropAsync(
                    ownedPaths,
                    args.ContainsTemporaryFiles,
                    allowedEffects,
                    args.ScreenX,
                    args.ScreenY,
                    args.ItemOrigins,
                    sourceCanCopy,
                    sourceCanMove,
                    allItemsTemporary)))
            {
                if (args.ContainsTemporaryFiles)
                {
                    _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(ownedPaths));
                }
            }

            return;
        }

        bool ctrlDown = (args.KeyState & NativeDropEffectPolicy.ControlKeyState) != 0;
        bool shiftDown = (args.KeyState & NativeDropEffectPolicy.ShiftKeyState) != 0;
        FileDropIntent? forcedIntent = args.ShortcutRequested && !allItemsTemporary
            ? FileDropIntent.Shortcut
            : ctrlDown && !shiftDown
                ? (sourceCanCopy ? FileDropIntent.Copy : FileDropIntent.None)
                : shiftDown && !ctrlDown
                    ? (sourceCanMove ? FileDropIntent.Move : FileDropIntent.None)
                    : null;

        if (!DispatcherQueue.TryEnqueue(() => _ = ImportStackPopoverNativeDropAsync(
                ownedPaths,
                args.ContainsTemporaryFiles,
                forcedIntent,
                allowedEffects,
                args.ScreenX,
                args.ScreenY,
                args.ItemOrigins)))
        {
            if (args.ContainsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(ownedPaths));
            }
        }
    }

    private async Task HandleStackPopoverRightButtonDropAsync(
        string[] ownedPaths,
        bool containsTemporaryFiles,
        uint allowedEffects,
        int screenX,
        int screenY,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins,
        bool sourceCanCopy,
        bool sourceCanMove,
        bool allItemsTemporary)
    {
        if (_isDisposed ||
            !_stackPopoverPopupOpen ||
            _stackPopoverSurface is not { XamlRoot: not null } surface ||
            _stackPopoverHostWindow is not { WindowHandle: not 0 } host)
        {
            if (containsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(ownedPaths));
            }

            return;
        }

        HideStackPopoverReorderIndicator();
        var tcs = new TaskCompletionSource<FileDropIntent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var menu = new MenuFlyout();

        void AddItem(string text, FileDropIntent? choice)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => tcs.TrySetResult(choice);
            menu.Items.Add(item);
        }

        if (sourceCanCopy)
        {
            AddItem(T("Common.Copy"), FileDropIntent.Copy);
        }

        if (sourceCanMove && HasStackPopoverMappedFolder())
        {
            AddItem(T("Common.Move"), FileDropIntent.Move);
        }

        if (!allItemsTemporary && HasStackPopoverMappedFolder())
        {
            AddItem(T("Widget.CreateShortcut"), FileDropIntent.Shortcut);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(T("Common.Cancel"), null);
        menu.Closed += (_, _) => tcs.TrySetResult(null);

        _stackPopoverContextMenuOpen = true;
        FileDropIntent? selected;
        try
        {
            var clientPt = new Win32Helper.POINT { X = screenX, Y = screenY };
            double scale = surface.XamlRoot.RasterizationScale > 0 ? surface.XamlRoot.RasterizationScale : 1.0;
            Windows.Foundation.Point localPt = Win32Helper.ScreenToClient(host.WindowHandle, ref clientPt)
                ? new Windows.Foundation.Point(clientPt.X / scale, clientPt.Y / scale)
                : new Windows.Foundation.Point(16, 16);
            menu.ShowAt(surface, localPt);
            selected = await tcs.Task;
        }
        catch
        {
            selected = null;
        }
        finally
        {
            _stackPopoverContextMenuOpen = false;
        }

        if (selected is null)
        {
            if (_stackPopoverOpenedBySpringLoad && _stackPopoverPopupOpen)
            {
                HideStackPopoverForReuse(restoreOwnerForeground: false);
            }

            if (containsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(ownedPaths));
            }

            return;
        }

        await ImportStackPopoverNativeDropAsync(
            ownedPaths,
            containsTemporaryFiles,
            selected,
            allowedEffects,
            screenX,
            screenY,
            itemOrigins);
    }

    private bool ShouldSkipStackPopoverLegacyDropFiles(IReadOnlyList<string> paths)
    {
        if (_isDisposed || !_stackPopoverPopupOpen || paths.Count == 0)
        {
            return true;
        }

        if (Environment.TickCount64 - _lastStackPopoverNativeCompletedTick64 < 250 &&
            string.Equals(paths[0], _lastStackPopoverNativeFirstPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void HandleStackPopoverLegacyDropFiles(LegacyDropFilesPayload payload)
    {
        _ = ImportStackPopoverNativeDropAsync(
            payload.StagedPaths,
            payload.ContainsTemporaryFiles,
            forcedIntent: null,
            allowedEffects: null,
            payload.ScreenX,
            payload.ScreenY,
            payload.ItemOrigins);
    }

    private async Task<bool> ImportStackPopoverNativeDropAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        FileDropIntent? forcedIntent,
        uint? allowedEffects,
        int? screenX,
        int? screenY,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins)
    {
        try
        {
            if (_isDisposed ||
                !_stackPopoverPopupOpen ||
                _stackPopoverKey is not { } stackKey ||
                ViewModel.FindStackByKey(stackKey) is not { } targetStack)
            {
                return false;
            }

            int? preferredMemberIndex = TryResolveStackPopoverNativeInsertionIndex(screenX, screenY);
            TryResolveStackPopoverChildIconDropTarget(
                screenX,
                screenY,
                out _,
                out WidgetItem? childIconTarget);
            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }

            bool closeAfterSpringDrop = _stackPopoverOpenedBySpringLoad;
            _stackPopoverOpenedBySpringLoad = false;
            StopSpringLoadedPopoverWatchTimer();

            if (childIconTarget is not null &&
                ItemDropBehaviorPolicy.Resolve(childIconTarget) == ItemDropBehavior.Launch)
            {
                bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
                    childIconTarget.Path,
                    paths);
                MarkNativeLaunchConsumed();
                if (!launched)
                {
                    ShowShortcutLaunchRefusedFeedback(childIconTarget.Name);
                }

                if (closeAfterSpringDrop && _stackPopoverPopupOpen)
                {
                    HideStackPopoverForReuse(restoreOwnerForeground: false);
                }

                return launched;
            }

            WidgetItem effectiveTargetItem = childIconTarget is not null &&
                ItemDropBehaviorPolicy.Resolve(childIconTarget) is
                    ItemDropBehavior.FolderImport or ItemDropBehavior.RecycleBinDelete
                ? childIconTarget
                : targetStack;

            bool imported = await ImportNativeDroppedFilesAsync(
                paths,
                containsTemporaryFiles,
                copyWhenMapped: null,
                targetItem: effectiveTargetItem,
                forcedIntent: forcedIntent,
                screenX: screenX,
                screenY: screenY,
                allowedEffects: allowedEffects,
                itemOrigins: itemOrigins,
                preferredStackMemberIndex: ReferenceEquals(effectiveTargetItem, targetStack)
                    ? preferredMemberIndex
                    : null);
            if (closeAfterSpringDrop && _stackPopoverPopupOpen)
            {
                HideStackPopoverForReuse(restoreOwnerForeground: false);
            }

            return imported;
        }
        finally
        {
            if (containsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(paths));
            }
        }
    }

    internal string ObserveCrossWidgetElevatedStackPopoverDrag(
        int screenX,
        int screenY,
        IReadOnlyList<string> sourcePaths,
        out bool canDrop)
    {
        canDrop = false;
        if (_isDisposed ||
            !_stackPopoverPopupOpen ||
            _isImportBusy ||
            _stackPopoverKey is not { } stackKey ||
            ViewModel.FindStackByKey(stackKey) is not { } targetStack)
        {
            return string.Empty;
        }

        if (TryResolveStackPopoverChildIconDropTarget(
                screenX,
                screenY,
                out Border? childBorder,
                out WidgetItem? childTarget) &&
            childBorder is not null &&
            childTarget is not null)
        {
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }

            ItemDropBehavior childBehavior = ItemDropBehaviorPolicy.Resolve(childTarget);
            if (childBehavior == ItemDropBehavior.Launch)
            {
                if (ShortcutLaunchPolicy.ContainsSameFile(sourcePaths, childTarget.Path))
                {
                    ClearLaunchDropTarget();
                    return string.Empty;
                }

                SetLaunchDropTarget(childBorder);
                canDrop = true;
                return _localizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    childTarget.Name);
            }

            if (childBehavior == ItemDropBehavior.RecycleBinDelete)
            {
                if (sourcePaths.Contains(childTarget.Path, StringComparer.OrdinalIgnoreCase))
                {
                    ClearFolderDropTarget();
                    return string.Empty;
                }

                SetFolderDropTarget(childBorder);
                canDrop = true;
                return T("Widget.DropOnRecycleBinHint");
            }

            if (childBehavior == ItemDropBehavior.FolderImport &&
                ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                    childTarget,
                    out string childFolderPath))
            {
                if (HasTransferConflict(sourcePaths, childFolderPath) ||
                    IsUnsafeFolderDrop(sourcePaths, childFolderPath))
                {
                    ClearFolderDropTarget();
                    return T("Widget.Error.UnsafeFolderTransfer");
                }

                if (AreAllSourcesAlreadyInDestinationLexically(sourcePaths, childFolderPath))
                {
                    ClearFolderDropTarget();
                    return T("Widget.DragCaption.CurrentWidget");
                }

                FileDropIntent childFolderIntent = ResolveCrossWidgetElevatedPopoverIntent(
                    sourcePaths,
                    childFolderPath,
                    hasMappedFolder: true,
                    controlDown: null,
                    shiftDown: null,
                    altDown: null);
                if (childFolderIntent == FileDropIntent.None)
                {
                    ClearFolderDropTarget();
                    return string.Empty;
                }

                SetFolderDropTarget(childBorder);
                canDrop = true;
                return FormatDropCaption(childFolderIntent, childTarget.Name);
            }
        }

        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        if (HasTransferConflict(sourcePaths, ViewModel.CurrentFolderPath))
        {
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }

            return string.Empty;
        }

        if (IsUnsafeFolderDrop(sourcePaths, ViewModel.CurrentFolderPath))
        {
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }

            return T("Widget.Error.UnsafeFolderTransfer");
        }

        if (AreAllSourcesAlreadyInDestinationLexically(sourcePaths, ViewModel.CurrentFolderPath))
        {
            HideStackPopoverReorderIndicator();
            if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
            {
                ClearStackMemberDropTarget();
            }

            return T("Widget.DragCaption.CurrentWidget");
        }

        if (_stackPopoverSurface is { } surface)
        {
            SetStackMemberDropTarget(surface);
        }

        UpdateStackPopoverIndicatorFromScreenPoint(screenX, screenY);

        string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
            ViewModel.CurrentFolderPath,
            ViewModel.MappedFolderPath,
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        FileDropIntent resolvedIntent = ResolveCrossWidgetElevatedPopoverIntent(
            sourcePaths,
            destination,
            hasMappedFolder: !string.IsNullOrWhiteSpace(destination),
            controlDown: null,
            shiftDown: null,
            altDown: null);
        if (resolvedIntent == FileDropIntent.None)
        {
            return string.Empty;
        }

        canDrop = true;
        return resolvedIntent == FileDropIntent.Shortcut
            ? FormatDropCaption(resolvedIntent, targetStack.Name)
            : _localizationService.Format("Widget.Stack.DragCaption.Add", targetStack.Name);
    }

    internal void LeaveCrossWidgetElevatedStackPopoverDrag()
    {
        if (_isDisposed)
        {
            return;
        }

        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        HideStackPopoverReorderIndicator();
        if (ReferenceEquals(_stackMemberDropTarget, _stackPopoverSurface))
        {
            ClearStackMemberDropTarget();
        }
    }

    internal async Task<bool> AcceptCrossWidgetElevatedStackPopoverDropAsync(
        IReadOnlyList<string> sourcePaths,
        int screenX,
        int screenY,
        bool? controlDown,
        bool? shiftDown,
        bool? altDown)
    {
        if (_isDisposed ||
            sourcePaths.Count == 0 ||
            !_stackPopoverPopupOpen ||
            _stackPopoverKey is not { } stackKey ||
            ViewModel.FindStackByKey(stackKey) is not { } targetStack)
        {
            LeaveCrossWidgetElevatedStackPopoverDrag();
            return false;
        }

        int? preferredMemberIndex = TryResolveStackPopoverNativeInsertionIndex(screenX, screenY);
        TryResolveStackPopoverChildIconDropTarget(
            screenX,
            screenY,
            out _,
            out WidgetItem? childIconTarget);
        LeaveCrossWidgetElevatedStackPopoverDrag();
        bool closeAfterSpringDrop = _stackPopoverOpenedBySpringLoad;
        _stackPopoverOpenedBySpringLoad = false;
        StopSpringLoadedPopoverWatchTimer();

        if (childIconTarget is not null &&
            ItemDropBehaviorPolicy.Resolve(childIconTarget) == ItemDropBehavior.Launch)
        {
            bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
                childIconTarget.Path,
                sourcePaths);
            MarkNativeLaunchConsumed();
            if (!launched)
            {
                ShowShortcutLaunchRefusedFeedback(childIconTarget.Name);
            }

            if (closeAfterSpringDrop && _stackPopoverPopupOpen)
            {
                HideStackPopoverForReuse(restoreOwnerForeground: false);
            }

            return launched;
        }

        WidgetItem effectiveTargetItem = childIconTarget is not null &&
            ItemDropBehaviorPolicy.Resolve(childIconTarget) is
                ItemDropBehavior.FolderImport or ItemDropBehavior.RecycleBinDelete
            ? childIconTarget
            : targetStack;

        string destination = childIconTarget is not null &&
            ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                childIconTarget,
                out string childFolderDest)
            ? childFolderDest
            : TransferIntentEngine.ResolveProspectiveManagedDestination(
                ViewModel.CurrentFolderPath,
                ViewModel.MappedFolderPath,
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        FileDropIntent snapshotIntent = ResolveCrossWidgetElevatedPopoverIntent(
            sourcePaths,
            destination,
            hasMappedFolder: !string.IsNullOrWhiteSpace(destination),
            controlDown: controlDown,
            shiftDown: shiftDown,
            altDown: altDown);
        if (snapshotIntent == FileDropIntent.None)
        {
            return false;
        }

        FileDropIntent? forcedIntent = snapshotIntent == FileDropIntent.Reference
            ? null
            : snapshotIntent;
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        uint allowedEffects = !origin.IsMoveProtectedSystemOrSharedSource
            ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
            : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;

        bool imported = await ImportNativeDroppedFilesAsync(
            sourcePaths,
            containsTemporaryFiles: false,
            copyWhenMapped: null,
            targetItem: effectiveTargetItem,
            forcedIntent: forcedIntent,
            screenX: screenX,
            screenY: screenY,
            allowedEffects: allowedEffects,
            preferredStackMemberIndex: ReferenceEquals(effectiveTargetItem, targetStack)
                ? preferredMemberIndex
                : null);
        if (closeAfterSpringDrop && _stackPopoverPopupOpen)
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }

        return imported;
    }

    private FileDropIntent ResolveCrossWidgetElevatedPopoverIntent(
        IReadOnlyList<string> sourcePaths,
        string destination,
        bool hasMappedFolder,
        bool? controlDown,
        bool? shiftDown,
        bool? altDown)
    {
        string dropAction = _settingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            dropAction,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        bool defaultMove = followWindows ||
            string.Equals(
                dropAction,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        bool sameVolume = !string.IsNullOrWhiteSpace(destination) &&
            PathOriginClassifier.AreAllOnSameVolume(
                sourcePaths,
                destination,
                TimeSpan.FromMilliseconds(80));
        uint allowedEffects = !origin.IsMoveProtectedSystemOrSharedSource
            ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
            : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: hasMappedFolder,
            ControlDown: controlDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
            ShiftDown: shiftDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
            AltDown: altDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
            AllowedEffects: allowedEffects,
            NoOperationMetadata: false,
            DefaultMove: defaultMove,
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).Intent;
    }
}
