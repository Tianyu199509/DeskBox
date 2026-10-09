using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StartupElevationGateTests
{
    [Theory]
    [InlineData(false, false, true)]   // UAC on, Explorer standard: relaunch works
    [InlineData(false, true, false)]   // UAC on but Explorer elevated: parent handover would stay elevated
    [InlineData(true, false, false)]   // UAC off: no reliable standard-user context to promise
    [InlineData(true, true, false)]    // UAC off and Explorer elevated (the typical UAC-off machine)
    public void ComputeCanRelaunchUnelevated_OnlyPromisesARelaunchIntoAStandardUserContext(
        bool isUacDisabled,
        bool isExplorerElevated,
        bool expected)
    {
        Assert.Equal(
            expected,
            DragDropPermissionService.ComputeCanRelaunchUnelevated(isUacDisabled, isExplorerElevated));
    }

    [Fact]
    public void StartupElevationAssessment_UnrelaunchableWhenExplorerIsElevated()
    {
        // Guards against regressing to Diagnose's looser NeedsRelaunch gate
        // (which would offer a restart that lands on an elevated instance
        // again whenever Explorer itself runs elevated).
        bool uacOffWithElevatedExplorer = DragDropPermissionService.ComputeCanRelaunchUnelevated(
            isUacDisabled: true,
            isExplorerElevated: true);
        bool uacOnWithElevatedExplorer = DragDropPermissionService.ComputeCanRelaunchUnelevated(
            isUacDisabled: false,
            isExplorerElevated: true);

        Assert.False(uacOffWithElevatedExplorer);
        Assert.False(uacOnWithElevatedExplorer);
    }
}
