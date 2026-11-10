using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class Round3Batch2MuscleMemoryTests
{
    [Fact]
    public void Ux1_AutoScrollAndMarqueeScrollAnchor_ComputeExpectedDeltasAndShiftedAnchor()
    {
        // Center dead-zone does not scroll
        Assert.Equal(0, FileSurfaceContent.ComputeAutoScrollDelta(120, 240, 50, 400));

        // Near bottom edge scrolls downward (+), near top edge scrolls upward (-)
        Assert.True(FileSurfaceContent.ComputeAutoScrollDelta(236, 240, 50, 400) > 0);
        Assert.True(FileSurfaceContent.ComputeAutoScrollDelta(4, 240, 50, 400) < 0);

        // Unclamped marquee drag pulled outside bottom/top border scrolls at full speed
        Assert.True(FileSurfaceContent.ComputeAutoScrollDelta(280, 240, 50, 400) > 0);
        Assert.True(FileSurfaceContent.ComputeAutoScrollDelta(-24, 240, 50, 400) < 0);

        // Shifting marquee anchor opposite to scroll delta keeps selection anchored to scrolled items
        Windows.Foundation.Point shifted = FileSurfaceContent.ShiftMarqueeAnchorForScroll(
            new Windows.Foundation.Point(40, 180),
            actualScrolledX: 0,
            actualScrolledY: 22);
        Assert.Equal(40, shifted.X);
        Assert.Equal(158, shifted.Y);
    }

    [Fact]
    public async Task Ux2_SubfolderNavigationDropTargets_ResolveParentAndMappedRootAccurately()
    {
        string tempRoot = CreateTempTestDir();
        try
        {
            string mappedRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "WorkRoot")).FullName;
            string subA = Directory.CreateDirectory(Path.Combine(mappedRoot, "SubA")).FullName;
            string subB = Directory.CreateDirectory(Path.Combine(subA, "SubB")).FullName;

            var settingsService = new SettingsService(Path.Combine(tempRoot, "data"));
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settingsService, fileService, () => tempRoot);
            var config = new WidgetConfig
            {
                Id = "ux2-widget",
                Name = "WorkBox",
                MappedFolderPath = mappedRoot,
            };
            config.Metadata[FileWidgetFolderOpenBehaviorNames.MetadataKey] =
                FileWidgetFolderOpenBehaviorNames.Embedded;
            using var vm = new WidgetViewModel(
                config,
                fileService,
                organizer,
                settingsService,
                TestServices.CreateLocalizationService(),
                dispatcherQueue: null);
            await vm.InitializeAsync();

            // At mapped root, neither Up nor Root drop target should resolve
            Assert.False(vm.TryGetParentNavigationFolderTarget(out _, out _));
            Assert.False(vm.TryGetRootNavigationFolderTarget(out _, out _));

            // Navigate into SubA -> SubB
            WidgetItem itemSubA = Assert.Single(vm.Items, i => i.IsFolder && i.Name == "SubA");
            Assert.True(await vm.NavigateIntoFolderAsync(itemSubA));
            WidgetItem itemSubB = Assert.Single(vm.Items, i => i.IsFolder && i.Name == "SubB");
            Assert.True(await vm.NavigateIntoFolderAsync(itemSubB));

            Assert.True(vm.CanNavigateUp);
            Assert.True(vm.TryGetParentNavigationFolderTarget(out string parentPath, out string parentName));
            Assert.Equal(subA, parentPath);
            Assert.Equal("SubA", parentName);

            Assert.True(vm.TryGetRootNavigationFolderTarget(out string rootPath, out string rootName));
            Assert.Equal(mappedRoot, rootPath);
            Assert.Equal("WorkBox", rootName);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Ux3_SearchRowModifierPress_AllowsCtrlAndShiftDragWithoutPrematureDeselectOrRangeWipe()
    {
        // Ctrl on already multi-selected row defers toggle-off until PointerReleased (so Ctrl+Drag drags all selected)
        var ctrlSelected = SearchPopupWindow.EvaluateSearchRowModifierPress(
            ctrlDown: true,
            shiftDown: false,
            itemAlreadyMultiSelected: true,
            itemIsPrimarySelected: true,
            hasValidAnchor: true);
        Assert.True(ctrlSelected.DeferToggleOffToRelease);
        Assert.False(ctrlSelected.RangeSelect);

        // Ctrl on unselected row adds immediately (not deferred) and arms drag candidate
        var ctrlUnselected = SearchPopupWindow.EvaluateSearchRowModifierPress(
            ctrlDown: true,
            shiftDown: false,
            itemAlreadyMultiSelected: false,
            itemIsPrimarySelected: false,
            hasValidAnchor: true);
        Assert.False(ctrlUnselected.DeferToggleOffToRelease);

        // Shift on already-selected row preserves selection for Shift+Drag (Move) instead of range-wiping
        var shiftSelected = SearchPopupWindow.EvaluateSearchRowModifierPress(
            ctrlDown: false,
            shiftDown: true,
            itemAlreadyMultiSelected: false,
            itemIsPrimarySelected: true,
            hasValidAnchor: true);
        Assert.True(shiftSelected.PreserveExistingSelection);
        Assert.False(shiftSelected.RangeSelect);

        // Shift on unselected row with valid anchor performs range select
        var shiftUnselected = SearchPopupWindow.EvaluateSearchRowModifierPress(
            ctrlDown: false,
            shiftDown: true,
            itemAlreadyMultiSelected: false,
            itemIsPrimarySelected: false,
            hasValidAnchor: true);
        Assert.False(shiftUnselected.PreserveExistingSelection);
        Assert.True(shiftUnselected.RangeSelect);
    }

    [Fact]
    public void Ux4_FileSurfacePendingDragSnapshot_PreservesSingleSelectionAndBlocksAnchorlessShiftRangeSelect()
    {
        var itemA = new WidgetItem { Name = "a.txt", Path = @"C:\Demo\a.txt" };
        var itemB = new WidgetItem { Name = "b.txt", Path = @"C:\Demo\b.txt" };
        var itemH = new WidgetItem { Name = "h.mp4", Path = @"C:\Demo\h.mp4" };

        // Single selected item pressed with Shift or Ctrl keeps [itemH]
        WidgetItem[] singleSelected = FileSurfaceContent.ResolvePendingPointerDragItemsOnPress(
            itemH,
            [itemH],
            shiftPressed: true);
        Assert.Single(singleSelected, itemH);

        // Multi-selected [itemA, itemB] pressed on itemB keeps [itemA, itemB]
        WidgetItem[] multiSelected = FileSurfaceContent.ResolvePendingPointerDragItemsOnPress(
            itemB,
            [itemA, itemB],
            shiftPressed: false);
        Assert.Equal([itemA, itemB], multiSelected);

        // Nothing selected + Shift+Press on itemH snapshots [itemH] so WinUI's hidden index-0 focus anchor cannot drag 0..H
        WidgetItem[] anchorlessShift = FileSurfaceContent.ResolvePendingPointerDragItemsOnPress(
            itemH,
            [],
            shiftPressed: true);
        Assert.Single(anchorlessShift, itemH);
    }

    [Fact]
    public void Ux5_ClipboardMultiSelectDragBatch_IncludesAllOrderedSelectedItemsWhenDraggingCheckedCard()
    {
        var localization = TestServices.CreateLocalizationService();
        ClipboardItemViewModel CreateCard(string id) => new(
            new ClipboardHistoryItem
            {
                Id = id,
                Kind = ClipboardItemKind.Files,
                FilePaths = [$@"C:\Demo\{id}.txt"],
            },
            localization,
            ClipboardSettingsSlice.SecretProtectionMask,
            showSourceApp: false,
            textSize: 13,
            secondaryTextSize: 11,
            previewMaxLines: 2,
            cardPadding: new Thickness(8),
            cardMinHeight: 40);

        var c1 = CreateCard("c1");
        var c2 = CreateCard("c2");
        var c3 = CreateCard("c3");
        c1.SetMultiSelectState(isMultiSelectMode: true, isSelected: true, selectionOrder: 1);
        c2.SetMultiSelectState(isMultiSelectMode: true, isSelected: true, selectionOrder: 2);
        c3.SetMultiSelectState(isMultiSelectMode: true, isSelected: false, selectionOrder: 0);

        IReadOnlyList<ClipboardItemViewModel> batch = ClipboardWidgetContent.ResolveMultiSelectDragBatch(
            c2,
            isMultiSelectMode: true,
            selectedInOrder: [c1, c2]);
        Assert.Equal([c1, c2], batch);

        // Dragging an unchecked card in multi-select mode drags only that card
        IReadOnlyList<ClipboardItemViewModel> uncheckedBatch = ClipboardWidgetContent.ResolveMultiSelectDragBatch(
            c3,
            isMultiSelectMode: true,
            selectedInOrder: [c1, c2]);
        Assert.Equal([c3], uncheckedBatch);
    }

    [Fact]
    public async Task Ux6_SubfolderMoveHistoryAndPasteProtection_SupportSafeUndoAndBlockProtectedCutMoves()
    {
        string tempRoot = CreateTempTestDir();
        try
        {
            string sourceDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Desktop")).FullName;
            string boxSubDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Box", "SubFolder")).FullName;
            string preExisting = Path.Combine(boxSubDir, "keep.txt");
            File.WriteAllText(preExisting, "keep");

            string srcFile = Path.Combine(sourceDir, "moved_into_sub.txt");
            File.WriteAllText(srcFile, "payload");

            var settingsService = new SettingsService(Path.Combine(tempRoot, "data"));
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settingsService, fileService, () => sourceDir);
            var widgetCfg = new WidgetConfig
            {
                Id = "w-undo",
                Name = "Box",
                MappedFolderPath = Path.Combine(tempRoot, "Box"),
            };

            IReadOnlyList<FileService.FileTransferResult> results = await fileService.TransferItemsWithResultAsync(
                [srcFile],
                boxSubDir,
                move: true,
                useShellProgress: false);
            Assert.Single(results);
            Assert.False(File.Exists(srcFile));

            OrganizationHistoryEntry? recorded = await organizer.RecordCompletedMoveHistoryAsync(
                widgetCfg,
                "Box",
                results);
            Assert.NotNull(recorded);
            Assert.True(recorded!.CanUndo);

            await organizer.UndoAsync(recorded.Id);
            Assert.True(File.Exists(srcFile));
            Assert.Equal("payload", File.ReadAllText(srcFile));
            Assert.True(File.Exists(preExisting));

            // Ctrl+V Cut paste protection downgrades protected USB/system/IM sources to Copy
            var protectedUsbOrigin = new SourceOriginSnapshot(
                InboundSourceProtectionKind.ExternalRemovableOrNetwork,
                HasMoveProtectedRoot: false,
                HasUnhydratedCloudPlaceholder: false,
                HasApplicationCacheSource: false,
                HasTransientTempOrMsixCacheSource: false);
            Assert.False(FileSurfaceContent.ResolveSafePasteMoveFlag(
                requestedMove: true,
                isDeskBoxInternalCut: false,
                sourceOrigin: protectedUsbOrigin));

            var protectedProgramOrigin = new SourceOriginSnapshot(
                InboundSourceProtectionKind.None,
                HasMoveProtectedRoot: true,
                HasUnhydratedCloudPlaceholder: false,
                HasApplicationCacheSource: false,
                HasTransientTempOrMsixCacheSource: false,
                PrefersShortcutByDefault: true);
            Assert.False(FileSurfaceContent.ResolveSafePasteMoveFlag(
                requestedMove: true,
                isDeskBoxInternalCut: false,
                sourceOrigin: protectedProgramOrigin));

            var localNormalOrigin = SourceOriginSnapshot.Empty;
            Assert.True(FileSurfaceContent.ResolveSafePasteMoveFlag(
                requestedMove: true,
                isDeskBoxInternalCut: false,
                sourceOrigin: localNormalOrigin));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempTestDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
}
