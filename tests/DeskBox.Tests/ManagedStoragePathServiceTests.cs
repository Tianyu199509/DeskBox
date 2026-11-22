using System.IO;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStoragePathServiceTests
{
    [Fact]
    public void SelectRecommendedPath_PrefersConciseDDeskBoxWhenDDriveIsSuitable()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 20L * 1024 * 1024 * 1024, false),
            new(@"E:\", DriveType.Fixed, true, 800L * 1024 * 1024 * 1024, false)
        ];

        string selected = ManagedStoragePathService.SelectRecommendedPath(
            @"C:\Users\Simon",
            @"C:\",
            drives);

        Assert.Equal(@"D:\DeskBox", selected);
    }

    [Fact]
    public void SelectRecommendedPath_SkipsInvalidCandidateAndFallsBackToUserProfileWhenOnlySystemDriveQualifies()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, false),
            // Portable USB SSD reporting as fixed must not become the automatic default.
            new(@"D:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, true)
        ];

        string singleInternal = ManagedStoragePathService.SelectRecommendedPath(
            @"C:\Users\Simon",
            @"C:\",
            drives);
        Assert.Equal(@"C:\Users\Simon\DeskBox", singleInternal);

        // If the user redirected their Windows Desktop directly to D:\ so D:\DeskBox
        // fails root validation, SelectRecommendedPath moves on to E:\DeskBox.
        ManagedStorageDriveCandidate[] withDesktopOnD =
        [
            new(@"C:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, false),
            new(@"E:\", DriveType.Fixed, true, 150L * 1024 * 1024 * 1024, false)
        ];
        string skippedInvalidD = ManagedStoragePathService.SelectRecommendedPath(
            @"C:\Users\Simon",
            @"C:\",
            withDesktopOnD,
            candidatePath => !string.Equals(candidatePath, @"D:\DeskBox", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"E:\DeskBox", skippedInvalidD);
    }

    [Fact]
    public void SelectSuitableNonSystemDrive_PrefersDDriveFirstThenLargestInternalNonSystemDrive()
    {
        ManagedStorageDriveCandidate[] drivesWithD =
        [
            new(@"C:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 20L * 1024 * 1024 * 1024, false),
            new(@"E:\", DriveType.Fixed, true, 80L * 1024 * 1024 * 1024, false),
            // A large USB drive reporting as fixed must lose to internal drives.
            new(@"F:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, true),
            new(@"G:\", DriveType.Removable, true, 100L * 1024 * 1024 * 1024, true)
        ];

        ManagedStorageDriveCandidate? selectedWithD = ManagedStoragePathService.SelectSuitableNonSystemDrive(
            @"C:\",
            drivesWithD);
        Assert.Equal(@"D:\", selectedWithD?.RootPath);

        ManagedStorageDriveCandidate[] drivesWhereDIsUsb =
        [
            new(@"C:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, true),
            new(@"E:\", DriveType.Fixed, true, 40L * 1024 * 1024 * 1024, false),
            new(@"F:\", DriveType.Fixed, true, 120L * 1024 * 1024 * 1024, false)
        ];

        ManagedStorageDriveCandidate? selectedFallback = ManagedStoragePathService.SelectSuitableNonSystemDrive(
            @"C:\",
            drivesWhereDIsUsb);
        Assert.Equal(@"F:\", selectedFallback?.RootPath);
    }

    [Fact]
    public void BuildSelectableStorageDriveOptions_IncludesSystemProfileAndInternalFixedDrivesInOrder()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"E:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, false),
            new(@"C:\", DriveType.Fixed, true, 80L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 5L * 1024 * 1024 * 1024, false),
            // Tiny OEM recovery partition (< 1 GB) and USB drives are excluded from quick pills.
            new(@"F:\", DriveType.Fixed, true, 500L * 1024 * 1024, false),
            new(@"G:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, true),
            new(@"H:\", DriveType.CDRom, true, 0, true)
        ];

        IReadOnlyList<ManagedStorageSelectableDriveOption> options =
            ManagedStoragePathService.BuildSelectableStorageDriveOptions(
                @"C:\Users\Simon",
                @"C:\",
                drives);

        Assert.Collection(
            options,
            c =>
            {
                Assert.Equal("C:", c.DriveLabel);
                Assert.Equal(@"C:\Users\Simon\DeskBox", c.TargetRootPath);
                Assert.True(c.IsSystemDrive);
            },
            d =>
            {
                Assert.Equal("D:", d.DriveLabel);
                Assert.Equal(@"D:\DeskBox", d.TargetRootPath);
                Assert.False(d.IsSystemDrive);
            },
            e =>
            {
                Assert.Equal("E:", e.DriveLabel);
                Assert.Equal(@"E:\DeskBox", e.TargetRootPath);
                Assert.False(e.IsSystemDrive);
            });
    }

    [Fact]
    public void SelectSuitableNonSystemDrive_ReturnsNullWithoutInternalCandidates()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 100L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, ManagedStoragePathService.MinimumRecommendedFreeSpaceBytes - 1, false),
            new(@"E:\", DriveType.Fixed, false, 100L * 1024 * 1024 * 1024, false),
            new(@"F:\", DriveType.Fixed, true, 100L * 1024 * 1024 * 1024, true),
            new(@"Z:\", DriveType.Network, true, 100L * 1024 * 1024 * 1024, true)
        ];

        Assert.Null(ManagedStoragePathService.SelectSuitableNonSystemDrive(@"C:\", drives));
    }

    [Theory]
    [InlineData(StorageBusTypeHelper.BusTypeUsb, true)]
    [InlineData(StorageBusTypeHelper.BusType1394, true)]
    [InlineData(StorageBusTypeHelper.BusTypeSd, true)]
    [InlineData(StorageBusTypeHelper.BusTypeMmc, true)]
    [InlineData(StorageBusTypeHelper.BusTypeVirtual, true)]
    [InlineData(StorageBusTypeHelper.BusTypeFileBackedVirtual, true)]
    [InlineData(0x01, false)] // Scsi
    [InlineData(0x03, false)] // Ata
    [InlineData(0x0B, false)] // Sata
    [InlineData(0x11, false)] // Nvme
    [InlineData(null, false)]
    public void IsTransientBus_ClassifiesDetachableBuses(int? busType, bool expected)
    {
        Assert.Equal(expected, StorageBusTypeHelper.IsTransientBus(busType));
    }

    [Theory]
    [InlineData(@"D:\DeskBox", @"D:\DeskBox", true)]
    [InlineData(@"D:\DeskBox\Simon\Files", @"D:\DeskBox", true)]
    [InlineData(@"D:\DeskBoxes", @"D:\DeskBox", false)]
    [InlineData(@"C:\DeskBox", @"D:\DeskBox", false)]
    public void IsSameOrDescendant_RespectsDirectoryBoundaries(
        string path,
        string directory,
        bool expected)
    {
        Assert.Equal(
            expected,
            ManagedStoragePathService.IsSameOrDescendant(path, directory));
    }
}
