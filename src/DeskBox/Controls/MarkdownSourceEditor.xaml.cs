using System.Text.RegularExpressions;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DeskBox.Controls;

/// <summary>
/// Lossless Markdown source editor. Formatting commands mutate the selected
/// range instead of replacing the whole document, preserving native undo,
/// caret, selection, and viewport state.
/// </summary>
public sealed partial class MarkdownSourceEditor : UserControl
{
    private const int LargeDocumentThreshold = MarkdownDocumentService.MaxCharacters;
    private const double FormattingButtonWidth = 28;
    private const double FormattingButtonSpacing = 1;

    private bool _isSynchronizingText;
    private bool _isApplyingTextLimit;
    private bool _isTextLimitUpdateQueued;
    private EditorViewportSnapshot? _lastEditorViewport;
    private EditorViewportSnapshot? _pendingCommandViewport;
    private Func<string, string>? _textResolver;
    private int _textRevision;
    private bool _isTextCompositionActive;
    private bool _isEditorPointerActive;
    private bool _visualHandlersReleased;
    private readonly PointerEventHandler _toolbarPointerPressedHandler;
    private readonly KeyEventHandler _editorPreviewKeyDownHandler;
    private readonly PointerEventHandler _editorPointerPressedHandler;
    private readonly PointerEventHandler _editorPointerReleasedHandler;
    private readonly TappedEventHandler _editorTappedHandler;
    private readonly KeyEventHandler _editorKeyUpHandler;

    public MarkdownSourceEditor()
    {
        InitializeComponent();
        _toolbarPointerPressedHandler = new PointerEventHandler(FormattingToolbar_PointerPressed);
        _editorPreviewKeyDownHandler = new KeyEventHandler(EditorTextBox_PreviewKeyDown);
        _editorPointerPressedHandler = new PointerEventHandler(EditorTextBox_PointerPressed);
        _editorPointerReleasedHandler = new PointerEventHandler(EditorTextBox_PointerReleased);
        _editorTappedHandler = new TappedEventHandler(EditorTextBox_Tapped);
        _editorKeyUpHandler = new KeyEventHandler(EditorTextBox_KeyUp);

        FormattingToolbar.AddHandler(
            PointerPressedEvent,
            _toolbarPointerPressedHandler,
            handledEventsToo: true);
        FormattingMenuPanel.AddHandler(
            PointerPressedEvent,
            _toolbarPointerPressedHandler,
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            PreviewKeyDownEvent,
            _editorPreviewKeyDownHandler,
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            PointerPressedEvent,
            _editorPointerPressedHandler,
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            PointerReleasedEvent,
            _editorPointerReleasedHandler,
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            TappedEvent,
            _editorTappedHandler,
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            KeyUpEvent,
            _editorKeyUpHandler,
            handledEventsToo: true);
        EditorTextBox.TextCompositionStarted += EditorTextBox_TextCompositionStarted;
        EditorTextBox.TextCompositionEnded += EditorTextBox_TextCompositionEnded;
        InitializeSmartEditing();
        ApplyToolbarVisibility();
        ApplyLocalizedText();
    }

