using Windows.ApplicationModel.DataTransfer;
using DeskBox.Controls;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class InternalArrangementProtocolContractTests
{
    [Theory]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, false, false)]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, false, true)]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true, false)]
    [InlineData(SettingsService.ManagedDragOutActionFollowWindows, true, true)]
    [InlineData(SettingsService.ManagedDragOutActionMove, false, false)]
    [InlineData(SettingsService.ManagedDragOutActionMove, true, false)]
    [InlineData(SettingsService.ManagedDragOutActionCopy, false, false)]
    [InlineData(SettingsService.ManagedDragOutActionCopy, true, false)]
    public void ResolveDragOutAllowedOperations_NeverReturnsWinUiUnsetSentinelSeven(
        string action,
        bool isWindows11OrNewer,
        bool isProtectedCopyOnly)
    {
        const DataPackageOperation winUiUnsetSentinel =
            DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;

        DataPackageOperation allowed = FileItemDragPackage.ResolveDragOutAllowedOperations(
            action,
            isWindows11OrNewer,
            sourcePaths: null,
            isProtectedCopyOnly: isProtectedCopyOnly);
        DataPackageOperation preferred = FileItemDragPackage.ResolveDragOutPreferredOperation(
            action,
            sourcePaths: null,
            isProtectedCopyOnly: isProtectedCopyOnly);

        Assert.NotEqual(winUiUnsetSentinel, allowed);
        Assert.NotEqual(DataPackageOperation.None, allowed);
        if (preferred != DataPackageOperation.None)
        {
            Assert.NotEqual(DataPackageOperation.None, allowed & preferred);
        }
    }

    [Fact]
    public void WholeStackHeaderDrag_DoesNotLeakFakeTextPathAndChildTilesGuardSurfacePathData()
    {
        string surfaceSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        Assert.DoesNotContain(
            "e.Data.SetText(stack.Name)",
            surfaceSource,
            StringComparison.Ordinal);

        string itemVisualsSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        Assert.Contains(
            "if (!payload.HasSurfacePathData)",
            itemVisualsSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SingleOwnerHoverArbitration_SuppressesNativeShellVisualsDuringReorderAndLowersTipPriority()
    {
        string surfaceSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.Contains(
            "if (_isSurfaceReorderDragActive || _stackPopoverDragActive)",
            surfaceSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DispatcherQueuePriority.Low",
            surfaceSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileDragSourceGuardDataObject_UsesZeroAllocationEnumFormatEtcPassthroughWhenNoPreferredDropEffect()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileDragSourceGuardDataObject.cs"));

        Assert.Contains(
            "if (direction != DataDirGet || !_hasPreferredDropEffect || s_preferredDropEffectFormat == 0)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_inner, direction, enumerator);",
            source,
            StringComparison.Ordinal);
    }
}
