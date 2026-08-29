namespace DeskBox.Tests;

public sealed class StackPopoverInteractionContractTests
{
    [Fact]
    public void Popover_ReusesFileGridWheelSizingAndReflowsItsCachedHost()
    {
        string popover = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));
        string scrollBars = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ScrollBars.cs"));

        Assert.Contains(
            "RegisterScrollBarActivityTracking(view);",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "new PointerEventHandler(ItemsView_IconSizePointerWheel)",
            scrollBars,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueStackPopoverLayoutRefresh();",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateStackPopoverIconItemContainerStyle(layout)",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverHostWindow.UpdateBounds(bounds);",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverItemsView is { } stackPopoverItemsView",
            scrollBars,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SmartCapsule_TreatsTheSeparatePopoverWindowAsBlockingSurface()
    {
        string popover = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));
        string host = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains(
            "internal bool IsStackPopoverBlockingSurfaceOpen",
            popover,
            StringComparison.Ordinal);
        Assert.Contains(
            "protected override bool HasBlockingFlyoutOpen()",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "base.HasBlockingFlyoutOpen()",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsStackPopoverBlockingSurfaceOpen: true",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasBlockingSurface: HasBlockingFlyoutOpen()",
            collapse,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScheduleSmartCollapse(SmartCollapseProbeMs);",
            collapse,
            StringComparison.Ordinal);
    }
}
