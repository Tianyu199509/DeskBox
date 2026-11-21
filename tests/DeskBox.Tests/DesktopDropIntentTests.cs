using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopDropIntentTests
{
    [Theory]
    [InlineData(true, false, false, 0u, nameof(FileDropIntent.Move))]
    [InlineData(false, false, true, 0u, nameof(FileDropIntent.Copy))]
    [InlineData(false, true, true, 0u, nameof(FileDropIntent.Move))]
    [InlineData(false, true, false, 0u, nameof(FileDropIntent.Copy))]
    [InlineData(true, false, true, 8u, nameof(FileDropIntent.Copy))]
    [InlineData(false, false, false, 4u, nameof(FileDropIntent.Move))]
    public void PublicAndMixedDesktopSelections_ObeySettingsAndModifiers(
        bool defaultMove, bool followWindows, bool sameVolume, uint keys, string expected)
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string personal = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.False(string.IsNullOrWhiteSpace(common));
        Assert.False(string.IsNullOrWhiteSpace(personal));
        foreach (string[] paths in new[]
        {
            new[] { Path.Combine(common, "Shared.lnk") },
            new[] { Path.Combine(common, "Shared.lnk"), Path.Combine(personal, "Personal.lnk") }
        })
        {
            var origin = PathOriginClassifier.ClassifySources(paths,
                driveTypeProbe: _ => DriveType.Fixed,
                busProbe: _ => (StorageBusKind.Nvme, false),
                cloudPlaceholderProbe: _ => false);
            Assert.False(origin.IsProtectedCopyByDefault);
            Assert.False(origin.IsMoveProtectedSystemOrSharedSource);
            Assert.False(DriveMediaInterop.IsProtectedDragOutSource(paths));
            var intent = FileDropIntentPolicy.ResolveMappedTransfer(true, false,
                (keys & 8) != 0, (keys & 4) != 0, defaultMove,
                followWindows: followWindows, sameVolume: sameVolume, sourceOrigin: origin);
            Assert.Equal(expected, intent.ToString());
            uint feedback = NativeDropEffectPolicy.ResolveFeedbackEffect(true, false, keys, 7,
                defaultMove: defaultMove, followWindows: followWindows, sameVolume: sameVolume, sourceOrigin: origin);
            Assert.Equal(intent == FileDropIntent.Move ? 2u : 1u, feedback);
        }
    }

    [Fact]
    public void StartMenuAndWindowsRemainProtected()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.Windows })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "Protected.lnk");
            var origin = PathOriginClassifier.ClassifySources([path],
                driveTypeProbe: _ => DriveType.Fixed, busProbe: _ => (StorageBusKind.Nvme, false));
            Assert.True(origin.IsMoveProtectedSystemOrSharedSource);
            Assert.True(origin.IsProtectedCopyByDefault);
        }
    }
}
