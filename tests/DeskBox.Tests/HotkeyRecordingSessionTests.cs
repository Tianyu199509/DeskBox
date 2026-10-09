using DeskBox.Models;
using Windows.System;

namespace DeskBox.Tests;

public sealed class HotkeyRecordingSessionTests
{
    private const uint LeftWindows = 0x5B;
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftMenu = 0xA4;
    private const uint LeftShift = 0xA0;
    private const uint Space = 0x20;
    private const uint Tab = 0x09;
    private const uint Escape = 0x1B;
    private const uint InternalMask = 0xE8;

    [Fact]
    public void ModifierKeyDown_TracksHeldModifiersLive()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        Assert.Equal(HotkeyModifierKeys.Control, session.HeldModifiers);
        Assert.False(session.HasGesture);

        session.ProcessKey(LeftMenu, isKeyDown: true);
        Assert.Equal(
            HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt,
            session.HeldModifiers);

        session.ProcessKey(LeftMenu, isKeyDown: false);
        Assert.Equal(HotkeyModifierKeys.Control, session.HeldModifiers);
    }

    [Fact]
    public void NonModifierKeyDown_SnapshotsChordWithHeldModifiers()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftWindows, isKeyDown: true);
        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.V, isKeyDown: true);

        Assert.True(session.HasGesture);
        Assert.Equal(
            new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows | HotkeyModifierKeys.Control,
                (int)VirtualKey.V),
            session.Gesture);
    }

    [Fact]
    public void ReleasingAllKeys_KeepsTheCapturedChord()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.D, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.D, isKeyDown: false);
        session.ProcessKey(LeftControl, isKeyDown: false);

        Assert.True(session.HasGesture);
        Assert.Equal(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Control, (int)VirtualKey.D),
            session.Gesture);
        Assert.Equal(HotkeyModifierKeys.None, session.HeldModifiers);
    }

    [Fact]
    public void SecondNonModifierKey_RecapturesWithCurrentModifiers()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.A, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.A, isKeyDown: false);
        session.ProcessKey(LeftControl, isKeyDown: false);
        session.ProcessKey(LeftShift, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.B, isKeyDown: true);

        Assert.Equal(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Shift, (int)VirtualKey.B),
            session.Gesture);
    }

    [Fact]
    public void DistinctPhysicalModifiers_CountTowardTheSameFlag()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey(RightControl, isKeyDown: true);
        session.ProcessKey(LeftControl, isKeyDown: false);

        Assert.Equal(HotkeyModifierKeys.Control, session.HeldModifiers);
    }

    [Fact]
    public void EscapeRequestsCancel()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(Escape, isKeyDown: true);

        Assert.True(session.CancelRequested);
        Assert.False(session.HasGesture);
    }

    [Fact]
    public void TabAndInjectedMaskKey_AreIgnored()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(Tab, isKeyDown: true);
        session.ProcessKey(InternalMask, isKeyDown: true);

        Assert.False(session.HasGesture);
        Assert.False(session.CancelRequested);
    }

    [Fact]
    public void NonModifierKeyUp_DoesNotChangeTheChord()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey(Space, isKeyDown: true);
        session.ProcessKey(Space, isKeyDown: false);

        Assert.Equal(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Control, (int)VirtualKey.Space),
            session.Gesture);
    }

    [Fact]
    public void Clear_DropsTheCapturedChord()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.F7, isKeyDown: true);
        session.Clear();

        Assert.False(session.HasGesture);
    }

    [Fact]
    public void GenericModifierVirtualKeys_TrackTheirFlag()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(0x11, isKeyDown: true); // VK_CONTROL generic
        session.ProcessKey(0x12, isKeyDown: true); // VK_MENU generic
        session.ProcessKey(0x10, isKeyDown: true); // VK_SHIFT generic

        Assert.Equal(
            HotkeyModifierKeys.Control |
            HotkeyModifierKeys.Alt |
            HotkeyModifierKeys.Shift,
            session.HeldModifiers);
    }
}
