using DeskBox.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

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
            new PropertyMetadata(true));

    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private TranslateTransform? _rightButtonsTransform;

    public event EventHandler<RoutedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RightTappedRoutedEventArgs>? TitleRightTapped;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerReleased;

    public WidgetShell()
    {
        InitializeComponent();
        RightActionButtons.SizeChanged += (_, _) =>
        {
            _rightButtonsTransform = RightActionButtons.RenderTransform as TranslateTransform;
        };
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
    public StackPanel RightActionButtonHost => RightActionButtons;
    public Button MoreActionButton => MoreButton;
    public Button CloseActionButton => CloseButton;
    public FontIcon MoreActionIcon => MoreButtonIcon;
    public FontIcon CloseActionIcon => CloseButtonIcon;

    public void SetContent(IWidgetContent content)
    {
        ShellContent = content.View;
    }

    /// <summary>
    /// Keeps legacy dynamic title sizing centralized on the shell while host windows are migrated.
    /// </summary>
    public void SetTitleBarRowHeight(GridLength height)
    {
        ShellRoot.RowDefinitions[0].Height = height;
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
        if (!ShowHoverButtons)
        {
            return;
        }

        EnsureStoryboards();
        _hideButtonsStoryboard?.Stop();
        _showButtonsStoryboard?.Begin();
    }

    private void ShellRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        EnsureStoryboards();
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Begin();
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

    private void UpdateTitleBarContentVisibility()
    {
        bool hasCustomTitleBar = TitleBarContent is not null;
        CustomTitleBarContentPresenter.Visibility = hasCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        DefaultTitleBarContentHost.Visibility = hasCustomTitleBar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MoreRequested?.Invoke(this, e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, e);
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
}
