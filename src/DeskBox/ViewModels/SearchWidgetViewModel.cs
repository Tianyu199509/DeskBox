using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Responsive layout tiers for the expanded desktop search entry widget.
/// </summary>
public enum SearchWidgetLayoutTier
{
    MicroTile,
    CompactBar,
    WideStrip,
    MediumCard,
    LargeHub
}

/// <summary>
/// Latched or prefix-detected quick-action mode in the desktop search widget.
/// </summary>
public enum SearchQuickMode
{
    None,
    Todo,
    Note,
    Command
}

public sealed record SearchPinnedQueryItem(
    string Query,
    string DisplayText);

public sealed record SearchRecentQueryItem(
    string Query,
    string DisplayText,
    bool IsPinned);

public sealed record SearchRecentOpenedItem(
    SearchRecommendationItem Source,
    string Glyph,
    string DisplayTitle,
    string DisplaySubtitle);

/// <summary>
/// Stateful ViewModel for the native WinUI 3 desktop Search entry widget.
/// Coordinates responsive tiers, quick-mode latching, inline math evaluation,
/// pinned/recent history management, desktop privacy masking, and search popup handoff.
/// </summary>
public sealed partial class SearchWidgetViewModel : ObservableObject, IDisposable
{
    private const int MaxPinnedItems = 10;
    private const int MaxRecentQueries = 8;
    private const int MaxRecentOpenedItems = 6;
    private const int MaxWideStripChips = 8;

    private readonly WidgetConfig _config;
    private readonly LocalizationService _localization;
    private readonly SettingsService _settings;
    private readonly SearchHistoryService? _historyService;
    private readonly Func<bool>? _isEverythingConnected;
    private readonly Action? _openSearchPopup;
    private readonly Action<string>? _openSearchPopupWithQuery;
    private readonly Func<string, Task>? _executeSearchAction;
    private readonly Func<SearchRecommendationItem, Task>? _openRecommendation;
    private readonly Action<string>? _openSettings;
    private readonly Action<string>? _copyText;
    private readonly Action<bool>? _setPrivacyMaskPreference;
    private readonly Func<bool>? _isTodoEnabled;
    private readonly Func<bool>? _isQuickCaptureEnabled;
    private readonly Action<Action>? _uiDispatch;

    private string _queryText = string.Empty;
    private SearchQuickMode _activeQuickMode = SearchQuickMode.None;
    private bool _hasCalcResult;
    private string _calcNormalizedExpression = string.Empty;
    private string _calcResultText = string.Empty;
    private string _calcRawValue = string.Empty;
    private bool _isPointerHovering;
    private int _capsuleRevision;
    private bool _disposed;

    public SearchWidgetViewModel(
        WidgetConfig config,
        LocalizationService localization,
        SettingsService settings,
        SearchHistoryService? historyService = null,
        Func<bool>? isEverythingConnected = null,
        Action? openSearchPopup = null,
        Action<string>? openSearchPopupWithQuery = null,
        Func<string, Task>? executeSearchAction = null,
        Func<SearchRecommendationItem, Task>? openRecommendation = null,
        Action<string>? openSettings = null,
        Action<string>? copyText = null,
        Action<bool>? setPrivacyMaskPreference = null,
        Func<bool>? isTodoEnabled = null,
        Func<bool>? isQuickCaptureEnabled = null,
        Action<Action>? uiDispatch = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _historyService = historyService;
        _isEverythingConnected = isEverythingConnected;
        _openSearchPopup = openSearchPopup;
        _openSearchPopupWithQuery = openSearchPopupWithQuery;
        _executeSearchAction = executeSearchAction;
        _openRecommendation = openRecommendation;
        _openSettings = openSettings;
        _copyText = copyText;
        _setPrivacyMaskPreference = setPrivacyMaskPreference;
        _isTodoEnabled = isTodoEnabled ?? (() => FeatureWidgetSettings.IsEnabled(_settings.Settings, WidgetKind.Todo));
        _isQuickCaptureEnabled = isQuickCaptureEnabled ?? (() => FeatureWidgetSettings.IsEnabled(_settings.Settings, WidgetKind.QuickCapture));
        _uiDispatch = uiDispatch;

        if (_historyService is not null)
        {
            _historyService.HistoryChanged += OnExternalStateChanged;
        }

        _settings.SettingsChanged += OnExternalStateChanged;
        RebuildSnapshots();
    }

