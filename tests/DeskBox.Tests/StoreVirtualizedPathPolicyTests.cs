using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using DeskBox.Services.DragDrop;

namespace DeskBox.Tests;

public sealed class StoreVirtualizedPathPolicyTests : IDisposable
{
    private readonly string _sandbox;

    public StoreVirtualizedPathPolicyTests()
    {
        _sandbox = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "StoreVfs_" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                foreach (string dir in Directory.EnumerateDirectories(_sandbox, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }

                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void ResolveOutboundShellSourcePath_PreservesLeafJunctionWithoutDereferencing()
    {
        string targetDir = Directory.CreateDirectory(Path.Combine(_sandbox, "real-target")).FullName;
        string junctionDir = Path.Combine(_sandbox, "link-junction");
        bool created = TryCreateDirectoryJunction(junctionDir, targetDir);
        Assert.True(created, "Expected directory junction creation to succeed in temp sandbox.");

        int unvirtualizeCalls = 0;
        string directResolved = StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
            junctionDir,
            isPackagedOverride: false,
            unvirtualizeProbe: path =>
            {
                unvirtualizeCalls++;
                return targetDir;
            });
        string storeResolved = StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
            junctionDir,
            isPackagedOverride: true,
            unvirtualizeProbe: path =>
            {
                unvirtualizeCalls++;
                return targetDir;
            });

        Assert.Equal(Path.GetFullPath(junctionDir), directResolved, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(junctionDir), storeResolved, ignoreCase: true);
        Assert.Equal(0, unvirtualizeCalls);
    }

    [Fact]
    public void ResolveOutboundShellSourcePath_UnvirtualizesOnlyPackagedAppDataRegularFiles()
    {
        string fakeProfile = Path.Combine(_sandbox, "Users", "alice");
        string fakeLocalAppData = Path.Combine(fakeProfile, "AppData", "Local");
        string fakeRoamingAppData = Path.Combine(fakeProfile, "AppData", "Roaming");
        string fakeDeskBoxRoot = Path.Combine(fakeProfile, "DeskBox", ".appdata");
        string appDataFile = Path.Combine(fakeLocalAppData, "SomeApp", "data.txt");
        string regularFile = Path.Combine(fakeProfile, "Documents", "report.docx");

        int unvirtualizeCalls = 0;
        string expectedPhysical = Path.Combine(fakeLocalAppData, "Packages", "DeskBox", "LocalCache", "Local", "SomeApp", "data.txt");

        // Direct build never unvirtualizes regular files or AppData files.
        string directAppData = StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
            appDataFile,
            isPackagedOverride: false,
            isLinkOrMountPointProbe: _ => false,
            unvirtualizeProbe: _ =>
            {
                unvirtualizeCalls++;
                return expectedPhysical;
            },
            localAppDataOverride: fakeLocalAppData,
            roamingAppDataOverride: fakeRoamingAppData,
            userProfileOverride: fakeProfile,
            deskBoxRootOverride: fakeDeskBoxRoot);
        Assert.Equal(Path.GetFullPath(appDataFile), directAppData, ignoreCase: true);
        Assert.Equal(0, unvirtualizeCalls);

