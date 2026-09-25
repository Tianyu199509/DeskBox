using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetPresentationAndDetachContractTests
{
    [Fact]
    public void NewContentWindow_UsesOneMutuallyExclusiveFirstPresentationPath()
    {
        string source = Read("src/DeskBox/Services/WidgetManager.cs");
        string creation = ExtractSection(
            source,
            "private async Task<ContentWidgetWindow> CreateContentWidgetFromConfigAsync(",
            "private void RegisterStandaloneUnifiedFileSessionIfNeeded(");

        int raisedBranch = creation.IndexOf(
            "if (revealAfterCreate)",
            StringComparison.Ordinal);
        int raisedShow = creation.IndexOf(
            "window.ShowPreparedRaisedFromTray();",
            StringComparison.Ordinal);
        int desktopBranch = creation.IndexOf(
            "else if (!keepPreparedForAnimation)",
            StringComparison.Ordinal);
        int desktopShow = creation.IndexOf(
            "window.ShowPreparedAtDesktopLayer();",
            StringComparison.Ordinal);

        Assert.True(
            raisedBranch >= 0 &&
            raisedBranch < raisedShow &&
            raisedShow < desktopBranch &&
            desktopBranch < desktopShow);
        Assert.Equal(
            1,
            CountOccurrences(creation, "window.ShowPreparedRaisedFromTray();"));
        Assert.Equal(
            1,
            CountOccurrences(creation, "window.ShowPreparedAtDesktopLayer();"));
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.xaml.cs",
        "public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)",
        "public void ShowPreparedRaisedFromTray(bool persistVisibility = true)",
        "PushToBottom(showWindow: false);",
        "TrayAnimation.RevealWindowForTrayShow();")]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.xaml.cs",
        "public void ShowPreparedRaisedFromTray(bool persistVisibility = true)",
        "public void PlayTrayShowAnimation()",
        "HoldTemporaryTopMost(showWindow: false);",
        "TrayAnimation.RevealWindowForTrayShow();")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)",
        "public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)",
        "PushToBottom(showWindow: false);",
        "_trayAnimation.RevealWindowForTrayShow();")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "public void ShowPreparedRaisedFromTray(bool persistVisibility = true)",
        "public void EnsureRaisedFromTrayTopMost()",
        "HoldTemporaryTopMost(showWindow: false);",
        "_trayAnimation.RevealWindowForTrayShow();")]
    public void PreparedPresentation_SettlesLayerBeforeRemovingDwmCloak(
        string relativePath,
        string startMarker,
        string endMarker,
        string layerCall,
        string revealCall)
    {
        string section = ExtractSection(
            Read(relativePath),
            startMarker,
            endMarker);

        int layerIndex = section.IndexOf(layerCall, StringComparison.Ordinal);
        int revealIndex = section.IndexOf(revealCall, StringComparison.Ordinal);

        Assert.True(layerIndex >= 0 && layerIndex < revealIndex);
    }

    [Fact]
    public void DetachPreview_ReusesOneWindowAndAvoidsSteadyStateZOrderChurn()
    {
        string grouping = Read(
            "src/DeskBox/Views/WidgetWindowBase.Grouping.cs");
        string start = ExtractSection(
            grouping,
            "private void StartGroupDetachPlacementPreview(",
            "private void StopGroupDetachPreviewPolling()");
        Assert.Contains(
            ".AcquireWidgetDetachPlacementPreview(",
            start,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new WidgetDetachPlacementPreviewWindow(",
            start,
            StringComparison.Ordinal);
        Assert.Contains("Task.Delay(16, token)", start, StringComparison.Ordinal);

        string preview = Read(
            "src/DeskBox/Views/WidgetDetachPlacementPreviewWindow.cs");
        string move = ExtractSection(
            preview,
            "private void MoveAndShowNoLock(RectInt32 bounds)",
            "private void HideNoLock()");
        string steadyMove = ExtractSection(
            move,
            "if (!boundsChanged)",
            "private void HideNoLock()",
            allowEndAtSourceEnd: true);

        Assert.Contains("Win32Helper.HWND_TOPMOST", move, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.SWP_SHOWWINDOW", move, StringComparison.Ordinal);
        Assert.Contains("IntPtr.Zero", steadyMove, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.SWP_NOZORDER", steadyMove, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Helper.HWND_TOPMOST", steadyMove, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Helper.SWP_SHOWWINDOW", steadyMove, StringComparison.Ordinal);
    }

    [Fact]
    public void DragDetach_ReassignsActiveHostAndPresentsReplacementBeforeMovingIt()
    {
        string source = Read("src/DeskBox/Services/WidgetManager.Groups.cs");
        string remove = ExtractSection(
            source,
            "public async Task<bool> RemoveWidgetFromGroupAsync(",
            "public async Task<bool> DissolveWidgetGroupContainingAsync(");
        int reuseBranch = remove.IndexOf(
            "if (!reusedSurface)",
            StringComparison.Ordinal);
        int retire = remove.IndexOf(
            "RetireLoadedWindowForGroup(",
            StringComparison.Ordinal);
        Assert.True(reuseBranch >= 0 && reuseBranch < retire);

        string reuse = ExtractSection(
            source,
            "TryCompleteDetachedActiveSurfaceReuseAsync(",
            "private async Task<IDesktopWidgetWindow?> ShowGroupActiveWindowAsync(");
        int removeGroupSurface = reuse.IndexOf(
            "_widgetSurfaces.RemoveSurface(originalGroup.SurfaceId);",
            StringComparison.Ordinal);
        int registerDetached = reuse.IndexOf(
            "CreateSurfaceDefinition(removedConfig)",
            StringComparison.Ordinal);
        int replacementShown = reuse.IndexOf(
            "replacementHost = await ShowGroupActiveWindowAsync(",
            StringComparison.Ordinal);
        int retargetDetached = reuse.IndexOf(
            "detachedHost.PrepareTrayShowAnimationForCurrentTopology()",
            StringComparison.Ordinal);
        int showDetached = reuse.IndexOf(
            "detachedHost.ShowPreparedRaisedFromTray(",
            StringComparison.Ordinal);

        Assert.True(
            removeGroupSurface >= 0 &&
            removeGroupSurface < registerDetached &&
            registerDetached < replacementShown &&
            replacementShown < retargetDetached &&
            retargetDetached < showDetached);
    }

    [Fact]
    public void NonReuseTopologyChanges_CompensateFailedReplacementFrames()
    {
        string source = Read("src/DeskBox/Services/WidgetManager.Groups.cs");
        string detach = ExtractSection(
            source,
            "public async Task<bool> RemoveWidgetFromGroupAsync(",
            "public async Task<bool> DissolveWidgetGroupContainingAsync(");
        string dissolve = ExtractSection(
            source,
            "public async Task<bool> DissolveWidgetGroupContainingAsync(",
            "public async Task<bool> ReorderWidgetGroupMemberAsync(");

        foreach (string operation in new[] { detach, dissolve })
        {
            Assert.Contains(
                "await WaitForGroupReplacementFirstFrameAsync(replacement);",
                operation,
                StringComparison.Ordinal);
            Assert.Contains(
                "await RecoverFailedGroupReplacementAsync(",
                operation,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReusedDetach_DoesNotReassignOldGroupBeforeDurableRollback()
    {
        string source = Read("src/DeskBox/Services/WidgetManager.Groups.cs");
        string reuse = ExtractSection(
            source,
            "private async Task<WidgetGroupDetachSurfaceReuseResult>",
            "private async Task ReconcileCommittedDetachedSurfaceAsync(");

        int persist = reuse.IndexOf(".TryRestorePreviousAsync(", StringComparison.Ordinal);
        int failedWrite = reuse.IndexOf("if (!rollbackSaved)", StringComparison.Ordinal);
        int keepSavedSplit = reuse.IndexOf(
            "await ReconcileCommittedDetachedSurfaceAsync(",
            StringComparison.Ordinal);
        int restoreOldClaim = reuse.IndexOf(
            "CommitSurfaceHost(originalGroup, detachedHost);",
            StringComparison.Ordinal);

        Assert.True(
            persist >= 0 &&
            persist < failedWrite &&
            failedWrite < keepSavedSplit &&
            keepSavedSplit < restoreOldClaim);
    }

    [Fact]
    public void Merge_DoubleFailureRecoversCommittedTopologyBeforeNotifyingGroups()
    {
        string source = Read("src/DeskBox/Services/WidgetManager.Groups.cs");
        string merge = ExtractSection(
            source,
            "public async Task<bool> MergeWidgetsAsync(",
            "private async Task<bool> TryRecoverCommittedMergeAfterRollbackFailureAsync(");

        int saved = merge.IndexOf("topologyPersisted = true;", StringComparison.Ordinal);
        int failAfterSave = merge.IndexOf("merge-post-save-commit", StringComparison.Ordinal);
        int rollback = merge.IndexOf("merge-rollback-save", StringComparison.Ordinal);
        int recovery = merge.IndexOf(
            "await TryRecoverCommittedMergeAfterRollbackFailureAsync(",
            StringComparison.Ordinal);
        int notify = merge.LastIndexOf("RaiseWidgetGroupsChanged();", StringComparison.Ordinal);

        Assert.True(saved >= 0 && saved < failAfterSave &&
            failAfterSave < rollback && rollback < recovery && recovery < notify);
        Assert.Contains("ReconcileCommittedMergeClaims(", source, StringComparison.Ordinal);
        Assert.Contains("QuarantineCommittedMergeAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusedSurface_RetargetsWhileCloakedBeforePreparingAnimationOffset()
    {
        string source = Read(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs");
        string preparation = ExtractSection(
            source,
            "private bool PrepareTrayShowAnimationCore(bool restoreBoundsForCurrentTopology)",
            "public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)");

        int cloak = preparation.IndexOf(
            "TrayAnimation.CloakWindowForTrayShow();",
            StringComparison.Ordinal);
        int retarget = preparation.IndexOf(
            "TryRestoreBoundsForCurrentTopology(allowHidden: true)",
            StringComparison.Ordinal);
        int prepareOffset = preparation.IndexOf(
            "TrayAnimation.PrepareVisualState(",
            StringComparison.Ordinal);

        Assert.True(
            cloak >= 0 &&
            cloak < retarget &&
            retarget < prepareOffset);
    }

    [Fact]
    public void SurfaceRegistry_SplitCanKeepTheOldHostForDetachedActiveMember()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var detachedHost = new object();
        var remainingHost = new object();
        registry.RegisterActive(
            new WidgetSurfaceDefinition(
                "group-surface",
                "group",
                ["a", "b"],
                "a"),
            detachedHost);

        Assert.True(registry.RemoveSurface("group-surface"));
        WidgetSurfaceSession<object> detached = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            detachedHost);
        WidgetSurfaceSession<object> remaining = registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            remainingHost);

        Assert.False(registry.TryGet("group-surface", out _));
        Assert.True(registry.TryGetByMember("a", out var fromDetachedAlias));
        Assert.True(registry.TryGetByMember("b", out var fromRemainingAlias));
        Assert.Same(detached, fromDetachedAlias);
        Assert.Same(remaining, fromRemainingAlias);
        Assert.Same(detachedHost, fromDetachedAlias!.Host);
        Assert.Same(remainingHost, fromRemainingAlias!.Host);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string ExtractSection(
        string source,
        string startMarker,
        string endMarker,
        bool allowEndAtSourceEnd = false)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (allowEndAtSourceEnd && end < 0)
        {
            end = source.Length;
        }

        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
