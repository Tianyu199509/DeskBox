namespace DeskBox.Tests;

/// <summary>
/// Contracts for the Recycle Bin namespace tile as a drop target: dropping
/// files on the bin's .lnk inside a grid recycles them (desktop/Explorer
/// external drags through the native OLE channel, cross-widget DeskBox
/// drags through the routed XAML channel, same-widget drags through the
/// native fallback that exists precisely because WinUI never delivers a
/// routed Drop to item surfaces of the source ListView), and both the menu
/// verb and the drop empty/refill the bin's state-aware icon.
/// </summary>
public sealed class RecycleBinDropContractTests
{
    [Fact]
    public void Policy_RoutesBinTileToRecycleBinDelete()
    {
        string policy = ReadRepositoryFile(
            "src/DeskBox/Services/ItemDropBehaviorPolicy.cs");

        Assert.Contains(
            "RecycleBinDelete",
            policy,
            StringComparison.Ordinal);
        // The bin check runs before the launch evaluation: the bin's .lnk
        // has no filesystem target, so no other behavior can apply.
        Assert.Contains(
            "NamespaceShortcutHelper.ResolveKind(item)",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "NamespaceShortcutKind.RecycleBin",
            policy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeChannel_ArmsAndRoutesBinTiles()
    {
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");

        // DragOver arm and Drop-time normalization must both accept the bin
        // behavior, or the tile highlights but the drop imports instead of
        // deleting (the exact disagreement the policy exists to prevent).
        int armRegion = window.IndexOf(
            "private void ObserveNativeFileDragPointer(",
            StringComparison.Ordinal);
        Assert.True(armRegion >= 0, "native drag pointer observer exists");
        Assert.Contains(
            "ComputeNativeDropTargetsOnUiThread(",
            window[armRegion..Math.Min(window.Length, armRegion + 3000)],
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemDropBehavior.RecycleBinDelete",
            window[armRegion..Math.Min(window.Length, armRegion + 5500)],
            StringComparison.Ordinal);
        int normalizeRegion = window.IndexOf(
            "private static WidgetItem? NormalizeNativeFileDropItemTarget(",
            StringComparison.Ordinal);
        Assert.True(normalizeRegion >= 0, "drop normalization exists");
        Assert.Contains(
            "ItemDropBehavior.RecycleBinDelete",
            window[normalizeRegion..Math.Min(
                window.Length,
                normalizeRegion + 800)],
            StringComparison.Ordinal);

        // The shell drop description must not advertise the generic
        // per-widget move/copy caption over a bin tile.
        Assert.Contains(
            "\"Widget.DropOnRecycleBinHint\"",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Surface_BinDropRecyclesInsteadOfImporting()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        int intercept = surface.IndexOf(
            "await DeleteDroppedFilesForBinAsync(paths, binTile.Path);",
            StringComparison.Ordinal);
        Assert.True(
            intercept >= 0,
            "the native import entry intercepts the bin target");
        int methodStart = surface.IndexOf(
            "internal async Task<bool> ImportNativeDroppedFilesAsync(",
            StringComparison.Ordinal);
        string interceptionRegion = surface[methodStart..intercept];
        // Drag visuals clear before the bin delete starts: the OLE Drop
        // never sends a DragLeave afterwards, so a hover state left standing
        // would strand (the 1.5.9 popover red-tint lesson).
        Assert.Contains(
            "ClearDragSessionVisualState();",
            interceptionRegion,
            StringComparison.Ordinal);

        Assert.Contains(
            "await ViewModel.DeleteDroppedPathsForBinAsync(",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoutedChannel_HandlesBinTilesLikeFolderTargets()
    {
        string visuals = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs");

        Assert.Contains(
            "private static bool TryGetRecycleBinDropTarget(",
            visuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void HandleRecycleBinDragOver(",
            visuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "private async Task HandleRecycleBinDropAsync(",
            visuals,
            StringComparison.Ordinal);

        int dropStart = visuals.IndexOf(
            "private async Task HandleRecycleBinDropAsync(",
            StringComparison.Ordinal);
        int dropEnd = visuals.IndexOf(
            "await DeleteDroppedFilesForBinAsync(",
            dropStart,
            StringComparison.Ordinal);
        Assert.True(dropEnd > dropStart, "routed bin drop reaches the delete");
        string dropRegion = visuals[dropStart..dropEnd];
        // The routed drop must never complete with a transfer result: the
        // destination deletes, and source-side cleanup is watcher-driven.
        Assert.Contains(
            "e.AcceptedOperation = DataPackageOperation.None;",
            dropRegion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_PathDelete_MirrorsTheItemDeletePipeline()
    {
        string operations = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs");

        int methodStart = operations.IndexOf(
            "DeleteDroppedPathsForBinAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "path-based bin delete exists");
        string methodRegion = operations[methodStart..];

        // Shell delete carries the confirmation (recycle: true), and the
        // model entries of deleted paths must leave Items in the same pass.
        Assert.Contains(
            "_fileService.DeleteEntriesWithShellAsync(",
            methodRegion,
            StringComparison.Ordinal);
        Assert.Contains(
            "recycle: true,",
            methodRegion,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoveStackMemberOverridePaths(",
            methodRegion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BinStateTransitions_RefreshTheStateAwareIcon()
    {
        // The bin renders its empty or full graphic for the same PIDL, and
        // the .lnk never changes on a state transition — neither the icon
        // cache key nor the freshness ledger can invalidate on its own, so
        // both transition points (menu Empty, bin drop) must refresh.
        string hydration = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs");
        Assert.Contains(
            "internal async Task RefreshItemIconsAsync(",
            hydration,
            StringComparison.Ordinal);
        int refreshStart = hydration.IndexOf(
            "internal async Task RefreshItemIconsAsync(",
            StringComparison.Ordinal);
        int refreshEnd = hydration.IndexOf(
            "StartItemHydration();",
            refreshStart,
            StringComparison.Ordinal);
        Assert.True(
            refreshEnd > refreshStart,
            "the targeted refresh restarts hydration");
        string refreshRegion = hydration[refreshStart..refreshEnd];
        Assert.Contains(
            "await _fileService.ClearIconCacheAsync(",
            refreshRegion,
            StringComparison.Ordinal);

        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        Assert.Contains(
            "await RefreshRecycleBinItemIconsAsync();",
            surface,
            StringComparison.Ordinal);

        string menus = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        Assert.Contains(
            "await RefreshRecycleBinItemIconsAsync();",
            menus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NamespaceKind_PrewarmsDuringShortcutTargetHydration()
    {
        // The drop-target policy resolves the kind inside drag callbacks;
        // a cold first resolve there (shell-link CoClass load plus the
        // vtable IDList read) can consume the entire hover-over-tile window
        // and leave the bin tile unarmed for that whole gesture — the
        // first-drag swallow seen on 2026-10-21. The shortcut-target
        // hydration pass runs on the UI thread's STA apartment with the
        // metadata read already cache-hot, so the classification prewarms
        // there instead.
        string hydration = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs");
        int setTarget = hydration.IndexOf(
            "private void SetShortcutTarget(",
            StringComparison.Ordinal);
        Assert.True(setTarget >= 0, "shortcut target hydration exists");
        string region = hydration[setTarget..Math.Min(
            hydration.Length,
            setTarget + 2500)];
        Assert.Contains(
            "NamespaceShortcutHelper.ResolveKind(item);",
            region,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
