using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class Phase0DragDropSafetyTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase0DragDropSafetyTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBoxTests",
            "Phase0Safety",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            string extendedRoot = StorageMigrationNativeMethods.Extended(_tempRoot);
            if (Directory.Exists(extendedRoot))
            {
                Directory.Delete(extendedRoot, recursive: true);
            }
            else if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_MergesNestedFoldersAndRenamesOnlyCollidingLeafFiles()
    {
        var service = new FileService();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "src")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst")).FullName;

        string sourceProject = Directory.CreateDirectory(Path.Combine(sourceRoot, "Assets")).FullName;
        string sourceSub = Directory.CreateDirectory(Path.Combine(sourceProject, "Docs")).FullName;
        File.WriteAllText(Path.Combine(sourceSub, "readme.txt"), "incoming-readme");
        File.WriteAllText(Path.Combine(sourceSub, "new-spec.txt"), "incoming-spec");

        string destProject = Directory.CreateDirectory(Path.Combine(destinationRoot, "Assets")).FullName;
        string destSub = Directory.CreateDirectory(Path.Combine(destProject, "Docs")).FullName;
        File.WriteAllText(Path.Combine(destSub, "readme.txt"), "existing-readme");

        var results = await service.TransferItemsWithResultAsync(
            [sourceProject],
            destinationRoot,
            move: false,
            progress: new Progress<FileService.FileTransferProgress>(_ => { }));

        var result = Assert.Single(results);
        Assert.Equal(destProject, result.DestinationPath);
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "Assets (2)")));
        Assert.False(Directory.Exists(Path.Combine(destProject, "Docs (2)")));
        Assert.Equal("existing-readme", File.ReadAllText(Path.Combine(destSub, "readme.txt")));
        Assert.Equal("incoming-readme", File.ReadAllText(Path.Combine(destSub, "readme (2).txt")));
        Assert.Equal("incoming-spec", File.ReadAllText(Path.Combine(destSub, "new-spec.txt")));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_FolderCollidingWithExistingFile_AllocatesAvailableFolderName()
    {
        var service = new FileService();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "src-file-clash")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "dst-file-clash")).FullName;

        string sourceFolder = Directory.CreateDirectory(Path.Combine(sourceRoot, "Report")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "data.csv"), "1,2,3");

        // An extensionless file named "Report" blocks folder merging; must allocate "Report (2)".
        string blockingFile = Path.Combine(destinationRoot, "Report");
        File.WriteAllText(blockingFile, "plain-file");

        var results = await service.TransferItemsWithResultAsync([sourceFolder], destinationRoot, move: true);

        var result = Assert.Single(results);
        string expectedFolder = Path.Combine(destinationRoot, "Report (2)");
        Assert.Equal(expectedFolder, result.DestinationPath);
        Assert.Equal("plain-file", File.ReadAllText(blockingFile));
        Assert.Equal("1,2,3", File.ReadAllText(Path.Combine(expectedFolder, "data.csv")));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_LongPathOver270Chars_CopiesAndPreservesTimestamps()
    {
        var service = new FileService();
        string deepSegmentA = new string('a', 95);
        string deepSegmentB = new string('b', 95);
        string deepSegmentC = new string('c', 85);

        string sourceDir = Path.Combine(_tempRoot, "src-long", deepSegmentA, deepSegmentB, deepSegmentC);
        string destDir = Path.Combine(_tempRoot, "dst-long", deepSegmentA, deepSegmentB);
        Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(sourceDir));
        Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(destDir));

        string sourceFile = Path.Combine(sourceDir, "deep-document.txt");
        Assert.True(sourceFile.Length > 270, $"Expected >270 chars, got {sourceFile.Length}");
        string extendedSourceFile = StorageMigrationNativeMethods.Extended(sourceFile);
        File.WriteAllText(extendedSourceFile, "long-path-payload");
        var fixedWriteUtc = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(extendedSourceFile, fixedWriteUtc);

        FileService.FileTransferSourceIdentity? identity = FileService.TryCaptureSourceIdentity(sourceFile);
        Assert.NotNull(identity);
        Assert.True(FileService.SourceFileMatchesIdentity(sourceFile, identity!.Value));

        var results = await service.TransferItemsWithResultAsync(
            [sourceFile],
            destDir,
            move: false,
            progress: new Progress<FileService.FileTransferProgress>(_ => { }));

        var result = Assert.Single(results);
        string extendedDest = StorageMigrationNativeMethods.Extended(result.DestinationPath);
        Assert.Equal("long-path-payload", File.ReadAllText(extendedDest));
        Assert.Equal(fixedWriteUtc, File.GetLastWriteTimeUtc(extendedDest));
    }

    [Fact]
    public void TryFallbackIdentityFromLegacyHandleInfo_HandlesFatExFatErrorsAndFailsClosedOtherwise()
    {
        var validInfo = new FileTransferNativeMethods.ByHandleFileInformation
        {
            VolumeSerialNumber = 0x1234ABCDu,
            FileIndexHigh = 0x00000002u,
            FileIndexLow = 0x00000099u,
        };
        long fixedTicks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();

        // ERROR_INVALID_PARAMETER (87) on FAT32/exFAT falls back to 64-bit FileIndex.
        FileService.FileTransferSourceIdentity? fallback =
            FileService.TryFallbackIdentityFromLegacyHandleInfo(in validInfo, 87, 4096, fixedTicks);
        Assert.NotNull(fallback);
        Assert.Equal(0x1234ABCDul, fallback!.Value.VolumeSerialNumber);
        Assert.Equal(new FileService.FileId128(0, (2ul << 32) | 0x99ul), fallback.Value.FileId);

        // Unexpected Win32 error (e.g., ERROR_ACCESS_DENIED = 5) fails closed.
        Assert.Null(FileService.TryFallbackIdentityFromLegacyHandleInfo(in validInfo, 5, 4096, fixedTicks));

        // Zero FileIndex or zero VolumeSerialNumber fails closed.
        var zeroIndex = validInfo with { FileIndexHigh = 0, FileIndexLow = 0 };
        Assert.Null(FileService.TryFallbackIdentityFromLegacyHandleInfo(in zeroIndex, 87, 4096, fixedTicks));
    }

    [Theory]
    [InlineData((uint)StorageBusKind.Virtual, true)]
    [InlineData((uint)StorageBusKind.FileBackedVirtual, true)]
    [InlineData((uint)StorageBusKind.Usb, true)]
    [InlineData((uint)StorageBusKind.Nvme, false)]
    [InlineData((uint)StorageBusKind.Sata, false)]
    public void DriveMediaInterop_IsExternalBusKind_IncludesVirtualMountedDrives(
        uint busKind,
        bool expected)
    {
        Assert.Equal(expected, DriveMediaInterop.IsExternalBusKind((StorageBusKind)busKind));
    }

    [Theory]
    [InlineData(@"\Device\Volume{7cb45678-1234-5678-9abc-def012345678}", true)]
    [InlineData(@"\Device\WinFsp.Disk", true)]
    [InlineData(@"\Device\Dokan_1", true)]
    [InlineData(@"\Device\HarddiskVolume3", false)]
    [InlineData(@"\Device\HarddiskDmVolumes\PhysicalDmVolumes\BlockVolume1", false)]
    [InlineData(@"\??\C:\Users\simon\MappedFolder", false)]
    public void DriveMediaInterop_IsVirtualFilesystemTargetDevicePath_DistinguishesVirtualFromPhysicalAndSubst(
        string dosDeviceTarget,
        bool expected)
    {
        Assert.Equal(expected, DriveMediaInterop.IsVirtualFilesystemTargetDevicePath(dosDeviceTarget));
    }

    [Theory]
    [InlineData(@"C:\Users\simon\Documents\WeChat Files\wxid_123\FileStorage\File\2026-11\contract.pdf", true)]
    [InlineData(@"D:\xwechat_files\wxid_123_abc\msg\file\2026-11\report.xlsx", true)]
    [InlineData(@"C:\Users\simon\Documents\WXWork\168885000\FileStorage\File\2026-11\spec.docx", true)]
    [InlineData(@"C:\Users\simon\Documents\Tencent Files\1047078635\FileRecv\archive.zip", true)]
    [InlineData(@"C:\Users\simon\Documents\Tencent Files\nt_qq\nt_data\Pic\2026-11\img.png", true)]
    [InlineData(@"C:\Users\simon\Desktop\videoplayback.mp4", false)]
    [InlineData(@"D:\Projects\WeChat MiniProgram\app.json", false)]
    public void DriveMediaInterop_ChatAttachmentCaches_AreProtectedAsCopyByDefault(
        string candidatePath,
        bool expectedProtected)
    {
        Assert.Equal(expectedProtected, DriveMediaInterop.IsProtectedApplicationCachePath(candidatePath));

        InboundSourceProtectionKind kind = DriveMediaInterop.ClassifyInboundDropSources(
            [candidatePath],
            driveTypeProbe: static _ => DriveType.Fixed,
            busProbe: static _ => (StorageBusKind.Nvme, false));
        Assert.Equal(
            expectedProtected
                ? InboundSourceProtectionKind.ExternalRemovableOrNetwork
                : InboundSourceProtectionKind.None,
            kind);
    }

    [Theory]
    [InlineData(@"C:\Users\simon\AppData\Local\Packages\Microsoft.OutlookForWindows_8wekyb3d8bbwe\AC\INetCache\XYZ123\invoice.pdf", true)]
    [InlineData(@"C:\Users\simon\AppData\Local\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\TempState\share\photo.jpg", true)]
    [InlineData(@"C:\Users\simon\AppData\Local\Packages\SomeApp_abc123\AC\Temp\attachment.docx", true)]
    [InlineData(@"C:\Users\simon\AppData\Local\Packages\SomeApp_abc123\LocalState\saved.docx", false)]
    public void DropStagingFileOps_RecognizesMsixPackageTransientDropPaths(
        string candidatePath,
        bool expectedTransient)
    {
        bool actual = DropStagingFileOps.IsExternalTransientTempDropPath(
            candidatePath,
            hasShellIdList: true,
            tempRootWithSep: @"C:\Users\simon\AppData\Local\Temp\",
            virtualDropsRootWithSep: @"C:\Users\simon\AppData\Local\Temp\DeskBox\VirtualDrops\",
            devRootWithSep: null,
            inetCacheWithSep: @"C:\Users\simon\AppData\Local\Microsoft\Windows\INetCache\");
        Assert.Equal(expectedTransient, actual);
    }

    [Fact]
    public void NativeDropTarget_MaterializeShellIdListItems_DoesNotMarkRealFileSystemPathsAsTemporary()
    {
        string realFile = Path.Combine(_tempRoot, "real-photo.png");
        File.WriteAllText(realFile, "png");

        IReadOnlyList<string> materialized = NativeDropTarget.MaterializeShellIdListItems(
        [
            new NativeDropTarget.ShellIdListDropItem(
                NativeDropTarget.ShellIdListItemKind.FileSystemPath,
                AppUserModelId: string.Empty,
                ParsingName: realFile,
                DisplayName: "real-photo.png",
                FileSystemPath: realFile),
        ]);

        string singlePath = Assert.Single(materialized);
        Assert.Equal(realFile, singlePath);
        Assert.False(NativeDropTarget.ContainsVirtualDropStagedPaths(materialized));
    }
}
