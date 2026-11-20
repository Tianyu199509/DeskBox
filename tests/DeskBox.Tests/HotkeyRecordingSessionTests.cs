using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views.Dialogs;
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
    public void FreshModifierHoldAfterCommittedChord_ClearsPriorPrimaryKeyForLivePreview()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.A, isKeyDown: true);
        session.ProcessKey((uint)VirtualKey.A, isKeyDown: false);
        session.ProcessKey(LeftControl, isKeyDown: false);
        Assert.True(session.HasGesture);

        // Starting a brand-new modifier hold from idle clears the prior chord
        // so live keycap preview shows only Shift instead of stale Ctrl+A.
        session.ProcessKey(LeftShift, isKeyDown: true);
        Assert.False(session.HasGesture);
        Assert.Equal(HotkeyModifierKeys.Shift, session.HeldModifiers);

        // Releasing without pressing a primary key surfaces Shift as a standalone modifier.
        session.ProcessKey(LeftShift, isKeyDown: false);
        Assert.False(session.HasGesture);
        Assert.True(session.HasStandaloneModifier);
        Assert.Equal(HotkeyModifierKeys.Shift, session.LastStandaloneModifier);
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
    public void EscapeRequestsCancelOnlyWhenNoModifiersAreHeld()
    {
        var modified = new HotkeyRecordingSession();
        modified.ProcessKey(LeftControl, isKeyDown: true);
        modified.ProcessKey(LeftShift, isKeyDown: true);
        modified.ProcessKey(Escape, isKeyDown: true);
        Assert.False(modified.CancelRequested);

        var bare = new HotkeyRecordingSession();
        bare.ProcessKey(Escape, isKeyDown: true);
        Assert.True(bare.CancelRequested);
        Assert.False(bare.HasGesture);
    }

    [Fact]
    public void BareTabAndInjectedMaskKey_AreIgnored_WhileModifiedTabCaptures()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(Tab, isKeyDown: true);
        session.ProcessKey(InternalMask, isKeyDown: true);

        Assert.False(session.HasGesture);
        Assert.False(session.CancelRequested);

        session.ProcessKey(LeftMenu, isKeyDown: true);
        session.ProcessKey(Tab, isKeyDown: true);
        Assert.True(session.HasGesture);
        Assert.Equal(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)Tab),
            session.Gesture);
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
    public void Clear_DropsTheCapturedChordAndStandaloneModifier()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftShift, isKeyDown: true);
        session.ProcessKey(LeftShift, isKeyDown: false);
        Assert.True(session.HasStandaloneModifier);

        session.Clear();
        Assert.False(session.HasGesture);
        Assert.False(session.HasStandaloneModifier);
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

    [Fact]
    public void MultipleModifiersReleasedWithoutPrimaryKey_AbortsStandaloneModifierCandidate()
    {
        var session = new HotkeyRecordingSession();

        session.ProcessKey(LeftControl, isKeyDown: true);
        session.ProcessKey(LeftShift, isKeyDown: true);
        session.ProcessKey(LeftShift, isKeyDown: false);
        session.ProcessKey(LeftControl, isKeyDown: false);

        Assert.Equal(HotkeyModifierKeys.None, session.HeldModifiers);
        Assert.False(session.HasGesture);
        Assert.False(session.HasStandaloneModifier);
        Assert.Equal(HotkeyModifierKeys.None, session.LastStandaloneModifier);

        // A subsequent clean single-modifier press still records normally.
        session.ProcessKey(LeftMenu, isKeyDown: true);
        session.ProcessKey(LeftMenu, isKeyDown: false);
        Assert.True(session.HasStandaloneModifier);
        Assert.Equal(HotkeyModifierKeys.Alt, session.LastStandaloneModifier);
    }

    [Fact]
    public void AbortHeldModifiers_ClearsLiveModifiersAndCandidate_WhilePreservingCommittedChord()
    {
        var midHold = new HotkeyRecordingSession();
        midHold.ProcessKey(LeftWindows, isKeyDown: true);
        Assert.Equal(HotkeyModifierKeys.Windows, midHold.HeldModifiers);

        midHold.AbortHeldModifiers();
        Assert.Equal(HotkeyModifierKeys.None, midHold.HeldModifiers);
        Assert.False(midHold.HasStandaloneModifier);

        // Late key-up after focus loss must not synthesize a standalone modifier.
        midHold.ProcessKey(LeftWindows, isKeyDown: false);
        Assert.False(midHold.HasStandaloneModifier);

        var committed = new HotkeyRecordingSession();
        committed.ProcessKey(LeftControl, isKeyDown: true);
        committed.ProcessKey((uint)VirtualKey.K, isKeyDown: true);
        committed.AbortHeldModifiers();

        Assert.True(committed.HasGesture);
        Assert.Equal(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Control, (int)VirtualKey.K),
            committed.Gesture);
        Assert.Equal(HotkeyModifierKeys.None, committed.HeldModifiers);
    }

    [Fact]
    public void HookEvaluation_PassesThroughInjectedAndTaggedEventsWithoutCapturing()
    {
        using var hook = new HotkeyRecorderHookService(uiDispatcherQueue: null);

        var injected = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.V,
            flags: Win32Helper.LLKHF_INJECTED,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(injected.Swallow);
        Assert.False(injected.InjectShellMask);
        Assert.Null(injected.DispatchedEvent);

        var lowerIl = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.V,
            flags: 0x02,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(lowerIl.Swallow);
        Assert.Null(lowerIl.DispatchedEvent);

        var tagged = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: InternalMask,
            flags: 0,
            extraInfo: HotkeyRecorderHookService.InjectedEventTag,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(tagged.Swallow);
        Assert.Null(tagged.DispatchedEvent);
    }

    [Fact]
    public void HookEvaluation_WhenTargetWindowNotForeground_PassesThroughAndResetsHeldKeysOnce()
    {
        using var hook = new HotkeyRecorderHookService(uiDispatcherQueue: null);

        var heldDown = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: LeftControl,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(heldDown.Swallow);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent(LeftControl, IsKeyDown: true),
            heldDown.DispatchedEvent);

        var firstBackgroundKey = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.A,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: false,
            hasUnmaskedShellModifierDown: false);
        Assert.False(firstBackgroundKey.Swallow);
        Assert.False(firstBackgroundKey.InjectShellMask);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent(0, IsKeyDown: false, IsFocusLostReset: true),
            firstBackgroundKey.DispatchedEvent);

        var secondBackgroundKey = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.B,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: false,
            hasUnmaskedShellModifierDown: false);
        Assert.False(secondBackgroundKey.Swallow);
        Assert.Null(secondBackgroundKey.DispatchedEvent);
    }

    [Fact]
    public void HookEvaluation_PassesThroughKeyUpWhenKeyDownWasNotSwallowed()
    {
        using var hook = new HotkeyRecorderHookService(uiDispatcherQueue: null);

        var preHeldRelease = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYUP,
            vkCode: LeftWindows,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(preHeldRelease.Swallow);
        Assert.Null(preHeldRelease.DispatchedEvent);

        var down = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.F7,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(down.Swallow);

        var matchingRelease = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYUP,
            vkCode: (uint)VirtualKey.F7,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(matchingRelease.Swallow);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent((uint)VirtualKey.F7, IsKeyDown: false),
            matchingRelease.DispatchedEvent);
    }

    [Fact]
    public void HookEvaluation_PassthroughRequested_AllowsBareButtonsWhileCapturingModifiedSpaceAndEnter()
    {
        using var hook = new HotkeyRecorderHookService(uiDispatcherQueue: null)
        {
            PassthroughRequested = true
        };

        var bareSpace = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: Space,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(bareSpace.Swallow);
        Assert.Null(bareSpace.DispatchedEvent);

        var bareEnter = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_KEYDOWN,
            vkCode: (uint)VirtualKey.Enter,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.False(bareEnter.Swallow);
        Assert.Null(bareEnter.DispatchedEvent);

        // Pre-held unmasked Alt/Win + Space must still be captured and shell-masked.
        var preHeldShellSpace = hook.ProcessHookKeyEvent(
            nCode: 0,
            wParam: Win32Helper.WM_SYSKEYDOWN,
            vkCode: Space,
            flags: 0,
            extraInfo: IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: true);
        Assert.True(preHeldShellSpace.Swallow);
        Assert.True(preHeldShellSpace.InjectShellMask);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent(Space, IsKeyDown: true),
            preHeldShellSpace.DispatchedEvent);
        _ = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_SYSKEYUP,
            Space,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);

        // Swallowed modifier + Space is also captured even when a button owns keyboard focus.
        _ = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            LeftControl,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        var ctrlSpace = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            Space,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(ctrlSpace.Swallow);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent(Space, IsKeyDown: true),
            ctrlSpace.DispatchedEvent);
    }

    [Fact]
    public void HookEvaluation_RequestsShellMaskOncePerWinOrAltStroke_AndCoalescesAutoRepeat()
    {
        using var hook = new HotkeyRecorderHookService(uiDispatcherQueue: null);

        var winDown = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            LeftWindows,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(winDown.Swallow);
        Assert.True(winDown.InjectShellMask);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent(LeftWindows, IsKeyDown: true),
            winDown.DispatchedEvent);

        var winAutoRepeat = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            LeftWindows,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(winAutoRepeat.Swallow);
        Assert.False(winAutoRepeat.InjectShellMask);
        Assert.Null(winAutoRepeat.DispatchedEvent);

        var vDown = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            (uint)VirtualKey.V,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(vDown.Swallow);
        Assert.False(vDown.InjectShellMask);
        Assert.Equal(
            new HotkeyRecorderHookService.RecordedKeyEvent((uint)VirtualKey.V, IsKeyDown: true),
            vDown.DispatchedEvent);

        var vAutoRepeat = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_KEYDOWN,
            (uint)VirtualKey.V,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(vAutoRepeat.Swallow);
        Assert.Null(vAutoRepeat.DispatchedEvent);

        _ = hook.ProcessHookKeyEvent(0, Win32Helper.WM_KEYUP, (uint)VirtualKey.V, 0, IntPtr.Zero, true, false);
        _ = hook.ProcessHookKeyEvent(0, Win32Helper.WM_KEYUP, LeftWindows, 0, IntPtr.Zero, true, false);

        // Next stroke re-arms shell mask injection.
        var nextAltDown = hook.ProcessHookKeyEvent(
            0,
            Win32Helper.WM_SYSKEYDOWN,
            LeftMenu,
            0,
            IntPtr.Zero,
            isTargetForeground: true,
            hasUnmaskedShellModifierDown: false);
        Assert.True(nextAltDown.Swallow);
        Assert.True(nextAltDown.InjectShellMask);
    }

    [Fact]
    public void ConvertActivationForDoubleTapMode_ConvertsChordsModifiersDoubleControlAndWindowsTap()
    {
        var altSpaceChord = GlobalHotkeyActivation.FromChord(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.Space));
        var altSpaceDouble = HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(
            altSpaceChord,
            doubleTapEnabled: true);
        Assert.Equal(HotkeyActivationKind.DoubleTap, altSpaceDouble.Kind);
        Assert.Equal(altSpaceChord.Gesture, altSpaceDouble.Gesture);
        Assert.Equal(
            altSpaceChord,
            HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(altSpaceDouble, doubleTapEnabled: false));

        var singleShiftChord = new GlobalHotkeyActivation(
            HotkeyActivationKind.Chord,
            new GlobalHotkeyGesture(HotkeyModifierKeys.Shift, 0));
        var doubleShift = HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(
            singleShiftChord,
            doubleTapEnabled: true);
        Assert.True(doubleShift.IsModifierOnlyDoubleTap);
        Assert.Equal(HotkeyModifierKeys.Shift, doubleShift.DoubleTapModifier);
        Assert.Equal(
            singleShiftChord,
            HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(doubleShift, doubleTapEnabled: false));

        var doubleCtrl = new GlobalHotkeyActivation(
            HotkeyActivationKind.DoubleControl,
            new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0));
        Assert.Equal(
            doubleCtrl,
            HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(doubleCtrl, doubleTapEnabled: true));
        Assert.Equal(
            new GlobalHotkeyActivation(
                HotkeyActivationKind.Chord,
                new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0)),
            HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(doubleCtrl, doubleTapEnabled: false));

        var windowsTap = new GlobalHotkeyActivation(
            HotkeyActivationKind.WindowsTap,
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, 0));
        var doubleWindows = HotkeyRecorderDialog.ConvertActivationForDoubleTapMode(
            windowsTap,
            doubleTapEnabled: true);
        Assert.True(doubleWindows.IsModifierOnlyDoubleTap);
        Assert.Equal(HotkeyModifierKeys.Windows, doubleWindows.DoubleTapModifier);
    }
}
