using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class NativeDropEffectPolicyTests
{
    [Fact]
    public void PayloadClassification_PhysicalPathsWinOverVirtualDescriptors()
    {
        Assert.False(
            NativeDropEffectPolicy.IsVirtualOnlyFileData(
                hasPhysicalPathData: true,
                hasVirtualDescriptorData: true));
        Assert.True(
            NativeDropEffectPolicy.IsVirtualOnlyFileData(
                hasPhysicalPathData: false,
                hasVirtualDescriptorData: true));
        Assert.False(
            NativeDropEffectPolicy.IsVirtualOnlyFileData(
                hasPhysicalPathData: false,
                hasVirtualDescriptorData: false));
    }

    [Fact]
    public void Feedback_DefaultsToMoveForPhysicalFiles()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move));
    }

    [Fact]
    public void Feedback_PhysicalFilesIgnoreAnAvailableLinkEffect()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move |
                                NativeDropEffectPolicy.Link));
    }

    [Fact]
    public void Feedback_UsesCopyForControlOrVirtualFiles()
    {
        const uint controlKeyState = 0x0008;
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: controlKeyState,
                allowedEffects: allowed));
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: true,
                keyState: 0,
                allowedEffects: allowed));
    }

    [Fact]
    public void Feedback_UsesConfiguredCopyUnlessShiftForcesMove()
    {
        const uint shiftKeyState = 0x0004;
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                defaultMove: false));
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: shiftKeyState,
                allowedEffects: allowed,
                defaultMove: false));
    }

    [Fact]
    public void Feedback_ControlWinsWhenControlAndShiftAreBothPressed()
    {
        const uint controlAndShiftKeyState = 0x0008 | 0x0004;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: controlAndShiftKeyState,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move));
    }

    [Fact]
    public void Feedback_UsesLinkForCtrlShiftOrAltWhenLinkIsAllowed()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move |
                       NativeDropEffectPolicy.Link;

        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: NativeDropEffectPolicy.ControlKeyState |
                          NativeDropEffectPolicy.ShiftKeyState,
                allowedEffects: allowed));
        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: NativeDropEffectPolicy.AltKeyState,
                allowedEffects: allowed));
    }

    [Fact]
    public void RightButtonStateIsTrackedSeparatelyFromCopyMoveIntent()
    {
        Assert.True(
            NativeDropEffectPolicy.IsRightButtonDrag(
                NativeDropEffectPolicy.RightButtonKeyState));
        Assert.False(NativeDropEffectPolicy.IsRightButtonDrag(0));
        Assert.True(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: NativeDropEffectPolicy.AltKeyState));
        Assert.False(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: true,
                keyState: NativeDropEffectPolicy.AltKeyState));
    }

    [Fact]
    public void TransferIntent_UsesTheConfiguredMoveWhenFeedbackIsCopyOnly()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy,
                defaultMove: true));
        Assert.False(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: false,
                keyState: 0,
                defaultMove: true));
    }

    [Fact]
    public void TransferIntent_RespectsTemporaryFilesAndDropModifiers()
    {
        const uint shiftKeyState = 0x0004;
        const uint controlKeyState = 0x0008;

        Assert.True(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: true,
                keyState: shiftKeyState,
                defaultMove: true));
        Assert.True(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: false,
                keyState: controlKeyState,
                defaultMove: true));
        Assert.False(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: false,
                keyState: shiftKeyState,
                defaultMove: false));
    }

    [Fact]
    public void Completion_NeverAuthorizesSourceMoveCleanup()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: allowed));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: NativeDropEffectPolicy.Move));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: false,
                allowedEffects: allowed));
    }

    [Fact]
    public void ShellApplications_UseLinkWithoutChangingFileCopyMovePolicy()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move |
                       NativeDropEffectPolicy.Link;

        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                hasShellApplicationData: true));
        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: allowed,
                createdShellApplicationLinks: true));
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy,
                hasShellApplicationData: true));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Move,
                hasShellApplicationData: true));
    }

    [Fact]
    public void ShortcutOutsideDesktop_RequestsShortcutForNonDesktopSources()
    {
        Assert.True(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: 0,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        Assert.True(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: false,
                keyState: 0,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
    }

    [Fact]
    public void ShortcutOutsideDesktop_ModifierGesturesStillWin()
    {
        const uint controlKeyState = 0x0008;
        const uint shiftKeyState = 0x0004;

        // Ctrl=copy and Shift=move must beat the desktop-based shortcut
        // default so the completed operation matches the feedback cursor.
        Assert.False(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: controlKeyState,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        Assert.False(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: shiftKeyState,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        // Alt and Ctrl+Shift remain explicit shortcut gestures.
        Assert.True(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: NativeDropEffectPolicy.AltKeyState,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
        Assert.True(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: controlKeyState | shiftKeyState,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
    }

    [Fact]
    public void ShortcutOutsideDesktop_KeepsMoveForDesktopSources()
    {
        Assert.False(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: false,
                keyState: 0,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
        Assert.False(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: false,
                keyState: 0,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
    }

    [Fact]
    public void ShortcutOutsideDesktop_NeverAppliesToTemporaryPayloads()
    {
        Assert.False(
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: true,
                keyState: 0,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        Assert.True(
            NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                containsTemporaryFiles: true,
                keyState: 0,
                defaultMove: true,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
    }

    [Fact]
    public void ShortcutOutsideDesktop_FeedbackUsesLinkWhenAllowed()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move |
                       NativeDropEffectPolicy.Link;

        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: true));
    }

    [Fact]
    public void ShortcutOutsideDesktop_FeedbackFallsBackToCopyWithoutLink()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                shortcutOutsideDesktop: true,
                sourcesOnDesktop: false));
    }
}
