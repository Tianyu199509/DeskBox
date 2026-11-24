namespace DeskBox.Tests;

/// <summary>
/// Pins the title-bar drag-end capsule-pair commit ordering:
/// <c>EndWidgetBoundsInteraction()</c> clears <c>_expandedInteractionStartBounds</c>
/// and <c>_compactInteractionStartBounds</c>, so <c>CompleteExpandedWidgetDrag</c>
/// and <c>CommitUserPlacementFromWindow</c> must run before the bounds
/// interaction ends — otherwise dragging an expanded capsule leaves its
/// compact bounds at the pre-drag position and causes pointer-exit collapse
/// to snap back to the origin.
/// </summary>
public sealed class WidgetCompactDragPlacementContractTests
{
    [Fact]
    public void NonMergedTitleDrag_CommitsExpandedCapsulePairAndHoverSuppressionBeforeEndingBoundsInteraction()
    {
        string interaction = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs");
        string finishCore = SliceMethod(
            interaction,
            "private void FinishTitleBarDragCore(",
            "private void CompleteNonMergedDragPlacementAndRestore(");
        string completeNonMerged = SliceMethod(
            interaction,
            "private void CompleteNonMergedDragPlacementAndRestore(",
            "// ── Resize logic");

        string normalBranch = SliceFrom(
            finishCore,
            "manager?.CancelWidgetGroupDrag(Config.Id);");

        Assert.DoesNotContain(
            "EndWidgetBoundsInteraction();",
            normalBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DisplayChangeWatcher?.ResumeRestore();",
            normalBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteNonMergedDragPlacementAndRestore(",
            normalBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "endBoundsInteraction: true",
            normalBranch,
            StringComparison.Ordinal);

        int expandDragIdx = IndexOfRequired(
            completeNonMerged,
            "CompleteExpandedWidgetDrag(finalBounds)");
        int commitPlacementIdx = IndexOfRequired(
            completeNonMerged,
            "CommitUserPlacementFromWindow(WidgetPlacementSource.Drag)");
        int suppressHoverIdx = IndexOfRequired(
            completeNonMerged,
            "_suppressHoverExpansionAfterDragUntilPointerExit = true;");
        int finallyIdx = IndexOfRequired(
            completeNonMerged,
            "finally");
        int guardIdx = IndexOfRequired(
            completeNonMerged,
            "if (endBoundsInteraction)");
        int endInteractionIdx = IndexOfRequired(
            completeNonMerged,
            "EndWidgetBoundsInteraction();");
        int resumeRestoreIdx = IndexOfRequired(
            completeNonMerged,
            "DisplayChangeWatcher?.ResumeRestore();");

        Assert.True(
            expandDragIdx < commitPlacementIdx,
            "CompleteExpandedWidgetDrag must precede CommitUserPlacementFromWindow");
        Assert.True(
            commitPlacementIdx < suppressHoverIdx,
            "CommitUserPlacementFromWindow must precede hover-expansion suppression");
        Assert.True(
            suppressHoverIdx < finallyIdx &&
            finallyIdx < guardIdx &&
            guardIdx < endInteractionIdx &&
            guardIdx < resumeRestoreIdx,
            "Placement commit and hover suppression must precede guarded EndWidgetBoundsInteraction/ResumeRestore in finally");
    }

    [Fact]
    public void MergeReadyTitleDrag_SnapshotsStartPairForRejectedMergeFallback()
    {
        string interaction = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs");
        string finishCore = SliceMethod(
            interaction,
            "private void FinishTitleBarDragCore(",
            "private void CompleteNonMergedDragPlacementAndRestore(");
        string mergeBranch = SliceMethod(
            finishCore,
            "manager?.IsWidgetGroupDragMergeReady(Config.Id) == true",
            "manager?.CancelWidgetGroupDrag(Config.Id);");

        int snapshotExpandedIdx = IndexOfRequired(
            mergeBranch,
            "mergeFallbackExpandedStart = _expandedInteractionStartBounds;");
        int snapshotCompactIdx = IndexOfRequired(
            mergeBranch,
            "mergeFallbackCompactStart = _compactInteractionStartBounds;");
        int endInteractionIdx = IndexOfRequired(
            mergeBranch,
            "EndWidgetBoundsInteraction();");

        Assert.True(
            snapshotExpandedIdx < endInteractionIdx &&
            snapshotCompactIdx < endInteractionIdx,
            "Merge branch must snapshot the start pair before EndWidgetBoundsInteraction clears it");

        string fallback = SliceFrom(mergeBranch, "onNotMergedFallback:");
        int restoreExpandedIdx = IndexOfRequired(
            fallback,
            "_expandedInteractionStartBounds = mergeFallbackExpandedStart;");
        int restoreCompactIdx = IndexOfRequired(
            fallback,
            "_compactInteractionStartBounds = mergeFallbackCompactStart;");
        int callFallbackIdx = IndexOfRequired(
            fallback,
            "CompleteNonMergedDragPlacementAndRestore(");
        int endBoundsFalseIdx = IndexOfRequired(
            fallback,
            "endBoundsInteraction: false");
        int finallyIdx = IndexOfRequired(fallback, "finally");
        int clearExpandedIdx = IndexOfRequired(
            fallback,
            "_expandedInteractionStartBounds = null;");
        int clearCompactIdx = IndexOfRequired(
            fallback,
            "_compactInteractionStartBounds = null;");

        Assert.True(
            restoreExpandedIdx < callFallbackIdx &&
            restoreCompactIdx < callFallbackIdx &&
            callFallbackIdx < endBoundsFalseIdx,
            "Rejected-merge fallback must restore the start pair before invoking CompleteNonMergedDragPlacementAndRestore(..., endBoundsInteraction: false)");
        Assert.True(
            endBoundsFalseIdx < finallyIdx &&
            finallyIdx < clearExpandedIdx &&
            finallyIdx < clearCompactIdx,
            "Rejected-merge fallback must clear the restored start pair in finally");
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = IndexOfRequired(source, startMarker);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string SliceFrom(string source, string startMarker)
    {
        int start = IndexOfRequired(source, startMarker);
        return source[start..];
    }

    private static int IndexOfRequired(string source, string marker)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing source marker: {marker}");
        return index;
    }
}
