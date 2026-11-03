using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace DeskBox.Controls;

public sealed class MarkdownClipboardPasteEventArgs(DataPackageView dataView) : EventArgs
{
    public DataPackageView DataView { get; } = dataView;

    public bool Handled { get; set; }

    public PasteDeferral GetDeferral() => default;

    public readonly struct PasteDeferral
    {
        public void Complete()
        {
        }
    }
}

public sealed partial class MarkdownSourceEditor
{
    private Popup? _selectionBubblePopup;
    private Point _lastPointerPositionInEditor;
    private bool _hasPointerPositionInEditor;
    private ScrollViewer? _monitoredEditorScrollViewer;
    private PointerEventHandler? _smartPointerReleasedHandler;
    private KeyEventHandler? _smartKeyUpHandler;

    public event EventHandler<MarkdownClipboardPasteEventArgs>? RichPasteRequested;

    public static bool TryFormatSmartUrlPaste(
        string? selectedText,
        string? pastedText,
        out string markdownLink)
    {
        markdownLink = string.Empty;
        if (string.IsNullOrWhiteSpace(selectedText) || string.IsNullOrWhiteSpace(pastedText))
        {
            return false;
        }

        // Single-line selection only, and skip if selection is already a URL or Markdown link.
        if (selectedText.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
        {
            return false;
        }

        string trimmedSelection = selectedText.Trim();
        if (Uri.TryCreate(trimmedSelection, UriKind.Absolute, out Uri? existingUri) &&
            (existingUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             existingUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string trimmedPaste = pastedText.Trim();
        if (trimmedPaste.IndexOfAny(['\r', '\n', ' ', '\t']) >= 0)
        {
            return false;
        }

        if (!Uri.TryCreate(trimmedPaste, UriKind.Absolute, out Uri? pastedUri) ||
            (!pastedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !pastedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string escapedUrl = pastedUri.AbsoluteUri
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
        markdownLink = $"[{selectedText}]({escapedUrl})";
        return true;
    }

    private void InitializeSmartEditing()
    {
        FormattingMoreFlyout.ShouldConstrainToRootBounds = false;
        _smartPointerReleasedHandler = SmartEditor_PointerReleased;
        _smartKeyUpHandler = SmartEditor_KeyUp;
        EditorTextBox.AddHandler(PointerReleasedEvent, _smartPointerReleasedHandler, handledEventsToo: true);
        EditorTextBox.AddHandler(KeyUpEvent, _smartKeyUpHandler, handledEventsToo: true);
        EditorTextBox.Paste += EditorTextBox_Paste;
        EditorTextBox.LostFocus += EditorTextBox_LostFocus;
        Unloaded += MarkdownSourceEditor_Unloaded;
    }

    private void ReleaseSmartEditingHandlers()
    {
        HideSelectionBubble();
        if (_selectionBubblePopup is not null)
        {
            _selectionBubblePopup.Child = null;
            _selectionBubblePopup = null;
        }

        if (_monitoredEditorScrollViewer is not null)
        {
            _monitoredEditorScrollViewer.ViewChanged -= EditorScrollViewer_ViewChanged;
            _monitoredEditorScrollViewer = null;
        }

        if (_smartPointerReleasedHandler is not null)
        {
            EditorTextBox.RemoveHandler(PointerReleasedEvent, _smartPointerReleasedHandler);
            _smartPointerReleasedHandler = null;
        }

        if (_smartKeyUpHandler is not null)
        {
            EditorTextBox.RemoveHandler(KeyUpEvent, _smartKeyUpHandler);
            _smartKeyUpHandler = null;
        }

        EditorTextBox.Paste -= EditorTextBox_Paste;
        EditorTextBox.LostFocus -= EditorTextBox_LostFocus;
        Unloaded -= MarkdownSourceEditor_Unloaded;
    }

    private void MarkdownSourceEditor_Unloaded(object sender, RoutedEventArgs e)
    {
        HideSelectionBubble();
    }

    private void EditorTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_visualHandlersReleased || EditorTextBox.FocusState == FocusState.Unfocused)
            {
                HideSelectionBubble();
            }
        });
    }

    private void EditorScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        HideSelectionBubble();
    }

