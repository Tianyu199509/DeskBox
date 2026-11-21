using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickRevealOutsideInteractionPolicyTests
{
    [Fact]
    public void PlainExternalClick_HoldsWhilePressedAndDismissesOnRelease()
    {
        var state = QuickRevealOutsideInteractionState.CreateInitial(initialMouseDown: false);

        QuickRevealOutsideSampleResult down = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 120, Y = 140 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: true,
            dragThresholdX: 4,
            dragThresholdY: 4);

        Assert.Equal(QuickRevealOutsideSampleActionKind.HoldExternalPress, down.Action);
        Assert.True(down.TaskbarOrigin);
        Assert.True(state.ShouldHoldForegroundDismiss);

        QuickRevealOutsideSampleResult up = state.Step(
            isMouseDown: false,
            cursor: new Win32Helper.POINT { X = 122, Y = 141 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: true,
            dragThresholdX: 4,
            dragThresholdY: 4);

        Assert.Equal(QuickRevealOutsideSampleActionKind.DismissOutsideClick, up.Action);
        Assert.True(up.TaskbarOrigin);
        Assert.False(state.ShouldHoldForegroundDismiss);
    }

    [Fact]
    public void ExternalDragDroppedIntoDeskBox_HoldsDuringDragAndKeepsSessionOpen()
    {
        var state = QuickRevealOutsideInteractionState.CreateInitial(initialMouseDown: false);

        _ = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 100, Y = 100 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);

        QuickRevealOutsideSampleResult dragging = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 320, Y = 260 },
            pointerOverDeskBox: true,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);

        Assert.Equal(QuickRevealOutsideSampleActionKind.HoldExternalPress, dragging.Action);
        Assert.True(state.ShouldHoldForegroundDismiss);

        QuickRevealOutsideSampleResult dropped = state.Step(
            isMouseDown: false,
            cursor: new Win32Helper.POINT { X = 320, Y = 260 },
            pointerOverDeskBox: true,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);

        Assert.Equal(QuickRevealOutsideSampleActionKind.KeepOpenAfterDropIntoDeskBox, dropped.Action);
        Assert.False(state.ShouldHoldForegroundDismiss);
    }

    [Fact]
    public void ExternalDragReleasedOutsideDeskBox_DismissesWhenGestureEnds()
    {
        var state = QuickRevealOutsideInteractionState.CreateInitial(initialMouseDown: false);

        _ = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 100, Y = 100 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);
        _ = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 180, Y = 180 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);

        QuickRevealOutsideSampleResult released = state.Step(
            isMouseDown: false,
            cursor: new Win32Helper.POINT { X = 180, Y = 180 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);

        Assert.Equal(QuickRevealOutsideSampleActionKind.DismissOutsideDragEnded, released.Action);
        Assert.False(state.ShouldHoldForegroundDismiss);
    }

    [Fact]
    public void InitialPreExistingMouseDown_IsIgnoredUntilReleased()
    {
        var state = QuickRevealOutsideInteractionState.CreateInitial(initialMouseDown: true);

        QuickRevealOutsideSampleResult heldFromSummon = state.Step(
            isMouseDown: true,
            cursor: new Win32Helper.POINT { X = 80, Y = 80 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);
        Assert.Equal(QuickRevealOutsideSampleActionKind.None, heldFromSummon.Action);
        Assert.False(state.ShouldHoldForegroundDismiss);

        QuickRevealOutsideSampleResult releasedFromSummon = state.Step(
            isMouseDown: false,
            cursor: new Win32Helper.POINT { X = 80, Y = 80 },
            pointerOverDeskBox: false,
            pointerOverTaskbar: false,
            dragThresholdX: 4,
            dragThresholdY: 4);
        Assert.Equal(QuickRevealOutsideSampleActionKind.None, releasedFromSummon.Action);
    }
}
