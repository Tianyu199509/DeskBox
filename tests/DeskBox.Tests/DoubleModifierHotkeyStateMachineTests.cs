using DeskBox.Models;
using DeskBox.Services;
using Windows.System;

namespace DeskBox.Tests;

public sealed class DoubleModifierHotkeyStateMachineTests
{
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint LeftControl = 0xA2;
    private const uint LeftMenu = 0xA4;
    private const uint LeftWindows = 0x5B;
    private const uint Space = 0x20;
    private const uint KeyA = 0x41;

    [Theory]
    [InlineData(HotkeyModifierKeys.Shift, LeftShift)]
    [InlineData(HotkeyModifierKeys.Alt, LeftMenu)]
    [InlineData(HotkeyModifierKeys.Windows, LeftWindows)]
    public void TwoStandaloneTapsWithinWindow_TriggerOnSecondRelease(
        HotkeyModifierKeys modifier,
        uint virtualKey)
    {
        var machine = new DoubleModifierHotkeyStateMachine(modifier);

        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(virtualKey, isKeyDown: true, eventTime: 100));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(virtualKey, isKeyDown: false, eventTime: 160));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(virtualKey, isKeyDown: true, eventTime: 300));
        Assert.Equal(
            ReservedHotkeyEventDisposition.TriggerAndPassThrough,
            machine.Process(virtualKey, isKeyDown: false, eventTime: 360));

        // Pair is consumed: a third tap starts a fresh pair instead of re-triggering.
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(virtualKey, isKeyDown: true, eventTime: 450));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(virtualKey, isKeyDown: false, eventTime: 500));
    }

    [Fact]
    public void ChordUseOrTimeout_CancelsPendingTap()
    {
        var machine = new DoubleModifierHotkeyStateMachine(HotkeyModifierKeys.Shift);

        // Shift+A chord must never count as the first tap.
        machine.Process(LeftShift, isKeyDown: true, eventTime: 100);
        machine.Process(KeyA, isKeyDown: true, eventTime: 120);
        machine.Process(KeyA, isKeyDown: false, eventTime: 140);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(LeftShift, isKeyDown: false, eventTime: 160));

        machine.Process(LeftShift, isKeyDown: true, eventTime: 220);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(LeftShift, isKeyDown: false, eventTime: 260));

        // Second standalone tap past the 320ms window starts a new pair instead of firing.
        machine.Process(LeftShift, isKeyDown: true, eventTime: 620);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(LeftShift, isKeyDown: false, eventTime: 660));
    }

    [Fact]
    public void SimultaneousLeftAndRightModifierPress_CancelsTap()
    {
        var machine = new DoubleModifierHotkeyStateMachine(HotkeyModifierKeys.Shift);

        machine.Process(LeftShift, isKeyDown: true, eventTime: 100);
        machine.Process(RightShift, isKeyDown: true, eventTime: 120);
        machine.Process(RightShift, isKeyDown: false, eventTime: 140);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(LeftShift, isKeyDown: false, eventTime: 160));

        machine.Process(LeftShift, isKeyDown: true, eventTime: 220);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(LeftShift, isKeyDown: false, eventTime: 260));
    }

    [Fact]
    public void DoubleSpace_TriggersAndSuppressesSecondTapWhilePassingThroughFirstTap()
    {
        var machine = new DoubleSpaceHotkeyStateMachine();

        // First tap passes through with zero latency.
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 100));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: false, eventTime: 150));

        // Second tap within 300ms triggers immediately and suppresses Down2 + Up2.
        Assert.Equal(
            ReservedHotkeyEventDisposition.TriggerAndSuppress,
            machine.Process(Space, isKeyDown: true, eventTime: 320));
        Assert.Equal(
            ReservedHotkeyEventDisposition.Suppress,
            machine.Process(Space, isKeyDown: false, eventTime: 370));

        // Pair is consumed: an immediate third Space starts a fresh first tap.
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 460));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: false, eventTime: 510));
    }

    [Fact]
    public void DoubleSpace_RejectsLongHoldsAutoRepeatModifiersAndRolloverTyping()
    {
        var machine = new DoubleSpaceHotkeyStateMachine();

        // 1. Long Space hold (>220ms) must not arm a pending tap.
        machine.Process(Space, isKeyDown: true, eventTime: 100);
        machine.Process(Space, isKeyDown: false, eventTime: 360);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 450));
        machine.Process(Space, isKeyDown: false, eventTime: 500);

        // 2. OS auto-repeat while Space is held must never trigger.
        machine.Reset();
        machine.Process(Space, isKeyDown: true, eventTime: 600);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 680));
        machine.Process(Space, isKeyDown: false, eventTime: 700);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 780));
        machine.Process(Space, isKeyDown: false, eventTime: 820);

        // 3. Ctrl+Space IME chord followed by Space must not trigger.
        machine.Reset();
        machine.Process(LeftControl, isKeyDown: true, eventTime: 900);
        machine.Process(Space, isKeyDown: true, eventTime: 920);
        machine.Process(Space, isKeyDown: false, eventTime: 950);
        machine.Process(LeftControl, isKeyDown: false, eventTime: 970);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 1050));
        machine.Process(Space, isKeyDown: false, eventTime: 1090);

        // 4. Rollover typing (A down -> Space1 down -> A up -> Space1 up -> Space2 down) must not trigger.
        machine.Reset();
        machine.Process(KeyA, isKeyDown: true, eventTime: 1200);
        machine.Process(Space, isKeyDown: true, eventTime: 1230);
        machine.Process(KeyA, isKeyDown: false, eventTime: 1250);
        machine.Process(Space, isKeyDown: false, eventTime: 1280);
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            machine.Process(Space, isKeyDown: true, eventTime: 1380));
    }

    [Fact]
    public void DoubleTapChordGate_TriggersOnPairWithinWindowAndConsumesPair()
    {
        var gate = new DoubleTapChordGate();
        var altD = new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.D);
        var f7 = new GlobalHotkeyGesture(HotkeyModifierKeys.None, (int)VirtualKey.F7);

        Assert.False(gate.RecordPressAndShouldTrigger(altD, nowTicks: 1_000));
        Assert.True(gate.RecordPressAndShouldTrigger(altD, nowTicks: 1_240));

        // 3rd tap starts a new pair.
        Assert.False(gate.RecordPressAndShouldTrigger(altD, nowTicks: 1_400));

        // Expired window starts a new pair.
        Assert.False(gate.RecordPressAndShouldTrigger(altD, nowTicks: 1_900));

        // Different chord resets pending gesture.
        Assert.False(gate.RecordPressAndShouldTrigger(f7, nowTicks: 2_000));
        Assert.True(gate.RecordPressAndShouldTrigger(f7, nowTicks: 2_200));
    }
}
