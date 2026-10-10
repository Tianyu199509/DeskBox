namespace DeskBox.Tests;

/// <summary>
/// Feedback 340 contracts: a capsule placement may only be created by a user
/// placement commit or by the capsule bar arrangement — never fabricated from
/// the expanded window corner and persisted by restore-period collapse paths.
/// After a restart, the first collapse of a never-collapsed widget derived its
/// capsule at the (possibly restored-wrong) expanded window corner and wrote
/// that geometry into settings, permanently scattering the capsule layout.
/// These contracts pin the transient-derivation shape of the window layer.
/// </summary>
public sealed class WidgetCompactPlacementFabricationContractTests
{
    [Fact]
    public void FirstCollapseDerivation_StaysTransientAndNeverPersistsPlacement()
    {
        string method = ExtractDeriveMethod();

        // The derived bounds only memoize the visual capsule geometry...
        Assert.Contains("_stableCompactBounds = fresh;", method, StringComparison.Ordinal);
        // ...while any capture is fenced behind the deliberate re-commit flag
        // used only when entering compact behavior with an existing placement.
        int captureGateIndex = method.IndexOf("if (capturePlacement)", StringComparison.Ordinal);
        int captureIndex = method.IndexOf(
            "CaptureCompactPlacement(fresh, persist: true);",
            StringComparison.Ordinal);
        Assert.True(captureGateIndex >= 0, "Missing capturePlacement gate");
        Assert.True(captureIndex > captureGateIndex, "Capture must live inside the gate");
        Assert.DoesNotContain("SettingsService.SaveDebounced", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCompactPlacement_MemoizesOnlyAndNeverCaptures()
    {
        string source = ReadCollapseSource();
        string method = ExtractSection(
            source,
            "private void EnsureCompactPlacement(RectInt32 bounds)",
            "protected void CaptureCompactPlacement(RectInt32 bounds, bool persist)");

        Assert.Contains("_stableCompactBounds = bounds;", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureCompactPlacement", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsService.SaveDebounced", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandTransition_DoesNotCreatePlacementFromTransientlyDerivedCapsule()
    {
        string source = ReadCollapseSource();
        string method = ExtractSection(
            source,
            "private void SetCollapsedState(",
            "private RectInt32 ResolvePersistedExpandedHostBounds()");

        // The zero-movement expand sync may only re-sync a placement that
        // already exists; capturing a transiently derived capsule would
        // launder restore-period geometry into a permanent placement.
        int placementGateIndex = method.IndexOf(
            "Config.CompactPlacement is not null &&",
            StringComparison.Ordinal);
        int captureIndex = method.IndexOf(
            "CaptureCompactPlacement(GetCurrentWindowBounds(), persist: false);",
            StringComparison.Ordinal);
        Assert.True(placementGateIndex >= 0, "Missing placement gate before expand sync capture");
        Assert.True(captureIndex > placementGateIndex, "Expand sync capture must follow the placement gate");
    }

    [Fact]
    public void TrayHideAndFirstCollapse_BothRouteThroughTransientDerivation()
    {
        string source = ReadCollapseSource();

        string trayHide = ExtractSection(
            source,
            "protected void PrepareCompactHostForTrayHide()",
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)");
        Assert.DoesNotContain("EnsureCompactPlacementFromExpandedBounds", trayHide, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureCompactPlacement", trayHide, StringComparison.Ordinal);

        // The collapse transition itself derives transiently (the Ensure
        // wrapper early-returns once the user owns a placement).
        string ensure = ExtractSection(
            source,
            "private void EnsureCompactPlacementFromExpandedBounds()",
            "/// <param name=\"capturePlacement\">");
        Assert.Contains("DeriveCompactPlacementFromExpandedBounds();", ensure, StringComparison.Ordinal);
        Assert.Contains("Config.CompactPlacement is not null", ensure, StringComparison.Ordinal);
    }

    private static string ExtractDeriveMethod()
    {
        string source = ReadCollapseSource();
        return ExtractSection(
            source,
            "private void DeriveCompactPlacementFromExpandedBounds(bool capturePlacement = false)",
            "protected void ResetCompactWidthOverride()");
    }

    private static string ReadCollapseSource() => File.ReadAllText(TestPaths.FromRepository(
        "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
