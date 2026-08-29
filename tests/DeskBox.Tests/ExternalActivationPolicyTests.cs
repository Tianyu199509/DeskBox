using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ExternalActivationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void DecideBareActivation_onlyRestoresWhenConfiguredFileWidgetsAreHidden(
        bool hasConfiguredFileWidgets,
        bool hasVisibleFileWidgets,
        bool expectRestore)
    {
        var context = new BareExternalActivationContext(
            hasConfiguredFileWidgets,
            hasVisibleFileWidgets);
        BareExternalActivationAction expected = expectRestore
            ? BareExternalActivationAction.RestoreAllWidgetsAndOpenSettings
            : BareExternalActivationAction.OpenSettingsOnly;

        Assert.Equal(expected, ExternalActivationPolicy.DecideBareActivation(context));
    }

    [Fact]
    public void ShouldCoalesceBareActivation_requiresAnOpenSettingsWindowAndPriorActivation()
    {
        var now = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.False(ExternalActivationPolicy.ShouldCoalesceBareActivation(
            lastActivationAtUtc: null,
            currentActivationAtUtc: now,
            isSettingsWindowOpen: true));
        Assert.False(ExternalActivationPolicy.ShouldCoalesceBareActivation(
            lastActivationAtUtc: now.AddMilliseconds(-100),
            currentActivationAtUtc: now,
            isSettingsWindowOpen: false));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(749, true)]
    [InlineData(750, true)]
    [InlineData(751, false)]
    [InlineData(-1, false)]
    public void ShouldCoalesceBareActivation_enforcesTheDuplicateWindow(
        int elapsedMilliseconds,
        bool expected)
    {
        var last = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset current = last.AddMilliseconds(elapsedMilliseconds);

        Assert.Equal(
            expected,
            ExternalActivationPolicy.ShouldCoalesceBareActivation(
                last,
                current,
                isSettingsWindowOpen: true));
    }

    [Fact]
    public void AppFallback_wiresPolicyWithoutRevealingTheFirstVisibleWidget()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        string handler = Slice(
            source,
            "private async Task HandleExternalActivationAsync()",
            "private bool DrainPendingNativeNotificationActivations()");

        Assert.Contains("ExternalActivationPolicy.DecideBareActivation", handler, StringComparison.Ordinal);
        Assert.Contains("WidgetManager.SetAllWidgetsVisibleAsync(true)", handler, StringComparison.Ordinal);
        Assert.Contains("OpenSettings();", handler, StringComparison.Ordinal);
        Assert.Contains("Coalesced duplicate bare activation", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowWidgetAsync(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefault(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationDiagnostics_distinguishDirectTrayAndSecondaryInstancePaths()
    {
        string appSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        string traySource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.Tray.cs"));

        Assert.Contains("[Activation] Secondary instance kind=", appSource, StringComparison.Ordinal);
        Assert.Contains("argumentCount=", appSource, StringComparison.Ordinal);
        Assert.Contains("GetParentProcessReport()", appSource, StringComparison.Ordinal);
        Assert.Contains("[Tray] Settings selected in primary instance", traySource, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
