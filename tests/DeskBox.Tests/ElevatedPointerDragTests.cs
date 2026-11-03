using DeskBox.Controls.WidgetContents;
using DeskBox.Views;
using Windows.Foundation;
using Windows.Graphics;
using Xunit;

namespace DeskBox.Tests;

public sealed class ElevatedPointerDragTests
{
    [Theory]
    [InlineData(400, 300, 1920, 1040)]
    [InlineData(1910, 300, 1920, 1040)]
    [InlineData(400, 1035, 1920, 1040)]
    [InlineData(1915, 1035, 1920, 1040)]
    public void ComputeGhostWindowBounds_NeverCoversPhysicalCursorHotSpot(
        int cursorX,
        int cursorY,
        int workWidth,
        int workHeight)
    {
        var workArea = new RectInt32(0, 0, workWidth, workHeight);
        RectInt32 bounds = ElevatedItemDragGhostWindow.ComputeGhostWindowBounds(
            cursorX,
            cursorY,
            widthPx: 228,
            heightPx: 54,
            workArea);

        bool coversCursor =
            cursorX >= bounds.X &&
            cursorX < bounds.X + bounds.Width &&
            cursorY >= bounds.Y &&
            cursorY < bounds.Y + bounds.Height;
        Assert.False(coversCursor);
        Assert.True(bounds.X >= workArea.X);
        Assert.True(bounds.Y >= workArea.Y);
        Assert.True(bounds.X + bounds.Width <= workArea.X + workArea.Width);
        Assert.True(bounds.Y + bounds.Height <= workArea.Y + workArea.Height);
    }

    [Fact]
    public void ComputeGhostWindowBounds_SecondaryNegativeMonitor_FlipsCleanlyInsideWorkArea()
    {
        var secondaryWorkArea = new RectInt32(-1920, 0, 1920, 1040);
        int cursorX = -12;
        int cursorY = 1030;

        RectInt32 bounds = ElevatedItemDragGhostWindow.ComputeGhostWindowBounds(
            cursorX,
            cursorY,
            widthPx: 240,
            heightPx: 60,
            secondaryWorkArea);

        Assert.Equal(
            cursorX - ElevatedItemDragGhostWindow.CursorOffsetPixels - 240,
            bounds.X);
        Assert.Equal(
            cursorY - ElevatedItemDragGhostWindow.CursorOffsetPixels - 60,
            bounds.Y);
        Assert.True(bounds.X >= secondaryWorkArea.X);
        Assert.True(bounds.X + bounds.Width <= secondaryWorkArea.X + secondaryWorkArea.Width);
    }

    [Theory]
    [InlineData(10, 10, 10, 10, false)]
    [InlineData(10, 10, 15.9, 10, false)]
    [InlineData(10, 10, 13, 14, false)]
    [InlineData(10, 10, 16, 10, true)]
    [InlineData(10, 10, 13.6, 14.8, true)]
    [InlineData(10, 10, 2, 10, true)]
    public void ShouldCrossElevatedDragThreshold_UsesSixDipDistance(
        double startX,
        double startY,
        double currentX,
        double currentY,
        bool expected)
    {
        bool actual = FileSurfaceContent.ShouldCrossElevatedDragThreshold(
            new Point(startX, startY),
            new Point(currentX, currentY));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeAutoScrollDelta_RespectsCenterDeadZoneAndScrollBounds()
    {
        // Center of a 400px viewport does not scroll.
        Assert.Equal(
            0,
            FileSurfaceContent.ComputeAutoScrollDelta(
                pointerOffset: 200,
                viewportLength: 400,
                currentScrollOffset: 100,
                scrollableLength: 500));

        // Top edge at scroll offset 0 cannot scroll up.
        Assert.Equal(
            0,
            FileSurfaceContent.ComputeAutoScrollDelta(
                pointerOffset: 2,
                viewportLength: 400,
                currentScrollOffset: 0,
                scrollableLength: 500));

        // Top edge with available offset scrolls up (negative).
        double upDelta = FileSurfaceContent.ComputeAutoScrollDelta(
            pointerOffset: 4,
            viewportLength: 400,
            currentScrollOffset: 120,
            scrollableLength: 500);
        Assert.InRange(upDelta, -18.0, -2.0);

        // Bottom edge at max scroll offset cannot scroll further down.
        Assert.Equal(
            0,
            FileSurfaceContent.ComputeAutoScrollDelta(
                pointerOffset: 396,
                viewportLength: 400,
                currentScrollOffset: 500,
                scrollableLength: 500));

        // Bottom edge with remaining scrollable range scrolls down (positive),
        // clamped to remaining distance when close to the end.
        double clampedDownDelta = FileSurfaceContent.ComputeAutoScrollDelta(
            pointerOffset: 398,
            viewportLength: 400,
            currentScrollOffset: 495,
            scrollableLength: 500);
        Assert.Equal(5.0, clampedDownDelta, precision: 3);
    }

    [Fact]
    public void ElevatedPointerDrag_ContractKeepsGhostClickThroughAndEngineScopedToElevatedMode()
    {
        string repoRoot = FindRepoRoot();
        string ghostSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "DeskBox",
            "Views",
            "ElevatedItemDragGhostWindow.cs"));
        string engineSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "FileSurfaceContent.ElevatedPointerDrag.cs"));

        Assert.Contains("WS_EX_TRANSPARENT", ghostSource, StringComparison.Ordinal);
        Assert.Contains("WS_EX_NOACTIVATE", ghostSource, StringComparison.Ordinal);
        Assert.Contains("WS_EX_TOOLWINDOW", ghostSource, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsCompatibilityService.SupportsNativeWindowCorners",
            ghostSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "if (!IsCurrentProcessElevatedCached()",
            engineSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[DllImport", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("[LibraryImport", engineSource, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "DeskBox.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
