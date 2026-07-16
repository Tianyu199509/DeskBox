using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace DeskBox.Controls;

public sealed partial class WidgetShell : UserControl
{
    /// <summary>
    /// Content hosted below the title area. Future widget kinds should provide their body through this slot.
    /// </summary>
    public static readonly DependencyProperty ShellContentProperty =
        DependencyProperty.Register(
            nameof(ShellContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TitleGlyphProperty =
        DependencyProperty.Register(
            nameof(TitleGlyph),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata("\uE8A5", OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconModeProperty =
        DependencyProperty.Register(
            nameof(TitleIconMode),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetTitleIconModeNames.Color, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconKindProperty =
        DependencyProperty.Register(
            nameof(TitleIconKind),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetTitleIconKindNames.Default, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconAccentColorProperty =
        DependencyProperty.Register(
            nameof(TitleIconAccentColor),
            typeof(Color),
            typeof(WidgetShell),
            new PropertyMetadata(AccentColorHelper.DefaultAccentColor, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty OverlayTitleProperty =
        DependencyProperty.Register(
            nameof(OverlayTitle),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(string.Empty, OnOverlayTitleChanged));

    /// <summary>
    /// Optional title bar override used by legacy windows while they migrate into the shared shell.
    /// When set, the built-in title and action buttons are hidden.
    /// </summary>
    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.Register(
            nameof(TitleBarContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnTitleBarContentChanged));

    public static readonly DependencyProperty ShowHoverButtonsProperty =
        DependencyProperty.Register(
            nameof(ShowHoverButtons),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(true, OnShowHoverButtonsChanged));

    public static readonly DependencyProperty ShowAddButtonProperty =
        DependencyProperty.Register(
            nameof(ShowAddButton),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(false, OnShowAddButtonChanged));

    public static readonly DependencyProperty ChromeModeProperty =
        DependencyProperty.Register(
            nameof(ChromeMode),
            typeof(WidgetChromeMode),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetChromeMode.Standard, OnChromeModeChanged));

    public static readonly DependencyProperty IsTitleEditableProperty =
        DependencyProperty.Register(
            nameof(IsTitleEditable),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(false));

    public static readonly DependencyProperty TitleEditorContentProperty =
        DependencyProperty.Register(
            nameof(TitleEditorContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnTitleEditorContentChanged));

    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private TranslateTransform? _rightButtonsTransform;
    private bool _isPointerOverShell;
    private bool _isCollapsed;
    private bool _isMinimalCompactStyle;
    private bool _isCompactKeyboardFocused;
    private bool _isCompactTransitionActive;
    private bool _isDragHandlePressed;
    private bool _isCompactPressCandidate;
    private bool _hasCompactPressMoved;
    private Windows.Foundation.Point _compactPressPoint;
    private double _compactOuterCornerRadius = 16;
    private double _compactInnerCornerRadius = 8;
    private double _compactMediaCornerRadius = 8;
    private double _expandedOuterCornerRadius = 8;
    private double _transitionOuterCornerRadiusFrom = 8;
    private double _transitionOuterCornerRadiusTo = 8;
    private GridLength _titleBarRowHeight = new(46);
    private Thickness _titleBarPadding = new(14, 7, 12, 5);

    public event EventHandler<RoutedEventArgs>? AddRequested;
    public event EventHandler<RoutedEventArgs>? PositionLockRequested;
    public event EventHandler<RoutedEventArgs>? SizeLockRequested;
    public event EventHandler<RoutedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? CollapseRequested;
    public event EventHandler<RoutedEventArgs>? ExpandRequested;
    public event EventHandler<RoutedEventArgs>? CompactPreviousRequested;
    public event EventHandler<RoutedEventArgs>? CompactPlayPauseRequested;
    public event EventHandler<RoutedEventArgs>? CompactNextRequested;
    public event EventHandler? CompactPointerEntered;
    public event EventHandler? CompactPointerExited;
    public event EventHandler? CompactPointerPressed;
    public event EventHandler? ExpandedInteractionRequested;
    public event EventHandler? CompactDragEntered;
    public event EventHandler? CompactDragLeft;
    public event EventHandler? CompactDropCompleted;
    public event EventHandler<DoubleTappedRoutedEventArgs>? TitleDoubleTapped;
    public event EventHandler<RightTappedRoutedEventArgs>? TitleRightTapped;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerReleased;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerReleased;

    public WidgetShell()
    {
        InitializeComponent();
        CompactTitleIcon.SetCompactPresentationMode(true);
        ShellRoot.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ShellRoot_DragEnter), true);
        ShellRoot.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(ShellRoot_DragLeave), true);
        ShellRoot.AddHandler(UIElement.DropEvent, new DragEventHandler(ShellRoot_Drop), true);
        ShellRoot.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ShellRoot_PointerPressed),
            true);
        RightActionButtons.SizeChanged += (_, _) =>
        {
            _rightButtonsTransform = RightActionButtons.RenderTransform as TranslateTransform;
        };
        Loaded += (_, _) => ApplyChromeMode();
    }

    public bool ShowHoverButtons
    {
        get => (bool)GetValue(ShowHoverButtonsProperty);
        set => SetValue(ShowHoverButtonsProperty, value);
    }

    public object? ShellContent
    {
        get => GetValue(ShellContentProperty);
        set => SetValue(ShellContentProperty, value);
    }

    public string TitleGlyph
    {
        get => (string)GetValue(TitleGlyphProperty);
        set => SetValue(TitleGlyphProperty, value);
    }

    public string TitleIconMode
    {
        get => (string)GetValue(TitleIconModeProperty);
        set => SetValue(TitleIconModeProperty, value);
    }

    public string TitleIconKind
    {
        get => (string)GetValue(TitleIconKindProperty);
        set => SetValue(TitleIconKindProperty, value);
    }

    public Color TitleIconAccentColor
    {
        get => (Color)GetValue(TitleIconAccentColorProperty);
        set => SetValue(TitleIconAccentColorProperty, value);
    }

    public string OverlayTitle
    {
        get => (string)GetValue(OverlayTitleProperty);
        set => SetValue(OverlayTitleProperty, value);
    }

    public bool ShowAddButton
    {
        get => (bool)GetValue(ShowAddButtonProperty);
        set => SetValue(ShowAddButtonProperty, value);
    }

    public WidgetChromeMode ChromeMode
    {
        get => (WidgetChromeMode)GetValue(ChromeModeProperty);
        set => SetValue(ChromeModeProperty, value);
    }

    public bool IsTitleEditable
    {
        get => (bool)GetValue(IsTitleEditableProperty);
        set => SetValue(IsTitleEditableProperty, value);
    }

    public object? TitleEditorContent
    {
        get => GetValue(TitleEditorContentProperty);
        set => SetValue(TitleEditorContentProperty, value);
    }

    public Visibility AddButtonVisibility => ShowAddButton ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Custom title bar content for migrated legacy widgets that still own title interactions.
    /// New simple widget kinds should prefer the default title bar.
    /// </summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public Grid TitleBar => TitleBarGrid;
    public Border BackgroundSurface => BackgroundPlate;
    public Border Divider => HeaderDivider;
    public WidgetTitleIcon TitleIconElement => TitleIcon;
    public TextBlock TitleTextElement => TitleText;
    public ContentPresenter TitleEditorPresenterElement => TitleEditorPresenter;
    public StackPanel RightActionButtonHost => RightActionButtons;
    public StackPanel TitleIdentityHostElement => TitleIdentityHost;
    public ContentPresenter ShellContentPresenterElement => ShellContentPresenter;
    public Button PositionLockActionButton => PositionLockButton;
    public Button SizeLockActionButton => SizeLockButton;
    public Button AddActionButton => AddButton;
    public Button CollapseActionButton => CollapseButton;
    public Button CompactExpandActionButton => CompactExpandButton;
    public Button MoreActionButton => MoreButton;
    public Button CloseActionButton => CloseButton;
    public FrameworkElement PositionLockActionIcon => PositionLockButtonIcon;
    public FrameworkElement PositionLockFilledActionIcon => PositionLockButtonFilledIcon;
    public FrameworkElement SizeLockActionIcon => SizeLockButtonIcon;
    public FrameworkElement SizeLockFilledActionIcon => SizeLockButtonFilledIcon;
    public FrameworkElement AddActionIcon => AddButtonIcon;
    public FrameworkElement MoreActionIcon => MoreButtonIcon;
    public FrameworkElement CloseActionIcon => CloseButtonIcon;
    public FrameworkElement DragHandleElement => _isCollapsed ? CollapsedChromeLayer : OverlayDragHandle;

    public bool IsOverlayChromeMode => ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

    public bool IsCollapsed => _isCollapsed;

    public void SetContent(IWidgetContent content)
    {
        ShellContent = content.View;
    }

    public void SetCollapsed(bool collapsed, string collapsedStyle)
    {
        ResetCompactTransitionVisuals();
        _isCollapsed = collapsed;
        _isMinimalCompactStyle = string.Equals(
            collapsedStyle,
            SettingsService.WidgetCollapsedStyleMinimal,
            StringComparison.Ordinal);
        ApplyCompactTextVisibility();
        ApplyChromeMode();
        ApplyCompactActionVisibility(animate: false);
    }

    public bool PrepareCompactTransition(
        bool collapsed,
        double expandedOuterRadius,
        double compactOuterRadius,
        double compactInnerRadius,
        double compactMediaRadius)
    {
        if (_isCollapsed == collapsed)
        {
            return false;
        }

        _expandedOuterCornerRadius = Math.Max(0, expandedOuterRadius);
        _compactOuterCornerRadius = Math.Max(0, compactOuterRadius);
        _compactInnerCornerRadius = Math.Max(0, compactInnerRadius);
        _compactMediaCornerRadius = Math.Max(0, compactMediaRadius);
        _transitionOuterCornerRadiusFrom = collapsed
            ? _expandedOuterCornerRadius
            : _compactOuterCornerRadius;
        _transitionOuterCornerRadiusTo = collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius;
        _isCompactTransitionActive = true;
        if (!collapsed)
        {
            _isCollapsed = false;
            ApplyChromeMode();
        }

        CollapsedChromeLayer.Visibility = Visibility.Visible;
        CollapsedChromeLayer.IsHitTestVisible = false;
        CollapsedChromeLayer.Opacity = collapsed ? 0 : 1;
        TitleBarGrid.Opacity = collapsed ? 1 : 0;
        ShellContentPresenter.Opacity = collapsed ? 1 : 0;
        ApplyCompactInnerCornerRadii();
        SetBackgroundCornerRadius(_transitionOuterCornerRadiusFrom);
        return true;
    }

    public void SetCompactTransitionProgress(bool collapsed, double progress)
    {
        if (!_isCompactTransitionActive)
        {
            return;
        }

        double value = Math.Clamp(progress, 0, 1);
        double compactOpacity;
        double expandedOpacity;
        if (collapsed)
        {
            expandedOpacity = 1 - SmoothStep(Math.Clamp(value / 0.62, 0, 1));
            compactOpacity = SmoothStep(Math.Clamp((value - 0.36) / 0.64, 0, 1));
        }
        else
        {
            compactOpacity = 1 - SmoothStep(Math.Clamp(value / 0.46, 0, 1));
            expandedOpacity = SmoothStep(Math.Clamp((value - 0.28) / 0.72, 0, 1));
        }

        CollapsedChromeLayer.Opacity = compactOpacity;
        TitleBarGrid.Opacity = expandedOpacity;
        ShellContentPresenter.Opacity = expandedOpacity;
        SetBackgroundCornerRadius(Lerp(
            _transitionOuterCornerRadiusFrom,
            _transitionOuterCornerRadiusTo,
            value));
    }

    private static double SmoothStep(double value) => value * value * (3 - (2 * value));

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0, 1));

