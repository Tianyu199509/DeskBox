using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStoragePathServiceTests
{
    [Fact]
    public void SelectRecommendedPath_UsesLargestSuitableNonSystemFixedDrive()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 500L * 1024 * 1024 * 1024),
            new(@"D:\", DriveType.Fixed, true, 20L * 1024 * 1024 * 1024),
            new(@"E:\", DriveType.Fixed, true, 80L * 1024 * 1024 * 1024),
            new(@"F:\", DriveType.Removable, true, 200L * 1024 * 1024 * 1024)
        ];

        string selected = ManagedStoragePathService.SelectRecommendedPath(
            @"C:\Users\Simon",
            "Simon",
            @"C:\",
            drives);

        Assert.Equal(@"E:\DeskBox\Simon", selected);
    }

    [Fact]
    public void SelectRecommendedPath_IgnoresLowSpaceUnavailableAndNetworkDrives()
    {
        ManagedStorageDriveCandidate[] drives =
        [
            new(@"C:\", DriveType.Fixed, true, 100L * 1024 * 1024 * 1024),
            new(@"D:\", DriveType.Fixed, true, ManagedStoragePathService.MinimumRecommendedFreeSpaceBytes - 1),
            new(@"E:\", DriveType.Fixed, false, 100L * 1024 * 1024 * 1024),
            new(@"Z:\", DriveType.Network, true, 100L * 1024 * 1024 * 1024)
        ];

        string selected = ManagedStoragePathService.SelectRecommendedPath(
            @"C:\Users\Simon",
            "Simon",
            @"C:\",
            drives);

        Assert.Equal(@"C:\Users\Simon\DeskBox", selected);
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
