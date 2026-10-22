namespace DeskBox.Tests;

/// <summary>
/// The stack popover is permanently TOPMOST while WinUI flyouts render in
/// normal-band "PopupWindowSiteBridge" windows, so a popover-hosted menu
/// needs the bridge band promotion to stay clickable, and the popover needs
/// its band re-asserted on reveal — owner-following z transactions on the
/// widget window can strip the topmost bit while parked (live-captured
/// 2026-10-21), which made the failure look intermittent between old and
/// new stacks. The immediate-hidden working-set trim must stay away from
/// casual reveal rhythms for the same measured reason.
/// </summary>
public sealed class StackPopoverMenuBandContractTests
{
    [Fact]
    public void PopoverFlyouts_PromoteBridgeWindowsWhileOpen()
    {
        string source = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        // The item flyout path wires promotion only for the popover sender.
        Assert.Contains(
            "flyout.Opened += (_, _) => BeginPopoverFlyoutBandPromotion();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(sender, _stackPopoverItemsView)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Services.FlyoutBridgeBandPromoter.TryBegin()",
            source,
            StringComparison.Ordinal);

        // The bridge enumerator the promoter drives lives in Platform.
        string helper = Read("src/DeskBox/Platform/Win32Helper.cs");
        Assert.Contains(
            "GetVisiblePopupBridgeWindows",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Microsoft.UI.Content.PopupWindowSiteBridge\"",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StackPopover_ReassertsTopMostBandOnReveal()
    {
        string source = Read("src/DeskBox/Views/StackPopoverHostWindow.cs");
        int revealStart = source.IndexOf(
            "internal void RevealPrepared",
            StringComparison.Ordinal);
        int revealEnd = source.IndexOf(
            "internal void UpdateBounds",
            StringComparison.Ordinal);
        Assert.True(revealStart >= 0 && revealEnd > revealStart, "RevealPrepared not found.");

        string reveal = source[revealStart..revealEnd];
        Assert.Contains("Win32Helper.HWND_TOPMOST", reveal, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmediateHiddenTrim_KeepsAwayFromCasualRevealRhythms()
    {
        string source = Read("src/DeskBox/App.ImmediateHiddenWorkingSetTrim.cs");
        Assert.Contains(
            "ImmediateHiddenWorkingSetTrimGracePeriodMs = 30000",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImmediateHiddenWorkingSetFloorBytes",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PopoverFlyouts_PlaceAgainstTheRaisingViewAndDismissAfterMenuRounds()
    {
        string source = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        // From the stack popover the items live in the popover's own visual
        // tree; positioning against this surface's Root (in the widget
        // window's tree) lands the menu far from the clicked icon. Since the
        // show block moved into ShowItemContextMenuForSelection (shared with
        // the right-button marquee release), the pin anchors on the
        // position-provider show: the caller's view stays the placement
        // target and the point stays in that view's coordinates.
        Assert.Contains(
            "FrameworkElement placementTarget =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sender as FrameworkElement ?? Root;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "flyout.ShowAt(placementTarget, getPosition(placementTarget));",
            source,
            StringComparison.Ordinal);

        // The outside-click light dismiss rides a fresh Activated→
        // Deactivated transition that the menu-round flag deliberately
        // blocks; the transition fires before Closed resets the flag and no
        // new one follows. Every menu-round completion re-evaluates the
        // foreground and runs the missed dismiss when the host lost it.
        string popover = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        Assert.Contains(
            "SchedulePopoverDismissIfDeactivated();",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.GetForegroundWindow() != host.WindowHandle",
            popover,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        // Normalize EOL: the repository blob is LF but a fresh Windows
        // checkout materializes CRLF, and multi-line assertions pin exact
        // "\n"-joined snippets.
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
