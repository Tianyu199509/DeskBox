using System.Xml.Linq;
using DeskBox.Helpers;
using Windows.System;

namespace DeskBox.Tests;

public sealed class QuickCaptureSettingsRuntimeContractTests
{
    [Fact]
    public void SharedSurface_SearchReplacesTabsInlineAndHasExplicitCancel()
    {
        string xamlPath = TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement listPage = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ListPage");
        XElement searchButton = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SearchButton");
        XElement searchBox = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SearchTextBox");
        XElement closeButton = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "CloseSearchButton");
        XElement segmented = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") ==
            "QuickCaptureViewSegmented");

        Assert.Same(segmented.Parent, searchButton.Parent);
        Assert.Same(segmented.Parent, searchBox.Parent);
        Assert.Same(segmented.Parent, closeButton.Parent);
        Assert.Equal(
            "{Binding SearchButtonVisibility}",
            (string?)searchButton.Attribute("Visibility"));
        Assert.Equal(
            "{Binding SearchBoxVisibility}",
            (string?)searchBox.Attribute("Visibility"));
        Assert.Equal(
            "CloseSearchButton_Click",
            (string?)closeButton.Attribute("Click"));
        Assert.Equal(
            "{Binding SearchCancelText}",
            (string?)closeButton
                .Element(presentation + "TextBlock")?
                .Attribute("Text"));

        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        Assert.Contains("CloseSearchAndRestoreFocus();", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CollapseSearch();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearSearchButton_Click", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSurface_ConsumesWideOpenModeAndTabBarVisibility()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains(
            "_settingsService.Settings.QuickCaptureWideOpenMode",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsService.QuickCaptureWideOpenEditing",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void SynchronizeSegmentedVisibility()",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.TabBarVisibility != Visibility.Visible",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(QuickCaptureWidgetViewModel.TabBarVisibility)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingNotes_UseGlobalFormatWhenEnteringEitherEditorHost()
    {
        string shared = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        shared = shared.ReplaceLineEndings("\n");

        Assert.Contains(
            "_detailContentFormat = _isDetailEditing\n            ? ViewModel.EditorContentFormat\n            : item.ContentFormat;",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailContentFormat = ViewModel.EditorContentFormat;\n        SetDetailEditorText(_detailItem?.Body ?? string.Empty);\n        _isDetailEditing = true;",
            shared,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEditors_ConsumeTheirFeatureEnterBehavior()
    {
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));
        string sharedQuickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));

        Assert.Contains(
            "SettingsService.ShouldSubmitEditorOnEnter(EditorEnterBehavior, control)",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorEnterBehavior=\"{Binding EditorEnterBehavior}\"",
            sharedQuickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorEnterBehavior=\"{Binding ElementName=RootGrid, Path=DataContext.EditorEnterBehavior}\"",
            todo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureEditors_OptIntoTheDefaultCtrlSSaveShortcut()
    {
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));
        string sharedQuickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));

        Assert.True(TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            VirtualKey.S,
            controlPressed: true));
        Assert.False(TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            VirtualKey.S,
            controlPressed: false));
        Assert.False(TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            VirtualKey.S,
            controlPressed: true,
            shiftPressed: true));
        Assert.False(TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            VirtualKey.Enter,
            controlPressed: true));

        Assert.Contains(
            "EnableCtrlSSaveShortcutProperty",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommitRequested?.Invoke(this, EventArgs.Empty)",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnableCtrlSSaveShortcut=\"True\"",
            sharedQuickCapture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnableCtrlSSaveShortcut",
            todo,
            StringComparison.Ordinal);
    }


    [Fact]
    public void GroupSwitch_RestoresQuickCaptureTabBeforeTheIncomingFrame()
    {
        string groups = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.Groups.cs"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string surfaceXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs"));

        int switchMethod = groups.IndexOf(
            "private async Task<bool> SwitchContentWidgetGroupMemberInPlaceAsync(",
            StringComparison.Ordinal);
        int firstPreview = groups.IndexOf(
            "PreviewWidgetGroupTransientState(",
            switchMethod,
            StringComparison.Ordinal);
        int prepare = groups.IndexOf(
            "preparedContent = await persistentWindow.PrepareContentSwitchAsync(",
            switchMethod,
            StringComparison.Ordinal);
        int secondPreview = groups.IndexOf(
            "PreviewWidgetGroupTransientState(",
            firstPreview + 1,
            StringComparison.Ordinal);
        int beginTransition = groups.IndexOf(
            "preparation.BeginTransition()",
            switchMethod,
            StringComparison.Ordinal);
        int finalRestore = groups.IndexOf(
            "RestoreWidgetGroupTransientState(targetConfig.Id)",
            beginTransition,
            StringComparison.Ordinal);

        Assert.True(switchMethod >= 0);
        Assert.True(firstPreview > switchMethod && firstPreview < prepare);
        Assert.True(prepare < secondPreview && secondPreview < beginTransition);
        Assert.True(finalRestore > beginTransition);
        Assert.Contains(
            "ViewModel.RestoreSelectedViewImmediately(quickState.SelectedView)",
            surface,
            StringComparison.Ordinal);
        int updateVisual = surface.IndexOf(
            "private void UpdateSelectedViewVisual()",
            StringComparison.Ordinal);
        int itemLoaded = surface.IndexOf(
            "private void QuickCaptureItem_Loaded(",
            updateVisual,
            StringComparison.Ordinal);
        Assert.True(updateVisual >= 0 && itemLoaded > updateVisual);
        Assert.DoesNotContain(
            "!IsLoaded",
            surface[updateVisual..itemLoaded],
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewSwitchRefreshTimer.Stop()",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_restoredViewForInitialization = view",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_restoredViewForInitialization is { } restoredView",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshFromDataAsync(data)",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedIndex=\"0\"",
            surfaceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "QuickCaptureViewSegmented.Visibility != Visibility.Visible",
            surface,
            StringComparison.Ordinal);
        int reveal = surface.IndexOf(
            "private void RevealSegmentedAtSelectedView()",
            StringComparison.Ordinal);
        int selectedViewBeforeReveal = surface.IndexOf(
            "UpdateSelectedViewVisual();",
            reveal,
            StringComparison.Ordinal);
        int visible = surface.IndexOf(
            "QuickCaptureViewSegmented.Visibility = Visibility.Visible;",
            reveal,
            StringComparison.Ordinal);
        Assert.True(reveal >= 0 && selectedViewBeforeReveal < visible);
    }

    [Fact]
    public void NewNoteDraft_DoesNotBlockSelectingAnotherNote()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "if (_isCreatingDetail && !HasNewDetailContent())\n        {\n            // A blank draft has nothing to preserve.",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "await SaveDetailAsync(completeEditing: false);",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "private bool HasNewDetailContent() =>",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "await FlushPendingDetailSaveAsync();\n        if (_detailHasUnsavedChanges)\n        {\n            return;\n        }\n\n        OpenDetail(item);",
            code,
            StringComparison.Ordinal);
    }
}
