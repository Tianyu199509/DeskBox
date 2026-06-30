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
    Completed
}

public sealed partial class TodoWidgetViewModel : ObservableObject
{
    private readonly TodoWidgetStore _store;
    private readonly LocalizationService _localizationService;
    private readonly WidgetConfig _config;
    private TodoFilter _selectedFilter = TodoFilter.All;
    private string _inputText = string.Empty;
    private bool _isInitialized;

    public TodoWidgetViewModel(TodoWidgetStore store, LocalizationService localizationService, WidgetConfig config)
    {
        _store = store;
        _localizationService = localizationService;
        _config = config;
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
                OnPropertyChanged(nameof(IsCompletedFilterSelected));
            }
        }
    }

    public int TotalCount => Items.Count;

    public int ActiveCount => Items.Count(item => !item.IsCompleted);

    public int CompletedCount => Items.Count(item => item.IsCompleted);

    public bool HasCompletedItems => CompletedCount > 0;

    public bool HasItems => TotalCount > 0;

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public Visibility ListVisibility => HasVisibleItems ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => HasVisibleItems ? Visibility.Collapsed : Visibility.Visible;

    public string AddPlaceholderText => _localizationService.T("Todo.AddPlaceholder");

    public string AllFilterText => _localizationService.T("Todo.Filter.All");

    public string ActiveFilterText => _localizationService.T("Todo.Filter.Active");

    public string CompletedFilterText => _localizationService.T("Todo.Filter.Completed");

    public string EmptyStateTitle => _localizationService.T("Todo.Empty.Title");

    public string EmptyStateText => SelectedFilter switch
    {
        TodoFilter.Active => _localizationService.T("Todo.Empty.Active"),
        TodoFilter.Completed => _localizationService.T("Todo.Empty.Completed"),
        _ => _localizationService.T("Todo.Empty.All")
    };

    public string ClearCompletedText => _localizationService.T("Todo.ClearCompleted");

    public string ItemsLeftText => string.Format(_localizationService.T("Todo.ItemsLeft"), ActiveCount);

    public bool IsAllFilterSelected => SelectedFilter == TodoFilter.All;

    public bool IsActiveFilterSelected => SelectedFilter == TodoFilter.Active;

    public bool IsCompletedFilterSelected => SelectedFilter == TodoFilter.Completed;

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
            Items.Add(new TodoItemViewModel(item));
        }

        NormalizeSortOrders();
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
        var viewModel = new TodoItemViewModel(item);

        Items.Insert(0, viewModel);
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

    public async Task<bool> DeleteItemAsync(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

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

    public void SetFilter(TodoFilter filter)
    {
        SelectedFilter = filter;
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
        return SelectedFilter switch
        {
            TodoFilter.Active => !item.IsCompleted,
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
        OnPropertyChanged(nameof(HasCompletedItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ItemsLeftText));
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private void RefreshVisibleStateProperties()
    {
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AddPlaceholderText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ClearCompletedText));
        OnPropertyChanged(nameof(ItemsLeftText));
    }

    private static string NormalizeText(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }
}
