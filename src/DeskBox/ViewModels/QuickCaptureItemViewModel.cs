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
    private bool _isCopySelected;
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
        RefreshAttachments();
    }

    public string Id => _model.Id;

    public string Body => _model.Body;

    public string CopyText => string.IsNullOrWhiteSpace(Body)
        ? Title ?? string.Empty
        : Body;

    public string? Title => _model.Title;

    public Visibility TitleVisibility => string.IsNullOrWhiteSpace(Title)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string? Url => _model.Url;

    private bool HasDisplayBody => !string.IsNullOrWhiteSpace(CopyText) &&
                                   !(Type == QuickCaptureItemType.Image &&
                                     string.Equals(CopyText, "Image", StringComparison.Ordinal));

    public string DisplayText => Type == QuickCaptureItemType.Image && !HasDisplayBody
        ? _localizationService.T("QuickCapture.ImageItem")
        : CopyText;

    public int HighlightStartIndex => GetHighlightStartIndex();

    public int HighlightLength => string.IsNullOrEmpty(_searchText) || HighlightStartIndex < 0
        ? 0
        : _searchText.Length;

    public Visibility HighlightVisibility => HighlightLength > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string? ImagePath => _model.ImagePath;

    public IReadOnlyList<TodoAttachmentViewModel> Attachments { get; private set; } = [];

    public IReadOnlyList<TodoAttachmentViewModel> ImageAttachments { get; private set; } = [];

    public Visibility ImageAttachmentsVisibility => ImageAttachments.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AttachmentSummaryVisibility => Attachments.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string AttachmentSummaryText => Attachments.Count.ToString();

    public Uri? ImagePreviewUri =>
        Type == QuickCaptureItemType.Image &&
        !string.IsNullOrWhiteSpace(ImagePath) &&
        Uri.TryCreate(ImagePath, UriKind.Absolute, out var uri)
            ? uri
            : null;

    public bool IsPinned => _model.IsPinned;

    public bool IsRecent => _model.IsRecent;

    public QuickCaptureAppearancePreset AppearancePreset => _model.AppearancePreset;

    public QuickCaptureSourceKind SourceKind => _model.SourceKind;

    public IReadOnlyList<string> Tags => _model.Tags ?? [];

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
        Type != QuickCaptureItemType.Image || HasDisplayBody
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string PinGlyph => IsPinned ? "\uE840" : "\uE718";

    public string PinTooltip => IsPinned
        ? _localizationService.T("QuickCapture.Unpin")
        : _localizationService.T("QuickCapture.Pin");

    public Visibility PinnedSortControlsVisibility => _showPinnedSortControls ? Visibility.Visible : Visibility.Collapsed;

    public bool CanMovePinnedUp => _canMovePinnedUp;

    public bool CanMovePinnedDown => _canMovePinnedDown;

    public string MoveUpTooltip => _localizationService.T("QuickCapture.MoveUp");

    public string MoveDownTooltip => _localizationService.T("QuickCapture.MoveDown");

    public bool IsCopySelected
    {
        get => _isCopySelected;
        set
        {
            if (SetProperty(ref _isCopySelected, value))
            {
                OnPropertyChanged(nameof(CopySelectionVisibility));
            }
        }
    }

    public Visibility CopySelectionVisibility => IsCopySelected ? Visibility.Visible : Visibility.Collapsed;

    public string UpdatedAtText => FormatUpdatedAt(_model.UpdatedAt);

    public string CreatedAtText => FormatUpdatedAt(_model.CreatedAt);

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
        Title = _model.Title,
        Url = _model.Url,
        ImagePath = _model.ImagePath,
        ContentHash = _model.ContentHash,
        Attachments = _model.Attachments?.Select(attachment => attachment.Clone()).ToList() ?? [],
        IsPinned = _model.IsPinned,
        IsRecent = _model.IsRecent,
        IsDeleted = _model.IsDeleted,
        AppearancePreset = _model.AppearancePreset,
        SourceKind = _model.SourceKind,
        Tags = _model.Tags is null ? [] : [.. _model.Tags],
        ArchivedAt = _model.ArchivedAt,
        SortOrder = _model.SortOrder,
        PinnedSortOrder = _model.PinnedSortOrder,
        CreatedAt = _model.CreatedAt,
        UpdatedAt = _model.UpdatedAt
    };

    public void Update(QuickCaptureItem model)
    {
        var old = _model;
        _model = model;

        bool attachmentsChanged = !(old.Attachments ?? []).Select(GetAttachmentSignature)
            .SequenceEqual((model.Attachments ?? []).Select(GetAttachmentSignature), StringComparer.Ordinal);
        if (attachmentsChanged)
        {
            RefreshAttachments();
            OnPropertyChanged(nameof(Attachments));
            OnPropertyChanged(nameof(ImageAttachments));
            OnPropertyChanged(nameof(ImageAttachmentsVisibility));
            OnPropertyChanged(nameof(AttachmentSummaryVisibility));
            OnPropertyChanged(nameof(AttachmentSummaryText));
        }

        if (old.Body != model.Body || old.Type != model.Type)
        {
            OnPropertyChanged(nameof(Body));
            OnPropertyChanged(nameof(CopyText));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(HighlightStartIndex));
            OnPropertyChanged(nameof(HighlightLength));
            OnPropertyChanged(nameof(HighlightVisibility));
        }

        if (old.Url != model.Url)
        {
            OnPropertyChanged(nameof(Url));
        }

        if (old.ImagePath != model.ImagePath || old.Type != model.Type)
        {
            OnPropertyChanged(nameof(ImagePath));
            OnPropertyChanged(nameof(ImagePreviewUri));
            OnPropertyChanged(nameof(ImagePreviewVisibility));
            OnPropertyChanged(nameof(TextPreviewVisibility));
        }

        if (old.IsPinned != model.IsPinned)
        {
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(PinGlyph));
            OnPropertyChanged(nameof(PinTooltip));
        }

        if (old.IsRecent != model.IsRecent)
        {
            OnPropertyChanged(nameof(IsRecent));
        }

        if (old.Type != model.Type)
        {
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(TypeGlyph));
        }

        if (old.Title != model.Title)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TitleVisibility));
            OnPropertyChanged(nameof(CopyText));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(HighlightStartIndex));
            OnPropertyChanged(nameof(HighlightLength));
            OnPropertyChanged(nameof(HighlightVisibility));
            OnPropertyChanged(nameof(TextPreviewVisibility));
        }

        if (old.AppearancePreset != model.AppearancePreset)
        {
            OnPropertyChanged(nameof(AppearancePreset));
        }

        if (old.SourceKind != model.SourceKind)
        {
            OnPropertyChanged(nameof(SourceKind));
        }

        if (!(old.Tags ?? []).SequenceEqual(model.Tags ?? [], StringComparer.CurrentCultureIgnoreCase))
        {
            OnPropertyChanged(nameof(Tags));
        }

        if (old.UpdatedAt != model.UpdatedAt)
        {
            OnPropertyChanged(nameof(UpdatedAtText));
        }

        if (old.CreatedAt != model.CreatedAt)
        {
            OnPropertyChanged(nameof(CreatedAtText));
        }
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

    private void RefreshAttachments()
    {
        Attachments = (_model.Attachments ?? [])
            .Select(attachment => new TodoAttachmentViewModel(attachment))
            .ToList();
        ImageAttachments = Attachments
            .Where(attachment => attachment.IsImage)
            .ToList();
    }

    private static string GetAttachmentSignature(TodoAttachment attachment)
    {
        return $"{attachment.Id}\0{attachment.FilePath}\0{attachment.DisplayName}\0{attachment.Type}\0{attachment.StorageMode}";
    }
}
