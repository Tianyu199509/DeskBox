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
    [Fact]
    public void SourceDragOperations_AdvertiseCopyAndMoveWithoutPreference()
    {
        Assert.Equal(
            DataPackageOperation.Copy | DataPackageOperation.Move,
            FileItemDragPackage.SupportedOperations);
    }

    [Theory]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, DataPackageOperation.None)]
    [InlineData(SettingsService.ManagedDragOutActionMove, DataPackageOperation.Move)]
    [InlineData(SettingsService.ManagedDragOutActionCopy, DataPackageOperation.Copy)]
    [InlineData("Nonsense", DataPackageOperation.None)]
    [InlineData(null, DataPackageOperation.None)]
    public void ResolveDragOutPreferredOperation_MapsSettingToPreferredEffect(
        string? action,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            FileItemDragPackage.ResolveDragOutPreferredOperation(action));
    }

    [Theory]
    // Windows 11 keeps the full Copy|Move advertisement for every setting.
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true,
        DataPackageOperation.Copy | DataPackageOperation.Move)]
    [InlineData(SettingsService.ManagedDragOutActionMove, true,
        DataPackageOperation.Copy | DataPackageOperation.Move)]
    [InlineData(SettingsService.ManagedDragOutActionCopy, true,
        DataPackageOperation.Copy | DataPackageOperation.Move)]
    // Windows 10 collapses to a single effect: Explorer there prompts on
    // every multi-effect drop. The silent single-bit shape cannot carry
    // Windows' volume-dependent default, so an explicit setting performs
    // what it says while the default (FollowWindows) and unknown values
    // resolve to Copy — the safe side of the forced choice, because a
    // Move-only offer makes IM-style receivers resolve Move and delete
    // the original themselves (feedback 500).
    [InlineData(SettingsService.ManagedDragOutActionMove, false,
        DataPackageOperation.Move)]
    [InlineData(SettingsService.ManagedDragOutActionCopy, false,
        DataPackageOperation.Copy)]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, false,
        DataPackageOperation.Copy)]
    [InlineData("Nonsense", false, DataPackageOperation.Copy)]
    [InlineData(null, false, DataPackageOperation.Copy)]
    public void ResolveDragOutAllowedOperations_CollapsesToSingleEffectOnWin10(
        string? action,
        bool isWindows11OrLater,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                action,
                isWindows11OrLater));
    }

    [Fact]
    public void ResolveDragOutAllowedOperations_Win10DefaultNeverOffersMove()
    {
        // Feedback 500: a Move-only advertisement made the WeChat send box
        // resolve the drop to Move and delete the original into the Recycle
        // Bin itself. The Win10 default (FollowWindows) and unknown values
        // must therefore advertise Copy-only: receivers can resolve at most
        // Copy, so the original can never be removed by the target.
        foreach (string? action in new[]
                 {
                     SettingsService.ManagedDragOutActionFollowWindows,
                     "Nonsense",
                     null,
                 })
        {
            DataPackageOperation allowed =
                FileItemDragPackage.ResolveDragOutAllowedOperations(
                    action,
                    isWindows11OrLater: false);

            Assert.Equal(DataPackageOperation.Copy, allowed);
            Assert.False(allowed.HasFlag(DataPackageOperation.Move));
        }
    }

    [Fact]
    public void Win10DefaultDropOutCopyResolutionNeverEntersFullRemovalWatch()
    {
        // The defense chain for feedback 500 on Win10: the default tier
        // advertises Copy-only, a compliant receiver resolves Copy, and a
        // Copy completion only ever runs the brief existence probe — never
        // the full removal watch — so originals stay and no row is pruned.
        DataPackageOperation allowed =
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false);
        DataPackageOperation resolved = allowed & DataPackageOperation.Copy;

        Assert.Equal(
            FileSurfaceContent.ExternalDragObservation.Brief,
            FileSurfaceContent.ResolveExternalDragObservation(
                resolved,
                hasStorageItems: true,
                handledAsStackMembership: false,
                fromStackPopover: false));
    }

    [Fact]
    public void ExternalDragOutReconcilePath_DeletesNothingItself()
    {
        // DeskBox never deletes a drag-out source: the completion path may
        // only reconcile rows for files the RECEIVER already moved
        // (feedback 500's deletion was performed by WeChat, which is why
        // the file landed in the Recycle Bin rather than vanishing). Pin
        // that the observation method reconciles solely through row
        // removal and contains no Shell/File delete calls of its own.
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        int methodStart = source.IndexOf(
            "private async Task ObserveExternalDragOutAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ObserveExternalDragOutAsync not found.");
        int methodEnd = source.IndexOf(
            "private void SuppressDesktopDragOutArrivals(",
            StringComparison.Ordinal);
        Assert.True(
            methodEnd > methodStart,
            "Method boundary not found.");
        string method = source[methodStart..methodEnd];

        Assert.Contains("HandleItemsMovedOutAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntriesWithShell", method, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RecycleBin", method, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void ReleasedSurfaceReorder_OnlyCommitsConfirmedInternalTarget(
        bool reorderActive,
        bool hasLastPosition,
        bool pointerInsideRoot,
        bool hasActiveChildDropTarget,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ShouldCommitReleasedSurfaceReorder(
                reorderActive,
                hasLastPosition,
                pointerInsideRoot,
                hasActiveChildDropTarget));
    }

    [Theory]
    [InlineData(true, 0, true, 1, false, true)]
    [InlineData(true, 3, true, 2, false, true)]
    [InlineData(false, 0, true, 1, false, false)]
    [InlineData(true, -1, true, 1, false, false)]
    [InlineData(true, 0, false, 1, false, false)]
    [InlineData(true, 0, true, 0, false, false)]
    [InlineData(true, 0, true, 1, true, false)]
    public void ReleasedStackPopoverReorder_OnlyCommitsPendingInternalTarget(
        bool dragActive,
        int insertionIndex,
        bool pointerInsideItems,
        int sourcePathCount,
        bool handledAsStackMembership,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ShouldCommitReleasedStackPopoverReorder(
                dragActive,
                insertionIndex,
                pointerInsideItems,
                sourcePathCount,
                handledAsStackMembership));
    }

    [Theory]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move |
            DataPackageOperation.Link,
        DataPackageOperation.Move,
        DataPackageOperation.Link)]
    [InlineData(
        DataPackageOperation.Link,
        DataPackageOperation.Move,
        DataPackageOperation.Link)]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move |
            DataPackageOperation.Link,
        DataPackageOperation.Link,
        DataPackageOperation.Link)]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move,
        DataPackageOperation.Move,
        DataPackageOperation.Copy)]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move,
        DataPackageOperation.None,
        DataPackageOperation.Copy)]
    [InlineData(
        DataPackageOperation.Move,
        DataPackageOperation.None,
        DataPackageOperation.Move)]
    [InlineData(
        DataPackageOperation.None,
        DataPackageOperation.Move,
        DataPackageOperation.Move)]
    [InlineData(
        DataPackageOperation.None,
        DataPackageOperation.None,
        DataPackageOperation.None)]
    public void InternalArrangementFeedback_PrefersLinkThenCopyThenMove(
        DataPackageOperation allowedOperations,
        DataPackageOperation requestedOperation,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ResolveInternalArrangementFeedbackOperation(
                allowedOperations,
                requestedOperation));
    }

    [Theory]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move,
        DataPackageOperation.Move)]
    [InlineData(DataPackageOperation.Move, DataPackageOperation.Move)]
    [InlineData(DataPackageOperation.Copy, DataPackageOperation.Copy)]
    [InlineData(DataPackageOperation.None, DataPackageOperation.None)]
    public void InternalMetadataOperation_AnswersOnlyAnOfferedEffect(
        DataPackageOperation allowedOperations,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            DeskBoxDragData.ResolveInternalMetadataOperation(
                allowedOperations));
    }

    [Fact]
    public void FileAssociationOperation_FallsBackToMoveForSingleEffectInternalDrags()
    {
        var package = new DataPackage();
        package.Properties[DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        package.Properties[DeskBoxDragData.SourcePathsProperty] =
            new[] { @"E:\DeskBox\one.txt" };
        DataPackageView view = package.GetView();

        // Windows 10 advertises a single Move: todo/quick-capture attach
        // targets must still route the drop.
        Assert.Equal(
            DataPackageOperation.Move,
            DeskBoxDragData.GetFileAssociationOperation(
                view,
                DataPackageOperation.Move));
        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.GetFileAssociationOperation(
                view,
                DataPackageOperation.Copy | DataPackageOperation.Move));
        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.GetFileAssociationOperation(view));
        // External sources keep their negotiated answer.
        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.GetFileAssociationOperation(
                new DataPackage().GetView(),
                DataPackageOperation.Move));
    }

    [Fact]
    public void FileDragFeedbackOperation_FallsBackToMoveWhenCopyIsNotAdvertised()
    {
        var package = new DataPackage();
        package.Properties[DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        package.Properties[DeskBoxDragData.SourcePathsProperty] =
            new[] { @"E:\DeskBox\one.txt" };
        DataPackageView view = package.GetView();

        // Internal drag, single-Move advertisement (Windows 10): feedback
        // must answer with the one offered effect or the drop never routes.
        Assert.Equal(
            DataPackageOperation.Move,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(
                view,
                DataPackageOperation.Move,
                DataPackageOperation.Move));
        // The full advertisement and the default argument keep answering
        // Copy so completion never authorizes shell source cleanup.
        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(
                view,
                DataPackageOperation.Move));
        Assert.Equal(
            DataPackageOperation.Move,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(
                view,
                DataPackageOperation.Copy,
                DataPackageOperation.Move));
        // Non-internal drags pass their negotiated operation through.
        Assert.Equal(
            DataPackageOperation.Move,
            DeskBoxDragData.ResolveFileDragFeedbackOperation(
                new DataPackage().GetView(),
                DataPackageOperation.Move,
                DataPackageOperation.Move));
    }

    [Theory]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move |
            DataPackageOperation.Link,
        DataPackageOperation.Move,
        DataPackageOperation.Link)]
    [InlineData(
        DataPackageOperation.Link,
        DataPackageOperation.Move,
        DataPackageOperation.Link)]
    [InlineData(
        DataPackageOperation.None,
        DataPackageOperation.Move,
        DataPackageOperation.None)]
    [InlineData(
        DataPackageOperation.Move,
        DataPackageOperation.Move,
        DataPackageOperation.None)]
    [InlineData(
        DataPackageOperation.Copy | DataPackageOperation.Move,
        DataPackageOperation.Move,
        DataPackageOperation.Copy)]
    [InlineData(
        DataPackageOperation.None,
        DataPackageOperation.None,
        DataPackageOperation.None)]
    public void InternalArrangementCompletion_NeverAuthorizesSourceMove(
        DataPackageOperation allowedOperations,
        DataPackageOperation requestedOperation,
        DataPackageOperation expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ResolveInternalArrangementCompletionOperation(
                allowedOperations,
                requestedOperation));
    }

    [Theory]
    [InlineData(true, "old", "new", false, true)]
    [InlineData(false, "same", "same", false, true)]
    [InlineData(false, "old", "new", true, false)]
    [InlineData(false, "old", null, true, false)]
    [InlineData(false, null, "old", true, false)]
    [InlineData(false, null, null, true, true)]
    [InlineData(false, null, null, false, false)]
    public void DragPayloadCache_IsScopedToDeskBoxDragSession(
        bool sameDataView,
        string? incomingSessionId,
        string? cachedSessionId,
        bool sameLegacyPayload,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.CanReuseDragPayloadSnapshot(
                sameDataView,
                incomingSessionId,
                cachedSessionId,
                sameLegacyPayload));
    }

    [Theory]
    [InlineData(DataPackageOperation.Move, true, false, true,
        FileSurfaceContent.ExternalDragObservation.Full)]
    [InlineData(DataPackageOperation.Move, true, false, false,
        FileSurfaceContent.ExternalDragObservation.Full)]
    [InlineData(DataPackageOperation.None, true, false, true,
        FileSurfaceContent.ExternalDragObservation.Brief)]
    [InlineData(DataPackageOperation.None, true, false, false,
        FileSurfaceContent.ExternalDragObservation.Full)]
    [InlineData(DataPackageOperation.Copy, true, false, true,
        FileSurfaceContent.ExternalDragObservation.Brief)]
    [InlineData(DataPackageOperation.Copy, true, false, false,
        FileSurfaceContent.ExternalDragObservation.Brief)]
    [InlineData(DataPackageOperation.None, false, false, false,
        FileSurfaceContent.ExternalDragObservation.None)]
    [InlineData(DataPackageOperation.Move, false, false, false,
        FileSurfaceContent.ExternalDragObservation.None)]
    [InlineData(DataPackageOperation.Link, true, false, false,
        FileSurfaceContent.ExternalDragObservation.None)]
    [InlineData(DataPackageOperation.Move, true, true, false,
        FileSurfaceContent.ExternalDragObservation.None)]
    public void ResolveExternalDragObservation_DistinguishesPopoverCancellation(
        DataPackageOperation dropResult,
        bool hasStorageItems,
        bool handledAsStackMembership,
        bool fromStackPopover,
        FileSurfaceContent.ExternalDragObservation expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ResolveExternalDragObservation(
                dropResult,
                hasStorageItems,
                handledAsStackMembership,
                fromStackPopover));
    }

    [Theory]
    [InlineData(
        DataPackageOperation.Copy,
        DataPackageOperation.Copy | DataPackageOperation.Move,
        "Widget.DragOutTip.AfterCopy")]
    [InlineData(
        DataPackageOperation.Move,
        DataPackageOperation.Copy | DataPackageOperation.Move,
        "Widget.DragOutTip.AfterMove")]
    // Windows 10 collapses the advertisement to a single effect: nothing
    // can be flipped, so the receipt stays a plain statement.
    [InlineData(
        DataPackageOperation.Copy,
        DataPackageOperation.Copy,
        "Widget.DragOutTip.AfterCopy.NoModifiers")]
    [InlineData(
        DataPackageOperation.Copy,
        DataPackageOperation.Move,
        "Widget.DragOutTip.AfterCopy.NoModifiers")]
    [InlineData(
        DataPackageOperation.Move,
        DataPackageOperation.Copy,
        "Widget.DragOutTip.AfterMove.NoModifiers")]
    [InlineData(
        DataPackageOperation.Move,
        DataPackageOperation.Move,
        "Widget.DragOutTip.AfterMove.NoModifiers")]
    // None is an optimized move or a cancel; Link is not a drag-out outcome.
    [InlineData(
        DataPackageOperation.Link,
        DataPackageOperation.Copy | DataPackageOperation.Move,
        null)]
    [InlineData(
        DataPackageOperation.None,
        DataPackageOperation.Copy | DataPackageOperation.Move,
        null)]
    [InlineData(DataPackageOperation.Link, DataPackageOperation.Copy, null)]
    [InlineData(DataPackageOperation.None, DataPackageOperation.Move, null)]
    // An extra advertised bit (Link) does not loosen the Copy+Move rule.
    [InlineData(
        DataPackageOperation.Copy,
        DataPackageOperation.Copy | DataPackageOperation.Move |
            DataPackageOperation.Link,
        "Widget.DragOutTip.AfterCopy")]
    public void GetDragOutResultHintKey_TeachesModifiersOnlyWhenCopyAndMoveAdvertised(
        DataPackageOperation dropResult,
        DataPackageOperation allowedOperations,
        string? expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.GetDragOutResultHintKey(
                dropResult,
                allowedOperations));
    }

    [Theory]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows)]
    [InlineData(SettingsService.ManagedDragOutActionMove)]
    [InlineData(SettingsService.ManagedDragOutActionCopy)]
    [InlineData("Nonsense")]
    [InlineData(null)]
    public void GetDragOutResultHintKey_Win10AdvertisementNeverTeachesModifiers(
        string? action)
    {
        // Windows 10 resolves every setting to a single-effect
        // advertisement, so the receipt there must never claim a modifier
        // could have flipped the result.
        DataPackageOperation allowed =
            FileItemDragPackage.ResolveDragOutAllowedOperations(action, false);

        string? copyKey = FileSurfaceContent.GetDragOutResultHintKey(
            DataPackageOperation.Copy,
            allowed);
        string? moveKey = FileSurfaceContent.GetDragOutResultHintKey(
            DataPackageOperation.Move,
            allowed);

        Assert.True(copyKey is null ||
            copyKey.EndsWith(".NoModifiers", StringComparison.Ordinal));
        Assert.True(moveKey is null ||
            moveKey.EndsWith(".NoModifiers", StringComparison.Ordinal));
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
            Assert.True(result.UsesNativeShellDataObject);
            Assert.Equal(DataPackageOperation.None, dataPackage.GetView().RequestedOperation);
            // Chromium maps CF_UNICODETEXT to text/plain + text/uri-list and
            // Electron drop zones then stop treating the drag as files.
            Assert.False(dataPackage.GetView().Contains(StandardDataFormats.Text));
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

    [Fact]
    public void TryPrepare_ShortcutDragRequestsNoPreferredOperation()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string shortcutPath = Path.Combine(tempDirectory, "Managed app.lnk");
        File.WriteAllBytes(shortcutPath, [0x4C, 0x00, 0x00, 0x00]);

        try
        {
            var dataPackage = new DataPackage();

            bool prepared = FileItemDragPackage.TryPrepare(
                dataPackage,
                [CreateItem(shortcutPath)],
                "source-widget",
                _ => Array.Empty<IStorageItem>(),
                paths => paths.Count.ToString(),
                out _);

            Assert.True(prepared);
            Assert.Equal(
                DataPackageOperation.None,
                dataPackage.GetView().RequestedOperation);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    public void EmptyState_TracksSourceItemsWithoutWaitingForStackProjection(
        bool isLoading,
        int sourceItemCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileSurfaceContent.ShouldShowEmptyState(
                isLoading,
                sourceItemCount));
    }

    private static WidgetItem CreateItem(string path) => new()
    {
        Name = path,
        Path = path
    };
}
