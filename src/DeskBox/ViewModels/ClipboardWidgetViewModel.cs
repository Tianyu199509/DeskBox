using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.Text;

namespace DeskBox.ViewModels;

public enum ClipboardFilterKind
{
    All = 0,
    Text = 1,
    Images = 2,
    Links = 3,
    Files = 4,
    Pinned = 5
}

[Bindable]
public sealed partial class ClipboardItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Func<string, int, Task<BitmapImage?>>? _iconLoader;
    private readonly Func<string, int, Task<BitmapImage?>>? _thumbnailLoader;
    private readonly Func<ClipboardHistoryItem, string?>? _thumbnailPathResolver;
    private ClipboardHistoryItem _model;
    private string _secretProtectionMode;
    private bool _showSourceApp;
    private double _textSize;
    private double _secondaryTextSize;
    private int _previewMaxLines;
    private Thickness _cardPadding;
    private double _cardMinHeight;
    private double _imageBannerMaxHeight;
    private bool _isCurrentClipboard;
    private int _quickSlotIndex;
    private bool _isCompactView;
    private BitmapImage? _thumbnail;
    private BitmapImage? _sourceAppIcon;
    private SolidColorBrush? _colorSwatchBrush;
    private string? _cachedColorHex;
    private bool _thumbnailLoadRequested;
    private bool _sourceIconLoadRequested;
    private int _visualAssetGeneration;
    private bool _isMissingAllFiles;
    private int _missingFilesCheckInFlight;
    private bool _isJustCopied;
    private bool _isPeekSelected;
    private bool _isMultiSelectMode;
    private bool _isMultiSelected;
    private int _multiSelectOrder;
    private int _pasteQueueOrder;

    public ClipboardItemViewModel(
        ClipboardHistoryItem model,
        LocalizationService localization,
        string secretProtectionMode,
        bool showSourceApp,
        double textSize,
        double secondaryTextSize,
        int previewMaxLines,
        Thickness cardPadding,
        double cardMinHeight,
        Func<ClipboardHistoryItem, string?>? thumbnailPathResolver = null,
        Func<string, int, Task<BitmapImage?>>? iconLoader = null,
        Func<string, int, Task<BitmapImage?>>? thumbnailLoader = null,
        double imageBannerMaxHeight = 96,
        bool isCurrentClipboard = false,
        int quickSlotIndex = 0,
        bool isCompactView = false)
    {
        _model = model;
        _localization = localization;
        _secretProtectionMode = secretProtectionMode;
        _showSourceApp = showSourceApp;
        _textSize = textSize;
        _secondaryTextSize = secondaryTextSize;
        _previewMaxLines = previewMaxLines;
        _cardPadding = cardPadding;
        _cardMinHeight = cardMinHeight;
        _imageBannerMaxHeight = imageBannerMaxHeight;
        _isCurrentClipboard = isCurrentClipboard;
        _quickSlotIndex = quickSlotIndex;
        _isCompactView = isCompactView;
        _thumbnailPathResolver = thumbnailPathResolver;
        _iconLoader = iconLoader;
        _thumbnailLoader = thumbnailLoader;
    }

    public ClipboardHistoryItem Model => _model;

    public string Id => _model.Id;

    public ClipboardItemKind Kind => _model.Kind;

    public ClipboardTextSubKind TextSubKind => _model.TextSubKind;

    public bool IsPinned => _model.IsPinned;

    public bool IsSensitive => _model.IsSensitive;

    public bool IsMaskedSecret =>
        _model.Kind == ClipboardItemKind.Text &&
        _model.IsSensitive &&
        !string.Equals(_secretProtectionMode, ClipboardSettingsSlice.SecretProtectionNone, StringComparison.OrdinalIgnoreCase) && !string.Equals(_secretProtectionMode, "Off", StringComparison.OrdinalIgnoreCase);

    public double TextSize => _textSize;

    public double CodeTextSize => Math.Max(11.0, _textSize - 1.0);

    public double SecondaryTextSize => _secondaryTextSize;

    public double MetaFontSize => Math.Clamp(_textSize - 2.0, 10.5, 12.0);

    public int PreviewMaxLines => _previewMaxLines;

    public int CodePreviewMaxLines => Math.Clamp(_previewMaxLines + 2, 2, 6);

    public Thickness CardPadding => _cardPadding;

    public double CardMinHeight => _cardMinHeight;

    public double ImageBannerMaxHeight => _imageBannerMaxHeight;

    public bool IsCurrentClipboard => _isCurrentClipboard;

    public Visibility CurrentClipboardIndicatorVisibility =>
        _isCurrentClipboard ? Visibility.Visible : Visibility.Collapsed;

    public int QuickSlotIndex => _quickSlotIndex;

    public string QuickSlotText => _quickSlotIndex is >= 1 and <= 9 ? $"#{_quickSlotIndex}" : string.Empty;

    public Visibility QuickSlotVisibility =>
        _quickSlotIndex is >= 1 and <= 9 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCompactView => _isCompactView;

    public Visibility HeaderRowVisibility =>
        _isCompactView ? Visibility.Collapsed : Visibility.Visible;

    public VerticalAlignment ActionOverlayVerticalAlignment =>
        _isCompactView ? VerticalAlignment.Center : VerticalAlignment.Top;

    public Thickness ActionOverlayMargin =>
        _isCompactView ? new Thickness(0, 0, 5, 0) : new Thickness(0, 4, 6, 0);

    public Visibility CompactPinVisibility =>
        _isCompactView && _model.IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactQuickSlotVisibility =>
        _isCompactView && _quickSlotIndex is >= 1 and <= 9 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactRightClusterVisibility =>
        _isCompactView && (_model.IsPinned || _quickSlotIndex is >= 1 and <= 9)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool ShowImageCardPreview =>
        _model.Kind == ClipboardItemKind.Image ||
        (!_isMissingAllFiles && ClipboardClassifier.IsSinglePreviewableImageFile(_model));

    public Visibility ImagePreviewVisibility =>
        ShowImageCardPreview ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HeroImageVisibility =>
        ShowImageCardPreview && !_isCompactView ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactImageVisibility =>
        ShowImageCardPreview && _isCompactView ? Visibility.Visible : Visibility.Collapsed;

    public string CompactImageText
    {
        get
        {
            if (!string.IsNullOrEmpty(ImageFileCaptionText))
            {
                return ImageFileCaptionText;
            }

            if (_model.ImageWidth > 0 && _model.ImageHeight > 0)
            {
                return $"{_localization.T("Clipboard.Filter.Images")} · {_model.ImageWidth} × {_model.ImageHeight}";
            }

            return _localization.T("Clipboard.Item.ImageTitle");
        }
    }

    public BitmapImage? Thumbnail => _thumbnail;

    public Visibility ThumbnailVisibility =>
        _thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImagePlaceholderVisibility =>
        ShowImageCardPreview && _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public string ImageFileCaptionText =>
        _model.Kind == ClipboardItemKind.Files &&
        ShowImageCardPreview &&
        _model.FilePaths.Count == 1 &&
        !ClipboardClassifier.IsGeneratedOrChatCacheImagePath(_model.FilePaths[0])
            ? GetLeafName(_model.FilePaths[0])
            : string.Empty;

    public Visibility ImageFileCaptionVisibility =>
        string.IsNullOrEmpty(ImageFileCaptionText) ? Visibility.Collapsed : Visibility.Visible;

    public SolidColorBrush? ColorSwatchBrush
    {
        get
        {
            string? hex = _model.ColorHex;
            if (!string.Equals(_cachedColorHex, hex, StringComparison.Ordinal))
            {
                _cachedColorHex = hex;
                _colorSwatchBrush = TryCreateColorBrush(hex);
            }

            return _colorSwatchBrush;
        }
    }

    public Visibility ColorSwatchVisibility =>
        !ShowImageCardPreview &&
        !IsMaskedSecret &&
        _model.Kind == ClipboardItemKind.Text &&
        _model.TextSubKind == ClipboardTextSubKind.Color &&
        !string.IsNullOrWhiteSpace(_model.ColorHex)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ColorBodyVisibility => ColorSwatchVisibility;

    public double ColorSwatchSize => _isCompactView ? 15.0 : 22.0;

    public CornerRadius ColorSwatchCornerRadius =>
        _isCompactView ? new CornerRadius(3.5) : new CornerRadius(5);

    public string ColorPrimaryText =>
        (_model.PlainText ?? _model.SummaryText ?? string.Empty).Trim();

    public string ColorSecondaryText
    {
        get
        {
            if (!TryParseColorHex(_model.ColorHex, out Color c))
            {
                return string.Empty;
            }

            string rgb = c.A == 255
                ? $"RGB({c.R}, {c.G}, {c.B})"
                : $"RGBA({c.R}, {c.G}, {c.B}, {Math.Round(c.A / 255.0, 2):0.##})";
            string hex6 = c.A == 255
                ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            return ColorPrimaryText.StartsWith("#", StringComparison.Ordinal)
                ? rgb
                : $"{hex6} · {rgb}";
        }
    }

    public Visibility CodeBodyVisibility =>
        !_isCompactView &&
        !ShowImageCardPreview &&
        !IsMaskedSecret &&
        _model.Kind == ClipboardItemKind.Text &&
        _model.TextSubKind == ClipboardTextSubKind.Code
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility StandardBodyVisibility =>
        HeroImageVisibility == Visibility.Collapsed &&
        CompactImageVisibility == Visibility.Collapsed &&
        ColorBodyVisibility == Visibility.Collapsed &&
        CodeBodyVisibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public TextWrapping BodyTextWrapping =>
        _isCompactView ? TextWrapping.NoWrap : TextWrapping.Wrap;

    public VerticalAlignment KindPrefixVerticalAlignment =>
        _isCompactView ? VerticalAlignment.Center : VerticalAlignment.Top;

    public Thickness KindPrefixMargin =>
        _isCompactView ? new Thickness(0, 0, 5, 0) : new Thickness(0, 2, 5, 0);

    public string KindPrefixGlyph
    {
        get
        {
            if (IsMaskedSecret)
            {
                return "\uE72E"; // Lock
            }

            if (_model.Kind == ClipboardItemKind.Files && !ShowImageCardPreview)
            {
                return "\uE8B7"; // File/Folder
            }

            if (_model.Kind == ClipboardItemKind.Text && _model.TextSubKind == ClipboardTextSubKind.Path)
            {
                return "\uE8B7"; // Path
            }

            if (_isCompactView && _model.Kind == ClipboardItemKind.Text)
            {
                if (_model.TextSubKind == ClipboardTextSubKind.Code)
                {
                    return "\uE943"; // Code
                }

                if (_model.TextSubKind == ClipboardTextSubKind.Url)
                {
                    return "\uE71B"; // Link
                }
            }

            return string.Empty;
        }
    }

    public Visibility KindPrefixVisibility =>
        string.IsNullOrEmpty(KindPrefixGlyph) ? Visibility.Collapsed : Visibility.Visible;

    public string SemanticBadgeText
    {
        get
        {
            if (IsMaskedSecret)
            {
                return _localization.T("Clipboard.Meta.SensitiveMasked");
            }

            if (_model.Kind == ClipboardItemKind.Text)
            {
                return _model.TextSubKind switch
                {
                    ClipboardTextSubKind.Code => !string.IsNullOrWhiteSpace(_model.CodeLanguage)
                        ? _model.CodeLanguage!
                        : _localization.T("Clipboard.Meta.Code"),
                    ClipboardTextSubKind.Url => _localization.T("Clipboard.Filter.Links"),
                    _ => string.Empty
                };
            }

            return string.Empty;
        }
    }

    public Visibility SemanticBadgeVisibility =>
        string.IsNullOrEmpty(SemanticBadgeText) ? Visibility.Collapsed : Visibility.Visible;

    public string NoteText => _model.Note ?? string.Empty;

    public Visibility NoteVisibility =>
        string.IsNullOrWhiteSpace(_model.Note) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsMissingAllFiles => _isMissingAllFiles;

    public double ContentOpacity => IsMissingAllFiles ? 0.52 : 1.0;

    public TextDecorations TitleDecorations =>
        IsMissingAllFiles ? TextDecorations.Strikethrough : TextDecorations.None;

    public string DisplayText
    {
        get
        {
            if (IsMaskedSecret)
            {
                return ClipboardClassifier.MaskSecretForDisplay(
                    !string.IsNullOrEmpty(_model.PlainText) ? _model.PlainText : _model.SummaryText);
            }

            if (_model.Kind == ClipboardItemKind.Text && _model.TextSubKind == ClipboardTextSubKind.Url)
            {
                if (!string.IsNullOrWhiteSpace(_model.UrlTitle))
                {
                    return _model.UrlTitle!;
                }

                string rawUrl = !string.IsNullOrWhiteSpace(_model.PlainText) ? _model.PlainText : _model.SummaryText;
                return ClipboardClassifier.FormatReadableUrl(rawUrl, 96);
            }

            return _model.Kind switch
            {
                ClipboardItemKind.Image => FormatImagePrimaryText(),
                ClipboardItemKind.Files => FormatFilesPrimaryText(_isCompactView),
                _ => FormatTextSnippet(_isCompactView)
            };
        }
    }

    public string UrlSubtitleText =>
        !_isCompactView &&
        !IsMaskedSecret &&
        _model.Kind == ClipboardItemKind.Text &&
        _model.TextSubKind == ClipboardTextSubKind.Url &&
        !string.IsNullOrWhiteSpace(_model.UrlTitle)
            ? ClipboardClassifier.FormatReadableUrl(_model.PlainText ?? _model.SummaryText, 84)
            : string.Empty;

    public Visibility UrlSubtitleVisibility =>
        _isCompactView || string.IsNullOrEmpty(UrlSubtitleText) ? Visibility.Collapsed : Visibility.Visible;

    public BitmapImage? SourceAppIcon => _showSourceApp ? _sourceAppIcon : null;

    public Visibility SourceAppIconVisibility =>
        _showSourceApp && _sourceAppIcon is not null ? Visibility.Visible : Visibility.Collapsed;

    public string MetaText
    {
        get
        {
            var parts = new List<string>(2);
            if (_showSourceApp)
            {
                string cleanApp = ClipboardClassifier.NormalizeFriendlyAppDisplayName(_model.SourceAppName);
                if (!string.IsNullOrWhiteSpace(cleanApp))
                {
                    parts.Add(cleanApp);
                }
            }

            string detail = BuildSecondaryDetail();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                parts.Add(detail);
            }

            return string.Join(" · ", parts);
        }
    }

    public Visibility MetaVisibility =>
        string.IsNullOrWhiteSpace(MetaText) &&
        SourceAppIconVisibility == Visibility.Collapsed &&
        SemanticBadgeVisibility == Visibility.Collapsed &&
        NoteVisibility == Visibility.Collapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string RelativeTimeText => FormatRelativeTime(_model.LastUsedAtUtc, _localization);

    public Visibility PinnedBadgeVisibility =>
        _model.IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PeekSelectionVisibility =>
        _isPeekSelected || _isMultiSelected ? Visibility.Visible : Visibility.Collapsed;

    public bool IsMultiSelectMode => _isMultiSelectMode;

    public bool IsMultiSelected => _isMultiSelected;

    public Visibility MultiSelectCheckVisibility =>
        _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MultiSelectEmptyRingVisibility =>
        _isMultiSelectMode && !_isMultiSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MultiSelectFilledBadgeVisibility =>
        _isMultiSelectMode && _isMultiSelected ? Visibility.Visible : Visibility.Collapsed;

    public string MultiSelectBadgeText =>
        _isMultiSelected && _multiSelectOrder > 0
            ? _multiSelectOrder.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    public Visibility PasteQueueBadgeVisibility =>
        !_isMultiSelectMode && _pasteQueueOrder > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string PasteQueueBadgeText =>
        _pasteQueueOrder > 0 ? $"#{_pasteQueueOrder}" : string.Empty;

    public bool CanExtractOcr => ShowImageCardPreview;

    public bool HasCachedOcr => !string.IsNullOrWhiteSpace(_model.OcrText);

    public Visibility OcrBadgeVisibility =>
        !_isCompactView && HasCachedOcr ? Visibility.Visible : Visibility.Collapsed;

    public string QuickActionGlyph
    {
        get
        {
            if (_model.Kind == ClipboardItemKind.Text && _model.TextSubKind == ClipboardTextSubKind.Url)
            {
                return "\uE8A7"; // OpenInNewWindow
            }

            if (_model.Kind == ClipboardItemKind.Image)
            {
                return "\uE74E"; // Save
            }

            if (_model.Kind == ClipboardItemKind.Files && !_isMissingAllFiles)
            {
                return "\uEC50"; // Show in Explorer
            }

            if (_model.Kind == ClipboardItemKind.Text &&
                _model.TextSubKind is ClipboardTextSubKind.Html or ClipboardTextSubKind.Rtf)
            {
                return "\uE8E4"; // Copy plain text
            }

            return string.Empty;
        }
    }

    public string QuickActionTooltip
    {
        get
        {
            if (_model.Kind == ClipboardItemKind.Text && _model.TextSubKind == ClipboardTextSubKind.Url)
            {
                return _localization.T("Clipboard.Action.OpenLink");
            }

            if (_model.Kind == ClipboardItemKind.Image)
            {
                return _localization.T("Clipboard.Action.SaveImageAs");
            }

            if (_model.Kind == ClipboardItemKind.Files && !_isMissingAllFiles)
            {
                return _localization.T("Clipboard.Action.ShowInExplorer");
            }

            if (_model.Kind == ClipboardItemKind.Text &&
                _model.TextSubKind is ClipboardTextSubKind.Html or ClipboardTextSubKind.Rtf)
            {
                return _localization.T("Clipboard.Action.CopyPlainText");
            }

            return string.Empty;
        }
    }

    public Visibility QuickActionVisibility =>
        string.IsNullOrEmpty(QuickActionGlyph) ? Visibility.Collapsed : Visibility.Visible;

    public string PeekEditTooltip => _localization.T("Clipboard.Action.EditAndNote");

    public string CopyButtonGlyph => _isJustCopied ? "\uE73E" : "\uE8C8";

    public string PinTooltip => _model.IsPinned
        ? _localization.T("Clipboard.Action.Unpin")
        : _localization.T("Clipboard.Action.Pin");

    public string CopyTooltip => _isJustCopied
        ? _localization.T("Clipboard.Feedback.Copied")
        : _localization.T("Clipboard.Action.Copy");

    public string DeleteTooltip => _localization.T("Clipboard.Action.Delete");

    public void UpdateFrom(
        ClipboardHistoryItem model,
        string secretProtectionMode,
        bool showSourceApp,
        double textSize,
        double secondaryTextSize,
        int previewMaxLines,
        Thickness cardPadding,
        double cardMinHeight,
        double imageBannerMaxHeight = 96,
        bool isCurrentClipboard = false,
        int quickSlotIndex = 0,
        bool isCompactView = false)
    {
        bool imageChanged = !string.Equals(_model.ImageRelativePath, model.ImageRelativePath, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_model.ThumbnailRelativePath, model.ThumbnailRelativePath, StringComparison.OrdinalIgnoreCase);
        bool sourceAppChanged = !string.Equals(_model.SourceAppPath, model.SourceAppPath, StringComparison.OrdinalIgnoreCase);
        bool colorChanged = !string.Equals(_model.ColorHex, model.ColorHex, StringComparison.Ordinal);

        _model = model;
        _secretProtectionMode = secretProtectionMode;
        _showSourceApp = showSourceApp;
        _textSize = textSize;
        _secondaryTextSize = secondaryTextSize;
        _previewMaxLines = previewMaxLines;
        _cardPadding = cardPadding;
        _cardMinHeight = cardMinHeight;
        _imageBannerMaxHeight = imageBannerMaxHeight;
        _isCurrentClipboard = isCurrentClipboard;
        _quickSlotIndex = quickSlotIndex;
        _isCompactView = isCompactView;

        if (colorChanged)
        {
            _cachedColorHex = null;
            _colorSwatchBrush = null;
        }

        if (imageChanged || sourceAppChanged)
        {
            _visualAssetGeneration++;
        }

        if (imageChanged)
        {
            _thumbnail = null;
            _thumbnailLoadRequested = false;
        }

        if (sourceAppChanged)
        {
            _sourceAppIcon = null;
            _sourceIconLoadRequested = false;
        }

        if (_model.Kind != ClipboardItemKind.Files || _model.FilePaths.Count == 0)
        {
            _isMissingAllFiles = false;
        }

        OnPropertyChanged(string.Empty);
    }

    public void RefreshTimeDisplay()
    {
        OnPropertyChanged(nameof(RelativeTimeText));
    }

    internal async Task RefreshMissingFilesStateAsync(Action<Action>? uiDispatch)
    {
        if (_model.Kind != ClipboardItemKind.Files || _model.FilePaths.Count == 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _missingFilesCheckInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            string[] snapshotPaths = _model.FilePaths.ToArray();
            bool missingAll = await Task.Run(
                () => snapshotPaths.All(static path => !File.Exists(path) && !Directory.Exists(path)))
                .ConfigureAwait(false);

            void Apply()
            {
                if (_isMissingAllFiles == missingAll)
                {
                    return;
                }

                _isMissingAllFiles = missingAll;
                OnPropertyChanged(string.Empty);
            }

            if (uiDispatch is not null)
            {
                uiDispatch(Apply);
            }
            else
            {
                Apply();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _missingFilesCheckInFlight, 0);
        }
    }

    public void SetJustCopied(bool value)
    {
        if (_isJustCopied == value)
        {
            return;
        }

        _isJustCopied = value;
        OnPropertyChanged(nameof(CopyButtonGlyph));
        OnPropertyChanged(nameof(CopyTooltip));
    }

    public void SetPeekSelected(bool value)
    {
        if (_isPeekSelected == value)
        {
            return;
        }

        _isPeekSelected = value;
        OnPropertyChanged(nameof(PeekSelectionVisibility));
    }

    public void SetMultiSelectState(bool isMultiSelectMode, bool isSelected, int selectionOrder)
    {
        if (_isMultiSelectMode == isMultiSelectMode &&
            _isMultiSelected == isSelected &&
            _multiSelectOrder == selectionOrder)
        {
            return;
        }

        _isMultiSelectMode = isMultiSelectMode;
        _isMultiSelected = isSelected;
        _multiSelectOrder = selectionOrder;
        OnPropertyChanged(nameof(IsMultiSelectMode));
        OnPropertyChanged(nameof(IsMultiSelected));
        OnPropertyChanged(nameof(PeekSelectionVisibility));
        OnPropertyChanged(nameof(MultiSelectCheckVisibility));
        OnPropertyChanged(nameof(MultiSelectEmptyRingVisibility));
        OnPropertyChanged(nameof(MultiSelectFilledBadgeVisibility));
        OnPropertyChanged(nameof(MultiSelectBadgeText));
        OnPropertyChanged(nameof(PasteQueueBadgeVisibility));
    }

    public void SetPasteQueueOrder(int order)
    {
        if (_pasteQueueOrder == order)
        {
            return;
        }

        _pasteQueueOrder = order;
        OnPropertyChanged(nameof(PasteQueueBadgeVisibility));
        OnPropertyChanged(nameof(PasteQueueBadgeText));
    }

    public async Task EnsureVisualAssetsLoadedAsync()
    {
        int generation = _visualAssetGeneration;

        if (ShowImageCardPreview && !_thumbnailLoadRequested && _thumbnailLoader is not null)
        {
            _thumbnailLoadRequested = true;
            string? previewPath = _model.Kind == ClipboardItemKind.Image
                ? _thumbnailPathResolver?.Invoke(_model)
                : _model.FilePaths.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                bool canLoad = _model.Kind == ClipboardItemKind.Image ||
                               await Task.Run(() => File.Exists(previewPath)).ConfigureAwait(true);
                if (generation != _visualAssetGeneration)
                {
                    return;
                }

                if (canLoad)
                {
                    BitmapImage? loaded = await _thumbnailLoader(previewPath, 320).ConfigureAwait(true);
                    if (generation != _visualAssetGeneration)
                    {
                        return;
                    }

                    if (loaded is not null)
                    {
                        _thumbnail = loaded;
                        OnPropertyChanged(nameof(Thumbnail));
                        OnPropertyChanged(nameof(ThumbnailVisibility));
                        OnPropertyChanged(nameof(ImagePlaceholderVisibility));
                    }
                }
            }
        }

        if (!_isCompactView &&
            _showSourceApp &&
            !_sourceIconLoadRequested &&
            _iconLoader is not null &&
            !string.IsNullOrWhiteSpace(_model.SourceAppPath))
        {
            _sourceIconLoadRequested = true;
            string sourcePath = _model.SourceAppPath;
            bool exists = await Task.Run(() => File.Exists(sourcePath)).ConfigureAwait(true);
            if (generation != _visualAssetGeneration)
            {
                return;
            }

            if (exists)
            {
                BitmapImage? icon = await _iconLoader(sourcePath, 32).ConfigureAwait(true);
                if (generation != _visualAssetGeneration)
                {
                    return;
                }

                if (icon is not null)
                {
                    _sourceAppIcon = icon;
                    OnPropertyChanged(nameof(SourceAppIcon));
                    OnPropertyChanged(nameof(SourceAppIconVisibility));
                    OnPropertyChanged(nameof(MetaVisibility));
                }
            }
        }
    }

    public void ReleaseVisualAssets()
    {
        _visualAssetGeneration++;
        bool hadAssets = _thumbnail is not null || _sourceAppIcon is not null;
        _thumbnail = null;
        _sourceAppIcon = null;
        _thumbnailLoadRequested = false;
        _sourceIconLoadRequested = false;
        if (hadAssets)
        {
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(ThumbnailVisibility));
            OnPropertyChanged(nameof(ImagePlaceholderVisibility));
            OnPropertyChanged(nameof(SourceAppIcon));
            OnPropertyChanged(nameof(SourceAppIconVisibility));
        }
    }

    private string FormatImagePrimaryText()
    {
        if (_model.ImageWidth > 0 && _model.ImageHeight > 0)
        {
            return $"{_model.ImageWidth} × {_model.ImageHeight}";
        }

        return _localization.T("Clipboard.Item.ImageTitle");
    }

    private string FormatTextSnippet(bool singleLineCompact)
    {
        string raw = !string.IsNullOrWhiteSpace(_model.SummaryText)
            ? _model.SummaryText
            : (_model.PlainText ?? string.Empty).Trim();
        if (!singleLineCompact || (raw.IndexOf('\n') < 0 && raw.IndexOf('\r') < 0))
        {
            return raw;
        }

        return string.Join(
            ' ',
            raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private string FormatFilesPrimaryText(bool singleLineCompact = false)
    {
        if (_model.FilePaths.Count == 0)
        {
            return _localization.T("Clipboard.Item.FilesTitle");
        }

        string first = GetLeafName(_model.FilePaths[0]);
        if (_model.FilePaths.Count == 1)
        {
            return first;
        }

        if (singleLineCompact)
        {
            return $"{first} (+{_model.FilePaths.Count - 1})";
        }

        string second = GetLeafName(_model.FilePaths[1]);
        if (_model.FilePaths.Count == 2)
        {
            return $"{first}\n{second}";
        }

        int extra = _model.FilePaths.Count - 2;
        return $"{first}\n{second} (+{extra})";
    }

    private string BuildSecondaryDetail()
    {
        if (IsMissingAllFiles)
        {
            return _localization.T("Clipboard.Meta.FileMissing");
        }

        if (IsMaskedSecret)
        {
            return string.Empty;
        }

        switch (_model.Kind)
        {
            case ClipboardItemKind.Image:
            {
                string dims = _model.ImageWidth > 0 && _model.ImageHeight > 0
                    ? $"{_model.ImageWidth} × {_model.ImageHeight}"
                    : "PNG";
                return _model.ByteSize > 0
                    ? $"{dims} · {FormatByteSize(_model.ByteSize)}"
                    : dims;
            }

            case ClipboardItemKind.Files:
                if (ShowImageCardPreview)
                {
                    string ext = _model.FilePaths.Count > 0
                        ? Path.GetExtension(_model.FilePaths[0]).TrimStart('.').ToUpperInvariant()
                        : "IMG";
                    string dimsOrExt = _model.ImageWidth > 0 && _model.ImageHeight > 0
                        ? $"{_model.ImageWidth} × {_model.ImageHeight}"
                        : (string.IsNullOrEmpty(ext) ? "IMG" : ext);
                    return _model.ByteSize > 0
                        ? $"{dimsOrExt} · {FormatByteSize(_model.ByteSize)}"
                        : dimsOrExt;
                }

                if (_model.FilePaths.Count > 1)
                {
                    string countText = _localization.Format("Clipboard.Meta.FileCount", _model.FilePaths.Count);
                    return _model.ByteSize > 0
                        ? $"{countText} · {FormatByteSize(_model.ByteSize)}"
                        : countText;
                }

                if (_model.FilePaths.Count == 1)
                {
                    string? parent = Path.GetDirectoryName(_model.FilePaths[0]);
                    string folder = !string.IsNullOrWhiteSpace(parent) ? GetLeafName(parent) : string.Empty;
                    if (_model.ByteSize > 0)
                    {
                        return string.IsNullOrEmpty(folder)
                            ? FormatByteSize(_model.ByteSize)
                            : $"{folder} · {FormatByteSize(_model.ByteSize)}";
                    }

                    return folder;
                }

                return string.Empty;

            default:
                if (_model.TextSubKind == ClipboardTextSubKind.Color)
                {
                    return string.Empty;
                }

                if (_model.TextSubKind == ClipboardTextSubKind.Url &&
                    Uri.TryCreate(_model.PlainText?.Trim(), UriKind.Absolute, out Uri? uri) &&
                    !string.IsNullOrWhiteSpace(uri.Host))
                {
                    return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                        ? uri.Host[4..]
                        : uri.Host;
                }

                if (_model.LineCount > 1)
                {
                    return _localization.Format("Clipboard.Meta.LinesAndChars", _model.LineCount, _model.CharacterCount);
                }

                if (_model.CharacterCount >= 80)
                {
                    return _localization.Format("Clipboard.Meta.Chars", _model.CharacterCount);
                }

                return string.Empty;
        }
    }

    internal static string GetLeafName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    internal static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return kb >= 100 ? $"{kb:0} KB" : $"{kb:0.0} KB";
        }

        double mb = kb / 1024.0;
        return $"{mb:0.0} MB";
    }

    private static bool IsPreviewableImageExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static SolidColorBrush? TryCreateColorBrush(string? normalizedArgbHex)
    {
        return TryParseColorHex(normalizedArgbHex, out Color color)
            ? new SolidColorBrush(color)
            : null;
    }

    private static bool TryParseColorHex(string? normalizedArgbHex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(normalizedArgbHex))
        {
            return false;
        }

        ReadOnlySpan<char> hex = normalizedArgbHex.AsSpan().Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
        {
            hex = hex[1..];
        }

        if (hex.Length == 8 &&
            byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a) &&
            byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r6) &&
            byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g6) &&
            byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b6))
        {
            color = Color.FromArgb(255, r6, g6, b6);
            return true;
        }

        return false;
    }

    private static string FormatRelativeTime(DateTime utcTime, LocalizationService localization)
    {
        if (utcTime == default)
        {
            return string.Empty;
        }

        TimeSpan elapsed = DateTime.UtcNow - utcTime.ToUniversalTime();
        if (elapsed.TotalSeconds < 60)
        {
            return localization.T("Clipboard.Time.JustNow");
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h";
        }

        DateTime local = utcTime.ToLocalTime();
        if (elapsed.TotalDays < 2 && local.Date == DateTime.Now.Date.AddDays(-1))
        {
            return localization.T("Clipboard.Time.Yesterday");
        }

        return local.Year == DateTime.Now.Year
            ? local.ToString("MM/dd", CultureInfo.InvariantCulture)
            : local.ToString("yy/MM/dd", CultureInfo.InvariantCulture);
    }
}

