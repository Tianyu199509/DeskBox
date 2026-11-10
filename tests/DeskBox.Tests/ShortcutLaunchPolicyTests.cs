using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class ShortcutLaunchPolicyTests
{
    private const string ExeTarget = @"C:\Tools\Photoshop.exe";
    private const string DirectoryTarget = @"C:\Tools\Previews";
    private const string MissingTarget = @"C:\Gone\Editor.exe";

    [Fact]
    public void ShortcutToExecutableFile_IsLaunchTarget()
    {
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: @"C:\grid\Photoshop.lnk",
                targetPath: ExeTarget,
                targetIsDirectory: false));
    }

    [Fact]
    public void ShortcutToDirectory_IsNotLaunchTarget()
    {
        // v1 red line: folder shortcuts keep their import semantics because
        // delegating would let the Shell move user files into the target.
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: @"C:\grid\Previews.lnk",
                targetPath: DirectoryTarget,
                targetIsDirectory: true));
    }

    [Theory]
    [InlineData(@"C:\grid\Site.url")]
    [InlineData(@"C:\grid\Readme.txt")]
    public void NonShellLinkPaths_AreNotLaunchTargets(string path)
    {
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: path,
                targetPath: ExeTarget,
                targetIsDirectory: false));
    }

    [Fact]
    public void NonShortcutItem_IsNotLaunchTarget()
    {
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: false,
                path: @"C:\grid\Notes.txt",
                targetPath: ExeTarget,
                targetIsDirectory: false));
    }

    [Fact]
    public void MissingStoredTarget_StaysLaunchTargetForLinkTracking()
    {
        // Explorer resolves moved targets through link tracking, so a missing
        // stored path must not silently convert the drop into an import.
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: @"C:\grid\Editor.lnk",
                targetPath: MissingTarget,
                targetIsDirectory: false));
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData("https://example.com/app")]
    [InlineData("shell:AppsFolder\\Microsoft.Photos_8wekyb3d8bbwe!App")]
    public void NonLocalFilesystemTargets_AreNotLaunchTargets(string targetPath)
    {
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: @"C:\grid\App.lnk",
                targetPath: targetPath,
                targetIsDirectory: false));
    }

    [Fact]
    public void EmptyTarget_IsNotLaunchTarget()
    {
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.Evaluate(
                isShortcut: true,
                path: @"C:\grid\Broken.lnk",
                targetPath: string.Empty,
                targetIsDirectory: false));
    }

    [Fact]
    public void InternalDragOnShortcut_LaunchesWithTheDraggedTiles()
    {
        // Dragging a tile onto an application-shortcut tile inside one widget
        // means the same thing as dragging an Explorer file there: open it with
        // the linked application. The dragged item stays in the widget.
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: false,
                paths: [@"C:\grid\photo.png"],
                shortcutPath: @"C:\grid\Photoshop.lnk"));
    }

    [Fact]
    public void DraggingAShortcutOntoItself_KeepsTheReorder()
    {
        // Putting a shortcut tile back where it was means "reorder", not "open
        // this shortcut with itself" - which used to launch the .lnk as a file
        // and make the linked application report a broken image.
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: false,
                paths: [@"C:\grid\Photoshop.lnk"],
                shortcutPath: @"C:\Grid\Photoshop.lnk"));
    }

    [Fact]
    public void DraggingAShortcutOntoADifferentShortcut_Launches()
    {
        // Only the shortcut being dragged is excluded; another shortcut tile is
        // still a launch target.
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: false,
                paths: [@"C:\grid\Photoshop.lnk"],
                shortcutPath: @"C:\grid\Editor.lnk"));
    }

    [Fact]
    public void ExternalDrag_IsNotAnInternalLaunch()
    {
        // An Explorer drag carries no DeskBox source paths; it stays on the
        // external branch, which resolves its own files.
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: false,
                isStackPopoverMemberDrag: false,
                paths: [@"C:\users\photo.png"],
                shortcutPath: @"C:\grid\Photoshop.lnk"));
    }

    [Fact]
    public void StackPopoverMemberDrag_LaunchesOnMainSurfaceButStaysReorderInsidePopover()
    {
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: true,
                paths: [@"C:\grid\photo.png"],
                shortcutPath: @"C:\grid\Photoshop.lnk",
                targetHostedInStackPopover: true));
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: true,
                paths: [@"C:\grid\photo.png"],
                shortcutPath: @"C:\grid\Photoshop.lnk",
                targetHostedInStackPopover: false));
    }

    [Fact]
    public void InternalDragWithoutResolvedPaths_IsNotALaunch()
    {
        // A browser drop materializes into temporary files later, so an empty
        // path list must not claim the launch gesture.
        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: false,
                paths: [],
                shortcutPath: @"C:\grid\Photoshop.lnk"));
    }

    [Fact]
    public void ContainsSameFile_MatchesByFullPathCaseInsensitively()
    {
        Assert.True(ShortcutLaunchPolicy.ContainsSameFile(
            [@"C:\grid\photo.png", @"C:\GRID\Photoshop.lnk"],
            @"C:\grid\Photoshop.lnk"));
        Assert.False(ShortcutLaunchPolicy.ContainsSameFile(
            [@"C:\grid\photo.png"],
            @"C:\grid\Photoshop.lnk"));
        Assert.False(ShortcutLaunchPolicy.ContainsSameFile(
            [@"C:\grid\photo.png"],
            string.Empty));
    }

    [Theory]
    [InlineData(DataPackageOperation.None)]
    [InlineData(DataPackageOperation.Move)]
    [InlineData(DataPackageOperation.Link)]
    public void CompletedInternalDrag_LaunchesWhileTheTileStillHoldsThePointer(
        DataPackageOperation dropResult)
    {
        // WinUI never delivers the routed Drop for a same-ListView drag, and the
        // operation the tile reports depends on what the source allowed (Move
        // for an internal reorder), so only the recorded hover and the release
        // point decide.
        Assert.True(ShortcutLaunchPolicy.ShouldLaunchFromCompletedInternalDrag(
            dropResult,
            hoveredLaunchTarget: true,
            cursorInsideLaunchTarget: true));
    }

    [Fact]
    public void CompletedInternalDrag_DoesNotLaunchAfterLeavingTheTile()
    {
        Assert.False(ShortcutLaunchPolicy.ShouldLaunchFromCompletedInternalDrag(
            DataPackageOperation.Move,
            hoveredLaunchTarget: true,
            cursorInsideLaunchTarget: false));
        Assert.False(ShortcutLaunchPolicy.ShouldLaunchFromCompletedInternalDrag(
            DataPackageOperation.Move,
            hoveredLaunchTarget: false,
            cursorInsideLaunchTarget: true));
    }

    [Fact]
    public void IsPointInsideRectWithSlack_IncludesTheSlackBandOnly()
    {
        // A default 30px icon with 6px slack: the label and padding beside the
        // icon (beyond the slack band) must fall outside the launch hit area.
        Assert.True(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, 15, 15));
        Assert.True(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, -6, 0));
        Assert.True(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, 36, 30));
        Assert.False(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, -6.01, 0));
        Assert.False(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, 36.01, 30));
        Assert.False(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 30, 6, 60, 15));
    }

    [Fact]
    public void IsPointInsideRectWithSlack_RejectsEmptyRects()
    {
        // An unmeasured icon host (zero bounds) can never claim the launch.
        Assert.False(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 0, 30, 6, 0, 15));
        Assert.False(ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0, 0, 30, 0, 6, 15, 0));
    }

    [Fact]
    public void ExpandTargetPath_ResolvesEnvironmentVariables()
    {
        string expanded = ShortcutLaunchPolicy.ExpandTargetPath(
            "%SystemRoot%\\System32\\notepad.exe");
        Assert.EndsWith("notepad.exe", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%SystemRoot%", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsExpandedTargetDirectory_DetectsSystemDirectory()
    {
        Assert.True(ShortcutLaunchPolicy.IsExpandedTargetDirectory(
            "%SystemRoot%\\System32"));
        Assert.False(ShortcutLaunchPolicy.IsExpandedTargetDirectory(
            "%SystemRoot%\\System32\\notepad.exe"));
        Assert.False(ShortcutLaunchPolicy.IsExpandedTargetDirectory(string.Empty));
    }
}