    public void CompleteCompactTransition(bool collapsed, string collapsedStyle)
    {
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        ResetCompactTransitionVisuals();
        SetCollapsed(collapsed, collapsedStyle);
    }

    public void CancelCompactTransition()
    {
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(_isCollapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        ResetCompactTransitionVisuals();
        ApplyChromeMode();
    }

    private void ResetCompactTransitionVisuals()
    {
        TitleBarGrid.Opacity = 1;
        ShellContentPresenter.Opacity = 1;
        CollapsedChromeLayer.Opacity = 1;
        CollapsedChromeLayer.IsHitTestVisible = true;
    }

    public void SetCompactPresentation(WidgetCompactPresentation presentation)
    {
        CompactTitleText.Text = presentation.Title;
        CompactSummaryText.Text = presentation.Summary;
        CompactTitleIcon.Glyph = presentation.Glyph;
        CompactTitleIcon.LabelText = presentation.Title;
        CompactThumbnail.Source = presentation.Thumbnail;
        CompactThumbnailHost.Visibility = presentation.Thumbnail is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompactTitleIcon.Visibility = presentation.Thumbnail is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        Visibility mediaVisibility = presentation.ShowMediaControls
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactPreviousButton.Visibility = mediaVisibility;
        CompactPlayPauseButton.Visibility = mediaVisibility;
        CompactNextButton.Visibility = mediaVisibility;
        CompactPreviousButton.IsEnabled = presentation.CanGoPrevious;
        CompactNextButton.IsEnabled = presentation.CanGoNext;
        CompactPlayPauseIcon.Glyph = presentation.IsPlaying ? "\uE769" : "\uE102";
        ApplyCompactTextVisibility();
    }

    public void NotifyCompactDragMoved()
    {
        if (_isCompactPressCandidate)
        {
            _hasCompactPressMoved = true;
        }
    }

    public void SetCompactCornerRadii(double outerRadius, double innerRadius, double mediaRadius)
    {
        _compactOuterCornerRadius = Math.Max(0, outerRadius);
        _compactInnerCornerRadius = Math.Max(0, innerRadius);
        _compactMediaCornerRadius = Math.Max(0, mediaRadius);
        ApplyCompactCornerRadii();
    }

    private void ApplyCompactCornerRadii()
    {
        SetBackgroundCornerRadius(_compactOuterCornerRadius);
        ApplyCompactInnerCornerRadii();
    }

    private void ApplyCompactInnerCornerRadii()
    {
        CompactThumbnailHost.CornerRadius = new CornerRadius(_compactMediaCornerRadius);
        CompactTitleIcon.SetSurfaceCornerRadiusOverride(_compactMediaCornerRadius);

        foreach (var button in new[]
        {
            CompactPreviousButton,
            CompactPlayPauseButton,
            CompactNextButton,
            CompactExpandButton
        })
        {
            button.CornerRadius = new CornerRadius(_compactInnerCornerRadius);
        }
    }

    private void SetBackgroundCornerRadius(double radius) =>
        BackgroundPlate.CornerRadius = new CornerRadius(Math.Max(0, radius));

    private void ApplyCompactTextVisibility()
    {
        bool showSummary = !_isMinimalCompactStyle && !string.IsNullOrWhiteSpace(CompactSummaryText.Text);
        CompactSummaryText.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        CompactTextSeparator.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetCollapseActionAvailable(bool available)
    {
        CollapseButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Keeps legacy dynamic title sizing centralized on the shell while host windows are migrated.
    /// </summary>
    public void SetTitleBarRowHeight(GridLength height)
    {
        _titleBarRowHeight = height;
        ApplyChromeMode();
    }

    public void SetTitleBarPadding(Thickness padding)
    {
        _titleBarPadding = padding;
        ApplyChromeMode();
    }

    /// <summary>
    /// Allows migrated windows to preserve their existing divider alignment during the transition.
    /// </summary>
    public void SetDividerMargin(Thickness margin)
    {
        HeaderDivider.Margin = margin;
    }

    private void ShellRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverShell = true;
        CompactPointerEntered?.Invoke(this, EventArgs.Empty);
        if (_isCollapsed)
        {
            ApplyCompactActionVisibility();
            return;
        }
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

        if (usesOverlay)
        {
            SetOverlayChromeVisible(true);
            return;
        }

        ApplyActionButtonVisibility();
    }

    private void ShellRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverShell = false;
        CompactPointerExited?.Invoke(this, EventArgs.Empty);
        if (_isCollapsed)
        {
            ApplyCompactActionVisibility();
            return;
        }
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

        if (usesOverlay)
        {
            SetOverlayChromeVisible(false);
            return;
        }

        ApplyActionButtonVisibility();
    }

    private void EnsureStoryboards()
    {
        if (_showButtonsStoryboard is not null)
        {
            return;
        }

        _rightButtonsTransform = new TranslateTransform { X = 12 };
        RightActionButtons.RenderTransform = _rightButtonsTransform;

        _showButtonsStoryboard = new Storyboard();

        var showOpacity = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(showOpacity, "Opacity");
        _showButtonsStoryboard.Children.Add(showOpacity);

        var showX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showX, _rightButtonsTransform);
        Storyboard.SetTargetProperty(showX, "X");
        _showButtonsStoryboard.Children.Add(showX);

        _hideButtonsStoryboard = new Storyboard();

        var hideOpacity = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(hideOpacity, "Opacity");
        _hideButtonsStoryboard.Children.Add(hideOpacity);

        var hideX = new DoubleAnimation
        {
            To = 12,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideX, _rightButtonsTransform);
        Storyboard.SetTargetProperty(hideX, "X");
        _hideButtonsStoryboard.Children.Add(hideX);
    }

