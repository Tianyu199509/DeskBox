using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls;

public sealed partial class WidgetInlineEditor : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(WidgetInlineEditor),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(WidgetInlineEditor),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CancelTextProperty =
        DependencyProperty.Register(
            nameof(CancelText),
            typeof(string),
            typeof(WidgetInlineEditor),
            new PropertyMetadata("Cancel"));

    public static readonly DependencyProperty SaveTextProperty =
        DependencyProperty.Register(
            nameof(SaveText),
            typeof(string),
            typeof(WidgetInlineEditor),
            new PropertyMetadata("Save"));

    public static readonly DependencyProperty TitleFontSizeProperty =
        DependencyProperty.Register(
            nameof(TitleFontSize),
            typeof(double),
            typeof(WidgetInlineEditor),
            new PropertyMetadata(13.0));

    public static readonly DependencyProperty EditorFontSizeProperty =
        DependencyProperty.Register(
            nameof(EditorFontSize),
            typeof(double),
            typeof(WidgetInlineEditor),
            new PropertyMetadata(13.0));

    public static readonly DependencyProperty CommandFontSizeProperty =
        DependencyProperty.Register(
            nameof(CommandFontSize),
            typeof(double),
            typeof(WidgetInlineEditor),
            new PropertyMetadata(12.0));

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    public event EventHandler<RoutedEventArgs>? CancelRequested;

    public event EventHandler<KeyRoutedEventArgs>? EditorKeyDown;

    public WidgetInlineEditor()
    {
        InitializeComponent();
        EditorTextBoxElement.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(EditorTextBox_KeyDown),
            handledEventsToo: true);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string CancelText
    {
        get => (string)GetValue(CancelTextProperty);
        set => SetValue(CancelTextProperty, value);
    }

    public string SaveText
    {
        get => (string)GetValue(SaveTextProperty);
        set => SetValue(SaveTextProperty, value);
    }

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public double CommandFontSize
    {
        get => (double)GetValue(CommandFontSizeProperty);
        set => SetValue(CommandFontSizeProperty, value);
    }

    public Border OverlaySurface => OverlayRoot;

    public TextBlock TitleTextBlock => TitleTextBlockElement;

    public TextBox EditorTextBox => EditorTextBoxElement;

    public Button CloseButton => CloseButtonElement;

    public Button CancelButton => CancelButtonElement;

    public Button SaveButton => SaveButtonElement;

    public void FocusEditor(bool selectAll = false, bool moveCaretToEnd = false)
    {
        EditorTextBox.Focus(FocusState.Programmatic);
        if (selectAll)
        {
            EditorTextBox.SelectAll();
            return;
        }

        if (moveCaretToEnd)
        {
            EditorTextBox.Select(EditorTextBox.Text.Length, 0);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveRequested?.Invoke(this, e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, e);
    }

    private void EditorTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        EditorKeyDown?.Invoke(this, e);
    }
}
