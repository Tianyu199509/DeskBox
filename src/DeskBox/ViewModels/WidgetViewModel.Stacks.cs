using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private readonly ObservableCollection<WidgetItem> _stackDisplayItems = [];
    private readonly Dictionary<string, WidgetStackItem> _stackItems = [];
    private bool _fileStacksEnabled;
    private string _fileStackGroupBy = SettingsService.FileStackGroupByKind;
    private int _fileStackThreshold = SettingsService.DefaultFileStackThreshold;
    private string _fileStackOrderBy = SettingsService.FileStackOrderByWidget;
    private string? _expandedStackKey;
    private bool _stackRebuildQueued;
    private DispatcherQueueTimer? _stackDateBoundaryTimer;

    public IEnumerable<WidgetItem> VisibleItems => FileStacksEnabled
        ? _stackDisplayItems
        : Items;

    public bool FileStacksEnabled
    {
        get => _fileStacksEnabled;
        private set => SetProperty(ref _fileStacksEnabled, value);
    }

    public string FileStackGroupBy
    {
        get => _fileStackGroupBy;
        private set => SetProperty(ref _fileStackGroupBy, value);
    }

    public int FileStackThreshold
    {
        get => _fileStackThreshold;
        private set => SetProperty(ref _fileStackThreshold, value);
    }

    public string FileStackOrderBy
    {
        get => _fileStackOrderBy;
        private set => SetProperty(ref _fileStackOrderBy, value);
    }

    public bool FileStacksFollowGlobalDefaults =>
        WidgetFileStackSettings.FollowsGlobalDefaults(Config);

    public bool FileStacksEnabledFollowsGlobal =>
        WidgetFileStackSettings.GetEnabledOverride(Config) is null;

    public bool FileStackGroupByFollowsGlobal =>
        WidgetFileStackSettings.GetGroupByOverride(Config) is null;

    public bool FileStackThresholdFollowsGlobal =>
        WidgetFileStackSettings.GetThresholdOverride(Config) is null;

    public bool FileStackOrderByFollowsGlobal =>
        WidgetFileStackSettings.GetOrderByOverride(Config) is null;

    public void ToggleStack(WidgetStackItem stack)
    {
        SetStackExpanded(stack, !stack.IsExpanded);
    }

    public void SetStackExpanded(WidgetStackItem stack, bool expanded)
    {
        _expandedStackKey = expanded ? stack.StackKey : null;
        RebuildStackDisplayItems();
    }

    public bool CollapseExpandedStack()
    {
        if (string.IsNullOrEmpty(_expandedStackKey))
        {
            return false;
        }

        _expandedStackKey = null;
        RebuildStackDisplayItems();
        return true;
    }

    internal void StabilizeStackDisplay()
    {
        RebuildStackDisplayItems();
        foreach (var stack in _stackItems.Values)
        {
            stack.RefreshPresentationState();
        }
    }

    public void SetFileStacksEnabledOverride(bool? enabled)
    {
        WidgetFileStackSettings.SetEnabledOverride(Config, enabled);
        PersistStackOverrides();
    }

    public void SetFileStackGroupByOverride(string? groupBy)
    {
        WidgetFileStackSettings.SetGroupByOverride(Config, groupBy);
        PersistStackOverrides();
    }

    public void SetFileStackThresholdOverride(int? threshold)
    {
        WidgetFileStackSettings.SetThresholdOverride(Config, threshold);
        PersistStackOverrides();
    }

    public void SetFileStackOrderByOverride(string? orderBy)
    {
        WidgetFileStackSettings.SetOrderByOverride(Config, orderBy);
        PersistStackOverrides();
    }

    public void ClearFileStackOverrides()
    {
        WidgetFileStackSettings.ClearOverrides(Config);
        PersistStackOverrides();
    }

    private void PersistStackOverrides()
    {
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        ApplyStackSettings();
        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
    }

    private void InitializeStacks()
    {
        _fileStacksEnabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        _fileStackGroupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        _fileStackThreshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        _fileStackOrderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        Items.CollectionChanged += StackSourceItems_CollectionChanged;
        ScheduleStackDateBoundaryRefresh();
        QueueStackDisplayRebuild();
    }

    private void CleanupStacks()
    {
        Items.CollectionChanged -= StackSourceItems_CollectionChanged;
        if (_stackDateBoundaryTimer is not null)
        {
            _stackDateBoundaryTimer.Stop();
            _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;
            _stackDateBoundaryTimer = null;
        }
    }

    private void StackSourceItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueStackDisplayRebuild();
    }

    private void QueueStackDisplayRebuild()
    {
        if (_stackRebuildQueued)
        {
            return;
        }

        _stackRebuildQueued = true;
        _dispatcherQueue.TryEnqueue(() =>
        {
            _stackRebuildQueued = false;
            RebuildStackDisplayItems();
        });
    }

    private void ApplyStackSettings()
    {
        bool enabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        string groupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        int threshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        string orderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        bool sourceChanged = FileStacksEnabled != enabled;
        FileStacksEnabled = enabled;
        FileStackGroupBy = groupBy;
        FileStackThreshold = threshold;
        FileStackOrderBy = orderBy;
        if (!enabled)
        {
            _expandedStackKey = null;
        }

        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
        if (sourceChanged)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }

        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
    }

    private void RebuildStackDisplayItems()
    {
        foreach (var item in Items)
        {
            item.IsStackChild = false;
        }

        if (!FileStacksEnabled)
        {
            _stackDisplayItems.Clear();
            return;
        }

        var groups = WidgetStackGroupingService.Group(
            Items,
            FileStackGroupBy,
            orderBy: FileStackOrderBy,
            customRules: _settingsService.Settings.FileStackCustomRules,
            unmatchedBehavior: _settingsService.Settings.FileStackUnmatchedBehavior);
        if (_expandedStackKey is not null &&
            !groups.Any(group =>
                group.CanStack &&
                group.Items.Count >= FileStackThreshold &&
                group.EffectiveKey == _expandedStackKey))
        {
            _expandedStackKey = null;
        }

        var projected = new List<WidgetItem>();
        foreach (var group in groups)
        {
            if (!group.CanStack || group.Items.Count < FileStackThreshold)
            {
                projected.AddRange(group.Items);
                continue;
            }

            string key = group.EffectiveKey;
            bool expanded = string.Equals(key, _expandedStackKey, StringComparison.Ordinal);
            projected.Add(CreateStackItem(group, expanded));
            if (!expanded)
            {
                continue;
            }

            foreach (var item in group.Items)
            {
                item.IsStackChild = true;
                projected.Add(item);
            }
        }

        ReconcileStackDisplayItems(projected);
    }

    private WidgetStackItem CreateStackItem(WidgetStackGroup group, bool expanded)
    {
        string key = group.EffectiveKey;
        string name = group.DisplayName ?? GetStackCategoryName(group.Category);
        if (!_stackItems.TryGetValue(key, out var stack))
        {
            stack = new WidgetStackItem
            {
                Category = group.Category,
                StackKey = key
            };
            _stackItems[key] = stack;
        }

        stack.Update(
            group.Items,
            name,
            _localizationService.Format("Widget.Stack.ItemCount", group.Items.Count),
            _localizationService.T(expanded
                ? "Widget.Stack.State.Expanded"
                : "Widget.Stack.State.Collapsed"),
            _localizationService.T("Widget.Stack.Collapse"),
            expanded,
            IconTileWidth,
            IconTileHeight,
            IconTileMargin,
            IconTilePadding,
            IconImageSize,
            Math.Clamp(Math.Round(IconImageSize * 0.76), 14, IconImageSize),
            IconLabelMaxWidth,
            IconLabelFontSize,
            ListItemMargin,
            ListItemPadding,
            ListIconSize);
        return stack;
    }

    private void ReconcileStackDisplayItems(IReadOnlyList<WidgetItem> desired)
    {
        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            WidgetItem desiredItem = desired[targetIndex];
            if (targetIndex < _stackDisplayItems.Count &&
                ReferenceEquals(_stackDisplayItems[targetIndex], desiredItem))
            {
                continue;
            }

            int existingIndex = IndexOfReference(_stackDisplayItems, desiredItem, targetIndex + 1);
            if (existingIndex >= 0)
            {
                _stackDisplayItems.Move(existingIndex, targetIndex);
            }
            else
            {
                _stackDisplayItems.Insert(targetIndex, desiredItem);
            }
        }

        while (_stackDisplayItems.Count > desired.Count)
        {
            _stackDisplayItems.RemoveAt(_stackDisplayItems.Count - 1);
        }
    }

    private static int IndexOfReference(
        IReadOnlyList<WidgetItem> items,
        WidgetItem candidate,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], candidate))
            {
                return index;
            }
        }

        return -1;
    }

    private string GetStackCategoryName(WidgetStackCategory category) =>
        _localizationService.T($"Widget.Stack.Category.{category}");

    private void ScheduleStackDateBoundaryRefresh()
    {
        _stackDateBoundaryTimer ??= _dispatcherQueue.CreateTimer();
        _stackDateBoundaryTimer.Stop();
        _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;

        bool usesDateGrouping = FileStackGroupBy is
            SettingsService.FileStackGroupByDateAdded or
            SettingsService.FileStackGroupByDateModified;
        if (!FileStacksEnabled || !usesDateGrouping)
        {
            return;
        }

        DateTime now = DateTime.Now;
        _stackDateBoundaryTimer.Interval = now.Date.AddDays(1).AddSeconds(1) - now;
        _stackDateBoundaryTimer.IsRepeating = false;
        _stackDateBoundaryTimer.Tick += StackDateBoundaryTimer_Tick;
        _stackDateBoundaryTimer.Start();
    }

    private void StackDateBoundaryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= StackDateBoundaryTimer_Tick;
        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
    }
}
