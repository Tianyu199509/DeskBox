using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class MusicProgressRefreshPolicyTests
{
    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 2000)]
    public void ResolveProgressRefreshIntervalMs_UsesLowerCadenceForCapsule(
        bool isCompactCollapsed,
        int expectedIntervalMs)
    {
        Assert.Equal(
            expectedIntervalMs,
            MusicWidgetViewModel.ResolveProgressRefreshIntervalMs(isCompactCollapsed));
    }

    [Theory]
    [InlineData(MusicPlaybackState.Playing, true, true)]
    [InlineData(MusicPlaybackState.Unknown, true, true)]
    [InlineData(MusicPlaybackState.Paused, true, false)]
    [InlineData(MusicPlaybackState.Stopped, true, false)]
    [InlineData(MusicPlaybackState.Playing, false, false)]
    [InlineData(MusicPlaybackState.Unknown, false, false)]
    public void ShouldRunProgressTimer_RequiresCurrentMediaInfo(
        MusicPlaybackState playbackState,
        bool hasCurrentMediaInfo,
        bool expected)
    {
        Assert.Equal(
            expected,
            MusicWidgetViewModel.ShouldRunProgressTimer(
                playbackState,
                hasCurrentMediaInfo));
    }
}
