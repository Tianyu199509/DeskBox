using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WidgetSurfaceLayoutUndoTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task FlatManualReorder_UndoRestoresRuntimeOrderPersistedOrderAndPriorSortMode()
    {
        (_, WidgetViewModel vm, _) = await CreateInitializedWidgetAsync(
            ["1.txt", "2.txt", "3.txt", "4.txt", "5.txt"],
            sortMode: WidgetSortMode.Name,
            fileStacksEnabled: false);

        using (vm)
        {
            Assert.Equal(["1", "2", "3", "4", "5"], vm.Items.Select(item => item.Name));

            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem item5 = vm.Items.Single(item => item.Name == "5");
            Assert.True(vm.MoveItemsForReorder([item5], targetIndex: 0));
            vm.PersistManualOrder();

            Assert.Equal(["5", "1", "2", "3", "4"], vm.Items.Select(item => item.Name));
            Assert.Equal(
                ["5", "1", "2", "3", "4"],
                vm.Config.Items.Select(item => Path.GetFileNameWithoutExtension(item.Path)));

            Assert.True(vm.TryUndoLatestLayoutChange());
            Assert.Equal(WidgetSortMode.Manual, vm.EffectiveSortMode);
            Assert.Equal(["1", "2", "3", "4", "5"], vm.Items.Select(item => item.Name));
            Assert.Equal(
                ["1", "2", "3", "4", "5"],
                vm.Config.Items.Select(item => Path.GetFileNameWithoutExtension(item.Path)));

            Assert.True(vm.TryUndoLatestLayoutChange());
            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            Assert.False(vm.HasLayoutUndoHistory);
            Assert.False(vm.TryUndoLatestLayoutChange());
        }
    }

    [Fact]
    public async Task CreateManualStackWithAnchorTarget_PlacesStackAtAnchorAndUndoDissolvesBackToLooseOrder()
    {
        (_, WidgetViewModel vm, _) = await CreateInitializedWidgetAsync(
            ["1.txt", "2.txt", "3.txt", "4.txt"],
            sortMode: WidgetSortMode.Manual,
            fileStacksEnabled: true);

        using (vm)
        {
            Assert.Equal(["1", "2", "3", "4"], vm.VisibleItems.Select(item => item.Name));

            WidgetItem item1 = vm.VisibleItems.Single(item => item.Name == "1");
            WidgetItem item4 = vm.VisibleItems.Single(item => item.Name == "4");

            Assert.True(vm.CreateManualStack([item4, item1], anchorTargetItem: item4));

            WidgetStackItem createdStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(["4", "1"], createdStack.Members.Select(member => member.Name));
            Assert.Equal(["2", "3", createdStack.Name], vm.VisibleItems.Select(item => item.Name));

            Assert.True(vm.TryUndoLatestLayoutChange());
            Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(["1", "2", "3", "4"], vm.VisibleItems.Select(item => item.Name));
        }
    }

    [Fact]
    public async Task InteractiveLayoutUndoBatch_CoalescesLivePreviewStepsAndSupportsCancelRevert()
    {
        (_, WidgetViewModel vm, _) = await CreateInitializedWidgetAsync(
            ["1.txt", "2.txt", "3.txt", "4.txt"],
            sortMode: WidgetSortMode.Manual,
            fileStacksEnabled: true);

        using (vm)
        {
            string[] initialOrder = vm.VisibleItems.Select(item => item.Name).ToArray();
            Assert.Equal(["1", "2", "3", "4"], initialOrder);

            vm.BeginInteractiveLayoutUndoBatch();
            WidgetItem item4 = vm.VisibleItems.Single(item => item.Name == "4");
            Assert.True(vm.MoveVisibleItemForReorder(item4, visibleInsertionIndex: 0));
            WidgetItem item3 = vm.VisibleItems.Single(item => item.Name == "3");
            Assert.True(vm.MoveVisibleItemForReorder(item3, visibleInsertionIndex: 1));
            vm.PersistManualOrder();
            Assert.True(vm.CommitInteractiveLayoutUndoBatch());

            Assert.NotNull(vm.GetLatestLayoutUndoTimestampUtc());
            Assert.Equal(["4", "3", "1", "2"], vm.VisibleItems.Select(item => item.Name));

            // Multi-step interactive drag coalesced into a single undo entry.
            Assert.True(vm.TryUndoLatestLayoutChange());
            Assert.Equal(initialOrder, vm.VisibleItems.Select(item => item.Name));
            Assert.False(vm.HasLayoutUndoHistory);

            // Reverting an interactive batch restores order without pushing onto the undo stack.
            vm.BeginInteractiveLayoutUndoBatch();
            WidgetItem item4Again = vm.VisibleItems.Single(item => item.Name == "4");
            Assert.True(vm.MoveVisibleItemForReorder(item4Again, visibleInsertionIndex: 0));
            Assert.Equal(["4", "1", "2", "3"], vm.VisibleItems.Select(item => item.Name));

            vm.RevertAndClearInteractiveLayoutUndoBatch();
            Assert.Equal(initialOrder, vm.VisibleItems.Select(item => item.Name));
            Assert.False(vm.HasLayoutUndoHistory);
        }
    }

    [Fact]
    public async Task LayoutUndoStack_CapsAtMaxLayoutUndoSnapshots()
    {
        (_, WidgetViewModel vm, _) = await CreateInitializedWidgetAsync(
            ["a.txt", "b.txt", "c.txt"],
            sortMode: WidgetSortMode.Manual,
            fileStacksEnabled: false);

        using (vm)
        {
            int totalMutations = WidgetViewModel.MaxLayoutUndoSnapshots + 5;
            for (int i = 0; i < totalMutations; i++)
            {
                WidgetItem lastItem = vm.Items[^1];
                Assert.True(vm.MoveItemsForReorder([lastItem], targetIndex: 0));
                vm.PersistManualOrder();
            }

            int undoneCount = 0;
            while (vm.TryUndoLatestLayoutChange())
            {
                undoneCount++;
            }

            Assert.Equal(WidgetViewModel.MaxLayoutUndoSnapshots, undoneCount);
            Assert.False(vm.HasLayoutUndoHistory);
        }
    }

    [Fact]
    public async Task LayoutUndo_ScopesToCurrentFolder_AndPreservesExpandedStackDuringMemberReorderUndo()
    {
        (SettingsService settings, WidgetViewModel vm, string rootPath) =
            await CreateInitializedWidgetAsync(
                ["1.txt", "2.txt", "3.txt"],
                sortMode: WidgetSortMode.Name,
                fileStacksEnabled: true,
                fileStackOpenMode: SettingsService.FileStackOpenModeInline);

        using (vm)
        {
            settings.Settings.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;

            string subFolder = Path.Combine(rootPath, "SubFolder");
            Directory.CreateDirectory(subFolder);
            await File.WriteAllTextAsync(Path.Combine(subFolder, "s1.txt"), "s1");
            await File.WriteAllTextAsync(Path.Combine(subFolder, "s2.txt"), "s2");
            await vm.RefreshFolderContentsAsync();

            // Capture a root-scope layout undo entry.
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem rootItem3 = vm.Items.Single(item => item.Name == "3");
            Assert.True(vm.MoveItemsForReorder([rootItem3], targetIndex: 0));
            vm.PersistManualOrder();
            Assert.NotNull(vm.GetLatestLayoutUndoTimestampForCurrentScopeUtc());

            // Enter subfolder: current-scope undo must NOT pop the root undo entry.
            WidgetItem folderItem = vm.Items.Single(item => item.IsFolder && item.Name == "SubFolder");
            Assert.True(await vm.NavigateIntoFolderAsync(folderItem));
            Assert.Null(vm.GetLatestLayoutUndoTimestampForCurrentScopeUtc());
            Assert.False(vm.TryUndoLatestLayoutChangeForCurrentScope());
            Assert.True(vm.HasLayoutUndoHistory);

            // Return to root: root layout undo entry is intact and undoable.
            Assert.True(await vm.NavigateUpAsync());
            Assert.NotNull(vm.GetLatestLayoutUndoTimestampForCurrentScopeUtc());
            Assert.True(vm.TryUndoLatestLayoutChangeForCurrentScope());

            // Expand an inline stack, reorder members inside it, and verify undo keeps it expanded.
            WidgetItem item1 = vm.VisibleItems.Single(item => !item.IsFolder && item.Name == "1");
            WidgetItem item2 = vm.VisibleItems.Single(item => !item.IsFolder && item.Name == "2");
            Assert.True(vm.CreateManualStack([item1, item2], anchorTargetItem: item1));

            WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            vm.SetStackExpanded(stack, true);
            Assert.True(Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>()).IsExpanded);

            WidgetItem secondMember = stack.Members[1];
            Assert.True(vm.MoveStackMembersForReorder(stack.StackKey, [secondMember], memberInsertionIndex: 0));

            Assert.True(vm.TryUndoLatestLayoutChangeForCurrentScope());
            WidgetStackItem afterReorderUndo = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.True(afterReorderUndo.IsExpanded);

            // Undoing stack creation dissolves the stack cleanly.
            Assert.True(vm.TryUndoLatestLayoutChangeForCurrentScope());
            Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
        }
    }

    [Fact]
    public void DragRemediationLocalizationKeys_ExistAndFormatCleanlyAcrossAllThirteenLocales()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(stringsDir, "*.json");
        Assert.Equal(13, localeFiles.Length);

        string[] requiredKeys =
        [
            "Search.DragOut.HoldShiftToMoveTip",
            "Search.DragOut.ElevatedThirdPartyHint",
            "Widget.LayoutUndoCompleted",
            "Widget.Stack.DragCaption.CreateWith",
        ];

        foreach (string filePath in localeFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(filePath));
            JsonElement root = doc.RootElement;
            foreach (string key in requiredKeys)
            {
                Assert.True(
                    root.TryGetProperty(key, out JsonElement value),
                    $"{Path.GetFileName(filePath)} is missing localization key '{key}'.");
                string? text = value.GetString();
                Assert.False(string.IsNullOrWhiteSpace(text));

                if (string.Equals(key, "Widget.Stack.DragCaption.CreateWith", StringComparison.Ordinal))
                {
                    Assert.Contains("{0}", text, StringComparison.Ordinal);
                    Assert.Contains("readme.txt", string.Format(text, "readme.txt"), StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task RestoreDepartedStackMemberships_IsolatesFolderScopes_RemapsCollisionRenamedUndoPaths_AndPersistsSourceWidgetId()
    {
        (SettingsService settings, WidgetViewModel vm, string rootPath) =
            await CreateInitializedWidgetAsync(
                ["a.txt", "b.txt"],
                sortMode: WidgetSortMode.Name,
                fileStacksEnabled: true);

        using (vm)
        {
            settings.Settings.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;

            string subFolder = Path.Combine(rootPath, "Sub");
            Directory.CreateDirectory(subFolder);
            await File.WriteAllTextAsync(Path.Combine(subFolder, "sub1.txt"), "sub1");
            await File.WriteAllTextAsync(Path.Combine(subFolder, "sub2.txt"), "sub2");
            await vm.RefreshFolderContentsAsync();

            WidgetItem itemA = vm.Items.Single(item => !item.IsFolder && item.Name == "a");
            WidgetItem itemB = vm.Items.Single(item => !item.IsFolder && item.Name == "b");
            string pathA = itemA.Path;
            string pathB = itemB.Path;
            Assert.True(vm.CreateManualStack([itemA, itemB]));
            Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());

            // Moving out one member dissolves the 2-item stack and captures a root-scope snapshot.
            File.Delete(pathA);
            await vm.HandleItemsMovedOutAsync([pathA]);
            Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());

            // Navigating into a subfolder isolates ScopeKey so root departed snapshots do not match.
            WidgetItem folderItem = vm.Items.Single(item => item.IsFolder && item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(folderItem));
            await File.WriteAllTextAsync(pathA, "a");
            Assert.False(vm.RestoreDepartedStackMemberships([pathA]));

            // Return to root and simulate undo collision rename: a.txt -> a (2).txt.
            Assert.True(await vm.NavigateUpAsync());
            File.Delete(pathA);
            string renamedA = Path.Combine(rootPath, "a (2).txt");
            await File.WriteAllTextAsync(renamedA, "a-renamed");
            await vm.RefreshFolderContentsAsync();

            Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.True(vm.RestoreDepartedStackMemberships(
                [renamedA],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [pathA] = renamedA
                }));

            WidgetStackItem restoredStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(2, restoredStack.Members.Count);
            Assert.Contains(restoredStack.Members, member => string.Equals(member.Path, renamedA, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(restoredStack.Members, member => string.Equals(member.Path, pathB, StringComparison.OrdinalIgnoreCase));

            // Snapshot is removed once all resolved members are back in the stack.
            Assert.False(vm.RestoreDepartedStackMemberships([renamedA]));

            string moveDestDir = Directory.CreateDirectory(Path.Combine(rootPath, "MoveDest")).FullName;
            string movedSrc = Path.Combine(rootPath, "moved-note.txt");
            string movedDst = Path.Combine(moveDestDir, "moved-note.txt");
            await File.WriteAllTextAsync(movedDst, "moved-note");

            OrganizationHistoryEntry? recorded = await vm.RecordSubfolderMoveUndoAsync(
                [new FileService.FileTransferResult(movedSrc, movedDst)],
                new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase),
                sourceWidgetId: "origin-widget-42");

            Assert.NotNull(recorded);
            Assert.Equal("origin-widget-42", recorded!.SourceWidgetId);
            OrganizationHistoryEntry persisted = Assert.Single(
                settings.OrganizationHistory.Entries,
                entry => string.Equals(entry.Id, recorded.Id, StringComparison.Ordinal));
            Assert.Equal("origin-widget-42", persisted.SourceWidgetId);
        }
    }

    private async Task<(SettingsService Settings, WidgetViewModel ViewModel, string FolderPath)> CreateInitializedWidgetAsync(
        string[] fileNames,
        WidgetSortMode sortMode,
        bool fileStacksEnabled,
        string fileStackOpenMode = SettingsService.FileStackOpenModePopover)
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings", Guid.NewGuid().ToString("N"));
        string folderPath = Path.Combine(_tempRoot, "files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        foreach (string name in fileNames)
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = new SettingsService(settingsRoot);
        await settings.LoadAsync();
        settings.Settings.FileStacksEnabled = fileStacksEnabled;
        settings.Settings.FileStackAutoStacking = false;
        settings.Settings.FileStackOpenMode = fileStackOpenMode;
        settings.Settings.ShowFileExtensions = false;

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "LayoutUndoTest",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = sortMode
        };
        settings.Settings.Widgets.Add(config);

        var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();
        return (settings, vm, folderPath);
    }
}
