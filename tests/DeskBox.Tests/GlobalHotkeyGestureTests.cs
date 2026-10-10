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
    public void SearchUsesRegisterHotKeyChord_ExcludesAltSpaceAndUnregistrableGestures()
    {
        Assert.True(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.Control, (int)VirtualKey.K)));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(
            SearchHotkeyService.AltSpaceGesture));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.Windows, (int)VirtualKey.K)));
        Assert.False(SearchHotkeyService.UsesRegisterHotKeyChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.None, 0)));
    }
}