        // Packaged Store build unvirtualizes genuine AppData files so Explorer can read LocalCache,
        // while leaving non-AppData regular files untouched.
        string storeRegular = StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
            regularFile,
            isPackagedOverride: true,
            isLinkOrMountPointProbe: _ => false,
            unvirtualizeProbe: _ =>
            {
                unvirtualizeCalls++;
                return expectedPhysical;
            },
            localAppDataOverride: fakeLocalAppData,
            roamingAppDataOverride: fakeRoamingAppData,
            userProfileOverride: fakeProfile,
            deskBoxRootOverride: fakeDeskBoxRoot);
        Assert.Equal(Path.GetFullPath(regularFile), storeRegular, ignoreCase: true);
        Assert.Equal(0, unvirtualizeCalls);

        string storeAppData = StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
            appDataFile,
            isPackagedOverride: true,
            isLinkOrMountPointProbe: _ => false,
            unvirtualizeProbe: _ =>
            {
                unvirtualizeCalls++;
                return expectedPhysical;
            },
            localAppDataOverride: fakeLocalAppData,
            roamingAppDataOverride: fakeRoamingAppData,
            userProfileOverride: fakeProfile,
            deskBoxRootOverride: fakeDeskBoxRoot);
        Assert.Equal(expectedPhysical, storeAppData, ignoreCase: true);
        Assert.Equal(1, unvirtualizeCalls);
    }

    [Fact]
    public void StoreVirtualizedDestinationPolicy_BlocksAppDataWritesAndForcesCopyOnlySources()
    {
        string fakeProfile = Path.Combine(_sandbox, "Users", "alice");
        string fakeLocalAppData = Path.Combine(fakeProfile, "AppData", "Local");
        string fakeRoamingAppData = Path.Combine(fakeProfile, "AppData", "Roaming");
        string fakeLocalLow = Path.Combine(fakeProfile, "AppData", "LocalLow", "Vendor");
        string fakeDeskBoxRoot = Path.Combine(fakeProfile, "DeskBox", ".appdata");
        string localTarget = Path.Combine(fakeLocalAppData, "CustomTool", "Logs");
        string roamingTarget = Path.Combine(fakeRoamingAppData, "Notes");
        string normalDocs = Path.Combine(fakeProfile, "Documents", "Work");

        // Store build blocks writes into Local & Roaming AppData, allows LocalLow and Documents.
        Assert.True(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            localTarget,
            isPackagedOverride: true,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));
        Assert.True(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            roamingTarget,
            isPackagedOverride: true,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));
        Assert.False(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            fakeLocalLow,
            isPackagedOverride: true,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));
        Assert.False(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            normalDocs,
            isPackagedOverride: true,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));

        // Direct build does not block AppData writes.
        Assert.False(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            localTarget,
            isPackagedOverride: false,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));

        // Outbound drag sources under AppData are Copy-only in Store mode.
        Assert.True(StoreVirtualizedPathPolicy.IsCopyOnlyVirtualizedSource(
            [Path.Combine(localTarget, "trace.log")],
            isPackagedOverride: true,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));
        Assert.False(StoreVirtualizedPathPolicy.IsCopyOnlyVirtualizedSource(
            [Path.Combine(localTarget, "trace.log")],
            isPackagedOverride: false,
            fakeLocalAppData,
            fakeRoamingAppData,
            fakeProfile,
            fakeDeskBoxRoot));

        // ImportFailureMessagePolicy routes StoreVirtualizedDestinationException to its dedicated message key.
        var overrideMsg = ImportFailureMessagePolicy.SelectOverride(
            new StoreVirtualizedDestinationException(localTarget),
            requestedCount: 1,
            singleItemPath: null);
        Assert.NotNull(overrideMsg);
        Assert.Equal("Widget.DropBlocked.StoreVirtualizedAppData", overrideMsg!.Value.Key);
    }

    [Fact]
    public void StoreVirtualizedDestinationPolicy_BlocksJunctionOutsideAppDataPointingIntoAppData()
    {
        string fakeProfile = Path.Combine(_sandbox, "Users", "alice");
        string fakeLocalAppData = Directory.CreateDirectory(Path.Combine(fakeProfile, "AppData", "Local")).FullName;
        string fakeRoamingAppData = Path.Combine(fakeProfile, "AppData", "Roaming");
        string appDataSubdir = Directory.CreateDirectory(Path.Combine(fakeLocalAppData, "SecretCache")).FullName;
        string outsideJunction = Path.Combine(_sandbox, "PublicAlias");
        Assert.True(TryCreateDirectoryJunction(outsideJunction, appDataSubdir));

        Assert.True(StoreVirtualizedPathPolicy.IsWriteBlockedDestination(
            outsideJunction,
            isPackagedOverride: true,
            localAppDataOverride: fakeLocalAppData,
            roamingAppDataOverride: fakeRoamingAppData,
            userProfileOverride: fakeProfile));
    }

    [Fact]
    public void FileItemDragPackage_ProtectedCopyOnlyCheck_InspectsBothSourceAndPhysicalPaths()
    {
        string windowsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "notepad.exe");
        string benignLink = Path.Combine(_sandbox, "benign-link.exe");

        var resultWithProtectedPhysical = new FileItemDragPackageResult(
            SourcePaths: [benignLink],
            HasStorageItems: true,
            UsesNativeShellDataObject: true,
            PhysicalPaths: [windowsFile]);

        Assert.False(FileItemDragPackage.IsProtectedCopyOnlySource(resultWithProtectedPhysical.SourcePaths));
        Assert.True(FileItemDragPackage.IsProtectedCopyOnlySource(resultWithProtectedPhysical));
    }

    [Fact]
    public void ElevatedDesktopRecycleBinGuard_RequiresVisibleListViewAndNoCopyOrShortcutModifier()
    {
        Assert.False(ElevatedShellOutboundDropInterop.IsDesktopIconListViewVisible(
            listViewHwnd: 0,
            defViewHwnd: 100,
            isWindowVisibleProbe: _ => true));
        Assert.False(ElevatedShellOutboundDropInterop.IsDesktopIconListViewVisible(
            listViewHwnd: 200,
            defViewHwnd: 100,
            isWindowVisibleProbe: hwnd => hwnd == (IntPtr)100));
        Assert.False(ElevatedShellOutboundDropInterop.IsDesktopIconListViewVisible(
            listViewHwnd: 200,
            defViewHwnd: 100,
            isWindowVisibleProbe: hwnd => hwnd == (IntPtr)200));
        Assert.True(ElevatedShellOutboundDropInterop.IsDesktopIconListViewVisible(
            listViewHwnd: 200,
            defViewHwnd: 100,
            isWindowVisibleProbe: _ => true));

        Assert.True(ElevatedShellOutboundDropInterop.ShouldAllowDesktopRecycleBinDrop(
            controlHeld: false,
            altHeld: false));
        Assert.False(ElevatedShellOutboundDropInterop.ShouldAllowDesktopRecycleBinDrop(
            controlHeld: true,
            altHeld: false));
        Assert.False(ElevatedShellOutboundDropInterop.ShouldAllowDesktopRecycleBinDrop(
            controlHeld: false,
            altHeld: true));
    }

    private static bool TryCreateDirectoryJunction(string junction, string target)
    {
        using Process? mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        mklink?.WaitForExit(5000);
        return mklink?.ExitCode == 0 && Directory.Exists(junction);
    }
}
