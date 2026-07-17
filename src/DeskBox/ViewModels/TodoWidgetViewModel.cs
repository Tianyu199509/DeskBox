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
    private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    private string _newTaskPosition = SettingsService.TodoNewTaskPositionTop;
    private string _tabStyle = SettingsService.WidgetTabStyleButton;
    private bool _showTabBar = true;
    private bool _showAllTab = true;
    private bool _showActiveTab;
    private bool _showTodayTab = true;
    private bool _showThisWeekTab;
    private bool _showThisMonthTab;
    private bool _showImportantTab = true;
    private bool _showCompletedTab = true;
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
            _layoutDensityScale = NormalizeDensity(_settingsService.Settings.LayoutDensityScale);
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
                OnPropertyChanged(nameof(IsThisWeekFilterSelected));
                OnPropertyChanged(nameof(IsThisMonthFilterSelected));
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

    public int ThisWeekFilterCount => Items.Count(item => IsDueThisWeek(item) && ShouldCountInNonCompletedFilter(item));

    public int ThisMonthFilterCount => Items.Count(item => IsDueThisMonth(item) && ShouldCountInNonCompletedFilter(item));

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

    public string ThisWeekFilterText => FormatFilterText("Todo.Filter.ThisWeek", ThisWeekFilterCount);

    public string ThisMonthFilterText => FormatFilterText("Todo.Filter.ThisMonth", ThisMonthFilterCount);

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
    public Visibility ThisWeekTabVisibility => _showThisWeekTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThisMonthTabVisibility => _showThisMonthTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImportantTabVisibility => _showImportantTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletedTabVisibility => _showCompletedTab ? Visibility.Visible : Visibility.Collapsed;

    public int VisibleTabCount =>
        (_showAllTab ? 1 : 0) +
        (_showActiveTab ? 1 : 0) +
        (_showTodayTab ? 1 : 0) +
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

    public double LayoutDensityScale
    {
        get => _layoutDensityScale;
        private set
        {
            if (SetProperty(ref _layoutDensityScale, NormalizeDensity(value)))
            {
                OnPropertyChanged(nameof(RootPadding));
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

    public double FilterTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => WidgetSegmentedMetrics.Create(TextSize).TextSize;

    public double SegmentHeight => WidgetSegmentedMetrics.Create(TextSize).Height;

    public Thickness SegmentPadding => WidgetSegmentedMetrics.Create(TextSize).Padding;

    public double InputHeight => WidgetInputMetrics.Create(TextSize).Height;

    public double InputButtonSize => WidgetInputMetrics.Create(TextSize).ButtonSize;

    public double InputActionIconSize => WidgetInputMetrics.Create(TextSize).ActionIconSize;

    public Thickness InputPadding => WidgetInputMetrics.Create(TextSize).Padding;

    public Thickness RootPadding => UniformThickness(Lerp(6, 10, LayoutDensityScale));

    public double RootRowSpacing => Math.Round(Lerp(5, 10, LayoutDensityScale));

    public double ItemMinHeight => Math.Round(Lerp(42, 56, LayoutDensityScale));

    public Thickness ItemPadding => new(
        Math.Round(Lerp(4, 11, LayoutDensityScale)),
        Math.Round(Lerp(3, 8, LayoutDensityScale)),
        Math.Round(Lerp(4, 11, LayoutDensityScale)),
        Math.Round(Lerp(3, 8, LayoutDensityScale)));

    public Thickness ItemMargin => new(0, 0, 0, Math.Round(Lerp(3, 8, LayoutDensityScale)));

    public Thickness ItemChromeMargin => new(
        -ItemPadding.Left,
        -ItemPadding.Top,
        -ItemPadding.Right,
        -ItemPadding.Bottom);

    public Thickness ItemContentMargin => new(0, Math.Round(Lerp(1, 3, LayoutDensityScale)), 0, Math.Round(Lerp(1, 3, LayoutDensityScale)));

    public double AddCardMinHeight => Math.Round(Lerp(42, 56, LayoutDensityScale));

    public Thickness AddCardMargin => new(0, 0, 0, Math.Round(Lerp(4, 9, LayoutDensityScale)));

    public Thickness AddCardPadding => new(
        Math.Round(Lerp(6, 12, LayoutDensityScale)),
        Math.Round(Lerp(4, 8, LayoutDensityScale)),
        Math.Round(Lerp(6, 12, LayoutDensityScale)),
        Math.Round(Lerp(4, 8, LayoutDensityScale)));

    public double ItemTextLineHeight => Math.Round(TextSize + Lerp(2.5, 6, LayoutDensityScale));

    public int ItemPreviewLineCount => _settingsService is null
        ? SettingsService.DefaultItemPreviewLineCount
        : SettingsService.NormalizeItemPreviewLineCount(
            _settingsService.Settings.TodoItemPreviewLineCount);

    public double SmallIconSize => Math.Round(Math.Clamp(TextSize + 1, 11, 15));

    public double StandardIconSize => Math.Round(Math.Clamp(TextSize + 3, 14, 18));

    public double PrimaryIconSize => Math.Round(Math.Clamp(TextSize + 5, 16, 20));

    public double CompletionIndicatorSize => Math.Round(Math.Clamp(TextSize + 7, 18, 22));

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
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
