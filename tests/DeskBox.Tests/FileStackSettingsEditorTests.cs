using System.Text.RegularExpressions;
using DeskBox.Contracts;
using DeskBox.Features.FeatureWidgets;
using DeskBox.Features.FileStack;
using DeskBox.Features.Interaction;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Fifth copy batch of the settings-shell binding-facade retirement: the
/// file-stack section is re-bound to its editor through a section-level
/// DataContext switch (its compiled x:Bind surface — the open-mode combo and
/// the rule list — is re-typed to the editor), and the file-widget overview
/// page swaps its single shell-facade dependency property for three typed
/// editor dependencies (file-stack, feature-widgets, interaction), the
/// blueprint-approved x:Bind bridge-free exception deferred from batch 42.
/// These tests pin the editors' behavior (read snapshot projection with the
/// old shell normalization, write-through with unchanged-write skip, no
/// write-back on external sync, the custom-rule aggregation's single write
/// entry, the shell-pushed preview entries, the derived gates and summary,
/// localization refresh, the overview editors' surfaces) and the migration
/// pattern itself (XAML paths, AOT bridges, window wiring, facade removal).
/// </summary>
public sealed class FileStackSettingsEditorTests : IDisposable
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

    private static (SettingsService Settings, FileStackSettingsCoordinator Coordinator, FileStackSettingsViewModel Editor)
        CreateEditor(string root, Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new FileStackSettingsCoordinator(settings);
        return (settings, coordinator, new FileStackSettingsViewModel(
            coordinator, PassthroughLocalize, PassthroughFormat));
    }

    private static (SettingsService Settings, FeatureWidgetsSettingsCoordinator Coordinator, FeatureWidgetsSettingsViewModel Editor)
        CreateFeatureWidgetsEditor(string root)
    {
        var settings = new SettingsService(root);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        return (settings, coordinator, new FeatureWidgetsSettingsViewModel(
            coordinator, PassthroughLocalize));
    }

    [Fact]
    public void Constructor_ProjectsPersistedStateFromTheReadSnapshot()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                FileWidgetSettingsSlice fileWidget = settings.Settings.FileWidget;
                fileWidget.FileStacksEnabled = false;
                fileWidget.FileStackAutoStacking = true;
                fileWidget.FileStackGroupBy = FileStackOptionKinds.GroupByCustom;
                fileWidget.FileStackThreshold = 5;
                fileWidget.FileStackOrderBy = FileStackOptionKinds.OrderByDateModified;
                fileWidget.FileStackOpenMode = FileStackOptionKinds.OpenModePopover;
                fileWidget.FileStackPopoverLayout = FileStackOptionKinds.PopoverLayoutGrid5;
                fileWidget.FileStackPopoverStyle = FileStackOptionKinds.PopoverStyleFollowMaterial;
                fileWidget.FileStackUnmatchedBehavior = FileStackOptionKinds.UnmatchedOther;
                fileWidget.FileStackCustomRules =
                [
                    new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = [".pdf"] }
                ];
            });

        Assert.False(editor.StacksEnabled);
        Assert.True(editor.AutoStacking);
        Assert.Equal(FileStackOptionKinds.GroupByCustom, editor.GroupBy);
        Assert.Equal(5, editor.Threshold);
        Assert.Equal(FileStackOptionKinds.OrderByDateModified, editor.OrderBy);
        Assert.Equal(FileStackOptionKinds.OpenModePopover, editor.OpenMode);
        Assert.Equal(FileStackOptionKinds.PopoverLayoutGrid5, editor.PopoverLayout);
        Assert.Equal(FileStackOptionKinds.PopoverStyleFollowMaterial, editor.PopoverStyle);
        Assert.Equal(FileStackOptionKinds.UnmatchedOther, editor.UnmatchedBehavior);
        Assert.Single(editor.CustomRules);
        Assert.Equal("Docs", editor.CustomRules[0].Name);
        Assert.True(editor.ShowCustomRules);
        Assert.False(editor.HasNoRules);
        // The master switch is off in this preset, so the add-rule action is
        // gated exactly like the legacy shell's CanAddFileStackCustomRule.
        Assert.False(editor.CanAddRule);
    }

    [Fact]
    public void Constructor_NormalizesInvalidPersistedValuesLikeTheLegacyShellDid()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                FileWidgetSettingsSlice fileWidget = settings.Settings.FileWidget;
                fileWidget.FileStackGroupBy = "Nonsense";
                fileWidget.FileStackThreshold = 7;
                fileWidget.FileStackOrderBy = null;
                fileWidget.FileStackOpenMode = "Sidecar";
                fileWidget.FileStackPopoverLayout = "Grid4";
                fileWidget.FileStackPopoverStyle = string.Empty;
                fileWidget.FileStackUnmatchedBehavior = "Discard";
            });

        Assert.Equal(FileStackOptionKinds.GroupByKind, editor.GroupBy);
        Assert.Equal(FileStackOptionKinds.DefaultThreshold, editor.Threshold);
        Assert.Equal(FileStackOptionKinds.OrderByWidget, editor.OrderBy);
        Assert.Equal(FileStackOptionKinds.OpenModeInline, editor.OpenMode);
        Assert.Equal(FileStackOptionKinds.PopoverLayoutAdaptive, editor.PopoverLayout);
        Assert.Equal(FileStackOptionKinds.PopoverStyleNeutral, editor.PopoverStyle);
        Assert.Equal(FileStackOptionKinds.UnmatchedKeepLoose, editor.UnmatchedBehavior);
    }

    [Fact]
    public void UserEdits_WriteThroughTheCoordinatorAndSkipUnchangedWrites()
    {
        (SettingsService settings, _, FileStackSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.StacksEnabled = false;
        editor.AutoStacking = true;
        editor.GroupBy = FileStackOptionKinds.GroupByCustom;
        editor.Threshold = 2;
        editor.OrderBy = FileStackOptionKinds.OrderByName;
        editor.OpenMode = FileStackOptionKinds.OpenModePopover;
        editor.PopoverLayout = FileStackOptionKinds.PopoverLayoutAdaptive;
        editor.PopoverStyle = FileStackOptionKinds.PopoverStyleFollowMaterial;
        editor.UnmatchedBehavior = FileStackOptionKinds.UnmatchedOther;

        FileWidgetSettingsSlice slice = settings.Settings.FileWidget;
        Assert.False(slice.FileStacksEnabled);
        Assert.True(slice.FileStackAutoStacking);
        Assert.Equal(FileStackOptionKinds.GroupByCustom, slice.FileStackGroupBy);
        Assert.Equal(2, slice.FileStackThreshold);
        Assert.Equal(FileStackOptionKinds.OrderByName, slice.FileStackOrderBy);
        Assert.Equal(FileStackOptionKinds.OpenModePopover, slice.FileStackOpenMode);
        Assert.Equal(FileStackOptionKinds.PopoverLayoutAdaptive, slice.FileStackPopoverLayout);
        Assert.Equal(FileStackOptionKinds.PopoverStyleFollowMaterial, slice.FileStackPopoverStyle);
        Assert.Equal(FileStackOptionKinds.UnmatchedOther, slice.FileStackUnmatchedBehavior);
        Assert.Equal(9, notified);

        editor.StacksEnabled = false;
        editor.GroupBy = FileStackOptionKinds.GroupByCustom;
        editor.UnmatchedBehavior = FileStackOptionKinds.UnmatchedOther;
        Assert.Equal(9, notified);
    }

    [Fact]
    public void ExternalSync_ReProjectsTheSnapshotWithoutWritingBack()
    {
        (SettingsService settings, FileStackSettingsCoordinator coordinator, FileStackSettingsViewModel editor) =
            CreateEditor(
                _root,
                settings => settings.Settings.FileWidget.FileStackCustomRules =
                [
                    new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = [".pdf"] }
                ]);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetFileStacksEnabled(false);
        coordinator.SetFileStackGroupBy(FileStackOptionKinds.GroupByCustom);
        int writesBeforeSync = notified;

        editor.SyncPresentation();
        Assert.False(editor.StacksEnabled);
        Assert.Equal(FileStackOptionKinds.GroupByCustom, editor.GroupBy);
        Assert.Equal(writesBeforeSync, notified);

        // A snapshot with equivalent rules must not rebuild the editor
        // collection (focus preservation of the legacy shell).
        DeskBox.ViewModels.FileStackCustomRuleEditor current = editor.CustomRules[0];
        editor.SyncPresentation();
        Assert.Same(current, editor.CustomRules[0]);
    }

    [Fact]
    public void CustomRuleAggregation_PersistsAddsEditsMovesAndRemovalsThroughOneWriteEntry()
    {
        (SettingsService settings, _, FileStackSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.AddRule();
        Assert.Single(editor.CustomRules);
        Assert.Equal(1, notified);
        Assert.Equal(
            "Settings.FileStacks.Custom.DefaultName:1",
            editor.CustomRules[0].Name);

        editor.CustomRules[0].Name = "Design";
        editor.CustomRules[0].ExtensionsText = "*.PSD psd .Ai";
        // AddRule plus the two per-rule edits each saved exactly once.
        Assert.Equal(3, notified);
        Assert.Equal([".psd", ".ai"], settings.Settings.FileWidget.FileStackCustomRules[0].Extensions);

        editor.AddRule();
        editor.MoveRule(editor.CustomRules[1], -1);
        Assert.Equal("Design", settings.Settings.FileWidget.FileStackCustomRules[1].Name);
        // The move itself persisted through the CollectionChanged entry; a
        // drag that lands back in the original order re-commits the same
        // projection and must not save again.
        int afterMove = notified;
        editor.CommitRuleOrder();
        Assert.Equal(afterMove, notified);

        editor.RemoveRule(editor.CustomRules[0]);
        Assert.Single(settings.Settings.FileWidget.FileStackCustomRules);
        Assert.Equal("Design", settings.Settings.FileWidget.FileStackCustomRules[0].Name);
    }

    [Fact]
    public void AddRule_RespectsTheCapAndTheMasterSwitchGate()
    {
        (SettingsService settings, FileStackSettingsCoordinator coordinator, FileStackSettingsViewModel editor) =
            CreateEditor(_root);
        coordinator.SetFileStacksEnabled(false);
        editor.SyncPresentation();
        Assert.False(editor.CanAddRule);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.AddRule();
        Assert.Empty(editor.CustomRules);
        Assert.Equal(0, notified);

        editor.StacksEnabled = true;
        for (int i = 0; i < FileStackOptionKinds.MaxCustomRules; i++)
        {
            editor.AddRule();
        }

        Assert.Equal(FileStackOptionKinds.MaxCustomRules, editor.CustomRules.Count);
        Assert.False(editor.CanAddRule);
        editor.AddRule();
        Assert.Equal(FileStackOptionKinds.MaxCustomRules, editor.CustomRules.Count);
    }

    [Fact]
    public void PreviewEntries_PushedByTheShell_DriveRuleMatchingAndTheSummary()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.FileWidget.FileStackCustomRules =
            [
                new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = [".pdf", ".docx"] },
                new FileStackCustomRule { Id = "r2", Name = "Images", Extensions = [".png"] },
                new FileStackCustomRule { Id = "r3", Name = "Empty", Extensions = [] }
            ]);

        editor.UpdatePreviewEntries(
        [
            new FileStackPreviewEntry("Widget A", @"C:\d\a.pdf", ".pdf"),
            new FileStackPreviewEntry("Widget A", @"C:\d\b.pdf", ".pdf"),
            new FileStackPreviewEntry("Widget B", @"C:\d\c.docx", ".docx"),
            new FileStackPreviewEntry("Widget B", @"C:\d\d.txt", ".txt"),
            new FileStackPreviewEntry("Widget B", @"C:\d\e.md", ".md"),
        ]);

        Assert.StartsWith(
            "Settings.FileStacks.Custom.Preview.Matches:3|",
            editor.CustomRules[0].PreviewText,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Settings.FileStacks.Custom.Preview.None",
            editor.CustomRules[1].PreviewText,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Settings.FileStacks.Custom.Preview.EmptyExtensions",
            editor.CustomRules[2].PreviewText,
            StringComparison.Ordinal);
        Assert.Equal(
            "Settings.FileStacks.Custom.Preview.Summary:3|2",
            editor.PreviewSummaryText);

        editor.MarkPreviewLoading();
        Assert.Equal("Settings.FileStacks.Custom.Preview.Loading", editor.PreviewSummaryText);
    }

    [Fact]
    public void DerivedGatesAndSummary_FollowThePersistedProjection()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(_root);

        // Defaults: enabled, auto-stacking off, group-by kind.
        Assert.False(editor.ShowCustomRules);
        Assert.True(editor.HasNoRules);
        Assert.Equal("Settings.FileStacks.Status.Manual", editor.SettingsSummaryText);

        editor.AutoStacking = true;
        Assert.Equal("Settings.FileStacks.Status.On", editor.SettingsSummaryText);

        editor.GroupBy = FileStackOptionKinds.GroupByCustom;
        Assert.True(editor.ShowCustomRules);
        Assert.Equal("Settings.FileStacks.Status.Custom:0", editor.SettingsSummaryText);

        editor.StacksEnabled = false;
        Assert.Equal("Settings.FileStacks.Status.Off", editor.SettingsSummaryText);
        Assert.False(editor.CanAddRule);
    }

    [Fact]
    public void OptionTables_ListCanonicalValuesWithLocalizedNames()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(_root);

        SettingsOption[] groupBy = [.. editor.AvailableGroupByOptions];
        Assert.Equal(
        [
            FileStackOptionKinds.GroupByKind,
            FileStackOptionKinds.GroupByDateModified,
            FileStackOptionKinds.GroupByCustom
        ],
        groupBy.Select(option => (string)option.Value));
        Assert.Equal("Settings.FileStacks.GroupBy.DateModified", groupBy[1].DisplayName);

        SettingsOption[] thresholds = [.. editor.AvailableThresholdOptions];
        Assert.Equal(
            [2, 3, 5],
            thresholds.Select(option => option.Value).Cast<int>().ToArray());
        Assert.Equal("Settings.FileStacks.Threshold.Option:5", thresholds[2].DisplayName);

        SettingsOption[] openModes = [.. editor.AvailableOpenModeOptions];
        Assert.Equal(FileStackOptionKinds.OpenModePopover, (string)openModes[1].Value);
        Assert.Equal("Settings.FileStacks.OpenMode.Popover", openModes[1].DisplayName);

        // The rule priorities are localized through the format delegate.
        editor.AddRule();
        editor.AddRule();
        Assert.Equal("Settings.FileStacks.Custom.Priority:2", editor.CustomRules[1].PriorityText);
        Assert.False(editor.CustomRules[0].CanMoveUp);
        Assert.True(editor.CustomRules[0].CanMoveDown);
        Assert.False(editor.CustomRules[1].CanMoveDown);
    }

    [Fact]
    public void RefreshLocalization_RebuildsOptionTablesAndLocalizedDerivations()
    {
        (_, _, FileStackSettingsViewModel editor) = CreateEditor(_root);
        string first = editor.AvailableGroupByOptions[1].DisplayName;
        Assert.Equal("Settings.FileStacks.GroupBy.DateModified", first);

        int raised = 0;
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileStackSettingsViewModel.AvailableGroupByOptions) or
                nameof(FileStackSettingsViewModel.SettingsSummaryText))
            {
                raised++;
            }
        };

        editor.RefreshLocalization();
        Assert.Equal(2, raised);
        Assert.Equal(first, editor.AvailableGroupByOptions[1].DisplayName);
    }

    [Fact]
    public void FolderOpenBehaviorEditor_ProjectsWritesAndReSyncsWithoutWriteBack()
    {
        (SettingsService settings, FeatureWidgetsSettingsCoordinator coordinator, FeatureWidgetsSettingsViewModel editor) =
            CreateFeatureWidgetsEditor(_root);
        Assert.Equal(FileWidgetFolderOpenBehaviors.Explorer, editor.FolderOpenBehavior);
        SettingsOption[] options = [.. editor.AvailableFolderOpenBehaviorOptions];
        Assert.Equal(FileWidgetFolderOpenBehaviors.Explorer, (string)options[0].Value);
        Assert.Equal("Settings.FileWidget.FolderOpenBehavior.Explorer", options[0].DisplayName);
        Assert.Equal(2, editor.AvailableFolderOpenBehaviorOptionItems.Length);

        int notified = 0;
        settings.SettingsChanged += () => notified++;
        editor.FolderOpenBehavior = "nonsense";
        Assert.Equal(FileWidgetFolderOpenBehaviors.Explorer, editor.FolderOpenBehavior);
        Assert.Equal(0, notified);

        editor.FolderOpenBehavior = FileWidgetFolderOpenBehaviors.Embedded;
        Assert.Equal(FileWidgetFolderOpenBehaviors.Embedded, settings.Settings.FileWidget.FileWidgetFolderOpenBehavior);
        Assert.Equal(1, notified);

        coordinator.SetFileWidgetFolderOpenBehavior(FileWidgetFolderOpenBehaviors.Explorer);
        int afterExternalWrite = notified;
        editor.SyncPresentation();
        Assert.Equal(FileWidgetFolderOpenBehaviors.Explorer, editor.FolderOpenBehavior);
        Assert.Equal(afterExternalWrite, notified);
        Assert.Equal(
            FileWidgetFolderOpenBehaviors.Explorer,
            coordinator.ReadFileWidgetFolderOpenBehavior());
    }

    [Fact]
    public void OverviewEditors_ExposeTheInteractionContextMenuSurface()
    {
        var settings = new SettingsService(_root);
        var interaction = new InteractionSettingsViewModel(
            new InteractionSettingsCoordinator(settings),
            PassthroughLocalize);
        Assert.False(interaction.FileItemContextMenuEnabled);

        bool? userEvent = null;
        interaction.FileItemContextMenuEnabledUserChanged += value => userEvent = value;

        interaction.FileItemContextMenuEnabled = true;
        Assert.True(settings.Settings.FileWidget.FileItemSystemContextMenuEnabled);
        Assert.True(userEvent);

        int notified = 0;
        settings.SettingsChanged += () => notified++;
        interaction.SyncPresentation();
        Assert.True(interaction.FileItemContextMenuEnabled);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void ShellFacade_TheFileStackFamilyIsGone()
    {
        string[] removed =
        [
            "FileStacksEnabled",
            "FileStackAutoStacking",
            "SelectedFileStackGroupBy",
            "SelectedFileStackThreshold",
            "SelectedFileStackOrderBy",
            "SelectedFileStackOpenMode",
            "SelectedFileStackPopoverLayout",
            "SelectedFileStackPopoverStyle",
            "SelectedFileStackUnmatchedBehavior",
            "AvailableFileStackGroupByOptions",
            "AvailableFileStackThresholdOptions",
            "AvailableFileStackOrderByOptions",
            "AvailableFileStackOpenModeOptions",
            "AvailableFileStackPopoverLayoutOptions",
            "AvailableFileStackPopoverStyleOptions",
            "AvailableFileStackUnmatchedBehaviorOptions",
            "FileStackCustomRules",
            "FileStackCustomRulesVisibility",
            "FileStackRulesEmptyVisibility",
            "CanAddFileStackCustomRule",
            "FileStackPreviewSummaryText",
            "FileStackSettingsSummaryText",
            "SelectedFileWidgetFolderOpenBehavior",
            "AvailableFileWidgetFolderOpenBehaviorOptions",
            "AvailableFileWidgetFolderOpenBehaviorOptionItems",
            "FileItemSystemContextMenuEnabled"
        ];
        HashSet<string> publicProperties = typeof(SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in removed)
        {
            Assert.DoesNotContain(name, publicProperties);
        }

        // The shell keeps only the preview-entry scan; the aggregation,
        // priorities, matching and summary moved to the editor.
        string shell = File.ReadAllText(Path.Combine(
            TestPaths.FromRepository("src/DeskBox/ViewModels"),
            "SettingsViewModel.FileStackOptions.cs"));
        Assert.Contains("RefreshFileStackRulePreviewFromDiskAsync", shell, StringComparison.Ordinal);
        Assert.Contains("_fileStackSettings.UpdatePreviewEntries(entries);", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistFileStackCustomRules", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshFileStackRulePreview()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationPattern_XamlBridgesAndWindowWiringStayPinned()
    {
        string windowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string overviewXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string overviewCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string bridge = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Features/FileStack/FileStackSettingsViewModel.AotBindableProperties.cs"));
        string bindableShell = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));

        // Section-level DataContext switch with the compiled x:Bind surface
        // typed to the editor (ProcessBindings receives the editor root).
        Assert.Contains(
            "x:DataType=\"fileStack:FileStackSettingsViewModel\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind CustomRules, Mode=OneWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{x:Bind OpenMode, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding StacksEnabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowCustomRules, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);

        // The overview page's three typed editor dependency properties.
        Assert.Contains(
            "IsOn=\"{x:Bind FileStack.StacksEnabled, Mode=TwoWay}\"",
            overviewXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Description=\"{x:Bind FileStack.SettingsSummaryText, Mode=OneWay}\"",
            overviewXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind FeatureWidgets.AvailableFolderOpenBehaviorOptionItems, Mode=OneWay}\"",
            overviewXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{x:Bind FeatureWidgets.FolderOpenBehavior, Mode=TwoWay}\"",
            overviewXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind Interaction.FileItemContextMenuEnabled, Mode=TwoWay}\"",
            overviewXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModelProperty", overviewCode, StringComparison.Ordinal);

        // The deferred host assigns the editor bridges after the DataContext
        // root and routes the compiled-bindings root for the file-stack
        // template.
        Assert.Contains(
            "fileSettings.FileStack = _fileStackSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileSettings.FeatureWidgets = _featureWidgetsSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileSettings.Interaction = _interactionSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "section.DataContext = _fileStackSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "object compiledBindingsRoot = sectionTag == \"FileStackSettings\"",
            deferred,
            StringComparison.Ordinal);

        // The editor's {Binding} bridge and the shrunken shell bridge.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(StacksEnabled)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(AvailableOpenModeOptions)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(CustomRules)", bridge, StringComparison.Ordinal);
        Assert.Equal(34, Regex.Matches(bindableShell, @"nameof\(").Count);
    }
}
