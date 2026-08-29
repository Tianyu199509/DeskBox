namespace DeskBox.Tests;

public sealed class WidgetGroupCloseConfirmationContractTests
{
    [Fact]
    public void CloseConfirmation_TransfersInteractionAcrossBothParentMenuPaths()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "AcquireCloseWidgetFlyoutHandoff();"));
        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "closeWidgetFlyoutHandoff);"));
    }

    [Fact]
    public void CloseConfirmation_HandoffProtectsCompactHostAndGroupedSession()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string acquire = ExtractSection(
            source,
            "private IDisposable AcquireCloseWidgetFlyoutHandoff()",
            "private void QueueCloseWidgetFlyout(");
        string queue = ExtractSection(
            source,
            "private void QueueCloseWidgetFlyout(IDisposable? interactionHandoff)",
            "private MenuFlyout CreateFeatureWidgetCloseFlyout(");

        Assert.Contains("BeginCompactInteraction();", acquire, StringComparison.Ordinal);
        Assert.Contains("BeginWidgetInteraction(", acquire, StringComparison.Ordinal);
        Assert.Contains("ShowCloseWidgetFlyout(ContentWidgetShell);", queue, StringComparison.Ordinal);
        Assert.Contains("interactionHandoff?.Dispose();", queue, StringComparison.Ordinal);
        Assert.True(
            queue.IndexOf("ShowCloseWidgetFlyout(ContentWidgetShell);", StringComparison.Ordinal) <
            queue.IndexOf("interactionHandoff?.Dispose();", StringComparison.Ordinal));
        Assert.Contains("owner.EndCompactInteraction();", source, StringComparison.Ordinal);
        Assert.Contains("widgetManager?.EndWidgetInteraction(", source, StringComparison.Ordinal);
    }

    private static string ExtractSection(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }
}
