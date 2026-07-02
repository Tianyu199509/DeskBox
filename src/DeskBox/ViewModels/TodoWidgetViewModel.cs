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
    Purple
}

public enum TodoDuePreset
{
    Today,
    Tomorrow,
    ThisWeek,
    Clear
}

internal sealed record TodoUndoSnapshot(
    IReadOnlyList<TodoItem> Items,
    string Message);

public sealed partial class TodoWidgetViewModel : ObservableObject
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
    private bool _showCompletedTasks = true;
    private bool _showFooterStats = true;
    private bool _showClearCompletedButton = true;
    private bool _confirmBeforeDelete;
    private TodoUndoSnapshot? _undoSnapshot;
    private bool _isInitialized;

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

    public TodoFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                RefreshVisibleItems();
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
                OnPropertyChanged(nameof(EmptyStateText));
                OnPropertyChanged(nameof(IsRedColorFilterSelected));
                OnPropertyChanged(nameof(IsOrangeColorFilterSelected));
                OnPropertyChanged(nameof(IsYellowColorFilterSelected));
                OnPropertyChanged(nameof(IsGreenColorFilterSelected));
                OnPropertyChanged(nameof(IsBlueColorFilterSelected));
                OnPropertyChanged(nameof(IsPurpleColorFilterSelected));
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

    public string AddPlaceholderText => _localizationService.T("Todo.AddPlaceholder");

    public string ExpandInputTooltipText => _localizationService.T("QuickCapture.ExpandInput");

    public string AllFilterText => FormatFilterText("Todo.Filter.All", AllFilterCount);

    public string ActiveFilterText => FormatFilterText("Todo.Filter.Active", ActiveCount);

    public string TodayFilterText => FormatFilterText("Todo.Filter.Today", TodayFilterCount);

    public string ImportantFilterText => FormatFilterText("Todo.Filter.Important", ImportantFilterCount);

    public string CompletedFilterText => FormatFilterText("Todo.Filter.Completed", CompletedCount);

    public string EmptyStateTitle => _localizationService.T("Todo.Empty.Title");

    public string EmptyStateText => SelectedFilter switch
    {
        _ when SelectedColorFilter != TodoColorFilter.All => _localizationService.T("Todo.Empty.Color"),
        TodoFilter.Active => _localizationService.T("Todo.Empty.Active"),
        TodoFilter.Today => _localizationService.T("Todo.Empty.Today"),
        TodoFilter.Important => _localizationService.T("Todo.Empty.Important"),
        TodoFilter.Completed => _localizationService.T("Todo.Empty.Completed"),
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
            }
        }
    }

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 2, TextSize + 3);

    public double FilterTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => WidgetSegmentedMetrics.Create(TextSize).TextSize;

    public double SegmentHeight => WidgetSegmentedMetrics.Create(TextSize).Height;

    public Thickness SegmentPadding => WidgetSegmentedMetrics.Create(TextSize).Padding;

    public double InputHeight => WidgetInputMetrics.Create(TextSize).Height;

    public double InputButtonSize => WidgetInputMetrics.Create(TextSize).ButtonSize;

    public double InputActionIconSize => WidgetInputMetrics.Create(TextSize).ActionIconSize;

    public Thickness InputPadding => WidgetInputMetrics.Create(TextSize).Padding;

    public double ItemTextLineHeight => Math.Round(TextSize * 1.26);

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public async Task InitializeAsync()
    {
        var data = await _store.LoadAsync();

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
        var item = await AddItemAsync(InputText);
        if (item is not null)
        {
            InputText = string.Empty;
        }

        return item;
    }

    public async Task<TodoItemViewModel?> AddItemAsync(string? text)
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

        item.IsCompleted = isCompleted;
        item.UpdatedAt = DateTimeOffset.UtcNow;
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

    public async Task<bool> SetDueDateAsync(string itemId, DateTimeOffset? dueDate)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate);
        if (Nullable.Equals(item.DueDate?.Date, normalizedDueDate?.Date))
        {
            return true;
        }

        item.DueDate = normalizedDueDate;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetDueDatePresetAsync(string itemId, TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        DateTimeOffset? dueDate = preset switch
        {
            TodoDuePreset.Today => today,
            TodoDuePreset.Tomorrow => today.AddDays(1),
            TodoDuePreset.ThisWeek => today.AddDays(daysUntilSunday),
            TodoDuePreset.Clear => null,
            _ => null
        };

        return await SetDueDateAsync(itemId, dueDate);
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

        CaptureUndoSnapshot(_localizationService.T("Todo.Undo.Deleted"));
        Items.Remove(item);
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
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

    public void SetColorFilter(TodoColorFilter filter)
    {
        SelectedColorFilter = filter;
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
        return Items.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
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
        foreach (var item in Items.Where(ShouldShowItem))
        {
            VisibleItems.Add(item);
        }

        RefreshVisibleStateProperties();
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
        foreach (var item in Items)
        {
            item.RefreshLocalizedText();
        }
    }

    private void ApplyTodoSettings(AppSettings settings, bool updateFilter)
    {
        NewTaskPosition = settings.TodoNewTaskPosition;
        ShowCompletedTasks = settings.TodoShowCompletedTasks;
        ShowFooterStats = settings.TodoShowFooterStats;
        ShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        ConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;

        if (updateFilter)
        {
            SelectedFilter = MapDefaultFilter(settings.TodoDefaultFilter);
        }
        else
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
        return dueDate?.Date;
    }

    private static bool IsDueToday(TodoItemViewModel item)
    {
        return item.DueDate is { } dueDate &&
               dueDate.Date == DateTimeOffset.Now.Date;
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
            _ => null
        };
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
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static string NormalizeText(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }

}
