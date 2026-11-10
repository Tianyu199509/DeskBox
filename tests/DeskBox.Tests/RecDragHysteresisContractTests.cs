namespace DeskBox.Tests;

/// <summary>
/// Structural pins for the recommendation-drag hysteresis rework. The
/// 2026-11-06 evening report traced flicker, unreachable folder merges and
/// broken batch reorders to one feedback loop: decisions were hit-tested
/// against live transforms that included the reflow translations. These
/// tests keep the three structural guarantees that break that loop.
/// </summary>
public sealed class RecDragHysteresisContractTests
{
    private const string SearchWindowPath =
        "src/DeskBox/Views/SearchPopupWindow.xaml.cs";

    [Fact]
    public void DropEvaluation_HitTestsBaseRectsNeverLiveTransforms()
    {
        string body = ExtractMethodBody(
            SearchWindowPath,
            "private RecommendedDropDecision EvaluateRecommendedDropTarget(");

        // Base rects make decisions immune to the reflow translations the
        // preview itself applies; a live TransformToVisual here would feed
        // the decision→reflow→decision oscillation again.
        Assert.Contains("GetTileBaseRect", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TransformToVisual", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DropDecisions_GoThroughTheHysteresisAnchor()
    {
        string movedBody = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_PointerMoved(");

        Assert.Contains(
            "ResolveRecommendedDropDecision",
            movedBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotRecommendedTileBaseRects",
            movedBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DropRelease_CommitsTheAnchoredDecisionNotAFreshEvaluation()
    {
        string releaseBody = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_PointerReleased(");

        Assert.Contains("_recLastDropDecision", releaseBody, StringComparison.Ordinal);
        // A fresh evaluation at release can disagree with the preview the
        // user just acted on — the commit must use the anchored decision.
        Assert.DoesNotContain(
            "EvaluateRecommendedDropTarget(",
            releaseBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayDragOut_ClosesTheFolderOverlayOnSuccessfulExtraction()
    {
        string source = ReadRepositoryFile(SearchWindowPath);

        // The overlay hides the main grid; keeping it open after an
        // extraction hides where the app landed. Close on any successful
        // drag-out, not only when the folder dissolves.
        Assert.Contains(
            "removed || !folderStillExists",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[RecDrag] start")]
    [InlineData("[RecDrag] decision")]
    [InlineData("[RecDrag] release")]
    [InlineData("[RecDrag] drag-out")]
    public void RecDrag_AttributionLogFingerprintsExist(string fingerprint)
    {
        Assert.Contains(
            fingerprint,
            ReadRepositoryFile(SearchWindowPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DropRelease_SnapshotsDecisionAndDraggedKeysBeforeReleasingPointerCapture()
    {
        string releaseBody = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_PointerReleased(");

        // ReleasePointerCapture synchronously raises RecommendedApp_PointerCaptureLost ->
        // CancelRecommendedDrag(), which wipes _recLastDropDecision, _recDraggedAppKeys,
        // and _recDraggedEntryKeys. Every commit input must be snapshotted first.
        int releaseCaptureIdx = releaseBody.IndexOf("ReleasePointerCapture(", StringComparison.Ordinal);
        int decisionIdx = releaseBody.IndexOf("_recLastDropDecision", StringComparison.Ordinal);
        int draggedAppsIdx = releaseBody.IndexOf("_recDraggedAppKeys", StringComparison.Ordinal);
        int draggedEntriesIdx = releaseBody.IndexOf("_recDraggedEntryKeys", StringComparison.Ordinal);

        Assert.True(releaseCaptureIdx > 0);
        Assert.True(decisionIdx > 0 && decisionIdx < releaseCaptureIdx);
        Assert.True(draggedAppsIdx > 0 && draggedAppsIdx < releaseCaptureIdx);
        Assert.True(draggedEntriesIdx > 0 && draggedEntriesIdx < releaseCaptureIdx);
    }

    [Fact]
    public void OverlayRelease_SnapshotsFolderIdAndReorderBeforeReleasingPointerCapture()
    {
        string releaseBody = ExtractMethodBody(
            SearchWindowPath,
            "private void OverlayAppCard_PointerReleased(");

        int releaseCaptureIdx = releaseBody.IndexOf("ReleasePointerCapture(", StringComparison.Ordinal);
        int folderIdIdx = releaseBody.IndexOf("_openFolderId", StringComparison.Ordinal);
        int reorderEvalIdx = releaseBody.IndexOf("EvaluateOverlayFolderReorderIndex(", StringComparison.Ordinal);

        Assert.True(releaseCaptureIdx > 0);
        Assert.True(folderIdIdx > 0 && folderIdIdx < releaseCaptureIdx);
        Assert.True(reorderEvalIdx > 0 && reorderEvalIdx < releaseCaptureIdx);
    }

    [Fact]
    public void SubZoneAnchors_AllowTransitionBetweenEdgeReorderAndCenterMerge()
    {
        var tileRect = new Windows.Foundation.Rect(100, 10, 104, 92);
        double deadband = Views.SearchPopupWindow.DropAnchorDeadband;

        // 1. Entering a folder tile at the left insertion strip must not lock out
        //    transitioning into the folder's center merge zone.
        Windows.Foundation.Rect folderLeftEdgeAnchor = Views.SearchPopupWindow.InflateDropAnchorRect(
            Views.SearchPopupWindow.ComputeTileReorderAnchorRect(
                tileRect,
                relX: 0.05,
                relY: 0.50,
                beforeTile: true,
                canMergeOntoTarget: true,
                targetIsFolder: true),
            deadband);
        Assert.True(folderLeftEdgeAnchor.Contains(new Windows.Foundation.Point(105, 56)));
        Assert.False(folderLeftEdgeAnchor.Contains(new Windows.Foundation.Point(152, 56)));

        // 2. Entering an app tile at the left insertion strip (single drag) must not
        //    lock out transitioning into the app's center folder-creation zone.
        Windows.Foundation.Rect appLeftEdgeAnchor = Views.SearchPopupWindow.InflateDropAnchorRect(
            Views.SearchPopupWindow.ComputeTileReorderAnchorRect(
                tileRect,
                relX: 0.10,
                relY: 0.50,
                beforeTile: true,
                canMergeOntoTarget: true,
                targetIsFolder: false),
            deadband);
        Assert.False(appLeftEdgeAnchor.Contains(new Windows.Foundation.Point(152, 56)));

        // 3. Entering a folder's merge zone must still release cleanly when the
        //    pointer moves to the outer insertion edges.
        Windows.Foundation.Rect folderMergeAnchor = Views.SearchPopupWindow.InflateDropAnchorRect(
            Views.SearchPopupWindow.ComputeTileMergeAnchorRect(tileRect, targetIsFolder: true),
            deadband);
        Assert.True(folderMergeAnchor.Contains(new Windows.Foundation.Point(152, 56)));
        Assert.False(folderMergeAnchor.Contains(new Windows.Foundation.Point(103, 56)));
        Assert.False(folderMergeAnchor.Contains(new Windows.Foundation.Point(201, 56)));

        // 4. Batch drag over an app tile (merge disabled) splits left/right halves so
        //    crossing the midline updates beforeTile -> afterTile.
        Windows.Foundation.Rect batchLeftHalfAnchor = Views.SearchPopupWindow.InflateDropAnchorRect(
            Views.SearchPopupWindow.ComputeTileReorderAnchorRect(
                tileRect,
                relX: 0.20,
                relY: 0.50,
                beforeTile: true,
                canMergeOntoTarget: false,
                targetIsFolder: false),
            deadband);
        Assert.False(batchLeftHalfAnchor.Contains(new Windows.Foundation.Point(180, 56)));

        // 5. Trailing append anchor never swallows the last tile on the bottom row.
        Windows.Foundation.Rect trailingAnchor = Views.SearchPopupWindow.InflateDropAnchorRect(
            Views.SearchPopupWindow.ComputeTrailingAppendAnchorRect(
                new Windows.Foundation.Point(240, 56),
                tileRect,
                surfaceWidth: 640,
                surfaceHeight: 320),
            deadband);
        Assert.False(trailingAnchor.Contains(new Windows.Foundation.Point(180, 56)));
    }

    [Fact]
    public void RecommendationTiles_UseHoverOnlyHighlightAndTransparentDragGhosts()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml");
        string selectEntryBody = ExtractMethodBody(
            SearchWindowPath,
            "private void SelectRecommendedEntry(");
        string editVisualsBody = ExtractMethodBody(
            SearchWindowPath,
            "private void ApplyRecommendedCardEditVisuals(");

        // Idle and selected recommendation tiles must not display a persistent mask.
        Assert.DoesNotContain("ControlFillColorSecondaryBrush", selectEntryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlFillColorSecondaryBrush", editVisualsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("? Visibility.Visible", ExtractPlateSection(editVisualsBody), StringComparison.Ordinal);

        // Drag ghosts carry only the clean icon + label (+ multi-drag count badge), no card mask.
        string recGhost = ExtractElementTag(xaml, "x:Name=\"RecDragGhostBorder\"");
        string folderGhost = ExtractElementTag(xaml, "x:Name=\"FolderDragGhostBorder\"");
        Assert.Contains("Background=\"Transparent\"", recGhost, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0\"", recGhost, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", folderGhost, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0\"", folderGhost, StringComparison.Ordinal);
    }

    [Fact]
    public void EditMode_SupportsFolderDoubleClickOpenAndSuppressesAccidentalFolderAppLaunch()
    {
        string recReleaseBody = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_PointerReleased(");
        string overlayReleaseBody = ExtractMethodBody(
            SearchWindowPath,
            "private void OverlayAppCard_PointerReleased(");

        Assert.Contains("EditFolderDoubleClickThresholdMs", recReleaseBody, StringComparison.Ordinal);
        Assert.Contains("OpenFolderOverlay(pressedEntry)", recReleaseBody, StringComparison.Ordinal);
        Assert.Contains("if (!_isRecommendationEditMode)", overlayReleaseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ReflowPreview_EnablesCompositionTranslationAndMemoizesUnchangedDecisions()
    {
        string preparedBody = ExtractMethodBody(
            SearchWindowPath,
            "private void OnRecommendedAppsElementPrepared(");
        string clearingBody = ExtractMethodBody(
            SearchWindowPath,
            "private void OnRecommendedAppsElementClearing(");
        string reflowAnimBody = ExtractMethodBody(
            SearchWindowPath,
            "private static void AnimateReflowTranslation(");
        string reflowPreviewBody = ExtractMethodBody(
            SearchWindowPath,
            "private bool ApplyRecommendedReflowPreview(");

        Assert.Contains("ElementCompositionPreview.SetIsTranslationEnabled(", preparedBody, StringComparison.Ordinal);
        Assert.Contains("ElementCompositionPreview.SetIsTranslationEnabled(", clearingBody, StringComparison.Ordinal);
        Assert.Contains("ElementCompositionPreview.SetIsTranslationEnabled(", reflowAnimBody, StringComparison.Ordinal);
        Assert.Contains("_recHasCachedReflowState", reflowPreviewBody, StringComparison.Ordinal);
    }

    private static string ExtractPlateSection(string editVisualsBody)
    {
        int checkIdx = editVisualsBody.IndexOf("CardSelectionCheckBox", StringComparison.Ordinal);
        return checkIdx > 0 ? editVisualsBody[..checkIdx] : editVisualsBody;
    }

    private static string ExtractElementTag(string xaml, string marker)
    {
        int idx = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{marker} not found");
        int start = xaml.LastIndexOf('<', idx);
        int end = xaml.IndexOf('>', idx);
        Assert.True(start >= 0 && end > start);
        return xaml[start..(end + 1)];
    }

    private static string ExtractMethodBody(string relativePath, string declarationPrefix)
    {
        string source = ReadRepositoryFile(relativePath).Replace("\r\n", "\n");
        int signature = source.IndexOf(declarationPrefix, StringComparison.Ordinal);
        Assert.True(
            signature >= 0,
            $"{declarationPrefix} not found in {relativePath}");

        int bodyStart = source.IndexOf('{', signature);
        Assert.True(bodyStart > 0, $"{declarationPrefix} has no body");

        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(signature, i - signature + 1);
                    }
                    break;
            }
        }

        throw new InvalidOperationException(
            $"Unbalanced braces extracting {declarationPrefix} from {relativePath}.");
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
