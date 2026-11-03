namespace DeskBox.Tests;

public sealed class WidgetGroupCloseConfirmationContractTests
{
    [Fact]
    public void CloseConfirmation_TransfersInteractionAcrossBothParentMenuPaths()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Equal(
            8,
            CountOccurrences(
                source,
                "AcquireCloseWidgetFlyoutHandoff();"));
        // The fourth acquire is the foreground color picker chain, the fifth
        // is the title-icon customizer chain, the sixth is the background
        // customizer chain, and the seventh/eighth are the Todo export
        // menu-to-flyout and flyout-to-picker chains; all hold the same
        // handoff across transitions.
        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "closeWidgetFlyoutHandoff);"));
        // The recycle-bin confirmation is a third flyout hop after the
        // close-mode selection; it must hold the same interaction handoff
        // or a Smart capsule collapses before it becomes visible.
        Assert.Contains(
            "recycleConfirmationHandoff ??= AcquireCloseWidgetFlyoutHandoff();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => ShowDeleteManagedFolderConfirmationAsync());",
            source,
            StringComparison.Ordinal);
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
        Assert.Contains("ShowCloseWidgetFlyout(ContentWidgetShell)", queue, StringComparison.Ordinal);
        Assert.Contains("interactionHandoff?.Dispose();", queue, StringComparison.Ordinal);
        // The queued show is awaited so the confirmation acquires its own
        // interaction before the handoff is released.
        Assert.True(
            queue.IndexOf("await showAsync();", StringComparison.Ordinal) <
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
