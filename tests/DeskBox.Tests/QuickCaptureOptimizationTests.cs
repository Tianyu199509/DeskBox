using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class QuickCaptureOptimizationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storeRoot;

    public QuickCaptureOptimizationTests()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _storeRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "quick-capture")).FullName;
    }

    [Fact]
    public void ExtractInlineTags_SupportsNestedTagsAndSkipsHeadingsUrlsHexNumbersAndCode()
    {
        string markdown = """
            # Release Checklist
            Track #工作/周报 and #dev/winui-3 items for issue #580.
            Colors like #FFF and #1E1E1E or #FF00AA88 must not become tags.
            Docs link: https://deskbox.fun/guide#installation and inline `#ignored_inline`.
            ```csharp
            // #ignored_in_fence
            string color = "#abc123";
            ```
            Final tag: #灵感!
            """;

        List<string> tags = QuickCaptureTagParser.ExtractInlineTags(markdown, title: "#置顶想法");

        Assert.Equal(["置顶想法", "工作/周报", "dev/winui-3", "灵感"], tags);
    }

    [Fact]
    public void MergeInlineTags_RemovesDeletedInlineTagsWhileKeepingManualTags()
    {
        IReadOnlyList<string> existing = ["manual-tag", "old-inline", "keep-inline"];
        string previousBody = "Note with #old-inline and #keep-inline";
        string nextBody = "Note with #keep-inline and #new/nested";

        List<string> merged = QuickCaptureTagParser.MergeInlineTags(
            existing,
            previousTitle: null,
            previousBody: previousBody,
            nextTitle: null,
            nextBody: nextBody);

        Assert.Equal(["manual-tag", "keep-inline", "new/nested"], merged);
    }

    [Fact]
    public void ExtractChecklistStatsAndSplitForTodo_ParsesMarkdownTasksIgnoringCodeFences()
    {
        string body = """
            # Sprint Retrospective
            Context paragraph before tasks.
            - [x] Ship inline composer
            - [ ] Verify popout sticky window
            ```markdown
            - [ ] Ignored fenced task
            ```
            Closing note line.
            """;

        (int completed, int total) = QuickCaptureTagParser.ExtractChecklistStats(body);
        Assert.Equal((1, 2), (completed, total));

        var item = new QuickCaptureItem
        {
            Body = body,
            ContentFormat = TextContentFormat.Markdown
        };

        (string title, List<TodoStep> steps, string? notes) = QuickCaptureTagParser.SplitForTodoConversion(item, "Fallback");
        Assert.Equal("Sprint Retrospective", title);
        Assert.Equal(2, steps.Count);
        Assert.True(steps[0].IsCompleted);
        Assert.Equal("Ship inline composer", steps[0].Text);
        Assert.False(steps[1].IsCompleted);
        Assert.Equal("Verify popout sticky window", steps[1].Text);
        Assert.NotNull(notes);
        Assert.Contains("Context paragraph before tasks.", notes, StringComparison.Ordinal);
        Assert.Contains("Closing note line.", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_AutoSyncsInlineTagsOnCreateAndEdit()
    {
        var service = CreateService();

        QuickCaptureItem created = await service.AddItemAsync(" Prepare release notes #work/release #urgent ");
        Assert.Equal(["work/release", "urgent"], created.Tags);

        QuickCaptureWriteResult updated = await service.UpdateItemDetailsWithResultAsync(
            created.Id,
            title: null,
            body: "Prepare release notes #work/release #done",
            appearancePreset: created.AppearancePreset,
            contentFormat: TextContentFormat.Markdown);

        Assert.True(updated.Saved);
        Assert.NotNull(updated.Item);
        Assert.Equal(["work/release", "done"], updated.Item!.Tags);
    }

    [Fact]
    public async Task SetArchivedAsync_ArchivesUnpinsAndRestoresBatchInSingleStore()
    {
        var service = CreateService();
        QuickCaptureItem pinned = await service.AddDetailedItemAsync(
            "Pinned note",
            "Body A",
            QuickCaptureAppearancePreset.StickyYellow,
            pin: true);
        QuickCaptureItem regular = await service.AddItemAsync("Body B");

        int archivedCount = await service.SetArchivedAsync([pinned.Id, regular.Id], archived: true);
        Assert.Equal(2, archivedCount);

        QuickCaptureStoreData afterArchive = await service.GetDataAsync();
        Assert.All(afterArchive.Items, item =>
        {
            Assert.NotNull(item.ArchivedAt);
            Assert.False(item.IsPinned);
        });

        int unarchivedCount = await service.SetArchivedAsync(pinned.Id, archived: false);
        Assert.Equal(1, unarchivedCount);

        QuickCaptureStoreData afterUnarchive = await service.GetDataAsync();
        Assert.Null(afterUnarchive.Items.Single(i => i.Id == pinned.Id).ArchivedAt);
        Assert.NotNull(afterUnarchive.Items.Single(i => i.Id == regular.Id).ArchivedAt);
    }

    [Fact]
    public async Task MergeItemsAsync_CombinesBodiesTagsAndAttachmentsAndAllowsUndoSnapshots()
    {
        var service = CreateService();
        string attachmentPath = Path.Combine(_tempRoot, "spec.txt");
        await File.WriteAllTextAsync(attachmentPath, "spec-content");

        QuickCaptureItem first = await service.AddDetailedItemAsync(
            "Part 1",
            "Alpha section #roadmap",
            QuickCaptureAppearancePreset.Mint,
            TextContentFormat.Markdown,
            pin: true);
        QuickCaptureItem second = (await service.AddItemWithAttachmentsAsync(
            [attachmentPath],
            copyToManagedStorage: false))!;
        second = (await service.UpdateItemDetailsWithResultAsync(
            second.Id,
            title: null,
            body: "Beta section #design",
            appearancePreset: second.AppearancePreset,
            contentFormat: TextContentFormat.Markdown)).Item!;

        QuickCaptureMergeResult mergeResult = await service.MergeItemsAsync([first.Id, second.Id]);

        Assert.NotNull(mergeResult.MergedItem);
        Assert.Equal(2, mergeResult.SourceSnapshots.Count);
        Assert.True(mergeResult.MergedItem!.IsPinned);
        Assert.Contains("## Part 1", mergeResult.MergedItem.Body, StringComparison.Ordinal);
        Assert.Contains("---", mergeResult.MergedItem.Body, StringComparison.Ordinal);
        Assert.Contains("Beta section #design", mergeResult.MergedItem.Body, StringComparison.Ordinal);
        Assert.Contains("roadmap", mergeResult.MergedItem.Tags);
        Assert.Contains("design", mergeResult.MergedItem.Tags);
        Assert.Single(mergeResult.MergedItem.Attachments);

        QuickCaptureStoreData activeData = await service.GetDataAsync();
        QuickCaptureItem remaining = Assert.Single(activeData.Items.Where(i => !i.IsDeleted));
        Assert.Equal(mergeResult.MergedItem.Id, remaining.Id);
    }

    [Fact]
    public async Task MirrorService_WritesFrontmatterAssetsAndSparesUserMarkdownFiles()
    {
        string mirrorDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "obsidian-vault")).FullName;
        string userFile = Path.Combine(mirrorDir, "My Own Research.md");
        await File.WriteAllTextAsync(userFile, "# Hand-written Obsidian note");

        string imageSource = Path.Combine(_tempRoot, "diagram.png");
        await File.WriteAllBytesAsync(imageSource, [137, 80, 78, 71, 13, 10, 26, 10]);

        var service = CreateService();
        await service.SetMirrorFolderPathAsync(mirrorDir);
        Assert.Equal(mirrorDir, await service.GetMirrorFolderPathAsync());

        QuickCaptureItem note = (await service.AddItemWithAttachmentsAsync(
            [imageSource],
            copyToManagedStorage: false))!;
        note = (await service.UpdateItemDetailsWithResultAsync(
            note.Id,
            title: "Architecture Sketch",
            body: "See attached diagram #arch/v2",
            appearancePreset: note.AppearancePreset,
            contentFormat: TextContentFormat.Markdown)).Item!;

        using var mirrorService = new QuickCaptureMarkdownMirrorService(service);
        int written = await mirrorService.SyncNowAsync();
        Assert.Equal(1, written);

        string[] mirroredFiles = Directory.GetFiles(mirrorDir, "*__qc_*.md");
        string mirroredPath = Assert.Single(mirroredFiles);
        string mirroredContent = await File.ReadAllTextAsync(mirroredPath);
        Assert.Contains("title: \"Architecture Sketch\"", mirroredContent, StringComparison.Ordinal);
        Assert.Contains("arch/v2", mirroredContent, StringComparison.Ordinal);
        Assert.Contains("assets/", mirroredContent, StringComparison.Ordinal);

        await service.DeleteItemAsync(note.Id);
        await mirrorService.SyncNowAsync();

        Assert.Empty(Directory.GetFiles(mirrorDir, "*__qc_*.md"));
        Assert.True(File.Exists(userFile));

        // Verify standalone batch export produces clean readable .md files as well.
        QuickCaptureItem exportNote = await service.AddDetailedItemAsync(
            "Weekly Summary",
            "- [x] Done item",
            QuickCaptureAppearancePreset.Default,
            TextContentFormat.Markdown);
        string exportDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "exports")).FullName;
        IReadOnlyList<string> exported = await QuickCaptureMarkdownMirrorService.ExportItemsToDirectoryAsync(
            [exportNote],
            exportDir);
        string exportedFile = Assert.Single(exported);
        Assert.EndsWith("Weekly Summary.md", exportedFile, StringComparison.Ordinal);
        Assert.True(File.Exists(exportedFile));
    }

    [Fact]
    public async Task SearchResultActionService_SaveFileToNote_UsesLiveQuickCaptureServiceWithoutCacheStomping()
    {
        var service = CreateService();
        QuickCaptureItem liveNote = await service.AddItemAsync("In-memory live note");

        string linkedFile = Path.Combine(_tempRoot, "report.pdf");
        await File.WriteAllBytesAsync(linkedFile, [1, 2, 3]);

        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        var settingsService = new SettingsService(settingsPath);
        var actionService = new SearchResultActionService(settingsService, service);

        bool saved = await actionService.SaveFileToNoteAsync(linkedFile);
        Assert.True(saved);

        QuickCaptureStoreData data = await service.GetDataAsync();
        Assert.Equal(2, data.Items.Count);
        Assert.Contains(data.Items, item => item.Id == liveNote.Id);
        Assert.Contains(data.Items, item => item.Title == "report.pdf" && item.Attachments.Count == 1);
    }

    [Theory]
    [InlineData("note Ship v1.6 #work", true, "Ship v1.6 #work")]
    [InlineData("n: Quick thought", true, "Quick thought")]
    [InlineData("随记 下午三点评审", true, "下午三点评审")]
    [InlineData("记：买牛奶", true, "买牛奶")]
    [InlineData("notebook", false, "")]
    [InlineData("note   ", false, "")]
    public void TryParseQuickNoteCommand_RecognizesPrefixesWithoutFalsePositives(
        string query,
        bool expectedMatch,
        string expectedBody)
    {
        bool matched = SearchEngineService.TryParseQuickNoteCommand(query, out string actualBody);
        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedBody, actualBody);
    }

    [Fact]
    public void TryFormatSmartUrlPaste_WrapsSingleLineSelectionIntoMarkdownLink()
    {
        Assert.True(MarkdownSourceEditor.TryFormatSmartUrlPaste(
            "DeskBox Docs",
            "https://deskbox.fun/wiki_(v2)",
            out string link));
        Assert.Equal("[DeskBox Docs](https://deskbox.fun/wiki_%28v2%29)", link);

        Assert.False(MarkdownSourceEditor.TryFormatSmartUrlPaste(
            "https://already-a-url.com",
            "https://deskbox.fun",
            out _));
        Assert.False(MarkdownSourceEditor.TryFormatSmartUrlPaste(
            "Multi\nLine",
            "https://deskbox.fun",
            out _));
        Assert.False(MarkdownSourceEditor.TryFormatSmartUrlPaste(
            "DeskBox",
            "not a url",
            out _));
    }

    [Fact]
    public void ItemViewModel_ComputesTwoTierHierarchyChecklistAndTagSummaries()
    {
        var settingsService = new SettingsService(Path.Combine(_tempRoot, "vm-settings.json"));
        var localization = new LocalizationService(settingsService);
        var model = new QuickCaptureItem
        {
            Id = "item-1",
            Body = "# Project Kickoff\nAlign scope with design.\n- [x] Draft spec\n- [ ] Review UI",
            ContentFormat = TextContentFormat.Markdown,
            Tags = ["work", "q4", "ui"],
            IsPinned = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var vm = new QuickCaptureItemViewModel(model, localization, textSize: 13, iconSize: 16, searchText: null);

        Assert.Equal("Project Kickoff", vm.DisplayTitleLine);
        Assert.Contains("Align scope with design.", vm.DisplaySummaryText, StringComparison.Ordinal);
        Assert.True(vm.HasChecklist);
        Assert.Equal("1/2", vm.ChecklistBadgeText);
        Assert.Equal("#work #q4 +1", vm.TagsSummaryText);
    }

    [Fact]
    public void FeatureWidgetExportAndPopoutContracts_UseMarkdownExtensionAndTopmostExemption()
    {
        string featureWidgets = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string popoutCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCapturePopoutWindow.xaml.cs"));
        string appWindows = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.QuickCaptureWindows.cs"));

        Assert.Contains(
            "item.ContentFormat == TextContentFormat.Markdown ? \".md\" : \".txt\"",
            featureWidgets,
            StringComparison.Ordinal);
        Assert.Contains("presenter.IsAlwaysOnTop", popoutCode, StringComparison.Ordinal);
        Assert.Contains("AppendNonTopmostHandles(handles)", appWindows, StringComparison.Ordinal);
    }

    private QuickCaptureService CreateService()
    {
        return new QuickCaptureService(new QuickCaptureStore(_storeRoot));
    }

    public void Dispose()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
