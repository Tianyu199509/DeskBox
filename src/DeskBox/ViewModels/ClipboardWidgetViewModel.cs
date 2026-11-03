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
    Files = 3,
    Pinned = 4
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
        Func<string, int, Task<BitmapImage?>>? thumbnailLoader = null)
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

    public double SecondaryTextSize => _secondaryTextSize;

    public int PreviewMaxLines => _previewMaxLines;

    public Thickness CardPadding => _cardPadding;

    public double CardMinHeight => _cardMinHeight;

    public bool ShowImageCardPreview =>
        _model.Kind == ClipboardItemKind.Image ||
        (_model.Kind == ClipboardItemKind.Files &&
         !_isMissingAllFiles &&
         _model.FilePaths.Count == 1 &&
         IsPreviewableImageExtension(_model.FilePaths[0]));

    public Visibility ImagePreviewVisibility =>
        ShowImageCardPreview ? Visibility.Visible : Visibility.Collapsed;

    public BitmapImage? Thumbnail => _thumbnail;

    public Visibility ThumbnailVisibility =>
        _thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImagePlaceholderVisibility =>
        ShowImageCardPreview && _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

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
        _model.Kind == ClipboardItemKind.Text &&
        _model.TextSubKind == ClipboardTextSubKind.Color &&
        !string.IsNullOrWhiteSpace(_model.ColorHex)
            ? Visibility.Visible
            : Visibility.Collapsed;

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

            if (_model.Kind == ClipboardItemKind.Text && _model.TextSubKind == ClipboardTextSubKind.Url)
            {
                return "\uE71B"; // Link
            }

            return string.Empty;
        }
    }

    public Visibility KindPrefixVisibility =>
        string.IsNullOrEmpty(KindPrefixGlyph) ? Visibility.Collapsed : Visibility.Visible;

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

            return _model.Kind switch
            {
                ClipboardItemKind.Image => FormatImagePrimaryText(),
                ClipboardItemKind.Files => FormatFilesPrimaryText(),
                _ => !string.IsNullOrWhiteSpace(_model.SummaryText)
                    ? _model.SummaryText
                    : (_model.PlainText ?? string.Empty).Trim()
            };
        }
    }

    public BitmapImage? SourceAppIcon => _showSourceApp ? _sourceAppIcon : null;

    public Visibility SourceAppIconVisibility =>
        _showSourceApp && _sourceAppIcon is not null ? Visibility.Visible : Visibility.Collapsed;

    public string MetaText
    {
        get
        {
            var parts = new List<string>(2);
            if (_showSourceApp && !string.IsNullOrWhiteSpace(_model.SourceAppName))
            {
                parts.Add(_model.SourceAppName.Trim());
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
        string.IsNullOrWhiteSpace(MetaText) && SourceAppIconVisibility == Visibility.Collapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string RelativeTimeText => FormatRelativeTime(_model.LastUsedAtUtc, _localization);

    public Visibility PinnedBadgeVisibility =>
        _model.IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PeekSelectionVisibility =>
        _isPeekSelected ? Visibility.Visible : Visibility.Collapsed;

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
        double cardMinHeight)
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
                OnPropertyChanged(nameof(IsMissingAllFiles));
                OnPropertyChanged(nameof(ContentOpacity));
                OnPropertyChanged(nameof(TitleDecorations));
                OnPropertyChanged(nameof(MetaText));
                OnPropertyChanged(nameof(MetaVisibility));
                OnPropertyChanged(nameof(ShowImageCardPreview));
                OnPropertyChanged(nameof(ImagePreviewVisibility));
                OnPropertyChanged(nameof(ImagePlaceholderVisibility));
                OnPropertyChanged(nameof(KindPrefixGlyph));
                OnPropertyChanged(nameof(KindPrefixVisibility));
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
                    BitmapImage? loaded = await _thumbnailLoader(previewPath, 160).ConfigureAwait(true);
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

        if (_showSourceApp &&
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

    private string FormatFilesPrimaryText()
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
            return _localization.T("Clipboard.Meta.SensitiveMasked");
        }

        switch (_model.Kind)
        {
            case ClipboardItemKind.Image:
                return _model.ByteSize > 0 ? FormatByteSize(_model.ByteSize) : "PNG";

            case ClipboardItemKind.Files:
                if (_model.FilePaths.Count > 1)
                {
                    return _localization.Format("Clipboard.Meta.FileCount", _model.FilePaths.Count);
                }

                if (_model.FilePaths.Count == 1)
                {
                    string? parent = Path.GetDirectoryName(_model.FilePaths[0]);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        return GetLeafName(parent);
                    }
                }

                return string.Empty;

            default:
                if (_model.TextSubKind == ClipboardTextSubKind.Color &&
                    TryParseColorHex(_model.ColorHex, out Color c))
                {
                    return c.A == 255
                        ? $"RGB({c.R}, {c.G}, {c.B})"
                        : $"RGBA({c.R}, {c.G}, {c.B}, {Math.Round(c.A / 255.0, 2):0.##})";
                }

                if (_model.TextSubKind == ClipboardTextSubKind.Url &&
                    Uri.TryCreate(_model.PlainText?.Trim(), UriKind.Absolute, out Uri? uri) &&
                    !string.IsNullOrWhiteSpace(uri.Host))
                {
                    return uri.Host;
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

    public Thickness RootPadding => new(10, 6, 10, 8);

    public string ClickBehavior => _settingsService.Settings.Clipboard.ClipboardClickBehavior;

    public bool DefaultPastePlainText => _settingsService.Settings.Clipboard.ClipboardDefaultPastePlainText;

    public string FilterAllText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.AllShort" : "Clipboard.Filter.All");
    public string FilterTextText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.TextShort" : "Clipboard.Filter.Text");
    public string FilterImagesText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.ImagesShort" : "Clipboard.Filter.Images");
    public string FilterFilesText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.FilesShort" : "Clipboard.Filter.Files");
    public string FilterPinnedText => _localizationService.T(_useShortFilterLabels ? "Clipboard.Filter.PinnedShort" : "Clipboard.Filter.Pinned");

    public string SearchPlaceholderText => _localizationService.T("Clipboard.Search.Placeholder");
    public string SearchCancelText => _localizationService.T("Common.Cancel");
    public string PauseToggleGlyph => IsPaused ? "\uE768" : "\uE769";
    public string PauseToggleTooltip => IsPaused
        ? _localizationService.T("Clipboard.Action.ResumeCapture")
        : _localizationService.T("Clipboard.Action.PauseCapture");
    public string PausedBannerText => _localizationService.T("Clipboard.Banner.Paused");
    public string ResumeActionText => _localizationService.T("Clipboard.Action.Resume");

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
        OnPropertyChanged(nameof(TextSize));
        OnPropertyChanged(nameof(SecondaryTextSize));
        OnPropertyChanged(nameof(TitleTextSize));
        OnPropertyChanged(nameof(SegmentTextSize));
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
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        string density = slice.ClipboardCardDensity;
        bool isCompact = string.Equals(density, ClipboardSettingsSlice.DensityCompact, StringComparison.OrdinalIgnoreCase);
        bool isComfortable =
            string.Equals(density, ClipboardSettingsSlice.DensityRelaxed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(density, "Comfortable", StringComparison.OrdinalIgnoreCase);

        int previewLines = isCompact ? 1 : isComfortable ? 4 : 2;
        Thickness cardPadding = isCompact ? new Thickness(8, 5, 8, 5) : new Thickness(10, 8, 10, 8);
        double cardMinHeight = isCompact ? 38 : isComfortable ? 62 : 50;
        double textSize = TextSize;
        double secondarySize = SecondaryTextSize;
        string secretMode = slice.ClipboardSensitiveProtectionMode;
        bool showSourceApp = slice.ClipboardShowSourceApp;
        string query = _searchText.Trim();

        var filtered = new List<ClipboardItemViewModel>(source.Count);
        List<ClipboardItemViewModel>? fileItemsToVerify = null;
        var aliveIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (ClipboardHistoryItem item in source)
        {
            aliveIds.Add(item.Id);
            if (!MatchesFilter(item, _activeFilter) || !MatchesQuery(item, query))
            {
                continue;
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
                    cardMinHeight);
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
                    _thumbnailLoader);
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
            ClipboardFilterKind.Images => item.Kind == ClipboardItemKind.Image,
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

        if (!string.IsNullOrEmpty(item.SourceAppName) &&
            item.SourceAppName.Contains(query, StringComparison.OrdinalIgnoreCase))
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
            OnPropertyChanged(nameof(PauseToggleGlyph));
            OnPropertyChanged(nameof(PauseToggleTooltip));
            _capsuleRevision++;
            OnPropertyChanged(nameof(CapsuleRevision));
            SummaryChanged?.Invoke(this, EventArgs.Empty);
        });
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
        OnPropertyChanged(nameof(FilterFilesText));
        OnPropertyChanged(nameof(FilterPinnedText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchCancelText));
        OnPropertyChanged(nameof(PauseToggleTooltip));
        OnPropertyChanged(nameof(PausedBannerText));
        OnPropertyChanged(nameof(ResumeActionText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
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
        }

        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        _localizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        ReleaseCachedThumbnails();
        _itemPool.Clear();
        VisibleItems.Clear();
    }
}
