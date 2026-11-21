namespace DeskBox.Tests;

public sealed class StackPopoverFirstFrameContractTests
{
    [Fact]
    public void SwitchingStacks_WaitsForTwoCompositionFramesBeforeReveal()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");

        Assert.Contains(
            "bool switchingStacks =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueStackPopoverRevealAfterContentCommit(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositionTarget.Rendering += _stackPopoverRevealRenderingHandler",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (++_stackPopoverRevealFrameCount < 2)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteStackPopoverReveal(host, generation, stackKey)",
            source,
            StringComparison.Ordinal);

        int rebuildContent = source.IndexOf(
            "RebuildStackPopoverContentForHost(",
            StringComparison.Ordinal);
        int reconcileItems = source.IndexOf(
            "ReconcileStackPopoverItems(_stackPopoverMembers);",
            rebuildContent,
            StringComparison.Ordinal);
        int updateLayout = source.IndexOf(
            "_stackPopoverSurface.UpdateLayout();",
            reconcileItems,
            StringComparison.Ordinal);
        int prepareOffscreen = source.IndexOf(
            "host.PrepareForShow(_stackPopoverScreenBounds);",
            updateLayout,
            StringComparison.Ordinal);
        int queueCommitGate = source.IndexOf(
            "QueueStackPopoverRevealAfterContentCommit(",
            prepareOffscreen,
            StringComparison.Ordinal);

        Assert.True(rebuildContent >= 0);
        Assert.True(reconcileItems > rebuildContent);
        Assert.True(updateLayout > reconcileItems);
        Assert.True(prepareOffscreen > updateLayout);
        Assert.True(queueCommitGate > prepareOffscreen);
    }

    [Fact]
    public void Host_SeparatesOffscreenPreparationFromPositionOnlyReveal()
    {
        string host = ReadRepositoryFile(
            "src/DeskBox/Views/StackPopoverHostWindow.cs");

        string prepare = SliceBetween(
            host,
            "internal void PrepareForShow(RectInt32 bounds)",
            "internal void RevealPrepared(RectInt32 bounds)");
        string reveal = SliceBetween(
            host,
            "internal void RevealPrepared(RectInt32 bounds)",
            "internal void UpdateBounds(RectInt32 bounds)");

        Assert.Contains("SetCloaked(true);", prepare, StringComparison.Ordinal);
        Assert.Contains("_content?.UpdateLayout();", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("-32000", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateInput(", prepare, StringComparison.Ordinal);

        Assert.Contains("_appWindow.MoveAndResize(bounds);", reveal, StringComparison.Ordinal);
        Assert.Contains("SetCloaked(false);", reveal, StringComparison.Ordinal);
        Assert.Contains("ActivateInput();", reveal, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateLayout", reveal, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingOrReplacingPendingReveal_UnsubscribesRenderingCallback()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");

        Assert.Contains(
            "CompositionTarget.Rendering -= _stackPopoverRevealRenderingHandler",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "generation == _stackPopoverShowGeneration",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(_stackPopoverHostWindow, host)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverShowGeneration++;\n        CancelStackPopoverReveal();",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StackSurfaceLookup_UsesItemsControlContainersAndRetiresDetachedHashSet()
    {
        string mainSource = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string itemVisuals = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs");
        string popoverSource = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        string elevatedDragSource = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ElevatedPointerDrag.cs");

        Assert.DoesNotContain("_stackSurfaces", mainSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_stackSurfaces", itemVisuals, StringComparison.Ordinal);
        Assert.DoesNotContain("_stackSurfaces", popoverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_stackSurfaces", elevatedDragSource, StringComparison.Ordinal);

        Assert.Contains("_stackSurfaceDataContextSubscriptions", itemVisuals, StringComparison.Ordinal);
        Assert.Contains("border.DataContextChanged += StackSurface_DataContextChanged;", itemVisuals, StringComparison.Ordinal);

        string findMethod = SliceBetween(
            mainSource,
            "private Border? FindStackSurface(string stackKey)",
            "private FrameworkElement? FindStackNameElement");
        Assert.Contains("GetMainItemsView()", findMethod, StringComparison.Ordinal);
        Assert.Contains("ContainerFromItem(", findMethod, StringComparison.Ordinal);
        Assert.Contains("FindDescendantByTag(", findMethod, StringComparison.Ordinal);
        Assert.Contains("mainView.UpdateLayout();", findMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowStackPopover_DecouplesAnchorFallbackRebuildsContentAcrossStacksAndArmsRevealFallback()
    {
        string popoverSource = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");

        Assert.Contains(
            "FindStackSurface(currentStack.StackKey)",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveStackPopoverAnchorHint(currentStack.StackKey)",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RebuildStackPopoverContentForHost(",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DetachStackPopoverContentTree();",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverSurface?.InvalidateArrange();",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ArmStackPopoverRevealFallbackTimer(host, generation, stackKey);",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.GetDoubleClickTime()",
            popoverSource,
            StringComparison.Ordinal);

        string rebuildMethod = SliceBetween(
            popoverSource,
            "private void RebuildStackPopoverContentForHost(",
            "private void DetachStackPopoverContentTree()");
        int detachCallIndex = rebuildMethod.IndexOf(
            "DetachStackPopoverContentTree();",
            StringComparison.Ordinal);
        int clearItemsIndex = rebuildMethod.IndexOf(
            "_stackPopoverItems.Clear();",
            StringComparison.Ordinal);
        Assert.True(detachCallIndex >= 0 && clearItemsIndex > detachCallIndex);

        Assert.Contains(
            "selectorItem.DragStarting -= Items_DragStarting;",
            popoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "selectorItem.DropCompleted -= Items_DropCompleted;",
            popoverSource,
            StringComparison.Ordinal);

        string renderWindowSource = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.RenderWindow.cs");
        Assert.DoesNotContain(
            "GetActiveItemsView()",
            renderWindowSource,
            StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker: {end}");
        return source[startIndex..endIndex];
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
