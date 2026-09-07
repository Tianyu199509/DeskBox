using DeskBox.Models;
using DeskBox.Services;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace DeskBox.Controls;

/// <summary>
/// Safe native Markdown reader. All textual blocks share one RichTextBlock so
/// users can select and copy across paragraphs, headings, lists, and tables.
/// </summary>
public sealed partial class MarkdownDocumentView : UserControl
{
    private const double BodyLineHeightRatio = 1.72;
    private const double TaskLineHeightRatio = 2.16;
    private const double HeadingLineHeightRatio = 1.42;
    private const double CodeLineHeightRatio = 1.60;
    private const double ParagraphSpacing = 5;
    private const double ListItemSpacing = 3;
    private const double InternalScrollBarContentClearance = 12;
    private const double EmbeddedBlockMinimumWidth = 160;
    private const double TableColumnMinimumWidth = 96;
    private const int WidthRenderDebounceMilliseconds = 160;
    private const double LightThemeSemanticMinimumContrast = 4.5;
    private static readonly Windows.UI.Color LightMarkdownWorstCaseSurface =
        Windows.UI.Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8);

    private readonly RichTextBlock _documentText = new()
    {
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ScrollViewer _scrollViewer = new();
    private readonly MarkdownDocumentService _markdownService;
    private Brush _contentForeground = new SolidColorBrush(Microsoft.UI.Colors.Black);
    private Brush _semanticForeground = new SolidColorBrush(Microsoft.UI.Colors.Black);
    private Func<string, string?>? _attachmentResolver;
    private int _taskListIndex;
    private bool _isLoaded;
    private bool _renderQueued;
    private bool _renderInvalidated = true;
    private bool _renderInvalidationRequiresRender = true;
    private bool _renderDependsOnWidth;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _widthRenderTimer;
    private double _lastRenderedWidth = double.NaN;
    private MarkdownRenderState _lastRenderedState;
    private bool _hasRenderedState;
    private int _renderParseGeneration;
    private string _renderInvalidationReason = "initial";

    public MarkdownDocumentView()
        : this(new MarkdownDocumentService())
    {
    }

    internal MarkdownDocumentView(MarkdownDocumentService markdownService)
    {
        _markdownService = markdownService;
        _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ApplyContentHost();
        RegisterPropertyChangedCallback(
            FontSizeProperty,
            (_, _) => InvalidateRenderForAppearanceChange("font-size"));
        RegisterPropertyChangedCallback(
            ForegroundProperty,
            (_, _) => InvalidateRenderForAppearanceChange("foreground"));
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueInvalidatedRender());
        ActualThemeChanged += (_, _) => InvalidateRenderForAppearanceChange("theme");
        SizeChanged += (_, args) => QueueWidthDependentRender(args.NewSize.Width);
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            QueueInvalidatedRender();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            ReleaseWidthRenderTimer();
        };
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(string.Empty, OnDocumentPropertyChanged));

    public static readonly DependencyProperty ContentFormatProperty = DependencyProperty.Register(
        nameof(ContentFormat),
        typeof(TextContentFormat),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(TextContentFormat.Markdown, OnDocumentPropertyChanged));

    public static readonly DependencyProperty AllowRemoteImagesProperty = DependencyProperty.Register(
        nameof(AllowRemoteImages),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(false, OnDocumentPropertyChanged));

    public static readonly DependencyProperty AreTaskListsInteractiveProperty = DependencyProperty.Register(
        nameof(AreTaskListsInteractive),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(false, OnDocumentPropertyChanged));

    public static readonly DependencyProperty UseInternalScrollViewerProperty = DependencyProperty.Register(
        nameof(UseInternalScrollViewer),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(true, OnContentHostPropertyChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public TextContentFormat ContentFormat
    {
        get => (TextContentFormat)GetValue(ContentFormatProperty);
        set => SetValue(ContentFormatProperty, value);
    }

    public bool AllowRemoteImages
    {
        get => (bool)GetValue(AllowRemoteImagesProperty);
        set => SetValue(AllowRemoteImagesProperty, value);
    }

    public bool AreTaskListsInteractive
    {
        get => (bool)GetValue(AreTaskListsInteractiveProperty);
        set => SetValue(AreTaskListsInteractiveProperty, value);
    }

    /// <summary>
    /// Controls whether the reader owns its vertical scrolling. Turn this off
    /// when the reader is hosted inside a page-level ScrollViewer.
    /// </summary>
    public bool UseInternalScrollViewer
    {
        get => (bool)GetValue(UseInternalScrollViewerProperty);
        set => SetValue(UseInternalScrollViewerProperty, value);
    }

    public Func<string, string?>? AttachmentResolver
    {
        get => _attachmentResolver;
        set
        {
            if (Equals(_attachmentResolver, value))
            {
                return;
            }

            _attachmentResolver = value;
            InvalidateRender("attachment-resolver");
        }
    }

    public bool WasTruncated { get; private set; }

    public event EventHandler<MarkdownTaskToggleRequestedEventArgs>? TaskToggleRequested;

    public event EventHandler<MarkdownAttachmentRequestedEventArgs>? AttachmentOpenRequested;

    public void Refresh() => InvalidateRender("explicit-refresh");

    private static void OnDocumentPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).InvalidateRender(
            "document-property",
            requiresRender: false);

    private void InvalidateRender(string reason, bool requiresRender = true)
    {
        _renderInvalidationRequiresRender = _renderInvalidated
            ? _renderInvalidationRequiresRender || requiresRender
            : requiresRender;
        _renderInvalidated = true;
        _renderInvalidationReason = reason;
        QueueInvalidatedRender();
    }

    private void InvalidateRenderForAppearanceChange(string reason)
    {
        if (!_hasRenderedState ||
            !GetRenderState().Equals(_lastRenderedState))
        {
            InvalidateRender(reason, requiresRender: false);
        }
    }

    private void QueueInvalidatedRender()
    {
        if (!_renderInvalidated || !_isLoaded || _renderQueued)
        {
            return;
        }

        _renderQueued = true;
        bool queued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _renderQueued = false;
                if (_renderInvalidated &&
                    _isLoaded &&
                    Visibility == Visibility.Visible)
                {
                    if (!_renderInvalidationRequiresRender &&
                        _hasRenderedState &&
                        GetRenderState().Equals(_lastRenderedState))
                    {
                        _renderInvalidated = false;
                        _renderInvalidationRequiresRender = false;
                        return;
                    }

                    Render();
                }
            });
        if (!queued)
        {
            _renderQueued = false;
        }
    }

    private void QueueWidthDependentRender(double width)
    {
        if (!_renderDependsOnWidth ||
            !double.IsFinite(width) ||
            width < EmbeddedBlockMinimumWidth ||
            (double.IsFinite(_lastRenderedWidth) &&
             Math.Abs(width - _lastRenderedWidth) <= 4))
        {
            ReleaseWidthRenderTimer();
            return;
        }

        _widthRenderTimer ??= CreateWidthRenderTimer();
        _widthRenderTimer.Stop();
        _widthRenderTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateWidthRenderTimer()
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(WidthRenderDebounceMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += WidthRenderTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        return timer;
    }

    private void WidthRenderTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        ReleaseWidthRenderTimer();
        if (_renderDependsOnWidth &&
            double.IsFinite(ActualWidth) &&
            ActualWidth >= EmbeddedBlockMinimumWidth &&
            (!double.IsFinite(_lastRenderedWidth) ||
             Math.Abs(ActualWidth - _lastRenderedWidth) > 4))
        {
            InvalidateRender("width");
        }
    }

    private void ReleaseWidthRenderTimer()
    {
        if (_widthRenderTimer is null)
        {
            return;
        }

        _widthRenderTimer.Stop();
        _widthRenderTimer.Tick -= WidthRenderTimer_Tick;
        _widthRenderTimer = null;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private static void OnContentHostPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).ApplyContentHost();

    private void ApplyContentHost()
    {
        _documentText.Margin = UseInternalScrollViewer
            ? new Thickness(0, 0, InternalScrollBarContentClearance, 0)
            : new Thickness(0);

        if (UseInternalScrollViewer)
        {
            if (!ReferenceEquals(_scrollViewer.Content, _documentText))
            {
                _scrollViewer.Content = _documentText;
            }

            Content = _scrollViewer;
            return;
        }

        if (ReferenceEquals(_scrollViewer.Content, _documentText))
        {
            _scrollViewer.Content = null;
        }

        Content = _documentText;
    }

    private void Render()
    {
        PerformanceLogger.RecordMarkdownRender();
        double verticalOffset = _scrollViewer.VerticalOffset;
        _lastRenderedWidth = ActualWidth;
        _renderDependsOnWidth = false;
        string source = Markdown ?? string.Empty;
        PerformanceLogger.Mark(
            "MarkdownRender",
            $"reason={_renderInvalidationReason} " +
            $"length={source.Length} format={ContentFormat} width={ActualWidth:F1}");
        UpdateForegrounds();
        _documentText.Blocks.Clear();
        _taskListIndex = 0;
        WasTruncated = false;
        if (source.Length == 0)
        {
            CompleteRender();
            return;
        }

        if (ContentFormat == TextContentFormat.PlainText)
        {
            var paragraph = CreateParagraph(BaseFontSize, FontWeights.Normal);
            AppendText(paragraph.Inlines, source);
            _documentText.Blocks.Add(paragraph);
            RestoreScrollOffset(source, verticalOffset);
            CompleteRender();
            return;
        }

        // Markdig parsing is pure CPU with no UI dependency; run it off the UI
        // thread so opening a long document does not block interaction.
        int generation = ++_renderParseGeneration;
        _ = RenderParsedMarkdownAsync(source, verticalOffset, generation);
    }

    private async Task RenderParsedMarkdownAsync(
        string source,
        double verticalOffset,
        int generation)
    {
        MarkdownParseResult document;
        try
        {
            document = await Task.Run(() => _markdownService.Parse(source));
        }
        catch (Exception ex)
        {
            App.Log($"[MarkdownDocumentView] Background parse failed: {ex.Message}");
            return;
        }

        if (generation != _renderParseGeneration ||
            !_isLoaded ||
            Visibility != Visibility.Visible)
        {
            return;
        }

        WasTruncated = document.WasTruncated;
        foreach (MdBlock block in document.Document)
        {
            AppendBlock(block, quoteDepth: 0, listDepth: 0);
        }

        RestoreScrollOffset(source, verticalOffset);
        CompleteRender();
        if (_renderInvalidated)
        {
            // An invalidation arrived while parsing; make sure a re-render is
            // queued instead of being swallowed by CompleteRender's flag reset.
            QueueInvalidatedRender();
        }
    }

    private void CompleteRender()
    {
        _lastRenderedState = GetRenderState();
        _hasRenderedState = true;
        _renderInvalidated = false;
        _renderInvalidationRequiresRender = false;
    }

    private MarkdownRenderState GetRenderState() => new(
        Markdown ?? string.Empty,
        ContentFormat,
        AllowRemoteImages,
        AreTaskListsInteractive,
        GetRenderAppearance());

    private MarkdownRenderAppearance GetRenderAppearance()
    {
        bool hasSolidForeground = Foreground is SolidColorBrush;
        Windows.UI.Color foregroundColor = Foreground is SolidColorBrush foreground
            ? foreground.Color
            : default;
        Windows.UI.Color accentColor = Application.Current is App
            {
                ThemeService: { } themeService
            }
            ? themeService.GetEffectiveAccentColor()
            : default;
        return new MarkdownRenderAppearance(
            BaseFontSize,
            UsesDarkTheme,
            hasSolidForeground,
            foregroundColor,
            accentColor);
    }

    private readonly record struct MarkdownRenderAppearance(
        double FontSize,
        bool UsesDarkTheme,
        bool HasSolidForeground,
        Windows.UI.Color ForegroundColor,
        Windows.UI.Color AccentColor);

    private readonly record struct MarkdownRenderState(
        string Markdown,
        TextContentFormat ContentFormat,
        bool AllowRemoteImages,
        bool AreTaskListsInteractive,
        MarkdownRenderAppearance Appearance);

    private void UpdateForegrounds()
    {
        // Body text follows the hosting widget's foreground scope instead of
        // independently choosing black/white from the app theme. Keeping the
        // shared brush instance also makes custom-color changes update existing
        // RichTextBlock runs without rebuilding the document.
        _contentForeground = Foreground ?? BrushResource("TextFillColorPrimaryBrush");
        _semanticForeground = UsesDarkTheme
            ? BrushResource("AccentTextFillColorPrimaryBrush")
            : CreateLightThemeSemanticForeground();
        _documentText.Foreground = _contentForeground;
    }

    private bool UsesDarkTheme => ActualTheme == ElementTheme.Dark ||
        (ActualTheme == ElementTheme.Default &&
         Application.Current?.RequestedTheme == ApplicationTheme.Dark);

    private static Brush CreateLightThemeSemanticForeground()
    {
        Windows.UI.Color accent = Application.Current is App { ThemeService: { } themeService }
            ? themeService.GetEffectiveAccentColor()
            : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
        return new SolidColorBrush(EnsureMinimumContrast(
            accent,
            LightMarkdownWorstCaseSurface,
            LightThemeSemanticMinimumContrast));
    }

    private static Windows.UI.Color EnsureMinimumContrast(
        Windows.UI.Color foreground,
        Windows.UI.Color background,
        double minimumContrast)
    {
        var opaqueForeground = Windows.UI.Color.FromArgb(
            0xFF,
            foreground.R,
            foreground.G,
            foreground.B);
        if (GetContrastRatio(opaqueForeground, background) >= minimumContrast)
        {
            return opaqueForeground;
        }

        for (int step = 1; step <= 20; step++)
        {
            Windows.UI.Color candidate = Blend(opaqueForeground, Microsoft.UI.Colors.Black, step / 20d);
            if (GetContrastRatio(candidate, background) >= minimumContrast)
            {
                return candidate;
            }
        }

        return Microsoft.UI.Colors.Black;
    }

    private static Windows.UI.Color Blend(
        Windows.UI.Color source,
        Windows.UI.Color target,
        double amount) => Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(source.R + (target.R - source.R) * amount),
            (byte)Math.Round(source.G + (target.G - source.G) * amount),
            (byte)Math.Round(source.B + (target.B - source.B) * amount));

    private static double GetContrastRatio(Windows.UI.Color first, Windows.UI.Color second)
    {
        double firstLuminance = GetRelativeLuminance(first);
        double secondLuminance = GetRelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double GetRelativeLuminance(Windows.UI.Color color)
    {
        static double ToLinear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * ToLinear(color.R) +
               0.7152 * ToLinear(color.G) +
               0.0722 * ToLinear(color.B);
    }

    private void RestoreScrollOffset(string renderedSource, double verticalOffset)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (string.Equals(renderedSource, Markdown, StringComparison.Ordinal))
                {
                    _scrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: true);
                }
            });
    }

    private double BaseFontSize => double.IsFinite(FontSize) && FontSize > 0
        ? FontSize
        : 14;

    private void AppendBlock(MdBlock block, int quoteDepth, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendLeafParagraph(
                    heading,
                    heading.Level switch
                    {
                        1 => BaseFontSize * 1.62,
                        2 => BaseFontSize * 1.38,
                        3 => BaseFontSize * 1.20,
                        _ => BaseFontSize * 1.08
                    },
                    FontWeights.SemiBold,
                    quoteDepth,
                    listDepth);
                break;
            case ParagraphBlock paragraph:
                AppendLeafParagraph(
                    paragraph,
                    BaseFontSize,
                    FontWeights.Normal,
                    quoteDepth,
                    listDepth);
                break;
            case QuoteBlock quote:
                foreach (MdBlock child in quote)
                {
                    AppendBlock(child, quoteDepth + 1, listDepth);
                }
                break;
            case ListBlock list:
                AppendList(list, quoteDepth, listDepth);
                break;
            case FencedCodeBlock code:
                AppendCode(code, quoteDepth, listDepth);
                break;
            case CodeBlock code:
                AppendCode(code, quoteDepth, listDepth);
                break;
            case ThematicBreakBlock:
                var separator = CreateParagraph(BaseFontSize, FontWeights.Normal);
                separator.Margin = new Thickness(Indent(quoteDepth, listDepth), 5, 0, 5);
                separator.Inlines.Add(new Run
                {
                    Text = "────────────────",
                    Foreground = BrushResource("DividerStrokeColorDefaultBrush")
                });
                _documentText.Blocks.Add(separator);
                break;
            case Table table:
                AppendTable(table, quoteDepth, listDepth);
                break;
            case HtmlBlock:
                break;
            case ContainerBlock container:
                foreach (MdBlock child in container)
                {
                    AppendBlock(child, quoteDepth, listDepth);
                }
                break;
            case LeafBlock leaf:
                AppendLeafParagraph(
                    leaf,
                    BaseFontSize,
                    FontWeights.Normal,
                    quoteDepth,
                    listDepth);
                break;
        }
    }

    private void AppendLeafParagraph(
        LeafBlock leaf,
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight,
        int quoteDepth,
        int listDepth)
    {
        Paragraph paragraph = CreateParagraph(fontSize, fontWeight);
        ApplyParagraphIndent(paragraph, quoteDepth, listDepth);
        if (fontSize > BaseFontSize * 1.05)
        {
            paragraph.LineHeight = Math.Ceiling(fontSize * HeadingLineHeightRatio);
            paragraph.Margin = new Thickness(paragraph.Margin.Left, 9, 0, 5);
        }
        AppendQuoteMarker(paragraph, quoteDepth);
        if (leaf.Inline is { } container)
        {
            UseInlineContentLineHeightWhenNeeded(paragraph, container);
            AppendContainer(paragraph.Inlines, container);
        }
        else if (leaf is CodeBlock code)
        {
            AppendText(paragraph.Inlines, code.Lines.ToString());
        }

        _documentText.Blocks.Add(paragraph);
    }

    private void AppendList(ListBlock list, int quoteDepth, int listDepth)
    {
        int number = int.TryParse(list.OrderedStart, out int orderedStart) ? orderedStart : 1;
        foreach (ListItemBlock item in list.OfType<ListItemBlock>())
        {
            bool? taskState = FindTaskState(item);
            MdBlock? firstContent = item.FirstOrDefault(child => child is not ListBlock);
            var paragraph = CreateParagraph(BaseFontSize, FontWeights.Normal);
            ApplyParagraphIndent(paragraph, quoteDepth, listDepth);
            paragraph.Margin = new Thickness(
                paragraph.Margin.Left,
                ListItemSpacing,
                0,
                ListItemSpacing);
            AppendQuoteMarker(paragraph, quoteDepth);
            if (taskState is { } isChecked)
            {
                paragraph.LineHeight = Math.Max(
                    paragraph.LineHeight,
                    Math.Ceiling(BaseFontSize * TaskLineHeightRatio));
                paragraph.Inlines.Add(new InlineUIContainer { Child = CreateTaskMarker(isChecked) });
                paragraph.Inlines.Add(new Run { Text = " " });
            }
            else
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = list.IsOrdered ? $"{number++}. " : "• ",
                    Foreground = _semanticForeground
                });
            }

            if (firstContent is LeafBlock leaf && leaf.Inline is { } inline)
            {
                UseInlineContentLineHeightWhenNeeded(paragraph, inline);
                AppendContainer(paragraph.Inlines, inline);
            }
            else if (firstContent is ContainerBlock container)
            {
                UseInlineContentLineHeightWhenNeeded(paragraph, container);
                AppendBlockText(paragraph.Inlines, container);
            }
            _documentText.Blocks.Add(paragraph);

            foreach (MdBlock child in item)
            {
                if (ReferenceEquals(child, firstContent))
                {
                    continue;
                }

                AppendBlock(child, quoteDepth, listDepth + 1);
            }
        }
    }

    private void AppendCode(CodeBlock code, int quoteDepth, int listDepth)
    {
        _renderDependsOnWidth = true;
        var paragraph = CreateParagraph(Math.Max(11, BaseFontSize - 1), FontWeights.Normal);
        paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        paragraph.Margin = new Thickness(Indent(quoteDepth, listDepth), 8, 0, 8);
        AppendQuoteMarker(paragraph, quoteDepth);
        double surfaceWidth = GetEmbeddedBlockWidth(quoteDepth, listDepth);
        var codeText = new TextBlock
        {
            Text = code.Lines.ToString().TrimEnd('\r', '\n'),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = Math.Max(11, BaseFontSize - 1),
            Foreground = _semanticForeground,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };
        var codeSurface = new Border
        {
            Width = surfaceWidth,
            MaxWidth = surfaceWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(9, 7, 9, 7),
            Background = BrushResource("ControlFillColorSecondaryBrush"),
            BorderBrush = BrushResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = codeText
        };
        paragraph.Inlines.Add(new InlineUIContainer { Child = codeSurface });
        _documentText.Blocks.Add(paragraph);
    }

    private void AppendTable(Table table, int quoteDepth, int listDepth)
    {
        TableRow[] rows = table.OfType<TableRow>().ToArray();
        int columnCount = rows.Length == 0
            ? 0
            : rows.Max(row => row.OfType<TableCell>().Count());
        if (columnCount == 0)
        {
            return;
        }

        _renderDependsOnWidth = true;
        double surfaceWidth = GetEmbeddedBlockWidth(quoteDepth, listDepth);
        double tableWidth = Math.Max(surfaceWidth, columnCount * TableColumnMinimumWidth);
        var tableGrid = new Grid
        {
            Width = tableWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            tableGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            TableRow row = rows[rowIndex];
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TableCell[] cells = row.OfType<TableCell>().ToArray();
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var cellText = new RichTextBlock
                {
                    FontSize = Math.Max(11, BaseFontSize - 0.5),
                    Foreground = _contentForeground,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                };
                var cellParagraph = new Paragraph
                {
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    LineHeight = Math.Ceiling(Math.Max(11, BaseFontSize - 0.5) * 1.45),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0)
                };
                if (columnIndex < cells.Length)
                {
                    AppendBlockText(cellParagraph.Inlines, cells[columnIndex]);
                }
                cellText.Blocks.Add(cellParagraph);

                var cellBorder = new Border
                {
                    MinHeight = 28,
                    Padding = new Thickness(6, 4, 6, 4),
                    Background = row.IsHeader
                        ? BrushResource("SubtleFillColorSecondaryBrush")
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderBrush = BrushResource("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(
                        columnIndex == 0 ? 0 : 1,
                        rowIndex == 0 ? 0 : 1,
                        0,
                        0),
                    Child = cellText
                };
                Grid.SetColumn(cellBorder, columnIndex);
                Grid.SetRow(cellBorder, rowIndex);
                tableGrid.Children.Add(cellBorder);
            }
        }

        var horizontalScroller = new ScrollViewer
        {
            Width = surfaceWidth,
            MaxWidth = surfaceWidth,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tableGrid
        };
        var tableSurface = new Border
        {
            Width = surfaceWidth,
            MaxWidth = surfaceWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = BrushResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = horizontalScroller
        };
        var paragraph = CreateParagraph(BaseFontSize, FontWeights.Normal);
        paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        paragraph.Margin = new Thickness(Indent(quoteDepth, listDepth), 7, 0, 7);
        AppendQuoteMarker(paragraph, quoteDepth);
        paragraph.Inlines.Add(new InlineUIContainer { Child = tableSurface });
        _documentText.Blocks.Add(paragraph);
    }

    private double GetEmbeddedBlockWidth(int quoteDepth, int listDepth)
    {
        double availableWidth = double.IsFinite(ActualWidth) && ActualWidth > 0
            ? ActualWidth
            : 320;
        availableWidth -= InternalScrollBarContentClearance;
        availableWidth -= Indent(quoteDepth, listDepth);
        return Math.Max(EmbeddedBlockMinimumWidth, availableWidth);
    }

    private static Paragraph CreateParagraph(
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight) => new()
    {
        FontSize = fontSize,
        FontWeight = fontWeight,
        LineHeight = Math.Ceiling(fontSize * BodyLineHeightRatio),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Margin = new Thickness(0, ParagraphSpacing, 0, ParagraphSpacing)
    };

    private static double Indent(int quoteDepth, int listDepth) =>
        quoteDepth * 10 + listDepth * 18;

    private static void ApplyParagraphIndent(Paragraph paragraph, int quoteDepth, int listDepth) =>
        paragraph.Margin = new Thickness(
            Indent(quoteDepth, listDepth),
            paragraph.Margin.Top,
            0,
            paragraph.Margin.Bottom);

    private static void UseInlineContentLineHeightWhenNeeded(
        Paragraph paragraph,
        ContainerInline container)
    {
        if (ContainsImage(container))
        {
            // BlockLineHeight deliberately keeps ordinary prose on a stable
            // baseline, but it also caps a line containing InlineUIContainer
            // to one text row. A decoded image then paints outside that row and
            // covers every paragraph below it. MaxHeight makes the line reserve
            // the actual image height while preserving the normal paragraph
            // settings for text-only content.
            paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        }
    }

    private static void UseInlineContentLineHeightWhenNeeded(
        Paragraph paragraph,
        ContainerBlock container)
    {
        if (container
            .Descendants<LeafBlock>()
            .Any(leaf => leaf.Inline is { } inline &&
                         ContainsImage(inline)))
        {
            paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        }
    }

    private static bool ContainsImage(ContainerInline container)
    {
        for (MdInline? current = container.FirstChild;
             current is not null;
             current = current.NextSibling)
        {
            if (current is LinkInline { IsImage: true })
            {
                return true;
            }

            if (current is ContainerInline nested && ContainsImage(nested))
            {
                return true;
            }
        }

        return false;
    }

    private void AppendQuoteMarker(Paragraph paragraph, int quoteDepth)
    {
        if (quoteDepth > 0)
        {
            paragraph.Inlines.Add(new Run
            {
                Text = "▎ ",
                Foreground = _semanticForeground
            });
        }
    }

    private static void AppendText(InlineCollection destination, string? text)
    {
        string value = text ?? string.Empty;
        string[] parts = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (int index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                destination.Add(new LineBreak());
            }
            if (parts[index].Length > 0)
            {
                destination.Add(new Run { Text = parts[index] });
            }
        }
    }

    private void AppendBlockText(InlineCollection destination, ContainerBlock container)
    {
        bool needsSeparator = false;
        foreach (MdBlock block in container)
        {
            if (needsSeparator)
            {
                destination.Add(new LineBreak());
            }

            if (block is LeafBlock leaf && leaf.Inline is { } inline)
            {
                AppendContainer(destination, inline);
                needsSeparator = true;
            }
            else if (block is ContainerBlock nested)
            {
                AppendBlockText(destination, nested);
                needsSeparator = true;
            }
        }
    }

    private CheckBox CreateTaskMarker(bool isChecked)
    {
        var marker = new CheckBox
        {
            IsChecked = isChecked,
            IsEnabled = AreTaskListsInteractive,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform { Y = 6 },
            Tag = _taskListIndex++
        };
        if (AreTaskListsInteractive)
        {
            marker.Click += TaskMarker_Click;
        }
        return marker;
    }

    private void TaskMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int taskIndex })
        {
            TaskToggleRequested?.Invoke(
                this,
                new MarkdownTaskToggleRequestedEventArgs(taskIndex));
        }
    }

    private void AppendContainer(InlineCollection destination, ContainerInline container)
    {
        for (MdInline? current = container.FirstChild; current is not null; current = current.NextSibling)
        {
            AppendInline(destination, current);
        }
    }

    private void AppendInline(InlineCollection destination, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                destination.Add(new Run { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                destination.Add(new Run
                {
                    Text = code.Content,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _semanticForeground
                });
                break;
            case LineBreakInline:
                destination.Add(new LineBreak());
                break;
            case HtmlInline:
                break;
            case LinkInline link when link.IsImage:
                AppendImage(destination, link);
                break;
            case LinkInline link:
                AppendLink(destination, link);
                break;
            case EmphasisInline emphasis:
                var span = new Span();
                if (emphasis.DelimiterChar == '~')
                {
                    span.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                }
                else if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                AppendContainer(span.Inlines, emphasis);
                destination.Add(span);
                break;
            case ContainerInline nested:
                AppendContainer(destination, nested);
                break;
        }
    }

    private void AppendImage(InlineCollection destination, LinkInline link)
    {
        string? source = link.Url;
        if (MarkdownDocumentService.TryGetAttachmentId(source, out string? attachmentId))
        {
            source = AttachmentResolver?.Invoke(attachmentId!);
        }
        else if (!AllowRemoteImages || !MarkdownDocumentService.IsRemoteImage(source))
        {
            AppendBlockedImageLabel(destination, link);
            return;
        }

        if (!TryCreateImageUri(source, out Uri? uri))
        {
            AppendBlockedImageLabel(destination, link);
            return;
        }

        PerformanceLogger.RecordMarkdownInlineImageDecode();
        var bitmap = new BitmapImage
        {
            // Inline screenshots can be very large. Decode at twice the maximum
            // display width for crisp high-DPI rendering without retaining the
            // full source bitmap in the XAML image surface.
            DecodePixelWidth = 960
        };
        bitmap.UriSource = uri;
        var image = new Image
        {
            Source = bitmap,
            MaxWidth = 480,
            MaxHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4)
        };
        image.ImageOpened += InlineImage_ImageOpened;
        image.ImageFailed += InlineImage_ImageFailed;
        AutomationProperties.SetName(
            image,
            string.IsNullOrWhiteSpace(link.Title) ? "Markdown image" : link.Title);
        destination.Add(new InlineUIContainer { Child = image });
    }

    private void InlineImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.ImageOpened -= InlineImage_ImageOpened;
            image.ImageFailed -= InlineImage_ImageFailed;
        }

        // BitmapImage resolves its dimensions asynchronously. Explicitly
        // invalidate the document once those dimensions are known so the
        // MaxHeight line is recomputed before the next frame is presented.
        _documentText.InvalidateMeasure();
        _documentText.InvalidateArrange();
    }

    private void InlineImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.ImageOpened -= InlineImage_ImageOpened;
            image.ImageFailed -= InlineImage_ImageFailed;
            image.Visibility = Visibility.Collapsed;
        }

        _documentText.InvalidateMeasure();
        _documentText.InvalidateArrange();
    }

    private static void AppendBlockedImageLabel(InlineCollection destination, LinkInline link) =>
        destination.Add(new Run
        {
            Text = string.IsNullOrWhiteSpace(link.Title) ? "[image]" : $"[{link.Title}]"
        });

    private void AppendLink(InlineCollection destination, LinkInline link)
    {
        if (!MarkdownDocumentService.IsAllowedLink(link.Url))
        {
            var unavailableLink = CreateLinkSpan();
            AppendContainer(unavailableLink.Inlines, link);
            destination.Add(unavailableLink);
            return;
        }

        if (MarkdownDocumentService.TryGetAttachmentId(link.Url, out string? attachmentId))
        {
            var attachmentLink = CreateHyperlink();
            AppendContainer(attachmentLink.Inlines, link);
            attachmentLink.Click += (_, _) => AttachmentOpenRequested?.Invoke(
                this,
                new MarkdownAttachmentRequestedEventArgs(attachmentId!));
            destination.Add(attachmentLink);
            return;
        }

        var hyperlink = CreateHyperlink();
        hyperlink.NavigateUri = new Uri(link.Url!);
        AppendContainer(hyperlink.Inlines, link);
        destination.Add(hyperlink);
    }

    private Hyperlink CreateHyperlink() => new()
    {
        FontWeight = FontWeights.SemiBold,
        Foreground = _semanticForeground,
        TextDecorations = Windows.UI.Text.TextDecorations.Underline
    };

    private Span CreateLinkSpan() => new()
    {
        FontWeight = FontWeights.SemiBold,
        Foreground = _contentForeground,
        TextDecorations = Windows.UI.Text.TextDecorations.Underline
    };

    private static bool? FindTaskState(ListItemBlock item)
    {
        foreach (LeafBlock leaf in item.Descendants<LeafBlock>())
        {
            if (leaf.Inline?.FirstChild is TaskList taskList)
            {
                return taskList.Checked;
            }
        }
        return null;
    }

    private static bool TryCreateImageUri(string? source, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }
        if (File.Exists(source))
        {
            uri = new Uri(Path.GetFullPath(source));
            return true;
        }
        return Uri.TryCreate(source, UriKind.Absolute, out uri);
    }

    private Brush BrushResource(string key)
    {
        for (DependencyObject? current = this;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element &&
                element.Resources.TryGetValue(key, out object? scopedResource) &&
                scopedResource is Brush scopedBrush)
            {
                return scopedBrush;
            }
        }

        if (Application.Current?.Resources.TryGetValue(key, out object? resource) == true &&
            resource is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

public sealed class MarkdownTaskToggleRequestedEventArgs(int taskIndex) : EventArgs
{
    public int TaskIndex { get; } = taskIndex;
}

public sealed class MarkdownAttachmentRequestedEventArgs(string attachmentId) : EventArgs
{
    public string AttachmentId { get; } = attachmentId;
}
