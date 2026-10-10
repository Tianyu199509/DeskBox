using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsTapHotkeyStateMachineTests
{
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private const uint D = 0x44;

    [Theory]
    [InlineData(LeftWindows)]
    [InlineData(RightWindows)]
    public void StandaloneWindowsTap_PreparesMaskOnPressAndTriggersOnReleaseWithoutSuppressingIt(uint windowsKey)
    {
        var state = new WindowsTapHotkeyStateMachine();

        // The press edge must ask for the Start-menu mask while the key is
        // still held (feedback 367): the release-side mask alone races the
        // key-up delivery.
        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(windowsKey, true));
        Assert.Equal(
            ReservedHotkeyEventDisposition.TriggerAndPassThrough,
            state.Process(windowsKey, false));
    }

    [Theory]
    [InlineData(LeftWindows)]
    [InlineData(RightWindows)]
    public void WindowsKeyAutoRepeat_DoesNotReprepareTheMask(uint windowsKey)
    {
        var state = new WindowsTapHotkeyStateMachine();

        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(windowsKey, true));

        // Holding the key emits repeated key-downs; only the first edge may
        // inject the mask so the input stream stays free of repeat noise.
        Assert.Equal(
            ReservedHotkeyEventDisposition.PassThrough,
            state.Process(windowsKey, true));
        Assert.Equal(
            ReservedHotkeyEventDisposition.TriggerAndPassThrough,
            state.Process(windowsKey, false));
    }

    [Fact]
    public void WindowsChord_StillTriggersMaskPrepareButNeverStandaloneActivation()
    {
        var state = new WindowsTapHotkeyStateMachine();

        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(LeftWindows, true));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(D, true));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(D, false));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(LeftWindows, false));
    }

    [Fact]
    public void HoldingBothWindowsKeys_IsTreatedAsAChordAndMasksEachPressEdge()
    {
        var state = new WindowsTapHotkeyStateMachine();
        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(LeftWindows, true));
        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(RightWindows, true));

        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(LeftWindows, false));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(RightWindows, false));
    }

    [Fact]
    public void Reset_ForgetsPressStateSoNextPressIsATransitionAgain()
    {
        var state = new WindowsTapHotkeyStateMachine();
        state.Process(LeftWindows, true);
        state.Reset();

        Assert.Equal(
            ReservedHotkeyEventDisposition.PrepareMaskAndPassThrough,
            state.Process(LeftWindows, true));
    }
}
