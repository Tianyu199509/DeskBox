using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private bool _fileStacksEnabled;
    private bool _fileStackAutoStacking;
    private string _selectedFileStackGroupBy = SettingsService.FileStackGroupByKind;
    private int _selectedFileStackThreshold = SettingsService.DefaultFileStackThreshold;
    private string _selectedFileStackOrderBy = SettingsService.FileStackOrderByWidget;
    private string _selectedFileStackOpenMode = SettingsService.FileStackOpenModeInline;
    private string _selectedFileStackPopoverLayout = SettingsService.FileStackPopoverLayoutGrid3;
    private string _selectedFileStackPopoverStyle = SettingsService.FileStackPopoverStyleNeutral;
    private string _selectedFileStackUnmatchedBehavior = SettingsService.FileStackUnmatchedKeepLoose;
    private bool _isSynchronizingFileStackRules;
    private int _fileStackPreviewRefreshGeneration;
    private string _fileStackPreviewSummaryText = string.Empty;
    private List<FileStackPreviewEntry> _fileStackPreviewEntries = [];
    private string[]? _cachedFileStackGroupByDisplayNames;
    private string[]? _cachedFileStackThresholdDisplayNames;
    private string[]? _cachedFileStackOrderByDisplayNames;
    private string[]? _cachedFileStackOpenModeDisplayNames;
    private string[]? _cachedFileStackPopoverLayoutDisplayNames;
    private string[]? _cachedFileStackPopoverStyleDisplayNames;
    private string[]? _cachedFileStackUnmatchedBehaviorDisplayNames;

    public ObservableCollection<FileStackCustomRuleEditor> FileStackCustomRules { get; } = [];

    public Visibility FileStackCustomRulesVisibility =>
        SelectedFileStackGroupBy == SettingsService.FileStackGroupByCustom
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility FileStackRulesEmptyVisibility => FileStackCustomRules.Count == 0
        ? Visibility.Visible
            : Visibility.Collapsed;

    public bool CanAddFileStackCustomRule => FileStacksEnabled &&
        FileStackCustomRules.Count < SettingsService.MaxFileStackCustomRules;

    public string FileStackPreviewSummaryText
    {
        get => _fileStackPreviewSummaryText;
        private set => SetProperty(ref _fileStackPreviewSummaryText, value);
    }

    public string FileStackSettingsSummaryText
    {
        get
        {
            if (!FileStacksEnabled)
            {
                return _localizationService.T("Settings.FileStacks.Status.Off");
            }

            if (!FileStackAutoStacking)
            {
                return _localizationService.T("Settings.FileStacks.Status.Manual");
            }

            if (SelectedFileStackGroupBy == SettingsService.FileStackGroupByCustom)
            {
                return _localizationService.Format(
                    "Settings.FileStacks.Status.Custom",
                    FileStackCustomRules.Count);
            }

            // A stable capability summary; the previous group-by display name
            // ("文件类型") read like a stray description on the entry row.
            return _localizationService.T("Settings.FileStacks.Status.On");
        }
    }

    public bool FileStacksEnabled
    {
        get => _fileStacksEnabled;
        set
        {
            if (!SetProperty(ref _fileStacksEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FileStackSettingsSummaryText));
            OnPropertyChanged(nameof(CanAddFileStackCustomRule));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStacksEnabled = value;
            _settingsService.SaveDebounced();
        }
    }

    public bool FileStackAutoStacking
    {
        get => _fileStackAutoStacking;
        set
        {
            if (!SetProperty(ref _fileStackAutoStacking, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FileStackSettingsSummaryText));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackAutoStacking = value;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedFileStackGroupBy
    {
        get => _selectedFileStackGroupBy;
        set
        {
            string normalized = SettingsService.NormalizeFileStackGroupBy(value);
            if (!SetProperty(ref _selectedFileStackGroupBy, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(FileStackCustomRulesVisibility));
            OnPropertyChanged(nameof(FileStackSettingsSummaryText));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackGroupBy = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableFileStackGroupBys { get; } =
    [
        SettingsService.FileStackGroupByKind,
        SettingsService.FileStackGroupByDateModified,
        SettingsService.FileStackGroupByCustom
    ];

    public string[] AvailableFileStackGroupByDisplayNames =>
        _cachedFileStackGroupByDisplayNames ??=
            AvailableFileStackGroupBys.Select(GetFileStackGroupByDisplayName).ToArray();


    public int SelectedFileStackThreshold
    {
        get => _selectedFileStackThreshold;
        set
        {
            int normalized = SettingsService.NormalizeFileStackThreshold(value);
            if (!SetProperty(ref _selectedFileStackThreshold, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackThreshold = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public int[] AvailableFileStackThresholds { get; } = [2, 3, 5];

    public string[] AvailableFileStackThresholdDisplayNames =>
        _cachedFileStackThresholdDisplayNames ??=
            AvailableFileStackThresholds
                .Select(value => _localizationService.Format(
                    "Settings.FileStacks.Threshold.Option",
                    value))
                .ToArray();


    public string SelectedFileStackOrderBy
    {
        get => _selectedFileStackOrderBy;
        set
        {
            string normalized = SettingsService.NormalizeFileStackOrderBy(value);
            if (!SetProperty(ref _selectedFileStackOrderBy, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackOrderBy = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableFileStackOrderBys { get; } =
    [
        SettingsService.FileStackOrderByWidget,
        SettingsService.FileStackOrderByName,
        SettingsService.FileStackOrderByDateAdded,
        SettingsService.FileStackOrderByDateModified
    ];

    public string[] AvailableFileStackOrderByDisplayNames =>
        _cachedFileStackOrderByDisplayNames ??=
            AvailableFileStackOrderBys.Select(GetFileStackOrderByDisplayName).ToArray();

    public string SelectedFileStackOpenMode
    {
        get => _selectedFileStackOpenMode;
        set
        {
            string normalized = SettingsService.NormalizeFileStackOpenMode(value);
            if (!SetProperty(ref _selectedFileStackOpenMode, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackOpenMode = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableFileStackOpenModes { get; } =
    [
        SettingsService.FileStackOpenModeInline,
        SettingsService.FileStackOpenModePopover
    ];

    public string[] AvailableFileStackOpenModeDisplayNames =>
        _cachedFileStackOpenModeDisplayNames ??=
            AvailableFileStackOpenModes
                .Select(GetFileStackOpenModeDisplayName)
                .ToArray();

    public IReadOnlyList<SettingsOption> AvailableFileStackOpenModeOptions =>
        CreateSelectionOptions(
            AvailableFileStackOpenModes,
            AvailableFileStackOpenModeDisplayNames);


    public string[] AvailableFileStackPopoverLayouts { get; } =
    [
        SettingsService.FileStackPopoverLayoutAdaptive,
        SettingsService.FileStackPopoverLayoutGrid3,
        SettingsService.FileStackPopoverLayoutGrid5
    ];

    public string[] AvailableFileStackPopoverLayoutDisplayNames =>
        _cachedFileStackPopoverLayoutDisplayNames ??=
            AvailableFileStackPopoverLayouts
                .Select(GetFileStackPopoverLayoutDisplayName)
                .ToArray();

    public IReadOnlyList<SettingsOption> AvailableFileStackPopoverLayoutOptions =>
        CreateSelectionOptions(
            AvailableFileStackPopoverLayouts,
            AvailableFileStackPopoverLayoutDisplayNames);

    public string SelectedFileStackPopoverLayout
    {
        get => _selectedFileStackPopoverLayout;
        set
        {
            string normalized = SettingsService.NormalizeFileStackPopoverLayout(value);
            if (!SetProperty(ref _selectedFileStackPopoverLayout, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackPopoverLayout = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableFileStackPopoverStyles { get; } =
    [
        SettingsService.FileStackPopoverStyleFollowMaterial,
        SettingsService.FileStackPopoverStyleNeutral
    ];

    public string[] AvailableFileStackPopoverStyleDisplayNames =>
        _cachedFileStackPopoverStyleDisplayNames ??=
            AvailableFileStackPopoverStyles
                .Select(GetFileStackPopoverStyleDisplayName)
                .ToArray();

    public IReadOnlyList<SettingsOption> AvailableFileStackPopoverStyleOptions =>
        CreateSelectionOptions(
            AvailableFileStackPopoverStyles,
            AvailableFileStackPopoverStyleDisplayNames);

    public string SelectedFileStackPopoverStyle
    {
        get => _selectedFileStackPopoverStyle;
        set
        {
            string normalized = SettingsService.NormalizeFileStackPopoverStyle(value);
            if (!SetProperty(ref _selectedFileStackPopoverStyle, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackPopoverStyle = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedFileStackUnmatchedBehavior
    {
        get => _selectedFileStackUnmatchedBehavior;
        set
        {
            string normalized = SettingsService.NormalizeFileStackUnmatchedBehavior(value);
            if (!SetProperty(ref _selectedFileStackUnmatchedBehavior, normalized))
            {
                return;
            }

            RefreshFileStackRulePreview();
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.FileStackUnmatchedBehavior = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableFileStackUnmatchedBehaviors { get; } =
    [
        SettingsService.FileStackUnmatchedKeepLoose,
        SettingsService.FileStackUnmatchedOther
    ];

    public string[] AvailableFileStackUnmatchedBehaviorDisplayNames =>
        _cachedFileStackUnmatchedBehaviorDisplayNames ??=
            AvailableFileStackUnmatchedBehaviors
                .Select(GetFileStackUnmatchedBehaviorDisplayName)
                .ToArray();


    public string GetFileStackGroupByDisplayName(string groupBy) =>
        SettingsService.NormalizeFileStackGroupBy(groupBy) switch
        {
            SettingsService.FileStackGroupByDateAdded =>
                _localizationService.T("Settings.FileStacks.GroupBy.DateAdded"),
            SettingsService.FileStackGroupByDateModified =>
                _localizationService.T("Settings.FileStacks.GroupBy.DateModified"),
            SettingsService.FileStackGroupByCustom =>
                _localizationService.T("Settings.FileStacks.GroupBy.Custom"),
            _ => _localizationService.T("Settings.FileStacks.GroupBy.Kind")
        };

    public string GetFileStackOrderByDisplayName(string orderBy) =>
        SettingsService.NormalizeFileStackOrderBy(orderBy) switch
        {
            SettingsService.FileStackOrderByName =>
                _localizationService.T("Settings.FileStacks.OrderBy.Name"),
            SettingsService.FileStackOrderByDateAdded =>
                _localizationService.T("Settings.FileStacks.OrderBy.DateAdded"),
            SettingsService.FileStackOrderByDateModified =>
                _localizationService.T("Settings.FileStacks.OrderBy.DateModified"),
            _ => _localizationService.T("Settings.FileStacks.OrderBy.Widget")
        };

    public string GetFileStackOpenModeDisplayName(string openMode) =>
        SettingsService.NormalizeFileStackOpenMode(openMode) ==
            SettingsService.FileStackOpenModePopover
                ? _localizationService.T("Settings.FileStacks.OpenMode.Popover")
                : _localizationService.T("Settings.FileStacks.OpenMode.Inline");

    public string GetFileStackPopoverLayoutDisplayName(string layout) =>
        SettingsService.NormalizeFileStackPopoverLayout(layout) switch
        {
            SettingsService.FileStackPopoverLayoutGrid3 =>
                _localizationService.T("Settings.FileStacks.PopoverLayout.Grid3"),
            SettingsService.FileStackPopoverLayoutGrid5 =>
                _localizationService.T("Settings.FileStacks.PopoverLayout.Grid5"),
            _ => _localizationService.T("Settings.FileStacks.PopoverLayout.Adaptive")
        };

    public string GetFileStackPopoverStyleDisplayName(string style) =>
        SettingsService.NormalizeFileStackPopoverStyle(style) ==
            SettingsService.FileStackPopoverStyleFollowMaterial
                ? _localizationService.T("Settings.FileStacks.PopoverStyle.FollowMaterial")
                : _localizationService.T("Settings.FileStacks.PopoverStyle.Neutral");

    public string GetFileStackUnmatchedBehaviorDisplayName(string behavior) =>
        SettingsService.NormalizeFileStackUnmatchedBehavior(behavior) ==
            SettingsService.FileStackUnmatchedOther
                ? _localizationService.T("Settings.FileStacks.Unmatched.Other")
                : _localizationService.T("Settings.FileStacks.Unmatched.KeepLoose");

    public void AddFileStackCustomRule()
    {
        if (!CanAddFileStackCustomRule)
        {
            return;
        }

        int nextNumber = FileStackCustomRules.Count + 1;
        var editor = new FileStackCustomRuleEditor
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = _localizationService.Format(
                "Settings.FileStacks.Custom.DefaultName",
                nextNumber)
        };
        FileStackCustomRules.Add(editor);
    }

    public void RemoveFileStackCustomRule(FileStackCustomRuleEditor editor)
    {
        FileStackCustomRules.Remove(editor);
    }

    public void MoveFileStackCustomRule(FileStackCustomRuleEditor editor, int offset)
    {
        int oldIndex = FileStackCustomRules.IndexOf(editor);
        int newIndex = Math.Clamp(oldIndex + offset, 0, FileStackCustomRules.Count - 1);
        if (oldIndex >= 0 && oldIndex != newIndex)
        {
            FileStackCustomRules.Move(oldIndex, newIndex);
        }
    }

    public void CommitFileStackCustomRuleOrder()
    {
        UpdateFileStackRulePriorities();
        PersistFileStackCustomRules();
    }

    public async Task RefreshFileStackRulePreviewFromDiskAsync()
    {
        int generation = Interlocked.Increment(ref _fileStackPreviewRefreshGeneration);
        FileStackPreviewSummaryText = _localizationService.T(
            "Settings.FileStacks.Custom.Preview.Loading");
        List<FileStackPreviewEntry> entries;
        try
        {
            entries = await Task.Run(
                () => BuildFileStackPreviewEntries(includeMappedFolders: true),
                _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isDisposed || generation != _fileStackPreviewRefreshGeneration)
        {
            return;
        }

        _fileStackPreviewEntries = entries;
        RefreshFileStackRulePreview();
    }

    private void InitializeFileStackSettings(AppSettings settings)
    {
        _fileStacksEnabled = settings.FileStacksEnabled;
        _fileStackAutoStacking = settings.FileStackAutoStacking;
        _selectedFileStackGroupBy = SettingsService.NormalizeFileStackGroupBy(
            settings.FileStackGroupBy);
        _selectedFileStackThreshold = SettingsService.NormalizeFileStackThreshold(
            settings.FileStackThreshold);
        _selectedFileStackOrderBy = SettingsService.NormalizeFileStackOrderBy(
            settings.FileStackOrderBy);
        _selectedFileStackOpenMode = SettingsService.NormalizeFileStackOpenMode(
            settings.FileStackOpenMode);
        _selectedFileStackPopoverLayout =
            SettingsService.NormalizeFileStackPopoverLayout(
                settings.FileStackPopoverLayout);
        _selectedFileStackPopoverStyle =
            SettingsService.NormalizeFileStackPopoverStyle(
                settings.FileStackPopoverStyle);
        _selectedFileStackUnmatchedBehavior =
            SettingsService.NormalizeFileStackUnmatchedBehavior(
                settings.FileStackUnmatchedBehavior);
        ReplaceFileStackCustomRuleEditors(settings.FileStackCustomRules);
        FileStackCustomRules.CollectionChanged += FileStackCustomRules_CollectionChanged;
        _fileStackPreviewEntries = BuildFileStackPreviewEntries(includeMappedFolders: false);
        RefreshFileStackRulePreview();
    }

    private void ApplyFileStackSettingsSnapshot(AppSettings settings)
    {
        FileStacksEnabled = settings.FileStacksEnabled;
        FileStackAutoStacking = settings.FileStackAutoStacking;
        SelectedFileStackGroupBy = SettingsService.NormalizeFileStackGroupBy(
            settings.FileStackGroupBy);
        SelectedFileStackThreshold = SettingsService.NormalizeFileStackThreshold(
            settings.FileStackThreshold);
        SelectedFileStackOrderBy = SettingsService.NormalizeFileStackOrderBy(
            settings.FileStackOrderBy);
        SelectedFileStackOpenMode = SettingsService.NormalizeFileStackOpenMode(
            settings.FileStackOpenMode);
        SelectedFileStackPopoverLayout =
            SettingsService.NormalizeFileStackPopoverLayout(
                settings.FileStackPopoverLayout);
        SelectedFileStackPopoverStyle =
            SettingsService.NormalizeFileStackPopoverStyle(
                settings.FileStackPopoverStyle);
        SelectedFileStackUnmatchedBehavior =
            SettingsService.NormalizeFileStackUnmatchedBehavior(
                settings.FileStackUnmatchedBehavior);
        if (!FileStackCustomRuleEditorsMatch(settings.FileStackCustomRules))
        {
            ReplaceFileStackCustomRuleEditors(settings.FileStackCustomRules);
        }

        RefreshFileStackRulePreview();
    }

    private void RefreshFileStackSelectionProperties()
    {
        _cachedFileStackGroupByDisplayNames = null;
        _cachedFileStackThresholdDisplayNames = null;
        _cachedFileStackOrderByDisplayNames = null;
        _cachedFileStackOpenModeDisplayNames = null;
        _cachedFileStackPopoverLayoutDisplayNames = null;
        _cachedFileStackPopoverStyleDisplayNames = null;
        _cachedFileStackUnmatchedBehaviorDisplayNames = null;
        OnPropertyChanged(nameof(AvailableFileStackGroupByDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackThresholdDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackOrderByDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackOpenModeDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackOpenModeOptions));
        OnPropertyChanged(nameof(SelectedFileStackOpenMode));
        OnPropertyChanged(nameof(AvailableFileStackPopoverLayoutDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackPopoverLayoutOptions));
        OnPropertyChanged(nameof(SelectedFileStackPopoverLayout));
        OnPropertyChanged(nameof(AvailableFileStackPopoverStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableFileStackPopoverStyleOptions));
        OnPropertyChanged(nameof(SelectedFileStackPopoverStyle));
        OnPropertyChanged(nameof(AvailableFileStackUnmatchedBehaviorDisplayNames));
        OnPropertyChanged(nameof(FileStackAutoStacking));
        OnPropertyChanged(nameof(FileStackSettingsSummaryText));
        UpdateFileStackRulePriorities();
        RefreshFileStackRulePreview();
    }

    private void ResetFileStackCustomRules()
    {
        SelectedFileStackUnmatchedBehavior = SettingsService.FileStackUnmatchedKeepLoose;
        ReplaceFileStackCustomRuleEditors([]);
    }

    private void DisposeFileStackSettings()
    {
        FileStackCustomRules.CollectionChanged -= FileStackCustomRules_CollectionChanged;
        foreach (FileStackCustomRuleEditor editor in FileStackCustomRules)
        {
            editor.PropertyChanged -= FileStackCustomRuleEditor_PropertyChanged;
        }
    }

    private void ReplaceFileStackCustomRuleEditors(
        IReadOnlyList<FileStackCustomRule>? rules)
    {
        _isSynchronizingFileStackRules = true;
        try
        {
            foreach (FileStackCustomRuleEditor editor in FileStackCustomRules)
            {
                editor.PropertyChanged -= FileStackCustomRuleEditor_PropertyChanged;
            }

            FileStackCustomRules.Clear();
            foreach (FileStackCustomRule rule in rules ?? [])
            {
                var editor = FileStackCustomRuleEditor.FromModel(rule);
                editor.PropertyChanged += FileStackCustomRuleEditor_PropertyChanged;
                FileStackCustomRules.Add(editor);
            }
        }
        finally
        {
            _isSynchronizingFileStackRules = false;
        }

        OnPropertyChanged(nameof(FileStackRulesEmptyVisibility));
        OnPropertyChanged(nameof(FileStackSettingsSummaryText));
        OnPropertyChanged(nameof(CanAddFileStackCustomRule));
        UpdateFileStackRulePriorities();
    }

    private void FileStackCustomRules_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FileStackCustomRuleEditor editor in e.OldItems)
            {
                editor.PropertyChanged -= FileStackCustomRuleEditor_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FileStackCustomRuleEditor editor in e.NewItems)
            {
                editor.PropertyChanged -= FileStackCustomRuleEditor_PropertyChanged;
                editor.PropertyChanged += FileStackCustomRuleEditor_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(FileStackRulesEmptyVisibility));
        OnPropertyChanged(nameof(FileStackSettingsSummaryText));
        OnPropertyChanged(nameof(CanAddFileStackCustomRule));
        UpdateFileStackRulePriorities();
        RefreshFileStackRulePreview();
        if (!_isSynchronizingFileStackRules)
        {
            PersistFileStackCustomRules();
        }
    }

    private void FileStackCustomRuleEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(FileStackCustomRuleEditor.Name) and
            not nameof(FileStackCustomRuleEditor.ExtensionsText))
        {
            return;
        }

        RefreshFileStackRulePreview();
        if (!_isSynchronizingFileStackRules)
        {
            PersistFileStackCustomRules();
        }
    }

    private void PersistFileStackCustomRules()
    {
        _settingsService.Settings.FileStackCustomRules = FileStackCustomRules
            .Select(editor => editor.ToModel())
            .ToList();
        _settingsService.SaveDebounced();
    }

    private bool FileStackCustomRuleEditorsMatch(
        IReadOnlyList<FileStackCustomRule>? rules)
    {
        var normalizedRules = rules ?? [];
        if (FileStackCustomRules.Count != normalizedRules.Count)
        {
            return false;
        }

        for (int index = 0; index < FileStackCustomRules.Count; index++)
        {
            FileStackCustomRule editorRule = FileStackCustomRules[index].ToModel();
            FileStackCustomRule modelRule = normalizedRules[index];
            if (!string.Equals(editorRule.Id, modelRule.Id, StringComparison.Ordinal) ||
                !string.Equals(editorRule.Name, modelRule.Name?.Trim(), StringComparison.Ordinal) ||
                !editorRule.Extensions.SequenceEqual(
                    SettingsService.NormalizeFileStackExtensions(modelRule.Extensions),
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateFileStackRulePriorities()
    {
        for (int index = 0; index < FileStackCustomRules.Count; index++)
        {
            FileStackCustomRuleEditor editor = FileStackCustomRules[index];
            editor.PriorityText = _localizationService.Format(
                "Settings.FileStacks.Custom.Priority",
                index + 1);
            editor.CanMoveUp = index > 0;
            editor.CanMoveDown = index < FileStackCustomRules.Count - 1;
        }
    }

    private void RefreshFileStackRulePreview()
    {
        IReadOnlyList<FileStackPreviewEntry> entries = _fileStackPreviewEntries;
        var unmatched = new List<FileStackPreviewEntry>(entries);
        int totalMatched = 0;

        foreach (FileStackCustomRuleEditor editor in FileStackCustomRules)
        {
            var extensions = FileStackCustomRuleEditor.ParseExtensions(editor.ExtensionsText)
                .Take(SettingsService.MaxFileStackExtensionsPerRule)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (extensions.Count == 0)
            {
                editor.PreviewText = _localizationService.T(
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
                editor.PreviewText = _localizationService.T(
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

            editor.PreviewText = _localizationService.Format(
                "Settings.FileStacks.Custom.Preview.Matches",
                matches.Count,
                samples);
        }

        FileStackPreviewSummaryText = _localizationService.Format(
            "Settings.FileStacks.Custom.Preview.Summary",
            totalMatched,
            unmatched.Count);
    }

    private List<FileStackPreviewEntry> BuildFileStackPreviewEntries(
        bool includeMappedFolders)
    {
        var entries = new List<FileStackPreviewEntry>();
        foreach (WidgetConfig widget in _settingsService.Settings.Widgets.Where(
                     widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            IEnumerable<string> paths = widget.Items.Select(item => item.Path);
            if (includeMappedFolders &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                FileService.TryResolveExistingPathForTraversal(
                    widget.MappedFolderPath,
                    out string mappedFolderTraversalPath))
            {
                paths = EnumerateFileStackPreviewPaths(mappedFolderTraversalPath);
            }

            foreach (string path in paths
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileStackPreviewEntry(
                    widget.Name,
                    path,
                    Directory.Exists(path) ? string.Empty : Path.GetExtension(path)));
            }
        }

        return entries;
    }

    private static IEnumerable<string> EnumerateFileStackPreviewPaths(string folderPath)
    {
        var folders = new List<string> { folderPath };
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(publicDesktop))
        {
            folders.Add(publicDesktop);
        }

        var paths = new List<string>();
        foreach (string folder in folders)
        {
            try
            {
                paths.AddRange(Directory
                    .EnumerateFileSystemEntries(folder)
                    .Where(IsVisibleFileStackPreviewPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return paths
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static bool IsVisibleFileStackPreviewPath(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record FileStackPreviewEntry(
        string WidgetName,
        string Path,
        string Extension);
}
