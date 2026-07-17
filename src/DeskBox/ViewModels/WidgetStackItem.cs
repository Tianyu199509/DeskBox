using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed class WidgetStackItem : WidgetItem
{
    private IReadOnlyList<WidgetItem> _members = [];
    private string _summary = string.Empty;
    private string _automationState = string.Empty;
    private string _collapseText = string.Empty;
    private bool _isExpanded;

    public required WidgetStackCategory Category { get; init; }

    public required string StackKey { get; init; }

    public IReadOnlyList<WidgetItem> Members => _members;

    public string Summary => _summary;

    public string AutomationState => _automationState;

    public string CollapseText => _collapseText;

    public WidgetItem PreviewOne => Members[0];

    public WidgetItem PreviewTwo => Members[Math.Min(1, Members.Count - 1)];

    public WidgetItem PreviewThree => Members[Math.Min(2, Members.Count - 1)];

    public Visibility ThirdPreviewVisibility => Members.Count >= 3
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CountText => Members.Count.ToString();

    public bool IsExpanded => _isExpanded;

    public Visibility CollapsedPreviewVisibility => IsExpanded
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility ExpandedAnchorVisibility => IsExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ChevronGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public double TileWidth { get; private set; }

    public double TileHeight { get; private set; }

    public Thickness TileMargin { get; private set; }

    public Thickness TilePadding { get; private set; }

    public double PreviewSize { get; private set; }

    public double PreviewItemSize { get; private set; }

    public double LabelMaxWidth { get; private set; }

    public double LabelFontSize { get; private set; }

    public Thickness ListMargin { get; private set; }

    public Thickness ListPadding { get; private set; }

    public double ListIconSize { get; private set; }

    public void Update(
        IReadOnlyList<WidgetItem> members,
        string name,
        string summary,
        string automationState,
        string collapseText,
        bool isExpanded,
        double tileWidth,
        double tileHeight,
        Thickness tileMargin,
        Thickness tilePadding,
        double previewSize,
        double previewItemSize,
        double labelMaxWidth,
        double labelFontSize,
        Thickness listMargin,
        Thickness listPadding,
        double listIconSize)
    {
        _members = members;
        Name = name;
        _summary = summary;
        _automationState = automationState;
        _collapseText = collapseText;
        _isExpanded = isExpanded;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        TileMargin = tileMargin;
        TilePadding = tilePadding;
        PreviewSize = previewSize;
        PreviewItemSize = previewItemSize;
        LabelMaxWidth = labelMaxWidth;
        LabelFontSize = labelFontSize;
        ListMargin = listMargin;
        ListPadding = listPadding;
        ListIconSize = listIconSize;

        RefreshPresentationState();
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileHeight));
        OnPropertyChanged(nameof(TileMargin));
        OnPropertyChanged(nameof(TilePadding));
        OnPropertyChanged(nameof(PreviewSize));
        OnPropertyChanged(nameof(PreviewItemSize));
        OnPropertyChanged(nameof(LabelMaxWidth));
        OnPropertyChanged(nameof(LabelFontSize));
        OnPropertyChanged(nameof(ListMargin));
        OnPropertyChanged(nameof(ListPadding));
        OnPropertyChanged(nameof(ListIconSize));
    }

    public void RefreshPresentationState()
    {
        OnPropertyChanged(nameof(Members));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AutomationState));
        OnPropertyChanged(nameof(CollapseText));
        OnPropertyChanged(nameof(PreviewOne));
        OnPropertyChanged(nameof(PreviewTwo));
        OnPropertyChanged(nameof(PreviewThree));
        OnPropertyChanged(nameof(ThirdPreviewVisibility));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(CollapsedPreviewVisibility));
        OnPropertyChanged(nameof(ExpandedAnchorVisibility));
        OnPropertyChanged(nameof(ChevronGlyph));
    }
}
