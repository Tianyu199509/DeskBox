using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TransferIntentMatrixContractTests : IDisposable
{
    private readonly string _tempRoot;

    public TransferIntentMatrixContractTests()
    {
        string baseRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            baseRoot = Path.GetTempPath();
        }

        _tempRoot = Path.Combine(
            baseRoot,
            "DeskBox.Tests",
            nameof(TransferIntentMatrixContractTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void InternalCrossWidgetDrag_MovesExecutableByDefaultWhereasExternalInboundCreatesShortcut()
    {
        var executableOrigin = new SourceOriginSnapshot(
            InboundSourceProtectionKind.None,
            HasMoveProtectedRoot: false,
            HasUnhydratedCloudPlaceholder: false,
            HasApplicationCacheSource: false,
            HasTransientTempOrMsixCacheSource: false,
            PrefersShortcutByDefault: true);

        FileDropIntent internalMove = FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: true,
            forceCopy: false,
            controlDown: false,
            shiftDown: false,
            defaultMove: false,
            canCopy: true,
            canMove: true,
            altDown: false,
            followWindows: true,
            sameVolume: true,
            canLink: true,
            protectDefaultMoveAsCopy: false,
            sourceOrigin: executableOrigin,
            domain: TransferDomain.InternalDeskBox);

        Assert.Equal(FileDropIntent.Move, internalMove);

        FileDropIntent externalLink = FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: true,
            forceCopy: false,
            controlDown: false,
            shiftDown: false,
            defaultMove: false,
            canCopy: true,
            canMove: true,
            altDown: false,
            followWindows: true,
            sameVolume: true,
            canLink: true,
            protectDefaultMoveAsCopy: false,
            sourceOrigin: executableOrigin,
            domain: TransferDomain.ExternalInbound);

        Assert.Equal(FileDropIntent.Shortcut, externalLink);
    }

    [Theory]
    [InlineData(false, true, false, nameof(FileDropIntent.Move))]
    [InlineData(true, false, false, nameof(FileDropIntent.Copy))]
    [InlineData(false, false, true, nameof(FileDropIntent.Shortcut))]
    [InlineData(true, true, false, nameof(FileDropIntent.Shortcut))]
    public void ExplicitKeyboardModifiers_OverrideDefaultTransferIntentAcrossDomains(
        bool controlDown,
        bool shiftDown,
        bool altDown,
        string expectedIntentName)
    {
        FileDropIntent expectedIntent = Enum.Parse<FileDropIntent>(expectedIntentName);
        foreach (TransferDomain domain in new[] { TransferDomain.InternalDeskBox, TransferDomain.ExternalInbound })
        {
            FileDropIntent intent = FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: controlDown,
                shiftDown: shiftDown,
                defaultMove: false,
                canCopy: true,
                canMove: true,
                altDown: altDown,
                followWindows: true,
                sameVolume: false,
                canLink: true,
                protectDefaultMoveAsCopy: false,
                domain: domain);

            Assert.Equal(expectedIntent, intent);
        }
    }

    [Fact]
    public void UserFolderWithCompanionDll_IsNotMoveProtectedSystemOrSharedSource()
    {
        string appFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "PortableTool")).FullName;
        string exePath = Path.Combine(appFolder, "tool.exe");
        string dllPath = Path.Combine(appFolder, "helper.dll");
        File.WriteAllText(exePath, "binary");
        File.WriteAllText(dllPath, "library");

        Assert.False(DriveMediaInterop.IsProtectedDragOutSource([appFolder]));
        Assert.False(DriveMediaInterop.IsMoveProtectedExecutableOrInstallPath(exePath));
        Assert.True(DriveMediaInterop.IsInstalledOrBundledExecutablePath(exePath));
        SourceOriginSnapshot snapshot = PathOriginClassifier.ClassifySources([exePath]);
        Assert.False(snapshot.HasMoveProtectedRoot);
        Assert.True(snapshot.PrefersShortcutByDefault);
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
