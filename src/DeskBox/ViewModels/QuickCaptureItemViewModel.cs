using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureItemViewModel : ObservableObject
{
    private readonly LocalizationService _localizationService;
    private QuickCaptureItem _model;
    private bool _showPinnedSortControls;
    private bool _canMovePinnedUp;
    private bool _canMovePinnedDown;
    private double _textSize;
    private double _iconSize;
    private string _searchText;

    public QuickCaptureItemViewModel(
        QuickCaptureItem model,
        LocalizationService localizationService,
        double textSize,
        double iconSize,
        string? searchText,
        bool showPinnedSortControls = false,
        bool canMovePinnedUp = false,
        bool canMovePinnedDown = false)
    {
        _model = model;
        _localizationService = localizationService;
        _textSize = textSize;
        _iconSize = iconSize;
        _searchText = NormalizeSearchText(searchText);
        _showPinnedSortControls = showPinnedSortControls;
        _canMovePinnedUp = canMovePinnedUp;
        _canMovePinnedDown = canMovePinnedDown;
    }

    public string Id => _model.Id;

    public string Body => _model.Body;

    public string? Url => _model.Url;

    public string DisplayText => Type == QuickCaptureItemType.Image
        ? _localizationService.T("QuickCapture.ImageItem")
        : _model.Body;

    public int HighlightStartIndex => GetHighlightStartIndex();

    public int HighlightLength => string.IsNullOrEmpty(_searchText) || HighlightStartIndex < 0
        ? 0
        : _searchText.Length;

    public Visibility HighlightVisibility => HighlightLength > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string? ImagePath => _model.ImagePath;

    public Uri? ImagePreviewUri =>
        Type == QuickCaptureItemType.Image &&
        !string.IsNullOrWhiteSpace(ImagePath) &&
        Uri.TryCreate(ImagePath, UriKind.Absolute, out var uri)
            ? uri
            : null;

    public bool IsPinned => _model.IsPinned;

    public bool IsRecent => _model.IsRecent;

    public QuickCaptureItemType Type => _model.Type;

    public string TypeGlyph => Type switch
    {
        QuickCaptureItemType.Link => "\uE774",
        QuickCaptureItemType.Image => "\uEB9F",
        _ => "\uE8A5"
    };

    public Visibility ImagePreviewVisibility =>
        Type == QuickCaptureItemType.Image && !string.IsNullOrWhiteSpace(ImagePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility TextPreviewVisibility =>
        Type == QuickCaptureItemType.Image
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string PinGlyph => IsPinned ? "\uE840" : "\uE718";

    public string PinTooltip => IsPinned
        ? _localizationService.T("QuickCapture.Unpin")
        : _localizationService.T("QuickCapture.Pin");

    public Visibility PinnedSortControlsVisibility => _showPinnedSortControls ? Visibility.Visible : Visibility.Collapsed;

    public bool CanMovePinnedUp => _canMovePinnedUp;

    public bool CanMovePinnedDown => _canMovePinnedDown;

    public string MoveUpTooltip => _localizationService.T("QuickCapture.MoveUp");

    public string MoveDownTooltip => _localizationService.T("QuickCapture.MoveDown");

    public string UpdatedAtText => FormatUpdatedAt(_model.UpdatedAt);

    public double TextSize => _textSize;

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize - 1, _textSize - 2);

    public double IconSize => _iconSize;

    public double TypeIconSize => Math.Max(11, Math.Round(_iconSize * 0.52));

    public double ActionIconSize => Math.Max(10, Math.Round(_iconSize * 0.42));

    public QuickCaptureItem ToModel() => new()
    {
        Id = _model.Id,
        Type = _model.Type,
        Body = _model.Body,
        Url = _model.Url,
        ImagePath = _model.ImagePath,
        ContentHash = _model.ContentHash,
        IsPinned = _model.IsPinned,
        IsRecent = _model.IsRecent,
        IsDeleted = _model.IsDeleted,
        SortOrder = _model.SortOrder,
        PinnedSortOrder = _model.PinnedSortOrder,
        CreatedAt = _model.CreatedAt,
        UpdatedAt = _model.UpdatedAt
    };

    public void Update(QuickCaptureItem model)
    {
        _model = model;
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(HighlightStartIndex));
        OnPropertyChanged(nameof(HighlightLength));
        OnPropertyChanged(nameof(HighlightVisibility));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(ImagePreviewUri));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsRecent));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(TypeGlyph));
        OnPropertyChanged(nameof(ImagePreviewVisibility));
        OnPropertyChanged(nameof(TextPreviewVisibility));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinTooltip));
        OnPropertyChanged(nameof(MoveUpTooltip));
        OnPropertyChanged(nameof(MoveDownTooltip));
        OnPropertyChanged(nameof(UpdatedAtText));
    }

    public void UpdateSearchText(string? searchText)
    {
        string normalizedSearchText = NormalizeSearchText(searchText);
        if (string.Equals(_searchText, normalizedSearchText, StringComparison.Ordinal))
        {
            return;
        }

        _searchText = normalizedSearchText;
        OnPropertyChanged(nameof(HighlightStartIndex));
        OnPropertyChanged(nameof(HighlightLength));
        OnPropertyChanged(nameof(HighlightVisibility));
    }

    public void UpdateAppearance(double textSize, double iconSize)
    {
        if (Math.Abs(_textSize - textSize) < 0.01 &&
            Math.Abs(_iconSize - iconSize) < 0.01)
        {
            return;
        }

        _textSize = textSize;
        _iconSize = iconSize;
        OnPropertyChanged(nameof(TextSize));
        OnPropertyChanged(nameof(SecondaryTextSize));
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(TypeIconSize));
        OnPropertyChanged(nameof(ActionIconSize));
    }

    public void UpdatePinnedSortState(bool showControls, bool canMoveUp, bool canMoveDown)
    {
        if (_showPinnedSortControls == showControls &&
            _canMovePinnedUp == canMoveUp &&
            _canMovePinnedDown == canMoveDown)
        {
            return;
        }

        _showPinnedSortControls = showControls;
        _canMovePinnedUp = canMoveUp;
        _canMovePinnedDown = canMoveDown;
        OnPropertyChanged(nameof(PinnedSortControlsVisibility));
        OnPropertyChanged(nameof(CanMovePinnedUp));
        OnPropertyChanged(nameof(CanMovePinnedDown));
    }

    private static string FormatUpdatedAt(DateTimeOffset updatedAt)
    {
        var local = updatedAt.ToLocalTime();
        var now = DateTimeOffset.Now;
        return local.Date == now.Date
            ? local.ToString("HH:mm:ss")
            : local.ToString("MM-dd HH:mm:ss");
    }

    private static string NormalizeSearchText(string? searchText)
    {
        return string.IsNullOrWhiteSpace(searchText)
            ? string.Empty
            : searchText.Trim();
    }

    private int GetHighlightStartIndex()
    {
        if (string.IsNullOrEmpty(_searchText))
        {
            return -1;
        }

        return DisplayText.IndexOf(_searchText, StringComparison.CurrentCultureIgnoreCase);
    }
}