    private void EditorTextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _isTextCompositionActive = true;
    }

    private void EditorTextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isTextCompositionActive = false;
        QueueEditorViewportCapture();
    }

    internal void ReleaseVisualHandlers()
    {
        if (_visualHandlersReleased)
        {
            return;
        }

        _visualHandlersReleased = true;
        _textResolver = null;
        FormattingMoreFlyout?.Hide();
        ReleaseSmartEditingHandlers();
        EditorLayoutRoot.SizeChanged -= EditorLayoutRoot_SizeChanged;
        FormattingToolbar.RemoveHandler(PointerPressedEvent, _toolbarPointerPressedHandler);
        FormattingMenuPanel.RemoveHandler(PointerPressedEvent, _toolbarPointerPressedHandler);
        EditorTextBox.RemoveHandler(PreviewKeyDownEvent, _editorPreviewKeyDownHandler);
        EditorTextBox.RemoveHandler(PointerPressedEvent, _editorPointerPressedHandler);
        EditorTextBox.RemoveHandler(PointerReleasedEvent, _editorPointerReleasedHandler);
        EditorTextBox.RemoveHandler(TappedEvent, _editorTappedHandler);
        EditorTextBox.RemoveHandler(KeyUpEvent, _editorKeyUpHandler);
        EditorTextBox.SelectionChanged -= EditorTextBox_SelectionChanged;
        EditorTextBox.BeforeTextChanging -= EditorTextBox_BeforeTextChanging;
        EditorTextBox.TextChanged -= EditorTextBox_TextChanged;
        EditorTextBox.TextCompositionStarted -= EditorTextBox_TextCompositionStarted;
        EditorTextBox.TextCompositionEnded -= EditorTextBox_TextCompositionEnded;
        Bindings.StopTracking();
        Content = null;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata("Write with Markdown…"));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly),
        typeof(bool),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(false));

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize),
        typeof(double),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(14.0));

    public static readonly DependencyProperty ShowFormattingToolbarProperty = DependencyProperty.Register(
        nameof(ShowFormattingToolbar),
        typeof(bool),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(true, OnToolbarVisibilityChanged));

    public static readonly DependencyProperty EditorEnterBehaviorProperty = DependencyProperty.Register(
        nameof(EditorEnterBehavior),
        typeof(string),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(SettingsService.EditorEnterBehaviorCtrlEnterSaves));

    public static readonly DependencyProperty EnableCtrlSSaveShortcutProperty = DependencyProperty.Register(
        nameof(EnableCtrlSSaveShortcut),
        typeof(bool),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(false));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public bool ShowFormattingToolbar
    {
        get => (bool)GetValue(ShowFormattingToolbarProperty);
        set => SetValue(ShowFormattingToolbarProperty, value);
    }

    public string EditorEnterBehavior
    {
        get => (string)GetValue(EditorEnterBehaviorProperty);
        set => SetValue(EditorEnterBehaviorProperty, value);
    }

    public bool EnableCtrlSSaveShortcut
    {
        get => (bool)GetValue(EnableCtrlSSaveShortcutProperty);
        set => SetValue(EnableCtrlSSaveShortcutProperty, value);
    }

    public Func<string, string>? TextResolver
    {
        get => _textResolver;
        set
        {
            _textResolver = value;
            ApplyLocalizedText();
        }
    }

    public TextBox SourceTextBox => EditorTextBox;

    public event EventHandler? EditorTextChanged;

    public event EventHandler? TextTruncated;

    public event EventHandler? CommitRequested;

    public event EventHandler? CancelRequested;

    public void FocusEditor(bool selectAll = false, bool moveCaretToEnd = false)
    {
        EditorTextBox.Focus(FocusState.Programmatic);
        if (selectAll)
        {
            EditorTextBox.SelectAll();
        }
        else if (moveCaretToEnd)
        {
            EditorTextBox.Select(EditorTextBox.Text.Length, 0);
        }

        RememberEditorViewport();
    }

    public bool FindNext(string? value, StringComparison comparison = StringComparison.CurrentCultureIgnoreCase)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int start = Math.Min(
            EditorTextBox.Text.Length,
            EditorTextBox.SelectionStart + EditorTextBox.SelectionLength);
        int index = EditorTextBox.Text.IndexOf(value, start, comparison);
        if (index < 0)
        {
            index = EditorTextBox.Text.IndexOf(value, comparison);
        }

        if (index < 0)
        {
            return false;
        }

        EditorTextBox.Focus(FocusState.Programmatic);
        EditorTextBox.Select(index, value.Length);
        RememberEditorViewport();
        return true;
    }

    public void ApplyFormat(string action)
    {
        if (IsReadOnly || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        if (!TryResolveCommand(action, out MarkdownEditCommand command))
        {
            return;
        }

        EditorViewportSnapshot viewport = PrepareEditorCommandViewport();
        if (!MarkdownEditCommandEngine.TryCreateEdit(
                EditorTextBox.Text,
                EditorTextBox.SelectionStart,
                EditorTextBox.SelectionLength,
                command,
                out MarkdownTextEdit edit))
        {
            return;
        }

        ApplyTextEdit(edit);
        RestoreEditorViewport(viewport);
    }

    private static bool TryResolveCommand(string action, out MarkdownEditCommand command)
    {
        command = action switch
        {
            "Bold" => MarkdownEditCommand.Bold,
            "Italic" => MarkdownEditCommand.Italic,
            "Strike" => MarkdownEditCommand.Strikethrough,
            "Code" => MarkdownEditCommand.Code,
            "Link" => MarkdownEditCommand.Link,
            "Heading" => MarkdownEditCommand.Heading,
            "List" => MarkdownEditCommand.List,
            "Task" => MarkdownEditCommand.Task,
            "Quote" => MarkdownEditCommand.Quote,
            "Table" => MarkdownEditCommand.Table,
            _ => default
        };
        return action is "Bold" or "Italic" or "Strike" or "Code" or "Link" or
            "Heading" or "List" or "Task" or "Quote" or "Table";
    }

    private static void OnTextPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var editor = (MarkdownSourceEditor)sender;
        string value = args.NewValue as string ?? string.Empty;
        editor.UpdateLargeDocumentBehavior(value.Length);
        if (editor.EditorTextBox.Text == value)
        {
            return;
        }

        editor._isSynchronizingText = true;
        try
        {
            editor._textRevision++;
            editor._lastEditorViewport = null;
            editor._pendingCommandViewport = null;
            editor.EditorTextBox.Text = value;
        }
        finally
        {
            editor._isSynchronizingText = false;
        }
    }

    private static void OnToolbarVisibilityChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownSourceEditor)sender).ApplyToolbarVisibility();

    private void ApplyToolbarVisibility() =>
        SetToolbarVisibility();

    private void SetToolbarVisibility()
    {
        FormattingToolbar.Visibility = ShowFormattingToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ShowFormattingToolbar)
        {
            UpdateFormattingToolbar(EditorLayoutRoot.ActualWidth);
        }
    }

    private void EditorLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShowFormattingToolbar)
        {
            UpdateFormattingToolbar(e.NewSize.Width);
        }
    }

    private void UpdateFormattingToolbar(double availableWidth)
    {
        Button[] toolbarButtons =
        [
            BoldButton,
            ItalicButton,
            StrikeButton,
            HeadingButton,
            ListButton,
            TaskButton,
            LinkButton,
            QuoteButton,
            CodeButton,
            TableButton
        ];
        Button[] menuButtons =
        [
            BoldMenuButton,
            ItalicMenuButton,
            StrikeMenuButton,
            HeadingMenuButton,
            ListMenuButton,
            TaskMenuButton,
            LinkMenuButton,
            QuoteMenuButton,
            CodeMenuButton,
            TableMenuButton
        ];

        int visibleCount = CalculateVisibleToolbarCommandCount(
            availableWidth,
            toolbarButtons.Length);
        for (int index = 0; index < toolbarButtons.Length; index++)
        {
            bool isInline = index < visibleCount;
            toolbarButtons[index].Visibility = isInline
                ? Visibility.Visible
                : Visibility.Collapsed;
            menuButtons[index].Visibility = isInline
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        bool hasOverflow = visibleCount < toolbarButtons.Length;
        FormattingMoreButton.Visibility = hasOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasOverflow)
        {
            FormattingMoreFlyout.Hide();
        }
    }

    internal static int CalculateVisibleToolbarCommandCount(
        double availableWidth,
        int commandCount)
    {
        if (availableWidth <= 0 || commandCount <= 0)
        {
            return 0;
        }

        double fullWidth = commandCount * FormattingButtonWidth +
                           (commandCount - 1) * FormattingButtonSpacing;
        if (availableWidth >= fullWidth)
        {
            return commandCount;
        }

        double inlineWidth = availableWidth - FormattingButtonWidth;
        int visibleCount = (int)Math.Floor(
            inlineWidth / (FormattingButtonWidth + FormattingButtonSpacing));
        return Math.Clamp(visibleCount, 0, commandCount - 1);
    }

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            FormattingMoreFlyout.Hide();
            ApplyFormat(action);
        }
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLargeDocumentBehavior(EditorTextBox.Text.Length);
        HideSelectionBubble();
        if (_isSynchronizingText)
        {
            return;
        }

        _textRevision++;
        SetValue(TextProperty, EditorTextBox.Text);
        EditorTextChanged?.Invoke(this, EventArgs.Empty);
        QueueEditorViewportCapture();
    }

    private void EditorTextBox_BeforeTextChanging(
        TextBox sender,
        TextBoxBeforeTextChangingEventArgs args)
    {
        if (_isApplyingTextLimit ||
            args.NewText.Length <= MarkdownDocumentService.MaxCharacters ||
            ContainsInlineDataImage(args.NewText))
        {
            return;
        }

        args.Cancel = true;
        if (_isTextLimitUpdateQueued)
        {
            return;
        }

        _isTextLimitUpdateQueued = true;
        string truncated = TruncateText(args.NewText);
        DispatcherQueue.TryEnqueue(() =>
        {
            _isTextLimitUpdateQueued = false;
            if (_visualHandlersReleased)
            {
                return;
            }

            _isApplyingTextLimit = true;
            try
            {
                EditorTextBox.Text = truncated;
                EditorTextBox.Select(truncated.Length, 0);
            }
            finally
            {
                _isApplyingTextLimit = false;
            }

            TextTruncated?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string TruncateText(string text)
    {
        int length = MarkdownDocumentService.MaxCharacters;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return text[..length];
    }

    private static bool ContainsInlineDataImage(string text) =>
        text.Contains(
            "data:image/",
            StringComparison.OrdinalIgnoreCase);

    private void EditorTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (EditorTextBox.SelectionLength <= 0)
        {
            HideSelectionBubble();
        }

        if (_isEditorPointerActive)
        {
            RememberEditorViewport();
        }
    }

    private void EditorTextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isEditorPointerActive = true;
        QueueEditorViewportCapture();
    }

    private void EditorTextBox_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RememberEditorViewport();
        _isEditorPointerActive = false;
    }

    private void EditorTextBox_Tapped(object sender, TappedRoutedEventArgs e)
    {
        RememberEditorViewport();
        _isEditorPointerActive = false;
    }

    private void EditorTextBox_KeyUp(object sender, KeyRoutedEventArgs e) =>
        RememberEditorViewport();

    private void QueueEditorViewportCapture()
    {
        if (_visualHandlersReleased || EditorTextBox.Text.Length > LargeDocumentThreshold)
        {
            return;
        }

        int revision = _textRevision;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_visualHandlersReleased)
                {
                    return;
                }

                if (revision == _textRevision &&
                    EditorTextBox.FocusState != FocusState.Unfocused)
                {
                    RememberEditorViewport();
                }
            });
    }

    private void RememberEditorViewport()
    {
        if (EditorTextBox.Text.Length > LargeDocumentThreshold)
        {
            return;
        }

        _lastEditorViewport = CaptureEditorViewport();
    }

    private void UpdateLargeDocumentBehavior(int textLength)
    {
        bool isLargeDocument = textLength > LargeDocumentThreshold;
        EditorTextBox.IsSpellCheckEnabled = !isLargeDocument;
        if (isLargeDocument)
        {
            _lastEditorViewport = null;
            _pendingCommandViewport = null;
        }
    }

    private void FormattingToolbar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsReadOnly)
        {
            return;
        }

        // The editor pointer session locks the last user selection before the
        // toolbar can alter TextBox focus/selection. Use the live value only as
        // a fallback when no snapshot exists for the current text revision.
        EditorViewportSnapshot current = CaptureEditorViewport();
        _pendingCommandViewport =
            _lastEditorViewport is { } previous && previous.TextRevision == _textRevision
                ? previous with
                {
                    HorizontalOffset = current.HorizontalOffset,
                    VerticalOffset = current.VerticalOffset
                }
                : current;
    }

    private void EditorTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        if (_isTextCompositionActive)
        {
            return;
        }

        if (EnableCtrlSSaveShortcut &&
            TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
                e.Key,
                control,
                shift))
        {
            e.Handled = true;
            CommitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (TryHandleExtraFormattingShortcut(e.Key, control, shift))
        {
            e.Handled = true;
            return;
        }

        if (control && e.Key is VirtualKey.B or VirtualKey.I or VirtualKey.K)
        {
            e.Handled = true;
            ApplyFormat(e.Key switch
            {
                VirtualKey.B => "Bold",
                VirtualKey.I => "Italic",
                _ => "Link"
            });
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            SettingsService.ShouldSubmitEditorOnEnter(EditorEnterBehavior, control))
        {
            e.Handled = true;
            CommitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideSelectionBubble();
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        if (ShowFormattingToolbar && e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            IndentSelection(outdent: shift);
            return;
        }

        if (ShowFormattingToolbar && !control && !shift &&
            e.Key == VirtualKey.Enter && TryContinueMarkdownList())
        {
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private EditorViewportSnapshot PrepareEditorCommandViewport()
    {
        EditorViewportSnapshot snapshot =
            _pendingCommandViewport is { } pending && pending.TextRevision == _textRevision
                ? pending
                : _lastEditorViewport is { } previous && previous.TextRevision == _textRevision
                    ? previous
                    : CaptureEditorViewport();
        _pendingCommandViewport = null;

        int start = Math.Clamp(snapshot.SelectionStart, 0, EditorTextBox.Text.Length);
        int length = Math.Clamp(
            snapshot.SelectionLength,
            0,
            EditorTextBox.Text.Length - start);
        EditorTextBox.Select(start, length);
        return snapshot with { SelectionStart = start, SelectionLength = length };
    }

    private EditorViewportSnapshot CaptureEditorViewport()
    {
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(EditorTextBox);
        return new EditorViewportSnapshot(
            EditorTextBox.SelectionStart,
            EditorTextBox.SelectionLength,
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0,
            _textRevision);
    }

    private void RestoreEditorViewport(EditorViewportSnapshot viewport)
    {
        int start = Math.Clamp(EditorTextBox.SelectionStart, 0, EditorTextBox.Text.Length);
        int length = Math.Clamp(
            EditorTextBox.SelectionLength,
            0,
            EditorTextBox.Text.Length - start);

        EditorTextBox.Focus(FocusState.Programmatic);
        EditorTextBox.Select(start, length);
        RestoreScrollOffset();
        _lastEditorViewport = viewport with
        {
            SelectionStart = start,
            SelectionLength = length,
            TextRevision = _textRevision
        };

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            RestoreScrollOffset);

        void RestoreScrollOffset()
        {
            if (_visualHandlersReleased)
            {
                return;
            }

            FindDescendant<ScrollViewer>(EditorTextBox)?.ChangeView(
                viewport.HorizontalOffset,
                viewport.VerticalOffset,
                null,
                disableAnimation: true);
        }
    }

    private void ApplyTextEdit(MarkdownTextEdit edit)
    {
        ReplaceTextRange(edit.Start, edit.Length, edit.Replacement);
        EditorTextBox.Select(edit.SelectionStart, edit.SelectionLength);
    }

    private void ReplaceTextRange(int start, int length, string replacement)
    {
        start = Math.Clamp(start, 0, EditorTextBox.Text.Length);
        length = Math.Clamp(length, 0, EditorTextBox.Text.Length - start);
        EditorTextBox.Select(start, length);
        EditorTextBox.SelectedText = replacement;
    }

    private void IndentSelection(bool outdent)
    {
        MarkdownEditCommand command = outdent
            ? MarkdownEditCommand.Outdent
            : MarkdownEditCommand.Indent;
        if (MarkdownEditCommandEngine.TryCreateEdit(
                EditorTextBox.Text,
                EditorTextBox.SelectionStart,
                EditorTextBox.SelectionLength,
                command,
                out MarkdownTextEdit edit))
        {
            ApplyTextEdit(edit);
        }
    }

    private bool TryContinueMarkdownList()
    {
        int selectionStart = EditorTextBox.SelectionStart;
        int selectionLength = EditorTextBox.SelectionLength;
        string text = EditorTextBox.Text;
        int lineStart = FindEditorLineStart(text, selectionStart);
        string line = text[lineStart..selectionStart];
        Match match = MarkdownListLineRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(match.Groups["content"].Value))
        {
            string newline = DetectEditorNewline(text);
            ReplaceTextRange(lineStart, selectionStart + selectionLength - lineStart, newline);
            EditorTextBox.Select(lineStart + newline.Length, 0);
            return true;
        }

        string marker = match.Groups["marker"].Value;
        if (int.TryParse(match.Groups["number"].Value, out int number))
        {
            marker = $"{number + 1}{match.Groups["punctuation"].Value}";
        }

        string continuation = DetectEditorNewline(text) +
                              match.Groups["indent"].Value +
                              marker + " " +
                              match.Groups["task"].Value;
        ReplaceTextRange(selectionStart, selectionLength, continuation);
        EditorTextBox.Select(selectionStart + continuation.Length, 0);
        return true;
    }

    private static int FindEditorLineStart(string text, int position)
    {
        for (int index = Math.Min(position, text.Length) - 1; index >= 0; index--)
        {
            if (text[index] is '\r' or '\n')
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string DetectEditorNewline(string text)
    {
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        return text.Contains('\r') ? "\r" : Environment.NewLine;
    }

    private void ApplyLocalizedText()
    {
        SetButtonText(BoldButton, "Markdown.Editor.Bold", "Bold");
        SetButtonText(ItalicButton, "Markdown.Editor.Italic", "Italic");
        SetButtonText(HeadingButton, "Markdown.Editor.Heading", "Heading");
        SetButtonText(StrikeButton, "Markdown.Editor.Strikethrough", "Strikethrough");
        SetButtonText(ListButton, "Markdown.Editor.List", "List");
        SetButtonText(TaskButton, "Markdown.Editor.Task", "Task list");
        SetButtonText(LinkButton, "Markdown.Editor.Link", "Link");
        SetButtonText(QuoteButton, "Markdown.Editor.Quote", "Quote");
        SetButtonText(CodeButton, "Markdown.Editor.Code", "Code");
        SetButtonText(TableButton, "Markdown.Editor.Table", "Table");
        SetButtonText(FormattingMoreButton, "Common.More", "More");
        SetButtonText(BoldMenuButton, BoldMenuButtonText, "Markdown.Editor.Bold", "Bold");
        SetButtonText(ItalicMenuButton, ItalicMenuButtonText, "Markdown.Editor.Italic", "Italic");
        SetButtonText(StrikeMenuButton, StrikeMenuButtonText, "Markdown.Editor.Strikethrough", "Strikethrough");
        SetButtonText(HeadingMenuButton, HeadingMenuButtonText, "Markdown.Editor.Heading", "Heading");
        SetButtonText(ListMenuButton, ListMenuButtonText, "Markdown.Editor.List", "List");
        SetButtonText(TaskMenuButton, TaskMenuButtonText, "Markdown.Editor.Task", "Task list");
        SetButtonText(LinkMenuButton, LinkMenuButtonText, "Markdown.Editor.Link", "Link");
        SetButtonText(QuoteMenuButton, QuoteMenuButtonText, "Markdown.Editor.Quote", "Quote");
        SetButtonText(CodeMenuButton, CodeMenuButtonText, "Markdown.Editor.Code", "Code");
        SetButtonText(TableMenuButton, TableMenuButtonText, "Markdown.Editor.Table", "Table");

        AutomationProperties.SetName(
            FormattingToolbar,
            T("Markdown.Editor.Toolbar", "Formatting"));
        AutomationProperties.SetName(
            EditorTextBox,
            T("Markdown.Editor.Source", "Markdown source editor"));
    }

    private void SetButtonText(Button button, string key, string fallback)
    {
        string text = T(key, fallback);
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    private void SetButtonText(
        Button button,
        TextBlock label,
        string key,
        string fallback)
    {
        string text = T(key, fallback);
        label.Text = text;
        AutomationProperties.SetName(button, text);
    }

    private string T(string key, string fallback)
    {
        if (_textResolver is null)
        {
            return fallback;
        }

        string value = _textResolver(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?:(?<number>\d+)(?<punctuation>[.)])|(?<marker>[-+*]))\s+(?<task>\[[ xX]\]\s+)?(?<content>.*)$")]
    private static partial Regex MarkdownListLineRegex();

    private readonly record struct EditorViewportSnapshot(
        int SelectionStart,
        int SelectionLength,
        double HorizontalOffset,
        double VerticalOffset,
        int TextRevision);
}
