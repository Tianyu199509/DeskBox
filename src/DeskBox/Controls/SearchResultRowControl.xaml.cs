using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

/// <summary>
/// A single row in the search popup result list. Receives a typed
/// <see cref="SearchResultItem"/> bridge while retaining the inherited DataContext and owns its own hover and
/// keyboard-selection visuals, query-span bolding, compact subtitle composition, and inline quick-action buttons.
/// </summary>
public sealed partial class SearchResultRowControl : UserControl
{
    private static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource? s_todoSvgIcon;
    private static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource? s_quickCaptureSvgIcon;

    private readonly Brush _defaultBackground;
    private readonly Brush _defaultBorderBrush;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isMultiSelected;
    private bool _fileColumnsVisible;
    private string _highlightQuery = string.Empty;

    internal static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource GetTodoSvgIcon() =>
        s_todoSvgIcon ??= new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri("ms-appx:///Assets/WidgetTitleIcons/todo.svg"));

    internal static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource GetQuickCaptureSvgIcon() =>
        s_quickCaptureSvgIcon ??= new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri("ms-appx:///Assets/WidgetTitleIcons/quick-capture.svg"));

    internal static bool IsTodoSearchResultOrAction(SearchResultItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.Kind == SearchResultKind.Todo)
        {
            return true;
        }

        if (item.Kind == SearchResultKind.Action && item.ActionId is { } id)
        {
            return id == "new-todo" ||
                   id == "open-todo" ||
                   id.StartsWith("quick-todo:", StringComparison.Ordinal) ||
                   id.StartsWith("quick-todo-open:", StringComparison.Ordinal);
        }

        return false;
    }

    internal static bool IsQuickCaptureSearchResultOrAction(SearchResultItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.Kind == SearchResultKind.QuickCapture)
        {
            return true;
        }

        if (item.Kind == SearchResultKind.Action && item.ActionId is { } id)
        {
            return id == "new-note" ||
                   id == "quick-jot" ||
                   id == "open-quickcapture" ||
                   id.StartsWith("quick-note:", StringComparison.Ordinal) ||
                   id.StartsWith("quick-note-open:", StringComparison.Ordinal);
        }

        return false;
    }

    internal static ImageSource? ResolveNativeWidgetSvgIcon(SearchResultItem? item)
    {
        if (IsTodoSearchResultOrAction(item))
        {
            return GetTodoSvgIcon();
        }

        if (IsQuickCaptureSearchResultOrAction(item))
        {
            return GetQuickCaptureSvgIcon();
        }

        return null;
    }

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(SearchResultRowControl),
            new PropertyMetadata(false, OnIsSelectedChanged));

    internal event Action<SearchResultRowControl, SearchResultItem>? PrimaryActionRequested;
    internal event Action<SearchResultRowControl, SearchResultItem>? OpenLocationRequested;
    internal event Action<SearchResultRowControl, SearchResultItem, FrameworkElement>? MoreActionsRequested;

    public SearchResultRowControl()
    {
        InitializeComponent();
        _defaultBackground = RowRoot.Background;
        _defaultBorderBrush = RowRoot.BorderBrush;
        PointerEntered += OnRowPointerEntered;
        PointerExited += OnRowPointerExited;
        PointerPressed += OnRowPointerPressed;
        PointerReleased += OnRowPointerReleased;
        PointerCaptureLost += OnRowPointerCaptureLost;
    }

    /// <summary>Keyboard-selection state, driven by the popup window.</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>
    /// Multi-selection state (rubber-band / Ctrl+click), driven by the popup window.
    /// Uses native card highlight + border without any checkbox/checkmark badge.
    /// </summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set
        {
            if (_isMultiSelected == value)
            {
                return;
            }

            _isMultiSelected = value;
            RefreshBackground();
            RefreshQuickActionsVisibility();
        }
    }

    internal SearchResultItem? Item { get; private set; }

    internal void ClearPressedState()
    {
        if (!_isPressed)
        {
            return;
        }

        _isPressed = false;
        RefreshBackground();
    }

    internal void ClearTransientPointerStates()
    {
        if (!_isPressed && !_isHovered)
        {
            return;
        }

        _isPressed = false;
        _isHovered = false;
        RefreshBackground();
        RefreshQuickActionsVisibility();
    }

    internal void PrepareItem(SearchResultItem? item)
    {
        ClearTransientPointerStates();
        Item = item;
        TitleText.Inlines.Clear();
        Bindings.Update();
        TitleText.Text = GetCleanDisplayTitle(item);
        ApplyTitleTypography(item);
        RefreshIconVisuals();
        RefreshQuickActionsVisibility();
    }

    internal void ConfigureRowPresentation(
        string? highlightQuery,
        string? openTooltip = null,
        string? openLocationTooltip = null,
        string? moreTooltip = null)
    {
        _highlightQuery = highlightQuery ?? string.Empty;
        ApplyTitleTypography(Item);
        ApplyQueryHighlight(_highlightQuery);
        ConfigureQuickActionTooltipsAndButtons(Item, openTooltip, openLocationTooltip, moreTooltip);
        RefreshQuickActionsVisibility();
    }

    private void ApplyTitleTypography(SearchResultItem? item)
    {
        bool isCalculator = item?.Kind == SearchResultKind.Action &&
                            item.ActionId?.StartsWith("copy-calc:", StringComparison.Ordinal) == true;
        TitleText.FontSize = isCalculator ? 16 : 14;
        TitleText.FontWeight = isCalculator ? FontWeights.SemiBold : FontWeights.Normal;
    }

    internal void ApplyQueryHighlight(string? query)
    {
        _highlightQuery = query ?? string.Empty;
        string title = GetCleanDisplayTitle(Item);
        TitleText.Inlines.Clear();

        bool isCalculator = Item?.Kind == SearchResultKind.Action &&
                            Item.ActionId?.StartsWith("copy-calc:", StringComparison.Ordinal) == true;
        string trimmed = _highlightQuery.Trim();
        if (isCalculator || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(trimmed))
        {
            TitleText.Text = title;
            return;
        }

        int matchIndex = title.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
        {
            TitleText.Text = title;
            return;
        }

        TitleText.Text = string.Empty;
        int cursor = 0;
        while (matchIndex >= 0 && cursor < title.Length)
        {
            if (matchIndex > cursor)
            {
                TitleText.Inlines.Add(new Run { Text = title[cursor..matchIndex] });
            }

            TitleText.Inlines.Add(new Run
            {
                Text = title.Substring(matchIndex, trimmed.Length),
                FontWeight = FontWeights.SemiBold
            });

            cursor = matchIndex + trimmed.Length;
            if (cursor >= title.Length)
            {
                break;
            }

            matchIndex = title.IndexOf(trimmed, cursor, StringComparison.OrdinalIgnoreCase);
        }

        if (cursor < title.Length)
        {
            TitleText.Inlines.Add(new Run { Text = title[cursor..] });
        }
    }

    internal bool IsElementInsideActionButtons(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, this))
        {
            if (ReferenceEquals(current, QuickActionsPanel))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ConfigureQuickActionTooltipsAndButtons(
        SearchResultItem? item,
        string? openTooltip,
        string? openLocationTooltip,
        string? moreTooltip)
    {
        bool isFileOrFolder = item?.Kind is SearchResultKind.File or SearchResultKind.Folder &&
                              !string.IsNullOrWhiteSpace(item.DetailPath);
        if (isFileOrFolder)
        {
            // File/folder rows show Open Location + More Actions (double-click/Enter opens).
            QuickOpenLocationButton.Visibility = Visibility.Visible;
            QuickPrimaryButton.Visibility = Visibility.Collapsed;
            QuickMoreButton.Visibility = Visibility.Visible;
        }
        else
        {
            // Non-file rows only have a single primary action (Copy or Open), so suppress the redundant '...' menu.
            QuickOpenLocationButton.Visibility = Visibility.Collapsed;
            QuickPrimaryButton.Visibility = Visibility.Visible;
            QuickMoreButton.Visibility = Visibility.Collapsed;
        }

        bool isCopyResult = item?.Kind == SearchResultKind.Clipboard ||
                            (item?.Kind == SearchResultKind.Action &&
                             item.ActionId?.StartsWith("copy-calc:", StringComparison.Ordinal) == true);
        QuickPrimaryGlyph.Glyph = isCopyResult ? "\uE8C8" : "\uE8A7";

        if (!string.IsNullOrWhiteSpace(openLocationTooltip))
        {
            ToolTipService.SetToolTip(QuickOpenLocationButton, openLocationTooltip);
        }
        if (!string.IsNullOrWhiteSpace(openTooltip))
        {
            ToolTipService.SetToolTip(QuickPrimaryButton, openTooltip);
        }
        if (!string.IsNullOrWhiteSpace(moreTooltip))
        {
            ToolTipService.SetToolTip(QuickMoreButton, moreTooltip);
        }
    }

    private static string GetCleanDisplayTitle(SearchResultItem? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        if (item.Kind == SearchResultKind.File &&
            (item.Title.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
             item.Title.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
        {
            string stripped = System.IO.Path.GetFileNameWithoutExtension(item.Title);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                return stripped;
            }
        }

        return item.Title;
    }

    private void RefreshQuickActionsVisibility()
    {
        // Keep single-line rows clutter-free (CmdPal pattern); contextual actions live in
        // the bottom CommandBar and Ctrl+K searchable action menu.
        QuickActionsPanel.Visibility = Visibility.Collapsed;
        if (_fileColumnsVisible)
        {
            DateText.Visibility = Visibility.Visible;
        }
    }

    private void QuickOpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            OpenLocationRequested?.Invoke(this, item);
        }
    }

    private void QuickPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            PrimaryActionRequested?.Invoke(this, item);
        }
    }

    private void QuickMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (Item is { } item)
        {
            MoreActionsRequested?.Invoke(this, item, QuickMoreButton);
        }
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SearchResultRowControl control)
        {
            return;
        }

        control.SelectionBar.Visibility = (bool)e.NewValue
            ? Visibility.Visible
            : Visibility.Collapsed;
        control.RefreshBackground();
        control.RefreshQuickActionsVisibility();
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        RefreshBackground();
        RefreshQuickActionsVisibility();
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        RefreshBackground();
        RefreshQuickActionsVisibility();
    }

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            IsElementInsideActionButtons(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isPressed = true;
        RefreshBackground();
    }

    private void OnRowPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        RefreshBackground();
    }

    private void OnRowPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        RefreshBackground();
    }

    private void RefreshBackground()
    {
        if (IsMultiSelected)
        {
            RowRoot.Background = ResolveThemeBrush(
                _isPressed || _isHovered
                    ? "ControlFillColorTertiaryBrush"
                    : "ControlFillColorSecondaryBrush");
            RowRoot.BorderBrush = ResolveThemeBrush("CardStrokeColorDefaultBrush") ?? _defaultBorderBrush;
            return;
        }

        if (IsSelected)
        {
            RowRoot.Background = ResolveThemeBrush(
                _isPressed
                    ? "SubtleFillColorTertiaryBrush"
                    : "ControlFillColorDefaultBrush")
                ?? ResolveThemeBrush("SubtleFillColorSecondaryBrush");
            RowRoot.BorderBrush = ResolveThemeBrush("CardStrokeColorDefaultBrush") ?? _defaultBorderBrush;
            return;
        }

        RowRoot.BorderBrush = _defaultBorderBrush;
        RowRoot.Background = _isPressed
            ? ResolveThemeBrush("SubtleFillColorTertiaryBrush")
            : _isHovered
                ? ResolveThemeBrush("SubtleFillColorSecondaryBrush")
                : _defaultBackground;
    }

    public void RefreshIconVisuals()
    {
        var item = Item;
        if (item is not null && item.Icon is null)
        {
            item.Icon = ResolveNativeWidgetSvgIcon(item);
        }

        bool hasIcon = item?.Icon is not null;
        FileIcon.Source = item?.Icon;
        FileIcon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        GlyphBlock.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;
        SizeText.Text = item?.SizeDisplay;
        DateText.Text = item?.DateDisplay;
        UpdateSubtitlePresentation();
    }

    public void SetFileColumnsVisible(bool visible)
    {
        _fileColumnsVisible = visible;
        TypeColumn.Width = visible ? new GridLength(72) : new GridLength(0);
        SizeColumn.Width = visible ? new GridLength(84) : new GridLength(0);
        DateColumn.Width = visible ? new GridLength(104) : new GridLength(0);
        TypeText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SizeText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateSubtitlePresentation();
    }

    private void UpdateSubtitlePresentation()
    {
        var item = Item;
        if (item is null)
        {
            SubtitleText.Text = string.Empty;
            SubtitleText.Visibility = Visibility.Collapsed;
            TitleText.MaxWidth = double.PositiveInfinity;
            return;
        }

        string subtitle = item.Subtitle ?? string.Empty;
        bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = hasSubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;
        TitleText.MaxWidth = hasSubtitle ? 276 : double.PositiveInfinity;
    }

    internal static string ComposeCompactFileSubtitle(
        string? typeDisplay,
        string? sizeDisplay,
        string? baseSubtitle)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(typeDisplay))
        {
            parts.Add(typeDisplay.Trim());
        }

        if (!string.IsNullOrWhiteSpace(sizeDisplay) &&
            !string.Equals(sizeDisplay.Trim(), "—", StringComparison.Ordinal))
        {
            parts.Add(sizeDisplay.Trim());
        }

        if (!string.IsNullOrWhiteSpace(baseSubtitle))
        {
            string trimmedBase = baseSubtitle.Trim();
            if (!parts.Contains(trimmedBase, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(trimmedBase);
            }
        }

        return string.Join(" · ", parts);
    }

    private Brush? ResolveThemeBrush(string key) =>
        NeutralInteractionBrush.ResolveThemedResource(key, this);
}
