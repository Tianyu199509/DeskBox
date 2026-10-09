using System.Text.RegularExpressions;
using DeskBox.Contracts;
using DeskBox.Features.Todo;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Seventh copy batch of the settings-shell binding-facade retirement: the
/// Todo section (29 unique binding properties) is re-bound to the batch-1
/// section editor grown into a full binding surface through a section-level
/// DataContext switch. These tests pin the editor's behavior (read snapshot
/// projection with the old shell normalization, write-through, no write-back
/// on external sync, the batch-14 default-filter/tab linkage rendered
/// through the coordinator's snapshot re-projection, the tab flyout state
/// machine, text-size step normalization and commit events, the inherited
/// zero-override text sizes, the derived gates/summaries, localization
/// refresh, the enable chain's pending projection) and the migration
/// pattern itself (XAML paths, AOT bridges, window wiring, facade removal).
/// </summary>
public sealed class TodoSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    private static readonly Func<string, object[], string> PassthroughFormat =
        static (key, args) => key + ":" + string.Join("|", args);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, TodoSettingsCoordinator Coordinator, TodoSettingsViewModel Editor)
        CreateEditor(string root, Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new TodoSettingsCoordinator(settings);
        return (settings, coordinator, new TodoSettingsViewModel(
            coordinator, PassthroughLocalize, PassthroughFormat, _ => { }));
    }

    [Fact]
    public void Constructor_ProjectsPersistedStateFromTheReadSnapshot()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                FeatureWidgetSettings.SetEnabled(
                    settings.Settings, WidgetKind.Todo, true);
                TodoSettingsSlice slice = settings.Settings.Todo;
                slice.TodoLayoutMode = TodoOptionKinds.LayoutModeDualPane;
                slice.TodoAutoSelectFirstInWideLayout = false;
                slice.TodoShowTabBar = false;
                slice.TodoShowActiveTab = true;
                slice.TodoDefaultFilter = TodoOptionKinds.DefaultFilterActive;
                slice.TodoTabStyle = SettingsService.WidgetTabStylePivot;
                slice.TodoShowCompletedTasks = true;
                slice.TodoShowFooterStats = true;
                slice.TodoShowClearCompletedButton = false;
                slice.TodoItemPreviewLineCount = 6;
                slice.TodoNewTaskPosition = TodoOptionKinds.NewTaskPositionBottom;
                slice.TodoEditorEnterBehavior =
                    QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
                slice.TodoListTextSize = 13.5;
                slice.TodoContentTextSize = 12.5;
                settings.Settings.Todo.TodoReminderEnabled = false;
                settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
            });

        Assert.True(editor.Enabled);
        Assert.True(editor.ShowWideOptions);
        Assert.False(editor.AutoSelectFirstInWideLayout);
        Assert.Equal(TodoOptionKinds.LayoutModeDualPane, editor.LayoutMode);
        Assert.False(editor.ShowTabBar);
        Assert.True(editor.IsTabSelected(TodoOptionKinds.DefaultFilterActive));
        Assert.Equal(TodoOptionKinds.DefaultFilterActive, editor.DefaultFilter);
        Assert.Equal(0, editor.TabStyleIndex);
        Assert.Equal(SettingsService.WidgetTabStylePivot, editor.TabStyle);
        Assert.True(editor.ShowCompletedTasks);
        Assert.True(editor.ShowFooterStats);
        Assert.False(editor.ShowClearCompletedButton);
        Assert.Equal(6, editor.ItemPreviewLineCount);
        Assert.Equal(TodoOptionKinds.NewTaskPositionBottom, editor.NewTaskPosition);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves,
            editor.EditorEnterBehavior);
        Assert.Equal(13.5, editor.ListTextSize);
        Assert.Equal(12.5, editor.ContentTextSize);
        Assert.False(editor.RemindersEnabled);
        Assert.Equal(30, editor.DefaultOffsetMinutes);
    }

    [Fact]
    public void Constructor_NormalizesUnknownValuesLikeTheOldShell()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                TodoSettingsSlice slice = settings.Settings.Todo;
                slice.TodoLayoutMode = "bogus";
                slice.TodoDefaultFilter = "bogus";
                slice.TodoNewTaskPosition = "bogus";
                slice.TodoEditorEnterBehavior = "bogus";
                slice.TodoItemPreviewLineCount = 99;
                slice.TodoTabStyle = "bogus";
            });

        Assert.Equal(TodoOptionKinds.LayoutModeAuto, editor.LayoutMode);
        Assert.Equal(TodoOptionKinds.DefaultFilterAll, editor.DefaultFilter);
        Assert.Equal(TodoOptionKinds.NewTaskPositionTop, editor.NewTaskPosition);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves,
            editor.EditorEnterBehavior);
        Assert.Equal(10, editor.ItemPreviewLineCount);
        Assert.Equal(SettingsService.WidgetTabStyleButton, editor.TabStyle);
        Assert.Equal(1, editor.TabStyleIndex);
    }

    [Fact]
    public void EditorWrites_PersistThroughTheCoordinator()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) =
            CreateEditor(_root);

        editor.LayoutMode = TodoOptionKinds.LayoutModeSinglePane;
        editor.AutoSelectFirstInWideLayout = false;
        editor.ShowTabBar = false;
        editor.DefaultFilter = TodoOptionKinds.DefaultFilterActive;
        editor.TabStyleIndex = 0;
        editor.ShowCompletedTasks = true;
        editor.ShowFooterStats = true;
        editor.ShowClearCompletedButton = false;
        editor.ItemPreviewLineCount = 4;
        editor.NewTaskPosition = TodoOptionKinds.NewTaskPositionBottom;
        editor.EditorEnterBehavior =
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
        editor.RemindersEnabled = false;
        editor.DefaultOffsetMinutes = 15;

        TodoSettingsSlice slice = settings.Settings.Todo;
        Assert.Equal(TodoOptionKinds.LayoutModeSinglePane, slice.TodoLayoutMode);
        Assert.False(slice.TodoAutoSelectFirstInWideLayout);
        Assert.False(slice.TodoShowTabBar);
        Assert.Equal(TodoOptionKinds.DefaultFilterActive, slice.TodoDefaultFilter);
        Assert.Equal(SettingsService.WidgetTabStylePivot, slice.TodoTabStyle);
        Assert.True(slice.TodoShowCompletedTasks);
        Assert.True(slice.TodoShowFooterStats);
        Assert.False(slice.TodoShowClearCompletedButton);
        Assert.Equal(4, slice.TodoItemPreviewLineCount);
        Assert.Equal(TodoOptionKinds.NewTaskPositionBottom, slice.TodoNewTaskPosition);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves,
            slice.TodoEditorEnterBehavior);
        Assert.False(slice.TodoReminderEnabled);
        Assert.Equal(15, slice.TodoDefaultReminderOffsetMinutes);
    }

    [Fact]
    public void ExternalSync_ReprojectsWithoutWritingBack()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) =
            CreateEditor(_root);

        settings.Settings.Todo.TodoLayoutMode =
            TodoOptionKinds.LayoutModeSinglePane;
        editor.Refresh();

        Assert.Equal(TodoOptionKinds.LayoutModeSinglePane, editor.LayoutMode);
        Assert.False(editor.ShowWideOptions);
        Assert.Equal(TodoOptionKinds.LayoutModeSinglePane, settings.Settings.Todo.TodoLayoutMode);
        // A coordinator-side tab change re-projects without an editor write.
        settings.Settings.Todo.TodoShowTabBar = false;
        editor.Refresh();
        Assert.False(editor.ShowTabBar);
        Assert.Equal(
            PassthroughLocalize("Settings.Toggle.Off"),
            editor.TabsSummaryText);
    }

    [Fact]
    public void TabLinkage_PickingHiddenDefaultFilter_RevealsItsTab()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) =
            CreateEditor(
                _root,
                settings2 =>
                    settings2.Settings.Todo.TodoShowImportantTab = false);

        Assert.False(editor.IsTabSelected(TodoOptionKinds.DefaultFilterImportant));
        Assert.DoesNotContain(
            TodoOptionKinds.DefaultFilterImportant,
            editor.VisibleDefaultFilterOptions.Select(option => (string)option.Value!));

        editor.DefaultFilter = TodoOptionKinds.DefaultFilterImportant;

        // The coordinator reveals the hidden target tab (batch 14) and the
        // write path's re-projection renders it.
        Assert.True(settings.Settings.Todo.TodoShowImportantTab);
        Assert.True(editor.IsTabSelected(TodoOptionKinds.DefaultFilterImportant));
        Assert.Equal(
            TodoOptionKinds.DefaultFilterImportant,
            editor.DefaultFilter);
        Assert.Contains(
            TodoOptionKinds.DefaultFilterImportant,
            editor.VisibleDefaultFilterOptions.Select(option => (string)option.Value!));
    }

    [Fact]
    public void TabLinkage_HidingTheDefaultFilterTab_FallsTheFilterBack()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) =
            CreateEditor(
                _root,
                settings2 =>
                {
                    settings2.Settings.Todo.TodoDefaultFilter =
                        TodoOptionKinds.DefaultFilterImportant;
                    settings2.Settings.Todo.TodoShowTabBar = true;
                    settings2.Settings.Todo.TodoShowImportantTab = true;
                    settings2.Settings.Todo.TodoShowTodayTab = true;
                });

        Assert.Equal(TodoOptionKinds.DefaultFilterImportant, editor.DefaultFilter);

        // Hiding the default filter's tab falls the filter back to the
        // first visible one (All is visible by default) through the
        // coordinator's normalization (batch 14).
        editor.ToggleTab(TodoOptionKinds.DefaultFilterImportant);

        Assert.False(settings.Settings.Todo.TodoShowImportantTab);
        Assert.Equal(TodoOptionKinds.DefaultFilterAll, editor.DefaultFilter);
        Assert.Equal(TodoOptionKinds.DefaultFilterAll, settings.Settings.Todo.TodoDefaultFilter);
    }

    [Fact]
    public void TabFlyout_GuardsTheLastVisibleTabAndDrivesDerivedSurfaces()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(_root);

        // Defaults: All/Today/Important/Completed are visible.
        Assert.True(editor.CanToggleTab(TodoOptionKinds.DefaultFilterToday));
        Assert.Equal(4, editor.VisibleDefaultFilterOptions.Count);
        Assert.Contains(
            TodoOptionKinds.DefaultFilterAll,
            editor.VisibleDefaultFilterOptions.Select(option => (string)option.Value!));
        Assert.DoesNotContain(
            TodoOptionKinds.DefaultFilterThisWeek,
            editor.VisibleDefaultFilterOptions.Select(option => (string)option.Value!));

        // Drop down to a single visible tab; the last one is protected.
        editor.ToggleTab(TodoOptionKinds.DefaultFilterImportant);
        editor.ToggleTab(TodoOptionKinds.DefaultFilterToday);
        editor.ToggleTab(TodoOptionKinds.DefaultFilterCompleted);
        Assert.True(editor.IsTabSelected(TodoOptionKinds.DefaultFilterAll));
        Assert.Equal(1, editor.VisibleDefaultFilterOptions.Count);
        editor.ToggleTab(TodoOptionKinds.DefaultFilterAll);
        Assert.True(editor.IsTabSelected(TodoOptionKinds.DefaultFilterAll));
        Assert.False(editor.CanToggleTab(TodoOptionKinds.DefaultFilterAll));

        string joined = editor.VisibleTabsText;
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.DefaultFilter.All"), joined);
        Assert.DoesNotContain(
            PassthroughLocalize("Settings.Todo.DefaultFilter.Today"), joined);
        Assert.DoesNotContain(
            PassthroughLocalize("Settings.Todo.DefaultFilter.Important"), joined);
    }

    [Fact]
    public void TextSizes_SnapToTheHalfPointGridAndRaiseCommitOnlyOnStoredWrites()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) = CreateEditor(_root);
        int commits = 0;
        editor.ListTextSizeCommitted += () => commits++;
        editor.ContentTextSizeCommitted += () => commits++;

        // Off-grid input snaps back onto the grid and commits through the
        // re-entry (the old shell's two-pass behavior).
        editor.ListTextSize = 12.34;
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(1, commits);
        Assert.Equal(12.5, settings.Settings.Todo.TodoListTextSize);

        // Equal re-writes do not commit again.
        editor.ListTextSize = 12.5;
        Assert.Equal(1, commits);

        editor.ContentTextSize = 13.0;
        Assert.Equal(2, commits);
        Assert.Equal(13.0, settings.Settings.Todo.TodoContentTextSize);
        Assert.Equal($"{12.5:0.#}pt", editor.ListTextSizeValueText);
    }

    [Fact]
    public void TextSizes_StoredZeroInheritsTheGlobalSize()
    {
        (SettingsService settings, _, TodoSettingsViewModel editor) =
            CreateEditor(
                _root,
                settings2 => settings2.Settings.WidgetShell.TextSize = 12.5);

        // Stored zeros inherit the global appearance size (batch 22).
        Assert.Equal(0d, settings.Settings.Todo.TodoListTextSize);
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(12.5, editor.ContentTextSize);

        // A global-size change re-projects through the coordinator snapshot.
        settings.Settings.WidgetShell.TextSize = 14.5;
        editor.Refresh();
        Assert.Equal(14.5, editor.ListTextSize);
        Assert.Equal(14.5, editor.ContentTextSize);
        // The raw override stays zero (still inheriting).
        Assert.Equal(0d, settings.Settings.Todo.TodoListTextSize);
    }

    [Fact]
    public void DerivedGatesAndSummaries_TrackThePersistedProjection()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(_root);

        Assert.True(editor.ShowWideOptions);
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.LayoutMode.Auto"),
            editor.LayoutSummaryText);
        editor.LayoutMode = TodoOptionKinds.LayoutModeSinglePane;
        Assert.False(editor.ShowWideOptions);
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.LayoutMode.SinglePane"),
            editor.LayoutSummaryText);
        editor.LayoutMode = TodoOptionKinds.LayoutModeDualPane;
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.LayoutMode.DualPane"),
            editor.LayoutSummaryText);

        Assert.True(editor.RemindersEnabled);
        // The default offset (5 minutes) renders through the minutes format.
        Assert.Equal(
            PassthroughFormat("Settings.Todo.ReminderOffset.Minutes", [5]),
            editor.ReminderSummaryText);
        editor.DefaultOffsetMinutes = 60;
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.ReminderOffset.OneHour"),
            editor.ReminderSummaryText);
        editor.RemindersEnabled = false;
        Assert.Equal(
            PassthroughLocalize("Settings.Toggle.Off"),
            editor.ReminderSummaryText);

        Assert.Equal(
            string.Join(
                " · ",
                PassthroughFormat(
                    "Settings.ContentEditor.PreviewLines.Option.Multiple", [2]),
                PassthroughLocalize("Settings.Todo.NewTaskPosition.Top"),
                PassthroughLocalize("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves")),
            editor.ContentSummaryText);

        // The footer summary lists the enabled options and falls back to
        // Off (defaults: stats off, clear-completed on).
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.ShowClearCompleted.Title"),
            editor.FooterDisplaySummaryText);
        editor.ToggleFooterDisplayOption("Stats");
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.ShowFooterStats.Title") + " · " +
            PassthroughLocalize("Settings.Todo.ShowClearCompleted.Title"),
            editor.FooterDisplaySummaryText);
        Assert.True(editor.IsFooterDisplayOptionSelected("Stats"));
        Assert.True(editor.IsFooterDisplayOptionSelected("ClearCompleted"));
        editor.ToggleFooterDisplayOption("Stats");
        editor.ToggleFooterDisplayOption("ClearCompleted");
        Assert.Equal(
            PassthroughLocalize("Settings.Toggle.Off"),
            editor.FooterDisplaySummaryText);
    }

    [Fact]
    public void OptionTables_OfferCanonicalValuesWithLocalizedNames()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(_root);

        Assert.Equal(
            new[]
            {
                TodoOptionKinds.LayoutModeAuto,
                TodoOptionKinds.LayoutModeSinglePane,
                TodoOptionKinds.LayoutModeDualPane
            },
            editor.AvailableLayoutOptions.Select(option => (string)option.Value!).ToArray());
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.LayoutMode.DualPane"),
            editor.AvailableLayoutOptions[2].DisplayName);
        Assert.Equal(2, editor.AvailableNewTaskPositionOptions.Count);
        Assert.Equal(2, editor.AvailableEnterBehaviorOptions.Count);
        Assert.Equal(10, editor.AvailablePreviewLineOptions.Count);
        Assert.Equal(7, editor.AvailableReminderOffsetOptions.Count);
        Assert.Equal(7, editor.AvailableDefaultFilters.Length);
        Assert.Equal(
            new[] { "Stats", "ClearCompleted" },
            editor.AvailableFooterDisplayOptions);
    }

    [Fact]
    public void RefreshLocalization_RebuildsCachesAndDerivations()
    {
        (_, _, TodoSettingsViewModel editor) = CreateEditor(_root);
        SettingsOption firstBefore = editor.AvailableLayoutOptions[0];

        editor.RefreshLocalization();

        Assert.NotSame(firstBefore, editor.AvailableLayoutOptions[0]);
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.LayoutMode.Auto"),
            editor.AvailableLayoutOptions[0].DisplayName);
        Assert.Equal(
            PassthroughLocalize("Settings.Todo.DefaultFilter.All"),
            editor.GetTabDisplayName(TodoOptionKinds.DefaultFilterAll));
    }

    [Fact]
    public async Task EnableChain_KeepsTheUserChoiceVisibleUntilTheTransitionCompletes()
    {
        bool? committed = null;
        var settings = new SettingsService(_root);
        await settings.SaveAsync();
        await settings.LoadAsync();
        var coordinator = new TodoSettingsCoordinator(
            settings,
            setWidgetEnabled: async enabled =>
            {
                committed = enabled;
                FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, enabled);
                await Task.Delay(50);
            });
        using var editor = new TodoSettingsViewModel(
            coordinator, PassthroughLocalize, PassthroughFormat, _ => { });

        editor.Enabled = true;
        // The pending transition keeps the user's choice projected.
        Assert.True(editor.Enabled);
        await editor.PendingChange;

        Assert.True(committed);
        Assert.True(FeatureWidgetSettings.IsEnabled(settings.Settings, WidgetKind.Todo));
        Assert.True(editor.Enabled);
        await coordinator.StopAsync();
    }

    [Fact]
    public void ShellReflects_NoTodoFacadePropertiesRemain()
    {
        string[] removed =
        [
            "TodoEnabled",
            "TodoReminderEnabled",
            "TodoShowTabBar",
            "TodoShowAllTab",
            "TodoShowActiveTab",
            "TodoShowTodayTab",
            "TodoShowThisWeekTab",
            "TodoShowThisMonthTab",
            "TodoShowImportantTab",
            "TodoShowCompletedTab",
            "TodoShowCompletedTasks",
            "TodoShowFooterStats",
            "TodoShowClearCompletedButton",
            "TodoUseWideDetailPane",
            "TodoAutoSelectFirstInWideLayout",
            "TodoListTextSize",
            "TodoContentTextSize",
            "TodoListTextSizeValueText",
            "TodoContentTextSizeValueText",
            "SelectedTodoLayoutMode",
            "SelectedTodoNewTaskPosition",
            "SelectedTodoNewTaskPositionText",
            "SelectedTodoDefaultFilter",
            "SelectedTodoDefaultFilterText",
            "SelectedTodoTabStyle",
            "SelectedTodoTabStyleText",
            "SelectedTodoReminderOffsetMinutes",
            "SelectedTodoReminderOffsetMinutesText",
            "TodoItemPreviewLineCount",
            "TodoEditorEnterBehavior",
            "TodoVisibleTabsText",
            "TodoTabsSummaryText",
            "TodoLayoutSummaryText",
            "TodoContentSummaryText",
            "TodoReminderSummaryText",
            "TodoFooterDisplaySummaryText",
            "TodoWideOptionsVisibility",
            "TodoTabStyleIndex",
            "VisibleTodoDefaultFilterOptions",
            "AvailableTodoLayoutModeOptions",
            "AvailableTodoLayoutModes",
            "AvailableTodoNewTaskPositionOptions",
            "AvailableTodoNewTaskPositions",
            "AvailableTodoNewTaskPositionDisplayNames",
            "AvailableTodoDefaultFilterOptions",
            "AvailableTodoDefaultFilters",
            "AvailableTodoDefaultFilterDisplayNames",
            "AvailableTodoTabStyleOptions",
            "AvailableTodoTabStyleDisplayNames",
            "AvailableTodoReminderOffsetOptions",
            "AvailableTodoReminderOffsetMinutes",
            "AvailableTodoReminderOffsetDisplayNames",
            "AvailableItemPreviewLineCountOptions",
            "AvailableItemPreviewLineCounts",
            "AvailableItemPreviewLineCountDisplayNames",
            "AvailableEditorEnterBehaviorOptions",
            "AvailableEditorEnterBehaviors",
            "AvailableEditorEnterBehaviorDisplayNames",
            "GetTodoNewTaskPositionDisplayName",
            "GetTodoLayoutModeDisplayName",
            "GetTodoDefaultFilterDisplayName",
            "GetTodoReminderOffsetDisplayName",
            "GetWidgetTabStyleDisplayName",
            "GetEditorEnterBehaviorDisplayName",
            "GetItemPreviewLineCountDisplayName",
            "GetTodoTabDisplayName",
            "IsTodoTabSelected",
            "CanToggleTodoTab",
            "ToggleTodoTab",
            "IsTodoFooterDisplayOptionSelected",
            "ToggleTodoFooterDisplayOption",
            "GetTodoFooterDisplayOptionName"
        ];
        HashSet<string> publicMembers = new(
            typeof(SettingsViewModel)
                .GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Select(member => member.Name),
            StringComparer.Ordinal);
        foreach (string name in removed)
        {
            Assert.DoesNotContain(name, publicMembers);
        }
    }

    [Fact]
    public void MigrationPattern_XamlBridgesAndWindowWiringStayPinned()
    {
        string windowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string navigation = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Navigation.cs"));
        string bridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/Todo/TodoSettingsViewModel.AotBindableProperties.cs"));
        string bindableShell = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));

        // Section-level DataContext switch; the template stays {Binding}-only
        // (no x:Bind, so its x:DataType keeps pointing at the shell type).
        Assert.Contains(
            "IsOn=\"{Binding Enabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding RemindersEnabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding DefaultFilter, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding VisibleDefaultFilterOptions}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{Binding ListTextSize, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedIndex=\"{Binding TabStyleIndex, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowWideOptions, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding DefaultOffsetMinutes, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailablePreviewLineOptions}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Todo", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Binding SelectedTodo", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Binding AvailableTodo", windowXaml, StringComparison.Ordinal);

        Assert.Contains(
            "section.DataContext = _todoSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "todoSettings.AvailableDefaultFilters,",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "var todoSettings = _todoSettingsViewModel;",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "todoSettings.AvailableFooterDisplayOptions,",
            navigation,
            StringComparison.Ordinal);

        // The editor's {Binding} bridge and the shrunken shell bridge.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(Enabled)", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(DefaultFilter)", bridge, StringComparison.Ordinal);
        Assert.Equal(29, Regex.Matches(bridge, @"nameof\(").Count);
        Assert.DoesNotContain("nameof(ShowAllTab)", bridge, StringComparison.Ordinal);
        Assert.Equal(33, Regex.Matches(bindableShell, @"nameof\(").Count);
    }
}
