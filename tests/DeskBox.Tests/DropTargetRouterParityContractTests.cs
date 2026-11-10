using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DropTargetRouterParityContractTests : IDisposable
{
    private readonly string _tempRoot;

    public DropTargetRouterParityContractTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            nameof(DropTargetRouterParityContractTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void TryGetFolderDropDestinationPath_AcceptsUserFolderShortcutAndRejectsMoveProtectedSystemFolderShortcut()
    {
        string userFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "UserDocs")).FullName;
        var userFolderShortcut = new WidgetItem
        {
            Path = Path.Combine(_tempRoot, "UserDocs.lnk"),
            TargetPath = userFolder,
            IsShortcut = true,
            IsFolder = false
        };

        Assert.True(ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
            userFolderShortcut,
            out string resolvedUserFolder));
        Assert.Equal(userFolder, resolvedUserFolder);
        Assert.Equal(
            ItemDropBehavior.FolderImport,
            ItemDropBehaviorPolicy.Resolve(userFolderShortcut));

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemFolderShortcut = new WidgetItem
        {
            Path = Path.Combine(_tempRoot, "Windows.lnk"),
            TargetPath = windowsDir,
            IsShortcut = true,
            IsFolder = false
        };

        Assert.False(ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
            systemFolderShortcut,
            out _));
        Assert.Equal(
            ItemDropBehavior.None,
            ItemDropBehaviorPolicy.Resolve(systemFolderShortcut));
    }

    [Fact]
    public void ShortcutLaunchPolicy_AllowsCrossSurfaceDropOntoStackPopoverShortcutWhileKeepingIntraPopoverDragAsReorder()
    {
        Assert.Equal(
            ShortcutLaunchDecision.Launch,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: false,
                paths: [@"C:\notes\doc.md"],
                shortcutPath: @"C:\grid\editor.lnk",
                targetHostedInStackPopover: true));

        Assert.Equal(
            ShortcutLaunchDecision.None,
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: true,
                paths: [@"C:\notes\doc.md"],
                shortcutPath: @"C:\grid\editor.lnk",
                targetHostedInStackPopover: true));
    }

    [Fact]
    public void ShortcutFileLauncher_EscapesDriveRootTrailingBackslashAndResolvesInternetShortcutUrls()
    {
        string driveRootArgs = ShortcutFileLauncher.BuildArgumentString([@"D:\"]);
        Assert.Equal("\"D:\\\\\"", driveRootArgs);

        string urlFilePath = Path.Combine(_tempRoot, "site.url");
        File.WriteAllText(
            urlFilePath,
            "[InternetShortcut]\r\nURL=https://deskbox.fun/docs/\r\n");

        string[] sanitized = ShortcutFileLauncher.SanitizeDroppedPaths([urlFilePath]);
        Assert.Equal(["https://deskbox.fun/docs/"], sanitized);
        Assert.Equal(
            "\"https://deskbox.fun/docs/\"",
            ShortcutFileLauncher.BuildArgumentString(sanitized));
    }

    [Fact]
    public void StackPopoverAndSpringBackRouting_MaintainsResolvedStackKeyAndCurrentFolderInvalidation()
    {
        string stackPopoverSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));
        Assert.Contains(
            "TryResolveStackPopoverChildIconDropTarget",
            stackPopoverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueStackPopoverReconciliation()",
            stackPopoverSource,
            StringComparison.Ordinal);

        string surfaceSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        Assert.Contains(
            "AreAllSourcesAlreadyInDestinationLexically",
            surfaceSource,
            StringComparison.Ordinal);

        string elevatedSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ElevatedPointerDrag.cs"));
        Assert.Contains(
            "TryResolveFolderNavigationDropTarget",
            elevatedSource,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
