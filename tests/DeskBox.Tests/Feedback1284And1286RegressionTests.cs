using DeskBox.Helpers;
using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class Feedback1284And1286RegressionTests
{
    [Theory]
    [InlineData("todo-custom-due", true)]
    [InlineData("todo-inline-rename", true)]
    [InlineData("file-inline-rename", true)]
    [InlineData("todo-list-background", false)]
    [InlineData("routed-pointer", false)]
    public void DesktopPinnedKeyboardActivation_RecognizesTodoCustomDueOverlayReason(
        string reason,
        bool expected)
    {
        Assert.Equal(expected, WidgetWindowBase.IsDesktopPinnedKeyboardActivationReason(reason));
    }

    [Theory]
    [InlineData(0L, 1_000L, 500, false)]
    [InlineData(1_000L, 999L, 500, false)]
    [InlineData(1_000L, 1_000L, 500, true)]
    [InlineData(1_000L, 1_250L, 500, true)]
    [InlineData(1_000L, 1_500L, 500, true)]
    [InlineData(1_000L, 1_501L, 500, false)]
    [InlineData(1_000L, 1_000L, 0, false)]
    public void ShellContextMenuProxy_PostMenuGraceWindow_HandlesBoundariesAndUninitializedState(
        long lastCompletedTick,
        long nowTick,
        int graceWindowMs,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShellContextMenuProxy.IsWithinPostMenuGraceWindow(lastCompletedTick, nowTick, graceWindowMs));
    }

    [Fact]
    public void TodoCustomDueDateOverlay_DefersMenuCloseActivatesOwnerAndRestoresDesktopPinnedOnClose()
    {
        string editingSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"));
        string clipboardSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ClipboardSelection.cs"));
        string menusSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Menus.cs"));
        string xamlCsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));
        string windowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));

        string showOverlay = SliceBetween(
            editingSource,
            "private void ShowAndFocusCustomDueDateOverlay()",
            "private Task PickBatchCustomDueDateAsync(");
        Assert.Contains("TryActivateOwnerForKeyboardInput(", showOverlay, StringComparison.Ordinal);
        Assert.Contains("\"todo-custom-due\"", showOverlay, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(", showOverlay, StringComparison.Ordinal);
        Assert.Contains("CustomDueDatePicker?.Focus(FocusState.Programmatic);", showOverlay, StringComparison.Ordinal);

        string pickSingle = SliceBetween(
            clipboardSource,
            "private Task PickCustomDueDateAsync(",
            "\n}");
        Assert.Contains("ShowAndFocusCustomDueDateOverlay();", pickSingle, StringComparison.Ordinal);

        string deferredHelper = SliceBetween(
            editingSource,
            "internal void AttachDeferredFlyoutOverlayAction(",
            "private void ShowAndFocusCustomDueDateOverlay()");
        Assert.Contains("rootFlyout.Closed +=", deferredHelper, StringComparison.Ordinal);
        Assert.Contains("_pendingOverlayHandoffDepth++;", deferredHelper, StringComparison.Ordinal);

        Assert.Contains("AttachDeferredFlyoutOverlayAction(", menusSource, StringComparison.Ordinal);
        Assert.Contains("AttachDeferredFlyoutOverlayAction(", xamlCsSource, StringComparison.Ordinal);

        string closeCustomDue = SliceBetween(
            editingSource,
            "private void CloseCustomDueDateOverlay()",
            "private void ConfigureCustomDueTimeState(");
        Assert.Contains("TryRequestOwnerDesktopPinnedRestoreIfIdle(", closeCustomDue, StringComparison.Ordinal);

        string closeTodoEdit = SliceBetween(
            editingSource,
            "private void CloseTodoEdit()",
            "private void SetTodoItemHoverState(");
        Assert.Contains("TryRequestOwnerDesktopPinnedRestoreIfIdle(", closeTodoEdit, StringComparison.Ordinal);

        string blockingFlyout = SliceBetween(
            windowSource,
            "protected override bool HasBlockingFlyoutOpen()",
            "protected override bool OwnsTransientForegroundWindow(");
        Assert.Contains("HasActiveInlineOverlay: true", blockingFlyout, StringComparison.Ordinal);
        Assert.Contains("HasActiveSystemContextMenu: true", blockingFlyout, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedFocusAndWatchdog_PreservesOpenPopupsInlineOverlaysAndActiveInputWindow()
    {
        string boundsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string deferRestore = SliceBetween(
            boundsSource,
            "private bool ShouldDeferDesktopPinnedRestoreForFocusedTextInput()",
            "private void RootElement_LostFocusForDesktopPinnedLayer(");

        int fgCheckPos = deferRestore.IndexOf("if (!ownsOrSharesTransientForeground)", StringComparison.Ordinal);
        int popupCheckPos = deferRestore.IndexOf("TransientInputFocusGuard.HasOpenPopupsInXamlRoot(xamlRoot)", StringComparison.Ordinal);
        int overlayCheckPos = deferRestore.IndexOf("HasBlockingInlineOverlayOpen()", StringComparison.Ordinal);
        Assert.True(fgCheckPos > 0, "Foreground ownership must be verified first so foreign windows never leak Z-order.");
        Assert.True(popupCheckPos > fgCheckPos);
        Assert.True(overlayCheckPos > fgCheckPos);

        string zorderSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string verifyBedding = SliceBetween(
            zorderSource,
            "private void VerifyDesktopPinnedBedding(",
            "private void RebedDesktopPinnedWidgetGroup(");
        string rebedGroup = SliceBetween(
            zorderSource,
            "private void RebedDesktopPinnedWidgetGroup(",
            "private void ResetBeddingLeakTracking()");

        Assert.Contains("activeInputWindow.IsHoldingDesktopPinnedInputOrPopupFocus()", verifyBedding, StringComparison.Ordinal);
        Assert.Contains("activeInputWindow.IsHoldingDesktopPinnedInputOrPopupFocus()", rebedGroup, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemContextMenuCustomRename_HoldsRenameHandoffGuardsPopoverImeAndDefersServerRetirement()
    {
        string selectionMenusSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));
        string showSystemMenu = SliceBetween(
            selectionMenusSource,
            "private async Task ShowSystemContextMenuAsync(",
            "private static string SystemMenuFailureText(");
        Assert.Contains("_systemContextMenuInFlightDepth++;", showSystemMenu, StringComparison.Ordinal);
        Assert.Contains("using (AcquireMenuCloseRenameHandoff())", showSystemMenu, StringComparison.Ordinal);
        Assert.Contains("await RenameItemAsync(item);", showSystemMenu, StringComparison.Ordinal);

        string stackPopoverSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));
        string dismissDeactivated = SliceBetween(
            stackPopoverSource,
            "private void SchedulePopoverDismissIfDeactivated()",
            "private void CompleteStackPopoverDrag()");
        Assert.Contains("_pendingMenuRenameHandoffDepth > 0", dismissDeactivated, StringComparison.Ordinal);
        Assert.Contains("IsWindowsTextInputExperienceWindow(fg)", dismissDeactivated, StringComparison.Ordinal);
        Assert.Contains("IsCurrentProcessPopupBridge(fg)", dismissDeactivated, StringComparison.Ordinal);

        string rustProxySource = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-thumbnail-proxy/src/main.rs"));
        string retireHelper = SliceBetween(
            rustProxySource,
            "fn should_retire_context_menu_server(",
            "fn warm_context_menu_handlers(");
        Assert.Contains("result_code != CONTEXT_MENU_EXIT_CUSTOM_COMMAND", retireHelper, StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }
}