    public WidgetConfig Config => _config;

    public IReadOnlyList<SearchPinnedQueryItem> PinnedQueryItems { get; private set; } = [];

    public IReadOnlyList<SearchRecentQueryItem> RecentQueryItems { get; private set; } = [];

    public IReadOnlyList<SearchPinnedQueryItem> WideStripItems { get; private set; } = [];

    public IReadOnlyList<SearchRecentOpenedItem> RecentOpenedItems { get; private set; } = [];

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event Action? StateRefreshed;

    public static SearchWidgetLayoutTier ResolveLayoutTier(double width, double height)
    {
        if (width < 190 || (width < 230 && height >= 84))
        {
            return SearchWidgetLayoutTier.MicroTile;
        }

        if (height < 84)
        {
            return SearchWidgetLayoutTier.CompactBar;
        }

        if (height < 150)
        {
            return SearchWidgetLayoutTier.WideStrip;
        }

        if (width >= 450)
        {
            return SearchWidgetLayoutTier.LargeHub;
        }

        return SearchWidgetLayoutTier.MediumCard;
    }

    public string QueryText
    {
        get => _queryText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_queryText, next, StringComparison.Ordinal))
            {
                return;
            }

            _queryText = next;
            if (_activeQuickMode == SearchQuickMode.None)
            {
                TryAutoLatchPrefix();
            }

            EvaluateCalculator();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueryText));
            OnPropertyChanged(nameof(HasActiveQuickMode));
            OnPropertyChanged(nameof(EffectivePlaceholderText));
        }
    }

    public bool HasQueryText => !string.IsNullOrEmpty(_queryText);

    public SearchQuickMode ActiveQuickMode
    {
        get => _activeQuickMode;
        private set
        {
            if (_activeQuickMode == value)
            {
                return;
            }

            _activeQuickMode = value;
            EvaluateCalculator();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveQuickMode));
            OnPropertyChanged(nameof(EffectivePlaceholderText));
            OnPropertyChanged(nameof(QuickModeHintText));
        }
    }

    public bool HasActiveQuickMode => _activeQuickMode != SearchQuickMode.None;

    public bool HasCalcResult
    {
        get => _hasCalcResult;
        private set => SetProperty(ref _hasCalcResult, value);
    }

    public string CalcNormalizedExpression
    {
        get => _calcNormalizedExpression;
        private set => SetProperty(ref _calcNormalizedExpression, value);
    }

    public string CalcResultText
    {
        get => _calcResultText;
        private set => SetProperty(ref _calcResultText, value);
    }

    public bool IsPointerHovering => _isPointerHovering;

    public bool ShowHistory => _settings.Settings.Search.SearchDesktopShowHistory;

    public bool ShowRecentOpened => _settings.Settings.Search.SearchDesktopShowRecentOpened;

    public bool IsPrivacyMaskEnabled => _settings.Settings.Search.SearchDesktopPrivacyMask;

    public bool EffectivePrivacyMaskActive => IsPrivacyMaskEnabled && !_isPointerHovering;

    public bool IsInstantHandoff => string.Equals(
        SearchSettingsSlice.NormalizeDesktopHandoffMode(_settings.Settings.Search.SearchDesktopHandoffMode),
        SearchSettingsSlice.DesktopHandoffInstant,
        StringComparison.Ordinal);

    public bool IsEverythingConnected =>
        _settings.Settings.Search.SearchEverythingEnabled &&
        (_isEverythingConnected?.Invoke() ?? false);

    public bool HasHotkey => _settings.Settings.Search.SearchHotkeyEnabled;

    public string HotkeyDisplayText
    {
        get
        {
            SearchSettingsSlice search = _settings.Settings.Search;
            if (!search.SearchHotkeyEnabled)
            {
                return string.Empty;
            }

            GlobalHotkeyActivation activation = GlobalHotkeyService.NormalizeActivation(
                search.SearchHotkeyActivationKind,
                search.SearchHotkeyModifiers,
                search.SearchHotkeyKey);
            return GlobalHotkeyService.FormatActivation(activation, _localization);
        }
    }

    public string EffectivePlaceholderText => ActiveQuickMode switch
    {
        SearchQuickMode.Todo => _localization.T("Search.Widget.TodoPlaceholder"),
        SearchQuickMode.Note => _localization.T("Search.Widget.NotePlaceholder"),
        SearchQuickMode.Command => _localization.T("Search.Widget.CommandPlaceholder"),
        _ => _localization.T("Search.Widget.Placeholder")
    };

    public string QuickModeHintText => ActiveQuickMode switch
    {
        SearchQuickMode.Todo => _localization.T("Search.Widget.TodoHint"),
        SearchQuickMode.Note => _localization.T("Search.Widget.NoteHint"),
        SearchQuickMode.Command => _localization.T("Search.Widget.CommandHint"),
        _ => string.Empty
    };

    public int CapsuleRevision
    {
        get => _capsuleRevision;
        private set => SetProperty(ref _capsuleRevision, value);
    }

    public bool ShouldTriggerInstantHandoff()
    {
        if (!IsInstantHandoff ||
            ActiveQuickMode != SearchQuickMode.None ||
            HasCalcResult ||
            string.IsNullOrWhiteSpace(_queryText))
        {
            return false;
        }

        string trimmed = _queryText.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '+' or ';' or '>' or '=')
        {
            return false;
        }

        return true;
    }

    public bool ToggleQuickMode(SearchQuickMode mode)
    {
        if (mode == SearchQuickMode.None)
        {
            ActiveQuickMode = SearchQuickMode.None;
            return true;
        }

        ActiveQuickMode = _activeQuickMode == mode ? SearchQuickMode.None : mode;
        return true;
    }

    public void ClearQuickMode()
    {
        ActiveQuickMode = SearchQuickMode.None;
    }

    public void ClearQueryAndMode()
    {
        ActiveQuickMode = SearchQuickMode.None;
        QueryText = string.Empty;
    }

    public bool HandleEmptyBackspace()
    {
        if (!string.IsNullOrEmpty(_queryText) || _activeQuickMode == SearchQuickMode.None)
        {
            return false;
        }

        ActiveQuickMode = SearchQuickMode.None;
        return true;
    }

    public void SetPointerHovering(bool hovering)
    {
        if (_isPointerHovering == hovering)
        {
            return;
        }

        _isPointerHovering = hovering;
        OnPropertyChanged(nameof(IsPointerHovering));
        OnPropertyChanged(nameof(EffectivePrivacyMaskActive));
        if (IsPrivacyMaskEnabled)
        {
            RebuildSnapshots();
            StateRefreshed?.Invoke();
        }
    }

    public void TogglePrivacyMask()
    {
        bool desired = !IsPrivacyMaskEnabled;
        if (_setPrivacyMaskPreference is not null)
        {
            _setPrivacyMaskPreference(desired);
        }
        else
        {
            _settings.Settings.Search.SearchDesktopPrivacyMask = desired;
            _settings.SaveDebounced();
        }

        RefreshState();
    }

    public async Task SubmitAsync()
    {
        string raw = _queryText;
        string trimmed = raw.Trim();

        if (HasCalcResult && !string.IsNullOrEmpty(_calcRawValue))
        {
            _copyText?.Invoke(_calcRawValue);
            FeedbackRequested?.Invoke(
                this,
                new WidgetFeedbackRequestedEventArgs(
                    new WidgetFeedbackRequest(
                        _localization.Format("Search.Widget.CalcCopied", _calcRawValue),
                        WidgetFeedbackSeverity.Success,
                        "search-calc-copied")));
            return;
        }

        if (_activeQuickMode == SearchQuickMode.Todo)
        {
            await ExecuteQuickTodoAsync(trimmed);
            return;
        }

        if (_activeQuickMode == SearchQuickMode.Note)
        {
            await ExecuteQuickNoteAsync(trimmed);
            return;
        }

        if (_activeQuickMode == SearchQuickMode.Command)
        {
            string commandQuery = string.IsNullOrEmpty(trimmed) ? "> " : $"> {trimmed}";
            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke(commandQuery);
            return;
        }

        if (!string.IsNullOrEmpty(trimmed))
        {
            ParsedSearchQuery parsed = SearchQuerySyntaxParser.Parse(trimmed);
            if (parsed.IsQuickTodoIntent && !string.IsNullOrWhiteSpace(parsed.QuickTodoText))
            {
                await ExecuteQuickTodoAsync(parsed.QuickTodoText.Trim());
                return;
            }

            if (parsed.IsQuickNoteIntent && !string.IsNullOrWhiteSpace(parsed.QuickNoteText))
            {
                await ExecuteQuickNoteAsync(parsed.QuickNoteText.Trim());
                return;
            }

            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke(trimmed);
            return;
        }

        _openSearchPopup?.Invoke();
    }

    public void RunQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _openSearchPopup?.Invoke();
            return;
        }

        _openSearchPopupWithQuery?.Invoke(query.Trim());
    }

    public void OpenFullPopup()
    {
        string trimmed = _queryText.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            string queryToOpen = _activeQuickMode switch
            {
                SearchQuickMode.Todo => $"+ {trimmed}",
                SearchQuickMode.Note => $"; {trimmed}",
                SearchQuickMode.Command => $"> {trimmed}",
                _ => trimmed
            };
            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke(queryToOpen);
            return;
        }

        string modePrefix = _activeQuickMode switch
        {
            SearchQuickMode.Todo => "+ ",
            SearchQuickMode.Note => "; ",
            SearchQuickMode.Command => "> ",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(modePrefix))
        {
            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke(modePrefix);
            return;
        }

        _openSearchPopup?.Invoke();
    }

    public void TogglePinQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || _historyService is null)
        {
            return;
        }

        _historyService.ToggleFavorite(query);
        RefreshState();
    }

    public void UnpinQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || _historyService is null)
        {
            return;
        }

        if (_historyService.IsFavorite(query))
        {
            _historyService.ToggleFavorite(query);
            RefreshState();
        }
    }

    public void RemoveRecentQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || _historyService is null)
        {
            return;
        }

        if (_historyService.RemoveRecentQuery(query))
        {
            RefreshState();
        }
    }

    public void ClearRecentQueries()
    {
        if (_historyService is null)
        {
            return;
        }

        _historyService.ClearRecentHistory();
        RefreshState();
    }

    public Task OpenRecentItemAsync(SearchRecommendationItem? item)
    {
        if (item is null || _openRecommendation is null)
        {
            return Task.CompletedTask;
        }

        return _openRecommendation(item);
    }

    public void OpenSearchSettings()
    {
        _openSettings?.Invoke("SearchSettings");
    }

    public void RefreshState()
    {
        if (_disposed)
        {
            return;
        }

        RebuildSnapshots();
        CapsuleRevision++;
        OnPropertyChanged(nameof(ShowHistory));
        OnPropertyChanged(nameof(ShowRecentOpened));
        OnPropertyChanged(nameof(IsPrivacyMaskEnabled));
        OnPropertyChanged(nameof(EffectivePrivacyMaskActive));
        OnPropertyChanged(nameof(IsInstantHandoff));
        OnPropertyChanged(nameof(IsEverythingConnected));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HotkeyDisplayText));
        StateRefreshed?.Invoke();
    }

    public (string PrimaryText, string FallbackText) BuildCapsuleSummary(bool hidesSensitiveContent)
    {
        string compactHint = _localization.T("Search.Widget.CompactHint");
        if (hidesSensitiveContent || IsPrivacyMaskEnabled)
        {
            return (string.Empty, _localization.T("Search.Widget.CompactPrivate"));
        }

        string? latest = _historyService?.FavoriteQueries.FirstOrDefault()
            ?? _historyService?.RecentQueries.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latest))
        {
            return (string.Empty, compactHint);
        }

        string trimmed = latest.Trim();
        if (trimmed.Length > 22)
        {
            trimmed = string.Concat(trimmed.AsSpan(0, 22), "...");
        }

        return (_localization.Format("Search.Widget.CompactRecent", trimmed), compactHint);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_historyService is not null)
        {
            _historyService.HistoryChanged -= OnExternalStateChanged;
        }

        _settings.SettingsChanged -= OnExternalStateChanged;
    }

    private async Task ExecuteQuickTodoAsync(string todoText)
    {
        if (string.IsNullOrWhiteSpace(todoText))
        {
            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke("+ ");
            return;
        }

        if (_executeSearchAction is not null && (_isTodoEnabled?.Invoke() ?? true))
        {
            ClearQueryAndMode();
            await _executeSearchAction($"quick-todo:{todoText}");
            return;
        }

        ClearQueryAndMode();
        _openSearchPopupWithQuery?.Invoke($"+ {todoText}");
    }

    private async Task ExecuteQuickNoteAsync(string noteText)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            ClearQueryAndMode();
            _openSearchPopupWithQuery?.Invoke("; ");
            return;
        }

        if (_executeSearchAction is not null && (_isQuickCaptureEnabled?.Invoke() ?? true))
        {
            ClearQueryAndMode();
            await _executeSearchAction($"quick-note:{noteText}");
            return;
        }

        ClearQueryAndMode();
        _openSearchPopupWithQuery?.Invoke($"; {noteText}");
    }

    private void TryAutoLatchPrefix()
    {
        if (_queryText.StartsWith("+ ", StringComparison.Ordinal))
        {
            _activeQuickMode = SearchQuickMode.Todo;
            _queryText = _queryText[2..];
        }
        else if (_queryText.StartsWith("; ", StringComparison.Ordinal))
        {
            _activeQuickMode = SearchQuickMode.Note;
            _queryText = _queryText[2..];
        }
        else if (_queryText.StartsWith("> ", StringComparison.Ordinal))
        {
            _activeQuickMode = SearchQuickMode.Command;
            _queryText = _queryText[2..];
        }
    }

    private void EvaluateCalculator()
    {
        if (_activeQuickMode != SearchQuickMode.None || string.IsNullOrWhiteSpace(_queryText))
        {
            ClearCalculator();
            return;
        }

        if (SearchCalculatorEvaluator.TryEvaluate(_queryText, out SearchCalculationResult calc))
        {
            CalcNormalizedExpression = calc.NormalizedExpression;
            CalcRawValue(calc.FormattedValue);
            CalcResultText = $"= {calc.FormattedValue}";
            HasCalcResult = true;
            return;
        }

        ClearCalculator();
    }

    private void CalcRawValue(string value)
    {
        _calcRawValue = value;
    }

    private void ClearCalculator()
    {
        _calcRawValue = string.Empty;
        CalcNormalizedExpression = string.Empty;
        CalcResultText = string.Empty;
        HasCalcResult = false;
    }

    internal void RequestRefresh() => OnExternalStateChanged();

    private void OnExternalStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (_uiDispatch is not null)
        {
            _uiDispatch(RefreshState);
        }
        else
        {
            RefreshState();
        }
    }

    private void RebuildSnapshots()
    {
        bool maskActive = EffectivePrivacyMaskActive;

        IReadOnlyList<string> favorites = _historyService?.FavoriteQueries ?? [];
        IReadOnlyList<string> recents = _historyService?.RecentQueries ?? [];
        IReadOnlyList<SearchRecommendationItem> results = _historyService?.RecentResults ?? [];

        var pinnedSet = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);

        PinnedQueryItems = favorites
            .Take(MaxPinnedItems)
            .Select(q => new SearchPinnedQueryItem(q, FormatMaskedText(q, maskActive)))
            .ToArray();

        RecentQueryItems = recents
            .Where(q => !pinnedSet.Contains(q))
            .Take(MaxRecentQueries)
            .Select(q => new SearchRecentQueryItem(q, FormatMaskedText(q, maskActive), IsPinned: false))
            .ToArray();

        WideStripItems = favorites
            .Concat(recents.Where(q => !pinnedSet.Contains(q)))
            .Take(MaxWideStripChips)
            .Select(q => new SearchPinnedQueryItem(q, FormatMaskedText(q, maskActive)))
            .ToArray();

        RecentOpenedItems = results
            .Take(MaxRecentOpenedItems)
            .Select(item =>
            {
                string displayTitle = FormatRecentOpenedTitle(item);
                string title = FormatMaskedText(displayTitle, maskActive);
                string subtitle = FormatRecentOpenedSubtitle(item, maskActive);
                return new SearchRecentOpenedItem(item, ResolveItemGlyph(item), title, subtitle);
            })
            .ToArray();
    }

    internal static string FormatRecentOpenedTitle(SearchRecommendationItem item)
    {
        string trimmed = item.Title?.Trim() ?? string.Empty;
        if (trimmed.Length > 4 &&
            (trimmed.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
        {
            string stem = trimmed[..^4].TrimEnd();
            if (stem.Length > 0)
            {
                return stem;
            }
        }

        return trimmed;
    }

    internal static string FormatCompactLocation(string? rawSubtitle, string? detailPath)
    {
        string candidate = rawSubtitle?.Trim() ?? string.Empty;
        if (candidate.Length > 0)
        {
            return LooksLikeFilePath(candidate)
                ? ExtractLeafFolderName(candidate)
                : candidate;
        }

        string trimmedDetail = detailPath?.Trim() ?? string.Empty;
        if (trimmedDetail.Length > 0 && LooksLikeFilePath(trimmedDetail))
        {
            string? parentDir = Path.GetDirectoryName(trimmedDetail.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                return ExtractLeafFolderName(parentDir);
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeFilePath(string value) =>
        value.Contains('\\', StringComparison.Ordinal) ||
        value.Contains('/', StringComparison.Ordinal) ||
        (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':');

    private static string ExtractLeafFolderName(string pathOrDir)
    {
        string trimmed = pathOrDir.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string withoutTrailing = trimmed.TrimEnd('\\', '/');
        if (withoutTrailing.Length == 2 && char.IsLetter(withoutTrailing[0]) && withoutTrailing[1] == ':')
        {
            return $"{withoutTrailing}\\";
        }

        string leaf = Path.GetFileName(withoutTrailing);
        return !string.IsNullOrEmpty(leaf) ? leaf : trimmed;
    }

    private string FormatRecentOpenedSubtitle(SearchRecommendationItem item, bool maskActive)
    {
        string typeLabel = item.Kind switch
        {
            SearchResultKind.Todo => _localization.T("Search.Tab.Todo"),
            SearchResultKind.QuickCapture => _localization.T("Search.Tab.Note"),
            SearchResultKind.Folder => _localization.T("Search.Tab.Folder"),
            SearchResultKind.File when IsAppDetailPath(item.DetailPath) => _localization.T("Search.Tab.App"),
            SearchResultKind.CustomWidget => _localization.T("Search.Tab.Widget"),
            _ => _localization.T("Search.Tab.File")
        };

        if (maskActive)
        {
            return typeLabel;
        }

        string compactLocation = FormatCompactLocation(item.Subtitle, item.DetailPath);
        if (!string.IsNullOrWhiteSpace(compactLocation) &&
            !string.Equals(compactLocation, typeLabel, StringComparison.OrdinalIgnoreCase))
        {
            return string.Create(CultureInfo.CurrentCulture, $"{typeLabel} · {compactLocation}");
        }

        return typeLabel;
    }

    internal static string ResolveItemGlyph(SearchRecommendationItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Glyph))
        {
            return CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs(
                item.Glyph,
                WindowsCompatibilityService.IsWindows11OrLater);
        }

        return item.Kind switch
        {
            SearchResultKind.Todo => "\uE73A",
            SearchResultKind.QuickCapture => "\uE70B",
            SearchResultKind.Folder => "\uE8B7",
            SearchResultKind.File when IsAppDetailPath(item.DetailPath) => "\uE71D",
            SearchResultKind.CustomWidget => "\uE71D",
            _ => "\uE8A5"
        };
    }

    private static bool IsAppDetailPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase));

    private static string FormatMaskedText(string? raw, bool maskActive)
    {
        string trimmed = raw?.Trim() ?? string.Empty;
        if (!maskActive || trimmed.Length == 0)
        {
            return trimmed;
        }

        return new string('•', Math.Clamp(trimmed.Length, 4, 10));
    }
}
