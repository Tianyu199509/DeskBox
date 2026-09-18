using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileOpenInteractionContractTests
{
    [Fact]
    public void ShortcutTargetProbe_TreatsUncHostRootAsUnverifiable()
    {
        string shortcutPath = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.Tests-{Guid.NewGuid():N}.lnk");
        File.WriteAllText(shortcutPath, "placeholder");
        try
        {
            ShortcutTargetProbeResult result = ShortcutTargetProbe.Probe(
                shortcutPath,
                @"\\10.0.10.8");

            Assert.Equal(ShortcutTargetKind.Unc, result.Kind);
            Assert.Equal(ShortcutTargetStatus.Unverifiable, result.Status);
            Assert.False(result.IsBroken);
        }
        finally
        {
            File.Delete(shortcutPath);
        }
    }

    [Fact]
    public void ShortcutTargetProbe_OnlyMarksMissingLocalTargetsAsBroken()
    {
        string shortcutPath = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.Tests-{Guid.NewGuid():N}.lnk");
        File.WriteAllText(shortcutPath, "placeholder");
        try
        {
            ShortcutTargetProbeResult result = ShortcutTargetProbe.Probe(
                shortcutPath,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

            Assert.Equal(ShortcutTargetKind.LocalFileSystem, result.Kind);
            Assert.Equal(ShortcutTargetStatus.Missing, result.Status);
            Assert.True(result.IsBroken);
        }
        finally
        {
            File.Delete(shortcutPath);
        }
    }

    [Fact]
    public void ShortcutTargetProbe_ExpandsEnvironmentVariablesBeforeClassification()
    {
        string target = Environment.GetEnvironmentVariable("TEMP") ??
            Path.GetTempPath();
        ShortcutTargetKind kind = ShortcutTargetProbe.Classify("%TEMP%");

        Assert.Equal(ShortcutTargetKind.LocalFileSystem, kind);
        Assert.True(Path.IsPathFullyQualified(target));
    }

    [Fact]
    public async Task OpenItemAsync_EmptyTargetReturnsFailureWithoutShellDispatch()
    {
        var item = new WidgetItem
        {
            Path = string.Empty,
            TargetPath = string.Empty,
            IsShortcut = false
        };

        FileService.OpenItemResult result = await FileService.OpenItemAsync(
            item,
            IntPtr.Zero);

        Assert.Equal(FileService.OpenItemResult.Failed, result);
    }

    [Fact]
    public async Task OpenItemAsync_HonorsCancellationBeforeQueueing()
    {
        var item = new WidgetItem
        {
            Path = string.Empty,
            TargetPath = string.Empty,
            IsShortcut = false
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileService.OpenItemAsync(
                item,
                IntPtr.Zero,
                cancellation.Token));
    }

    [Fact]
    public void FileSurfaceOpenPath_UsesAsyncLaunchAndResultFeedback()
    {
        string navigation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        string opening = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"));

        Assert.Contains("await OpenFileItemAsync(item)", navigation, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.OpenItemAsync(", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemFailed", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemBusy", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemShortcutTargetMissing", opening, StringComparison.Ordinal);
        // Success stays silent: the opened window is its own feedback.
        Assert.DoesNotContain("Widget.OpenItemDispatched", opening, StringComparison.Ordinal);
        Assert.Contains("OpenItem.DuplicateSuppressed", opening, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield()", opening, StringComparison.Ordinal);
    }

    [Fact]
    public void UnassociatedItems_DeferToPickerAndKeepFailureToasts()
    {
        string win32Helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        string openItem = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.OpenItem.cs"));
        string opening = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"));

        // Every Shell dispatch path reports a dismissed picker as a silent
        // success, and no dialog API exposes a reliable cancellation signal,
        // so the service defers unassociated items to the UI layer's picker
        // and a dismissal stays silent by design.
        Assert.Contains(
            "return OpenItemResult.RequiresOpenWithPicker;",
            openItem,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static bool HasShellOpenAssociation(string path)",
            win32Helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows.System.Launcher.LaunchFileAsync",
            opening,
            StringComparison.Ordinal);
        // A picker failure is still a real error path.
        Assert.Contains(
            "pickerFailed",
            opening,
            StringComparison.Ordinal);
        Assert.Contains(
            "Widget.OpenItemFailed",
            opening,
            StringComparison.Ordinal);
        // The cancelled-open outcome no longer exists anywhere.
        Assert.DoesNotContain(
            "Widget.OpenItemCancelled",
            opening,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenItemResult.Cancelled",
            openItem + File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/FileService.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FolderShortcutNavigation_ClosesStackPopoverOnlyBeforeRealReplacement()
    {
        string navigation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));

        int close = navigation.IndexOf(
            "CloseStackPopover();",
            StringComparison.Ordinal);
        int callback = navigation.IndexOf(
            "beforeItemsReplaced: () =>",
            StringComparison.Ordinal);

        Assert.True(callback >= 0);
        Assert.True(close > callback);
        Assert.Contains(
            "external or unverifiable",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "!isExternalFolderShortcut",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutTargetKind.NetworkDrive",
            navigation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileSurfaceOpenPath_ClearsOpenedSelectionOnlyAfterSuccessfulDispatch()
    {
        string opening = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"));
        int successBranch = opening.IndexOf(
            "else if (result == FileService.OpenItemResult.OpenedOrHandled)",
            StringComparison.Ordinal);

        Assert.True(successBranch >= 0);
        // Success stays silent: the opened window is its own feedback.
        Assert.DoesNotContain("Widget.OpenItemDispatched", opening, StringComparison.Ordinal);
        // A finished dispatch ends the double-click's selection gesture: the
        // definition plus one call per completing branch, each deselecting
        // immediately and once more after the pending input batch drains.
        Assert.Contains(
            "DispatcherQueuePriority.Low",
            opening,
            StringComparison.Ordinal);
        // Late native selection commits are also suppressed at the source:
        // fast dispatch paths can finish every scheduled deselect first.
        Assert.Contains(
            "RegisterOpenedItemSelectionSuppression(item);",
            opening,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOpenSelectionSuppressed(added)",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs")),
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            opening.Split(
                "ClearOpenedItemSelectionAfterDispatch(",
                StringSplitOptions.None).Length - 1);
        // Unassociated items get the system application picker here; a
        // dismissal is silent because the dialog APIs report it as success.
        Assert.Contains(
            "DisplayApplicationPicker",
            opening,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows.System.Launcher.LaunchFileAsync",
            opening,
            StringComparison.Ordinal);
        Assert.Contains(
            "generation != _openStateGeneration",
            opening[..successBranch],
            StringComparison.Ordinal);

        // The per-view deselect helper must clear both layouts, the stack
        // popover, and the pointer feedback without a blanket selection reset.
        Assert.Contains("ItemsGrid.SelectedItems.Remove(item)", opening, StringComparison.Ordinal);
        Assert.Contains("ItemsList.SelectedItems.Remove(item)", opening, StringComparison.Ordinal);
        Assert.Contains("ClearOpenedItemPointerFeedback(ItemsGrid, item)", opening, StringComparison.Ordinal);
        Assert.Contains("ClearOpenedItemPointerFeedback(ItemsList, item)", opening, StringComparison.Ordinal);
        Assert.Contains("_stackPopoverItemsView?.SelectedItems.Remove(item)", opening, StringComparison.Ordinal);
        Assert.Contains("stackPopoverGeneration == _stackPopoverShowGeneration", opening, StringComparison.Ordinal);
        Assert.Contains("surface.ClearPointerFeedbackAfterOpen()", opening, StringComparison.Ordinal);
        Assert.Contains("UpdateSelectionCommandBar()", opening, StringComparison.Ordinal);
        Assert.Contains("RefreshItemSelectionVisuals()", opening, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItems.Clear()", opening, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemSurfaces_ClearPendingDragSnapshotWhenPointerGestureEnds()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        string visuals = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        string selection = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));

        // Every item template that stages a drag snapshot on press must clear
        // it when the gesture ends, so the snapshot cannot keep the
        // deactivation selection clear guarded after an opened file's app
        // takes the foreground.
        int pressedCount = xaml.Split(
            "PointerPressed=\"ItemSurface_PointerPressed\"",
            StringSplitOptions.None).Length - 1;
        int releasedCount = xaml.Split(
            "PointerReleased=\"ItemSurface_PointerGestureEnded\"",
            StringSplitOptions.None).Length - 1;
        int captureLostCount = xaml.Split(
            "PointerCaptureLost=\"ItemSurface_PointerGestureEnded\"",
            StringSplitOptions.None).Length - 1;
        Assert.True(pressedCount > 0);
        Assert.Equal(pressedCount, releasedCount);
        Assert.Equal(pressedCount, captureLostCount);

        Assert.Contains(
            "private void ItemSurface_PointerGestureEnded(",
            visuals,
            StringComparison.Ordinal);
        int handlerStart = visuals.IndexOf(
            "private void ItemSurface_PointerGestureEnded(",
            StringComparison.Ordinal);
        int handlerEnd = visuals.IndexOf(
            "private void ItemSurface_DragOver(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        Assert.Contains(
            "_pendingPointerDragItems = [];",
            visuals[handlerStart..handlerEnd],
            StringComparison.Ordinal);

        int captureLostStart = selection.IndexOf(
            "private void HandleItemsPointerCaptureLost(",
            StringComparison.Ordinal);
        int captureLostEnd = selection.IndexOf(
            "private bool CanStartBoxSelection(",
            captureLostStart,
            StringComparison.Ordinal);
        Assert.True(captureLostStart >= 0 && captureLostEnd > captureLostStart);
        Assert.Contains(
            "_pendingPointerDragItems = [];",
            selection[captureLostStart..captureLostEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileItemSurface_UsesPointerFeedbackPolicyAndResetsRecycledContainers()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));

        Assert.Contains("SetVisualState(_pointerFeedback.OnOpenDispatched())", source, StringComparison.Ordinal);
        Assert.Contains("SetVisualState(_pointerFeedback.OnPointerEntered())", source, StringComparison.Ordinal);
        Assert.Contains("SetVisualState(_pointerFeedback.OnPointerPressed())", source, StringComparison.Ordinal);
        Assert.Contains("SetVisualState(_pointerFeedback.OnPointerReleased(inside))", source, StringComparison.Ordinal);

        foreach (string handler in new[]
                 {
                     "private void FileItemSurface_DataContextChanged(",
                     "private void SurfaceBorder_Loaded(",
                     "private void SurfaceBorder_Unloaded("
                 })
        {
            int start = source.IndexOf(handler, StringComparison.Ordinal);
            int end = source.IndexOf("\n    private ", start + handler.Length, StringComparison.Ordinal);
            Assert.Contains("_pointerFeedback.ResetForReuse()", source[start..end], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FileOpenWorker_PreservesStaAndBoundedDispatch()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.OpenItem.cs"));
        string runner = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/BoundedStaOperationRunner.cs"));

        Assert.Contains("maxConcurrency: 2", source, StringComparison.Ordinal);
        Assert.Contains("maxQueued: 6", source, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", runner, StringComparison.Ordinal);
        Assert.Contains("thread.SetApartmentState(ApartmentState.STA)", runner, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.OpenFile(ownerHwnd", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FileItemActivityBadge_ReusesExistingTransferVisual()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));

        Assert.Contains("ActivityBadgeVisibility", xaml, StringComparison.Ordinal);
        Assert.Contains("IsActivityActive", xaml, StringComparison.Ordinal);
        Assert.Contains("SetOpeningState", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenItemSurface", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenRequestGate_BoundsHistoryAndAllowsRetryAfterFailure()
    {
        var gate = new FileOpenRequestGate();
        const long firstTick = 1000;

        Assert.True(gate.TryBegin("C:\\Temp\\Report.txt", firstTick, 500));
        Assert.True(gate.IsActive("c:\\temp\\report.txt"));
        Assert.False(gate.TryBegin("c:\\temp\\report.txt", firstTick + 1, 500));

        gate.Complete("C:\\Temp\\Report.txt", dispatched: false);
        Assert.False(gate.IsActive("c:\\temp\\report.txt"));
        Assert.True(gate.TryBegin("c:\\temp\\report.txt", firstTick + 2, 500));

        for (int index = 0; index < FileOpenRequestGate.HistoryLimit + 8; index++)
        {
            gate.Complete($"C:\\Temp\\{index}.txt", dispatched: false);
            gate.TryBegin($"C:\\Temp\\{index}.txt", firstTick + index + 3, 1);
        }

        Assert.True(gate.HistoryCount <= FileOpenRequestGate.HistoryLimit);
    }
}
