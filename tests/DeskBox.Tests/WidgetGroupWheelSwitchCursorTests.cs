using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupWheelSwitchCursorTests
{
    private const string MemberA = "widget-a";
    private const string MemberB = "widget-b";
    private const string MemberC = "widget-c";

    [Fact]
    public void StowInterrupt_DropsPendingCursor_SoRevealReconcileCannotReplayIt()
    {
        // Feedback 387: hover the group title, wheel one step (optimistic
        // cursor B while A is active), stow before the switch settles, then
        // reveal. The cursor must be gone; the next wheel step must base on
        // the committed active member, not on the pre-stow target.
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        cursor.NotifyInterrupted();

        Assert.Null(cursor.PendingTargetId);
        Assert.False(cursor.Reconcile(MemberA));
        Assert.Null(cursor.PendingTargetId);
    }

    [Fact]
    public void CompletionReport_Failure_ReleasesCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        Assert.True(cursor.ShouldReleaseOnCompletion(MemberB, succeeded: false, MemberA));
        cursor.Clear();

        Assert.Null(cursor.PendingTargetId);
    }

    [Fact]
    public void CompletionReport_CoalescedDuplicateWithLaggingPresentation_KeepsCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        Assert.False(cursor.ShouldReleaseOnCompletion(
            MemberB,
            succeeded: true,
            presentationActiveMemberId: MemberA));

        Assert.Equal(MemberB, cursor.PendingTargetId);
    }

    [Fact]
    public void CompletionReport_CommittedPresentation_ReleasesCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        Assert.True(cursor.ShouldReleaseOnCompletion(
            MemberB,
            succeeded: true,
            presentationActiveMemberId: MemberB));
    }

    [Fact]
    public void CompletionReport_ForAnotherMember_DoesNotDisturbTheCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberC);

        // A superseded earlier request reporting failure must not release
        // the newer optimistic cursor.
        Assert.False(cursor.ShouldReleaseOnCompletion(MemberB, succeeded: false, MemberA));
        Assert.Equal(MemberC, cursor.PendingTargetId);
    }

    [Fact]
    public void Reconcile_CommittedMatch_ReleasesCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        Assert.True(cursor.Reconcile(MemberB));
        Assert.Null(cursor.PendingTargetId);
    }

    [Fact]
    public void Reconcile_NonMatchWithoutInterruption_KeepsInFlightCursor()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);

        // Deliberate coalescing semantics: while the switch is in flight the
        // committed presentation still shows the old member.
        Assert.False(cursor.Reconcile(MemberA));
        Assert.Equal(MemberB, cursor.PendingTargetId);
    }

    [Fact]
    public void Interrupt_ThenNewWheelStep_BehavesNormally()
    {
        var cursor = new WidgetGroupWheelSwitchCursor();
        cursor.SetPending(MemberB);
        cursor.NotifyInterrupted();

        // A fresh gesture after the reveal is unaffected by the interrupt:
        // its cursor records the post-interrupt epoch and survives an
        // in-flight reconcile, so normal switching keeps working.
        cursor.SetPending(MemberC);
        Assert.False(cursor.WasInterruptedSinceSet);
        Assert.False(cursor.Reconcile(MemberA));
        Assert.Equal(MemberC, cursor.PendingTargetId);
        Assert.True(cursor.Reconcile(MemberC));
    }
}

public sealed class WidgetGroupSwitchRequestOriginTests
{
    [Theory]
    [InlineData(WidgetGroupSwitchOrigin.Wheel)]
    [InlineData(WidgetGroupSwitchOrigin.Keyboard)]
    public void RelativeGestureOrigins_AreGatedOnHiddenSurfaces(WidgetGroupSwitchOrigin origin)
    {
        Assert.True(WidgetGroupSwitchRequest.IsRelativeGestureOrigin(origin));
    }

    [Theory]
    [InlineData(WidgetGroupSwitchOrigin.Programmatic)]
    [InlineData(WidgetGroupSwitchOrigin.Picker)]
    [InlineData(WidgetGroupSwitchOrigin.DragHover)]
    public void AbsoluteAndProgrammaticOrigins_KeepTheHiddenSurfaceFastPath(WidgetGroupSwitchOrigin origin)
    {
        Assert.False(WidgetGroupSwitchRequest.IsRelativeGestureOrigin(origin));
    }
}

public sealed class WindowsTapStartMenuLeakPolicyTests
{
    [Theory]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("startmenuexperiencehost")]
    [InlineData("  StartMenuExperienceHost  ")]
    public void StartMenuHostProcess_IsClassifiedAsALeak(string processName)
    {
        Assert.True(WindowsTapStartMenuLeakPolicy.IsStartMenuHostProcessName(processName));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("SearchHost")]
    [InlineData("SearchApp")]
    [InlineData("SearchUI")]
    [InlineData("DeskBox")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherForegroundProcesses_AreNotStartMenuLeaks(string? processName)
    {
        Assert.False(WindowsTapStartMenuLeakPolicy.IsStartMenuHostProcessName(processName));
    }
}
