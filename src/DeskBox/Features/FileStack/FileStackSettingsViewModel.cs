using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Features.FileStack;

/// <summary>
/// File-stack-section settings editor. Owns the section's XAML binding
/// surface (auto-stacking, the grouping mode including the custom-rule
/// collection, the threshold, ordering, the open mode with its popover
/// layout and style, and the unmatched-file behavior): persisted fields
/// project through <see cref="IFileStackSettings"/> snapshots (normalized
/// with the old shell-constructor semantics), user edits write through the
/// same coordinator ports the legacy shell used, and external refresh paths
/// (settings broadcasts, default restores, language changes) re-sync the
/// projection instead of writing back. The custom-rule list keeps its
/// single aggregation path (batch 36): add/remove/reorder and per-rule edits
/// funnel through the collection's change events into one
/// <see cref="IFileStackSettings.SetFileStackCustomRules"/> write whose
/// equivalent-recommit skip lives in the coordinator. The rule *preview*
/// entry scan (widget config plus disk enumeration) needs services the
/// editor must not reference, so the shell builds the entries and pushes
/// them through <see cref="UpdatePreviewEntries"/>; matching rules against
/// those entries and formatting the per-rule and summary preview texts is
/// presentation state and lives here. The bindable property names
/// intentionally drop the legacy <c>FileStack</c>/<c>Selected</c> prefixes:
/// the section-level DataContext switch means they no longer need to be
/// unique across the whole shell, and unprefixed names keep clear of the
/// flat <c>AppSettings</c> facade-name ratchet. This class references
/// neither App nor WinUI nor the settings adapter; localization arrives as
/// delegates.
/// </summary>
public sealed partial class FileStackSettingsViewModel : ObservableObject
{
    private static readonly string[] GroupByValues =
    [
        FileStackOptionKinds.GroupByKind,
        FileStackOptionKinds.GroupByDateModified,
        FileStackOptionKinds.GroupByCustom
    ];

    private static readonly string[] OrderByValues =
    [
        FileStackOptionKinds.OrderByWidget,
        FileStackOptionKinds.OrderByName,
        FileStackOptionKinds.OrderByDateAdded,
        FileStackOptionKinds.OrderByDateModified
    ];

    private static readonly string[] OpenModeValues =
    [
        FileStackOptionKinds.OpenModeInline,
        FileStackOptionKinds.OpenModePopover
    ];

    private static readonly string[] PopoverLayoutValues =
    [
        FileStackOptionKinds.PopoverLayoutAdaptive,
        FileStackOptionKinds.PopoverLayoutGrid3,
        FileStackOptionKinds.PopoverLayoutGrid5
    ];

    private static readonly string[] PopoverStyleValues =
    [
        FileStackOptionKinds.PopoverStyleFollowMaterial,
        FileStackOptionKinds.PopoverStyleNeutral
    ];

    private static readonly string[] UnmatchedBehaviorValues =
    [
        FileStackOptionKinds.UnmatchedKeepLoose,
        FileStackOptionKinds.UnmatchedOther
    ];

    private readonly IFileStackSettings _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _format;
    private bool _isSyncingPresentation;
    private bool _isSynchronizingRules;
    private bool _autoStacking;
    private string _groupBy = FileStackOptionKinds.GroupByKind;
    private int _threshold = FileStackOptionKinds.DefaultThreshold;
    private string _orderBy = FileStackOptionKinds.OrderByWidget;
    private string _openMode = FileStackOptionKinds.OpenModePopover;
    private string _popoverLayout = FileStackOptionKinds.PopoverLayoutGrid3;
    private string _popoverStyle = FileStackOptionKinds.PopoverStyleNeutral;
    private string _unmatchedBehavior = FileStackOptionKinds.UnmatchedKeepLoose;
    private string _previewSummaryText = string.Empty;
    private string[]? _cachedGroupByNames;
    private string[]? _cachedThresholdNames;
    private string[]? _cachedOrderByNames;
    private string[]? _cachedOpenModeNames;
    private string[]? _cachedPopoverLayoutNames;
    private string[]? _cachedPopoverStyleNames;
    private string[]? _cachedUnmatchedBehaviorNames;
    private IReadOnlyList<FileStackPreviewEntry> _previewEntries = [];

