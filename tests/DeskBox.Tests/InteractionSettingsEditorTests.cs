using DeskBox.Contracts;
using DeskBox.Features.Interaction;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// First copy batch of the settings-shell binding-facade retirement: the
/// interaction main section and the interaction-window advanced section are
/// re-bound to the interaction editor through a section-level DataContext
/// switch, exactly like the pilot's music section. These tests pin the
/// editor's behavior (read snapshot projection, write-through, no write-back
/// on external sync, linkage events, pushed hotkey/hover presentations,
/// localization refresh) and the migration pattern itself (XAML paths, AOT
/// bridges, window wiring, facade removal) so later batches can keep copying
/// the shape.
/// </summary>
public sealed class InteractionSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, InteractionSettingsViewModel Editor) CreateEditor(
        string root,
        Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new InteractionSettingsCoordinator(settings);
        return (settings, new InteractionSettingsViewModel(coordinator, PassthroughLocalize));
    }

    [Fact]
    public void Constructor_ProjectsPersistedPresentationFromTheReadSnapshot()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
                shell.WidgetLayerMode = WidgetLayerModes.QuickReveal;
                shell.DynamicClickRaiseScope = DynamicClickRaiseScopes.Group;
                shell.ResizeSnapEnabled = false;
                shell.WidgetSnapSpacing = 12d;
                settings.Settings.FileWidget.DoubleClickToOpen = true;
                shell.KeepWidgetsVisibleOnShowDesktop = true;
            });

        Assert.Equal(WidgetLayerModes.QuickReveal, editor.LayerMode);
        Assert.False(editor.IsDynamicLayerMode);
        Assert.Equal(DynamicClickRaiseScopes.Group, editor.ClickRaiseScope);
        Assert.False(editor.SnapEnabled);
        Assert.Equal(12d, editor.SnapSpacing);
        Assert.Equal(FileOpenMethods.DoubleClick, editor.FileOpenMethod);
        Assert.Equal(ShowDesktopBehaviors.KeepVisible, editor.ShowDesktopBehavior);
    }

    [Fact]
    public void ReadSnapshot_NormalizesLayerModeAndClampsSpacing()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                settings.Settings.WidgetShell.WidgetLayerMode = "Nonsense";
                settings.Settings.WidgetShell.DynamicClickRaiseScope = "Nonsense";
                settings.Settings.WidgetShell.WidgetSnapSpacing = 9_999d;
            });

        Assert.Equal(WidgetLayerModes.Dynamic, editor.LayerMode);
        Assert.True(editor.IsDynamicLayerMode);
        Assert.Equal(DynamicClickRaiseScopes.Group, editor.ClickRaiseScope);
        Assert.Equal(SettingsService.MaxWidgetSnapSpacing, editor.SnapSpacing);
    }

    [Fact]
    public void UserEdits_WriteThroughTheCoordinatorAndPersist()
    {
        (SettingsService settings, InteractionSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.LayerMode = WidgetLayerModes.DesktopPinned;
        editor.ClickRaiseScope = DynamicClickRaiseScopes.Single;
        editor.SnapEnabled = false;
        editor.SnapSpacing = 14d;
        editor.FileOpenMethod = FileOpenMethods.SingleClick;
        editor.ShowDesktopBehavior = ShowDesktopBehaviors.HideWithWindows;

        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(WidgetLayerModes.DesktopPinned, shell.WidgetLayerMode);
        Assert.False(editor.IsDynamicLayerMode);
        Assert.Equal(DynamicClickRaiseScopes.Single, shell.DynamicClickRaiseScope);
        Assert.False(shell.ResizeSnapEnabled);
        Assert.Equal(14d, shell.WidgetSnapSpacing);
        Assert.False(settings.Settings.FileWidget.DoubleClickToOpen);
        Assert.False(shell.KeepWidgetsVisibleOnShowDesktop);
        Assert.Equal(6, notified);
    }

    [Fact]
    public void UserEdits_RaiseLinkageEventsWhilePushesDoNot()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(_root);
        int layerMode = 0, showDesktop = 0, snapEnabled = 0, snapSpacing = 0, hotkeyEnabled = 0;
        editor.LayerModeUserChanged += () => layerMode++;
        editor.ShowDesktopBehaviorUserChanged += () => showDesktop++;
        editor.SnapEnabledUserChanged += value => snapEnabled++;
        editor.SnapSpacingUserChanged += value => snapSpacing++;
        editor.HotkeyEnabledUserChanged += value => hotkeyEnabled++;

        editor.LayerMode = WidgetLayerModes.DesktopPinned;
        editor.ShowDesktopBehavior = ShowDesktopBehaviors.HideWithWindows;
        editor.SnapEnabled = false;
        editor.SnapSpacing = 9d;
        editor.HotkeyEnabled = true;
        Assert.Equal(1, layerMode);
        Assert.Equal(1, showDesktop);
        Assert.Equal(1, snapEnabled);
        Assert.Equal(1, snapSpacing);
        Assert.Equal(1, hotkeyEnabled);

        // External sync and shell pushes re-project without re-raising.
        editor.SyncPresentation();
        editor.UpdateGlobalHotkeyPresentation(new GlobalHotkeyPresentationSettings(
            Enabled: false, Text: "t", StatusText: "s", Description: "d",
            WarningText: "w", CanShowWarning: true));
        Assert.Equal(1, layerMode);
        Assert.Equal(1, showDesktop);
        Assert.Equal(1, snapEnabled);
        Assert.Equal(1, snapSpacing);
        Assert.Equal(1, hotkeyEnabled);
        Assert.False(editor.HotkeyEnabled);
        Assert.True(editor.CanShowHotkeyWarning);
        Assert.Equal("t", editor.HotkeyText);
        Assert.Equal("s", editor.HotkeyStatusText);
        Assert.Equal("d", editor.HotkeyDescription);
        Assert.Equal("w", editor.HotkeyWarningText);
    }

    [Fact]
    public void HotkeyToggle_RaisesTheUserEventAndCarriesTheValue()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(_root);
        bool? requested = null;
        editor.HotkeyEnabledUserChanged += value => requested = value;

        editor.HotkeyEnabled = true;
        Assert.True(requested);
    }

    [Fact]
    public void ExternalSync_RefreshesTheProjectionWithoutWritingBack()
    {
        (SettingsService settings, InteractionSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // External mutation (restore, snapshot apply, feature-card reset).
        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        shell.WidgetLayerMode = WidgetLayerModes.DesktopPinned;
        shell.DynamicClickRaiseScope = DynamicClickRaiseScopes.Group;
        shell.ResizeSnapEnabled = false;
        shell.WidgetSnapSpacing = 20d;
        settings.Settings.FileWidget.DoubleClickToOpen = true;
        shell.KeepWidgetsVisibleOnShowDesktop = true;

        int notifiedBeforeSync = notified;
        editor.SyncPresentation();

        Assert.Equal(WidgetLayerModes.DesktopPinned, editor.LayerMode);
        Assert.False(editor.IsDynamicLayerMode);
        Assert.Equal(DynamicClickRaiseScopes.Group, editor.ClickRaiseScope);
        Assert.False(editor.SnapEnabled);
        Assert.Equal(20d, editor.SnapSpacing);
        Assert.Equal(FileOpenMethods.DoubleClick, editor.FileOpenMethod);
        Assert.Equal(ShowDesktopBehaviors.KeepVisible, editor.ShowDesktopBehavior);
        Assert.Equal(notifiedBeforeSync, notified);
    }

    [Fact]
    public void Options_ListCanonicalValuesWithLocalizedNames()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(_root);

        SettingsOption[] layerModes = [.. editor.AvailableLayerModeOptions];
        Assert.Equal(3, layerModes.Length);
        Assert.Equal(WidgetLayerModes.Dynamic, layerModes[0].Value);
        Assert.Equal(WidgetLayerModes.DesktopPinned, layerModes[1].Value);
        Assert.Equal(WidgetLayerModes.QuickReveal, layerModes[2].Value);
        Assert.Equal("Settings.WidgetLayerMode.Dynamic", layerModes[0].DisplayName);
        Assert.Equal("Settings.WidgetLayerMode.QuickReveal", layerModes[2].DisplayName);

        SettingsOption[] clickRaiseScopes = [.. editor.AvailableClickRaiseScopeOptions];
        Assert.Equal(2, clickRaiseScopes.Length);
        Assert.Equal(DynamicClickRaiseScopes.Single, clickRaiseScopes[0].Value);
        Assert.Equal(DynamicClickRaiseScopes.Group, clickRaiseScopes[1].Value);
        Assert.Equal("Settings.ClickRaiseScope.Single", clickRaiseScopes[0].DisplayName);
        Assert.Equal("Settings.ClickRaiseScope.Group", clickRaiseScopes[1].DisplayName);

        SettingsOption[] openMethods = [.. editor.AvailableFileOpenMethodOptions];
        Assert.Equal(FileOpenMethods.SingleClick, openMethods[0].Value);
        Assert.Equal(FileOpenMethods.DoubleClick, openMethods[1].Value);
        Assert.Equal("Settings.OpenMethod.DoubleClick", openMethods[1].DisplayName);

        SettingsOption[] desktopBehaviors = [.. editor.AvailableShowDesktopBehaviorOptions];
        Assert.Equal(ShowDesktopBehaviors.KeepVisible, desktopBehaviors[0].Value);
        Assert.Equal(ShowDesktopBehaviors.HideWithWindows, desktopBehaviors[1].Value);
        Assert.Equal("Settings.ShowDesktopBehavior.HideWithWindows", desktopBehaviors[1].DisplayName);
    }

    [Fact]
    public void RefreshLocalization_RebuildsTheOptionsAndNotifies()
    {
        (_, InteractionSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InteractionSettingsViewModel.AvailableLayerModeOptions))
            {
                notified++;
            }
        };

        var relabeled = new InteractionSettingsViewModel(
            new InteractionSettingsCoordinator(
                new SettingsService(Path.Combine(_root, "second"))),
            static key => key == "Settings.WidgetLayerMode.DesktopPinned" ? "zh:Pinned" : key);
        Assert.Equal("zh:Pinned", relabeled.AvailableLayerModeOptions[1].DisplayName);

        editor.RefreshLocalization();
        Assert.Equal(1, notified);
        // The cache rebuild keeps the canonical values untouched.
        Assert.Equal(WidgetLayerModes.DesktopPinned, editor.AvailableLayerModeOptions[1].Value);
    }

    [Fact]
    public void SettingsService_LayerModeConstants_AliasTheContractCanonicalValues()
    {
        Assert.Equal(WidgetLayerModes.Dynamic, SettingsService.WidgetLayerModeDynamic);
        Assert.Equal(WidgetLayerModes.DesktopPinned, SettingsService.WidgetLayerModeDesktopPinned);
        Assert.Equal(WidgetLayerModes.QuickReveal, SettingsService.WidgetLayerModeQuickReveal);
        Assert.Equal(DynamicClickRaiseScopes.Single, SettingsService.DynamicClickRaiseScopeSingle);
        Assert.Equal(DynamicClickRaiseScopes.Group, SettingsService.DynamicClickRaiseScopeGroup);
    }

    [Fact]
    public void SettingsShell_NoLongerExposesTheInteractionCompatFacade()
    {
        System.Reflection.PropertyInfo[] properties = typeof(ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        string[] removed =
        [
            "SelectedWidgetLayerMode",
            "SelectedWidgetLayerModeText",
            "AvailableWidgetLayerModeOptions",
            "HoverButtonActionsSummaryText",
            "SelectedFileOpenMethod",
            "AvailableFileOpenMethodOptions",
            "SelectedShowDesktopBehavior",
            "AvailableShowDesktopBehaviorOptions",
            "GlobalHotkeyEnabled",
            "GlobalHotkeyText",
            "GlobalHotkeyStatusText",
            "GlobalHotkeyStatusKind",
            "GlobalHotkeyDescription",
            "GlobalHotkeyWarningText",
            "CanShowGlobalHotkeyWarning",
            "ResizeSnapEnabled",
            "WidgetSnapSpacing",
            "WidgetSnapSpacingText",
            "DoubleClickToOpen",
            "KeepWidgetsVisibleOnShowDesktop"
        ];
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }
    }

    [Fact]
    public void InteractionSections_BindToTheEditorThroughSectionLevelDataContext()
    {
        string xaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string fileWidgetXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string bindable = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));
        string editorBridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/Interaction/InteractionSettingsViewModel.AotBindableProperties.cs"));
        string sync = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs"));
        string hotkeyAndStorage = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.HotkeyAndStorage.cs"));

        // Consolidated Interaction section binds to InteractionSettingsViewModel via
        // section-level DataContext; FileOpenMethod binds via compiled {x:Bind Interaction.*}
        // on FileWidgetSettingsSection.
        Assert.Contains("ItemsSource=\"{Binding AvailableLayerModeOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding LayerMode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AvailableClickRaiseScopeOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding ClickRaiseScope, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsDynamicLayerMode}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Description=\"{Binding HoverButtonActionsSummary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsSegmented.Options=\"{x:Bind Interaction.AvailableFileOpenMethodOptions, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsSegmented.Value=\"{x:Bind Interaction.FileOpenMethod, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding ShowDesktopBehavior, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding HotkeyEnabled, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding CanShowHotkeyWarning, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Message=\"{Binding HotkeyWarningText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"{Binding HotkeyText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding SnapEnabled, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding SnapEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding SnapSpacing, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SnapSpacingText}\"", xaml, StringComparison.Ordinal);
        foreach (string legacy in new[]
                 {
                     "{Binding SelectedWidgetLayerMode",
                     "{Binding AvailableWidgetLayerModeOptions",
                     "{Binding HoverButtonActionsSummaryText",
                     "{Binding SelectedFileOpenMethod",
                     "{Binding SelectedShowDesktopBehavior",
                     "{Binding GlobalHotkeyEnabled",
                     "{Binding GlobalHotkeyText",
                     "{Binding GlobalHotkeyStatusText",
                     "{Binding GlobalHotkeyWarningText",
                     "{Binding CanShowGlobalHotkeyWarning",
                     "{Binding GlobalHotkeyDescription",
                     "{Binding ResizeSnapEnabled",
                     "{Binding WidgetSnapSpacing"
                 })
        {
            Assert.DoesNotContain(legacy, xaml, StringComparison.Ordinal);
        }

        // The deferred-section host switches the consolidated Interaction section
        // DataContext to the editor and wires the FileWidgetSettingsSection bridge.
        Assert.Contains("if (sectionTag == \"Interaction\")", deferred, StringComparison.Ordinal);
        Assert.DoesNotContain("\"InteractionWindowSettings\"", deferred, StringComparison.Ordinal);
        Assert.Contains("section.DataContext = _interactionSettingsViewModel;", deferred, StringComparison.Ordinal);
        Assert.Contains("fileSettings.Interaction = _interactionSettingsViewModel;", deferred, StringComparison.Ordinal);

        // The shell bridge drops the sixteen interaction entries; the editor
        // carries its own NativeAOT custom-property bridge.
        foreach (string name in new[]
                 {
                     "SelectedWidgetLayerMode",
                     "AvailableWidgetLayerModeOptions",
                     "HoverButtonActionsSummaryText",
                     "SelectedFileOpenMethod",
                     "AvailableFileOpenMethodOptions",
                     "SelectedShowDesktopBehavior",
                     "AvailableShowDesktopBehaviorOptions",
                     "GlobalHotkeyEnabled",
                     "GlobalHotkeyText",
                     "GlobalHotkeyStatusText",
                     "GlobalHotkeyDescription",
                     "GlobalHotkeyWarningText",
                     "CanShowGlobalHotkeyWarning",
                     "ResizeSnapEnabled",
                     "WidgetSnapSpacing",
                     "WidgetSnapSpacingText"
                 })
        {
            Assert.DoesNotContain($"nameof({name})", bindable, StringComparison.Ordinal);
        }

        Assert.Contains("#if DESKBOX_NATIVE_AOT", editorBridge, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", editorBridge, StringComparison.Ordinal);
        foreach (string name in new[]
                 {
                     "AvailableClickRaiseScopeOptions",
                     "AvailableFileOpenMethodOptions",
                     "AvailableLayerModeOptions",
                     "AvailableShowDesktopBehaviorOptions",
                     "CanShowHotkeyWarning",
                     "ClickRaiseScope",
                     "FileOpenMethod",
                     "HoverButtonActionsSummary",
                     "HotkeyDescription",
                     "HotkeyEnabled",
                     "HotkeyStatusText",
                     "HotkeyText",
                     "HotkeyWarningText",
                     "IsDynamicLayerMode",
                     "LayerMode",
                     "ShowDesktopBehavior",
                     "SnapEnabled",
                     "SnapSpacing",
                     "SnapSpacingText"
                 })
        {
            Assert.Contains($"nameof({name})", editorBridge, StringComparison.Ordinal);
        }

        // External refresh paths re-sync the editor projection and push the
        // shell-owned hotkey presentation instead of assigning facade state.
        Assert.Contains("_interactionSettings.SyncPresentation();", sync, StringComparison.Ordinal);
        Assert.Contains("_interactionSettings.RefreshLocalization();", sync, StringComparison.Ordinal);
        Assert.Contains(
            "_interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());",
            sync, StringComparison.Ordinal);
        Assert.Contains(
            "_interactionSettings.UpdateGlobalHotkeyPresentation(new GlobalHotkeyPresentationSettings(",
            hotkeyAndStorage, StringComparison.Ordinal);
    }
}
