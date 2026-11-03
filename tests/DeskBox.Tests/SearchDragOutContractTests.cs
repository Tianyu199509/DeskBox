namespace DeskBox.Tests;

/// <summary>
/// Structural pins for the search-result drag-out pipeline. The 2026-11-06
/// fail-fast crashes (two dumps, 0x800710DD) came from a DragStarting handler
/// that awaited broker payload preparation before calling a DragUI setter:
/// once the handler yielded under a deferral the platform drag started, and
/// SetContentFromDataPackage became illegal. These tests keep the fix
/// structural — the handler cannot regress to async without failing them.
/// </summary>
public sealed class SearchDragOutContractTests
{
    private const string SearchWindowPath =
        "src/DeskBox/Views/SearchPopupWindow.xaml.cs";

    [Fact]
    public void DragStartingHandler_IsSynchronousAndSetsDragUIBeforeReturn()
    {
        string body = ExtractMethodBody(
            SearchWindowPath,
            "private void ResultsRow_DragStarting(");

        Assert.Contains(
            "args.DragUI.SetContentFromDataPackage()",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("await ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDeferral", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWindow_HasNoDeferralOrEphemeralAsyncDragHandlerLeft()
    {
        string source = ReadRepositoryFile(SearchWindowPath);

        // The ephemeral "handler = async (_, args) =>" pattern is gone; the
        // wired handler is the named synchronous one.
        Assert.DoesNotContain("DragStarting += handler", source, StringComparison.Ordinal);
        Assert.Contains(
            "DragStarting += ResultsRow_DragStarting",
            source,
            StringComparison.Ordinal);
        // No drag-out deferral may return: with a synchronous handler there
        // is nothing to defer, and a deferral re-opens the window where the
        // platform drag starts mid-handler.
        Assert.DoesNotContain("GetDeferral", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartDragAsyncBoundary_CatchesAndAttributesInsteadOfFailingFast()
    {
        string body = ExtractMethodBody(
            SearchWindowPath,
            "private async Task RunDragOutAsync(");

        // An exception escaping this async boundary fail-fasts the process
        // through DispatcherQueue (Application.UnhandledException never sees
        // it) — the catch with an HRESULT fingerprint is the only guard.
        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        Assert.Contains(
            "StartDragAsync failed",
            body,
            StringComparison.Ordinal);
        Assert.Contains("ex.HResult", body, StringComparison.Ordinal);
        Assert.Contains(
            "App.SafeFireAndForget",
            ExtractMethodBody(SearchWindowPath, "private void StartDragOut("),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SearchDragOut_SharesTheGridPayloadCoreWithoutOriginStamps()
    {
        string handlerBody = ExtractMethodBody(
            SearchWindowPath,
            "private void ResultsRow_DragStarting(");
        string packageSource = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemDragPackage.cs");

        // One payload pipeline for every file drag host: search calls the
        // path-driven core (native Shell IDataObject, never a partial
        // payload) and resolves the advertised effect set through the same
        // resolver as the grid.
        Assert.Contains(
            "FileItemDragPackage.TryPreparePaths(",
            handlerBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "FileItemDragPackage.ResolveDragOutAllowedOperations(",
            handlerBody,
            StringComparison.Ordinal);
        // ...while staying unstamped: the widget-only origin markers live in
        // the WidgetItem overload, never in the shared core.
        Assert.DoesNotContain(
            "SourceWidgetIdProperty",
            ExtractMethodBody(
                "src/DeskBox/Controls/FileItemDragPackage.cs",
                "public static bool TryPreparePaths("),
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceWidgetIdProperty",
            packageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SearchDragOut_AdvertisesTheSameEffectSetAsTheGrid()
    {
        // The grid's advertisement is preference-driven and OS-aware (Win10
        // collapses to one effect to avoid the per-drop picker). Search drags
        // carry files too and must not fork a second effect policy.
        string handlerBody = ExtractMethodBody(
            SearchWindowPath,
            "private void ResultsRow_DragStarting(");

        Assert.DoesNotContain(
            "AllowedOperations = DataPackageOperation.Copy;",
            handlerBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedAppDragStarting_IsSynchronousCopyOnlyAndSetsDragUIBeforeReturn()
    {
        string body = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_DragStarting(");

        Assert.Contains(
            "FileItemDragPackage.TryPreparePaths(",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "args.Data.RequestedOperation = DataPackageOperation.Copy;",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "args.DragUI.SetContentFromDataPackage()",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("await ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDeferral", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedAndOverlayIcons_HandOffToExternalDragWhenLeavingPopupBounds()
    {
        string recMovedBody = ExtractMethodBody(
            SearchWindowPath,
            "private void RecommendedApp_PointerMoved(");
        string overlayMovedBody = ExtractMethodBody(
            SearchWindowPath,
            "private void OverlayAppCard_PointerMoved(");
        string runBody = ExtractMethodBody(
            SearchWindowPath,
            "private async Task RunRecommendedDragOutAsync(");

        Assert.Contains("IsPointLeavingPopupDragBounds(", recMovedBody, StringComparison.Ordinal);
        Assert.Contains("TryStartRecommendedExternalDragOut(", recMovedBody, StringComparison.Ordinal);
        Assert.Contains("IsPointLeavingPopupDragBounds(", overlayMovedBody, StringComparison.Ordinal);
        Assert.Contains("TryStartRecommendedExternalDragOut(", overlayMovedBody, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", runBody, StringComparison.Ordinal);
        Assert.Contains("ex.HResult", runBody, StringComparison.Ordinal);

        Assert.True(Views.SearchPopupWindow.IsPointLeavingPopupDragBounds(
            new Windows.Foundation.Point(1.0, 120), 680, 500));
        Assert.True(Views.SearchPopupWindow.IsPointLeavingPopupDragBounds(
            new Windows.Foundation.Point(-12, 120), 680, 500));
        // Dragging inside the 12px right padding next to the rightmost column must
        // stay in internal reorder mode instead of misfiring external drag-out.
        Assert.False(Views.SearchPopupWindow.IsPointLeavingPopupDragBounds(
            new Windows.Foundation.Point(672, 180), 680, 500));
        Assert.False(Views.SearchPopupWindow.IsPointLeavingPopupDragBounds(
            new Windows.Foundation.Point(120, 180), 680, 500));
    }

    [Fact]
    public void ResultRowDragAndDisarm_ClearTransientPointerMaskStates()
    {
        string disarmBody = ExtractMethodBody(
            SearchWindowPath,
            "private void DisarmDragOutCandidate(");
        string startBody = ExtractMethodBody(
            SearchWindowPath,
            "private void StartDragOut(");
        string runBody = ExtractMethodBody(
            SearchWindowPath,
            "private async Task RunDragOutAsync(");

        Assert.Contains("ClearPressedState()", disarmBody, StringComparison.Ordinal);
        Assert.Contains("ClearTransientPointerStates()", startBody, StringComparison.Ordinal);
        Assert.Contains("ClearTransientPointerStates()", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StartMenuShortcuts_AlwaysAdvertiseCopyInDragOutAllowedOperations()
    {
        string commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrWhiteSpace(commonStartMenu))
        {
            string startMenuShortcut = Path.Combine(commonStartMenu, "Programs", "DemoApp.lnk");
            Assert.Equal(
                Controls.FileItemDragPackage.ProtectedWin11Operations,
                Controls.FileItemDragPackage.ResolveDragOutAllowedOperations(
                    Services.SettingsService.ManagedDragOutActionMove,
                    isWindows11OrLater: true,
                    sourcePaths: [startMenuShortcut]));
            Assert.Equal(
                Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
                Controls.FileItemDragPackage.ResolveDragOutAllowedOperations(
                    Services.SettingsService.ManagedDragOutActionMove,
                    isWindows11OrLater: false,
                    sourcePaths: [startMenuShortcut]));
        }

        string userDocument = Path.Combine(Path.GetTempPath(), "report.pdf");
        Assert.Equal(
            Controls.FileItemDragPackage.FullFollowWindowsOperations,
            Controls.FileItemDragPackage.ResolveDragOutAllowedOperations(
                Services.SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true,
                sourcePaths: [userDocument]));
        Assert.Equal(
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            Controls.FileItemDragPackage.ResolveDragOutAllowedOperations(
                Services.SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                sourcePaths: [userDocument]));
    }

    [Fact]
    public void ExternalDragFade_ReleasesShowStoryboardHoldEndAndAvoidsAutoHideFlash()
    {
        string startFadeBody = ExtractMethodBody(
            SearchWindowPath,
            "private void StartExternalDragFadeTimer(");
        string runResultDragBody = ExtractMethodBody(
            SearchWindowPath,
            "private async Task RunDragOutAsync(");
        string runRecDragBody = ExtractMethodBody(
            SearchWindowPath,
            "private async Task RunRecommendedDragOutAsync(");
        string hidePopupBody = ExtractMethodBody(
            SearchWindowPath,
            "public void HidePopup(");

        // PopupShowStoryboard animates RootGrid.Opacity with HoldEnd; stopping it
        // and locking local Opacity=1.0 is required before 0.20 drag-fade writes take effect.
        Assert.Contains("PopupShowStoryboard.Stop()", startFadeBody, StringComparison.Ordinal);
        Assert.Contains("RootGrid.Opacity = 1.0;", startFadeBody, StringComparison.Ordinal);

        // Auto-hide after drop must not snap Opacity back to 1.0 or run From="1" hide storyboard.
        Assert.Contains("restoreOpacity: !shouldAutoHide", runResultDragBody, StringComparison.Ordinal);
        Assert.Contains("restoreOpacity: !shouldAutoHide", runRecDragBody, StringComparison.Ordinal);
        Assert.Contains("currentOpacity >= 0.5", hidePopupBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearAllResultRowTransientStates_WalksRealizedVisualChildrenOnly()
    {
        string clearRowsBody = ExtractMethodBody(
            SearchWindowPath,
            "private void ClearAllResultRowTransientStates(");

        Assert.Contains("VisualTreeHelper.GetChildrenCount(ResultsRepeater)", clearRowsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetElement(", clearRowsBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts a member by its full declaration prefix (e.g.
    /// "private void StartDragOut(") — unambiguous against call sites, which
    /// never carry the modifier.
    /// </summary>
    private static string ExtractMethodBody(string relativePath, string declarationPrefix)
    {
        string source = ReadRepositoryFile(relativePath).Replace("\r\n", "\n");
        int signature = source.IndexOf(declarationPrefix, StringComparison.Ordinal);
        Assert.True(
            signature >= 0,
            $"{declarationPrefix} not found in {relativePath}");

        int bodyStart = source.IndexOf('{', signature);
        Assert.True(bodyStart > 0, $"{declarationPrefix} has no body");

        // Match braces so nested lambdas/records inside the body cannot end
        // the slice early.
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