[Bindable]
public sealed partial class ClipboardWidgetViewModel : ObservableObject, IDisposable
{
    public const string IconCacheScope = "clipboard-widget";
    public const int MaxPeekTextChars = 16384;

    private readonly ClipboardHistoryService? _service;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Action<Action>? _uiDispatch;
    private readonly Func<string, int, Task<BitmapImage?>>? _iconLoader;
    private readonly Func<string, int, Task<BitmapImage?>>? _thumbnailLoader;
    private readonly Dictionary<string, ClipboardItemViewModel> _itemPool = new(StringComparer.Ordinal);
    private readonly List<string> _selectedIdsInOrder = [];
    private string? _lastMultiSelectAnchorId;
    private bool _isMultiSelectMode;
    private ClipboardFilterKind _activeFilter = ClipboardFilterKind.All;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;
    private bool _useShortFilterLabels;
    private bool _isInitialized;
    private bool _isViewAttached = true;
    private bool _isDirty;
    private bool _isDisposed;
    private int _capsuleRevision;

    public int CapsuleRevision => _capsuleRevision;

    public bool IsViewAttached => _isViewAttached;

    internal bool HasPendingRebuild => _isDirty;

    public ClipboardWidgetViewModel(
        WidgetConfig config,
        ClipboardHistoryService? service,
        SettingsService settingsService,
        LocalizationService localizationService,
        Action<Action>? uiDispatch = null,
        Func<string, int, Task<BitmapImage?>>? iconLoader = null,
        Func<string, int, Task<BitmapImage?>>? thumbnailLoader = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);

