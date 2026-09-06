namespace DeskBox.Tests;

public sealed class WidgetInteractiveResizeFrameContractTests
{
    [Fact]
    public void PointerBursts_CannotPerformResizeGeometryOrSnapWorkBeforeTheFrameCallback()
    {
        string interaction = ReadSource("WidgetWindowBase.Interaction.cs");
        string baseWindow = ReadSource("WidgetWindowBase.cs");
        string pointerMoved = Section(interaction,
            "protected void ResizeBorder_PointerMovedCore(",
            "private RectInt32 ResolveInteractiveResizeBounds(");
        string queue = Section(baseWindow,
            "private void QueueInteractiveResizePointer(",
            "private void ApplyPendingInteractiveResizeBounds()");

        foreach (string inputPath in new[] { pointerMoved, queue })
        {
            Assert.DoesNotContain("UpdateGuidesAndSnap", inputPath, StringComparison.Ordinal);
            Assert.DoesNotContain("AnchorExpandedResizeBounds", inputPath, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplyWindowBounds", inputPath, StringComparison.Ordinal);
            Assert.DoesNotContain("InitialWindowSize", inputPath, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveInteractiveResizeBounds(", inputPath, StringComparison.Ordinal);
        }
        Assert.Contains("Win32Helper.GetCursorPos", pointerMoved, StringComparison.Ordinal);
        Assert.Contains("_pendingInteractiveResizePointer = pointer;", queue, StringComparison.Ordinal);
        Assert.Contains("HWnd, paceToDisplay: true", queue, StringComparison.Ordinal);

        string callback = Section(baseWindow,
            "private void ApplyPendingInteractiveResizeBounds()",
            "private void FlushPendingInteractiveResizeBounds()");
        AssertBefore(callback,
            "_pendingInteractiveResizePointer = null;",
            "ResolveInteractiveResizeBounds(pointer)");
        AssertBefore(callback, "ResolveInteractiveResizeBounds(pointer)", "ApplyWindowBounds(");
    }

    [Fact]
    public void FinalResizeFrame_BypassesPacingBeforeCaptureAndPersistence()
    {
        string baseWindow = ReadSource("WidgetWindowBase.cs");
        string interaction = ReadSource("WidgetWindowBase.Interaction.cs");
        string flush = Section(baseWindow,
            "private void FlushPendingInteractiveResizeBounds()",
            "private void CancelPendingInteractiveResizeFrame()");

        AssertBefore(flush,
            "ApplyPendingInteractiveResizeBounds();",
            "CancelPendingInteractiveResizeFrame();");
        Assert.DoesNotContain("TryEnqueue", flush, StringComparison.Ordinal);
        Assert.DoesNotContain("Register(", flush, StringComparison.Ordinal);

        string commit = Section(interaction,
            "private void CommitInteractiveResizeBounds()",
            "protected void ResizeBorder_PointerReleasedCore(");
        AssertBefore(commit, "FlushPendingInteractiveResizeBounds();", "GetActualWindowBounds();");
        AssertBefore(commit, "GetActualWindowBounds();", "IsResizing = false;");
        AssertBefore(commit, "IsResizing = false;", "PersistCompletedWidgetResize(finalBounds);");
    }

    private static string ReadSource(string fileName) =>
        File.ReadAllText(TestPaths.FromRepository($"src/DeskBox/Views/{fileName}"));

    private static string Section(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end: {endMarker}");
        return source[start..end];
    }

    private static void AssertBefore(string source, string before, string after)
    {
        int first = source.IndexOf(before, StringComparison.Ordinal);
        int second = source.IndexOf(after, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing preceding operation: {before}");
        Assert.True(second > first, $"Expected {after} after {before}");
    }
}
