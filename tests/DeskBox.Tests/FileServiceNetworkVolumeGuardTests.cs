using System;
using System.IO;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class FileServiceNetworkVolumeGuardTests
{
    [Fact]
    public void IsNetworkVolumePath_IdentifiesUncShares()
    {
        Assert.True(FileService.IsNetworkVolumePath(
            @"\\server\share\folder\file.txt"));
    }

    [Fact]
    public void IsNetworkVolumePath_IdentifiesMappedNetworkDrivesDeterministically()
    {
        string? observedRoot = null;
        bool isNetwork = FileService.IsNetworkVolumePath(
            @"Z:\shared-workspace\report.xlsx",
            root =>
            {
                observedRoot = root;
                return DriveType.Network;
            });

        Assert.True(isNetwork);
        Assert.Equal(@"Z:\", observedRoot);

        Assert.False(FileService.IsNetworkVolumePath(
            @"Z:\shared-workspace\report.xlsx",
            _ => DriveType.Fixed));

        Assert.False(FileService.IsNetworkVolumePath(
            @"Z:\shared-workspace\report.xlsx",
            _ => throw new IOException("Drive disappeared")));
    }

    [Fact]
    public void IsNetworkVolumePath_LocalVolumesAreNotNetwork()
    {
        string localPath = Path.Combine(Path.GetTempPath(), "file.txt");

        Assert.False(FileService.IsNetworkVolumePath(localPath));
    }
}
