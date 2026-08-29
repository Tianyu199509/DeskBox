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

        int clearItems = source.IndexOf(
            "_stackPopoverItems.Clear();",
            StringComparison.Ordinal);
        int reconcileItems = source.IndexOf(
            "ReconcileStackPopoverItems(_stackPopoverMembers);",
            clearItems,
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

        Assert.True(clearItems >= 0);
        Assert.True(reconcileItems > clearItems);
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

        Assert.Contains("-32000", prepare, StringComparison.Ordinal);
        Assert.Contains("_content?.UpdateLayout();", prepare, StringComparison.Ordinal);
        Assert.Contains("_parked = true;", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("Activate();", prepare, StringComparison.Ordinal);

        Assert.Contains("_appWindow.MoveAndResize(bounds);", reveal, StringComparison.Ordinal);
        Assert.Contains("_parked = false;", reveal, StringComparison.Ordinal);
        Assert.Contains("Activate();", reveal, StringComparison.Ordinal);
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
