using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public enum TodoFilter
{
    All,
    Active,
    Today,
    Overdue,
    ThisWeek,
    ThisMonth,
    Important,
    Completed
}

public enum TodoColorFilter
{
    All,
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Purple,
    Teal,
    Pink
}

public enum TodoDuePreset
{
    Today,
    Tomorrow,
    ThisWeek,
    NextMonday,
    Clear
}

internal sealed record TodoUndoDeletedEntry(
    int OriginalIndex,
    TodoItem ItemSnapshot);

internal sealed record TodoUndoModifiedEntry(
    string ItemId,
    DateTimeOffset? PreviousDueDate,
    bool PreviousHasDueTime,
    DateTimeOffset? PreviousReminderDismissedForDueDate,
    DateTimeOffset? PreviousSnoozedUntil,
    TodoRecurrence? PreviousRecurrence,
    bool? PreviousIsCompleted = null,
    DateTimeOffset? PreviousCompletedAt = null,
    string? PreviousGeneratedNextItemId = null,
    string? PreviousRecurrenceSeriesId = null,
    DateTimeOffset? PreviousSnoozeLastNotifiedAt = null);

internal sealed record TodoUndoSnapshot(
    IReadOnlyList<TodoUndoDeletedEntry> DeletedEntries,
    string Message,
    IReadOnlyList<TodoUndoModifiedEntry>? ModifiedEntries = null,
    IReadOnlyList<string>? AddedItemIdsToRetract = null);

public sealed partial class TodoWidgetViewModel : ObservableObject, IDisposable
{
    private readonly TodoWidgetStore _store;
    private readonly LocalizationService _localizationService;
    private readonly WidgetConfig _config;
    private readonly SettingsService? _settingsService;
    private readonly Func<string, Task<bool>>? _recycleAttachmentFileAsync;
    private readonly Func<DateTimeOffset> _clock;
    private DateTime _lastEvaluatedLocalDate;
    private TodoFilter _selectedFilter = TodoFilter.All;
    private TodoColorFilter _selectedColorFilter = TodoColorFilter.All;
    private string _inputText = string.Empty;
    private double _textSize = SettingsService.DefaultTextSize;
    private double _contentTextSize = SettingsService.DefaultTextSize;
    private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    private string _newTaskPosition = SettingsService.TodoNewTaskPositionTop;
    private string _tabStyle = SettingsService.WidgetTabStyleButton;
    private bool _showTabBar = true;
    private bool _showAllTab = true;
    private bool _showActiveTab;
    private bool _showTodayTab = true;
    private bool _showOverdueTab = true;
    private bool _showThisWeekTab;
    private bool _showThisMonthTab;
    private bool _showImportantTab = true;
    private bool _showCompletedTab = true;
    private bool _showCompletedTasks = true;
    private bool _showFooterStats = true;
    private bool _showClearCompletedButton = true;
    private bool _draftImportant;
    private DateTimeOffset? _draftDueDate;
    private bool _draftHasDueTime;
    private string _draftRecurrenceMode = TodoRecurrenceMode.None;
    private bool _userManuallyOverrodeDraftDue;
    private bool _userManuallyOverrodeDraftRecurrence;
    private bool _autoAppliedDraftDue;
    private bool _autoAppliedDraftRecurrence;
    private bool _suppressDraftManualOverrideTracking;
    private string _searchQuery = string.Empty;
    private bool _isSearchOpen;
    private string? _lastSingleCreatedItemId;
    private TodoItemViewModel? _selectedDetailItem;
    private bool _isCreatingDetailItem;
    private bool _isTodayOverdueExpanded = true;
    private TodoUndoSnapshot? _undoSnapshot;
    // Soft-deleted items kept out of Items but persisted on save: a merge
    // restore treats an absent id as "unknown to this device" and would
    // resurrect deleted items. Live ids win over tombstones at save time.
    private readonly List<TodoItem> _tombstones = [];
    private const int MaxRetainedTombstones = 500;
    private bool _isInitialized;
    private bool _isDisposed;
    internal bool IsDisposed => _isDisposed;
    internal SettingsService? SettingsService => _settingsService;
    internal string PreviewCacheScope => TodoAttachmentViewModel.GetTodoPreviewCacheScope(_config.Id);
    private readonly HashSet<string> _expandedRecurringHistoryGroupKeys = new(StringComparer.Ordinal);

