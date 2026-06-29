using DeskBox.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public sealed partial class WidgetShell : UserControl
{
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

    public event EventHandler<RoutedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RightTappedRoutedEventArgs>? TitleRightTapped;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerReleased;

    public WidgetShell()
    {
        InitializeComponent();
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

    public void SetTitleBarRowHeight(GridLength height)
    {
        ShellRoot.RowDefinitions[0].Height = height;
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
