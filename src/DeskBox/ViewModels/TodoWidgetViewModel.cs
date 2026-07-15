using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public enum TodoFilter
{
    All,
    Active,
    Today,
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

internal sealed record TodoUndoSnapshot(
    IReadOnlyList<TodoItem> Items,
    string Message);

public sealed partial class TodoWidgetViewModel : ObservableObject, IDisposable
{
    private readonly TodoWidgetStore _store;
    private readonly LocalizationService _localizationService;
    private readonly WidgetConfig _config;
    private readonly SettingsService? _settingsService;
    private TodoFilter _selectedFilter = TodoFilter.All;
    private TodoColorFilter _selectedColorFilter = TodoColorFilter.All;
    private string _inputText = string.Empty;
    private double _textSize = SettingsService.DefaultTextSize;
    private string _newTaskPosition = SettingsService.TodoNewTaskPositionTop;
    private string _tabStyle = SettingsService.WidgetTabStyleButton;
    private bool _showCompletedTasks = true;
    private bool _showFooterStats = true;
    private bool _showClearCompletedButton = true;
    private bool _confirmBeforeDelete;
    private bool _draftImportant;
    private DateTimeOffset? _draftDueDate;
    private TodoItemViewModel? _selectedDetailItem;
    private bool _isCreatingDetailItem;
    private TodoUndoSnapshot? _undoSnapshot;
    private bool _isInitialized;
    private bool _isDisposed;
    private readonly HashSet<string> _expandedRecurringHistoryGroupKeys = new(StringComparer.Ordinal);

    public TodoWidgetViewModel(
        TodoWidgetStore store,
        LocalizationService localizationService,
        WidgetConfig config,
        SettingsService? settingsService = null)
    {
        _store = store;
        _localizationService = localizationService;
        _config = config;
        _settingsService = settingsService;
        if (_settingsService is not null)
        {
            _textSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
            ApplyTodoSettings(_settingsService.Settings, updateFilter: false);
        }

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Todo.Title")
        : _config.Name;

    public ObservableCollection<TodoItemViewModel> Items { get; } = [];

    public ObservableCollection<TodoItemViewModel> VisibleItems { get; } = [];

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                OnPropertyChanged(nameof(CanAddInput));
            }
        }
    }

    public bool CanAddInput => !string.IsNullOrWhiteSpace(InputText);

    public bool DraftImportant
    {
        get => _draftImportant;
        set
        {
            if (SetProperty(ref _draftImportant, value))
            {
                OnPropertyChanged(nameof(DraftImportantGlyph));
            }
        }
    }

    public DateTimeOffset? DraftDueDate
    {
        get => _draftDueDate;
        set
        {
            if (SetProperty(ref _draftDueDate, value))
            {
                OnPropertyChanged(nameof(DraftDueDateText));
                OnPropertyChanged(nameof(DraftDueDateActive));
            }
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
            var today = DateTimeOffset.Now.Date;
            if (local.Date == today)
            {
                return _localizationService.T("Todo.Due.Today");
            }

            if (local.Date == today.AddDays(1))
            {
                return _localizationService.T("Todo.Due.Tomorrow");
            }

            bool isEndOfDay = local.Hour == 23 && local.Minute == 59;
            return isEndOfDay
                ? $"{local.Month}/{local.Day}"
                : $"{local.Month}/{local.Day} {local:HH:mm}";
        }
    }

    public bool DraftDueDateActive => _draftDueDate is not null;

    public TodoItemViewModel? SelectedDetailItem
    {
        get => _selectedDetailItem;
        private set
        {
            if (SetProperty(ref _selectedDetailItem, value))
            {
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
                OnPropertyChanged(nameof(IsImportantFilterSelected));
                OnPropertyChanged(nameof(IsCompletedFilterSelected));
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
                RefreshColorFilterVisibilityProperties();
            }
        }
    }

    public int TotalCount => Items.Count;

    public int ActiveCount => Items.Count(item => !item.IsCompleted);

    public int CompletedCount => Items.Count(item => item.IsCompleted);

    public int AllFilterCount => ShowCompletedTasks ? TotalCount : ActiveCount;

    public int TodayFilterCount => Items.Count(item => IsDueToday(item) && ShouldCountInNonCompletedFilter(item));

    public int ImportantFilterCount => Items.Count(item => item.IsImportant && ShouldCountInNonCompletedFilter(item));

    public int RedColorFilterCount => Items.Count(item => item.HasRedMarker && ShouldCountInNonCompletedFilter(item));

    public int OrangeColorFilterCount => GetColorFilterCount(TodoItem.OrangeColorMarker);

    public int YellowColorFilterCount => GetColorFilterCount(TodoItem.YellowColorMarker);

    public int GreenColorFilterCount => GetColorFilterCount(TodoItem.GreenColorMarker);

    public int BlueColorFilterCount => GetColorFilterCount(TodoItem.BlueColorMarker);

    public int PurpleColorFilterCount => GetColorFilterCount(TodoItem.PurpleColorMarker);

    public int TealColorFilterCount => GetColorFilterCount(TodoItem.TealColorMarker);

    public int PinkColorFilterCount => GetColorFilterCount(TodoItem.PinkColorMarker);

    public int AnyColorFilterCount => Items.Count(item => item.HasColorMarker && ShouldCountInNonCompletedFilter(item));

    public bool HasCompletedItems => CompletedCount > 0;

    public bool HasItems => TotalCount > 0;

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public Visibility ListVisibility => HasVisibleItems ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => HasVisibleItems ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FooterStatsVisibility => ShowFooterStats ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ClearCompletedButtonVisibility => ShowClearCompletedButton ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FooterVisibility => ShowFooterStats || ShowClearCompletedButton ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UndoBarVisibility => CanUndoLastAction ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ColorFilterVisibility => Visibility.Visible;

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

    public string AllFilterText => FormatFilterText("Todo.Filter.All", AllFilterCount);

    public string ActiveFilterText => FormatFilterText("Todo.Filter.Active", ActiveCount);

    public string TodayFilterText => FormatFilterText("Todo.Filter.Today", TodayFilterCount);

    public string ImportantFilterText => FormatFilterText("Todo.Filter.Important", ImportantFilterCount);

    public string CompletedFilterText => FormatFilterText("Todo.Filter.Completed", CompletedCount);

    public string ListHeaderText => _localizationService.Format("Todo.List.Header", VisibleItems.Count);

    public string DetailBackText => _localizationService.T("Todo.Detail.Back");

    public string DetailAddFileText => _localizationService.T("Todo.Detail.AddFile");

    public string DetailRemoveAttachmentText => _localizationService.T("Todo.Detail.RemoveAttachment");

    public string DetailFileMissingText => _localizationService.T("Todo.Detail.FileMissing");

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
        !HasVisibleItems &&
        TotalCount > 0 &&
        ActiveCount == 0 &&
        SelectedFilter == TodoFilter.All &&
        SelectedColorFilter == TodoColorFilter.All;

    public string EmptyStateTitle => IsAllTasksCompletedEmptyState
        ? _localizationService.T("Todo.Empty.AllCompleted")
        : _localizationService.T("Todo.Empty.Title");

    public string EmptyStateText => SelectedFilter switch
    {
        _ when SelectedColorFilter != TodoColorFilter.All => _localizationService.T("Todo.Empty.Color"),
        TodoFilter.Active => _localizationService.T("Todo.Empty.Active"),
        TodoFilter.Today => _localizationService.T("Todo.Empty.Today"),
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

    public bool ConfirmBeforeDelete
    {
        get => _confirmBeforeDelete;
        private set => SetProperty(ref _confirmBeforeDelete, value);
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

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, TextSize + 1);

    public double FilterTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => WidgetSegmentedMetrics.Create(TextSize).TextSize;

    public double SegmentHeight => WidgetSegmentedMetrics.Create(TextSize).Height;

    public Thickness SegmentPadding => WidgetSegmentedMetrics.Create(TextSize).Padding;

    public double InputHeight => WidgetInputMetrics.Create(TextSize).Height;

    public double InputButtonSize => WidgetInputMetrics.Create(TextSize).ButtonSize;

    public double InputActionIconSize => WidgetInputMetrics.Create(TextSize).ActionIconSize;

    public Thickness InputPadding => WidgetInputMetrics.Create(TextSize).Padding;

    public double ItemTextLineHeight => Math.Round(TextSize + 4);

    public double SmallIconSize => Math.Round(Math.Clamp(TextSize + 1, 11, 15));

    public double StandardIconSize => Math.Round(Math.Clamp(TextSize + 3, 14, 18));

    public double PrimaryIconSize => Math.Round(Math.Clamp(TextSize + 5, 16, 20));

    public double CompletionIndicatorSize => Math.Round(Math.Clamp(TextSize + 7, 18, 22));

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public async Task InitializeAsync()
    {
        var data = await _store.LoadAsync();

        SelectedDetailItem = null;
        IsCreatingDetailItem = false;
        Items.Clear();
        foreach (var item in data.Items.OrderBy(item => item.SortOrder).ThenByDescending(item => item.UpdatedAt))
        {
            Items.Add(new TodoItemViewModel(item, _localizationService));
        }

        NormalizeSortOrders();
        ApplySettings(updateFilter: true);
        RefreshVisibleItems();
        RefreshCountProperties();
        IsInitialized = true;
    }

    public async Task<TodoItemViewModel?> AddInputAsync()
    {
        var item = await AddItemAsync(InputText, _draftImportant, _draftDueDate);
        if (item is not null)
        {
            InputText = string.Empty;
            DraftImportant = false;
            DraftDueDate = null;
        }

        return item;
    }

    public async Task<TodoItemViewModel?> AddItemAsync(string? text, bool isImportant = false, DateTimeOffset? dueDate = null)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalizedText,
            IsImportant = isImportant,
            DueDate = NormalizeDueDate(dueDate),
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService);

        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(viewModel);
        }
        else
        {
            Items.Insert(0, viewModel);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return viewModel;
    }

    public async Task<bool> UpdateItemTextAsync(string itemId, string? text)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        if (string.Equals(item.Text, normalizedText, StringComparison.Ordinal))
        {
            return true;
        }

        item.Text = normalizedText;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetCompletedAsync(string itemId, bool isCompleted)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (item.IsCompleted == isCompleted)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (isCompleted)
        {
            if (item.Recurrence is not null)
            {
                item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
            }

            item.IsCompleted = true;
            item.CompletedAt = now;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.Item.GeneratedNextItemId = null;

            if (TodoRecurrenceService.TryCreateNextOccurrence(item.Item, now, out TodoItem? nextItem) &&
                nextItem is not null)
            {
                item.Item.GeneratedNextItemId = nextItem.Id;
                int currentIndex = Items.IndexOf(item);
                var nextViewModel = new TodoItemViewModel(nextItem, _localizationService);
                Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), nextViewModel);
            }
        }
        else
        {
            TodoItemViewModel? generatedOccurrence = FindGeneratedOccurrence(item);
            if (generatedOccurrence is not null &&
                TodoRecurrenceService.ShouldRemoveGeneratedOccurrence(item.Item, generatedOccurrence.Item))
            {
                Items.Remove(generatedOccurrence);
            }

            item.IsCompleted = false;
            item.CompletedAt = null;
            item.Item.GeneratedNextItemId = null;
        }

        item.UpdatedAt = now;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetImportantAsync(string itemId, bool isImportant)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (item.IsImportant == isImportant)
        {
            return true;
        }

        item.IsImportant = isImportant;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetColorMarkerAsync(string itemId, string? colorMarker)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        if (string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal))
        {
            return true;
        }

        item.ColorMarker = normalizedColorMarker;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetColorMarkerAsync(IEnumerable<string> itemIds, string? colorMarker)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            if (string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal))
            {
                continue;
            }

            item.ColorMarker = normalizedColorMarker;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetDueDateAsync(string itemId, DateTimeOffset? dueDate)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate);
        TodoRecurrence? updatedRecurrence = normalizedDueDate is null
            ? null
            : item.Recurrence?.Clone();
        if (updatedRecurrence is not null)
        {
            updatedRecurrence.AnchorDueDate = normalizedDueDate;
        }

        if (Nullable.Equals(item.DueDate, normalizedDueDate) &&
            AreRecurrenceEqual(item.Recurrence, updatedRecurrence) &&
            (normalizedDueDate is not null || item.ReminderOffsetMinutes is null))
        {
            return true;
        }

        item.DueDate = normalizedDueDate;
        item.Recurrence = updatedRecurrence;
        if (normalizedDueDate is null)
        {
            item.ReminderOffsetMinutes = null;
        }
        if (updatedRecurrence is null)
        {
            item.RecurrenceSeriesId = null;
            item.Item.GeneratedNextItemId = null;
        }
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetDueDateAsync(IEnumerable<string> itemIds, DateTimeOffset? dueDate)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            TodoRecurrence? updatedRecurrence = normalizedDueDate is null
                ? null
                : item.Recurrence?.Clone();
            if (updatedRecurrence is not null)
            {
                updatedRecurrence.AnchorDueDate = normalizedDueDate;
            }

            if (Nullable.Equals(item.DueDate, normalizedDueDate) &&
                AreRecurrenceEqual(item.Recurrence, updatedRecurrence) &&
                (normalizedDueDate is not null || item.ReminderOffsetMinutes is null))
            {
                continue;
            }

            item.DueDate = normalizedDueDate;
            item.Recurrence = updatedRecurrence;
            if (normalizedDueDate is null)
            {
                item.ReminderOffsetMinutes = null;
            }
            if (updatedRecurrence is null)
            {
                item.RecurrenceSeriesId = null;
                item.Item.GeneratedNextItemId = null;
            }
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetRecurrenceAsync(string itemId, string? mode)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, item.DueDate);
        if (AreRecurrenceEqual(item.Recurrence, recurrence))
        {
            return true;
        }

        if (item.DueDate is null && recurrence is not null)
        {
            return false;
        }

        item.Recurrence = recurrence;
        item.RecurrenceSeriesId = recurrence is null
            ? null
            : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
        item.Item.GeneratedNextItemId = null;
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetRecurrenceAsync(IEnumerable<string> itemIds, string? mode)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, item.DueDate);
            if (item.DueDate is null && recurrence is not null)
            {
                continue;
            }

            if (AreRecurrenceEqual(item.Recurrence, recurrence))
            {
                continue;
            }

            item.Recurrence = recurrence;
            item.RecurrenceSeriesId = recurrence is null
                ? null
                : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
            item.Item.GeneratedNextItemId = null;
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetReminderOffsetAsync(string itemId, int? offsetMinutes)
    {
        var item = FindItem(itemId);
        if (item is null || item.DueDate is null)
        {
            return false;
        }

        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        if (Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset))
        {
            return true;
        }

        item.ReminderOffsetMinutes = normalizedOffset;
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        if (TodoReminderOptions.IsReminderOff(normalizedOffset))
        {
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetReminderOffsetAsync(IEnumerable<string> itemIds, int? offsetMinutes)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id) && item.DueDate is not null))
        {
            if (Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset))
            {
                continue;
            }

            item.ReminderOffsetMinutes = normalizedOffset;
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            if (TodoReminderOptions.IsReminderOff(normalizedOffset))
            {
                item.SnoozedUntil = null;
                item.Item.SnoozeLastNotifiedAt = null;
            }

            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SnoozeReminderAsync(string itemId, TimeSpan snoozeFor)
    {
        var item = FindItem(itemId);
        if (item is null ||
            item.IsCompleted ||
            item.DueDate is null ||
            snoozeFor <= TimeSpan.Zero)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        item.SnoozedUntil = now.Add(snoozeFor);
        item.Item.SnoozeLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = item.DueDate;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SnoozeReminderUntilAsync(string itemId, DateTimeOffset snoozedUntil)
    {
        var item = FindItem(itemId);
        if (item is null ||
            item.IsCompleted ||
            item.DueDate is null ||
            snoozedUntil <= DateTimeOffset.Now)
        {
            return false;
        }

        item.SnoozedUntil = snoozedUntil;
        item.Item.SnoozeLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = item.DueDate;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public void ToggleRecurringHistoryGroup(string? groupKey)
    {
        string? normalizedGroupKey = NormalizeRecurringHistoryGroupKey(groupKey);
        if (normalizedGroupKey is null)
        {
            return;
        }

        if (!_expandedRecurringHistoryGroupKeys.Add(normalizedGroupKey))
        {
            _expandedRecurringHistoryGroupKeys.Remove(normalizedGroupKey);
        }

        RefreshVisibleItems();
        RefreshCountProperties();
    }

    public async Task<bool> SetDueDatePresetAsync(string itemId, TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DateTimeOffset? dueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };

        return await SetDueDateAsync(itemId, dueDate);
    }

    public Task<int> SetDueDatePresetAsync(IEnumerable<string> itemIds, TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DateTimeOffset? dueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };

        return SetDueDateAsync(itemIds, dueDate);
    }

    public void BeginEdit(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return;
        }

        foreach (var other in Items.Where(other => !string.Equals(other.Id, itemId, StringComparison.Ordinal)))
        {
            other.CancelEdit();
        }

        item.BeginEdit();
    }

    public void CancelEdit(string itemId)
    {
        FindItem(itemId)?.CancelEdit();
    }

    public async Task<bool> CommitEditAsync(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        bool updated = await UpdateItemTextAsync(itemId, item.EditText);
        if (updated)
        {
            item.IsEditing = false;
        }

        return updated;
    }

    public async Task<bool> DeleteItemAsync(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (IsCreatingDetailItem &&
            string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal))
        {
            item.CancelEdit();
            SelectedDetailItem = null;
            IsCreatingDetailItem = false;
            return true;
        }

        CaptureUndoSnapshot(_localizationService.T("Todo.Undo.Deleted"));
        if (string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal))
        {
            SelectedDetailItem = null;
        }
        Items.Remove(item);
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> DeleteItemsAsync(IEnumerable<string> itemIds)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var itemsToDelete = Items
            .Where(item => selectedIds.Contains(item.Id))
            .ToList();
        if (itemsToDelete.Count == 0)
        {
            return 0;
        }

        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.DeletedSelected", itemsToDelete.Count));
        if (SelectedDetailItem is not null && selectedIds.Contains(SelectedDetailItem.Id))
        {
            SelectedDetailItem = null;
        }

        foreach (var item in itemsToDelete)
        {
            Items.Remove(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return itemsToDelete.Count;
    }

    public async Task<int> ClearCompletedAsync()
    {
        var completedItems = Items.Where(item => item.IsCompleted).ToList();
        if (completedItems.Count == 0)
        {
            return 0;
        }

        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.ClearedCompleted", completedItems.Count));
        foreach (var item in completedItems)
        {
            Items.Remove(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return completedItems.Count;
    }

    public async Task<int> ClearAllAsync()
    {
        if (Items.Count == 0)
        {
            return 0;
        }

        int removedCount = Items.Count;
        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.ClearedAll", removedCount));
        Items.Clear();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return removedCount;
    }

    public async Task<bool> MoveItemAsync(string itemId, int targetVisibleIndex)
    {
        var item = FindItem(itemId);
        if (item is null || targetVisibleIndex < 0)
        {
            return false;
        }

        int currentIndex = Items.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        int targetIndex = GetUnderlyingInsertIndex(targetVisibleIndex, item);
        if (targetIndex == currentIndex)
        {
            return true;
        }

        Items.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Items.Count);
        Items.Insert(targetIndex, item);
        NormalizeSortOrders();
        RefreshVisibleItems();
        await SaveAsync();
        return true;
    }

    public async Task<bool> UndoLastActionAsync()
    {
        if (_undoSnapshot is null)
        {
            return false;
        }

        Items.Clear();
        foreach (var item in _undoSnapshot.Items.Select(CloneTodoItem))
        {
            Items.Add(new TodoItemViewModel(item, _localizationService));
        }

        _undoSnapshot = null;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        return true;
    }

    public void DismissUndo()
    {
        if (_undoSnapshot is null)
        {
            return;
        }

        _undoSnapshot = null;
        RefreshUndoProperties();
    }

    public void SetFilter(TodoFilter filter)
    {
        SelectedFilter = filter;
    }

    public void SetDraftDueDatePreset(TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DraftDueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };
    }

    public void SetColorFilter(TodoColorFilter filter)
    {
        SelectedColorFilter = filter;
    }

    public TodoItemViewModel? OpenDetail(string itemId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        foreach (TodoItemViewModel entry in Items)
        {
            entry.CancelEdit();
        }

        item.BeginEdit();
        IsCreatingDetailItem = false;
        SelectedDetailItem = item;
        return item;
    }

    public TodoItemViewModel OpenNewDetail()
    {
        CloseDetail();
        foreach (TodoItemViewModel entry in Items)
        {
            entry.CancelEdit();
        }

        var now = DateTimeOffset.UtcNow;
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom
                ? Items.Count
                : 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService);
        viewModel.BeginEdit();
        IsCreatingDetailItem = true;
        SelectedDetailItem = viewModel;
        return viewModel;
    }

    public async Task<TodoItemViewModel?> FinalizeDetailAsync(string? title)
    {
        TodoItemViewModel? item = SelectedDetailItem;
        if (item is null)
        {
            return null;
        }

        string normalizedTitle = NormalizeText(title);
        if (!IsCreatingDetailItem)
        {
            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                await UpdateItemTextAsync(item.Id, normalizedTitle);
            }

            item.CancelEdit();
            SelectedDetailItem = null;
            return item;
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            item.CancelEdit();
            SelectedDetailItem = null;
            IsCreatingDetailItem = false;
            return null;
        }

        item.Text = normalizedTitle;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.CancelEdit();
        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(item);
        }
        else
        {
            Items.Insert(0, item);
        }

        IsCreatingDetailItem = false;
        SelectedDetailItem = null;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return item;
    }

    public void CloseDetail()
    {
        SelectedDetailItem?.CancelEdit();
        SelectedDetailItem = null;
        IsCreatingDetailItem = false;
    }

    public async Task<TodoStepViewModel?> AddStepAsync(string itemId, string? text)
    {
        TodoItemViewModel? item = FindItem(itemId);
        string normalizedText = NormalizeText(text);
        if (item is null || string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var step = new TodoStep
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalizedText,
            SortOrder = item.Steps.Count
        };
        var stepViewModel = new TodoStepViewModel(step);
        item.Item.Steps.Add(step);
        item.Steps.Add(stepViewModel);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return stepViewModel;
    }

    public async Task<bool> SetStepCompletedAsync(string itemId, string stepId, bool isCompleted)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        if (item is null || step is null)
        {
            return false;
        }

        step.IsCompleted = isCompleted;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> UpdateStepTextAsync(string itemId, string stepId, string? text)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        string normalizedText = NormalizeText(text);
        if (item is null || step is null || string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        step.Text = normalizedText;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync();
        return true;
    }

    public async Task<bool> DeleteStepAsync(string itemId, string stepId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        if (item is null || step is null)
        {
            return false;
        }

        item.Steps.Remove(step);
        item.Item.Steps.RemoveAll(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        for (int index = 0; index < item.Steps.Count; index++)
        {
            item.Steps[index].SortOrder = index;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> UpdateNotesAsync(string itemId, string? notes)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string? normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (string.Equals(item.Item.Notes, normalizedNotes, StringComparison.Ordinal))
        {
            return true;
        }

        item.Item.Notes = normalizedNotes;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentAsync(
        string itemId,
        string filePath,
        string displayName,
        string type)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var attachment = new TodoAttachment
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = filePath.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(filePath) : displayName.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? "file" : type.Trim(),
            StorageMode = TodoAttachment.LinkedStorageMode,
            AddedAt = DateTimeOffset.UtcNow
        };
        return await AddAttachmentAsync(item, attachment);
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentPathAsync(
        string itemId,
        string filePath,
        bool? copyToManagedStorageOverride = null)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        bool copyToManagedStorage = copyToManagedStorageOverride ??
            (_settingsService is not null &&
             SettingsService.NormalizeAttachmentStorageMode(_settingsService.Settings.AttachmentStorageMode) ==
             SettingsService.AttachmentStorageModeCopy);
        TodoAttachment? attachment = await AttachmentStorageService.ImportPathAsync(
            filePath,
            GetManagedAttachmentDirectory(itemId),
            copyToManagedStorage);
        return attachment is null ? null : await AddAttachmentAsync(item, attachment);
    }

    public async Task<int> AddDroppedAttachmentsAsync(
        string itemId,
        IReadOnlyList<DroppedFilePath> droppedFiles)
    {
        int addedCount = 0;
        foreach (DroppedFilePath droppedFile in droppedFiles)
        {
            TodoAttachmentViewModel? attachment = await AddAttachmentPathAsync(
                itemId,
                droppedFile.Path,
                droppedFile.ForceManagedCopy ? true : null);
            if (attachment is not null)
            {
                addedCount++;
            }
        }

        return addedCount;
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentStreamAsync(
        string itemId,
        Stream stream,
        string? fileName)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        TodoAttachment? attachment = await AttachmentStorageService.SaveStreamAsync(
            stream,
            fileName,
            GetManagedAttachmentDirectory(itemId));
        return attachment is null ? null : await AddAttachmentAsync(item, attachment);
    }

    private async Task<TodoAttachmentViewModel?> AddAttachmentAsync(
        TodoItemViewModel item,
        TodoAttachment attachment)
    {
        if (item.Attachments.Any(existing =>
                string.Equals(existing.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return item.Attachments.First(existing =>
                string.Equals(existing.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
        }

        var attachmentViewModel = new TodoAttachmentViewModel(attachment);
        item.Item.Attachments.Add(attachment);
        item.Attachments.Add(attachmentViewModel);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return attachmentViewModel;
    }

    public async Task<bool> DeleteAttachmentAsync(string itemId, string attachmentId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoAttachmentViewModel? attachment = item?.Attachments.FirstOrDefault(entry =>
            string.Equals(entry.Id, attachmentId, StringComparison.Ordinal));
        if (item is null || attachment is null)
        {
            return false;
        }

        item.Attachments.Remove(attachment);
        item.Item.Attachments.RemoveAll(entry => string.Equals(entry.Id, attachmentId, StringComparison.Ordinal));
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        if (attachment.Attachment.IsManagedCopy && File.Exists(attachment.FilePath))
        {
            try
            {
                File.Delete(attachment.FilePath);
            }
            catch (Exception ex)
            {
                App.Log($"[Todo] Failed to delete managed attachment '{attachment.FilePath}': {ex.Message}");
            }
        }
        return true;
    }

    private string GetManagedAttachmentDirectory(string itemId)
    {
        return Path.Combine(_store.AttachmentDirectory, itemId);
    }

    public void ToggleExpanded(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return;
        }

        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            return;
        }

        foreach (var other in Items)
        {
            if (!string.Equals(other.Id, itemId, StringComparison.Ordinal))
            {
                other.IsExpanded = false;
                other.CancelEdit();
            }
        }

        item.IsExpanded = true;
    }

    public void CollapseAllExpanded()
    {
        foreach (var item in Items)
        {
            if (item.IsExpanded)
            {
                item.CancelEdit();
                item.IsExpanded = false;
            }
        }
    }

    public TodoItemViewModel? FocusReminderItem(string? itemId, bool preferTodayFilter)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        SelectedColorFilter = TodoColorFilter.All;
        SelectedFilter = preferTodayFilter && IsDueToday(item)
            ? TodoFilter.Today
            : TodoFilter.All;

        if (!VisibleItems.Contains(item))
        {
            SelectedFilter = TodoFilter.All;
        }

        foreach (var visibleItem in Items)
        {
            visibleItem.IsCopySelected = false;
        }

        item.IsCopySelected = true;
        return item;
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplySettings(updateFilter: false);
    }

    public void ApplySettings(bool updateFilter)
    {
        if (_settingsService is null)
        {
            return;
        }

        ApplyTodoSettings(_settingsService.Settings, updateFilter);
    }

    private TodoItemViewModel? FindItem(string itemId)
    {
        TodoItemViewModel? item = Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is not null)
        {
            return item;
        }

        return IsCreatingDetailItem &&
               string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal)
            ? SelectedDetailItem
            : null;
    }

    private TodoItemViewModel? FindGeneratedOccurrence(TodoItemViewModel item)
    {
        return string.IsNullOrWhiteSpace(item.Item.GeneratedNextItemId)
            ? null
            : FindItem(item.Item.GeneratedNextItemId);
    }

    private async Task SaveAsync()
    {
        NormalizeSortOrders();
        await _store.SaveAsync(new TodoWidgetData
        {
            Items = Items.Select(item => item.Item).ToList()
        });
    }

    private void RefreshVisibleItems()
    {
        VisibleItems.Clear();
        ResetRecurringHistoryState();

        var filteredItems = Items.Where(ShouldShowItem).ToList();
        filteredItems.Sort(CompareVisibleItems);

        var activeItems = filteredItems
            .Where(item => !item.IsCompleted)
            .ToList();
        var completedItems = filteredItems
            .Where(item => item.IsCompleted)
            .ToList();

        foreach (var item in activeItems)
        {
            VisibleItems.Add(item);
        }

        foreach (var item in BuildCompletedVisibleItems(completedItems))
        {
            VisibleItems.Add(item);
        }

        RefreshVisibleStateProperties();
    }

    private void ResetRecurringHistoryState()
    {
        foreach (var item in Items)
        {
            item.UpdateRecurringHistoryState(isLead: false, isExpanded: false, itemCount: 0);
        }
    }

    private List<TodoItemViewModel> BuildCompletedVisibleItems(IReadOnlyList<TodoItemViewModel> completedItems)
    {
        if (completedItems.Count == 0)
        {
            return [];
        }

        var groupBuckets = completedItems
            .Where(IsRecurringHistoryCandidate)
            .GroupBy(GetRecurringHistoryGroupKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key!,
                group => group
                    .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
                    .ThenByDescending(item => item.UpdatedAt)
                    .ToList(),
                StringComparer.Ordinal);

        var usedGroupKeys = new HashSet<string>(StringComparer.Ordinal);
        var completedDisplayItems = new List<TodoItemViewModel>();

        foreach (var item in completedItems)
        {
            string? groupKey = GetRecurringHistoryGroupKey(item);
            if (groupKey is null ||
                !groupBuckets.TryGetValue(groupKey, out List<TodoItemViewModel>? groupItems) ||
                groupItems.Count <= 1)
            {
                completedDisplayItems.Add(item);
                continue;
            }

            if (!usedGroupKeys.Add(groupKey))
            {
                continue;
            }

            bool isExpanded = _expandedRecurringHistoryGroupKeys.Contains(groupKey);
            groupItems[0].UpdateRecurringHistoryState(
                isLead: true,
                isExpanded: isExpanded,
                itemCount: groupItems.Count);
            completedDisplayItems.Add(groupItems[0]);

            if (isExpanded)
            {
                for (int index = 1; index < groupItems.Count; index++)
                {
                    completedDisplayItems.Add(groupItems[index]);
                }
            }
        }

        PruneRecurringHistoryExpansionState(
            groupBuckets
                .Where(entry => entry.Value.Count > 1)
                .Select(entry => entry.Key));
        return completedDisplayItems;
    }

    private bool ShouldShowItem(TodoItemViewModel item)
    {
        string? selectedColorMarker = MapColorFilterToMarker(SelectedColorFilter);
        if (selectedColorMarker is not null &&
            !string.Equals(item.ColorMarker, selectedColorMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ShowCompletedTasks &&
            item.IsCompleted &&
            SelectedFilter != TodoFilter.Completed)
        {
            return false;
        }

        return SelectedFilter switch
        {
            TodoFilter.Active => !item.IsCompleted,
            TodoFilter.Today => IsDueToday(item),
            TodoFilter.Important => item.IsImportant,
            TodoFilter.Completed => item.IsCompleted,
            _ => true
        };
    }

    private void NormalizeSortOrders()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].SortOrder = index;
        }
    }

    private void RefreshCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(AllFilterCount));
        OnPropertyChanged(nameof(TodayFilterCount));
        OnPropertyChanged(nameof(ImportantFilterCount));
        OnPropertyChanged(nameof(RedColorFilterCount));
        OnPropertyChanged(nameof(OrangeColorFilterCount));
        OnPropertyChanged(nameof(YellowColorFilterCount));
        OnPropertyChanged(nameof(GreenColorFilterCount));
        OnPropertyChanged(nameof(BlueColorFilterCount));
        OnPropertyChanged(nameof(PurpleColorFilterCount));
        OnPropertyChanged(nameof(TealColorFilterCount));
        OnPropertyChanged(nameof(PinkColorFilterCount));
        OnPropertyChanged(nameof(AnyColorFilterCount));
        OnPropertyChanged(nameof(HasCompletedItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ItemsLeftText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(TodayFilterText));
        OnPropertyChanged(nameof(ImportantFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
        OnPropertyChanged(nameof(ColorFilterVisibility));
        RefreshColorFilterVisibilityProperties();
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(IsAllTasksCompletedEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ListHeaderText));
    }

    private void RefreshVisibleStateProperties()
    {
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ColorFilterVisibility));
        RefreshColorFilterVisibilityProperties();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AddPlaceholderText));
        OnPropertyChanged(nameof(ExpandInputTooltipText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(TodayFilterText));
        OnPropertyChanged(nameof(ImportantFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ClearCompletedText));
        OnPropertyChanged(nameof(ItemsLeftText));
        OnPropertyChanged(nameof(UndoActionText));
        OnPropertyChanged(nameof(ListHeaderText));
        OnPropertyChanged(nameof(DetailBackText));
        OnPropertyChanged(nameof(DetailAddFileText));
        OnPropertyChanged(nameof(DetailRemoveAttachmentText));
        OnPropertyChanged(nameof(DetailFileMissingText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ColorMarkerText));
        OnPropertyChanged(nameof(RedColorText));
        OnPropertyChanged(nameof(OrangeColorText));
        OnPropertyChanged(nameof(YellowColorText));
        OnPropertyChanged(nameof(GreenColorText));
        OnPropertyChanged(nameof(BlueColorText));
        OnPropertyChanged(nameof(PurpleColorText));
        OnPropertyChanged(nameof(TealColorText));
        OnPropertyChanged(nameof(PinkColorText));
        foreach (var item in Items)
        {
            item.RefreshLocalizedText();
        }
    }

    private void ApplyTodoSettings(AppSettings settings, bool updateFilter)
    {
        string normalizedNewTaskPosition = NormalizeNewTaskPosition(settings.TodoNewTaskPosition);
        bool newTaskPositionChanged = !string.Equals(
            NewTaskPosition,
            normalizedNewTaskPosition,
            StringComparison.Ordinal);

        NewTaskPosition = normalizedNewTaskPosition;
        TabStyle = settings.TodoTabStyle;
        ShowCompletedTasks = settings.TodoShowCompletedTasks;
        ShowFooterStats = settings.TodoShowFooterStats;
        ShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        ConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;

        bool filterChanged = false;
        if (updateFilter)
        {
            TodoFilter defaultFilter = MapDefaultFilter(settings.TodoDefaultFilter);
            filterChanged = SelectedFilter != defaultFilter;
            SelectedFilter = defaultFilter;
        }

        if (newTaskPositionChanged && !filterChanged)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
        }
    }

    private static TodoFilter MapDefaultFilter(string? defaultFilter)
    {
        return defaultFilter switch
        {
            SettingsService.TodoDefaultFilterToday => TodoFilter.Today,
            SettingsService.TodoDefaultFilterImportant => TodoFilter.Important,
            SettingsService.TodoDefaultFilterCompleted => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static string NormalizeNewTaskPosition(string? value)
    {
        return value == SettingsService.TodoNewTaskPositionBottom
            ? SettingsService.TodoNewTaskPositionBottom
            : SettingsService.TodoNewTaskPositionTop;
    }

    private static DateTimeOffset? NormalizeDueDate(DateTimeOffset? dueDate)
    {
        if (dueDate is not { } value)
        {
            return null;
        }

        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Offset);
    }

    private static bool IsDueToday(TodoItemViewModel item)
    {
        return item.DueDate is { } dueDate &&
               dueDate.ToLocalTime().Date == DateTimeOffset.Now.Date;
    }

    private static DateTimeOffset WithEndOfDay(DateTimeOffset date)
    {
        return new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            23,
            59,
            0,
            date.Offset);
    }

    private static int CompareVisibleItems(TodoItemViewModel? left, TodoItemViewModel? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        if (left.IsCompleted != right.IsCompleted)
        {
            return left.IsCompleted ? 1 : -1;
        }

        int sortCompare = left.SortOrder.CompareTo(right.SortOrder);
        return sortCompare != 0
            ? sortCompare
            : right.UpdatedAt.CompareTo(left.UpdatedAt);
    }

    private bool ShouldCountInNonCompletedFilter(TodoItemViewModel item)
    {
        return ShowCompletedTasks || !item.IsCompleted;
    }

    private int GetColorFilterCount(string colorMarker)
    {
        return Items.Count(item =>
            string.Equals(item.ColorMarker, colorMarker, StringComparison.Ordinal) &&
            ShouldCountInNonCompletedFilter(item));
    }

    private void RefreshColorFilterVisibilityProperties()
    {
        OnPropertyChanged(nameof(RedColorFilterVisibility));
        OnPropertyChanged(nameof(OrangeColorFilterVisibility));
        OnPropertyChanged(nameof(YellowColorFilterVisibility));
        OnPropertyChanged(nameof(GreenColorFilterVisibility));
        OnPropertyChanged(nameof(BlueColorFilterVisibility));
        OnPropertyChanged(nameof(PurpleColorFilterVisibility));
        OnPropertyChanged(nameof(TealColorFilterVisibility));
        OnPropertyChanged(nameof(PinkColorFilterVisibility));
    }

    private static string? MapColorFilterToMarker(TodoColorFilter filter)
    {
        return filter switch
        {
            TodoColorFilter.Red => TodoItem.RedColorMarker,
            TodoColorFilter.Orange => TodoItem.OrangeColorMarker,
            TodoColorFilter.Yellow => TodoItem.YellowColorMarker,
            TodoColorFilter.Green => TodoItem.GreenColorMarker,
            TodoColorFilter.Blue => TodoItem.BlueColorMarker,
            TodoColorFilter.Purple => TodoItem.PurpleColorMarker,
            TodoColorFilter.Teal => TodoItem.TealColorMarker,
            TodoColorFilter.Pink => TodoItem.PinkColorMarker,
            _ => null
        };
    }

    private static bool IsRecurringHistoryCandidate(TodoItemViewModel item)
    {
        return item.IsCompleted &&
               item.Recurrence is not null &&
               GetRecurringHistoryGroupKey(item) is not null;
    }

    private static string? GetRecurringHistoryGroupKey(TodoItemViewModel item)
    {
        if (item.Recurrence is null)
        {
            return null;
        }

        string? seriesId = TodoRecurrenceService.NormalizeSeriesId(item.RecurrenceSeriesId);
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            return seriesId;
        }

        if (item.DueDate is not { } dueDate)
        {
            return null;
        }

        string recurrenceMode = TodoRecurrenceMode.Normalize(item.Recurrence.Mode);
        long anchorTicks = (item.Recurrence.AnchorDueDate ?? dueDate).UtcTicks;
        return $"{recurrenceMode}|{anchorTicks}|{item.Text}";
    }

    private static string? NormalizeRecurringHistoryGroupKey(string? groupKey)
    {
        return string.IsNullOrWhiteSpace(groupKey)
            ? null
            : groupKey.Trim();
    }

    private void PruneRecurringHistoryExpansionState(IEnumerable<string> activeGroupKeys)
    {
        var activeKeys = activeGroupKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        _expandedRecurringHistoryGroupKeys.RemoveWhere(groupKey => !activeKeys.Contains(groupKey));
    }

    private string FormatFilterText(string key, int count)
    {
        return $"{_localizationService.T(key)} {count}";
    }

    private int GetUnderlyingInsertIndex(int targetVisibleIndex, TodoItemViewModel movingItem)
    {
        var visibleWithoutMoving = VisibleItems
            .Where(item => !string.Equals(item.Id, movingItem.Id, StringComparison.Ordinal))
            .ToList();
        targetVisibleIndex = Math.Clamp(targetVisibleIndex, 0, visibleWithoutMoving.Count);
        if (targetVisibleIndex >= visibleWithoutMoving.Count)
        {
            return Items.Count;
        }

        return Math.Max(0, Items.IndexOf(visibleWithoutMoving[targetVisibleIndex]));
    }

    private void CaptureUndoSnapshot(string message)
    {
        _undoSnapshot = new TodoUndoSnapshot(
            Items.Select(item => CloneTodoItem(item.Item)).ToList(),
            message);
        RefreshUndoProperties();
    }

    private void RefreshUndoProperties()
    {
        OnPropertyChanged(nameof(CanUndoLastAction));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(UndoBarVisibility));
    }

    private static TodoItem CloneTodoItem(TodoItem item)
    {
        return new TodoItem
        {
            Id = item.Id,
            Text = item.Text,
            IsCompleted = item.IsCompleted,
            IsImportant = item.IsImportant,
            ColorMarker = item.ColorMarker,
            DueDate = item.DueDate,
            Recurrence = item.Recurrence?.Clone(),
            Steps = item.Steps.Select(step => new TodoStep
            {
                Id = step.Id,
                Text = step.Text,
                IsCompleted = step.IsCompleted,
                SortOrder = step.SortOrder
            }).ToList(),
            Notes = item.Notes,
            Attachments = item.Attachments.Select(attachment => new TodoAttachment
            {
                Id = attachment.Id,
                FilePath = attachment.FilePath,
                DisplayName = attachment.DisplayName,
                Type = attachment.Type,
                StorageMode = attachment.StorageMode,
                AddedAt = attachment.AddedAt
            }).ToList(),
            CompletedAt = item.CompletedAt,
            ReminderLastNotifiedAt = item.ReminderLastNotifiedAt,
            ReminderDismissedForDueDate = item.ReminderDismissedForDueDate,
            ReminderOffsetMinutes = item.ReminderOffsetMinutes,
            SnoozedUntil = item.SnoozedUntil,
            SnoozeLastNotifiedAt = item.SnoozeLastNotifiedAt,
            RecurrenceSeriesId = item.RecurrenceSeriesId,
            GeneratedNextItemId = item.GeneratedNextItemId,
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static bool AreRecurrenceEqual(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        return string.Equals(leftMode, rightMode, StringComparison.Ordinal) &&
               Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate);
    }

    private static string NormalizeText(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

}
