using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Tests;

public sealed class FileItemMultiDragTests
{
    [Theory]
    [InlineData(DataPackageOperation.Move, true, false, true, true)]
    [InlineData(DataPackageOperation.None, true, false, true, false)]
    [InlineData(DataPackageOperation.None, true, false, false, true)]
    [InlineData(DataPackageOperation.None, false, false, false, false)]
    [InlineData(DataPackageOperation.Move, true, true, true, false)]
    public void ShouldObserveExternalDragOut_DistinguishesPopoverCancellation(
        DataPackageOperation dropResult,
        bool hasStorageItems,
        bool handledAsStackMembership,
        bool fromStackPopover,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ShouldObserveExternalDragOut(
                dropResult,
                hasStorageItems,
                handledAsStackMembership,
                fromStackPopover));
    }

    [Theory]
    [InlineData(DataPackageOperation.Move, true, 1, 1, DataPackageOperation.None)]
    [InlineData(DataPackageOperation.Move, false, 1, 1, DataPackageOperation.Move)]
    [InlineData(DataPackageOperation.Move, false, 1, 0, DataPackageOperation.None)]
    [InlineData(DataPackageOperation.Move, false, 2, 1, DataPackageOperation.None)]
    [InlineData(DataPackageOperation.Copy, true, 1, 0, DataPackageOperation.Copy)]
    [InlineData(DataPackageOperation.Link, true, 1, 0, DataPackageOperation.Link)]
    public void ResolveSafeDropCompletionOperation_PreventsSourceCleanupBeforeMove(
        DataPackageOperation requestedOperation,
        bool isDeskBoxFileDrag,
        int requestedMoveCount,
        int completedMoveCount,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ResolveSafeDropCompletionOperation(
                requestedOperation,
                isDeskBoxFileDrag,
                requestedMoveCount,
                completedMoveCount));
    }

    [Fact]
    public void TryMoveStackMemberOverride_ReordersPersistedManualMembers()
    {
        List<string> paths =
        [
            @"E:\DeskBox\my\first.lnk",
            @"E:\DeskBox\my\second.lnk",
            @"E:\DeskBox\my\third.lnk"
        ];

        bool moved = WidgetViewModel.TryMoveStackMemberOverride(
            paths,
            @"E:\DeskBox\my\first.lnk",
            @"E:\DeskBox\my\third.lnk");

        Assert.True(moved);
        Assert.Equal(
        [
            @"E:\DeskBox\my\second.lnk",
            @"E:\DeskBox\my\third.lnk",
            @"E:\DeskBox\my\first.lnk"
        ], paths);
    }

    [Fact]
    public void TryMoveStackMemberOverrides_MovesSelectionAsOneStableBlock()
    {
        List<string> paths =
        [
            @"E:\DeskBox\first.lnk",
            @"E:\DeskBox\second.lnk",
            @"E:\DeskBox\third.lnk",
            @"E:\DeskBox\fourth.lnk"
        ];

        bool moved = WidgetViewModel.TryMoveStackMemberOverrides(
            paths,
            [
                @"E:\DeskBox\first.lnk",
                @"E:\DeskBox\third.lnk"
            ],
            insertionIndex: 4);

        Assert.True(moved);
        Assert.Equal(
        [
            @"E:\DeskBox\second.lnk",
            @"E:\DeskBox\fourth.lnk",
            @"E:\DeskBox\first.lnk",
            @"E:\DeskBox\third.lnk"
        ], paths);
    }

    [Fact]
    public void TryMoveStackMemberOverrides_DoesNotMutateEquivalentDrop()
    {
        List<string> paths =
        [
            @"E:\DeskBox\first.lnk",
            @"E:\DeskBox\second.lnk",
            @"E:\DeskBox\third.lnk"
        ];

        bool moved = WidgetViewModel.TryMoveStackMemberOverrides(
            paths,
            [@"E:\DeskBox\second.lnk"],
            insertionIndex: 2);

        Assert.False(moved);
        Assert.Equal(
        [
            @"E:\DeskBox\first.lnk",
            @"E:\DeskBox\second.lnk",
            @"E:\DeskBox\third.lnk"
        ], paths);
    }

    [Fact]
    public void ResolveDraggedItems_UsesFullSelectionWhenEventOnlyContainsAnchor()
    {
        WidgetItem first = CreateItem("first.txt");
        WidgetItem second = CreateItem("second.txt");
        WidgetItem third = CreateItem("third.txt");

        IReadOnlyList<WidgetItem> resolved = FileItemDragPackage.ResolveDraggedItems(
            [second],
            [first, second, third]);

        Assert.Equal([first, second, third], resolved);
    }

    [Fact]
    public void ResolveDraggedItems_DoesNotBorrowUnrelatedSelection()
    {
        WidgetItem dragged = CreateItem("dragged.txt");
        WidgetItem selectedFirst = CreateItem("selected-first.txt");
        WidgetItem selectedSecond = CreateItem("selected-second.txt");

        IReadOnlyList<WidgetItem> resolved = FileItemDragPackage.ResolveDraggedItems(
            [dragged],
            [selectedFirst, selectedSecond]);

        Assert.Equal([dragged], resolved);
    }

    [Fact]
    public void TryPrepare_WritesEveryResolvedPathToInternalDragPayload()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string firstPath = Path.Combine(tempDirectory, "first.txt");
        string secondPath = Path.Combine(tempDirectory, "second.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");

        try
        {
            WidgetItem first = CreateItem(firstPath);
            WidgetItem second = CreateItem(secondPath);
            var dataPackage = new DataPackage();

            bool prepared = FileItemDragPackage.TryPrepare(
                dataPackage,
                [first, second],
                "source-widget",
                _ => Array.Empty<IStorageItem>(),
                paths => paths.Count.ToString(),
                out FileItemDragPackageResult result);

            Assert.True(prepared);
            Assert.Equal([firstPath, secondPath], result.SourcePaths);
            Assert.True(dataPackage.Properties.TryGetValue(
                DeskBoxDragData.SourcePathsProperty,
                out object? payload));
            Assert.Equal([firstPath, secondPath], Assert.IsType<string[]>(payload));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static WidgetItem CreateItem(string path) => new()
    {
        Name = path,
        Path = path
    };
}
