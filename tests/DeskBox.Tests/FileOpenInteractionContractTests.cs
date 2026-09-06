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
        Assert.Contains("Widget.OpenItemDispatched", opening, StringComparison.Ordinal);
        Assert.Contains("OpenItem.DuplicateSuppressed", opening, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield()", opening, StringComparison.Ordinal);
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
        int clearSelection = opening.IndexOf(
            "ClearOpenedItemSelection(item, stackPopoverGeneration);",
            StringComparison.Ordinal);
        int successFeedback = opening.IndexOf(
            "T(\"Widget.OpenItemDispatched\")",
            StringComparison.Ordinal);

        Assert.True(successBranch >= 0 && clearSelection > successBranch);
        Assert.True(successFeedback > clearSelection);
        Assert.Equal(
            clearSelection,
            opening.LastIndexOf(
                "ClearOpenedItemSelection(item, stackPopoverGeneration);",
                StringComparison.Ordinal));
        Assert.Contains(
            "generation != _openStateGeneration",
            opening[..successBranch],
            StringComparison.Ordinal);

        int helperStart = opening.IndexOf(
            "private void ClearOpenedItemSelection(",
            StringComparison.Ordinal);
        int helperEnd = opening.IndexOf(
            "private bool TryBeginOpenItem(",
            helperStart,
            StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart);
        string helper = opening[helperStart..helperEnd];

        Assert.Contains("ItemsGrid.SelectedItems.Remove(item)", helper, StringComparison.Ordinal);
        Assert.Contains("ItemsList.SelectedItems.Remove(item)", helper, StringComparison.Ordinal);
        Assert.Contains("ClearOpenedItemPointerFeedback(ItemsGrid, item)", helper, StringComparison.Ordinal);
        Assert.Contains("ClearOpenedItemPointerFeedback(ItemsList, item)", helper, StringComparison.Ordinal);
        Assert.Contains("_stackPopoverItemsView?.SelectedItems.Remove(item)", helper, StringComparison.Ordinal);
        Assert.Contains("stackPopoverGeneration == _stackPopoverShowGeneration", helper, StringComparison.Ordinal);
        Assert.Contains("ClearOpenedItemPointerFeedback(popover, item)", helper, StringComparison.Ordinal);
        Assert.Contains("view.ContainerFromItem(item)", helper, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(surface.DataContext, item)", helper, StringComparison.Ordinal);
        Assert.Contains("surface.ClearPointerFeedbackAfterOpen()", helper, StringComparison.Ordinal);
        Assert.Contains("ApplyItemSurfaceVisual(border, surface.VisualState)", helper, StringComparison.Ordinal);
        Assert.Contains("UpdateSelectionCommandBar()", helper, StringComparison.Ordinal);
        Assert.Contains("RefreshItemSelectionVisuals()", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItems.Clear()", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearItemSelection()", helper, StringComparison.Ordinal);
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