        Config = config;
        _service = service;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _uiDispatch = uiDispatch;
        _iconLoader = iconLoader;
        _thumbnailLoader = thumbnailLoader;

        if (_service is not null)
        {
            _service.ItemsChanged += Service_ItemsChanged;
            _service.MonitoringStateChanged += Service_MonitoringStateChanged;
            _service.PasteQueueChanged += Service_PasteQueueChanged;
        }

        _settingsService.SettingsChanged += SettingsService_SettingsChanged;
        _localizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    public event EventHandler? SummaryChanged;

    public WidgetConfig Config { get; }

    public ObservableCollection<ClipboardItemViewModel> VisibleItems { get; } = [];

    public bool IsInitialized => _isInitialized;

    public ClipboardFilterKind ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (_activeFilter == value)
            {
                return;
            }

            _activeFilter = value;
            OnPropertyChanged();
            RebuildVisibleItems();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = next;
            OnPropertyChanged();
            RebuildVisibleItems();
        }
    }

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (_isSearchOpen == value)
            {
                return;
            }

            _isSearchOpen = value;
            if (!value && !string.IsNullOrEmpty(_searchText))
            {
                _searchText = string.Empty;
                OnPropertyChanged(nameof(SearchText));
                RebuildVisibleItems();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(TopFilterBarVisibility));
            OnPropertyChanged(nameof(SearchBoxVisibility));
        }
    }

    public bool IsPaused => _service?.IsPaused ?? false;

    public Visibility PausedBannerVisibility => IsPaused ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusBannersVisibility => IsPaused || IsPasteQueueActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TopFilterBarVisibility => _isSearchOpen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SearchBoxVisibility => _isSearchOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => VisibleItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public double TextSize => Math.Clamp(_settingsService.Settings.WidgetShell.TextSize, 11, 20);

    public double SecondaryTextSize => Math.Max(11.5, TextSize - 1.5);

    public double TitleTextSize => Math.Min(20, TextSize + 2);

    public double SegmentTextSize => Math.Clamp(TextSize - 0.5, 11.5, 13.5);

    public double SegmentHeight => 28;

    public double InputHeight => 28;

    public Thickness RootPadding => new(10, 10, 10, 10);

    public string TabStyle => SettingsService.WidgetTabStylePivot;

    public string ClickBehavior => _settingsService.Settings.Clipboard.ClipboardClickBehavior;

    public bool DefaultPastePlainText => _settingsService.Settings.Clipboard.ClipboardDefaultPastePlainText;

    public bool IsCompactViewMode =>
        string.Equals(
            _settingsService.Settings.Clipboard.ClipboardViewMode,
            ClipboardSettingsSlice.ViewModeCompact,
            StringComparison.OrdinalIgnoreCase);

    public string ViewMode => IsCompactViewMode
        ? ClipboardSettingsSlice.ViewModeCompact
        : ClipboardSettingsSlice.ViewModeStandard;

    public string ViewModeToggleGlyph => IsCompactViewMode ? "\uE8A1" : "\uE8FD";

    public string ViewModeToggleTooltip => _localizationService.T(
        IsCompactViewMode
            ? "Clipboard.Action.SwitchToStandardView"
            : "Clipboard.Action.SwitchToCompactView");

    public string TabDisplayMode =>
        ClipboardSettingsSlice.NormalizeTabDisplayMode(
            _settingsService.Settings.Clipboard.ClipboardTabDisplayMode);

    public Visibility FilterIconVisibility =>
        string.Equals(TabDisplayMode, ClipboardSettingsSlice.TabDisplayTextOnly, StringComparison.Ordinal)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility FilterLabelVisibility =>
        string.Equals(TabDisplayMode, ClipboardSettingsSlice.TabDisplayIconOnly, StringComparison.Ordinal)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public double FilterIconFontSize =>
        string.Equals(TabDisplayMode, ClipboardSettingsSlice.TabDisplayIconOnly, StringComparison.Ordinal)
            ? 12.5
            : 11.5;

    public Thickness FilterTabButtonPadding =>
        string.Equals(TabDisplayMode, ClipboardSettingsSlice.TabDisplayIconOnly, StringComparison.Ordinal)
            ? new Thickness(8, 0, 8, 0)
            : new Thickness(9, 0, 9, 0);

    public double FilterTabButtonMinWidth =>
        string.Equals(TabDisplayMode, ClipboardSettingsSlice.TabDisplayIconOnly, StringComparison.Ordinal)
            ? 30
            : 0;

    public string FilterAllText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.AllShort" : "Clipboard.Filter.All");
    public string FilterTextText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.TextShort" : "Clipboard.Filter.Text");
    public string FilterImagesText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.ImagesShort" : "Clipboard.Filter.Images");
    public string FilterLinksText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.LinksShort" : "Clipboard.Filter.Links");
    public string FilterFilesText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.FilesShort" : "Clipboard.Filter.Files");
    public string FilterPinnedText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.PinnedShort" : "Clipboard.Filter.Pinned");

    public string FilterAllTooltip => _localizationService.T("Clipboard.Filter.All");
    public string FilterTextTooltip => _localizationService.T("Clipboard.Filter.Text");
    public string FilterImagesTooltip => _localizationService.T("Clipboard.Filter.Images");
    public string FilterLinksTooltip => _localizationService.T("Clipboard.Filter.Links");
    public string FilterFilesTooltip => _localizationService.T("Clipboard.Filter.Files");
    public string FilterPinnedTooltip => _localizationService.T("Clipboard.Filter.Pinned");

    public string SearchPlaceholderText => _localizationService.T("Clipboard.Search.Placeholder");
    public string SearchCancelText => _localizationService.T("Common.Cancel");
    public string PauseToggleGlyph => IsPaused ? "\uE768" : "\uE769";
    public string PauseToggleTooltip => IsPaused
        ? _localizationService.T("Clipboard.Action.ResumeCapture")
        : _localizationService.T("Clipboard.Action.PauseCapture");
    public string PausedBannerText => _localizationService.T("Clipboard.Banner.Paused");
    public string ResumeActionText => _localizationService.T("Clipboard.Action.Resume");

    public bool IsMultiSelectMode => _isMultiSelectMode;
    public Visibility MultiSelectBarVisibility => _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
    public int MultiSelectedCount => _selectedIdsInOrder.Count;
    public bool HasMultiSelection => _selectedIdsInOrder.Count > 0;
    public string MultiSelectStatusText => _localizationService.Format("Clipboard.MultiSelect.SelectedCount", _selectedIdsInOrder.Count);
    public string MergeCopyLabel => _localizationService.T("Clipboard.Action.MergeCopy");
    public string MergeSaveQuickCaptureTooltip => _localizationService.T("Clipboard.Action.MergeSaveToQuickCapture");
    public string EnqueueSelectedTooltip => _localizationService.T("Clipboard.Action.EnqueuePasteQueue");
    public string BatchPinTooltip => _localizationService.T("Clipboard.Action.BatchPin");
    public string BatchDeleteTooltip => _localizationService.T("Clipboard.Action.BatchDelete");
    public string ExitMultiSelectTooltip => _localizationService.T("Common.Cancel");

    public bool IsPasteQueueActive => _service?.IsPasteQueueActive ?? false;
    public bool IsPasteQueueLiveCapture => _service?.IsPasteQueueLiveCapture ?? false;
    public int PasteQueueCount => _service?.PasteQueueCount ?? 0;
    public Visibility PasteQueueBannerVisibility => IsPasteQueueActive ? Visibility.Visible : Visibility.Collapsed;
    public bool CanPasteOrSkipQueue => PasteQueueCount > 0;
    public string PasteQueueToggleTooltip => _localizationService.T(
        IsPasteQueueActive ? "Clipboard.PasteQueue.StopTooltip" : "Clipboard.PasteQueue.StartTooltip");
    public string PasteQueuePasteText => _localizationService.T("Clipboard.Action.Paste");
    public string PasteQueueSkipText => _localizationService.T("Clipboard.PasteQueue.Skip");
    public string PasteQueueStopTooltip => _localizationService.T("Clipboard.PasteQueue.StopTooltip");

    public string PasteQueueBannerTitle => PasteQueueCount > 0
        ? _localizationService.Format("Clipboard.PasteQueue.RemainingFormat", PasteQueueCount)
        : _localizationService.T("Clipboard.PasteQueue.WaitingCapture");

    public string PasteQueueHeadPreview
    {
        get
        {
            IReadOnlyList<ClipboardHistoryItem> queue = _service?.GetPasteQueueSnapshot() ?? [];
            if (queue.Count == 0)
            {
                return _localizationService.T("Clipboard.PasteQueue.LiveHint");
            }

            ClipboardHistoryItem head = queue[0];
            string summary = head.Kind switch
            {
                ClipboardItemKind.Image => head.ImageWidth > 0 && head.ImageHeight > 0
                    ? $"[{_localizationService.T("Clipboard.Filter.Images")} {head.ImageWidth}×{head.ImageHeight}]"
                    : $"[{_localizationService.T("Clipboard.Filter.Images")}]",
                ClipboardItemKind.Files => head.SummaryText,
                _ => (head.SummaryText ?? head.PlainText ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim()
            };
            return summary.Length > 48 ? string.Concat(summary.AsSpan(0, 48), "…") : summary;
        }
    }

    public string EmptyStateGlyph => !string.IsNullOrWhiteSpace(_searchText) ? "\uE721" : "\uE77F";

    public string EmptyStateTitle => !string.IsNullOrWhiteSpace(_searchText)
        ? _localizationService.T("Clipboard.Empty.NoMatchTitle")
        : _activeFilter == ClipboardFilterKind.Pinned
            ? _localizationService.T("Clipboard.Empty.NoPinnedTitle")
            : _localizationService.T("Clipboard.Empty.Title");

    public string EmptyStateSubtitle => !string.IsNullOrWhiteSpace(_searchText)
        ? _localizationService.T("Clipboard.Empty.NoMatchSubtitle")
        : _activeFilter == ClipboardFilterKind.Pinned
            ? _localizationService.T("Clipboard.Empty.NoPinnedSubtitle")
            : _localizationService.T("Clipboard.Empty.Subtitle");

    public int TotalItemCount => _service?.Items.Count ?? 0;

    public async Task InitializeAsync()
    {
        if (_service is not null)
        {
            await _service.EnsureLoadedAsync().ConfigureAwait(true);
        }

        _isInitialized = true;
        RebuildVisibleItems();
    }

    public void SetCompactFilterLabels(bool useShortLabels)
    {
        if (_useShortFilterLabels == useShortLabels)
        {
            return;
        }

        _useShortFilterLabels = useShortLabels;
        NotifyLocalizedStringsChanged();
    }

    public void UpdateResponsiveFilterChrome(double rootWidth)
    {
        if (rootWidth <= 0)
        {
            return;
        }

        // The Fluent 2 filter strip scrolls horizontally, so tabs never force-collapse
        // based on width; user-chosen TabDisplayMode controls icon/text visibility.
        if (_useShortFilterLabels)
        {
            _useShortFilterLabels = false;
            NotifyLocalizedStringsChanged();
        }
    }

    public void SetTabDisplayMode(string tabDisplayMode)
    {
        string normalized = ClipboardSettingsSlice.NormalizeTabDisplayMode(tabDisplayMode);
        if (string.Equals(_settingsService.Settings.Clipboard.ClipboardTabDisplayMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.Clipboard.ClipboardTabDisplayMode = normalized;
        NotifyFilterTabDisplayModeChanged();
        _settingsService.SaveDebounced();
    }

    private void NotifyFilterTabDisplayModeChanged()
    {
        OnPropertyChanged(nameof(TabDisplayMode));
        OnPropertyChanged(nameof(FilterIconVisibility));
        OnPropertyChanged(nameof(FilterLabelVisibility));
        OnPropertyChanged(nameof(FilterIconFontSize));
        OnPropertyChanged(nameof(FilterTabButtonPadding));
        OnPropertyChanged(nameof(FilterTabButtonMinWidth));
    }

    public void SetViewMode(string viewMode)
    {
        string normalized = string.Equals(viewMode, ClipboardSettingsSlice.ViewModeCompact, StringComparison.OrdinalIgnoreCase)
            ? ClipboardSettingsSlice.ViewModeCompact
            : ClipboardSettingsSlice.ViewModeStandard;

        if (string.Equals(_settingsService.Settings.Clipboard.ClipboardViewMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.Clipboard.ClipboardViewMode = normalized;
        OnPropertyChanged(nameof(IsCompactViewMode));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(ViewModeToggleGlyph));
        OnPropertyChanged(nameof(ViewModeToggleTooltip));
        RebuildVisibleItems();
        _settingsService.SaveDebounced();
    }

    public void ToggleViewMode()
    {
        SetViewMode(IsCompactViewMode
            ? ClipboardSettingsSlice.ViewModeStandard
            : ClipboardSettingsSlice.ViewModeCompact);
    }

    public void TogglePause()
    {
        if (_service is null)
        {
            return;
        }

        _service.SetPaused(!_service.IsPaused);
    }

    public void ResumeMonitoring()
    {
        _service?.SetPaused(false);
    }

    public async Task<bool> CopyItemAsync(ClipboardItemViewModel item, bool forcePlainText = false)
    {
        if (_service is null)
        {
            return false;
        }

        return await _service.CopyItemToClipboardAsync(item.Id, forcePlainText).ConfigureAwait(true);
    }

    public async Task<(bool Copied, bool Pasted)> PasteItemAsync(ClipboardItemViewModel item, bool forcePlainText = false)
    {
        if (_service is null)
        {
            return (false, false);
        }

        return await _service.PasteItemToExternalWindowAsync(item.Id, forcePlainText).ConfigureAwait(true);
    }

    public async Task TogglePinAsync(ClipboardItemViewModel item)
    {
        if (_service is not null)
        {
            await _service.TogglePinAsync(item.Id).ConfigureAwait(true);
        }
    }

    public async Task<bool> UpdateItemNoteAsync(ClipboardItemViewModel item, string? note)
    {
        return _service is not null &&
               await _service.UpdateItemNoteAsync(item.Id, note).ConfigureAwait(true);
    }

    public async Task<bool> UpdateItemTextAsync(ClipboardItemViewModel item, string newText, bool copyAfterSave)
    {
        if (_service is null)
        {
            return false;
        }

        ClipboardHistoryItem? updated = await _service.UpdateItemTextAsync(item.Id, newText).ConfigureAwait(true);
        if (updated is null)
        {
            return false;
        }

        if (copyAfterSave)
        {
            await _service.CopyItemToClipboardAsync(updated, forcePlainText: DefaultPastePlainText).ConfigureAwait(true);
        }

        return true;
    }

    public async Task DeleteItemAsync(ClipboardItemViewModel item)
    {
        if (_service is not null)
        {
            await _service.DeleteItemAsync(item.Id).ConfigureAwait(true);
        }
    }

    public async Task<int> ClearUnpinnedAsync()
    {
        return _service is not null
            ? await _service.ClearUnpinnedAsync().ConfigureAwait(true)
            : 0;
    }

    public string? ResolvePreviewableImagePath(ClipboardItemViewModel item) =>
        _service?.ResolvePreviewableImagePath(item.Model);

    public async Task<string?> ExtractOcrTextAsync(
        ClipboardItemViewModel item,
        bool copyToClipboard = true,
        CancellationToken cancellationToken = default)
    {
        if (_service is null)
        {
            return null;
        }

        string? text = await _service.ExtractAndCacheOcrTextAsync(item.Id, cancellationToken).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text) && copyToClipboard)
        {
            await _service.CopyGeneratedTextToClipboardAsync(text).ConfigureAwait(true);
        }

        return text;
    }

    public async Task<bool> CopyGeneratedTextAsync(string? text)
    {
        return _service is not null &&
               !string.IsNullOrEmpty(text) &&
               await _service.CopyGeneratedTextToClipboardAsync(text).ConfigureAwait(true);
    }

    public async Task<string?> TryGetCleanTrackingUrlAsync(ClipboardItemViewModel item)
    {
        string full = await GetFullTextAsync(item).ConfigureAwait(true);
        return ClipboardTextTransformService.TryCleanTrackingUrl(full, out string cleaned)
            ? cleaned
            : null;
    }

    public async Task<bool> CopyCleanTrackingUrlAsync(ClipboardItemViewModel item)
    {
        string? cleaned = await TryGetCleanTrackingUrlAsync(item).ConfigureAwait(true);
        return !string.IsNullOrEmpty(cleaned) && await CopyGeneratedTextAsync(cleaned).ConfigureAwait(true);
    }

    public async Task<bool> CopyMergedPdfLinesAsync(ClipboardItemViewModel item)
    {
        string full = await GetFullTextAsync(item).ConfigureAwait(true);
        if (!ClipboardTextTransformService.CanMergePdfLineBreaks(full))
        {
            return false;
        }

        string merged = ClipboardTextTransformService.MergePdfLineBreaks(full);
        return await CopyGeneratedTextAsync(merged).ConfigureAwait(true);
    }

    public async Task<bool> CopyFormattedJsonAsync(ClipboardItemViewModel item, bool indented)
    {
        string full = await GetFullTextAsync(item).ConfigureAwait(true);
        return ClipboardTextTransformService.TryFormatJson(full, indented, out string formatted) &&
               await CopyGeneratedTextAsync(formatted).ConfigureAwait(true);
    }

    public void EnterMultiSelectMode(ClipboardItemViewModel? initialItem = null)
    {
        _isMultiSelectMode = true;
        if (initialItem is not null && !_selectedIdsInOrder.Contains(initialItem.Id, StringComparer.Ordinal))
        {
            _selectedIdsInOrder.Add(initialItem.Id);
            _lastMultiSelectAnchorId = initialItem.Id;
        }

        RefreshItemMultiSelectAndQueueVisuals();
        NotifyMultiSelectChanged();
    }

    public void ExitMultiSelectMode()
    {
        if (!_isMultiSelectMode && _selectedIdsInOrder.Count == 0)
        {
            return;
        }

        _isMultiSelectMode = false;
        _selectedIdsInOrder.Clear();
        _lastMultiSelectAnchorId = null;
        RefreshItemMultiSelectAndQueueVisuals();
        NotifyMultiSelectChanged();
    }

    public void ToggleMultiSelectItem(ClipboardItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_isMultiSelectMode)
        {
            _isMultiSelectMode = true;
        }

        if (!_selectedIdsInOrder.Remove(item.Id))
        {
            _selectedIdsInOrder.Add(item.Id);
            _lastMultiSelectAnchorId = item.Id;
        }
        else if (string.Equals(_lastMultiSelectAnchorId, item.Id, StringComparison.Ordinal))
        {
            _lastMultiSelectAnchorId = _selectedIdsInOrder.LastOrDefault();
        }

        RefreshItemMultiSelectAndQueueVisuals();
        NotifyMultiSelectChanged();
    }

    public void SelectRangeToItem(ClipboardItemViewModel targetItem)
    {
        ArgumentNullException.ThrowIfNull(targetItem);
        if (!_isMultiSelectMode)
        {
            _isMultiSelectMode = true;
        }

        int targetIdx = -1;
        int anchorIdx = -1;
        for (int i = 0; i < VisibleItems.Count; i++)
        {
            if (string.Equals(VisibleItems[i].Id, targetItem.Id, StringComparison.Ordinal))
            {
                targetIdx = i;
            }

            if (_lastMultiSelectAnchorId is not null &&
                string.Equals(VisibleItems[i].Id, _lastMultiSelectAnchorId, StringComparison.Ordinal))
            {
                anchorIdx = i;
            }
        }

        if (targetIdx < 0)
        {
            return;
        }

        if (anchorIdx < 0)
        {
            ToggleMultiSelectItem(targetItem);
            return;
        }

        int step = anchorIdx <= targetIdx ? 1 : -1;
        for (int i = anchorIdx; ; i += step)
        {
            string id = VisibleItems[i].Id;
            if (!_selectedIdsInOrder.Contains(id, StringComparer.Ordinal))
            {
                _selectedIdsInOrder.Add(id);
            }

            if (i == targetIdx)
            {
                break;
            }
        }

        _lastMultiSelectAnchorId = targetItem.Id;
        RefreshItemMultiSelectAndQueueVisuals();
        NotifyMultiSelectChanged();
    }

    public void SelectAllVisible()
    {
        if (VisibleItems.Count == 0)
        {
            return;
        }

        _isMultiSelectMode = true;
        foreach (ClipboardItemViewModel vm in VisibleItems)
        {
            if (!_selectedIdsInOrder.Contains(vm.Id, StringComparer.Ordinal))
            {
                _selectedIdsInOrder.Add(vm.Id);
            }
        }

        _lastMultiSelectAnchorId = VisibleItems[^1].Id;
        RefreshItemMultiSelectAndQueueVisuals();
        NotifyMultiSelectChanged();
    }

    public IReadOnlyList<ClipboardItemViewModel> GetSelectedItemsInOrder()
    {
        var list = new List<ClipboardItemViewModel>(_selectedIdsInOrder.Count);
        foreach (string id in _selectedIdsInOrder)
        {
            if (_itemPool.TryGetValue(id, out ClipboardItemViewModel? vm))
            {
                list.Add(vm);
            }
        }

        return list;
    }

    public async Task<string> BuildMergedTextForSelectedAsync(string separator = "\n")
    {
        IReadOnlyList<ClipboardItemViewModel> selected = GetSelectedItemsInOrder();
        if (selected.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(selected.Count);
        foreach (ClipboardItemViewModel item in selected)
        {
            if (item.Kind == ClipboardItemKind.Text)
            {
                string text = (await GetFullTextAsync(item).ConfigureAwait(true)).TrimEnd('\r', '\n');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }
            else if (item.Kind == ClipboardItemKind.Files && item.Model.FilePaths.Count > 0)
            {
                parts.Add(string.Join(Environment.NewLine, item.Model.FilePaths));
            }
            else if (item.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(item.Model.OcrText))
            {
                parts.Add(item.Model.OcrText.Trim());
            }
        }

        return string.Join(separator, parts);
    }

    public async Task<bool> MergeCopySelectedAsync(string separator = "\n")
    {
        string merged = await BuildMergedTextForSelectedAsync(separator).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(merged))
        {
            return false;
        }

        bool copied = await CopyGeneratedTextAsync(merged).ConfigureAwait(true);
        if (copied)
        {
            ExitMultiSelectMode();
        }

        return copied;
    }

    public async Task<int> BatchPinSelectedAsync()
    {
        if (_service is null || _selectedIdsInOrder.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<ClipboardItemViewModel> selected = GetSelectedItemsInOrder();
        bool shouldPin = selected.Any(x => !x.IsPinned);
        string[] ids = _selectedIdsInOrder.ToArray();
        int changed = await _service.SetPinnedBatchAsync(ids, shouldPin).ConfigureAwait(true);
        RefreshItemMultiSelectAndQueueVisuals();
        return changed;
    }

    public async Task<int> BatchDeleteSelectedAsync()
    {
        if (_service is null || _selectedIdsInOrder.Count == 0)
        {
            return 0;
        }

        string[] ids = _selectedIdsInOrder.ToArray();
        ExitMultiSelectMode();
        return await _service.DeleteItemsBatchAsync(ids).ConfigureAwait(true);
    }

    public async Task<int> EnqueueSelectedToPasteQueueAsync()
    {
        if (_service is null || _selectedIdsInOrder.Count == 0)
        {
            return 0;
        }

        string[] ids = _selectedIdsInOrder.ToArray();
        ExitMultiSelectMode();
        return await _service.EnqueuePasteQueueAsync(ids, enableLiveCapture: false).ConfigureAwait(true);
    }

    public Task<int> EnqueueSingleItemToPasteQueueAsync(ClipboardItemViewModel item)
    {
        return _service is not null
            ? _service.EnqueuePasteQueueAsync([item.Id], enableLiveCapture: false)
            : Task.FromResult(0);
    }

    public void ToggleLivePasteQueue()
    {
        if (_service is null)
        {
            return;
        }

        if (_service.IsPasteQueueActive)
        {
            _service.StopPasteQueue();
        }
        else
        {
            _service.StartLivePasteQueue();
        }
    }

    public void StopPasteQueue()
    {
        _service?.StopPasteQueue();
    }

    public Task<(bool Copied, bool Pasted)> PasteNextQueueItemAsync(bool forcePlainText = false)
    {
        return _service is not null
            ? _service.PasteNextFromQueueAsync(forcePlainText)
            : Task.FromResult((false, false));
    }

    public Task<bool> SkipQueueHeadAsync()
    {
        return _service is not null
            ? _service.SkipPasteQueueHeadAsync()
            : Task.FromResult(false);
    }

    internal async Task<int> ImportDroppedFilesAsync(IReadOnlyList<DroppedFilePath> files)
    {
        return _service is not null
            ? await _service.ImportDroppedFilesAsync(files).ConfigureAwait(true)
            : 0;
    }

    public async Task<int> ImportDroppedPathsAsync(IEnumerable<string> paths)
    {
        return _service is not null
            ? await _service.ImportDroppedFilesAsync(paths).ConfigureAwait(true)
            : 0;
    }

    public async Task<bool> ImportDroppedTextAsync(string text)
    {
        return _service is not null &&
               await _service.ImportDroppedTextAsync(text).ConfigureAwait(true);
    }

    public string? ResolveOriginalImagePath(ClipboardHistoryItem item)
    {
        return _service?.GetAbsolutePath(item.ImageRelativePath);
    }

    public string? ResolveThumbnailPath(ClipboardHistoryItem item)
    {
        return _service?.GetAbsolutePath(item.ThumbnailRelativePath)
            ?? _service?.GetAbsolutePath(item.ImageRelativePath);
    }

    public Task<string?> PrepareDragExportImageAsync(ClipboardHistoryItem item)
    {
        return _service is not null
            ? _service.PrepareDragExportImageAsync(item)
            : Task.FromResult<string?>(null);
    }

    public void SetViewAttached(bool isAttached)
    {
        if (_isDisposed || _isViewAttached == isAttached)
        {
            return;
        }

        _isViewAttached = isAttached;
        if (!isAttached)
        {
            _isDirty = true;
            ReleaseCachedThumbnails();
            VisibleItems.Clear();
            _itemPool.Clear();
            OnPropertyChanged(nameof(ListVisibility));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            return;
        }

        if ((_isDirty || (VisibleItems.Count == 0 && TotalItemCount > 0)) && _isInitialized)
        {
            RebuildVisibleItems();
        }
    }

    public void SetViewForegroundTracking(object viewToken, bool isActive)
    {
        _service?.SetViewForegroundTracking(viewToken, isActive);
    }

    public void CaptureExternalForegroundWindow()
    {
        _service?.CaptureExternalForegroundWindow();
    }

    public Task<string> GetFullTextAsync(
        ClipboardItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_service is not null)
        {
            return _service.GetFullTextAsync(item.Model, cancellationToken);
        }

        return Task.FromResult(item.Model.PlainText ?? item.Model.SummaryText ?? string.Empty);
    }

    public Task<(string Text, bool WasTruncated)> GetPeekTextAsync(
        ClipboardItemViewModel item,
        int maxChars = MaxPeekTextChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_service is not null)
        {
            return _service.GetTextPreviewAsync(item.Model, maxChars, cancellationToken);
        }

        string inline = item.Model.PlainText ?? item.Model.SummaryText ?? string.Empty;
        if (inline.Length <= maxChars)
        {
            return Task.FromResult((inline, false));
        }

        return Task.FromResult((ClipboardStore.TruncateSurrogateSafe(inline, maxChars), true));
    }

    public void ReleaseCachedThumbnails()
    {
        foreach (ClipboardItemViewModel vm in _itemPool.Values)
        {
            vm.ReleaseVisualAssets();
        }
    }

    public void RefreshRelativeTimestamps()
    {
        if (!_isViewAttached || _isDisposed)
        {
            return;
        }

        var fileItems = new List<ClipboardItemViewModel>();
        foreach (ClipboardItemViewModel vm in VisibleItems)
        {
            vm.RefreshTimeDisplay();
            if (vm.Kind == ClipboardItemKind.Files)
            {
                fileItems.Add(vm);
            }
        }

        if (fileItems.Count > 0)
        {
            _ = RefreshFileExistenceAsync(fileItems);
        }
    }

    private async Task RefreshFileExistenceAsync(IReadOnlyList<ClipboardItemViewModel> fileItems)
    {
        foreach (ClipboardItemViewModel vm in fileItems)
        {
            if (_isDisposed || !_isViewAttached)
            {
                return;
            }

            await vm.RefreshMissingFilesStateAsync(DispatchToUi).ConfigureAwait(false);
        }
    }

    public (string PrimaryText, string SecondaryText) BuildCapsuleSummary(bool hideSensitiveContent)
    {
        if (IsPaused)
        {
            return (
                _localizationService.T("Clipboard.Capsule.Paused"),
                _localizationService.Format("Clipboard.Capsule.Count", TotalItemCount));
        }

        IReadOnlyList<ClipboardHistoryItem> items = _service?.Items ?? [];
        if (items.Count == 0)
        {
            return (_localizationService.T("Clipboard.Capsule.Empty"), string.Empty);
        }

        if (hideSensitiveContent)
        {
            return (
                _localizationService.Format("Clipboard.Capsule.Count", items.Count),
                string.Empty);
        }

        ClipboardHistoryItem latest = items[0];
        string latestSummary;
        if (latest.IsSensitive &&
            !string.Equals(_settingsService.Settings.Clipboard.ClipboardSensitiveProtectionMode, ClipboardSettingsSlice.SecretProtectionNone, StringComparison.OrdinalIgnoreCase) && !string.Equals(_settingsService.Settings.Clipboard.ClipboardSensitiveProtectionMode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            latestSummary = ClipboardClassifier.MaskSecretForDisplay(latest.PlainText ?? latest.SummaryText);
        }
        else if (latest.Kind == ClipboardItemKind.Image)
        {
            latestSummary = latest.ImageWidth > 0 && latest.ImageHeight > 0
                ? $"[{_localizationService.T("Clipboard.Item.ImageTitle")} {latest.ImageWidth}×{latest.ImageHeight}]"
                : $"[{_localizationService.T("Clipboard.Item.ImageTitle")}]";
        }
        else if (latest.Kind == ClipboardItemKind.Files)
        {
            string leaf = latest.FilePaths.Count > 0 ? ClipboardItemViewModel.GetLeafName(latest.FilePaths[0]) : string.Empty;
            latestSummary = latest.FilePaths.Count > 1 ? $"{leaf} (+{latest.FilePaths.Count - 1})" : leaf;
        }
        else
        {
            latestSummary = (latest.SummaryText ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        if (latestSummary.Length > 36)
        {
            latestSummary = string.Concat(latestSummary.AsSpan(0, 36), "…");
        }

        return (
            latestSummary,
            _localizationService.Format("Clipboard.Capsule.Count", items.Count));
    }

    public void ApplyAppearanceFromSettings()
    {
        OnPropertyChanged(nameof(TabStyle));
        OnPropertyChanged(nameof(TextSize));
        OnPropertyChanged(nameof(SecondaryTextSize));
        OnPropertyChanged(nameof(TitleTextSize));
        OnPropertyChanged(nameof(SegmentTextSize));
        OnPropertyChanged(nameof(IsCompactViewMode));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(ViewModeToggleGlyph));
        OnPropertyChanged(nameof(ViewModeToggleTooltip));
        NotifyFilterTabDisplayModeChanged();
        RebuildVisibleItems();
    }

    public void RebuildVisibleItems()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_isViewAttached)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(TotalItemCount));
            _capsuleRevision++;
            OnPropertyChanged(nameof(CapsuleRevision));
            SummaryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _isDirty = false;
        IReadOnlyList<ClipboardHistoryItem> source = _service?.Items ?? [];
        string? currentClipHash = _service?.CurrentClipboardContentHash;
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        bool isCompactView = IsCompactViewMode;
        string density = slice.ClipboardCardDensity;
        bool isCompactDensity = string.Equals(density, ClipboardSettingsSlice.DensityCompact, StringComparison.OrdinalIgnoreCase);
        bool isComfortable =
            string.Equals(density, ClipboardSettingsSlice.DensityRelaxed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(density, "Comfortable", StringComparison.OrdinalIgnoreCase);

        int previewLines = isCompactView ? 1 : (isCompactDensity ? 1 : isComfortable ? 4 : 2);
        Thickness cardPadding = isCompactView
            ? new Thickness(9, 5, 9, 5)
            : (isCompactDensity ? new Thickness(9, 6, 9, 6) : new Thickness(10, 7, 10, 8));
        double cardMinHeight = isCompactView
            ? 28
            : (isCompactDensity ? 38 : isComfortable ? 60 : 48);
        double bannerMaxHeight = isCompactDensity ? 64 : isComfortable ? 132 : 98;
        double textSize = TextSize;
        double secondarySize = SecondaryTextSize;
        string secretMode = slice.ClipboardSensitiveProtectionMode;
        bool showSourceApp = slice.ClipboardShowSourceApp;
        string query = _searchText.Trim();

        var filtered = new List<ClipboardItemViewModel>(source.Count);
        List<ClipboardItemViewModel>? fileItemsToVerify = null;
        var aliveIds = new HashSet<string>(StringComparer.Ordinal);
        bool assignedActiveClip = false;

        foreach (ClipboardHistoryItem item in source)
        {
            aliveIds.Add(item.Id);
            if (!MatchesFilter(item, _activeFilter) || !MatchesQuery(item, query))
            {
                continue;
            }

            int quickSlot = filtered.Count < 9 ? filtered.Count + 1 : 0;
            bool isCurrentClip = !assignedActiveClip &&
                                 !string.IsNullOrEmpty(currentClipHash) &&
                                 string.Equals(item.ContentHash, currentClipHash, StringComparison.Ordinal);
            if (isCurrentClip)
            {
                assignedActiveClip = true;
            }

            if (_itemPool.TryGetValue(item.Id, out ClipboardItemViewModel? vm))
            {
                vm.UpdateFrom(
                    item,
                    secretMode,
                    showSourceApp,
                    textSize,
                    secondarySize,
                    previewLines,
                    cardPadding,
                    cardMinHeight,
                    bannerMaxHeight,
                    isCurrentClip,
                    quickSlot,
                    isCompactView);
            }
            else
            {
                vm = new ClipboardItemViewModel(
                    item,
                    _localizationService,
                    secretMode,
                    showSourceApp,
                    textSize,
                    secondarySize,
                    previewLines,
                    cardPadding,
                    cardMinHeight,
                    ResolveThumbnailPath,
                    _iconLoader,
                    _thumbnailLoader,
                    bannerMaxHeight,
                    isCurrentClip,
                    quickSlot,
                    isCompactView);
                _itemPool[item.Id] = vm;
            }

            if (vm.Kind == ClipboardItemKind.Files)
            {
                (fileItemsToVerify ??= []).Add(vm);
            }

            filtered.Add(vm);
        }

        if (_itemPool.Count > aliveIds.Count)
        {
            string[] staleIds = _itemPool.Keys.Where(id => !aliveIds.Contains(id)).ToArray();
            foreach (string staleId in staleIds)
            {
                if (_itemPool.Remove(staleId, out ClipboardItemViewModel? removed))
                {
                    removed.ReleaseVisualAssets();
                }
            }
        }

        if (_selectedIdsInOrder.Count > 0)
        {
            int removedSel = _selectedIdsInOrder.RemoveAll(id => !aliveIds.Contains(id));
            if (removedSel > 0)
            {
                NotifyMultiSelectChanged();
            }
        }

        RefreshItemMultiSelectAndQueueVisuals();
        SyncObservableCollection(VisibleItems, filtered);
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(EmptyStateGlyph));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
        OnPropertyChanged(nameof(TotalItemCount));
        _capsuleRevision++;
        OnPropertyChanged(nameof(CapsuleRevision));
        SummaryChanged?.Invoke(this, EventArgs.Empty);

        if (fileItemsToVerify is { Count: > 0 })
        {
            _ = RefreshFileExistenceAsync(fileItemsToVerify);
        }
    }

    private static bool MatchesFilter(ClipboardHistoryItem item, ClipboardFilterKind filter)
    {
        return filter switch
        {
            ClipboardFilterKind.Text => item.Kind == ClipboardItemKind.Text,
            ClipboardFilterKind.Images => item.Kind == ClipboardItemKind.Image || ClipboardClassifier.IsSinglePreviewableImageFile(item),
            ClipboardFilterKind.Links => item.Kind == ClipboardItemKind.Text && item.TextSubKind == ClipboardTextSubKind.Url,
            ClipboardFilterKind.Files => item.Kind == ClipboardItemKind.Files,
            ClipboardFilterKind.Pinned => item.IsPinned,
            _ => true
        };
    }

    private static bool MatchesQuery(ClipboardHistoryItem item, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.Note) &&
            item.Note.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.UrlTitle) &&
            item.UrlTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.CodeLanguage) &&
            item.CodeLanguage.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.SummaryText) &&
            item.SummaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.PlainText) &&
            item.PlainText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.OcrText) &&
            item.OcrText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.SourceAppName) &&
            (item.SourceAppName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             ClipboardClassifier.NormalizeFriendlyAppDisplayName(item.SourceAppName).Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (item.FilePaths.Count > 0 &&
            item.FilePaths.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static void SyncObservableCollection(
        ObservableCollection<ClipboardItemViewModel> target,
        IReadOnlyList<ClipboardItemViewModel> desired)
    {
        if (target.Count == 0 && desired.Count == 0)
        {
            return;
        }

        if (desired.Count == 0)
        {
            target.Clear();
            return;
        }

        // Fast common paths (prepend 1 item, bump 1 item to top, remove 1 item, or identical list)
        if (target.Count == desired.Count)
        {
            bool identical = true;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(target[i], desired[i]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return;
            }
        }

        if (Math.Abs(target.Count - desired.Count) > 8 || desired.Count > 120)
        {
            target.Clear();
            foreach (ClipboardItemViewModel item in desired)
            {
                target.Add(item);
            }

            return;
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            ClipboardItemViewModel item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                continue;
            }

            int existingIndex = target.IndexOf(item);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }

    private void Service_ItemsChanged(object? sender, EventArgs e)
    {
        DispatchToUi(RebuildVisibleItems);
    }

    private void Service_MonitoringStateChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PausedBannerVisibility));
            OnPropertyChanged(nameof(StatusBannersVisibility));
            OnPropertyChanged(nameof(PauseToggleGlyph));
            OnPropertyChanged(nameof(PauseToggleTooltip));
            _capsuleRevision++;
            OnPropertyChanged(nameof(CapsuleRevision));
            SummaryChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Service_PasteQueueChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            RefreshItemMultiSelectAndQueueVisuals();
            NotifyPasteQueueChanged();
        });
    }

    private void RefreshItemMultiSelectAndQueueVisuals()
    {
        var selectionOrderMap = new Dictionary<string, int>(_selectedIdsInOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < _selectedIdsInOrder.Count; i++)
        {
            selectionOrderMap[_selectedIdsInOrder[i]] = i + 1;
        }

        IReadOnlyList<ClipboardHistoryItem> queue = _service?.GetPasteQueueSnapshot() ?? [];
        var queueOrderMap = new Dictionary<string, int>(queue.Count, StringComparer.Ordinal);
        for (int i = 0; i < queue.Count; i++)
        {
            queueOrderMap[queue[i].Id] = i + 1;
        }

        foreach (ClipboardItemViewModel vm in _itemPool.Values)
        {
            bool isSelected = selectionOrderMap.TryGetValue(vm.Id, out int selOrder);
            vm.SetMultiSelectState(_isMultiSelectMode, isSelected, isSelected ? selOrder : 0);
            vm.SetPasteQueueOrder(queueOrderMap.TryGetValue(vm.Id, out int qOrder) ? qOrder : 0);
        }
    }

    private void NotifyMultiSelectChanged()
    {
        OnPropertyChanged(nameof(IsMultiSelectMode));
        OnPropertyChanged(nameof(MultiSelectBarVisibility));
        OnPropertyChanged(nameof(MultiSelectedCount));
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(MultiSelectStatusText));
    }

    private void NotifyPasteQueueChanged()
    {
        OnPropertyChanged(nameof(IsPasteQueueActive));
        OnPropertyChanged(nameof(IsPasteQueueLiveCapture));
        OnPropertyChanged(nameof(PasteQueueCount));
        OnPropertyChanged(nameof(PasteQueueBannerVisibility));
        OnPropertyChanged(nameof(StatusBannersVisibility));
        OnPropertyChanged(nameof(CanPasteOrSkipQueue));
        OnPropertyChanged(nameof(PasteQueueToggleTooltip));
        OnPropertyChanged(nameof(PasteQueueBannerTitle));
        OnPropertyChanged(nameof(PasteQueueHeadPreview));
    }

    private void SettingsService_SettingsChanged()
    {
        DispatchToUi(ApplyAppearanceFromSettings);
    }

    private void LocalizationService_LanguageChanged()
    {
        DispatchToUi(() =>
        {
            NotifyLocalizedStringsChanged();
            RebuildVisibleItems();
        });
    }

    private void NotifyLocalizedStringsChanged()
    {
        OnPropertyChanged(nameof(FilterAllText));
        OnPropertyChanged(nameof(FilterTextText));
        OnPropertyChanged(nameof(FilterImagesText));
        OnPropertyChanged(nameof(FilterLinksText));
        OnPropertyChanged(nameof(FilterFilesText));
        OnPropertyChanged(nameof(FilterPinnedText));
        OnPropertyChanged(nameof(FilterAllTooltip));
        OnPropertyChanged(nameof(FilterTextTooltip));
        OnPropertyChanged(nameof(FilterImagesTooltip));
        OnPropertyChanged(nameof(FilterLinksTooltip));
        OnPropertyChanged(nameof(FilterFilesTooltip));
        OnPropertyChanged(nameof(FilterPinnedTooltip));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchCancelText));
        OnPropertyChanged(nameof(PauseToggleTooltip));
        OnPropertyChanged(nameof(ViewModeToggleTooltip));
        OnPropertyChanged(nameof(PausedBannerText));
        OnPropertyChanged(nameof(ResumeActionText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
        OnPropertyChanged(nameof(MultiSelectStatusText));
        OnPropertyChanged(nameof(MergeCopyLabel));
        OnPropertyChanged(nameof(MergeSaveQuickCaptureTooltip));
        OnPropertyChanged(nameof(EnqueueSelectedTooltip));
        OnPropertyChanged(nameof(BatchPinTooltip));
        OnPropertyChanged(nameof(BatchDeleteTooltip));
        OnPropertyChanged(nameof(ExitMultiSelectTooltip));
        NotifyPasteQueueChanged();
    }

    private void DispatchToUi(Action action)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_uiDispatch is not null)
        {
            _uiDispatch(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_service is not null)
        {
            _service.ItemsChanged -= Service_ItemsChanged;
            _service.MonitoringStateChanged -= Service_MonitoringStateChanged;
            _service.PasteQueueChanged -= Service_PasteQueueChanged;
        }

        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        _localizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        ReleaseCachedThumbnails();
        _itemPool.Clear();
        VisibleItems.Clear();
    }
}
