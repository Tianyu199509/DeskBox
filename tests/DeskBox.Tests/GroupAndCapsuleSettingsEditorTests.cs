using DeskBox.Contracts;
using DeskBox.Features.Capsule;
using DeskBox.Features.GroupNavigation;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Batch 44 editor tests: the WidgetGroups section and the capsule family
/// (main section plus behavior/arrangement/animation/overrides subsections)
/// bind through the group-navigation and capsule editors. These tests pin the
/// read projections, the write-through semantics with their derived
/// view-state dances (animation preset pair-writes, the never-persisted
/// hover-response label), the pushed projections and the XAML/bridge wiring.
/// </summary>
public sealed class GroupAndCapsuleSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string PassthroughLocalize(string key) => key;

    // --- Group-navigation editor ---

    [Fact]
    public void GroupNavigation_ConstructorProjectsTheDefaults()
    {
        var settings = new SettingsService(_root);
        settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle =
            WidgetGroupNavigationStyles.Tabs;
        settings.Settings.WidgetLayout.WidgetGroupDefaultTitleDisplayMode =
            WidgetGroupTitleDisplayModes.TextOnly;
        settings.Settings.WidgetLayout.WidgetGroupWheelSwitchEnabled = false;
        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            PassthroughLocalize);

        Assert.Equal(WidgetGroupNavigationStyles.Tabs, editor.DefaultNavigationStyle);
        Assert.Equal(WidgetGroupTitleDisplayModes.TextOnly, editor.DefaultTitleDisplayMode);
        Assert.False(editor.WheelSwitchEnabled);
        // Fresh-install default: the hover switch ships disabled.
        Assert.False(editor.HoverSwitchEnabled);
        // Fresh-install default: the switch animation follows the layout.
        Assert.Equal(WidgetGroupSwitchAnimationStyles.Auto, editor.DefaultSwitchAnimationStyle);
        Assert.Empty(editor.ExistingGroups);
        Assert.False(editor.HasExistingGroups);
        Assert.True(editor.ShowExistingGroupsEmpty);
    }

    [Fact]
    public void GroupNavigation_UserWrites_PersistAndRaiseTheHostEvent()
    {
        var settings = new SettingsService(_root);
        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            PassthroughLocalize);
        int hostEvents = 0;
        int notified = 0;
        editor.PresentationUserChanged += () => hostEvents++;
        settings.SettingsChanged += () => notified++;

        editor.DefaultNavigationStyle = WidgetGroupNavigationStyles.Stack;
        editor.DefaultTitleDisplayMode = WidgetGroupTitleDisplayModes.IconOnly;
        editor.WheelSwitchEnabled = false;
        editor.HoverSwitchEnabled = true;
        editor.DefaultSwitchAnimationStyle = WidgetGroupSwitchAnimationStyles.Horizontal;

        Assert.Equal(5, hostEvents);
        Assert.Equal(5, notified);
        Assert.Equal(WidgetGroupNavigationStyles.Stack,
            settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle);
        Assert.Equal(WidgetGroupTitleDisplayModes.IconOnly,
            settings.Settings.WidgetLayout.WidgetGroupDefaultTitleDisplayMode);
        Assert.False(settings.Settings.WidgetLayout.WidgetGroupWheelSwitchEnabled);
        Assert.True(settings.Settings.WidgetLayout.WidgetGroupHoverSwitchEnabled);
        Assert.Equal(WidgetGroupSwitchAnimationStyles.Horizontal,
            settings.Settings.WidgetLayout.WidgetGroupSwitchAnimationStyle);

        // Unchanged writes skip both the save and the host event.
        int eventsBeforeRepeat = hostEvents;
        editor.WheelSwitchEnabled = false;
        Assert.Equal(eventsBeforeRepeat, hostEvents);
    }

    [Fact]
    public void GroupNavigation_ExternalSync_ReprojectsWithoutEventsOrWrites()
    {
        var settings = new SettingsService(_root);
        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            PassthroughLocalize);
        int hostEvents = 0;
        int notified = 0;
        editor.PresentationUserChanged += () => hostEvents++;
        settings.SettingsChanged += () => notified++;

        settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle =
            WidgetGroupNavigationStyles.Tabs;
        editor.SyncPresentation();

        Assert.Equal(WidgetGroupNavigationStyles.Tabs, editor.DefaultNavigationStyle);
        Assert.Equal(0, hostEvents);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void GroupNavigation_ExistingGroupsPush_UpdatesTheGatesSilently()
    {
        var settings = new SettingsService(_root);
        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            PassthroughLocalize);
        int hostEvents = 0;
        editor.PresentationUserChanged += () => hostEvents++;

        var item = new WidgetGroupSettingsItem(
            "group-1", "widget-1", "Group", "Summary", HasOverrides: false,
            WidgetGroupNavigationStyles.FollowDefault, [],
            WidgetGroupTitleDisplayModes.FollowDefault, [],
            "FollowDefault", [], "FollowDefault", [],
            WidgetCollapseBehaviorNames.System, [],
            WidgetChromeModeNames.Standard, [],
            []);
        editor.UpdateExistingGroups([item]);

        Assert.Same(item, editor.ExistingGroups[0]);
        Assert.True(editor.HasExistingGroups);
        Assert.False(editor.ShowExistingGroupsEmpty);
        Assert.Equal(0, hostEvents);
    }

    [Fact]
    public void GroupNavigation_OptionTables_UseCanonicalValuesAndLocalizer()
    {
        var settings = new SettingsService(_root);
        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            PassthroughLocalize);

        Assert.Equal(
            [WidgetGroupNavigationStyles.Tabs, WidgetGroupNavigationStyles.Stack],
            editor.AvailableNavigationStyleOptions.Select(option => option.Value));
        Assert.Equal(
            "Settings.WidgetGroupNavigation.Tabs",
            editor.AvailableNavigationStyleOptions[0].DisplayName);
        Assert.Equal(
            [
                WidgetGroupTitleDisplayModes.IconAndText,
                WidgetGroupTitleDisplayModes.IconOnly,
                WidgetGroupTitleDisplayModes.TextOnly
            ],
            editor.AvailableTitleDisplayModeOptions.Select(option => option.Value));
        Assert.Equal(
            [
                WidgetGroupSwitchAnimationStyles.Auto,
                WidgetGroupSwitchAnimationStyles.Vertical,
                WidgetGroupSwitchAnimationStyles.Horizontal,
                WidgetGroupSwitchAnimationStyles.Fade,
                WidgetGroupSwitchAnimationStyles.None
            ],
            editor.AvailableSwitchAnimationOptions.Select(option => option.Value));
        Assert.Equal(
            "Settings.WidgetGroupSwitchAnimation.None",
            editor.AvailableSwitchAnimationOptions[^1].DisplayName);
    }

    [Fact]
    public void GroupNavigation_RefreshLocalization_RebuildsTheOptionTables()
    {
        var settings = new SettingsService(_root);
        int localizeCalls = 0;
        string Localize(string key)
        {
            localizeCalls++;
            return key;
        }

        var editor = new GroupNavigationSettingsViewModel(
            new GroupNavigationSettingsCoordinator(settings),
            Localize);
        _ = editor.AvailableNavigationStyleOptions;
        _ = editor.AvailableTitleDisplayModeOptions;
        int callsAfterFirstRead = localizeCalls;

        // Cached names serve repeat reads without new localize calls.
        _ = editor.AvailableNavigationStyleOptions;
        Assert.Equal(callsAfterFirstRead, localizeCalls);

        editor.RefreshLocalization();
        _ = editor.AvailableNavigationStyleOptions;
        Assert.True(localizeCalls > callsAfterFirstRead);
    }

    // --- Capsule editor ---

    private static (SettingsService Settings, CapsuleSettingsViewModel Editor) CreateCapsuleEditor(
        string root,
        Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        return (settings, new CapsuleSettingsViewModel(
            new CapsuleSettingsCoordinator(settings),
            PassthroughLocalize,
            (key, args) => key + ":" + string.Join('|', args)));
    }

    [Fact]
    public void Capsule_ConstructorProjectsAndNormalizesTheSnapshot()
    {
        (SettingsService settings, CapsuleSettingsViewModel editor) =
            CreateCapsuleEditor(_root, settings =>
            {
                settings.Settings.WidgetShell.WidgetCollapseBehavior =
                    CapsuleOptionKinds.CollapseSmart;
                settings.Settings.WidgetShell.WidgetCompactContentMode =
                    CapsuleOptionKinds.ContentModeMinimal;
                settings.Settings.WidgetShell.WidgetCompactHideSensitiveContent = true;
                settings.Settings.WidgetShell.WidgetCompactWidthMode =
                    CapsuleOptionKinds.WidthModeIndependent;
                settings.Settings.WidgetShell.WidgetCompactExpansionDirection =
                    CapsuleOptionKinds.ExpansionDirectionUp;
                settings.Settings.WidgetShell.WidgetCapsuleArrangementMode =
                    CapsuleOptionKinds.ArrangementBar;
                settings.Settings.WidgetShell.WidgetCapsuleBarPlacement =
                    CapsuleOptionKinds.BarPlacementRight;
                settings.Settings.WidgetShell.WidgetCapsuleBarDirection =
                    CapsuleOptionKinds.BarDirectionVertical;
                settings.Settings.WidgetShell.WidgetCapsuleBarSpacing = 12;
                settings.Settings.WidgetShell.WidgetCompactAnimationEffect =
                    CapsuleOptionKinds.AnimationSlow;
                settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs = 9999;
                settings.Settings.WidgetShell.WidgetCompactExpandDelayMs =
                    CapsuleOptionKinds.SensitiveExpandDelayMs;
                settings.Settings.WidgetShell.WidgetCompactCollapseDelayMs =
                    CapsuleOptionKinds.SensitiveCollapseDelayMs;
            });

        Assert.Equal(CapsuleOptionKinds.CollapseSmart, editor.CollapseBehavior);
        Assert.Equal(CapsuleOptionKinds.ContentModeMinimal, editor.ContentMode);
        Assert.True(editor.HideSensitiveContent);
        Assert.Equal(CapsuleOptionKinds.WidthModeIndependent, editor.WidthMode);
        Assert.Equal(CapsuleOptionKinds.ExpansionDirectionUp, editor.ExpansionDirection);
        Assert.Equal(CapsuleOptionKinds.ArrangementBar, editor.ArrangementMode);
        Assert.Equal(CapsuleOptionKinds.BarPlacementRight, editor.BarPlacement);
        Assert.Equal(CapsuleOptionKinds.BarDirectionVertical, editor.BarDirection);
        Assert.Equal(12d, editor.BarSpacing);
        Assert.Equal(CapsuleOptionKinds.AnimationSlow, editor.AnimationEffect);
        Assert.Equal(CapsuleOptionKinds.MaxAnimationDurationMs, editor.AnimationDurationMs);
        Assert.Equal(CapsuleOptionKinds.HoverResponseSensitive, editor.HoverResponse);
        Assert.True(editor.ShowHoverResponseEntry);
        Assert.True(editor.IsBarEnabled);
        Assert.True(editor.IsBarSpacingEnabled);
        Assert.True(editor.ShowArrangementEntry);
    }

    [Fact]
    public void Capsule_UserOptionWrites_PersistThroughTheCoordinator()
    {
        (SettingsService settings, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.CollapseBehavior = CapsuleOptionKinds.CollapseSmart;
        editor.ContentMode = CapsuleOptionKinds.ContentModeSummary;
        editor.HideSensitiveContent = true;
        editor.WidthMode = CapsuleOptionKinds.WidthModeIndependent;
        editor.ExpansionDirection = CapsuleOptionKinds.ExpansionDirectionUp;
        editor.ArrangementMode = CapsuleOptionKinds.ArrangementBar;
        editor.BarPlacement = CapsuleOptionKinds.BarPlacementTop;
        editor.BarDirection = CapsuleOptionKinds.BarDirectionHorizontal;
        editor.BarSpacing = 21;

        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(CapsuleOptionKinds.CollapseSmart, shell.WidgetCollapseBehavior);
        Assert.Equal(CapsuleOptionKinds.ContentModeSummary, shell.WidgetCompactContentMode);
        Assert.True(shell.WidgetCompactHideSensitiveContent);
        Assert.Equal(CapsuleOptionKinds.WidthModeIndependent, shell.WidgetCompactWidthMode);
        Assert.Equal(CapsuleOptionKinds.ExpansionDirectionUp, shell.WidgetCompactExpansionDirection);
        Assert.Equal(CapsuleOptionKinds.ArrangementBar, shell.WidgetCapsuleArrangementMode);
        Assert.Equal(CapsuleOptionKinds.BarPlacementTop, shell.WidgetCapsuleBarPlacement);
        Assert.Equal(CapsuleOptionKinds.BarDirectionHorizontal, shell.WidgetCapsuleBarDirection);
        Assert.Equal(21d, shell.WidgetCapsuleBarSpacing);
        Assert.True(notified >= 9);
    }

    [Fact]
    public void Capsule_AnimationPreset_PairWritesDurationAndViewState()
    {
        (SettingsService settings, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);

        editor.AnimationEffect = CapsuleOptionKinds.AnimationSlow;

        Assert.Equal(CapsuleOptionKinds.SlowAnimationDurationMs, editor.AnimationDurationMs);
        Assert.Equal(CapsuleOptionKinds.SlowAnimationDurationMs,
            settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs);
        Assert.Equal($"{CapsuleOptionKinds.SlowAnimationDurationMs} ms", editor.AnimationDurationText);
        Assert.False(editor.ShowAnimationCustom);

        // A manual duration write flips a preset label to Custom (view state
        // plus the coordinator's persisted flip).
        editor.AnimationDurationMs = 301;
        Assert.Equal(CapsuleOptionKinds.AnimationCustom, editor.AnimationEffect);
        Assert.True(editor.ShowAnimationCustom);
        Assert.True(editor.CanOpenAnimationDetails);
        Assert.Equal(CapsuleOptionKinds.AnimationCustom,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);
    }

    [Fact]
    public void Capsule_HoverResponsePreset_WritesTheDelayPairWithoutPersistingTheLabel()
    {
        (SettingsService settings, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);

        editor.HoverResponse = CapsuleOptionKinds.HoverResponsePreventAccidental;

        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(CapsuleOptionKinds.PreventAccidentalExpandDelayMs, shell.WidgetCompactExpandDelayMs);
        Assert.Equal(CapsuleOptionKinds.PreventAccidentalCollapseDelayMs, shell.WidgetCompactCollapseDelayMs);
        // The selection itself is derived view state: the persisted slice has
        // no hover-response field to write.
        Assert.Null(typeof(WidgetShellSettingsSlice).GetProperty(
            "WidgetCompactHoverResponse",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));

        // A manual delay edit flips the label back to Custom; the preset
        // write does not (it suppresses the mark while applying).
        editor.ExpandDelayMs = 500;
        Assert.Equal(CapsuleOptionKinds.HoverResponseCustom, editor.HoverResponse);
        Assert.True(editor.ShowHoverResponseCustom);
        Assert.True(editor.CanOpenHoverResponseDetails);
        Assert.Equal(500, settings.Settings.WidgetShell.WidgetCompactExpandDelayMs);
    }

    [Fact]
    public void Capsule_SmartCollapseOverridePush_KeepsTheHoverEntryVisible()
    {
        (_, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);
        Assert.False(editor.ShowHoverResponseEntry);

        editor.UpdateOverridePresentation(
            [], hasOverrides: true, "summary", hasWidthOverrides: true,
            smartCollapseOverrideActive: true);

        Assert.True(editor.ShowHoverResponseEntry);
        Assert.True(editor.ShowOverridesEntry);
        Assert.True(editor.ShowOverridesList);
        Assert.False(editor.ShowOverridesEmpty);
        Assert.True(editor.HasWidthOverrides);
        Assert.Equal("summary", editor.OverrideSummaryText);
    }

    [Fact]
    public void Capsule_ExternalSync_ReprojectsWithoutWritingBack()
    {
        (SettingsService settings, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        settings.Settings.WidgetShell.WidgetCapsuleArrangementMode =
            CapsuleOptionKinds.ArrangementBar;
        settings.Settings.WidgetShell.WidgetCompactExpandDelayMs =
            CapsuleOptionKinds.SensitiveExpandDelayMs;
        settings.Settings.WidgetShell.WidgetCompactCollapseDelayMs =
            CapsuleOptionKinds.SensitiveCollapseDelayMs;
        editor.SyncPresentation();

        Assert.Equal(CapsuleOptionKinds.ArrangementBar, editor.ArrangementMode);
        Assert.Equal(CapsuleOptionKinds.HoverResponseSensitive, editor.HoverResponse);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void Capsule_ArrangementSummary_FormatsTheLocalizedDetails()
    {
        (_, CapsuleSettingsViewModel editor) = CreateCapsuleEditor(_root);
        editor.BarPlacement = CapsuleOptionKinds.BarPlacementTop;
        editor.BarDirection = CapsuleOptionKinds.BarDirectionVertical;
        editor.BarSpacing = 12;

        Assert.Equal(
            "Settings.Capsule.Arrangement.Summary:Settings.Capsule.Placement.Top|Settings.Capsule.Direction.Vertical|12 px",
            editor.ArrangementDetailsSummary);
    }

    // --- Wiring text pins ---

    [Fact]
    public void XamlAndBridge_WireTheGroupAndCapsuleFamiliesToTheirEditors()
    {
        string repository = TestPaths.FromRepository("src/DeskBox");
        string window = File.ReadAllText(Path.Combine(repository, "Views/SettingsWindow.xaml"));
        string capsuleSection = File.ReadAllText(
            Path.Combine(repository, "Views/SettingsSections/CapsuleModeSettingsSection.xaml"));
        string appearanceSection = File.ReadAllText(
            Path.Combine(repository, "Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string deferred = File.ReadAllText(
            Path.Combine(repository, "Views/SettingsWindow.DeferredSections.cs"));
        string shellBridge = File.ReadAllText(
            Path.Combine(repository, "ViewModels/SettingsViewModel.AotBindableProperties.cs"));
        string groupBridge = File.ReadAllText(Path.Combine(
            repository, "Features/GroupNavigation/GroupNavigationSettingsViewModel.AotBindableProperties.cs"));
        string capsuleBridge = File.ReadAllText(Path.Combine(
            repository, "Features/Capsule/CapsuleSettingsViewModel.AotBindableProperties.cs"));

        // The WidgetGroups section binds through the group-navigation editor.
        Assert.Contains("ItemsSource=\"{Binding AvailableNavigationStyleOptions}\"", window);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding DefaultNavigationStyle, Mode=TwoWay}\"", window);
        Assert.Contains("IsOn=\"{Binding WheelSwitchEnabled, Mode=TwoWay}\"", window);
        Assert.Contains("ItemsSource=\"{Binding ExistingGroups}\"", window);
        Assert.Contains(
            "Visibility=\"{Binding HasExistingGroups, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            window);

        // The capsule family binds through the capsule editor, with the
        // visibility gates running through the bool->visibility converter.
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding CollapseBehavior, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains("IsOn=\"{Binding HideSensitiveContent, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding HoverResponse, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains(
            "Visibility=\"{Binding ShowHoverResponseEntry, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            capsuleSection);
        Assert.Contains("Value=\"{Binding BarSpacing, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains("Value=\"{Binding ExpandDelayMs, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains("Value=\"{Binding AnimationDurationMs, Mode=TwoWay}\"", capsuleSection);
        Assert.Contains("ItemsSource=\"{Binding OverrideItems}\"", window);

        // The appearance main section no longer hosts the recycled
        // group-navigation inline selector.
        Assert.DoesNotContain("{Binding GroupNavigationStyle", appearanceSection);
        Assert.DoesNotContain("{Binding AvailableGroupNavigationStyleOptions}", appearanceSection);
        Assert.Contains("Tag=\"WidgetGroups\"", appearanceSection);

        // Section-level DataContext switch covers both families.
        Assert.Contains("section.DataContext = _groupNavigationSettingsViewModel;", deferred);
        Assert.Contains("\"CapsuleMode\" or", deferred);
        Assert.Contains("\"CapsuleOverridesSettings\"", deferred);
        Assert.Contains("section.DataContext = _capsuleSettingsViewModel;", deferred);

        // The editors expose AOT bridges and the shell bridge dropped the
        // migrated names.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", groupBridge);
        Assert.Contains("nameof(DefaultNavigationStyle)", groupBridge);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", capsuleBridge);
        Assert.Contains("nameof(CollapseBehavior)", capsuleBridge);
        Assert.Contains("nameof(HoverResponse)", capsuleBridge);
        Assert.DoesNotContain("nameof(SelectedWidgetCollapseBehavior)", shellBridge);
        Assert.DoesNotContain("nameof(SelectedWidgetGroupDefaultNavigationStyle)", shellBridge);
        Assert.DoesNotContain("nameof(ExistingWidgetGroupItems)", shellBridge);
        Assert.DoesNotContain("nameof(WidgetCompactExpandDelayMs)", shellBridge);
        Assert.DoesNotContain("nameof(WidgetCompactHoverResponseCustomVisibility)", shellBridge);
    }

    [Fact]
    public void ShellSurface_NoGroupOrCapsuleBindingFacadesRemain()
    {
        string[] removed =
        [
            "SelectedWidgetGroupDefaultNavigationStyle",
            "SelectedWidgetGroupDefaultTitleDisplayMode",
            "AvailableWidgetGroupNavigationStyleOptions",
            "AvailableWidgetGroupTitleDisplayModeOptions",
            "IsWidgetGroupWheelSwitchEnabled",
            "IsWidgetGroupHoverSwitchEnabled",
            "ExistingWidgetGroupItems",
            "ExistingWidgetGroupsVisibility",
            "ExistingWidgetGroupsEmptyVisibility",
            "WidgetGroupOverviewSummaryText",
            "SelectedWidgetCollapseBehavior",
            "SelectedWidgetCompactContentMode",
            "SelectedWidgetCompactWidthMode",
            "SelectedWidgetCompactExpansionDirection",
            "SelectedWidgetCompactAnimationEffect",
            "SelectedWidgetCompactHoverResponse",
            "SelectedWidgetCompactMediaCornerMode",
            "SelectedWidgetCapsuleArrangementMode",
            "SelectedWidgetCapsuleBarPlacement",
            "SelectedWidgetCapsuleBarDirection",
            "WidgetCapsuleBarSpacing",
            "WidgetCompactHideSensitiveContent",
            "WidgetCompactAnimationDurationMs",
            "WidgetCompactExpandDelayMs",
            "WidgetCompactCollapseDelayMs",
            "CapsuleOverrideItems",
            "CapsuleOverrideSummaryText",
            "HasCapsuleWidthOverrides",
            "IsSmartWidgetCollapseBehavior",
        ];
        System.Reflection.PropertyInfo[] properties = typeof(SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }

        // The projection state machine stays on the shell as editor pushes.
        Assert.Contains(properties, property => property.Name == "ResetAllCapsuleOverridesCommand");
    }
}