    public TodoWidgetViewModel(
        TodoWidgetStore store,
        LocalizationService localizationService,
        WidgetConfig config,
        SettingsService? settingsService = null,
        Func<string, Task<bool>>? recycleAttachmentFileAsync = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _localizationService = localizationService;
        _config = config;
        _settingsService = settingsService;
        _recycleAttachmentFileAsync = recycleAttachmentFileAsync;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _lastEvaluatedLocalDate = CurrentLocalDate;
        if (_settingsService is not null)
        {
            _textSize = SettingsService.NormalizeTextSize(
                (_settingsService.Settings.TodoListTextSize > 0 ? _settingsService.Settings.TodoListTextSize : _settingsService.Settings.TextSize));
            _contentTextSize = SettingsService.NormalizeTextSize(
                (_settingsService.Settings.TodoContentTextSize > 0 ? _settingsService.Settings.TodoContentTextSize : _settingsService.Settings.TextSize));
            _layoutDensityScale = NormalizeDensity(_settingsService.Settings.LayoutDensityScale);
            ApplyTodoSettings(_settingsService.Settings, updateFilter: false);
        }

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    internal DateTime CurrentLocalDate => _clock().ToLocalTime().Date;

    private DateTimeOffset CurrentUtcNow => _clock().ToUniversalTime();

    private TodoItemViewModel CreateItemViewModel(TodoItem item) =>
        new(item, _localizationService, PreviewCacheScope, _clock);

    public string WidgetId => _config.Id;

    internal string? LastSingleCreatedItemId => _lastSingleCreatedItemId;

    internal void ClearLastSingleCreatedItemId(string? expectedId = null)
    {
        if (expectedId is null || string.Equals(_lastSingleCreatedItemId, expectedId, StringComparison.Ordinal))
        {
            _lastSingleCreatedItemId = null;
        }
    }

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Todo.Title")
        : _config.Name;

    public ObservableCollection<TodoItemViewModel> Items { get; } = [];

    public ObservableCollection<TodoItemViewModel> VisibleItems { get; } = [];

    private object[]? _cachedVisibleItemsSource;

    // ListView.ItemsSource is object-valued at the WinRT ABI boundary.
    // Keep the typed collection for filtering and project a concrete object
    // array so populated task rows remain available in Native AOT builds.
    public object[] VisibleItemsSource => _cachedVisibleItemsSource ??= VisibleItems.Cast<object>().ToArray();

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                OnPropertyChanged(nameof(CanAddInput));
                ApplyNaturalDateRecognitionForCurrentDraft(value);
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref _searchQuery, normalized))
            {
                OnPropertyChanged(nameof(HasSearchQuery));
                OnPropertyChanged(nameof(SearchBarVisibility));
                OnPropertyChanged(nameof(SearchClearButtonVisibility));
                RefreshVisibleItems();
                OnPropertyChanged(nameof(IsAllTasksCompletedEmptyState));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateText));
                OnPropertyChanged(nameof(EmptyStateVisibility));
            }
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (SetProperty(ref _isSearchOpen, value))
            {
                if (!value && HasSearchQuery)
                {
                    SearchQuery = string.Empty;
                }

                OnPropertyChanged(nameof(SearchBarVisibility));
            }
            else if (!value && HasSearchQuery)
            {
                SearchQuery = string.Empty;
            }
        }
    }

    public Visibility SearchBarVisibility => _isSearchOpen || HasSearchQuery
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SearchClearButtonVisibility => HasSearchQuery
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanAddInput => !string.IsNullOrWhiteSpace(InputText);

    public bool DraftImportant
    {
        get => _draftImportant;
        set
        {
            if (SetProperty(ref _draftImportant, value))
            {
                OnPropertyChanged(nameof(DraftImportantGlyph));
                OnPropertyChanged(nameof(DraftImportantTooltipText));
            }
        }
    }

    public string DraftImportantTooltipText => _localizationService.T(
        _draftImportant ? "Todo.Menu.UnmarkImportant" : "Todo.Menu.MarkImportant");

    public DateTimeOffset? DraftDueDate
    {
        get => _draftDueDate;
        set => SetDraftDueDate(value, hasDueTime: value is not null);
    }

    public bool DraftHasDueTime
    {
        get => _draftDueDate is not null && _draftHasDueTime;
        set => SetDraftDueDate(_draftDueDate, hasDueTime: value);
    }

    public void SetDraftDueDate(DateTimeOffset? dueDate, bool hasDueTime)
    {
        if (!_suppressDraftManualOverrideTracking)
        {
            _userManuallyOverrodeDraftDue = true;
            _autoAppliedDraftDue = false;
        }

        bool effectiveHasTime = dueDate is not null && hasDueTime;
        DateTimeOffset? normalized = dueDate is null
            ? null
            : effectiveHasTime
                ? NormalizeDueDate(dueDate, hasDueTime: true)
                : TodoWidgetStore.NormalizeAllDayDueDate(dueDate.Value);
        bool timeFlagChanged = _draftHasDueTime != effectiveHasTime;
        _draftHasDueTime = effectiveHasTime;
        bool dateChanged = SetProperty(ref _draftDueDate, normalized, nameof(DraftDueDate));
        if (timeFlagChanged)
        {
            OnPropertyChanged(nameof(DraftHasDueTime));
        }

        if (dateChanged || timeFlagChanged)
        {
            OnPropertyChanged(nameof(DraftDueDateText));
            OnPropertyChanged(nameof(DraftDueDateActive));
        }

        if (normalized is null && !string.Equals(_draftRecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            if (!_suppressDraftManualOverrideTracking)
            {
                _userManuallyOverrodeDraftRecurrence = true;
                _autoAppliedDraftRecurrence = false;
            }

            _draftRecurrenceMode = TodoRecurrenceMode.None;
            OnPropertyChanged(nameof(DraftRecurrenceMode));
            OnPropertyChanged(nameof(DraftRecurrenceActive));
            OnPropertyChanged(nameof(DraftRecurrenceText));
            OnPropertyChanged(nameof(DraftRecurrenceTooltipText));
        }
    }

    public string DraftRecurrenceMode => _draftRecurrenceMode;

    public bool DraftRecurrenceActive =>
        !string.Equals(_draftRecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);

    public string DraftRecurrenceText => DraftRecurrenceActive
        ? _localizationService.T(TodoRecurrenceService.GetLocalizationKey(_draftRecurrenceMode))
        : _localizationService.T("Todo.Menu.Recurrence");

    public string DraftRecurrenceTooltipText => DraftRecurrenceText;

    public void SetDraftRecurrenceMode(string? mode)
    {
        if (!_suppressDraftManualOverrideTracking)
        {
            _userManuallyOverrodeDraftRecurrence = true;
            _autoAppliedDraftRecurrence = false;
        }

        string normalized = TodoRecurrenceMode.Normalize(mode);
        if (!string.Equals(normalized, TodoRecurrenceMode.None, StringComparison.Ordinal) &&
            _draftDueDate is null)
        {
            bool previousSuppress = _suppressDraftManualOverrideTracking;
            _suppressDraftManualOverrideTracking = true;
            try
            {
                SetDraftDueDate(TodoWidgetStore.NormalizeAllDayDueDate(_clock()), hasDueTime: false);
            }
            finally
            {
                _suppressDraftManualOverrideTracking = previousSuppress;
            }
        }

        if (SetProperty(ref _draftRecurrenceMode, normalized, nameof(DraftRecurrenceMode)))
        {
            OnPropertyChanged(nameof(DraftRecurrenceActive));
            OnPropertyChanged(nameof(DraftRecurrenceText));
            OnPropertyChanged(nameof(DraftRecurrenceTooltipText));
        }
    }

    public void ResetDraftComposerState(bool clearMetadata = false)
    {
        _suppressDraftManualOverrideTracking = true;
        try
        {
            if (clearMetadata)
            {
                DraftImportant = false;
                SetDraftDueDate(null, hasDueTime: false);
                SetDraftRecurrenceMode(TodoRecurrenceMode.None);
            }
        }
        finally
        {
            _suppressDraftManualOverrideTracking = false;
        }

        _userManuallyOverrodeDraftDue = false;
        _userManuallyOverrodeDraftRecurrence = false;
        _autoAppliedDraftDue = false;
        _autoAppliedDraftRecurrence = false;
    }

    private void ApplyNaturalDateRecognitionForCurrentDraft(string? text)
    {
        if (_suppressDraftManualOverrideTracking)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _suppressDraftManualOverrideTracking = true;
            try
            {
                if (_autoAppliedDraftRecurrence)
                {
                    SetDraftRecurrenceMode(TodoRecurrenceMode.None);
                    _autoAppliedDraftRecurrence = false;
                }

                if (_autoAppliedDraftDue)
                {
                    SetDraftDueDate(null, hasDueTime: false);
                    _autoAppliedDraftDue = false;
                }
            }
            finally
            {
                _suppressDraftManualOverrideTracking = false;
            }

            if (_draftDueDate is null)
            {
                _userManuallyOverrodeDraftDue = false;
            }

            if (!DraftRecurrenceActive)
            {
                _userManuallyOverrodeDraftRecurrence = false;
            }

            return;
        }

        bool parsed = TodoNaturalDateParser.TryParse(text, _clock(), out TodoNaturalDateMatch match);
        _suppressDraftManualOverrideTracking = true;
        try
        {
            if (!_userManuallyOverrodeDraftDue)
            {
                if (parsed && match.HasDueDate)
                {
                    SetDraftDueDate(match.DueDate, match.HasDueTime);
                    _autoAppliedDraftDue = true;
                }
                else if (_autoAppliedDraftDue)
                {
                    SetDraftDueDate(null, hasDueTime: false);
                    _autoAppliedDraftDue = false;
                }
            }

            if (!_userManuallyOverrodeDraftRecurrence)
            {
                if (parsed && match.HasRecurrence)
                {
                    SetDraftRecurrenceMode(match.RecurrenceMode);
                    _autoAppliedDraftRecurrence = true;
                }
                else if (_autoAppliedDraftRecurrence)
                {
                    SetDraftRecurrenceMode(TodoRecurrenceMode.None);
                    _autoAppliedDraftRecurrence = false;
                }
            }
        }
        finally
        {
            _suppressDraftManualOverrideTracking = false;
        }
    }

    public string DraftImportantGlyph => _draftImportant ? "\uE735" : "\uE734";

    public string DraftDueDateText
    {
        get
        {
            if (_draftDueDate is not { } dueDate)
            {
                return _localizationService.T("Todo.Menu.DueDate");
            }

            DateTimeOffset local = dueDate.ToLocalTime();
            var today = CurrentLocalDate;
            if (local.Date == today)
            {
                return DraftHasDueTime
                    ? _localizationService.Format("Todo.Due.TodayAt", local.ToString("HH:mm"))
                    : _localizationService.T("Todo.Due.Today");
            }

            if (local.Date == today.AddDays(1))
            {
                return DraftHasDueTime
                    ? _localizationService.Format("Todo.Due.TomorrowAt", local.ToString("HH:mm"))
                    : _localizationService.T("Todo.Due.Tomorrow");
            }

            if (!DraftHasDueTime)
            {
                return local.Date.Year == today.Year
                    ? $"{local.Month}/{local.Day}"
                    : $"{local.Year}/{local.Month}/{local.Day}";
            }

            return $"{local.Month}/{local.Day} {local:HH:mm}";
        }
    }

    public bool DraftDueDateActive => _draftDueDate is not null;

    public TodoItemViewModel? SelectedDetailItem
    {
        get => _selectedDetailItem;
        private set
        {
            TodoItemViewModel? previous = _selectedDetailItem;
            if (SetProperty(ref _selectedDetailItem, value))
            {
                if (previous is not null)
                {
                    previous.IsDetailSelected = false;
                }

                if (value is not null)
                {
                    value.IsDetailSelected = true;
                }

                OnPropertyChanged(nameof(IsDetailPageOpen));
                OnPropertyChanged(nameof(ListPageVisibility));
                OnPropertyChanged(nameof(DetailPageVisibility));
            }
        }
    }

    public bool IsDetailPageOpen => SelectedDetailItem is not null;

    public bool IsCreatingDetailItem
    {
        get => _isCreatingDetailItem;
        private set => SetProperty(ref _isCreatingDetailItem, value);
    }

    public Visibility ListPageVisibility => IsDetailPageOpen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DetailPageVisibility => IsDetailPageOpen ? Visibility.Visible : Visibility.Collapsed;

    public TodoFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                RefreshVisibleItems();
                OnPropertyChanged(nameof(IsAllTasksCompletedEmptyState));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateText));
                OnPropertyChanged(nameof(IsAllFilterSelected));
                OnPropertyChanged(nameof(IsActiveFilterSelected));
                OnPropertyChanged(nameof(IsTodayFilterSelected));
                OnPropertyChanged(nameof(IsOverdueFilterSelected));
                OnPropertyChanged(nameof(OverdueSectionChevronVisibility));
                OnPropertyChanged(nameof(IsOverdueSectionToggleable));
                OnPropertyChanged(nameof(OverdueSectionToggleVisibility));
                OnPropertyChanged(nameof(OverdueSectionSummaryVisibility));
                OnPropertyChanged(nameof(IsThisWeekFilterSelected));
                OnPropertyChanged(nameof(IsThisMonthFilterSelected));
                OnPropertyChanged(nameof(IsImportantFilterSelected));
                OnPropertyChanged(nameof(IsCompletedFilterSelected));
                OnPropertyChanged(nameof(TodayOverdueBarVisibility));
                OnPropertyChanged(nameof(EmptyStateVisibility));
            }
        }
    }

    public TodoColorFilter SelectedColorFilter
    {
        get => _selectedColorFilter;
        set
        {
            if (SetProperty(ref _selectedColorFilter, value))
            {
                RefreshVisibleItems();
                OnPropertyChanged(nameof(IsAllTasksCompletedEmptyState));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateText));
                OnPropertyChanged(nameof(IsRedColorFilterSelected));
                OnPropertyChanged(nameof(IsOrangeColorFilterSelected));
                OnPropertyChanged(nameof(IsYellowColorFilterSelected));
                OnPropertyChanged(nameof(IsGreenColorFilterSelected));
                OnPropertyChanged(nameof(IsBlueColorFilterSelected));
                OnPropertyChanged(nameof(IsPurpleColorFilterSelected));
                OnPropertyChanged(nameof(IsTealColorFilterSelected));
                OnPropertyChanged(nameof(IsPinkColorFilterSelected));
                OnPropertyChanged(nameof(ColorFilterVisibility));
                OnPropertyChanged(nameof(TodayOverdueCount));
                OnPropertyChanged(nameof(TodayOverdueBarVisibility));
                OnPropertyChanged(nameof(TodayOverdueHeaderText));
                OnPropertyChanged(nameof(OverdueTabSummaryText));
                OnPropertyChanged(nameof(EmptyStateVisibility));
                RefreshColorFilterVisibilityProperties();
            }
        }
    }

    public int TotalCount => Items.Count(item => !IsDeferredFutureOccurrence(item));

    public int ActiveCount => Items.Count(item => !item.IsCompleted && !IsDeferredFutureOccurrence(item));

    public int CompletedCount => Items.Count(item => item.IsCompleted);

    public int AllFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && ShouldCountInNonCompletedFilter(item));

    public int TodayDueOnlyCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && IsDueToday(item) && ShouldCountInNonCompletedFilter(item));

    public int TodayFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && (IsInTodayView(item) || IsSameDayCompletedRecurringInTodayView(item)) && ShouldCountInNonCompletedFilter(item));

    public int TodayOverdueCount => Items.Count(item => IsOverdueFromPreviousDays(item) && MatchesActiveColorFilter(item));

    public bool IsTodayOverdueExpanded => _isTodayOverdueExpanded;

    public bool ShowTodayOverdueSection =>
        TodayOverdueCount > 0 &&
        SelectedFilter is TodoFilter.Today or TodoFilter.Overdue or TodoFilter.All or TodoFilter.Active;

    public Visibility TodayOverdueBarVisibility =>
        ShowTodayOverdueSection
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool IsOverdueSectionToggleable => SelectedFilter == TodoFilter.Today;

    public Visibility OverdueSectionChevronVisibility =>
        SelectedFilter == TodoFilter.Today ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OverdueSectionToggleVisibility =>
        SelectedFilter == TodoFilter.Today ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OverdueSectionSummaryVisibility =>
        SelectedFilter is TodoFilter.Overdue or TodoFilter.All or TodoFilter.Active
            ? Visibility.Visible
            : Visibility.Collapsed;

    public int OverdueFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && item.IsOverdue);

    public string TodayOverdueHeaderText =>
        _localizationService.Format("Todo.Overdue.SectionCount", TodayOverdueCount);

    public string OverdueTabSummaryText =>
        _localizationService.Format("Todo.Overdue.TabSummary", TodayOverdueCount);

    public string TodayOverdueExpandGlyph => _isTodayOverdueExpanded ? "\uE70D" : "\uE70E";

    public string TodayOverdueToggleTooltipText => _localizationService.T("Todo.Overdue.ToggleSection");

    public string RescheduleOverdueText => _localizationService.T("Todo.Overdue.RescheduleToday");

    public int ThisWeekFilterCount => Items.Count(item => IsDueThisWeek(item) && ShouldCountInNonCompletedFilter(item));

    public int ThisMonthFilterCount => Items.Count(item => IsDueThisMonth(item) && ShouldCountInNonCompletedFilter(item));

    public int ImportantFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && item.IsImportant && ShouldCountInNonCompletedFilter(item));

    public int RedColorFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && item.HasRedMarker && ShouldCountInNonCompletedFilter(item));

    public int OrangeColorFilterCount => GetColorFilterCount(TodoItem.OrangeColorMarker);

    public int YellowColorFilterCount => GetColorFilterCount(TodoItem.YellowColorMarker);

    public int GreenColorFilterCount => GetColorFilterCount(TodoItem.GreenColorMarker);

    public int BlueColorFilterCount => GetColorFilterCount(TodoItem.BlueColorMarker);

    public int PurpleColorFilterCount => GetColorFilterCount(TodoItem.PurpleColorMarker);

    public int TealColorFilterCount => GetColorFilterCount(TodoItem.TealColorMarker);

    public int PinkColorFilterCount => GetColorFilterCount(TodoItem.PinkColorMarker);

    public int AnyColorFilterCount => Items.Count(item => !IsDeferredFutureOccurrence(item) && item.HasColorMarker && ShouldCountInNonCompletedFilter(item));

    public bool HasCompletedItems => CompletedCount > 0;

    public bool HasItems => TotalCount > 0;

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public Visibility ListVisibility => HasVisibleItems ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility =>
        HasVisibleItems || (!HasSearchQuery && SelectedFilter == TodoFilter.Today && TodayOverdueCount > 0)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility FooterStatsVisibility => ShowFooterStats ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ClearCompletedButtonVisibility => ShowClearCompletedButton ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FooterVisibility => ShowFooterStats || ShowClearCompletedButton ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UndoBarVisibility => CanUndoLastAction ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ColorFilterVisibility =>
        AnyColorFilterCount == 0 && SelectedColorFilter == TodoColorFilter.All
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility RedColorFilterVisibility => Visibility.Visible;

    public Visibility OrangeColorFilterVisibility => Visibility.Visible;

    public Visibility YellowColorFilterVisibility => Visibility.Visible;

    public Visibility GreenColorFilterVisibility => Visibility.Visible;

    public Visibility BlueColorFilterVisibility => Visibility.Visible;

    public Visibility PurpleColorFilterVisibility => Visibility.Visible;

    public Visibility TealColorFilterVisibility => Visibility.Visible;

    public Visibility PinkColorFilterVisibility => Visibility.Visible;

    public string AddPlaceholderText => _localizationService.T("Todo.AddPlaceholder");

    public string ExpandInputTooltipText => _localizationService.T("QuickCapture.ExpandInput");

    public string CancelText => _localizationService.T("Common.Cancel");

    public string QuickComposerHintText =>
        EditorEnterBehavior == SettingsService.EditorEnterBehaviorEnterSaves
            ? _localizationService.T("Settings.ContentEditor.EnterBehavior.EnterSaves")
            : _localizationService.T("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves");

    public string AllFilterText => FormatFilterText("Todo.Filter.All", AllFilterCount);

    public string ActiveFilterText => FormatFilterText("Todo.Filter.Active", ActiveCount);

    public string TodayFilterText => FormatFilterText("Todo.Filter.Today", TodayFilterCount);

    public string OverdueFilterText => FormatFilterText("Todo.Filter.Overdue", OverdueFilterCount);

    public string ThisWeekFilterText => FormatFilterText("Todo.Filter.ThisWeek", ThisWeekFilterCount);

    public string ThisMonthFilterText => FormatFilterText("Todo.Filter.ThisMonth", ThisMonthFilterCount);

    public string ImportantFilterText => FormatFilterText("Todo.Filter.Important", ImportantFilterCount);

    public string CompletedFilterText => FormatFilterText("Todo.Filter.Completed", CompletedCount);

    public string ListHeaderText => _localizationService.Format("Todo.List.Header", VisibleItems.Count);

    public string DetailBackText => _localizationService.T("Todo.Detail.Back");

    public string DetailSaveText => _localizationService.T("Common.Save");

    public string CopyText => _localizationService.T("Common.Copy");

    public string RenameText => _localizationService.T("Todo.Menu.Rename");

    public string ConvertToQuickCaptureText => _localizationService.T("Todo.Menu.ConvertToQuickCapture");

    public string DueDateText => _localizationService.T("Todo.Menu.DueDate");

    public string SearchPlaceholderText => _localizationService.T("Search.Scope.Placeholder.Todos");

    public string CloseSearchText => _localizationService.T("Search.Close");

    public string ClearSearchText => _localizationService.T("Widget.Search.Clear");

    public string DetailAddFileText => _localizationService.T("Todo.Detail.AddFile");

    public string DetailRemoveAttachmentText => _localizationService.T("Todo.Detail.RemoveAttachment");

    public string DetailFileMissingText => _localizationService.T("Todo.Detail.FileMissing");

    public string DetailStepsText => _localizationService.T("Todo.Detail.Steps");

    public string DetailAddStepText => _localizationService.T("Todo.Detail.AddStep");

    public string DetailNotesText => _localizationService.T("Todo.Detail.Notes");

    public string DetailNotesPlaceholderText => _localizationService.T("Todo.Detail.NotesPlaceholder");

    public string DetailNotesEditText => _localizationService.T("Todo.Menu.Edit");

    public string DetailNotesDoneText => _localizationService.T("Common.Save");

    public string DetailNotesRetryText => _localizationService.T("Settings.Update.OneClick.Retry");

    public string DetailNotesSaveFailedText => _localizationService.T("Todo.Detail.NotesSaveFailed");

    public string DetailMoreActionsText => _localizationService.T("Todo.Detail.MoreActions");

    public string DetailResizePanesText => _localizationService.T("Todo.Detail.ResizePanes");

    public string DetailResizeTitleEditorText =>
        _localizationService.T("Todo.Detail.ResizeTitleEditor");

    public string WideDetailEmptyTitle => _localizationService.T("Todo.Detail.EmptyTitle");

    public string WideDetailEmptyText => _localizationService.T("Todo.Detail.EmptyText");

    public MasterDetailLayoutPreference LayoutPreference =>
        SettingsService.NormalizeTodoLayoutMode(
            _settingsService?.Settings.TodoLayoutMode,
            _settingsService?.Settings.TodoUseWideDetailPane ?? true) switch
        {
            SettingsService.TodoLayoutModeSinglePane => MasterDetailLayoutPreference.SinglePane,
            SettingsService.TodoLayoutModeDualPane => MasterDetailLayoutPreference.DualPane,
            _ => MasterDetailLayoutPreference.Auto
        };

    public bool UseWideDetailPane => LayoutPreference != MasterDetailLayoutPreference.SinglePane;

    public bool AutoSelectFirstInWideLayout =>
        _settingsService?.Settings.TodoAutoSelectFirstInWideLayout ?? true;

    public double PreferredMasterPaneWidth =>
        new MasterDetailLayoutPolicy().NormalizePersistedMasterWidth(
            TodoMasterDetailSettings.GetMasterPaneWidth(_config));

    public double? PreferredTitleEditorHeight =>
        TodoMasterDetailSettings.GetTitleEditorHeight(_config);

    public void PersistMasterPaneWidth(double width)
    {
        if (TodoMasterDetailSettings.SetMasterPaneWidth(_config, width))
        {
            _settingsService?.SaveDebounced();
        }
    }

    public void PersistTitleEditorHeight(double height)
    {
        if (TodoMasterDetailSettings.SetTitleEditorHeight(_config, height))
        {
            _settingsService?.SaveDebounced();
        }
    }

    public void ClearPreferredTitleEditorHeight()
    {
        if (TodoMasterDetailSettings.ClearTitleEditorHeight(_config))
        {
            _settingsService?.SaveDebounced();
        }
    }

    public string DeleteText => _localizationService.T("Common.Delete");

    public string ColorMarkerText => _localizationService.T("Todo.Menu.ColorMarker");

    public string RedColorText => _localizationService.T("Todo.Color.Red");

    public string OrangeColorText => _localizationService.T("Todo.Color.Orange");

    public string YellowColorText => _localizationService.T("Todo.Color.Yellow");

    public string GreenColorText => _localizationService.T("Todo.Color.Green");

    public string BlueColorText => _localizationService.T("Todo.Color.Blue");

    public string PurpleColorText => _localizationService.T("Todo.Color.Purple");

    public string TealColorText => _localizationService.T("Todo.Color.Teal");

    public string PinkColorText => _localizationService.T("Todo.Color.Pink");

    public bool IsAllTasksCompletedEmptyState =>
        !HasSearchQuery &&
        !HasVisibleItems &&
        TotalCount > 0 &&
        ActiveCount == 0 &&
        SelectedFilter == TodoFilter.All &&
        SelectedColorFilter == TodoColorFilter.All;

    public string EmptyStateTitle => HasSearchQuery && !HasVisibleItems
        ? _localizationService.T("Search.NoResults.Title")
        : IsAllTasksCompletedEmptyState
            ? _localizationService.T("Todo.Empty.AllCompleted")
            : _localizationService.T("Todo.Empty.Title");

    public string EmptyStateText => SelectedFilter switch
    {
        _ when HasSearchQuery && !HasVisibleItems => _localizationService.T("Search.NoResults.Subtitle"),
        _ when SelectedColorFilter != TodoColorFilter.All => _localizationService.T("Todo.Empty.Color"),
        TodoFilter.Active => _localizationService.T("Todo.Empty.Active"),
        TodoFilter.Today => _localizationService.T("Todo.Empty.Today"),
        TodoFilter.Overdue => _localizationService.T("Todo.Empty.Overdue"),
        TodoFilter.ThisWeek => _localizationService.T("Todo.Empty.ThisWeek"),
        TodoFilter.ThisMonth => _localizationService.T("Todo.Empty.ThisMonth"),
        TodoFilter.Important => _localizationService.T("Todo.Empty.Important"),
        TodoFilter.Completed => _localizationService.T("Todo.Empty.Completed"),
        _ when IsAllTasksCompletedEmptyState
            => _localizationService.T("Todo.Empty.AllCompletedDesc"),
        _ => _localizationService.T("Todo.Empty.All")
    };

    public string ClearCompletedText => _localizationService.T("Todo.ClearCompleted");

    public string ItemsLeftText => string.Format(_localizationService.T("Todo.ItemsLeft"), ActiveCount);

    public string UndoText => _undoSnapshot?.Message ?? string.Empty;

    public string UndoActionText => _localizationService.T("Common.Undo");

    public bool IsAllFilterSelected => SelectedFilter == TodoFilter.All;

    public bool IsActiveFilterSelected => SelectedFilter == TodoFilter.Active;

    public bool IsTodayFilterSelected => SelectedFilter == TodoFilter.Today;

    public bool IsOverdueFilterSelected => SelectedFilter == TodoFilter.Overdue;

    public bool IsThisWeekFilterSelected => SelectedFilter == TodoFilter.ThisWeek;

    public bool IsThisMonthFilterSelected => SelectedFilter == TodoFilter.ThisMonth;

    public bool IsImportantFilterSelected => SelectedFilter == TodoFilter.Important;

    public bool IsCompletedFilterSelected => SelectedFilter == TodoFilter.Completed;

    public bool IsRedColorFilterSelected => SelectedColorFilter == TodoColorFilter.Red;

    public bool IsOrangeColorFilterSelected => SelectedColorFilter == TodoColorFilter.Orange;

    public bool IsYellowColorFilterSelected => SelectedColorFilter == TodoColorFilter.Yellow;

    public bool IsGreenColorFilterSelected => SelectedColorFilter == TodoColorFilter.Green;

    public bool IsBlueColorFilterSelected => SelectedColorFilter == TodoColorFilter.Blue;

    public bool IsPurpleColorFilterSelected => SelectedColorFilter == TodoColorFilter.Purple;

    public bool IsTealColorFilterSelected => SelectedColorFilter == TodoColorFilter.Teal;

    public bool IsPinkColorFilterSelected => SelectedColorFilter == TodoColorFilter.Pink;

    public string TabStyle
    {
        get => _tabStyle;
        private set => SetProperty(ref _tabStyle, SettingsService.NormalizeWidgetTabStyle(value));
    }

    public Visibility TabBarVisibility => _showTabBar ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AllTabVisibility => _showAllTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActiveTabVisibility => _showActiveTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TodayTabVisibility => _showTodayTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverdueTabVisibility => _showOverdueTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThisWeekTabVisibility => _showThisWeekTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThisMonthTabVisibility => _showThisMonthTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImportantTabVisibility => _showImportantTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletedTabVisibility => _showCompletedTab ? Visibility.Visible : Visibility.Collapsed;

    public int VisibleTabCount =>
        (_showAllTab ? 1 : 0) +
        (_showActiveTab ? 1 : 0) +
        (_showTodayTab ? 1 : 0) +
        (_showOverdueTab ? 1 : 0) +
        (_showThisWeekTab ? 1 : 0) +
        (_showThisMonthTab ? 1 : 0) +
        (_showImportantTab ? 1 : 0) +
        (_showCompletedTab ? 1 : 0);

    public string NewTaskPosition
    {
        get => _newTaskPosition;
        private set => SetProperty(ref _newTaskPosition, NormalizeNewTaskPosition(value));
    }

    public bool ShowCompletedTasks
    {
        get => _showCompletedTasks;
        private set
        {
            if (SetProperty(ref _showCompletedTasks, value))
            {
                RefreshVisibleItems();
                RefreshCountProperties();
            }
        }
    }

    public bool ShowFooterStats
    {
        get => _showFooterStats;
        private set
        {
            if (SetProperty(ref _showFooterStats, value))
            {
                OnPropertyChanged(nameof(FooterStatsVisibility));
                OnPropertyChanged(nameof(FooterVisibility));
            }
        }
    }

    public bool ShowClearCompletedButton
    {
        get => _showClearCompletedButton;
        private set
        {
            if (SetProperty(ref _showClearCompletedButton, value))
            {
                OnPropertyChanged(nameof(ClearCompletedButtonVisibility));
                OnPropertyChanged(nameof(FooterVisibility));
            }
        }
    }

    public bool CanUndoLastAction => _undoSnapshot is not null;

    public double TextSize
    {
        get => _textSize;
        private set
        {
            if (SetProperty(ref _textSize, value))
            {
                OnPropertyChanged(nameof(SecondaryTextSize));
                OnPropertyChanged(nameof(DetailHeaderTextSize));
                OnPropertyChanged(nameof(TitleTextSize));
                OnPropertyChanged(nameof(FilterTextSize));
                OnPropertyChanged(nameof(SegmentTextSize));
                OnPropertyChanged(nameof(SegmentHeight));
                OnPropertyChanged(nameof(SegmentPadding));
                OnPropertyChanged(nameof(InputHeight));
                OnPropertyChanged(nameof(InputButtonSize));
                OnPropertyChanged(nameof(InputActionIconSize));
                OnPropertyChanged(nameof(InputPadding));
                OnPropertyChanged(nameof(ItemTextLineHeight));
                OnPropertyChanged(nameof(SmallIconSize));
                OnPropertyChanged(nameof(StandardIconSize));
                OnPropertyChanged(nameof(PrimaryIconSize));
                OnPropertyChanged(nameof(CompletionIndicatorSize));
            }
        }
    }

    public double ContentTextSize
    {
        get => _contentTextSize;
        private set
        {
            if (SetProperty(ref _contentTextSize, value))
            {
                OnPropertyChanged(nameof(ContentSecondaryTextSize));
                OnPropertyChanged(nameof(ContentDetailHeaderTextSize));
                OnPropertyChanged(nameof(ContentTitleTextSize));
            }
        }
    }

    public double LayoutDensityScale
    {
        get => _layoutDensityScale;
        private set
        {
            if (SetProperty(ref _layoutDensityScale, NormalizeDensity(value)))
            {
                OnPropertyChanged(nameof(RootPadding));
                OnPropertyChanged(nameof(DetailPageMargin));
                OnPropertyChanged(nameof(RootRowSpacing));
                OnPropertyChanged(nameof(ItemMinHeight));
                OnPropertyChanged(nameof(ItemPadding));
                OnPropertyChanged(nameof(ItemMargin));
                OnPropertyChanged(nameof(ItemChromeMargin));
                OnPropertyChanged(nameof(ItemContentMargin));
                OnPropertyChanged(nameof(AddCardMinHeight));
                OnPropertyChanged(nameof(AddCardMargin));
                OnPropertyChanged(nameof(AddCardPadding));
                OnPropertyChanged(nameof(ItemTextLineHeight));
            }
        }
    }

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double DetailHeaderTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, TextSize + 1);

    public double ContentSecondaryTextSize => Math.Max(SettingsService.MinTextSize, ContentTextSize - 1);

    public double ContentDetailHeaderTextSize => Math.Max(SettingsService.MinTextSize, ContentTextSize - 1);

    public double ContentTitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, ContentTextSize + 1);

    public double FilterTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => WidgetSegmentedMetrics.Create(TextSize).TextSize;

    public double SegmentHeight => WidgetSegmentedMetrics.Create(TextSize).Height;

    public Thickness SegmentPadding => WidgetSegmentedMetrics.Create(TextSize).Padding;

    public double InputHeight => WidgetInputMetrics.Create(TextSize).Height;

    public double InputButtonSize => WidgetInputMetrics.Create(TextSize).ButtonSize;

    public double InputActionIconSize => WidgetInputMetrics.Create(TextSize).ActionIconSize;

    public Thickness InputPadding => WidgetInputMetrics.Create(TextSize).Padding;

    public Thickness RootPadding => UniformThickness(Lerp(6, 10, LayoutDensityScale));

    public Thickness DetailPageMargin => new(0, 6 - RootPadding.Top, 0, 0);

    public double RootRowSpacing => Math.Round(Lerp(5, 10, LayoutDensityScale));

    public double ItemMinHeight => Math.Round(Lerp(42, 56, LayoutDensityScale));

    public Thickness ItemPadding => new(
        Math.Round(Lerp(4, 11, LayoutDensityScale)),
        Math.Round(Lerp(3, 8, LayoutDensityScale)),
        Math.Round(Lerp(4, 11, LayoutDensityScale)),
        Math.Round(Lerp(3, 8, LayoutDensityScale)));

    public Thickness ItemMargin => new(0, 0, 10, Math.Round(Lerp(3, 8, LayoutDensityScale)));

    public Thickness ItemChromeMargin => new(
        -ItemPadding.Left,
        -ItemPadding.Top,
        -ItemPadding.Right,
        -ItemPadding.Bottom);

    public Thickness ItemContentMargin => new(0, Math.Round(Lerp(1, 3, LayoutDensityScale)), 0, Math.Round(Lerp(1, 3, LayoutDensityScale)));

    public double AddCardMinHeight => Math.Round(Lerp(42, 56, LayoutDensityScale));

    public Thickness AddCardMargin => new(0, 0, 10, Math.Round(Lerp(4, 9, LayoutDensityScale)));

    public Thickness AddCardPadding => new(
        Math.Round(Lerp(6, 12, LayoutDensityScale)),
        Math.Round(Lerp(4, 8, LayoutDensityScale)),
        Math.Round(Lerp(6, 12, LayoutDensityScale)),
        Math.Round(Lerp(4, 8, LayoutDensityScale)));

    public double ItemTextLineHeight => Math.Round(TextSize + Lerp(2.5, 6, LayoutDensityScale));

    public int ItemPreviewLineCount => _settingsService is null
        ? SettingsService.DefaultTodoItemPreviewLineCount
        : SettingsService.NormalizeItemPreviewLineCount(
            _settingsService.Settings.TodoItemPreviewLineCount);

    public string EditorEnterBehavior => _settingsService is null
        ? SettingsService.EditorEnterBehaviorCtrlEnterSaves
        : SettingsService.NormalizeEditorEnterBehavior(
            _settingsService.Settings.TodoEditorEnterBehavior);

    public double SmallIconSize => Math.Round(Math.Clamp(TextSize + 1, 11, 15));

    public double StandardIconSize => Math.Round(Math.Clamp(TextSize + 3, 14, 18));

    public double PrimaryIconSize => Math.Round(Math.Clamp(TextSize + 5, 16, 20));

    public double CompletionIndicatorSize => Math.Round(Math.Clamp(TextSize + 7, 18, 22));

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    internal void ReleaseAttachmentThumbnails(bool includeSelectedDetail = true)
    {
        TodoItemViewModel? activeDetail = includeSelectedDetail ? null : SelectedDetailItem;
        foreach (TodoItemViewModel item in Items)
        {
            if (ReferenceEquals(item, activeDetail))
            {
                continue;
            }

            item.ReleaseAttachmentThumbnails();
        }

        if (includeSelectedDetail)
        {
            SelectedDetailItem?.ReleaseAttachmentThumbnails();
        }
    }

    internal void EnsureSelectedDetailThumbnailsLoaded()
    {
        if (SelectedDetailItem is not { } detail)
        {
            return;
        }

        foreach (TodoAttachmentViewModel attachment in detail.Attachments)
        {
            _ = attachment.EnsureThumbnailAsync();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ReleaseAttachmentThumbnails();
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

}