    private static void OnTitleBarContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.UpdateTitleBarContentVisibility();
        }
    }

    private static void OnOverlayTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnTitleIconAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnShowAddButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnShowHoverButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.ApplyChromeMode();
        }
    }

    private static void OnChromeModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.ApplyChromeMode();
        }
    }

    private static void OnTitleEditorContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.UpdateTitleEditorVisibility();
        }
    }

    private void UpdateTitleBarContentVisibility()
    {
        bool hasCustomTitleBar = TitleBarContent is not null;
        CustomTitleBarContentPresenter.Visibility = hasCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        DefaultTitleBarContentHost.Visibility = hasCustomTitleBar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyChromeMode()
    {
        if (ShellRoot.RowDefinitions.Count < 2)
        {
            return;
        }

        if (_isCollapsed)
        {
            ShellRoot.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            ShellRoot.RowDefinitions[1].Height = new GridLength(0);
            TitleBarGrid.Visibility = Visibility.Collapsed;
            HeaderDivider.Visibility = Visibility.Collapsed;
            ShellContentPresenter.Visibility = Visibility.Collapsed;
            OverlayChromeLayer.Visibility = Visibility.Collapsed;
            CollapsedChromeLayer.Visibility = Visibility.Visible;
            return;
        }

        CollapsedChromeLayer.Visibility = Visibility.Collapsed;
        ShellContentPresenter.Visibility = Visibility.Visible;
        ShellRoot.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;
        bool isOverlay = ChromeMode == WidgetChromeMode.Overlay;
        bool isEditingTitle = TitleEditorContent is not null;

        ShellRoot.RowDefinitions[0].Height = usesOverlay
            ? new GridLength(0)
            : _titleBarRowHeight;
        BackgroundPlate.Margin = new Thickness(0);
        Grid.SetRow(TitleBarGrid, usesOverlay ? 1 : 0);
        Canvas.SetZIndex(TitleBarGrid, usesOverlay ? 40 : 2);
        Canvas.SetZIndex(ShellContentPresenter, 1);
        TitleBarGrid.HorizontalAlignment = usesOverlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TitleBarGrid.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        TitleBarGrid.Margin = usesOverlay ? new Thickness(0, -2, 6, 0) : new Thickness(0);
        TitleBarGrid.Padding = usesOverlay ? new Thickness(2, 0, 0, 0) : _titleBarPadding;
        RightActionButtons.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Center;
        TitleBarGrid.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;

        HeaderDivider.Visibility = usesOverlay ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ShellContentPresenter, usesOverlay ? 0 : 1);
        Grid.SetRowSpan(ShellContentPresenter, usesOverlay ? 2 : 1);
        ShellContentPresenter.Margin = new Thickness(0);
        TitleIdentityHost.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;
        OverlayChromeLayer.Visibility = isOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
        OverlayIdentityHost.Visibility = Visibility.Collapsed;
        OverlayDragHandle.Visibility = isOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;

        if (usesOverlay)
        {
            RightActionButtons.Opacity = 0;
            RightActionButtons.IsHitTestVisible = false;
        }
        else
        {
            ApplyActionButtonVisibility();
        }

        SetOverlayChromeVisible(_isPointerOverShell, animateButtons: false);
        ApplyActionButtonSurface(false);
    }

    private void ApplyActionButtonVisibility()
    {
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Stop();
        RightActionButtons.Opacity = ShowHoverButtons ? 1 : 0;
        RightActionButtons.IsHitTestVisible = ShowHoverButtons;
        if (_rightButtonsTransform is not null)
        {
            _rightButtonsTransform.X = ShowHoverButtons ? 0 : 12;
        }
    }

    private void SetOverlayChromeVisible(bool isVisible, bool animateButtons = true)
    {
        bool isEditingTitle = TitleEditorContent is not null;
        bool showHandle = ChromeMode == WidgetChromeMode.Overlay && !isEditingTitle && (isVisible || _isDragHandlePressed);

        OverlayIdentityHost.Opacity = 0;
        OverlayDragHandle.Opacity = showHandle ? 1 : 0;
        OverlayDragHandle.IsHitTestVisible = showHandle;

        if (!animateButtons)
        {
            if (ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden)
            {
                RightActionButtons.Opacity = 0;
            }
        }
    }

    private void ApplyActionButtonSurface(bool isOverlay)
    {
        var background = isOverlay ? CreateOpaqueOverlayButtonBackground() : new SolidColorBrush(Colors.Transparent);
        var border = isOverlay ? CreateOpaqueOverlayButtonBorder() : new SolidColorBrush(Colors.Transparent);
        var thickness = isOverlay ? new Thickness(0.8) : new Thickness(0);

        foreach (var button in new[] { PositionLockButton, SizeLockButton, AddButton, CollapseButton, MoreButton, CloseButton })
        {
            button.Background = background;
            button.BorderBrush = border;
            button.BorderThickness = thickness;
        }
    }

    private void ApplyCompactActionVisibility(bool animate = true)
    {
        bool visible = _isCollapsed && (_isPointerOverShell || _isCompactKeyboardFocused);
        CompactActionHost.IsHitTestVisible = visible;
        if (!animate)
        {
            CompactActionHost.Opacity = visible ? 1 : 0;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(visible ? 150 : 120)),
            EasingFunction = new CubicEase
            {
                EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactActionHost);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, e);

    private void CompactExpandButton_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, e);

    private void CompactPreviousButton_Click(object sender, RoutedEventArgs e) => CompactPreviousRequested?.Invoke(this, e);

    private void CompactPlayPauseButton_Click(object sender, RoutedEventArgs e) => CompactPlayPauseRequested?.Invoke(this, e);

    private void CompactNextButton_Click(object sender, RoutedEventArgs e) => CompactNextRequested?.Invoke(this, e);

    private void CollapsedChromeLayer_GotFocus(object sender, RoutedEventArgs e)
    {
        _isCompactKeyboardFocused = true;
        ApplyCompactActionVisibility();
    }

    private void CollapsedChromeLayer_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCompactKeyboardFocused = false;
        ApplyCompactActionVisibility();
    }

    private void ShellRoot_DragEnter(object sender, DragEventArgs e) => CompactDragEntered?.Invoke(this, EventArgs.Empty);

    private void ShellRoot_DragLeave(object sender, DragEventArgs e) => CompactDragLeft?.Invoke(this, EventArgs.Empty);

    private void ShellRoot_Drop(object sender, DragEventArgs e) => CompactDropCompleted?.Invoke(this, EventArgs.Empty);

    private Brush CreateOpaqueOverlayButtonBackground()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x2C, 0x2F, 0x36)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private Brush CreateOpaqueOverlayButtonBorder()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void UpdateTitleEditorVisibility()
    {
        bool isEditingTitle = TitleEditorContent is not null;
        TitleEditorPresenter.Visibility = isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = isEditingTitle ? Visibility.Collapsed : Visibility.Visible;
        ApplyChromeMode();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddRequested?.Invoke(this, e);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        PositionLockRequested?.Invoke(this, e);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SizeLockRequested?.Invoke(this, e);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MoreRequested?.Invoke(this, e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, e);
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TitleDoubleTapped?.Invoke(this, e);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        TitleRightTapped?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerPressed?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerMoved?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerReleased?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // When pointer capture is lost mid-drag (e.g., alt-tab, UAC),
        // notify the parent window so it can call EndWindowDragCore.
        TitlePointerReleased?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCollapsed && e.OriginalSource is DependencyObject source && IsWithin(source, CompactActionHost))
        {
            return;
        }

        if (_isCollapsed)
        {
            CompactPointerPressed?.Invoke(this, EventArgs.Empty);
            _isCompactPressCandidate = true;
            _hasCompactPressMoved = false;
            _compactPressPoint = e.GetCurrentPoint(CollapsedChromeLayer).Position;
        }

        _isDragHandlePressed = true;
        DragHandleElement.CapturePointer(e.Pointer);
        SetOverlayChromeVisible(true);
        DragHandlePointerPressed?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isCompactPressCandidate && !_hasCompactPressMoved)
        {
            Windows.Foundation.Point current = e.GetCurrentPoint(CollapsedChromeLayer).Position;
            double deltaX = current.X - _compactPressPoint.X;
            double deltaY = current.Y - _compactPressPoint.Y;
            _hasCompactPressMoved = (deltaX * deltaX) + (deltaY * deltaY) >= 25;
        }

        DragHandlePointerMoved?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool invokeCompact = _isCompactPressCandidate && !_hasCompactPressMoved;
        DragHandlePointerReleased?.Invoke(this, e);
        EndDragHandlePress(e.Pointer);
        if (invokeCompact)
        {
            ExpandRequested?.Invoke(this, e);
        }
    }

    private void OverlayDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // When pointer capture is lost mid-drag (e.g., alt-tab, UAC),
        // notify the parent window so it can call EndWindowDragCore.
        DragHandlePointerReleased?.Invoke(this, e);
        _isCompactPressCandidate = false;
        _hasCompactPressMoved = false;
        EndDragHandlePress(e.Pointer);
    }

    private void EndDragHandlePress(Pointer pointer)
    {
        if (!_isDragHandlePressed)
        {
            return;
        }

        _isDragHandlePressed = false;
        _isCompactPressCandidate = false;
        _hasCompactPressMoved = false;
        DragHandleElement.ReleasePointerCapture(pointer);
        SetOverlayChromeVisible(_isPointerOverShell);
    }

    private void ShellRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCollapsed || !e.GetCurrentPoint(ShellRoot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ExpandedInteractionRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }
}
