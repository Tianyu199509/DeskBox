using DeskBox.Contracts;
using DeskBox.Services;
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
            new PropertyMetadata("\uE8A5"));

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
    private bool _isDragHandlePressed;
    private GridLength _titleBarRowHeight = new(46);
    private Thickness _titleBarPadding = new(14, 7, 12, 5);

    public event EventHandler<RoutedEventArgs>? AddRequested;
    public event EventHandler<RoutedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
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
    public FontIcon TitleIconElement => TitleIcon;
    public TextBlock TitleTextElement => TitleText;
    public ContentPresenter TitleEditorPresenterElement => TitleEditorPresenter;
    public StackPanel RightActionButtonHost => RightActionButtons;
    public StackPanel TitleIdentityHostElement => TitleIdentityHost;
    public ContentPresenter ShellContentPresenterElement => ShellContentPresenter;
    public Button AddActionButton => AddButton;
    public Button MoreActionButton => MoreButton;
    public Button CloseActionButton => CloseButton;
    public FontIcon AddActionIcon => AddButtonIcon;
    public FontIcon MoreActionIcon => MoreButtonIcon;
    public FontIcon CloseActionIcon => CloseButtonIcon;
    public FrameworkElement DragHandleElement => OverlayDragHandle;

    public bool IsOverlayChromeMode => ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

    public void SetContent(IWidgetContent content)
    {
        ShellContent = content.View;
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

        foreach (var button in new[] { AddButton, MoreButton, CloseButton })
        {
            button.Background = background;
            button.BorderBrush = border;
            button.BorderThickness = thickness;
        }
    }

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

    private void OverlayDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragHandlePressed = true;
        OverlayDragHandle.CapturePointer(e.Pointer);
        SetOverlayChromeVisible(true);
        DragHandlePointerPressed?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        DragHandlePointerMoved?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        DragHandlePointerReleased?.Invoke(this, e);
        EndDragHandlePress(e.Pointer);
    }

    private void OverlayDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDragHandlePress(e.Pointer);
    }

    private void EndDragHandlePress(Pointer pointer)
    {
        if (!_isDragHandlePressed)
        {
            return;
        }

        _isDragHandlePressed = false;
        OverlayDragHandle.ReleasePointerCapture(pointer);
        SetOverlayChromeVisible(_isPointerOverShell);
    }
}
