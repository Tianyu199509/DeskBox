using System.Text.RegularExpressions;
using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Sixth copy batch of the settings-shell binding-facade retirement: the
/// Quick Capture section (the largest single section, 35 unique binding
/// properties) is re-bound to a dedicated editor through a section-level
/// DataContext switch. These tests pin the editor's behavior (read snapshot
/// projection with the old shell normalization, write-through with
/// unchanged-write skip, no write-back on external sync, the recording
/// chain's coordinator-side enablement push-back, the tab flyout state
/// machine, the recent-capacity write chain, text-size step normalization
/// and commit events, the derived gates/summaries, localization refresh,
/// the shell-pushed image-cache presentation) and the migration pattern
/// itself (XAML paths, AOT bridges, window wiring, facade removal).
/// </summary>
public sealed class QuickCaptureSettingsEditorTests : IDisposable
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

    private static (SettingsService Settings, QuickCaptureSettingsCoordinator Coordinator, QuickCaptureSettingsViewModel Editor)
        CreateEditor(string root, Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var clipboard = new QuickCaptureClipboardRuntime(
            () => false, () => throw new InvalidOperationException(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(
            settings, clipboard,
            (_, _) => Task.CompletedTask,
            action => { action(); return true; },
            _ => { });
        return (settings, coordinator, new QuickCaptureSettingsViewModel(
            coordinator, PassthroughLocalize, PassthroughFormat, _ => { }, _ => { }));
    }

    [Fact]
    public void Constructor_ProjectsPersistedStateFromTheReadSnapshot()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                FeatureWidgetSettings.SetEnabled(
                    settings.Settings, WidgetKind.QuickCapture, true);
                QuickCaptureSettingsSlice slice = settings.Settings.QuickCapture;
                slice.QuickCaptureWideLayout = QuickCaptureOptionKinds.WideLayoutDualPane;
                slice.QuickCaptureWideOpenMode = QuickCaptureOptionKinds.WideOpenEditing;
                slice.QuickCaptureShowTabBar = false;
                slice.QuickCaptureShowRecentTab = false;
                slice.QuickCaptureDefaultView = QuickCaptureOptionKinds.DefaultViewPinned;
                slice.QuickCaptureTabStyle = SettingsService.WidgetTabStylePivot;
                slice.QuickCaptureShowCreatedTime = false;
                slice.QuickCaptureItemPreviewLineCount = 6;
                slice.QuickCaptureDefaultFormat = QuickCaptureOptionKinds.FormatPlainText;
                slice.QuickCaptureEditorEnterBehavior =
                    QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
                slice.QuickCaptureAllowRemoteImages = true;
                slice.QuickCaptureRecentLimit = 80;
            });

        Assert.True(editor.Enabled);
        Assert.Equal(QuickCaptureOptionKinds.WideLayoutDualPane, editor.WideLayout);
        Assert.Equal(QuickCaptureOptionKinds.WideOpenEditing, editor.WideOpenMode);
        Assert.False(editor.ShowTabBar);
        Assert.False(editor.ShowRecentTab);
        Assert.True(editor.ShowPinnedTab);
        Assert.Equal(QuickCaptureOptionKinds.DefaultViewPinned, editor.SelectedDefaultView);
        Assert.Equal(0, editor.TabStyleIndex);
        Assert.False(editor.ShowCreatedTime);
        Assert.Equal(6, editor.ItemPreviewLineCount);
        Assert.Equal(QuickCaptureOptionKinds.FormatPlainText, editor.EditorFormat);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves,
            editor.EditorEnterBehavior);
        Assert.True(editor.AllowRemoteImages);
        Assert.Equal(80, editor.RecentLimit);
        // Effective text sizes inherit the global default (stored zero).
        Assert.Equal(SettingsService.DefaultTextSize, editor.ListTextSize);
    }

    [Fact]
    public void Constructor_NormalizesUnknownValuesLikeTheOldShell()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                QuickCaptureSettingsSlice slice = settings.Settings.QuickCapture;
                slice.QuickCaptureWideLayout = "bogus";
                slice.QuickCaptureWideOpenMode = "bogus";
                slice.QuickCaptureDefaultFormat = "bogus";
                slice.QuickCaptureEditorEnterBehavior = "bogus";
                slice.QuickCaptureItemPreviewLineCount = 99;
            });

        Assert.Equal(QuickCaptureOptionKinds.WideLayoutAuto, editor.WideLayout);
        Assert.Equal(QuickCaptureOptionKinds.WideOpenReading, editor.WideOpenMode);
        Assert.Equal(QuickCaptureOptionKinds.FormatMarkdown, editor.EditorFormat);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves,
            editor.EditorEnterBehavior);
        Assert.Equal(10, editor.ItemPreviewLineCount);
    }

    [Fact]
    public void EditorWrites_PersistThroughTheCoordinator()
    {
        (SettingsService settings, QuickCaptureSettingsCoordinator coordinator, QuickCaptureSettingsViewModel editor) =
            CreateEditor(_root);
        // Mirror the production wiring: the coordinator's Changed broadcast
        // re-projects the editor (the shell owns the subscription).
        coordinator.Changed += () => editor.SyncPresentation();

        editor.WideLayout = QuickCaptureOptionKinds.WideLayoutDualPane;
        editor.WideOpenMode = QuickCaptureOptionKinds.WideOpenEditing;
        editor.EditorFormat = QuickCaptureOptionKinds.FormatPlainText;
        editor.EditorEnterBehavior = QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
        editor.AllowRemoteImages = true;
        editor.ShowCreatedTime = false;
        editor.ItemPreviewLineCount = 4;
        editor.TabStyleIndex = 0;
        editor.ShowTabBar = false;
        editor.SelectedDefaultView = QuickCaptureOptionKinds.DefaultViewRecent;

        QuickCaptureSettingsSlice slice = settings.Settings.QuickCapture;
        Assert.Equal(QuickCaptureOptionKinds.WideLayoutDualPane, slice.QuickCaptureWideLayout);
        Assert.Equal(QuickCaptureOptionKinds.WideOpenEditing, slice.QuickCaptureWideOpenMode);
        Assert.Equal(QuickCaptureOptionKinds.FormatPlainText, slice.QuickCaptureDefaultFormat);
        Assert.Equal(
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves,
            slice.QuickCaptureEditorEnterBehavior);
        Assert.True(slice.QuickCaptureAllowRemoteImages);
        Assert.False(slice.QuickCaptureShowCreatedTime);
        Assert.Equal(4, slice.QuickCaptureItemPreviewLineCount);
        Assert.Equal(SettingsService.WidgetTabStylePivot, slice.QuickCaptureTabStyle);
        Assert.False(slice.QuickCaptureShowTabBar);
        // Selecting a hidden default view makes the coordinator reveal it.
        Assert.Equal(
            QuickCaptureOptionKinds.DefaultViewRecent,
            slice.QuickCaptureDefaultView);
        Assert.True(slice.QuickCaptureShowRecentTab);
    }

    [Fact]
    public void ExternalSync_ReprojectsWithoutWritingBack()
    {
        (SettingsService settings, QuickCaptureSettingsCoordinator coordinator, QuickCaptureSettingsViewModel editor) =
            CreateEditor(_root);
        int changed = 0;
        coordinator.Changed += () => changed++;

        settings.Settings.QuickCapture.QuickCaptureWideLayout =
            QuickCaptureOptionKinds.WideLayoutSinglePane;
        editor.SyncPresentation();

        Assert.Equal(QuickCaptureOptionKinds.WideLayoutSinglePane, editor.WideLayout);
        Assert.False(editor.ShowWideOptions);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void RecordingChain_ImageSwitchOnFromDisabledState_ChainsThroughCoordinator()
    {
        (SettingsService settings, QuickCaptureSettingsCoordinator coordinator, QuickCaptureSettingsViewModel editor) =
            CreateEditor(_root);
        coordinator.Changed += () => editor.SyncPresentation();
        Assert.False(editor.Enabled);

        // Turning image recording on while the feature is off enables the
        // feature and text recording in the coordinator; the Changed
        // broadcast re-projects all three switches on the editor.
        editor.ImageClipboardEnabled = true;

        Assert.True(settings.Settings.QuickCapture.QuickCaptureImageClipboardEnabled);
        Assert.True(settings.Settings.QuickCapture.QuickCaptureClipboardEnabled);
        Assert.True(FeatureWidgetSettings.IsEnabled(
            settings.Settings, WidgetKind.QuickCapture));
        Assert.True(editor.Enabled);
        Assert.True(editor.ClipboardEnabled);
        Assert.True(editor.ImageClipboardEnabled);
    }

    [Fact]
    public void TabFlyout_GuardsTheLastSelectedTabAndDrivesDerivedSurfaces()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.QuickCapture.QuickCaptureShowRecentTab = false);

        Assert.False(editor.IsTabSelected(QuickCaptureOptionKinds.DefaultViewRecent));
        Assert.True(editor.CanToggleTab(QuickCaptureOptionKinds.DefaultViewPinned));
        Assert.Equal(2, editor.VisibleDefaultViewOptions.Count);
        Assert.Contains(
            QuickCaptureOptionKinds.DefaultViewRecords,
            editor.VisibleDefaultViewOptions.Select(option => (string)option.Value!));
        Assert.DoesNotContain(
            QuickCaptureOptionKinds.DefaultViewRecent,
            editor.VisibleDefaultViewOptions.Select(option => (string)option.Value!));

        // Deselecting the last two remaining tabs is refused.
        editor.ShowPinnedTab = false;
        editor.ToggleTab(QuickCaptureOptionKinds.DefaultViewRecords);
        Assert.True(editor.ShowRecordsTab);

        // The default-view combo only offers visible tabs.
        editor.ShowTabBar = false;
        Assert.Equal(PassthroughLocalize("Settings.Toggle.Off"), editor.TabsSummaryText);
        editor.ShowTabBar = true;
        string joined = editor.VisibleTabsText;
        Assert.Contains(PassthroughLocalize("Settings.QuickCapture.DefaultView.Records"), joined);
        Assert.DoesNotContain(PassthroughLocalize("Settings.QuickCapture.DefaultView.Recent"), joined);
    }

    [Fact]
    public void RecentLimit_WritesThroughTheCoordinatorNormalizeChain()
    {
        (SettingsService settings, QuickCaptureSettingsCoordinator coordinator, QuickCaptureSettingsViewModel editor) =
            CreateEditor(_root);
        coordinator.Changed += () => editor.SyncPresentation();

        editor.RecentLimit = 500;

        Assert.Equal(100, settings.Settings.QuickCapture.QuickCaptureRecentLimit);
        Assert.Equal(100, editor.RecentLimit);
        Assert.Equal(
            PassthroughFormat("Settings.QuickCapture.RecentLimitValue", [100]),
            editor.RecentLimitText);

        // Below-minimum input falls back to the default like the old chain.
        editor.RecentLimit = 1;
        Assert.Equal(30, settings.Settings.QuickCapture.QuickCaptureRecentLimit);
        Assert.Equal(30, editor.RecentLimit);
    }

    [Fact]
    public void TextSizes_SnapToTheHalfPointGridAndRaiseCommitOnlyOnStoredWrites()
    {
        (SettingsService settings, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);
        int commits = 0;
        editor.ListTextSizeCommitted += () => commits++;
        editor.ContentTextSizeCommitted += () => commits++;

        // Off-grid input snaps back onto the grid and commits through the
        // re-entry (the old shell's two-pass behavior).
        editor.ListTextSize = 12.34;
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(1, commits);
        Assert.Equal(12.5, settings.Settings.QuickCapture.QuickCaptureListTextSize);

        // Equal re-writes do not commit again.
        editor.ListTextSize = 12.5;
        Assert.Equal(1, commits);

        editor.ContentTextSize = 13.0;
        Assert.Equal(2, commits);
        Assert.Equal(13.0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);
        Assert.Equal($"{12.5:0.#}pt", editor.ListTextSizeValueText);
    }

    [Fact]
    public void DerivedGatesAndSummaries_TrackThePersistedProjection()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);

        Assert.True(editor.ShowWideOptions);
        editor.WideLayout = QuickCaptureOptionKinds.WideLayoutSinglePane;
        Assert.False(editor.ShowWideOptions);
        Assert.Equal(
            PassthroughLocalize("Settings.QuickCapture.WideLayout.SinglePane"),
            editor.LayoutSummaryText);
        editor.WideLayout = QuickCaptureOptionKinds.WideLayoutDualPane;
        Assert.Equal(
            PassthroughLocalize("Settings.QuickCapture.WideLayout.DualPane") + " · " +
            PassthroughLocalize("Settings.QuickCapture.WideOpen.Reading"),
            editor.LayoutSummaryText);

        Assert.Equal(
            string.Join(
                " · ",
                PassthroughFormat(
                    "Settings.ContentEditor.PreviewLines.Option.Multiple", [3]),
                PassthroughLocalize("Settings.QuickCapture.Format.Markdown"),
                PassthroughLocalize("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves")),
            editor.ContentSummaryText);

        Assert.True(editor.ShowRemoteImages);
        editor.EditorFormat = QuickCaptureOptionKinds.FormatPlainText;
        Assert.False(editor.ShowRemoteImages);
        Assert.Contains(
            PassthroughLocalize("Settings.QuickCapture.Format.PlainText"),
            editor.ContentSummaryText);

        Assert.False(editor.Enabled);
        Assert.Equal(
            PassthroughLocalize("Settings.QuickCapture.Status.Disabled"),
            editor.StatusText);
    }

    [Fact]
    public void OptionTables_OfferCanonicalValuesWithLocalizedNames()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);

        Assert.Equal(
            new[]
            {
                QuickCaptureOptionKinds.WideLayoutAuto,
                QuickCaptureOptionKinds.WideLayoutSinglePane,
                QuickCaptureOptionKinds.WideLayoutDualPane
            },
            editor.AvailableWideLayoutOptions.Select(option => (string)option.Value!).ToArray());
        Assert.Equal(
            PassthroughLocalize("Settings.QuickCapture.WideLayout.DualPane"),
            editor.AvailableWideLayoutOptions[2].DisplayName);
        Assert.Equal(2, editor.AvailableWideOpenModeOptions.Count);
        Assert.Equal(2, editor.AvailableFormatOptions.Count);
        Assert.Equal(2, editor.AvailableEnterBehaviorOptions.Count);
        Assert.Equal(10, editor.AvailablePreviewLineOptions.Count);
        Assert.Equal(3, editor.AvailableDefaultViews.Length);
    }

    [Fact]
    public void RefreshLocalization_RebuildsCachesAndDerivations()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);
        SettingsOption firstBefore = editor.AvailableWideLayoutOptions[0];

        editor.RefreshLocalization();

        Assert.NotSame(firstBefore, editor.AvailableWideLayoutOptions[0]);
        Assert.Equal(
            PassthroughLocalize("Settings.QuickCapture.WideLayout.Auto"),
            editor.AvailableWideLayoutOptions[0].DisplayName);
        Assert.Equal(
            PassthroughFormat(
                "Settings.QuickCapture.ClipboardDiagnosticsNotRecordingNoCapture",
                [PassthroughLocalize("Settings.QuickCapture.ClipboardReason.QuickCaptureOff")]),
            editor.ClipboardDiagnosticsText);
    }

    [Fact]
    public void ImageCachePresentation_IsShellPushed()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);
        Assert.False(editor.CanClearImageCache);

        editor.UpdateImageCachePresentation("42 files", canClear: true);

        Assert.Equal("42 files", editor.ImageCacheText);
        Assert.True(editor.CanClearImageCache);
    }

    [Fact]
    public void ShellReflects_NoQuickCaptureFacadePropertiesRemain()
    {
        string[] removed =
        [
            "QuickCaptureEnabled",
            "QuickCaptureShowTabBar",
            "QuickCaptureShowRecordsTab",
            "QuickCaptureShowPinnedTab",
            "QuickCaptureShowRecentTab",
            "QuickCaptureClipboardEnabled",
            "QuickCaptureImageClipboardEnabled",
            "QuickCaptureRecentLimit",
            "QuickCaptureShowCreatedTime",
            "QuickCaptureListTextSize",
            "QuickCaptureContentTextSize",
            "QuickCaptureEditorEnterBehavior",
            "QuickCaptureEditorFormat",
            "QuickCaptureWideLayout",
            "QuickCaptureWideOpenMode",
            "QuickCaptureAllowRemoteImages",
            "QuickCaptureItemPreviewLineCount",
            "SelectedQuickCaptureDefaultView",
            "SelectedQuickCaptureTabStyle",
            "SelectedQuickCaptureDefaultViewText",
            "SelectedQuickCaptureTabStyleText",
            "QuickCaptureTabStyleIndex",
            "QuickCaptureStatusText",
            "QuickCaptureDependencyStatusText",
            "QuickCaptureRecentLimitText",
            "QuickCaptureRecentLimitInput",
            "QuickCaptureVisibleTabsText",
            "QuickCaptureTabsSummaryText",
            "QuickCaptureLayoutSummaryText",
            "QuickCaptureContentSummaryText",
            "QuickCaptureWideOptionsVisibility",
            "QuickCaptureRemoteImagesVisibility",
            "QuickCaptureClipboardDiagnosticsText",
            "QuickCaptureImageCacheText",
            "CanClearQuickCaptureImageCache",
            "VisibleQuickCaptureDefaultViewOptions",
            "AvailableQuickCaptureDefaultViews",
            "AvailableQuickCaptureDefaultViewDisplayNames",
            "AvailableQuickCaptureDefaultViewOptions",
            "AvailableQuickCaptureTabStyleOptions",
            "AvailableQuickCaptureTabStyleDisplayNames",
            "AvailableQuickCaptureFormatOptions",
            "AvailableQuickCaptureFormats",
            "AvailableQuickCaptureFormatDisplayNames",
            "AvailableQuickCaptureWideLayoutOptions",
            "AvailableQuickCaptureWideLayouts",
            "AvailableQuickCaptureWideLayoutDisplayNames",
            "AvailableQuickCaptureWideOpenModeOptions",
            "AvailableQuickCaptureWideOpenModes",
            "AvailableQuickCaptureWideOpenModeDisplayNames",
            "GetQuickCaptureDefaultViewDisplayName",
            "GetQuickCaptureFormatDisplayName",
            "GetQuickCaptureWideLayoutDisplayName",
            "GetQuickCaptureWideOpenModeDisplayName",
            "IsQuickCaptureTabSelected",
            "CanToggleQuickCaptureTab",
            "ToggleQuickCaptureTab",
            "GetQuickCaptureTabDisplayName"
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
                "src/DeskBox/Features/QuickCapture/QuickCaptureSettingsViewModel.AotBindableProperties.cs"));
        string bindableShell = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));

        // Section-level DataContext switch; the template stays {Binding}-only
        // (no x:Bind, so its x:DataType keeps pointing at the shell type).
        Assert.Contains(
            "IsOn=\"{Binding Enabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding ImageClipboardEnabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding EditorFormat, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding VisibleDefaultViewOptions}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{Binding RecentLimit, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowWideOptions, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding QuickCapture", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Binding SelectedQuickCaptureDefaultView", windowXaml, StringComparison.Ordinal);

        Assert.Contains(
            "section.DataContext = _quickCaptureSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "quickCaptureSettings.AvailableDefaultViews,",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "var quickCaptureSettings = _quickCaptureSettingsViewModel;",
            navigation,
            StringComparison.Ordinal);

        // The editor's {Binding} bridge and the shrunken shell bridge.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(Enabled)", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(ImageClipboardEnabled)", bridge, StringComparison.Ordinal);
        Assert.Equal(35, Regex.Matches(bridge, @"nameof\(").Count);
        Assert.DoesNotContain("nameof(ShowRecordsTab)", bridge, StringComparison.Ordinal);
        Assert.Equal(33, Regex.Matches(bindableShell, @"nameof\(").Count);
    }
}
