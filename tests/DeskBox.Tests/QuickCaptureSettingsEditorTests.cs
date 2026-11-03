using System.Text.RegularExpressions;
using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

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
        var coordinator = new QuickCaptureSettingsCoordinator(
            settings,
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
                slice.QuickCaptureDefaultView = QuickCaptureOptionKinds.DefaultViewPinned;
                slice.QuickCaptureTabStyle = SettingsService.WidgetTabStylePivot;
                slice.QuickCaptureShowCreatedTime = false;
                slice.QuickCaptureItemPreviewLineCount = 6;
                slice.QuickCaptureDefaultFormat = QuickCaptureOptionKinds.FormatPlainText;
                slice.QuickCaptureEditorEnterBehavior =
                    QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
                slice.QuickCaptureAllowRemoteImages = true;
            });

        Assert.True(editor.Enabled);
        Assert.Equal(QuickCaptureOptionKinds.WideLayoutDualPane, editor.WideLayout);
        Assert.Equal(QuickCaptureOptionKinds.WideOpenEditing, editor.WideOpenMode);
        Assert.False(editor.ShowTabBar);
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
            CreateEditor(_root, s => s.Settings.QuickCapture.QuickCaptureShowPinnedTab = false);
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
        editor.SelectedDefaultView = QuickCaptureOptionKinds.DefaultViewPinned;

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
        Assert.Equal(
            QuickCaptureOptionKinds.DefaultViewPinned,
            slice.QuickCaptureDefaultView);
        Assert.True(slice.QuickCaptureShowPinnedTab);
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
    public void TabFlyout_GuardsTheLastSelectedTabAndDrivesDerivedSurfaces()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);

        Assert.True(editor.IsTabSelected(QuickCaptureOptionKinds.DefaultViewRecords));
        Assert.True(editor.CanToggleTab(QuickCaptureOptionKinds.DefaultViewPinned));
        Assert.Equal(2, editor.VisibleDefaultViewOptions.Count);
        Assert.Contains(
            QuickCaptureOptionKinds.DefaultViewRecords,
            editor.VisibleDefaultViewOptions.Select(option => (string)option.Value!));
        Assert.DoesNotContain(
            QuickCaptureOptionKinds.DefaultViewRecent,
            editor.VisibleDefaultViewOptions.Select(option => (string)option.Value!));

        // Deselecting the last remaining tab is refused.
        editor.ShowPinnedTab = false;
        editor.ToggleTab(QuickCaptureOptionKinds.DefaultViewRecords);
        Assert.True(editor.ShowRecordsTab);

        editor.ShowTabBar = false;
        Assert.Equal(PassthroughLocalize("Settings.Toggle.Off"), editor.TabsSummaryText);
        editor.ShowTabBar = true;
        string joined = editor.VisibleTabsText;
        Assert.Contains(PassthroughLocalize("Settings.QuickCapture.DefaultView.Records"), joined);
        Assert.DoesNotContain(PassthroughLocalize("Settings.QuickCapture.DefaultView.Recent"), joined);
    }

    [Fact]
    public void TextSizes_SnapToTheHalfPointGridAndRaiseCommitOnlyOnStoredWrites()
    {
        (SettingsService settings, _, QuickCaptureSettingsViewModel editor) = CreateEditor(_root);
        int commits = 0;
        editor.ListTextSizeCommitted += () => commits++;
        editor.ContentTextSizeCommitted += () => commits++;

        editor.ListTextSize = 12.34;
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(1, commits);
        Assert.Equal(12.5, settings.Settings.QuickCapture.QuickCaptureListTextSize);

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
        Assert.Equal(2, editor.AvailableDefaultViews.Length);
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

        Assert.Contains(
            "IsOn=\"{Binding Enabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ImageClipboardEnabled",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:DataType=\"quickCapture:QuickCaptureSettingsViewModel\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedIndex=\"{x:Bind EditorFormatIndex, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedIndex=\"{x:Bind WideOpenModeIndex, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanConfigureWideOpenMode, Mode=OneWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanConfigureRemoteImages, Mode=OneWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding VisibleDefaultViewOptions}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecentLimit",
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
            "? _quickCaptureSettingsViewModel",
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

        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(Enabled)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(ImageClipboardEnabled)", bridge, StringComparison.Ordinal);
        Assert.Equal(30, Regex.Matches(bridge, @"nameof\(").Count);
        Assert.DoesNotContain("nameof(ShowRecordsTab)", bridge, StringComparison.Ordinal);
        Assert.Equal(39, Regex.Matches(bindableShell, @"nameof\(").Count);
    }

    [Fact]
    public void SegmentedModes_LastTabGuard_AndMirrorFolderPresentation_StaySynchronized()
    {
        (_, _, QuickCaptureSettingsViewModel editor) = CreateEditor(
            _root,
            s => FeatureWidgetSettings.SetEnabled(s.Settings, WidgetKind.QuickCapture, true));

        Assert.True(editor.Enabled);
        Assert.Equal(0, editor.WideOpenModeIndex);
        Assert.True(editor.CanConfigureWideOpenMode);

        editor.WideOpenModeIndex = 1;
        Assert.Equal(QuickCaptureOptionKinds.WideOpenEditing, editor.WideOpenMode);

        editor.WideLayout = QuickCaptureOptionKinds.WideLayoutSinglePane;
        Assert.False(editor.CanConfigureWideOpenMode);

        Assert.Equal(0, editor.EditorFormatIndex);
        Assert.True(editor.CanConfigureRemoteImages);

        editor.EditorFormatIndex = 1;
        Assert.Equal(QuickCaptureOptionKinds.FormatPlainText, editor.EditorFormat);
        Assert.False(editor.CanConfigureRemoteImages);

        Assert.True(editor.ShowRecordsTab);
        Assert.True(editor.ShowPinnedTab);
        Assert.True(editor.CanEditRecordsTab);
        Assert.True(editor.CanEditPinnedTab);

        editor.ShowPinnedTab = false;
        Assert.False(editor.ShowPinnedTab);
        Assert.False(editor.CanEditRecordsTab);
        Assert.True(editor.CanEditPinnedTab);

        editor.ShowRecordsTab = false;
        Assert.True(editor.ShowRecordsTab);

        Assert.False(editor.HasMirrorFolder);
        Assert.Equal("Settings.QuickCapture.MirrorFolder.Unset", editor.MirrorFolderDisplayText);

        editor.UpdateMirrorFolderPresentation("  D:\\Notes\\QuickCapture  ");
        Assert.True(editor.HasMirrorFolder);
        Assert.Equal("D:\\Notes\\QuickCapture", editor.MirrorFolderPath);
        Assert.Equal("D:\\Notes\\QuickCapture", editor.MirrorFolderDisplayText);

        editor.UpdateMirrorFolderPresentation("   ");
        Assert.False(editor.HasMirrorFolder);
        Assert.Null(editor.MirrorFolderPath);
        Assert.Equal("Settings.QuickCapture.MirrorFolder.Unset", editor.MirrorFolderDisplayText);
    }
}