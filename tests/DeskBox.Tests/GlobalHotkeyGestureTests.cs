using DeskBox.Models;
using DeskBox.Services;
using Windows.System;

namespace DeskBox.Tests;

public sealed class GlobalHotkeyGestureTests
{
    [Fact]
    public void NormalizeGesture_PreservesWindowsModifier()
    {
        GlobalHotkeyGesture gesture = GlobalHotkeyService.NormalizeGesture(
            (int)(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift),
            (int)VirtualKey.F7);

        Assert.Equal(
            HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift,
            gesture.Modifiers);
    }

    [Fact]
    public void WinSpaceAndAltSpace_AreReservedSystemGestures()
    {
        Assert.True(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, (int)VirtualKey.Space)));
        Assert.True(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.Space)));
        Assert.False(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows | HotkeyModifierKeys.Control,
                (int)VirtualKey.Space)));
        Assert.False(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, (int)VirtualKey.F7)));
    }

    [Theory]
    [InlineData(HotkeyModifierKeys.Windows, VirtualKey.F7)]
    [InlineData(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift, VirtualKey.F7)]
    [InlineData(HotkeyModifierKeys.Windows, VirtualKey.Space)]
    public void WindowsGestures_AreAcceptedByTheSharedRecorder(
        HotkeyModifierKeys modifiers,
        VirtualKey key)
    {
        Assert.True(GlobalHotkeyService.IsValidGesture(
            new GlobalHotkeyGesture(modifiers, (int)key)));
    }

    [Theory]
    [InlineData(HotkeyActivationKind.DoubleControl)]
    [InlineData(HotkeyActivationKind.WindowsTap)]
    public void SpecialActivationKinds_DoNotDependOnTheStoredFallbackChord(
        HotkeyActivationKind kind)
    {
        var activation = new GlobalHotkeyActivation(
            kind,
            new GlobalHotkeyGesture(HotkeyModifierKeys.None, 0));

        Assert.True(GlobalHotkeyService.IsValidActivation(activation));
    }

    [Theory]
    [InlineData(HotkeyModifierKeys.Control)]
    [InlineData(HotkeyModifierKeys.Shift)]
    [InlineData(HotkeyModifierKeys.Alt)]
    [InlineData(HotkeyModifierKeys.Windows)]
    public void DoubleTap_AcceptsSingleModifierAndValidChords_RejectsBareLettersAndMultiModifierOnly(
        HotkeyModifierKeys modifier)
    {
        Assert.True(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyActivation.FromDoubleTapModifier(modifier)));

        Assert.True(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyActivation.FromDoubleTapChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.None, (int)VirtualKey.F7))));
        Assert.True(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyActivation.FromDoubleTapChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.D))));

        Assert.True(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyService.DoubleSpaceActivation));
        Assert.False(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyActivation.FromChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.None, (int)VirtualKey.Space))));
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyService.DoubleSpaceActivation));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyService.DoubleSpaceActivation));

        // Bare typing keys and multi-modifier-without-key remain invalid even in double-tap mode.
        Assert.False(GlobalHotkeyService.IsValidActivation(
            GlobalHotkeyActivation.FromDoubleTapChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.None, (int)VirtualKey.A))));
        Assert.False(GlobalHotkeyService.IsValidActivation(
            new GlobalHotkeyActivation(
                HotkeyActivationKind.DoubleTap,
                new GlobalHotkeyGesture(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift, 0))));
    }

    [Fact]
    public void DoubleTapEquivalenceAndConflictRules_MatchLegacyDoubleControlAndChordOwnership()
    {
        var legacyDoubleCtrl = new GlobalHotkeyActivation(
            HotkeyActivationKind.DoubleControl,
            new GlobalHotkeyGesture(HotkeyModifierKeys.None, (int)VirtualKey.F7));
        var genericDoubleCtrl = GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Control);
        var doubleShift = GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift);

        Assert.True(GlobalHotkeyService.AreEquivalentActivations(legacyDoubleCtrl, genericDoubleCtrl));
        Assert.True(GlobalHotkeyService.DoActivationsConflict(legacyDoubleCtrl, genericDoubleCtrl));
        Assert.False(GlobalHotkeyService.DoActivationsConflict(genericDoubleCtrl, doubleShift));

        // Single-press Alt+D and double-tap Alt+D share the same RegisterHotKey slot, so they conflict.
        var singleAltD = GlobalHotkeyActivation.FromChord(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.D));
        var doubleAltD = GlobalHotkeyActivation.FromDoubleTapChord(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Alt, (int)VirtualKey.D));
        Assert.False(GlobalHotkeyService.AreEquivalentActivations(singleAltD, doubleAltD));
        Assert.True(GlobalHotkeyService.DoActivationsConflict(singleAltD, doubleAltD));
    }

    [Fact]
    public void WellKnownWindowsShortcuts_AreDetectedForRecorderWarning()
    {
        Assert.True(GlobalHotkeyService.IsWellKnownWindowsShortcut(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, (int)VirtualKey.V)));
        Assert.True(GlobalHotkeyService.IsWellKnownWindowsShortcut(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift, (int)VirtualKey.S)));
        Assert.False(GlobalHotkeyService.IsWellKnownWindowsShortcut(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Alt, (int)VirtualKey.V)));
    }

    [Fact]
    public void NormalizeActivation_RejectsUnknownKindByFallingBackToChord()
    {
        GlobalHotkeyActivation activation = GlobalHotkeyService.NormalizeActivation(
            (HotkeyActivationKind)999,
            (int)HotkeyModifierKeys.None,
            (int)VirtualKey.F7);

        Assert.Equal(HotkeyActivationKind.Chord, activation.Kind);
        Assert.Equal((int)VirtualKey.F7, activation.Gesture.VirtualKey);
    }

    [Fact]
    public void CopilotKeyGesture_IsAValidNonReservedChord()
    {
        Assert.Equal(
            HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift,
            GlobalHotkeyService.CopilotKeyGesture.Modifiers);
        Assert.Equal((int)VirtualKey.F23, GlobalHotkeyService.CopilotKeyGesture.VirtualKey);
        Assert.True(GlobalHotkeyService.IsValidGesture(GlobalHotkeyService.CopilotKeyGesture));
        Assert.False(GlobalHotkeyService.IsReservedSystemGesture(GlobalHotkeyService.CopilotKeyGesture));
        Assert.False(GlobalHotkeyService.IsRiskyGesture(GlobalHotkeyService.CopilotKeyGesture));
        Assert.Equal(
            GlobalHotkeyService.CopilotKeyGesture,
            GlobalHotkeyService.NormalizeGesture(
                (int)(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift),
                (int)VirtualKey.F23));
    }

    [Fact]
    public void SearchAltSpaceGesture_IsAValidReservedSystemGesture()
    {
        Assert.Equal(
            HotkeyModifierKeys.Alt,
            SearchHotkeyService.AltSpaceGesture.Modifiers);
        Assert.Equal((int)VirtualKey.Space, SearchHotkeyService.AltSpaceGesture.VirtualKey);
        Assert.True(GlobalHotkeyService.IsValidGesture(SearchHotkeyService.AltSpaceGesture));
        Assert.True(GlobalHotkeyService.IsReservedSystemGesture(SearchHotkeyService.AltSpaceGesture));
    }

    [Fact]
    public void UsesRegisterHotKeyChord_PlainChordsRideHeartbeatReservedModesDoNot()
    {
        // RegisterHotKey chords deliver WM_HOTKEY, have no callback and no
        // query API, so exactly they ride the watchdog heartbeat; the
        // reserved low-level-hook modes stay on callback liveness probing.
        Assert.True(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.None, (int)VirtualKey.F7))));
        Assert.True(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt, (int)VirtualKey.J))));
        Assert.True(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(GlobalHotkeyService.CopilotKeyGesture)));
        Assert.True(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromDoubleTapChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Alt, (int)VirtualKey.D))));

        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows, (int)VirtualKey.Space))));
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Alt, (int)VirtualKey.Space))));
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            new GlobalHotkeyActivation(
                HotkeyActivationKind.DoubleControl,
                new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0))));
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift)));
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            new GlobalHotkeyActivation(
                HotkeyActivationKind.WindowsTap,
                new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, 0))));

        // Unregistrable configurations must not join the heartbeat either —
        // they would just burn retries against a guaranteed failure.
        Assert.False(GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Control, 0))));
    }

    [Fact]
    public void SearchUsesRegisterHotKeyChord_AlignsWithGlobalAndExcludesReservedHookModes()
    {
        Assert.True(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.Control, (int)VirtualKey.K)));
        Assert.True(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.Windows, (int)VirtualKey.K)));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(
            SearchHotkeyService.AltSpaceGesture));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift)));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.None, 0)));
    }
}
