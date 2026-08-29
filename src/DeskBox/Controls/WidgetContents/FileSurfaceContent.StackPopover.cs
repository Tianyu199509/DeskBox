using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
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
using Microsoft.UI.Xaml.Media.Animation;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan StackPopoverCacheRetention =
        TimeSpan.FromSeconds(30);

    private Popup? _stackPopoverPopup;
    private ListViewBase? _stackPopoverItemsView;
    // Keep one source instance for the lifetime of the cached popup. Replacing
    // ItemsSource on every open makes WinUI rebuild its view/recycle pool and
    // leaves native template allocations behind after repeated light dismisses.
    private readonly ObservableCollection<WidgetItem> _stackPopoverItems = [];
    private DispatcherQueueTimer? _stackPopoverCacheReleaseTimer;
    private WidgetMaterialSystemBackdrop? _stackPopoverMaterialBackdrop;
    private DesktopAcrylicBackdrop? _stackPopoverNeutralBackdrop;
    private Button? _stackPopoverCloseButton;
    private Border? _stackPopoverSurface;
    private Grid? _stackPopoverTitleHost;
    private TextBlock? _stackPopoverTitleText;
    private TextBox? _stackPopoverTitleEditor;
    private StackPopoverInlineRenameWindow? _stackPopoverTitleEditorWindow;
    private TextBlock? _stackPopoverEmptyText;
    private Canvas? _stackPopoverTextShadowHost;
    private WidgetTextShadowManager? _stackPopoverTextShadowManager;
    private Canvas? _stackPopoverReorderOverlay;
    private Border? _stackPopoverReorderIndicator;
    private Canvas? _stackPopoverSelectionOverlay;
    private Border? _stackPopoverSelectionRectangle;
    private Grid? _stackPopoverSelectionHost;
    private StackPopoverLayout? _stackPopoverLayout;
    private int _stackPopoverReorderInsertionIndex = -1;
    private WidgetItem[] _stackPopoverMembers = [];
    private string? _stackPopoverKey;
    private bool _stackPopoverPopupOpen;
    private bool _stackPopoverPopupClosing;
    private bool _stackPopoverIsListMode;
    private bool _stackPopoverContextMenuOpen;
    private bool _stackPopoverDragActive;
    private bool _stackPopoverCleanupPending;
    private long _stackPopoverShowGeneration;
    private string? _pendingStackPopoverKey;
    private KeyEventHandler? _stackPopoverPreviewKeyHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerPressedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerMovedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerReleasedHandler;
    private PointerEventHandler? _stackPopoverSelectionPointerCaptureLostHandler;
    private PointerEventHandler? _stackPopoverSurfacePointerPressedHandler;
    private bool _stackPopoverTitleEditing;
    private bool _stackPopoverTitleCommitInProgress;
    private string? _stackPopoverTitleOriginalName;

    private bool IsStackPopoverInteractionActive =>
        _stackPopoverItemsView is not null &&
        (_stackPopoverPopupOpen || _stackPopoverContextMenuOpen);

    private void InitializeStackPopoverLifecycle()
    {
        ViewModel.PropertyChanged += ViewModel_StackPopoverPropertyChanged;
    }

    private void DisposeStackPopoverLifecycle()
    {
        ViewModel.PropertyChanged -= ViewModel_StackPopoverPropertyChanged;
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

        if (e.PropertyName is nameof(WidgetViewModel.IconImageSize) or
            nameof(WidgetViewModel.ListIconSize) or
            nameof(WidgetViewModel.EffectiveIconSize))
        {
            // Layout settings update the stack item metrics later in the same
            // dispatcher turn. Reapply after that update so the local visual
            // values continue to follow the configured icon size.
            DispatcherQueue.TryEnqueue(UpdateStackFolderPreviewModes);
        }

        if (e.PropertyName is nameof(WidgetViewModel.IsIconMode) or
            nameof(WidgetViewModel.IsListMode))
        {
            CloseStackPopover(releaseImmediately: true);
        }
    }

    private void UpdateStackFolderPreviewModes()
    {
        foreach (Border surface in _stackSurfaces.ToArray())
        {
            if (surface.XamlRoot is null)
            {
                _stackSurfaces.Remove(surface);
                continue;
            }

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
                _settingsService.Settings.WidgetCornerPreference);

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

        // A normally dismissed popover keeps its control tree as a bounded
        // per-surface cache. Reopening it only rebinds the current members.
        QueueStackPopoverShow(stackKey);
    }

    private void QueueStackPopoverShow(string stackKey)
    {
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

            _pendingStackPopoverKey = null;
            if (ViewModel.UsesStackPopover &&
                ViewModel.FindStackByKey(stackKey) is { } current)
            {
                ShowStackPopover(current);
            }
        });
        if (!queued && generation == _stackPopoverShowGeneration)
        {
            _pendingStackPopoverKey = null;
        }
    }

    private void ShowStackPopover(WidgetStackItem stack)
    {
        if (_isDisposed ||
            !ViewModel.UsesStackPopover ||
            _stackPopoverPopupOpen ||
            _stackPopoverPopupClosing ||
            XamlRoot is null ||
            FindStackSurface(stack.StackKey) is not { } anchor)
        {
            return;
        }

        WidgetStackItem? currentStack =
            ViewModel.FindStackByKey(stack.StackKey);
        if (currentStack is null || currentStack.Members.Count == 0)
        {
            return;
        }

        StackPopoverLayout layout = CalculateStackPopoverLayout(
            currentStack.Members.Count);

        StopStackPopoverCacheReleaseTimer();

        // ListView and GridView have different native templates. Recreate the
        // cached tree only when the host view mode changes; ordinary open/close
        // cycles reuse the same Popup, acrylic backdrop, and item control.
        if (_stackPopoverPopup is { } cachedPopup &&
            _stackPopoverIsListMode != ViewModel.IsListMode)
        {
            ReleaseStackPopover(cachedPopup);
        }

        Popup popup;
        if (_stackPopoverPopup is null)
        {
            ListViewBase itemsView = CreateStackPopoverItemsView(layout);
            Border surface = CreateStackPopoverSurface(
                currentStack,
                itemsView,
                layout);
            // Windows-native popup entrance: soft rise plus fade, matching
            // the system light-dismiss flyout motion.
            surface.Transitions = new TransitionCollection
            {
                new PopupThemeTransition { FromVerticalOffset = 20 }
            };
            popup = new Popup
            {
                Child = surface,
                XamlRoot = XamlRoot,
                IsLightDismissEnabled = true,
                LightDismissOverlayMode = LightDismissOverlayMode.Off,
                ShouldConstrainToRootBounds = false
            };
            popup.Opened += StackPopoverPopup_Opened;
            popup.Closed += StackPopoverPopup_Closed;
            _stackPopoverPopup = popup;
            _stackPopoverItemsView = itemsView;
            _stackPopoverSurface = surface;
            _stackPopoverIsListMode = ViewModel.IsListMode;
        }
        else
        {
            popup = _stackPopoverPopup;
        }

        if (_stackPopoverItemsView is null || _stackPopoverSurface is null)
        {
            ReleaseStackPopover(popup);
            return;
        }

        _stackPopoverKey = currentStack.StackKey;
        _stackPopoverMembers = currentStack.Members.ToArray();
        _stackPopoverLayout = layout;
        _stackPopoverPopupClosing = false;
        _stackPopoverCleanupPending = false;
        _stackPopoverItemsView.SelectedItems.Clear();
        ReconcileStackPopoverItems(_stackPopoverMembers);
        _stackPopoverEmptyText?.Visibility =
            _stackPopoverMembers.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        _stackPopoverSurface.DataContext = currentStack;
        AutomationProperties.SetName(
            _stackPopoverSurface,
            currentStack.Name);
        ApplyStackPopoverLayout(currentStack);
        UpdateStackPopoverAppearance();

        try
        {
            popup.XamlRoot = XamlRoot;
            popup.IsOpen = true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileStack] Popover open failed widget={WidgetId} " +
                $"stack={currentStack.StackKey}: {ex}");
            ReleaseStackPopover(popup);
        }
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
            var containerStyle = new Style(typeof(GridViewItem))
            {
                BasedOn =
                    Resources["SurfaceGridViewItemStyle"] as Style
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
            view = new GridView
            {
                ItemContainerStyle = containerStyle,
                ItemTemplate =
                    Resources["StackPopoverFileIconTemplate"] as DataTemplate
            };
        }

        view.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        // Keep the native panel virtualized and avoid the default item-container
        // transition objects. The popup already has a bounded viewport; animating
        // every recycled child adds compositor work and retains transition state.
        view.ItemsPanel = ViewModel.IsListMode
            ? Resources["StackPopoverListItemsPanelTemplate"] as ItemsPanelTemplate
            : Resources["StackPopoverIconItemsPanelTemplate"] as ItemsPanelTemplate;
        view.ItemContainerTransitions = null;
        view.ItemsSource = _stackPopoverItems;
        view.Width = layout.ItemsWidth;
        view.MaxHeight = layout.ItemsHeight;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Stretch;
        view.IsItemClickEnabled = true;
        view.CanDragItems = true;
        view.CanReorderItems = false;
        view.AllowDrop = true;
        view.IsMultiSelectCheckBoxEnabled = false;
        view.SelectionMode = ListViewSelectionMode.Extended;
        ScrollViewer.SetVerticalScrollBarVisibility(
            view,
            layout.HasVerticalOverflow
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            view,
            ScrollBarVisibility.Disabled);

        view.ItemClick += Items_ItemClick;
        view.DragItemsCompleted += Items_DragItemsCompleted;
        view.DragItemsStarting += Items_DragItemsStarting;
        view.DragStarting += Items_DragStarting;
        view.DragOver += StackPopoverItems_DragOver;
        view.DragLeave += StackPopoverItems_DragLeave;
        view.Drop += StackPopoverItems_Drop;
        view.DoubleTapped += Items_DoubleTapped;
        view.KeyDown += Root_KeyDown;
        view.RightTapped += Items_RightTapped;
        view.SelectionChanged += Items_SelectionChanged;
        view.CharacterReceived += Root_CharacterReceived;
        _stackPopoverPreviewKeyHandler =
            new KeyEventHandler(ItemsView_PreviewKeyDown);
        view.AddHandler(
            UIElement.PreviewKeyDownEvent,
            _stackPopoverPreviewKeyHandler,
            handledEventsToo: true);
        return view;
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
        titleHost.Children.Add(closeButton);
        _stackPopoverCloseButton = closeButton;
        content.Children.Add(titleHost);

        var textShadowHost = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(textShadowHost, 2);
        Canvas.SetZIndex(textShadowHost, 30);
        content.Children.Add(textShadowHost);
        _stackPopoverTextShadowHost = textShadowHost;

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
        var reorderOverlay = new Canvas
        {
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(reorderOverlay, 25);
        var reorderIndicator = new Border
        {
            Background = new SolidColorBrush(
                App.Current.ThemeService?.GetEffectiveAccentColor() ??
                AccentColorHelper.DefaultAccentColor),
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
            Child = content,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 48)
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

    private void BeginStackPopoverTitleRename(TextBlock title)
    {
        if (_stackPopoverTitleEditing ||
            !ReferenceEquals(_stackPopoverTitleText, title) ||
            _stackPopoverKey is not { } stackKey ||
            ViewModel.FindStackByKey(stackKey) is not { } stack)
        {
            return;
        }

        const double editorHeight =
            StackPopoverLayoutCalculator.TitleEditorHeight;
        double maximumWidth = Math.Max(
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            (_stackPopoverSurface?.ActualWidth ?? 0) -
                (StackPopoverLayoutCalculator.SurfacePadding * 2));
        double editorWidth = Math.Clamp(
            Math.Max(title.ActualWidth, title.DesiredSize.Width) + 6,
            StackPopoverLayoutCalculator.TitleMinimumWidth,
            maximumWidth);
        Style? inlineRenameStyle =
            Application.Current.Resources.TryGetValue(
                "WidgetInlineRenameTextBoxStyle",
                out object? inlineRenameStyleValue)
                ? inlineRenameStyleValue as Style
                : null;
        var editorWindow = new StackPopoverInlineRenameWindow(
            stack.Name,
            inlineRenameStyle,
            CreateStackPopoverSurfaceBrush(),
            ResolveBrush("TextFillColorPrimaryBrush"),
            ResolveStackPopoverMaterialAppearance(),
            _hostWindowHandle);
        TextBox editor = editorWindow.Editor;
        editor.Loaded += StackPopoverTitleEditor_Loaded;
        editor.KeyDown += StackPopoverTitleEditor_KeyDown;
        editor.LostFocus += StackPopoverTitleEditor_LostFocus;
        editorWindow.Closed += StackPopoverTitleEditorWindow_Closed;
        AutomationProperties.SetName(editor, T("Common.Rename"));

        _stackPopoverTitleEditing = true;
        _stackPopoverTitleOriginalName = stack.Name;
        _stackPopoverTitleEditor = editor;
        _stackPopoverTitleEditorWindow = editorWindow;
        if (_stackPopoverPopup is { } popup)
        {
            popup.IsLightDismissEnabled = false;
        }
        title.Visibility = Visibility.Collapsed;
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-stack-popover-title-rename-opened");
        editorWindow.ShowAndFocus(ResolveStackPopoverTitleEditorBounds(
            editorWidth,
            editorHeight));
    }

    private Windows.Graphics.RectInt32 ResolveStackPopoverTitleEditorBounds(
        double editorWidth,
        double editorHeight)
    {
        double scale = Math.Max(
            0.5,
            Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                XamlRoot));
        int width = Math.Max(1, (int)Math.Round(editorWidth * scale));
        int height = Math.Max(1, (int)Math.Round(editorHeight * scale));
        if (_hostWindowHandle != IntPtr.Zero &&
            Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT hostBounds) &&
            _stackPopoverPopup is { } popup &&
            _stackPopoverSurface is { } surface)
        {
            double left = popup.HorizontalOffset +
                ((surface.ActualWidth - editorWidth) / 2);
            double top = popup.VerticalOffset +
                surface.Padding.Top +
                ((StackPopoverLayoutCalculator.TitleHeight -
                    editorHeight) / 2);
            return new Windows.Graphics.RectInt32(
                hostBounds.Left + (int)Math.Round(left * scale),
                hostBounds.Top + (int)Math.Round(top * scale),
                width,
                height);
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return new Windows.Graphics.RectInt32(
                cursor.X - (width / 2),
                cursor.Y - (height / 2),
                width,
                height);
        }

        return new Windows.Graphics.RectInt32(0, 0, width, height);
    }

    private void StackPopoverTitleEditorWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        if (_stackPopoverTitleEditing &&
            ReferenceEquals(sender, _stackPopoverTitleEditorWindow))
        {
            CommitStackPopoverTitleRename();
        }
    }

    private void StackPopoverTitleEditor_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
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

    private void StackPopoverTitleEditor_LostFocus(
        object sender,
        RoutedEventArgs e) =>
        CommitStackPopoverTitleRename();

    private void StackPopoverSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_stackPopoverTitleEditing)
        {
            return;
        }

        CommitStackPopoverTitleRename();
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
                    if (_stackPopoverPopup is not null)
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
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
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
        TextBox? editor = _stackPopoverTitleEditor;
        StackPopoverInlineRenameWindow? editorWindow =
            _stackPopoverTitleEditorWindow;
        _stackPopoverTitleEditor = null;
        _stackPopoverTitleEditorWindow = null;
        if (editor is not null)
        {
            editor.Loaded -= StackPopoverTitleEditor_Loaded;
            editor.KeyDown -= StackPopoverTitleEditor_KeyDown;
            editor.LostFocus -= StackPopoverTitleEditor_LostFocus;
        }
        if (editorWindow is not null)
        {
            editorWindow.Closed -= StackPopoverTitleEditorWindow_Closed;
        }
        if (_stackPopoverPopup is { } popup)
        {
            popup.IsLightDismissEnabled = true;
        }
        if (_stackPopoverTitleText is { } title)
        {
            title.Visibility = Visibility.Visible;
        }
        App.Current?.WidgetManager?.EndWidgetInteraction(
            "surface-stack-popover-title-rename-closed");
        editorWindow?.CloseEditorWindow();
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
        if (materialSupported &&
            WindowsCompatibilityService.UsesLegacyWindowAcrylic &&
            SettingsService.IsAcrylicMaterial(appearance.MaterialType))
        {
            surfaceColor =
                WidgetMaterialVisualCalculator
                    .BuildLegacyAcrylicSurfaceOverlayColor(
                        appearance.IsDark,
                        appearance.AccentColor,
                        appearance.MaterialType ==
                            SettingsService.WidgetMaterialTypeAcrylicBase,
                        appearance.SurfaceOpacity,
                        appearance.MaterialIntensity);
        }
        else if (materialSupported)
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
        }

        return SharedBrushCache.GetOrCreate(surfaceColor);
    }

    private double ResolveStackPopoverCornerRadius() =>
        WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            _settingsService.Settings.WidgetCornerPreference);

    private WidgetBorderVisuals ResolveStackPopoverBorderVisuals()
    {
        Windows.UI.Color accentColor =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        return WidgetBorderVisualCalculator.Resolve(
            _settingsService.Settings.WidgetBorderStyle,
            _settingsService.Settings.WidgetBorderColorMode,
            IsStackPopoverDarkTheme(),
            accentColor);
    }

    private bool IsStackPopoverDarkTheme() =>
        ActualTheme == ElementTheme.Dark ||
        ActualTheme == ElementTheme.Default &&
        Application.Current?.RequestedTheme == ApplicationTheme.Dark;

    private void UpdateStackPopoverAppearance()
    {
        if (_stackPopoverSurface is null)
        {
            return;
        }

        bool followMaterial = UsesStackPopoverMaterialStyle();
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
            if (followMaterial)
            {
                UpdateStackPopoverTextEdge(content, primary);
            }
            else if (_stackPopoverTextShadowManager is not null)
            {
                _stackPopoverTextShadowManager.Dispose();
                _stackPopoverTextShadowManager = null;
            }
        }
        if (_stackPopoverTitleText is not null)
        {
            _stackPopoverTitleText.Foreground = primary;
        }
        if (_stackPopoverEmptyText is not null)
        {
            _stackPopoverEmptyText.Foreground = secondary;
        }
        if (_stackPopoverReorderIndicator is not null)
        {
            _stackPopoverReorderIndicator.Background =
                SharedBrushCache.GetOrCreate(materialAppearance.AccentColor);
        }

        if (_stackPopoverPopup is { } popup)
        {
            if (followMaterial &&
                WidgetMaterialSystemBackdrop.IsSupported(
                    materialAppearance.MaterialType))
            {
                _stackPopoverMaterialBackdrop ??=
                    new WidgetMaterialSystemBackdrop(materialAppearance);
                _stackPopoverMaterialBackdrop.UpdateAppearance(
                    materialAppearance);
                if (!ReferenceEquals(
                        popup.SystemBackdrop,
                        _stackPopoverMaterialBackdrop))
                {
                    popup.SystemBackdrop = _stackPopoverMaterialBackdrop;
                }
            }
            else if (followMaterial)
            {
                popup.SystemBackdrop = null;
                _stackPopoverMaterialBackdrop = null;
            }
            else
            {
                // Neutral keeps the original translucent acrylic look with a
                // theme-following tint so the light theme reads correctly.
                _stackPopoverNeutralBackdrop ??= new DesktopAcrylicBackdrop();
                if (!ReferenceEquals(
                        popup.SystemBackdrop,
                        _stackPopoverNeutralBackdrop))
                {
                    popup.SystemBackdrop = _stackPopoverNeutralBackdrop;
                }
                _stackPopoverMaterialBackdrop = null;
            }
        }

        _stackPopoverTitleEditorWindow?.UpdateAppearance(
            background,
            primary,
            followMaterial
                ? materialAppearance
                : materialAppearance with
                {
                    MaterialType = SettingsService.WidgetMaterialTypeSolid
                });
    }

    private bool UsesStackPopoverMaterialStyle() =>
        SettingsService.NormalizeFileStackPopoverStyle(
            _settingsService.Settings.FileStackPopoverStyle) ==
        SettingsService.FileStackPopoverStyleFollowMaterial;

    private static SolidColorBrush CreateStackPopoverNeutralBrush(
        bool isDark) =>
        SharedBrushCache.GetOrCreate(isDark
            ? Windows.UI.Color.FromArgb(0x42, 0x18, 0x18, 0x1B)
            : Windows.UI.Color.FromArgb(0x58, 0xF8, 0xF8, 0xFA));

    private void UpdateStackPopoverTextEdge(
        FrameworkElement content,
        Brush? primary)
    {
        string edgeMode = WindowsCompatibilityService.IsHighContrast
            ? WidgetForegroundSettings.EdgeOff
            : WidgetForegroundSettings.ResolveEdgeMode(
                Config,
                _settingsService.Settings);
        if (string.Equals(
                edgeMode,
                WidgetForegroundSettings.EdgeOff,
                StringComparison.Ordinal) ||
            primary is not SolidColorBrush primaryBrush ||
            _stackPopoverTextShadowHost is null)
        {
            _stackPopoverTextShadowManager?.Dispose();
            _stackPopoverTextShadowManager = null;
            return;
        }

        _stackPopoverTextShadowManager ??=
            new WidgetTextShadowManager(
                content,
                _stackPopoverTextShadowHost);
        _stackPopoverTextShadowManager.Apply(edgeMode, primaryBrush.Color);
    }

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

    private void StackPopoverPopup_Opened(
        object? sender,
        object e)
    {
        if (!ReferenceEquals(sender, _stackPopoverPopup))
        {
            return;
        }

        _stackPopoverPopupOpen = true;
        _stackPopoverCleanupPending = false;
        App.LogVerbose(
            $"[FileStack] Popover opened widget={WidgetId} " +
            $"stack={_stackPopoverKey}");
        _stackPopoverItemsView?.Focus(FocusState.Programmatic);
    }

    private void StackPopoverPopup_Closed(
        object? sender,
        object e)
    {
        if (sender is not Popup popup ||
            !ReferenceEquals(popup, _stackPopoverPopup))
        {
            return;
        }

        CommitStackPopoverTitleRename();
        _stackPopoverPopupOpen = false;
        _stackPopoverCleanupPending = true;
        App.LogVerbose(
            $"[FileStack] Popover closed widget={WidgetId} " +
            $"stack={_stackPopoverKey}");
        if (!_stackPopoverContextMenuOpen &&
            !_stackPopoverDragActive)
        {
            ClearStackPopoverContentForReuse(popup);
            ScheduleStackPopoverCacheRelease();
            QueuePendingStackPopoverShowAfterClose();
        }
    }

    private void CloseStackPopover(bool releaseImmediately = false)
    {
        _stackPopoverShowGeneration++;
        _pendingStackPopoverKey = null;
        if (_stackPopoverPopup is not { } popup)
        {
            return;
        }

        CommitStackPopoverTitleRename();

        if (!releaseImmediately)
        {
            _stackPopoverCleanupPending = true;
        }

        _stackPopoverPopupClosing = _stackPopoverPopupOpen;

        if (releaseImmediately)
        {
            popup.Opened -= StackPopoverPopup_Opened;
            popup.Closed -= StackPopoverPopup_Closed;
        }

        try
        {
            popup.IsOpen = false;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileStack] Popover close failed widget={WidgetId}: {ex}");
        }

        if (releaseImmediately ||
            !_stackPopoverPopupOpen &&
            !_stackPopoverDragActive &&
            !_stackPopoverContextMenuOpen)
        {
            if (releaseImmediately)
            {
                ReleaseStackPopover(popup);
            }
            else
            {
                ClearStackPopoverContentForReuse(popup);
                ScheduleStackPopoverCacheRelease();
                QueuePendingStackPopoverShowAfterClose();
            }
        }
    }

    private void QueuePendingStackPopoverShowAfterClose()
    {
        if (_pendingStackPopoverKey is null)
        {
            return;
        }

        long generation = _stackPopoverShowGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _stackPopoverShowGeneration ||
                _stackPopoverPopupOpen ||
                _stackPopoverPopupClosing ||
                _pendingStackPopoverKey is not { } stackKey)
            {
                return;
            }

            _pendingStackPopoverKey = null;
            if (!_isDisposed &&
                ViewModel.UsesStackPopover &&
                ViewModel.FindStackByKey(stackKey) is { } current)
            {
                ShowStackPopover(current);
            }
        });
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

    private void ScheduleStackPopoverCacheRelease()
    {
        if (_isDisposed || _stackPopoverPopup is null)
        {
            return;
        }

        if (_stackPopoverCacheReleaseTimer is null)
        {
            _stackPopoverCacheReleaseTimer = DispatcherQueue.CreateTimer();
            _stackPopoverCacheReleaseTimer.IsRepeating = false;
            _stackPopoverCacheReleaseTimer.Tick +=
                StackPopoverCacheReleaseTimer_Tick;
        }

        _stackPopoverCacheReleaseTimer.Stop();
        _stackPopoverCacheReleaseTimer.Interval = StackPopoverCacheRetention;
        _stackPopoverCacheReleaseTimer.Start();
    }

    private void StopStackPopoverCacheReleaseTimer()
    {
        if (_stackPopoverCacheReleaseTimer is not { } timer)
        {
            return;
        }

        _stackPopoverCacheReleaseTimer = null;
        timer.Stop();
        timer.Tick -= StackPopoverCacheReleaseTimer_Tick;
    }

    private void StackPopoverCacheReleaseTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_isDisposed ||
            _stackPopoverPopupOpen ||
            _stackPopoverPopupClosing ||
            _stackPopoverContextMenuOpen ||
            _stackPopoverDragActive ||
            _stackPopoverPopup is not { } popup)
        {
            return;
        }

        ReleaseStackPopover(popup);
        App.ScheduleLightMemoryCleanup();
    }

    private void DetachStackPopoverItemSurfaces(ListViewBase view)
    {
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
        if (root is FileItemSurface surface)
        {
            surface.VisualStateChanged -= ItemSurface_VisualStateChanged;
            surface.LayoutContext = null;
            _itemSurfaces.Remove(surface.InteractiveBorder);
            if (ReferenceEquals(surface.InteractiveBorder, _folderDropTarget))
            {
                _folderDropTarget = null;
                _folderDropVisualActive = false;
            }
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DetachStackPopoverItemSurfaces(
                VisualTreeHelper.GetChild(root, index));
        }
    }

    private void ClearStackPopoverContentForReuse(Popup popup)
    {
        if (!ReferenceEquals(popup, _stackPopoverPopup))
        {
            return;
        }

        CommitStackPopoverTitleRename();
        if (_stackPopoverItemsView is { } view)
        {
            view.SelectedItems.Clear();
        }

        if (_stackPopoverSurface is { } surface)
        {
            surface.DataContext = null;
        }

        ResetBoxSelectionState();
        HideStackPopoverReorderIndicator();
        _stackPopoverMembers = [];
        _stackPopoverKey = null;
        _stackPopoverLayout = null;
        _stackPopoverPopupOpen = false;
        _stackPopoverPopupClosing = false;
        _stackPopoverCleanupPending = false;
        _stackPopoverContextMenuOpen = false;
        _stackPopoverDragActive = false;
        // Keep the realized viewport and its data source intact while the popup
        // is cached. Rebinding/clearing here is the operation that caused the
        // repeated open/close memory climb and close-time frame drops.
        UpdateSelectionCommandBar();
    }

    private void ReleaseStackPopover(Popup popup)
    {
        if (!ReferenceEquals(popup, _stackPopoverPopup))
        {
            return;
        }

        StopStackPopoverCacheReleaseTimer();
        popup.Opened -= StackPopoverPopup_Opened;
        popup.Closed -= StackPopoverPopup_Closed;
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
        if (_stackPopoverTitleEditorWindow is not null)
        {
            _stackPopoverTitleEditorWindow.Closed -=
                StackPopoverTitleEditorWindow_Closed;
        }
        if (_stackPopoverItemsView is { } view)
        {
            DetachStackPopoverItemSurfaces(view);
            view.ItemClick -= Items_ItemClick;
            view.DragItemsCompleted -= Items_DragItemsCompleted;
            view.DragItemsStarting -= Items_DragItemsStarting;
            view.DragStarting -= Items_DragStarting;
            view.DragOver -= StackPopoverItems_DragOver;
            view.DragLeave -= StackPopoverItems_DragLeave;
            view.Drop -= StackPopoverItems_Drop;
            view.DoubleTapped -= Items_DoubleTapped;
            view.KeyDown -= Root_KeyDown;
            view.RightTapped -= Items_RightTapped;
            view.SelectionChanged -= Items_SelectionChanged;
            view.CharacterReceived -= Root_CharacterReceived;
            if (_stackPopoverPreviewKeyHandler is not null)
            {
                view.RemoveHandler(
                    UIElement.PreviewKeyDownEvent,
                    _stackPopoverPreviewKeyHandler);
            }
            view.ItemsSource = null;
        }

        // The collection is intentionally retained during ordinary light
        // dismisses, but a lifecycle release must drop every member reference
        // before the cached native tree is detached.
        _stackPopoverItems.Clear();

        if (_stackPopoverSurface is { } surface)
        {
            surface.DragOver -= StackSurface_DragOver;
            surface.DragLeave -= StackSurface_DragLeave;
            surface.Drop -= StackSurface_Drop;
            surface.DataContext = null;
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

        StackPopoverInlineRenameWindow? titleEditorWindow =
            _stackPopoverTitleEditorWindow;
        if (_stackPopoverCloseButton is not null)
        {
            _stackPopoverCloseButton.Click -= StackPopoverCloseButton_Click;
        }
        _stackPopoverTextShadowManager?.Dispose();
        _stackPopoverTextShadowManager = null;
        popup.Child = null;
        popup.SystemBackdrop = null;
        _stackPopoverMaterialBackdrop = null;
        _stackPopoverNeutralBackdrop = null;
        ResetBoxSelectionState();
        _stackPopoverPopup = null;
        _stackPopoverItemsView = null;
        _stackPopoverSurface = null;
        _stackPopoverTitleHost = null;
        _stackPopoverTitleText = null;
        _stackPopoverCloseButton = null;
        _stackPopoverTitleEditor = null;
        _stackPopoverTitleEditorWindow = null;
        _stackPopoverEmptyText = null;
        _stackPopoverTextShadowHost = null;
        _stackPopoverReorderOverlay = null;
        _stackPopoverReorderIndicator = null;
        _stackPopoverSelectionOverlay = null;
        _stackPopoverSelectionRectangle = null;
        _stackPopoverSelectionHost = null;
        _stackPopoverLayout = null;
        _stackPopoverReorderInsertionIndex = -1;
        _stackPopoverPreviewKeyHandler = null;
        _stackPopoverSelectionPointerPressedHandler = null;
        _stackPopoverSelectionPointerMovedHandler = null;
        _stackPopoverSelectionPointerReleasedHandler = null;
        _stackPopoverSelectionPointerCaptureLostHandler = null;
        _stackPopoverSurfacePointerPressedHandler = null;
        _stackPopoverTitleEditing = false;
        _stackPopoverTitleCommitInProgress = false;
        _stackPopoverTitleOriginalName = null;
        titleEditorWindow?.CloseEditorWindow();
        _stackPopoverMembers = [];
        _stackPopoverKey = null;
        _stackPopoverPopupOpen = false;
        _stackPopoverPopupClosing = false;
        _stackPopoverContextMenuOpen = false;
        _stackPopoverDragActive = false;
        _stackPopoverCleanupPending = false;
        _pendingStackPopoverKey = null;
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
            _stackPopoverPopup is { } popup)
        {
            ClearStackPopoverContentForReuse(popup);
            ScheduleStackPopoverCacheRelease();
            QueuePendingStackPopoverShowAfterClose();
        }
    }

    private void CompleteStackPopoverDrag()
    {
        HideStackPopoverReorderIndicator();
        _stackPopoverDragActive = false;
        if (_stackPopoverCleanupPending &&
            !_stackPopoverContextMenuOpen &&
            _stackPopoverPopup is { } popup)
        {
            ClearStackPopoverContentForReuse(popup);
            ScheduleStackPopoverCacheRelease();
            QueuePendingStackPopoverShowAfterClose();
        }
    }

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

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!TryGetCurrentStackPopoverDrag(
                payload,
                out _,
                out _))
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer
            .DataPackageOperation.Link;
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
        if (sender is not ListViewBase view)
        {
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
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
        bool reordered = false;
        ApplyStackProjectionChange(() =>
            reordered = ViewModel.MoveStackMembersForReorder(
                sourceStack.StackKey,
                items,
                insertionIndex));
        if (reordered)
        {
            UpdateStackPopoverKeyAfterReorder(items);
        }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer
            .DataPackageOperation.Link;
        HideStackPopoverReorderIndicator();
        PersistSurfaceReorder();
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

    private int ResolveStackPopoverMemberInsertionIndex(
        ListViewBase view,
        Windows.Foundation.Point position)
    {
        int visibleInsertionIndex = ReorderDropIndexCalculator.Compute(
            view,
            position,
            _stackPopoverReorderInsertionIndex);
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

    private void QueueStackPopoverReconciliation(
        string? expectedStackKey = null,
        IReadOnlyList<string>? memberAnchorPaths = null)
    {
        if (_stackPopoverPopup is not { } expectedPopup ||
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
            if (!ReferenceEquals(_stackPopoverPopup, expectedPopup))
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
                            _stackPopoverPopup,
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
        if (_stackPopoverPopup is null ||
            _stackPopoverKey is not { } stackKey)
        {
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
        if (_stackPopoverPopup is not { } popup ||
            _stackPopoverItemsView is not { } itemsView ||
            _stackPopoverSurface is not { } surface)
        {
            return;
        }

        StackPopoverLayout layout = CalculateStackPopoverLayout(
            stack.Members.Count);
        _stackPopoverLayout = layout;
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
        UpdateStackPopoverScrollPolicy(itemsView.Items.Count);

        if (FindStackSurface(stack.StackKey) is not { } anchor)
        {
            return;
        }

        StackPopoverPosition position = ResolveStackPopoverPosition(
            anchor,
            layout.Width,
            layout.Height);
        popup.HorizontalOffset = position.Left;
        popup.VerticalOffset = position.Top;
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
}