    private void SmartEditor_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RecordEditorPointerPosition(e);
        DispatcherQueue.TryEnqueue(() => UpdateSelectionBubbleAfterPointerOrKey(fromPointer: true));
    }

    private void SmartEditor_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
        {
            UpdateSelectionBubbleAfterPointerOrKey(fromPointer: false);
        }
    }

    private void RecordEditorPointerPosition(PointerRoutedEventArgs e)
    {
        try
        {
            _lastPointerPositionInEditor = e.GetCurrentPoint(this).Position;
            _hasPointerPositionInEditor = true;
        }
        catch
        {
            _hasPointerPositionInEditor = false;
        }
    }

    private bool TryHandleExtraFormattingShortcut(VirtualKey key, bool control, bool shift)
    {
        if (!control || IsReadOnly || !ShowFormattingToolbar)
        {
            return false;
        }

        if (shift && key == VirtualKey.X)
        {
            ApplyFormat("Strike");
            return true;
        }

        if (shift && key == VirtualKey.L)
        {
            ApplyFormat("Task");
            return true;
        }

        if ((shift && key == VirtualKey.C) || (!shift && key == (VirtualKey)192))
        {
            ApplyFormat("Code");
            return true;
        }

        return false;
    }

    private async void EditorTextBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        if (IsReadOnly || _visualHandlersReleased)
        {
            return;
        }

        DataPackageView dataView;
        try
        {
            dataView = Clipboard.GetContent();
        }
        catch
        {
            return;
        }

        // 1.Copied files from Explorer -> forward to host attachment handler.
        if (dataView.Contains(StandardDataFormats.StorageItems) && RichPasteRequested is not null)
        {
            e.Handled = true;
            HideSelectionBubble();
            RichPasteRequested.Invoke(this, new MarkdownClipboardPasteEventArgs(dataView));
            return;
        }

        // 2. Text on clipboard: preserve Office/Excel text paste priority over Bitmap,
        //    and apply smart [selectedText](url) wrapping when text is currently selected.
        if (dataView.Contains(StandardDataFormats.Text))
        {
            int selectionStart = EditorTextBox.SelectionStart;
            int selectionLength = EditorTextBox.SelectionLength;
            if (!ShowFormattingToolbar || selectionLength <= 0)
            {
                return;
            }

            string selectedSnapshot = EditorTextBox.SelectedText;
            if (string.IsNullOrWhiteSpace(selectedSnapshot) ||
                selectedSnapshot.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
            {
                return;
            }

            e.Handled = true;
            HideSelectionBubble();
            try
            {
                string pastedText = await dataView.GetTextAsync();
                if (_visualHandlersReleased)
                {
                    return;
                }

                if (TryFormatSmartUrlPaste(selectedSnapshot, pastedText, out string markdownLink))
                {
                    ReplaceTextRange(selectionStart, selectionLength, markdownLink);
                    EditorTextBox.Select(selectionStart + markdownLink.Length, 0);
                }
                else
                {
                    ReplaceTextRange(selectionStart, selectionLength, pastedText ?? string.Empty);
                    int caret = selectionStart + (pastedText?.Length ?? 0);
                    EditorTextBox.Select(Math.Min(caret, EditorTextBox.Text.Length), 0);
                }
            }
            catch
            {
                // Ignore clipboard read race.
            }

            return;
        }

        // 3. Pure screenshot / image bitmap (Win+Shift+S, Snipaste, WeChat, QQ, browser Copy Image).
        if (RichPasteRequested is not null)
        {
            e.Handled = true;
            HideSelectionBubble();
            RichPasteRequested.Invoke(this, new MarkdownClipboardPasteEventArgs(dataView));
        }
    }

    private void UpdateSelectionBubbleAfterPointerOrKey(bool fromPointer)
    {
        if (_visualHandlersReleased ||
            !ShowFormattingToolbar ||
            IsReadOnly ||
            !IsLoaded ||
            XamlRoot is null ||
            EditorTextBox.SelectionLength <= 0 ||
            string.IsNullOrWhiteSpace(EditorTextBox.SelectedText))
        {
            HideSelectionBubble();
            return;
        }

        EnsureScrollViewerMonitored();
        Popup popup = EnsureSelectionBubblePopup();
        popup.XamlRoot = XamlRoot;
        if (popup.Child is FrameworkElement bubbleRoot)
        {
            bubbleRoot.RequestedTheme = ActualTheme;
            ApplyBubbleThemeBrushes(bubbleRoot);
        }

        try
        {
            GeneralTransform transform = TransformToVisual(null);
            double localX = fromPointer && _hasPointerPositionInEditor
                ? _lastPointerPositionInEditor.X
                : Math.Min(ActualWidth * 0.5, 140);
            double localY = fromPointer && _hasPointerPositionInEditor
                ? _lastPointerPositionInEditor.Y
                : 36;

            Point rootPoint = transform.TransformPoint(new Point(localX, localY));
            double bubbleWidth = 176;
            double bubbleHeight = 34;
            double maxRootX = Math.Max(8, XamlRoot.Size.Width - bubbleWidth - 8);
            double targetX = Math.Clamp(rootPoint.X - (bubbleWidth * 0.5), 8, maxRootX);
            double targetY = rootPoint.Y - bubbleHeight - 10;
            if (targetY < 8)
            {
                targetY = Math.Min(Math.Max(8, XamlRoot.Size.Height - bubbleHeight - 8), rootPoint.Y + 18);
            }

            popup.HorizontalOffset = Math.Round(targetX);
            popup.VerticalOffset = Math.Round(targetY);
            popup.IsOpen = true;
        }
        catch
        {
            HideSelectionBubble();
        }
    }

    private void HideSelectionBubble()
    {
        if (_selectionBubblePopup is { IsOpen: true })
        {
            _selectionBubblePopup.IsOpen = false;
        }
    }

    private void EnsureScrollViewerMonitored()
    {
        if (_monitoredEditorScrollViewer is not null)
        {
            return;
        }

        _monitoredEditorScrollViewer = FindDescendant<ScrollViewer>(EditorTextBox);
        if (_monitoredEditorScrollViewer is not null)
        {
            _monitoredEditorScrollViewer.ViewChanged += EditorScrollViewer_ViewChanged;
        }
    }

    private Popup EnsureSelectionBubblePopup()
    {
        if (_selectionBubblePopup is not null)
        {
            return _selectionBubblePopup;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        row.AddHandler(PointerPressedEvent, _toolbarPointerPressedHandler, handledEventsToo: true);

        row.Children.Add(CreateBubbleButton("Bold", "B", isTextGlyph: true, bold: true, italic: false, "Markdown.Editor.Bold", "Bold"));
        row.Children.Add(CreateBubbleButton("Italic", "I", isTextGlyph: true, bold: false, italic: true, "Markdown.Editor.Italic", "Italic"));
        row.Children.Add(CreateBubbleButton("Strike", "S\u0336", isTextGlyph: true, bold: false, italic: false, "Markdown.Editor.Strikethrough", "Strikethrough"));
        row.Children.Add(CreateBubbleButton("Code", "\uE943", isTextGlyph: false, bold: false, italic: false, "Markdown.Editor.Code", "Code"));
        row.Children.Add(CreateBubbleButton("Link", "\uE71B", isTextGlyph: false, bold: false, italic: false, "Markdown.Editor.Link", "Link"));
        row.Children.Add(CreateBubbleButton("Task", "\uE73A", isTextGlyph: false, bold: false, italic: false, "Markdown.Editor.Task", "Task list"));

        var surface = new Border
        {
            Padding = new Thickness(4, 3, 4, 3),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Child = row
        };
        ApplyBubbleThemeBrushes(surface);

        _selectionBubblePopup = new Popup
        {
            ShouldConstrainToRootBounds = false,
            IsLightDismissEnabled = false,
            Child = surface
        };
        return _selectionBubblePopup;
    }

    private void ApplyBubbleThemeBrushes(FrameworkElement element)
    {
        if (element is not Border border)
        {
            return;
        }

        border.Background = NeutralInteractionBrush.ResolveThemedResource("SolidBackgroundFillColorTertiaryBrush", this)
            ?? NeutralInteractionBrush.ResolveThemedResource("SolidBackgroundFillColorBaseBrush", this)
            ?? new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        border.BorderBrush = NeutralInteractionBrush.ResolveThemedResource("SurfaceStrokeColorFlyoutBrush", this)
            ?? NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this)
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private Button CreateBubbleButton(
        string action,
        string glyph,
        bool isTextGlyph,
        bool bold,
        bool italic,
        string locKey,
        string fallbackLabel)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = isTextGlyph ? 13 : 12,
            IsTextScaleFactorEnabled = false
        };
        if (isTextGlyph)
        {
            icon.FontFamily = new FontFamily(italic ? "Cambria" : "Segoe UI Variable Text");
            if (bold)
            {
                icon.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            if (italic)
            {
                icon.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }
        }

        var button = new Button
        {
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsTabStop = false,
            Tag = action,
            Content = icon
        };
        if (Resources.TryGetValue("MarkdownFormattingButtonStyle", out object? styleObj) && styleObj is Style btnStyle)
        {
            button.Style = btnStyle;
            button.IsTabStop = false;
        }

        string tooltip = T(locKey, fallbackLabel);
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += BubbleFormatButton_Click;
        return button;
    }

    private void BubbleFormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            HideSelectionBubble();
            ApplyFormat(action);
        }
    }
}
