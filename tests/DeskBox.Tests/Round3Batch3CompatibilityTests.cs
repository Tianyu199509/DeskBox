using System.Text;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Round3Batch3CompatibilityTests
{
    [Fact]
    public void OneDriveKfmDesktopAlignment_IgnoresUnpinnedAloneAndExemptsPersonalDesktop()
    {
        const uint FileAttributeNormal = 0x00000080u;
        const uint FileAttributeUnpinned = 0x00100000u;
        const uint FileAttributeRecallOnDataAccess = 0x00400000u;
        const uint FileAttributeOffline = 0x00001000u;

        // Hydrated file or empty folder carrying FILE_ATTRIBUTE_UNPINNED alone is NOT unhydrated
        Assert.False(DriveMediaInterop.HasUnhydratedCloudFileAttributes(FileAttributeNormal | FileAttributeUnpinned));
        Assert.False(DriveMediaInterop.HasUnhydratedCloudFileAttributes(0x00000010u | FileAttributeUnpinned));

        // Genuine recall / offline flags ARE detected outside personal Desktop
        Assert.True(DriveMediaInterop.HasUnhydratedCloudFileAttributes(FileAttributeRecallOnDataAccess));
        Assert.True(DriveMediaInterop.HasUnhydratedCloudFileAttributes(FileAttributeOffline));

        string fakeOneDriveDesktop = @"C:\Users\alice\OneDrive\Desktop";
        string kfmDesktopFile = @"C:\Users\alice\OneDrive\Desktop\ProjectSpec.docx";
        string nonDesktopOneDriveFile = @"C:\Users\alice\OneDrive\Documents\Archive.zip";

        Assert.True(DriveMediaInterop.IsPathUnderPersonalUserDesktop(kfmDesktopFile, fakeOneDriveDesktop));
        Assert.False(DriveMediaInterop.IsPathUnderPersonalUserDesktop(fakeOneDriveDesktop, fakeOneDriveDesktop));
        Assert.False(DriveMediaInterop.IsPathUnderPersonalUserDesktop(nonDesktopOneDriveFile, fakeOneDriveDesktop));

        // Decision 3A: personal Desktop (including OneDrive KFM Desktop) is exempted from cloud forced-Copy
        Assert.False(DriveMediaInterop.IsUnhydratedCloudPlaceholderPath(
            kfmDesktopFile,
            userDesktopOverride: fakeOneDriveDesktop,
            fileAttributesProbe: _ => FileAttributeRecallOnDataAccess));

        // Non-desktop OneDrive recall-on-data-access placeholder remains protected as Copy
        Assert.True(DriveMediaInterop.IsUnhydratedCloudPlaceholderPath(
            nonDesktopOneDriveFile,
            userDesktopOverride: fakeOneDriveDesktop,
            fileAttributesProbe: _ => FileAttributeRecallOnDataAccess));
    }

    [Fact]
    public void CrossWidgetDragOutHintAndShortcutArgQuoting_SuppressDeskBoxOwnWindowAndEscapeTrailingSlashes()
    {
        // Dropping onto another DeskBox widget or Todo/QuickCapture never shows an external "Copied" toast
        Assert.True(FileSurfaceContent.ShouldSuppressDragOutResultHint(
            handledAsStackMembership: false,
            FileSurfaceContent.ExternalDragObservation.Full,
            DropTargetWindowKind.DeskBoxOwnWindow));

        Assert.False(FileSurfaceContent.ShouldSuppressDragOutResultHint(
            handledAsStackMembership: false,
            FileSurfaceContent.ExternalDragObservation.Full,
            DropTargetWindowKind.ExplorerFileView));

        Assert.False(FileSurfaceContent.ShouldSuppressDragOutResultHint(
            handledAsStackMembership: false,
            FileSurfaceContent.ExternalDragObservation.Brief,
            DropTargetWindowKind.NonExplorerWindow));

        // Directories with trailing separator and drive roots format safely for CommandLineToArgvW
        string args = ShortcutFileLauncher.BuildArgumentString(
        [
            @"C:\Projects\My Repo\",
            @"D:\",
        ]);
        Assert.Equal("\"C:\\Projects\\My Repo\" \"D:\\\\\"", args);
    }

    [Fact]
    public void WebpageLinkVirtualDropAndGlobalMemorySize_AcceptDropEffectLinkAndTruncateToDeclaredFileSize()
    {
        // Webpage <a href> drag: HasVirtualFileData == true, AllowedEffects == DROPEFFECT_LINK (no Copy bit)
        TransferIntentDecision resolution = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: true,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: true,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: false,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: false,
            FollowWindows: true,
            SameVolume: false,
            CanLink: true,
            SourceOrigin: SourceOriginSnapshot.Empty));

        Assert.Equal(FileDropIntent.Copy, resolution.Intent);
        Assert.Equal(NativeDropEffectPolicy.Link, resolution.FeedbackEffect);

        uint completion = NativeDropEffectPolicy.ResolveCompletionEffect(
            hasExtractedPaths: true,
            allowedEffects: NativeDropEffectPolicy.Link,
            createdShellApplicationLinks: false,
            hasVirtualOnlyData: true);
        Assert.Equal(NativeDropEffectPolicy.Link, completion);

        // Over-allocated HGLOBAL truncates to FD_FILESIZE when smaller than GlobalSize
        Assert.Equal(137, NativeDropTarget.ResolveEffectiveGlobalMemoryLength(4096, 137));
        Assert.Equal(0, NativeDropTarget.ResolveEffectiveGlobalMemoryLength(64, 0));
        Assert.Equal(512, NativeDropTarget.ResolveEffectiveGlobalMemoryLength(512, null));
        Assert.Equal(512, NativeDropTarget.ResolveEffectiveGlobalMemoryLength(512, 1024));
    }

    [Fact]
    public void VirtualDropMagicBytesAndImCacheProtection_CoverHeicAvifFeishuDingTalkTelegramAndNtQq()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox_R3B3_Magic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string heicNoExt = Path.Combine(tempDir, "iphone_photo");
            byte[] heicHeader = new byte[16];
            Encoding.ASCII.GetBytes("ftypheic").CopyTo(heicHeader, 4);
            File.WriteAllBytes(heicNoExt, heicHeader);

            string resolvedHeic = VirtualDropFileNameResolver.AddMissingExtensionFromContent(heicNoExt);
            Assert.Equal(".heic", Path.GetExtension(resolvedHeic));

            string avifNoExt = Path.Combine(tempDir, "web_hero");
            byte[] avifHeader = new byte[16];
            Encoding.ASCII.GetBytes("ftypavif").CopyTo(avifHeader, 4);
            File.WriteAllBytes(avifNoExt, avifHeader);

            string resolvedAvif = VirtualDropFileNameResolver.AddMissingExtensionFromContent(avifNoExt);
            Assert.Equal(".avif", Path.GetExtension(resolvedAvif));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Assert.True(DriveMediaInterop.IsProtectedApplicationCachePath(
            @"C:\Users\alice\AppData\Roaming\LarkShell\sdk_storage\abc\FileStorage\2026-11\spec.pdf"));
        Assert.True(DriveMediaInterop.IsProtectedApplicationCachePath(
            @"C:\Users\alice\AppData\Local\Feishu\Image\preview.png"));
        Assert.True(DriveMediaInterop.IsProtectedApplicationCachePath(
            @"C:\Users\alice\AppData\Roaming\DingTalk\12345_v2\StandardStorage\FileRecv\budget.xlsx"));
        Assert.True(DriveMediaInterop.IsProtectedApplicationCachePath(
            @"C:\Users\alice\AppData\Roaming\Telegram Desktop\tdata\user_data\photo.jpg"));
        Assert.True(DriveMediaInterop.IsProtectedApplicationCachePath(
            @"D:\CustomChat\nt_qq\nt_data\Pic\2026-11\Ori\img.jpg"));
    }

    [Fact]
    public void ElevatedExplorerMultiTabAndChromeGuard_BlocksInactiveTabStripAndTreeWhileKeepingFolderView()
    {
        // Active folder file-view hierarchy remains ExplorerFolder
        Assert.Equal(
            ElevatedOutboundTargetKind.ExplorerFolder,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["DirectUIHWND", "SHELLDLL_DefView", "ShellTabWindowClass", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        // Win11 top XAML Island tab bar / command bar (outside ShellTabWindowClass) is rejected
        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["Microsoft.UI.Content.DesktopChildSiteBridge", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        // Left navigation tree & classic address/breadcrumb bands are rejected
        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["SysTreeView32", "NamespaceTreeControl", "ShellTabWindowClass", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));

        Assert.Equal(
            ElevatedOutboundTargetKind.None,
            ElevatedShellOutboundDropInterop.ClassifyWindowClasses(
                ["ToolbarWindow32", "Breadcrumb Parent", "Address Band Root", "CabinetWClass"],
                "CabinetWClass",
                matchesShellPid: true,
                isCurrentProcess: false));
    }

    [Fact]
    public void LanmanUncMountCacheKeyAndCollisionNaming_HandleNetUseMountPointsDotfilesAndVersionedFolders()
    {
        // net use Z: \\nas01\design -> \Device\LanmanRedirector\;Z:000000000001a2b3\nas01\design
        Assert.True(PathOriginClassifier.TryExtractLanmanUncPrefix(
            @"\Device\LanmanRedirector\;Z:000000000001a2b3\nas01\design",
            'Z',
            out string unc1));
        Assert.Equal(@"\\nas01\design", unc1);

        Assert.True(PathOriginClassifier.TryExtractLanmanUncPrefix(
            @"\Device\LanmanRedirector\fileserver\teamshare",
            'Y',
            out string unc2));
        Assert.Equal(@"\\fileserver\teamshare", unc2);

        // NTFS mount-point directory keeps its own cache key instead of poisoning C:\Mounts\
        string mountKey = PathOriginClassifier.GetDirectoryLookupKey(
            @"C:\Mounts\CameraSD",
            directoryExistsProbe: p => string.Equals(p, @"C:\Mounts\CameraSD", StringComparison.OrdinalIgnoreCase));
        string siblingFileKey = PathOriginClassifier.GetDirectoryLookupKey(
            @"C:\Mounts\LocalDocs\report.pdf",
            directoryExistsProbe: _ => false);
        Assert.Equal(@"C:\Mounts\CameraSD\", mountKey);
        Assert.Equal(@"C:\Mounts\LocalDocs\", siblingFileKey);

        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox_R3B3_Collision_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string versionedFolder = Path.Combine(tempDir, "Release_v1.2");
            Directory.CreateDirectory(versionedFolder);
            string availableFolder = FileService.GetAvailablePath(versionedFolder);
            Assert.Equal("Release_v1.2 (2)", Path.GetFileName(availableFolder));

            string dotfile = Path.Combine(tempDir, ".gitignore");
            File.WriteAllText(dotfile, "bin/");
            string availableDotfile = FileService.GetAvailablePath(dotfile);
            Assert.Equal(".gitignore (2)", Path.GetFileName(availableDotfile));

            string regularFile = Path.Combine(tempDir, "report.v1.pdf");
            File.WriteAllText(regularFile, "pdf");
            string availableRegular = FileService.GetAvailablePath(regularFile);
            Assert.Equal("report.v1 (2).pdf", Path.GetFileName(availableRegular));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