    public FileStackSettingsViewModel(
        IFileStackSettings settings,
        Func<string, string> localize,
        Func<string, object[], string> format)
    {
        _settings = settings;
        _localize = localize;
        _format = format;
        CustomRules.CollectionChanged += CustomRules_CollectionChanged;
        SyncPresentation();
    }

    public ObservableCollection<FileStackCustomRuleEditor> CustomRules { get; } = [];

    public bool AutoStacking
    {
        get => _autoStacking;
        set
        {
            if (!SetProperty(ref _autoStacking, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SettingsSummaryText));
            OnPropertyChanged(nameof(CanAddRule));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackAutoStacking(value);
        }
    }

    public string GroupBy
    {
        get => _groupBy;
        set
        {
            string normalized = FileStackOptionKinds.NormalizeGroupBy(value);
            if (!SetProperty(ref _groupBy, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowCustomRules));
            OnPropertyChanged(nameof(SettingsSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackGroupBy(normalized);
        }
    }

    public bool ShowCustomRules => GroupBy == FileStackOptionKinds.GroupByCustom;

    public int Threshold
    {
        get => _threshold;
        set
        {
            int normalized = FileStackOptionKinds.NormalizeThreshold(value);
            if (!SetProperty(ref _threshold, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackThreshold(normalized);
        }
    }

    public string OrderBy
    {
        get => _orderBy;
        set
        {
            string normalized = FileStackOptionKinds.NormalizeOrderBy(value);
            if (!SetProperty(ref _orderBy, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackOrderBy(normalized);
        }
    }

    public string OpenMode
    {
        get => _openMode;
        set
        {
            string normalized = FileStackOptionKinds.NormalizeOpenMode(value);
            if (!SetProperty(ref _openMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPopoverOpenMode));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackOpenMode(normalized);
        }
    }

    /// <summary>
    /// Whether the popover-only cards (layout and visual style) are
    /// configurable: popover expansion uses them, inline expansion freezes
    /// both.
    /// </summary>
    public bool IsPopoverOpenMode => _openMode == FileStackOptionKinds.OpenModePopover;

    public string PopoverLayout
    {
        get => _popoverLayout;
        set
        {
            string normalized = FileStackOptionKinds.NormalizePopoverLayout(value);
            if (!SetProperty(ref _popoverLayout, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackPopoverLayout(normalized);
        }
    }

    public string PopoverStyle
    {
        get => _popoverStyle;
        set
        {
            string normalized = FileStackOptionKinds.NormalizePopoverStyle(value);
            if (!SetProperty(ref _popoverStyle, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackPopoverStyle(normalized);
        }
    }

    public string UnmatchedBehavior
    {
        get => _unmatchedBehavior;
        set
        {
            string normalized = FileStackOptionKinds.NormalizeUnmatchedBehavior(value);
            if (!SetProperty(ref _unmatchedBehavior, normalized))
            {
                return;
            }

            RefreshRulePreview();
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileStackUnmatchedBehavior(normalized);
        }
    }

    public bool HasNoRules => CustomRules.Count == 0;

    public bool CanAddRule => AutoStacking &&
        CustomRules.Count < FileStackOptionKinds.MaxCustomRules;

    public string PreviewSummaryText
    {
        get => _previewSummaryText;
        private set => SetProperty(ref _previewSummaryText, value);
    }

    /// <summary>
    /// The file-widget overview row's capability summary (Manual / custom-rule
    /// count / on), derived from the persisted projection.
    /// </summary>
    public string SettingsSummaryText
    {
        get
        {
            if (!AutoStacking)
            {
                return _localize("Settings.FileStacks.Status.Manual");
            }

            if (GroupBy == FileStackOptionKinds.GroupByCustom)
            {
                return _format(
                    "Settings.FileStacks.Status.Custom",
                    [CustomRules.Count]);
            }

            // A stable capability summary; the previous group-by display name
            // ("文件类型") read like a stray description on the entry row.
            return _localize("Settings.FileStacks.Status.On");
        }
    }

    public IReadOnlyList<SettingsOption> AvailableGroupByOptions
    {
        get
        {
            _cachedGroupByNames ??= GroupByValues.Select(GetGroupByDisplayName).ToArray();
            return BuildOptions(GroupByValues, _cachedGroupByNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableThresholdOptions
    {
        get
        {
            _cachedThresholdNames ??= FileStackOptionKinds.Thresholds
                .Select(value => _format(
                    "Settings.FileStacks.Threshold.Option",
                    [value]))
                .ToArray();
            return BuildOptions(
                FileStackOptionKinds.Thresholds.Cast<object>().ToArray(),
                _cachedThresholdNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableOrderByOptions
    {
        get
        {
            _cachedOrderByNames ??= OrderByValues.Select(GetOrderByDisplayName).ToArray();
            return BuildOptions(OrderByValues, _cachedOrderByNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableOpenModeOptions
    {
        get
        {
            _cachedOpenModeNames ??= OpenModeValues.Select(GetOpenModeDisplayName).ToArray();
            return BuildOptions(OpenModeValues, _cachedOpenModeNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailablePopoverLayoutOptions
    {
        get
        {
            _cachedPopoverLayoutNames ??= PopoverLayoutValues
                .Select(GetPopoverLayoutDisplayName)
                .ToArray();
            return BuildOptions(PopoverLayoutValues, _cachedPopoverLayoutNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailablePopoverStyleOptions
    {
        get
        {
            _cachedPopoverStyleNames ??= PopoverStyleValues
                .Select(GetPopoverStyleDisplayName)
                .ToArray();
            return BuildOptions(PopoverStyleValues, _cachedPopoverStyleNames);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableUnmatchedBehaviorOptions
    {
        get
        {
            _cachedUnmatchedBehaviorNames ??= UnmatchedBehaviorValues
                .Select(GetUnmatchedBehaviorDisplayName)
                .ToArray();
            return BuildOptions(UnmatchedBehaviorValues, _cachedUnmatchedBehaviorNames);
        }
    }

    // Build a real SettingsOption[] (not a collection expression): the
    // hidden read-only-array type cannot marshal across the WinRT ABI in
    // Native AOT builds and would leave the ItemsSource empty.
    private static SettingsOption[] BuildOptions(
        object[] values,
        string[] displayNames)
    {
        var options = new SettingsOption[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            options[index] = new SettingsOption(values[index], displayNames[index]);
        }

        return options;
    }

    public string GetGroupByDisplayName(string groupBy) =>
        FileStackOptionKinds.NormalizeGroupBy(groupBy) switch
        {
            FileStackOptionKinds.GroupByDateAdded =>
                _localize("Settings.FileStacks.GroupBy.DateAdded"),
            FileStackOptionKinds.GroupByDateModified =>
                _localize("Settings.FileStacks.GroupBy.DateModified"),
            FileStackOptionKinds.GroupByCustom =>
                _localize("Settings.FileStacks.GroupBy.Custom"),
            _ => _localize("Settings.FileStacks.GroupBy.Kind")
        };

    public string GetOrderByDisplayName(string orderBy) =>
        FileStackOptionKinds.NormalizeOrderBy(orderBy) switch
        {
            FileStackOptionKinds.OrderByName =>
                _localize("Settings.FileStacks.OrderBy.Name"),
            FileStackOptionKinds.OrderByDateAdded =>
                _localize("Settings.FileStacks.OrderBy.DateAdded"),
            FileStackOptionKinds.OrderByDateModified =>
                _localize("Settings.FileStacks.OrderBy.DateModified"),
            _ => _localize("Settings.FileStacks.OrderBy.Widget")
        };

    public string GetOpenModeDisplayName(string openMode) =>
        FileStackOptionKinds.NormalizeOpenMode(openMode) ==
            FileStackOptionKinds.OpenModePopover
                ? _localize("Settings.FileStacks.OpenMode.Popover")
                : _localize("Settings.FileStacks.OpenMode.Inline");

    public string GetPopoverLayoutDisplayName(string layout) =>
        FileStackOptionKinds.NormalizePopoverLayout(layout) switch
        {
            FileStackOptionKinds.PopoverLayoutGrid3 =>
                _localize("Settings.FileStacks.PopoverLayout.Grid3"),
            FileStackOptionKinds.PopoverLayoutGrid5 =>
                _localize("Settings.FileStacks.PopoverLayout.Grid5"),
            _ => _localize("Settings.FileStacks.PopoverLayout.Adaptive")
        };

    public string GetPopoverStyleDisplayName(string style) =>
        FileStackOptionKinds.NormalizePopoverStyle(style) ==
            FileStackOptionKinds.PopoverStyleFollowMaterial
                ? _localize("Settings.FileStacks.PopoverStyle.FollowMaterial")
                : _localize("Settings.FileStacks.PopoverStyle.Neutral");

    public string GetUnmatchedBehaviorDisplayName(string behavior) =>
        FileStackOptionKinds.NormalizeUnmatchedBehavior(behavior) ==
            FileStackOptionKinds.UnmatchedOther
                ? _localize("Settings.FileStacks.Unmatched.Other")
                : _localize("Settings.FileStacks.Unmatched.KeepLoose");

    /// <summary>
    /// Re-projects the persisted file-stack state onto the binding surface
    /// without writing back. Called on construction, settings broadcasts and
    /// default restores; normalizes the raw snapshot with the old
    /// shell-constructor semantics.
    /// </summary>
    public void SyncPresentation()
    {
        FileStackSettingsSnapshot snapshot = _settings.ReadAll();
        _isSyncingPresentation = true;
        try
        {
            AutoStacking = snapshot.FileStackAutoStacking;
            GroupBy = FileStackOptionKinds.NormalizeGroupBy(snapshot.FileStackGroupBy);
            Threshold = FileStackOptionKinds.NormalizeThreshold(snapshot.FileStackThreshold);
            OrderBy = FileStackOptionKinds.NormalizeOrderBy(snapshot.FileStackOrderBy);
            OpenMode = FileStackOptionKinds.NormalizeOpenMode(snapshot.FileStackOpenMode);
            PopoverLayout = FileStackOptionKinds.NormalizePopoverLayout(
                snapshot.FileStackPopoverLayout);
            PopoverStyle = FileStackOptionKinds.NormalizePopoverStyle(
                snapshot.FileStackPopoverStyle);
            UnmatchedBehavior = FileStackOptionKinds.NormalizeUnmatchedBehavior(
                snapshot.FileStackUnmatchedBehavior);
            if (!RuleEditorsMatch(snapshot.FileStackCustomRules))
            {
                ReplaceRuleEditors(snapshot.FileStackCustomRules);
            }
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        RefreshRulePreview();
    }

    /// <summary>
    /// Drops the localized option-name caches after a language change so the
    /// option tables, priorities, preview texts and the summary re-project
    /// in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedGroupByNames = null;
        _cachedThresholdNames = null;
        _cachedOrderByNames = null;
        _cachedOpenModeNames = null;
        _cachedPopoverLayoutNames = null;
        _cachedPopoverStyleNames = null;
        _cachedUnmatchedBehaviorNames = null;
        OnPropertyChanged(nameof(AvailableGroupByOptions));
        OnPropertyChanged(nameof(AvailableThresholdOptions));
        OnPropertyChanged(nameof(AvailableOrderByOptions));
        OnPropertyChanged(nameof(AvailableOpenModeOptions));
        OnPropertyChanged(nameof(AvailablePopoverLayoutOptions));
        OnPropertyChanged(nameof(AvailablePopoverStyleOptions));
        OnPropertyChanged(nameof(AvailableUnmatchedBehaviorOptions));
        // Re-notify the current selections: replacing the localized option
        // arrays makes WinUI reset every bound ComboBox.SelectedIndex to -1.
        OnPropertyChanged(nameof(GroupBy));
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(OrderBy));
        OnPropertyChanged(nameof(OpenMode));
        OnPropertyChanged(nameof(PopoverLayout));
        OnPropertyChanged(nameof(PopoverStyle));
        OnPropertyChanged(nameof(UnmatchedBehavior));
        OnPropertyChanged(nameof(SettingsSummaryText));
        UpdateRulePriorities();
        RefreshRulePreview();
    }

    public void AddRule()
    {
        if (!CanAddRule)
        {
            return;
        }

        int nextNumber = CustomRules.Count + 1;
        var editor = new FileStackCustomRuleEditor
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = _format(
                "Settings.FileStacks.Custom.DefaultName",
                [nextNumber])
        };
        CustomRules.Add(editor);
    }

    public void RemoveRule(FileStackCustomRuleEditor editor)
    {
        CustomRules.Remove(editor);
    }

    public void MoveRule(FileStackCustomRuleEditor editor, int offset)
    {
        int oldIndex = CustomRules.IndexOf(editor);
        int newIndex = Math.Clamp(oldIndex + offset, 0, CustomRules.Count - 1);
        if (oldIndex >= 0 && oldIndex != newIndex)
        {
            CustomRules.Move(oldIndex, newIndex);
        }
    }

    /// <summary>Commits a drag-reorder: refresh priorities, then persist.</summary>
    public void CommitRuleOrder()
    {
        UpdateRulePriorities();
        PersistCustomRules();
    }

    /// <summary>
    /// Shows the localized loading placeholder while the shell re-scans the
    /// widget config and the mapped folders for preview entries.
    /// </summary>
    public void MarkPreviewLoading()
    {
        PreviewSummaryText = _localize("Settings.FileStacks.Custom.Preview.Loading");
    }

    /// <summary>
    /// Pushed by the shell: the freshly scanned preview entries (widget
    /// items plus, on a disk refresh, files under mapped folders). The
    /// editor re-runs the rule matching against them.
    /// </summary>
    public void UpdatePreviewEntries(IReadOnlyList<FileStackPreviewEntry> entries)
    {
        _previewEntries = entries;
        RefreshRulePreview();
    }

    private void CustomRules_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FileStackCustomRuleEditor editor in e.OldItems)
            {
                editor.PropertyChanged -= RuleEditor_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FileStackCustomRuleEditor editor in e.NewItems)
            {
                editor.PropertyChanged -= RuleEditor_PropertyChanged;
                editor.PropertyChanged += RuleEditor_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(SettingsSummaryText));
        OnPropertyChanged(nameof(CanAddRule));
        UpdateRulePriorities();
        RefreshRulePreview();
        if (!_isSynchronizingRules)
        {
            PersistCustomRules();
        }
    }

    private void RuleEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(FileStackCustomRuleEditor.Name) and
            not nameof(FileStackCustomRuleEditor.ExtensionsText))
        {
            return;
        }

        RefreshRulePreview();
        if (!_isSynchronizingRules)
        {
            PersistCustomRules();
        }
    }

    // Single write entry for the custom-rule collection: add/remove,
    // per-rule edits and drag-order commits all project their editors to
    // models here (extension-list normalization stays in ToModel) and let
    // the coordinator own the store-and-save semantics.
    private void PersistCustomRules()
    {
        _settings.SetFileStackCustomRules(
            CustomRules.Select(editor => editor.ToModel()).ToList());
    }

    private void ReplaceRuleEditors(IReadOnlyList<FileStackCustomRule>? rules)
    {
        _isSynchronizingRules = true;
        try
        {
            foreach (FileStackCustomRuleEditor editor in CustomRules)
            {
                editor.PropertyChanged -= RuleEditor_PropertyChanged;
            }

            CustomRules.Clear();
            foreach (FileStackCustomRule rule in rules ?? [])
            {
                var editor = FileStackCustomRuleEditor.FromModel(rule);
                editor.PropertyChanged += RuleEditor_PropertyChanged;
                CustomRules.Add(editor);
            }
        }
        finally
        {
            _isSynchronizingRules = false;
        }

        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(SettingsSummaryText));
        OnPropertyChanged(nameof(CanAddRule));
        UpdateRulePriorities();
    }

    private bool RuleEditorsMatch(IReadOnlyList<FileStackCustomRule>? rules)
    {
        var normalizedRules = rules ?? [];
        if (CustomRules.Count != normalizedRules.Count)
        {
            return false;
        }

        for (int index = 0; index < CustomRules.Count; index++)
        {
            FileStackCustomRule editorRule = CustomRules[index].ToModel();
            FileStackCustomRule modelRule = normalizedRules[index];
            if (!string.Equals(editorRule.Id, modelRule.Id, StringComparison.Ordinal) ||
                !string.Equals(editorRule.Name, modelRule.Name?.Trim(), StringComparison.Ordinal) ||
                !editorRule.Extensions.SequenceEqual(
                    FileStackOptionKinds.NormalizeExtensions(modelRule.Extensions),
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateRulePriorities()
    {
        for (int index = 0; index < CustomRules.Count; index++)
        {
            FileStackCustomRuleEditor editor = CustomRules[index];
            editor.PriorityText = _format(
                "Settings.FileStacks.Custom.Priority",
                [index + 1]);
            editor.CanMoveUp = index > 0;
            editor.CanMoveDown = index < CustomRules.Count - 1;
        }
    }

    private void RefreshRulePreview()
    {
        IReadOnlyList<FileStackPreviewEntry> entries = _previewEntries;
        var unmatched = new List<FileStackPreviewEntry>(entries);
        int totalMatched = 0;

        foreach (FileStackCustomRuleEditor editor in CustomRules)
        {
            var extensions = FileStackCustomRuleEditor.ParseExtensions(editor.ExtensionsText)
                .Take(FileStackOptionKinds.MaxExtensionsPerRule)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (extensions.Count == 0)
            {
                editor.PreviewText = _localize(
                    "Settings.FileStacks.Custom.Preview.EmptyExtensions");
                continue;
            }

            var matches = unmatched
                .Where(entry => extensions.Contains(entry.Extension))
                .ToList();
            foreach (FileStackPreviewEntry match in matches)
            {
                unmatched.Remove(match);
            }

            totalMatched += matches.Count;
            if (matches.Count == 0)
            {
                editor.PreviewText = _localize(
                    "Settings.FileStacks.Custom.Preview.None");
                continue;
            }

            string samples = string.Join(", ", matches
                .Take(3)
                .Select(entry => string.IsNullOrWhiteSpace(entry.WidgetName)
                    ? Path.GetFileName(entry.Path)
                    : $"{entry.WidgetName} · {Path.GetFileName(entry.Path)}"));
            if (matches.Count > 3)
            {
                samples = $"{samples} …";
            }

            editor.PreviewText = _format(
                "Settings.FileStacks.Custom.Preview.Matches",
                [matches.Count, samples]);
        }

        PreviewSummaryText = _format(
            "Settings.FileStacks.Custom.Preview.Summary",
            [totalMatched, unmatched.Count]);
    }
}
