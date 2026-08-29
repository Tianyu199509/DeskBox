using System.IO;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStoragePathServiceTests
{
    [Fact]
    public void GetRecommendedPath_AlwaysStaysUnderUserProfile()
    {
        // The default for new profiles must not depend on whatever hardware
        // happens to be attached at first launch (e.g. a USB drive).
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "DeskBox");

        Assert.Equal(expected, ManagedStoragePathService.GetRecommendedPath());
    }

    [Fact]
    public void SelectSuitableNonSystemDrive_PrefersLargestInternalNonSystemDrive()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024, false),
            new(@"D:\", DriveType.Fixed, true, 20L * 1024 * 1024 * 1024, false),
            new(@"E:\", DriveType.Fixed, true, 80L * 1024 * 1024 * 1024, false),
            // A large USB drive reporting as fixed must lose to internal drives.
            new(@"F:\", DriveType.Fixed, true, 200L * 1024 * 1024 * 1024, true),
            new(@"G:\", DriveType.Removable, true, 100L * 1024 * 1024 * 1024, true)
        ];

        ManagedStorageDriveCandidate? selected = ManagedStoragePathService.SelectSuitableNonSystemDrive(
            @"C:\",
            drives);

        Assert.Equal(@"E:\", selected?.RootPath);
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
